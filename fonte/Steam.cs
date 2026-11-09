// ===========================================================================
//  Jogos instalados na Steam: lista (pelos appmanifest_*.acf de cada
//  biblioteca) e arte de cada jogo (cache da biblioteca da Steam).
// ===========================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace StreamDeckCyd {

class SteamGame {
  public string AppId;
  public string Name;
  public override string ToString() { return Name; }
}

static class SteamLib {
  public static string SteamPath() {
    try {
      using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")) {
        var p = k == null ? null : k.GetValue("SteamPath") as string;
        if (p != null) return Path.GetFullPath(p.Replace('/', '\\'));
      }
    } catch { }
    string def = @"C:\Program Files (x86)\Steam";
    return Directory.Exists(def) ? def : null;
  }

  static List<string> Libraries(string steam) {
    var libs = new List<string> { steam };
    try {
      string vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
      foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"")) {
        string p = m.Groups[1].Value.Replace("\\\\", "\\");
        if (!libs.Exists(x => string.Equals(x.TrimEnd('\\'), p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) libs.Add(p);
      }
    } catch { }
    return libs;
  }

  public static List<SteamGame> Games() {
    var list = new List<SteamGame>();
    string steam = SteamPath();
    if (steam == null) return list;
    foreach (string lib in Libraries(steam)) {
      string apps = Path.Combine(lib, "steamapps");
      if (!Directory.Exists(apps)) continue;
      foreach (string f in Directory.GetFiles(apps, "appmanifest_*.acf")) {
        try {
          string t = File.ReadAllText(f);
          string id = Regex.Match(t, "\"appid\"\\s+\"(\\d+)\"").Groups[1].Value;
          string name = Regex.Match(t, "\"name\"\\s+\"([^\"]+)\"").Groups[1].Value;
          if (id == "" || name == "") continue;
          // ferramentas da Steam nao sao jogos
          if (Regex.IsMatch(name, "Redistributable|Steamworks|Proton|Steam Linux Runtime|SteamVR", RegexOptions.IgnoreCase)) continue;
          if (!list.Exists(g => g.AppId == id)) list.Add(new SteamGame { AppId = id, Name = name });
        } catch { }
      }
    }
    list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    return list;
  }

  public static string GameName(string appId) {
    foreach (var g in Games()) if (g.AppId == appId) return g.Name;
    return "Jogo " + appId;
  }

  // procura um arquivo de arte no cache (layout novo: pastas com hash; antigo: <appid>_nome)
  static string FindArt(string appId, string name) {
    string steam = SteamPath();
    if (steam == null) return null;
    string cache = Path.Combine(steam, @"appcache\librarycache");
    try {
      string dir = Path.Combine(cache, appId);
      if (Directory.Exists(dir)) {
        var found = Directory.GetFiles(dir, name, SearchOption.AllDirectories);
        if (found.Length > 0) return found[0];
      }
      string old = Path.Combine(cache, appId + "_" + name);
      if (File.Exists(old)) return old;
    } catch { }
    return null;
  }

  static Image Load(string path) {
    if (path == null) return null;
    try {
      using (var img = Image.FromFile(path)) return new Bitmap(img);
    } catch { return null; }
  }

  // Arte da tecla: imagem de fundo do jogo + logo por cima.
  // Sem logo, usa a capa.
  public static Image Art(string appId) {
    Image hero = Load(FindArt(appId, "library_hero.jpg"));
    Image logo = Load(FindArt(appId, "logo.png"));
    if (hero != null && logo != null) {
      const int w = 304, h = 272;  // 4x a tecla
      var bmp = new Bitmap(w, h);
      using (var g = Graphics.FromImage(bmp)) {
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float k = Math.Max((float)w / hero.Width, (float)h / hero.Height);
        float hw = hero.Width * k, hh = hero.Height * k;
        g.DrawImage(hero, (w - hw) / 2, (h - hh) / 2, hw, hh);
        // escurece um pouco embaixo do logo para ele aparecer
        using (var br = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(40, 0, 0, 0), Color.FromArgb(150, 0, 0, 0), 90f))
          g.FillRectangle(br, 0, 0, w, h);
        float lk = Math.Min(w * 0.86f / logo.Width, h * 0.62f / logo.Height);
        float lw = logo.Width * lk, lh = logo.Height * lk;
        g.DrawImage(logo, (w - lw) / 2, (h - lh) / 2, lw, lh);
      }
      hero.Dispose();
      logo.Dispose();
      return bmp;
    }
    if (hero != null) hero.Dispose();
    if (logo != null) logo.Dispose();
    return Load(FindArt(appId, "library_600x900.jpg")) ?? Load(FindArt(appId, "library_capsule.jpg")) ??
           Load(FindArt(appId, "library_header.jpg")) ?? Load(FindArt(appId, "header.jpg"));
  }
}

}
