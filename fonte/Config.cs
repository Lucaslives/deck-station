// ===========================================================================
//  botoes.txt: leitura, gravacao e icones de cada tecla.
// ===========================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace StreamDeckCyd {

class DeckButton {
  // o que esta escrito no botoes.txt
  public string Label = "";
  public string Action = "";
  public string ColorRaw = "";
  public string IconRaw = "";
  public string AnimRaw = "";

  // calculado a partir disso
  public string ColorHex = "2A2D36";
  public bool ColorAuto;
  public string Glyph;      // simbolo da fonte de icones do Windows
  public string GlyphName = "";
  public Image Picture;     // icone do programa, arte do jogo ou imagem do usuario
  public bool PictureFill;  // preenche a tecla toda
  public string Flags = ""; // m microfone, s mudo, v volume

  public DeckButton Copy() {
    return new DeckButton { Label = Label, Action = Action, ColorRaw = ColorRaw, IconRaw = IconRaw, AnimRaw = AnimRaw };
  }
}

class DeckPage {
  public string Name = "";
  public DeckButton[] Buttons = new DeckButton[12];
}

class DeckConfig {
  public List<DeckPage> Pages = new List<DeckPage>();
  public bool Sound = true;
  public bool Labels = false;
  public bool IdleShine = true;
  public string Skin = "padrao";    // padrao, matrix, pixel
  public int DimMinutes = 5;
  public string Port = "auto";
  public int ObsPort = 0;           // 0 = a do proprio OBS
  public string ObsPassword = "";   // vazio = a do proprio OBS
  public List<string> Warnings = new List<string>();

  public static readonly Dictionary<string, string> Colors = new Dictionary<string, string> {
    {"azul", "2F6BE0"}, {"roxo", "6A4BE8"}, {"anil", "3D3FD8"}, {"lilas", "9B5DE5"},
    {"verde", "23A55A"}, {"turquesa", "12B5A0"}, {"ciano", "1CA7C4"}, {"vermelho", "E0383E"},
    {"laranja", "F07A22"}, {"amarelo", "E8B320"}, {"rosa", "E04C93"}, {"cinza", "4A5160"},
    {"grafite", "2A2D36"}, {"preto", "1E1F25"}, {"marrom", "8A5A3B"}, {"branco", "C9CDD6"}
  };

  public static DeckConfig Load(string path) {
    var cfg = new DeckConfig();
    KeyArt.Skin = "padrao";
    string text = ReadText(path);
    DeckPage page = null;
    int slot = 0, lineNo = 0;
    foreach (string raw in text.Split('\n')) {
      lineNo++;
      string line = raw.Trim();
      if (line.Length == 0 || line.StartsWith("#")) continue;
      if (line.StartsWith("[") && line.EndsWith("]")) {
        page = new DeckPage { Name = line.Substring(1, line.Length - 2).Trim() };
        cfg.Pages.Add(page);
        slot = 0;
        continue;
      }
      if (page == null) {
        // configuracoes antes da primeira pagina:  nome = valor
        int eq = line.IndexOf('=');
        if (eq < 0) { cfg.Warnings.Add("Linha " + lineNo + ": esperava [Nome da pagina]"); continue; }
        string k = Norm(line.Substring(0, eq)), v = line.Substring(eq + 1).Trim();
        if (k == "som") cfg.Sound = Norm(v) != "nao";
        else if (k == "rotulos") cfg.Labels = Norm(v) != "nao";
        else if (k == "brilho") cfg.IdleShine = Norm(v) != "nao";
        else if (k == "skin") { cfg.Skin = Array.IndexOf(KeyArt.SkinNames, Norm(v)) >= 0 ? Norm(v) : "padrao"; KeyArt.Skin = cfg.Skin; }
        else if (k == "apagar_tela") { int m; if (int.TryParse(v, out m)) cfg.DimMinutes = m; }
        else if (k == "porta") cfg.Port = v;
        else if (k == "obs_porta") { int pt; int.TryParse(v, out pt); cfg.ObsPort = pt; }
        else if (k == "obs_senha") cfg.ObsPassword = v;
        continue;
      }
      if (slot >= 12) {
        // pagina cheia: continua numa pagina nova com o mesmo nome
        page = new DeckPage { Name = page.Name };
        cfg.Pages.Add(page);
        slot = 0;
      }
      if (line == "-") { slot++; continue; }  // espaco vazio
      string[] f = line.Split('|');
      var b = new DeckButton();
      b.Label = f[0].Trim();
      b.Action = f.Length > 1 ? f[1].Trim() : "";
      b.ColorRaw = f.Length > 2 ? f[2].Trim() : "";
      b.IconRaw = f.Length > 3 ? f[3].Trim() : "";
      b.AnimRaw = f.Length > 4 ? f[4].Trim() : "";
      cfg.Prepare(b);
      page.Buttons[slot++] = b;
    }
    if (cfg.Pages.Count == 0) cfg.Warnings.Add("Nenhuma pagina em botoes.txt");
    return cfg;
  }

  // calcula cor, icone e desenho de uma tecla
  public void Prepare(DeckButton b) {
    b.Glyph = null;
    b.GlyphName = "";
    b.Picture = null;
    b.PictureFill = false;
    b.Flags = "";
    bool auto;
    b.ColorHex = ResolveColor(b.ColorRaw, b.Action, out auto);
    b.ColorAuto = auto;
    try {
      Icons.Resolve(b, b.IconRaw);
    } catch (Exception ex) {
      Warnings.Add("Icone de '" + b.Label + "': " + ex.Message);
      b.GlyphName = "app";
      b.Glyph = Icons.GlyphFor("app");
    }
    // confere o desenho agora para avisar no botoes.txt (o painel desenha no tamanho da tela)
    try {
      using (KeyArt.Render(b, Labels, null, 1)) { }
    } catch (Exception ex) {
      Warnings.Add("Desenho de '" + b.Label + "': " + ex.Message);
    }
  }

  public static string ResolveColor(string c, string action, out bool auto) {
    auto = false;
    string n = Norm(c);
    if (Colors.ContainsKey(n)) return Colors[n];
    if (Regex.IsMatch(c, "^#?[0-9a-fA-F]{6}$")) return c.TrimStart('#').ToUpper();
    // cor padrao pelo tipo de acao
    auto = true;
    string a = Norm(action);
    if (a.StartsWith("midia") || a.StartsWith("volume")) return Colors["azul"];
    if (a.StartsWith("microfone")) return Colors["roxo"];
    if (a.StartsWith("pagina")) return Colors["laranja"];
    return Colors["grafite"];
  }

  // grava no botoes.txt (o programa percebe e atualiza a placa)
  public void Save(string path) {
    var sb = new StringBuilder();
    sb.Append(DefaultConfig.Header);
    sb.Append("skin = " + Skin + "\n");
    sb.Append("som = " + (Sound ? "sim" : "nao") + "\n");
    sb.Append("rotulos = " + (Labels ? "sim" : "nao") + "\n");
    sb.Append("brilho = " + (IdleShine ? "sim" : "nao") + "\n");
    sb.Append("apagar_tela = " + DimMinutes + "\n");
    sb.Append("porta = " + Port + "\n");
    sb.Append("obs_porta = " + (ObsPort > 0 ? ObsPort.ToString() : "") + "\n");
    sb.Append("obs_senha = " + ObsPassword + "\n");
    foreach (var p in Pages) {
      sb.Append("\n[" + p.Name + "]\n");
      int last = -1;
      for (int i = 0; i < 12; i++) if (p.Buttons[i] != null) last = i;
      for (int i = 0; i <= last; i++) {
        var b = p.Buttons[i];
        if (b == null) { sb.Append("-\n"); continue; }
        var f = new List<string> { Clean(b.Label), Clean(b.Action), Clean(b.ColorRaw), Clean(b.IconRaw), Clean(b.AnimRaw) };
        while (f.Count > 2 && f[f.Count - 1] == "") f.RemoveAt(f.Count - 1);
        sb.Append(string.Join(" | ", f.ToArray()) + "\n");
      }
    }
    File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
  }

  static string Clean(string s) { return (s ?? "").Replace("|", "/").Replace("\n", " ").Trim(); }

  // le UTF-8; se o arquivo foi salvo em ANSI, usa a codificacao do Windows
  static string ReadText(string path) {
    byte[] data = File.ReadAllBytes(path);
    try {
      return new UTF8Encoding(false, true).GetString(data).TrimStart('﻿');
    } catch (DecoderFallbackException) {
      return Encoding.Default.GetString(data);
    }
  }

  // minusculas e sem acento
  public static string Norm(string s) {
    return Ascii(s ?? "").ToLowerInvariant().Trim();
  }
  public static string Ascii(string s) {
    var sb = new StringBuilder();
    foreach (char ch in s.Normalize(NormalizationForm.FormD)) {
      if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
      sb.Append(ch < 32 || ch > 126 || ch == '|' ? '?' : ch);
    }
    return sb.ToString();
  }
}

// ------------------------------- icones ------------------------------------
static class Icons {
  // nome -> simbolo da fonte "Segoe MDL2 Assets" (vem no Windows 10 e 11)
  public static readonly Dictionary<string, string> Glyphs = new Dictionary<string, string> {
    {"play", "E768"}, {"pausa", "E769"}, {"anterior", "E892"}, {"proxima", "E893"}, {"parar", "E71A"},
    {"volmais", "E992"}, {"volmenos", "E992"}, {"volume", "E767"}, {"mudo", "E74F"}, {"mic", "E720"},
    {"fone", "E7F6"}, {"web", "E774"}, {"pasta", "E838"}, {"musica", "EC4F"}, {"chat", "E8BD"},
    {"app", "E71D"}, {"copiar", "E8C8"}, {"colar", "E77F"}, {"print", "E722"}, {"cadeado", "E72E"},
    {"energia", "E7E8"}, {"estrela", "E735"}, {"casa", "E80F"}, {"jogo", "E7FC"}, {"calc", "E8EF"},
    {"nota", "E70B"}, {"desfazer", "E7A7"}, {"monitor", "E7F4"}, {"grafico", "E9D2"}, {"seta", "E72A"},
    {"voltar", "E72B"}, {"teclado", "E765"}, {"config", "E713"}, {"email", "E715"}, {"telefone", "E717"},
    {"video", "E714"}, {"brilho", "E706"}, {"download", "E896"}, {"atualizar", "E72C"}, {"busca", "E721"},
    {"link", "E71B"}, {"recortar", "E8C6"}, {"salvar", "E74E"}, {"nuvem", "E753"}, {"wifi", "E701"},
    {"bluetooth", "E702"}, {"loja", "E719"}, {"coracao", "EB51"}, {"bandeira", "E7C1"}, {"relogio", "E823"},
    {"calendario", "E787"}, {"emoji", "E76E"}, {"ideia", "EA80"}, {"codigo", "E943"}, {"cronometro", "E916"},
    {"usuario", "E77B"}, {"lixeira", "E74D"}, {"olho", "E7B3"}, {"imprimir", "E749"}, {"raio", "E945"},
    {"mapa", "E707"}, {"carro", "E804"}, {"aviao", "E709"}, {"sino", "EA8F"}, {"escudo", "EA18"},
    {"onda", "E9D9"}, {"tela", "E7C4"}, {"gravar", "E7C8"}, {"live", "E93E"}, {"antena", "EC05"}, {"cena", "E8B2"}, {"mesa", "E9E9"},
    {"facecam", "E7EE"}, {"biblioteca", "E8F1"}, {"replay", "E81C"}, {"imagem", "EB9F"}, {"sincronizar", "E895"}
  };

  public static string GlyphFor(string name) {
    string code;
    if (!Glyphs.TryGetValue(name, out code)) return null;
    return ((char)Convert.ToInt32(code, 16)).ToString();
  }

  public static void Resolve(DeckButton b, string icon) {
    string n = DeckConfig.Norm(icon);
    if (n.Length > 0 && n != "auto") {
      if (Glyphs.ContainsKey(n)) { b.GlyphName = n; b.Glyph = GlyphFor(n); }
      else if (n.StartsWith("simbolo:") || n.StartsWith("u+")) {
        // qualquer simbolo da fonte pelo codigo, ex.: simbolo:E7FC
        string hex = n.Substring(n.IndexOf(n.StartsWith("u+") ? '+' : ':') + 1).Trim();
        b.Glyph = ((char)Convert.ToInt32(hex, 16)).ToString();
      } else {
        // caminho de imagem, .ico, .exe ou atalho
        string p = Environment.ExpandEnvironmentVariables(icon.Trim('"'));
        if (!File.Exists(p)) throw new Exception("arquivo nao encontrado: " + p);
        string ext = Path.GetExtension(p).ToLowerInvariant();
        if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif") {
          using (var img = Image.FromFile(p)) b.Picture = new Bitmap(img);
          b.PictureFill = true;
        } else {
          b.Picture = AppIcon(p);
          if (b.Picture == null) throw new Exception("sem icone em " + p);
        }
      }
    } else {
      string auto = AutoName(b.Action);
      string game = FirstAction(b.Action, "jogo:");
      if (game != null) {
        // jogo da Steam: arte da biblioteca
        b.Picture = SteamLib.Art(game.Trim());
        b.PictureFill = b.Picture != null;
        if (b.Picture == null) { b.GlyphName = "jogo"; b.Glyph = GlyphFor("jogo"); }
      } else if (auto != null) {
        b.GlyphName = auto;
        b.Glyph = GlyphFor(auto);
      } else {
        // abrir:programa/atalho -> icone do proprio programa; pasta -> simbolo de pasta
        string target = FirstAction(b.Action, "abrir:");
        if (target != null) {
          string args;
          string file = Actions.ResolveTarget(target, out args);
          if (file != null && Directory.Exists(file)) { b.GlyphName = "pasta"; b.Glyph = GlyphFor("pasta"); }
          else if (file != null) b.Picture = AppIcon(file);
        }
        if (b.Picture == null && b.Glyph == null) { b.GlyphName = "app"; b.Glyph = GlyphFor("app"); }
      }
    }
    // estados que a placa mostra sozinha
    string a = DeckConfig.Norm(b.Action);
    if (a.StartsWith("microfone")) b.Flags += "m";
    if (a == "midia:mudo") b.Flags += "s";
    if (a.StartsWith("midia:volume") || a.StartsWith("volume:") || a == "midia:mudo") b.Flags += "v";
  }

  static string FirstAction(string action, string prefix) {
    string a = action.Split(new[] { " ; " }, StringSplitOptions.None)[0].Trim();
    return a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? a.Substring(prefix.Length).Trim() : null;
  }

  public static string AutoName(string action) {
    string a = DeckConfig.Norm(action.Split(new[] { " ; " }, StringSplitOptions.None)[0]);
    switch (a) {
      case "midia:play": return "play";
      case "midia:proxima": return "proxima";
      case "midia:anterior": return "anterior";
      case "midia:parar": return "parar";
      case "midia:volume+": return "volmais";
      case "midia:volume-": return "volmenos";
      case "midia:mudo": return "mudo";
      case "atalho:ctrl+c": return "copiar";
      case "atalho:ctrl+v": return "colar";
      case "atalho:ctrl+x": return "recortar";
      case "atalho:ctrl+z": return "desfazer";
      case "atalho:ctrl+s": return "salvar";
      case "atalho:win+shift+s": case "atalho:print": return "print";
      case "atalho:win+l": return "cadeado";
      case "atalho:win+d": return "monitor";
      case "atalho:ctrl+shift+esc": return "grafico";
      case "pagina:-": return "voltar";
    }
    if (a.StartsWith("volume:-")) return "volmenos";
    if (a.StartsWith("volume:")) return "volmais";
    if (a.StartsWith("microfone")) return "mic";
    if (a.StartsWith("pagina:")) return "seta";
    if (a.StartsWith("atalho:") || a.StartsWith("digitar:")) return "teclado";
    if (a.StartsWith("comando:")) return "codigo";
    if (a.StartsWith("discord:ensurdecer")) return "fone";
    if (a == "discord:meumic") return "mic";
    if (a == "discord:meusom") return "fone";
    if (a.StartsWith("discord:")) return "usuario";
    if (a.StartsWith("mixer")) return "mesa";
    if (a.StartsWith("esportes")) return "grafico";
    if (a.StartsWith("obs:")) {
      string o = a.Substring(4);
      if (o.StartsWith("cena")) return "cena";
      if (o.StartsWith("mic")) return "mic";
      if (o.StartsWith("desktop")) return "volume";
      if (o.StartsWith("mudo")) return "mudo";
      if (o.StartsWith("facecam")) return "facecam";
      if (o.StartsWith("mostrar")) return "olho";
      if (o.StartsWith("live")) return "live";
      if (o.StartsWith("gravar")) return "gravar";
      if (o.StartsWith("pausar")) return "pausa";
      if (o.StartsWith("replay")) return "replay";
      if (o.StartsWith("camvirtual")) return "video";
      if (o.StartsWith("estudio")) return "tela";
      if (o.StartsWith("mesa")) return "mesa";
    }
    if (a.StartsWith("abrir:steam://")) {
      if (a.Contains("store")) return "loja";
      if (a.Contains("friends")) return "usuario";
      if (a.Contains("downloads")) return "download";
      if (a.Contains("bigpicture")) return "monitor";
      if (a.Contains("screenshots")) return "imagem";
      if (a.Contains("settings")) return "config";
      if (a.Contains("games")) return "biblioteca";
    }
    if (a.StartsWith("abrir:http://") || a.StartsWith("abrir:https://") || a.StartsWith("abrir:www.")) return "web";
    return null;
  }

  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  struct SHFILEINFO {
    public IntPtr hIcon; public int iIcon; public uint dwAttributes;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
  }
  [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
  static extern IntPtr SHGetFileInfo(string path, uint attr, ref SHFILEINFO info, uint size, uint flags);
  [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, int[] ids, uint n, uint flags);

  // icone do programa em alta resolucao (128 px), sem a setinha de atalho
  public static Image AppIcon(string path) {
    string file = path;
    int idx = 0;
    if (Path.GetExtension(path).ToLowerInvariant() == ".lnk") LinkIconLocation(path, out file, out idx);
    if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
    var h = new IntPtr[1];
    var ids = new int[1];
    try {
      if (PrivateExtractIcons(file, idx, 128, 128, h, ids, 1, 0) > 0 && h[0] != IntPtr.Zero) return IconToBitmap(h[0]);
    } catch { }
    var info = new SHFILEINFO();
    SHGetFileInfo(file, 0, ref info, (uint)Marshal.SizeOf(info), 0x100);
    return info.hIcon != IntPtr.Zero ? IconToBitmap(info.hIcon) : null;
  }

  static Image IconToBitmap(IntPtr h) {
    try {
      using (var ico = Icon.FromHandle(h)) return ico.ToBitmap();
    } finally {
      DestroyIcon(h);
    }
  }

  static void LinkIconLocation(string lnk, out string file, out int idx) {
    file = null;
    idx = 0;
    try {
      Type t = Type.GetTypeFromProgID("WScript.Shell");
      object shell = Activator.CreateInstance(t);
      object sc = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
      Type st = sc.GetType();
      string loc = (string)st.InvokeMember("IconLocation", System.Reflection.BindingFlags.GetProperty, null, sc, null);
      string target = (string)st.InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, sc, null);
      Marshal.ReleaseComObject(sc);
      Marshal.ReleaseComObject(shell);
      file = target;
      if (!string.IsNullOrEmpty(loc)) {
        int comma = loc.LastIndexOf(',');
        string f = Environment.ExpandEnvironmentVariables((comma >= 0 ? loc.Substring(0, comma) : loc).Trim());
        if (f.Length > 0 && File.Exists(f)) {
          file = f;
          if (comma >= 0) int.TryParse(loc.Substring(comma + 1), out idx);
        }
      }
    } catch { }
  }
}

}
