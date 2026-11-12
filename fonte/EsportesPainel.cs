// ===========================================================================
//  Tela Esportes no painel: uma aba por esporte (Brasileirao, NBA, CS, LoL,
//  Valorant) e, em cada uma, Classificacao, Jogos (ao vivo, proximos,
//  resultados com placar) e Estatisticas; nos e-sports so os Jogos, com o
//  campeonato de cada partida. Arrastar o dedo rola a lista.
// ===========================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace StreamDeckCyd {

partial class PanelForm {
  volatile bool sports;
  volatile int sportIdx, sportGen;
  int sportView, sportScroll, sportContentH;
  Rectangle sportBack, sportArea;
  readonly List<Rectangle> sportTabs = new List<Rectangle>(), viewRects = new List<Rectangle>();
  bool sportDrag, sportsHooked;
  int sportDragY, sportDragScroll;
  readonly AutoResetEvent sportWake = new AutoResetEvent(false);
  static readonly string[] ViewNames = { "Classificação", "Jogos", "Estatísticas" };

  // which: id ou nome do esporte ("nba", "Brasileirão"...), vazio = o ultimo aberto
  public void OpenSports(string which = "", int view = 0) {
    string w = DeckConfig.Norm(which);
    for (int k = 0; k < Sports.All.Length; k++)
      if (w != "" && (Sports.All[k].Id == w || DeckConfig.Norm(Sports.All[k].Name) == w)) { sportIdx = k; sportView = view; sportScroll = 0; }
    if (sports) { sportWake.Set(); Invalidate(); return; }
    CloseMixer();
    sports = true;
    StopAnim();
    pressed = -1;
    if (!sportsHooked) {
      sportsHooked = true;
      Sports.Changed += () => {
        if (IsHandleCreated) try { BeginInvoke((Action)(() => { if (sports) Invalidate(); })); } catch { }
      };
    }
    // busca os dados da aba aberta: na hora, depois a cada 5 min (40 s com jogo ao vivo)
    int my = ++sportGen;
    new Thread(() => {
      while (sports && sportGen == my) {
        var def = Sports.All[sportIdx];
        try { if ((def.Path != null || def.Wiki != null) && Sports.Stale(def.Id)) Sports.Refresh(def.Id); }
        catch (Exception ex) { app.Log("esportes: " + ex.Message); }
        sportWake.WaitOne(2000);
      }
    }) { IsBackground = true, Name = "esportes" }.Start();
    UpdateRain();
    Invalidate();
  }

  public void CloseSports() {
    if (!sports) return;
    sports = false;
    sportDrag = false;
    sportWake.Set();
    UpdateRain();
    Invalidate();
  }

  // ---- toque ----
  void SportsDown(Point p) {
    if (sportBack.Contains(p)) { CloseSports(); return; }
    for (int k = 0; k < sportTabs.Count; k++)
      if (sportTabs[k].Contains(p)) {
        if (k != sportIdx) { sportIdx = k; sportView = 0; sportScroll = 0; sportWake.Set(); }
        Invalidate();
        return;
      }
    for (int k = 0; k < viewRects.Count; k++)
      if (viewRects[k].Contains(p)) { if (Sports.All[sportIdx].Wiki == null) sportView = k; sportScroll = 0; Invalidate(); return; }
    if (sportArea.Contains(p)) {
      sportDrag = true;
      sportDragY = p.Y;
      sportDragScroll = sportScroll;
    }
  }

  void SportsMove(MouseEventArgs e) {
    if (!sports || !sportDrag) return;
    int max = Math.Max(0, sportContentH - sportArea.Height);
    int s = Math.Max(0, Math.Min(max, sportDragScroll - (e.Y - sportDragY)));
    if (s != sportScroll) { sportScroll = s; Invalidate(); }
  }

  bool SportsUp() {
    if (!sports) return false;
    sportDrag = false;
    return true;
  }

  // ---- desenho ----
  void DrawSports(Graphics g) {
    int w = ClientSize.Width, h = ClientSize.Height, pad = barH / 4;
    Color bg = KeyArt.Bg, dim = Color.FromArgb(150, 155, 168), line = Mix(bg, Color.White, 0.16f);
    Color accent = KeyArt.Accent;
    int headH = (int)(barH * 0.8f), top = barH + pad;
    var def = Sports.All[sportIdx];
    SportData d;
    Sports.Data.TryGetValue(def.Id, out d);

    using (var font = new Font("Segoe UI Semibold", barH * 0.26f, GraphicsUnit.Pixel))
    using (var small = new Font("Segoe UI", barH * 0.2f, GraphicsUnit.Pixel))
    using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
    using (var dimBr = new SolidBrush(dim)) {
      // abas dos esportes
      sportBack = new Rectangle(pad, top, (int)(barH * 2.4f), headH);
      Button(g, sportBack, "◀  Teclas", font, line, Mix(bg, Color.White, 0.04f), Color.White, center);
      sportTabs.Clear();
      int x0 = sportBack.Right + pad;
      float tw = (w - x0 - pad / 2f) / Sports.All.Length;
      for (int k = 0; k < Sports.All.Length; k++) {
        var r = Rectangle.Round(new RectangleF(x0 + k * tw, top, tw - pad, headH));
        if (k == sportIdx) Button(g, r, Sports.All[k].Name, font, accent, Mix(bg, accent, 0.35f), Color.White, center);
        else Button(g, r, Sports.All[k].Name, font, line, Mix(bg, Color.White, 0.04f), Color.FromArgb(200, 204, 214), center);
        sportTabs.Add(r);
      }

      // Classificacao / Jogos / Estatisticas e a hora da ultima atualizacao
      int vy = top + headH + pad / 2, vh = (int)(barH * 0.6f);
      viewRects.Clear();
      if (def.Path != null || def.Wiki != null) {
        int vx = pad;
        var names = def.Wiki != null ? new[] { "Jogos" } : ViewNames;
        int cur = def.Wiki != null ? 0 : sportView;
        for (int v = 0; v < names.Length; v++) {
          var r = new Rectangle(vx, vy, (int)(barH * 2.5f), vh);
          if (v == cur) Button(g, r, names[v], small, Color.White, Mix(bg, Color.White, 0.2f), Color.White, center);
          else Button(g, r, names[v], small, line, bg, dim, center);
          viewRects.Add(r);
          vx = r.Right + pad / 2;
        }
        string upd = d == null ? "Carregando..." : d.Error != "" ? d.Error : "Atualizado " + d.Updated.ToString("HH:mm") + (d.HasLive ? "  ·  ao vivo" : "");
        using (var br = new SolidBrush(d != null && d.Error != "" ? Color.FromArgb(240, 110, 110) : dim))
        using (var right = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
          g.DrawString(upd, small, br, new RectangleF(vx + pad, vy, w - vx - pad * 2, vh), right);
      }

      sportArea = new Rectangle(pad, vy + vh + pad, w - pad * 2, h - (vy + vh + pad) - pad / 2);
      if (def.Path == null && def.Wiki == null) {
        sportContentH = 0;
        g.DrawString(def.Name + "\n\nAinda sem fonte de dados.\nA HLTV bloqueia programas que leem o site.\nAssim que a fonte for escolhida, os jogos e placares aparecem aqui.",
                     font, dimBr, sportArea, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
        return;
      }
      if (d == null) {
        sportContentH = 0;
        g.DrawString("Carregando " + def.Name + "...", font, dimBr, sportArea, center);
        return;
      }

      var oldClip = g.Clip;
      g.SetClip(sportArea, System.Drawing.Drawing2D.CombineMode.Intersect);   // Intersect: respeita o pedaco sendo redesenhado
      var a = new Rectangle(sportArea.X, sportArea.Y - sportScroll, sportArea.Width, sportArea.Height);
      if (def.Wiki != null) sportContentH = DrawGames(g, d, a, pad, dimBr, line);
      else if (sportView == 0) sportContentH = DrawStandings(g, d, a, pad, dimBr, accent);
      else if (sportView == 1) sportContentH = DrawGames(g, d, a, pad, dimBr, line);
      else sportContentH = DrawLeaders(g, d, a, pad, dimBr, accent);
      g.Clip = oldClip;

      // barrinha de rolagem
      if (sportContentH > sportArea.Height) {
        float frac = sportArea.Height / (float)sportContentH;
        float th = Math.Max(30, sportArea.Height * frac);
        float ty = sportArea.Y + (sportArea.Height - th) * sportScroll / (float)Math.Max(1, sportContentH - sportArea.Height);
        using (var br = new SolidBrush(Color.FromArgb(110, 255, 255, 255)))
        using (var p = Round(new RectangleF(w - pad / 2f - 3, ty, 5, th), 2.5f)) g.FillPath(br, p);
      }
    }
  }

  static readonly StringFormat LeftMid = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
  static readonly StringFormat RightMid = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };
  static readonly StringFormat CenterMid = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap };

  void DrawLogo(Graphics g, string url, RectangleF r) {
    var img = Sports.Logo(url);
    if (img != null) g.DrawImage(img, r);
  }

  // tabela: 1 tabela = metade em cada coluna (1-10 | 11-20); 2 tabelas = Leste | Oeste
  int DrawStandings(Graphics g, SportData d, Rectangle a, int pad, Brush dimBr, Color accent) {
    if (d.Tables.Count == 0) { g.DrawString("Sem classificação", Font, dimBr, a, CenterMid); return 0; }
    var cols = new List<KeyValuePair<string, List<StandRow>>>();
    if (d.Tables.Count == 1) {
      var rows = d.Tables[0].Rows;
      int half = (rows.Count + 1) / 2;
      cols.Add(new KeyValuePair<string, List<StandRow>>("", rows.GetRange(0, half)));
      cols.Add(new KeyValuePair<string, List<StandRow>>("", rows.GetRange(half, rows.Count - half)));
    } else foreach (var t in d.Tables) cols.Add(new KeyValuePair<string, List<StandRow>>(t.Title, t.Rows));
    string[] heads = d.Tables[0].Headers;
    int maxRows = 1;
    bool titles = false;
    foreach (var c in cols) { maxRows = Math.Max(maxRows, c.Value.Count); titles |= c.Key != ""; }
    int titleH = titles ? (int)(barH * 0.42f) : 0, headerH = (int)(barH * 0.36f);
    int rowH = Math.Max((int)(barH * 0.3f), (a.Height - titleH - headerH) / maxRows);
    int colW = (a.Width - pad * (cols.Count - 1)) / cols.Count;
    float cellW = barH * 0.62f;
    Color bg = KeyArt.Bg;
    using (var rowFont = new Font("Segoe UI", Math.Min(barH * 0.25f, rowH * 0.42f), GraphicsUnit.Pixel))
    using (var boldFont = new Font("Segoe UI Semibold", Math.Min(barH * 0.26f, rowH * 0.44f), GraphicsUnit.Pixel))
    using (var headFont = new Font("Segoe UI", barH * 0.18f, GraphicsUnit.Pixel))
    using (var titleFont = new Font("Segoe UI Semibold", barH * 0.24f, GraphicsUnit.Pixel))
    using (var odd = new SolidBrush(Mix(bg, Color.White, 0.045f))) {
      for (int c = 0; c < cols.Count; c++) {
        int x = a.X + c * (colW + pad), y = a.Y;
        if (titleH > 0) { g.DrawString(cols[c].Key, titleFont, Brushes.White, new RectangleF(x, y, colW, titleH), LeftMid); y += titleH; }
        float rankW = barH * 0.5f, logoW = rowH * 0.95f;
        float nameX = x + 8 + rankW + logoW, nameW = colW - (nameX - x) - cellW * heads.Length;
        g.DrawString("#", headFont, dimBr, new RectangleF(x + 8, y, rankW, headerH), CenterMid);
        g.DrawString("Time", headFont, dimBr, new RectangleF(nameX, y, nameW, headerH), LeftMid);
        for (int k = 0; k < heads.Length; k++)
          g.DrawString(heads[k], headFont, dimBr, new RectangleF(nameX + nameW + k * cellW, y, cellW, headerH), CenterMid);
        y += headerH;
        var rows = cols[c].Value;
        for (int i = 0; i < rows.Count; i++, y += rowH) {
          var row = rows[i];
          if (i % 2 == 0) g.FillRectangle(odd, x, y, colW, rowH);
          if (row.Zone != Color.Empty) using (var zb = new SolidBrush(row.Zone)) g.FillRectangle(zb, x, y + 2, 5, rowH - 4);
          g.DrawString(row.Rank.ToString(), boldFont, Brushes.White, new RectangleF(x + 8, y, rankW, rowH), CenterMid);
          float ls = rowH * 0.72f;
          DrawLogo(g, row.Team.Logo, new RectangleF(x + 8 + rankW + (logoW - ls) / 2, y + (rowH - ls) / 2, ls, ls));
          g.DrawString(row.Team.Name, rowFont, Brushes.White, new RectangleF(nameX, y, nameW, rowH), LeftMid);
          for (int k = 0; k < row.Cells.Length && k < heads.Length; k++) {
            using (var br = new SolidBrush(k == 0 ? Color.White : Color.FromArgb(200, 204, 214)))
              g.DrawString(row.Cells[k], k == 0 ? boldFont : rowFont, br, new RectangleF(nameX + nameW + k * cellW, y, cellW, rowH), CenterMid);
          }
        }
      }
    }
    return titleH + headerH + maxRows * rowH;
  }

  // jogos: ao vivo, depois proximos, depois resultados (mais recente primeiro)
  int DrawGames(Graphics g, SportData d, Rectangle a, int pad, Brush dimBr, Color line) {
    var live = d.Games.FindAll(x => x.State == "in");
    var next = d.Games.FindAll(x => x.State == "pre");
    var done = d.Games.FindAll(x => x.State == "post");
    live.Sort((p, q) => p.When.CompareTo(q.When));
    next.Sort((p, q) => p.When.CompareTo(q.When));
    done.Sort((p, q) => q.When.CompareTo(p.When));
    var sections = new[] { new KeyValuePair<string, List<SportGame>>("Ao vivo", live),
                           new KeyValuePair<string, List<SportGame>>("Próximos jogos", next),
                           new KeyValuePair<string, List<SportGame>>("Resultados", done) };
    if (d.Games.Count == 0) { using (var f = new Font("Segoe UI", barH * 0.24f, GraphicsUnit.Pixel)) g.DrawString("Nenhum jogo nesses dias", f, dimBr, a, CenterMid); return 0; }
    int colsN = 3, cardH = (int)(barH * 1.3f), secH = (int)(barH * 0.5f);
    int cardW = (a.Width - pad * (colsN - 1)) / colsN;
    int y = a.Y;
    Color bg = KeyArt.Bg, liveRed = Color.FromArgb(240, 70, 70);
    using (var secFont = new Font("Segoe UI Semibold", barH * 0.24f, GraphicsUnit.Pixel))
    using (var whenFont = new Font("Segoe UI", barH * 0.18f, GraphicsUnit.Pixel))
    using (var teamFont = new Font("Segoe UI", barH * 0.22f, GraphicsUnit.Pixel))
    using (var scoreFont = new Font("Segoe UI Semibold", barH * 0.27f, GraphicsUnit.Pixel)) {
      foreach (var sec in sections) {
        if (sec.Value.Count == 0) continue;
        using (var br = new SolidBrush(sec.Key == "Ao vivo" ? liveRed : Color.White))
          g.DrawString(sec.Key, secFont, br, new RectangleF(a.X, y, a.Width, secH), LeftMid);
        y += secH;
        for (int i = 0; i < sec.Value.Count; i++) {
          var gm = sec.Value[i];
          int cx = a.X + (i % colsN) * (cardW + pad), cy = y + (i / colsN) * (cardH + pad / 2);
          if (cy + cardH < a.Y - a.Height || cy > a.Bottom + a.Height * 2) continue;
          var r = new Rectangle(cx, cy, cardW, cardH);
          bool isLive = gm.State == "in";
          using (var p = Round(r, barH * 0.14f)) {
            using (var br = new SolidBrush(Mix(bg, isLive ? liveRed : Color.White, isLive ? 0.12f : 0.05f))) g.FillPath(br, p);
            using (var pen = new Pen(isLive ? liveRed : line)) g.DrawPath(pen, p);
          }
          int ip = barH / 7, whenH = (int)(barH * 0.32f);
          using (var br = new SolidBrush(isLive ? liveRed : Color.FromArgb(150, 155, 168)))
            g.DrawString(Sports.When(gm) + (gm.Tour != "" ? "  ·  " + gm.Tour : ""), whenFont, br, new RectangleF(cx + ip, cy + ip / 2, cardW - ip * 2, whenH), LeftMid);
          int lineH = (cardH - whenH - ip) / 2;
          int hs, asc;
          bool parsed = int.TryParse(gm.HomeScore, out hs) & int.TryParse(gm.AwayScore, out asc);
          bool scored = gm.State != "pre" && parsed;
          var teams = new[] { gm.Home, gm.Away };
          var scores = new[] { gm.HomeScore, gm.AwayScore };
          for (int k = 0; k < 2; k++) {
            float ty = cy + whenH + ip / 2f + k * lineH;
            float ls = lineH * 0.78f;
            DrawLogo(g, teams[k].Logo, new RectangleF(cx + ip, ty + (lineH - ls) / 2, ls, ls));
            bool loser = gm.State == "post" && scored && (k == 0 ? hs < asc : asc < hs);
            using (var br = new SolidBrush(loser ? Color.FromArgb(150, 155, 168) : Color.White)) {
              float sw = barH * 0.8f;
              g.DrawString(teams[k].Name, teamFont, br, new RectangleF(cx + ip * 2 + ls, ty, cardW - ip * 3 - ls - sw, lineH), LeftMid);
              if (scored) g.DrawString(scores[k], scoreFont, br, new RectangleF(cx + cardW - ip - sw, ty, sw, lineH), RightMid);
            }
          }
        }
        y += ((sec.Value.Count + colsN - 1) / colsN) * (cardH + pad / 2) + pad / 2;
      }
    }
    return y - a.Y;
  }

  // lideres: uma coluna por lista (artilheiros, assistencias...)
  int DrawLeaders(Graphics g, SportData d, Rectangle a, int pad, Brush dimBr, Color accent) {
    if (d.Leaders.Count == 0) { using (var f = new Font("Segoe UI", barH * 0.24f, GraphicsUnit.Pixel)) g.DrawString("Sem estatísticas", f, dimBr, a, CenterMid); return 0; }
    int n = d.Leaders.Count, colW = (a.Width - pad * (n - 1)) / n;
    int titleH = (int)(barH * 0.55f);
    int rowH = Math.Max((int)(barH * 0.45f), (a.Height - titleH) / 10);
    Color bg = KeyArt.Bg;
    using (var titleFont = new Font("Segoe UI Semibold", barH * 0.26f, GraphicsUnit.Pixel))
    using (var rowFont = new Font("Segoe UI", Math.Min(barH * 0.24f, rowH * 0.4f), GraphicsUnit.Pixel))
    using (var valFont = new Font("Segoe UI Semibold", Math.Min(barH * 0.28f, rowH * 0.46f), GraphicsUnit.Pixel))
    using (var odd = new SolidBrush(Mix(bg, Color.White, 0.045f)))
    using (var accBr = new SolidBrush(accent)) {
      int maxRows = 0;
      for (int c = 0; c < n; c++) {
        var ll = d.Leaders[c];
        int x = a.X + c * (colW + pad), y = a.Y;
        g.DrawString(ll.Title, titleFont, Brushes.White, new RectangleF(x, y, colW, titleH), LeftMid);
        y += titleH;
        float rankW = barH * 0.5f, teamW = barH * 0.9f, valW = barH * 0.8f;
        for (int i = 0; i < ll.Rows.Count; i++, y += rowH) {
          var r = ll.Rows[i];
          if (i % 2 == 0) g.FillRectangle(odd, x, y, colW, rowH);
          g.DrawString((i + 1).ToString(), valFont, i == 0 ? accBr : (Brush)Brushes.White, new RectangleF(x, y, rankW, rowH), CenterMid);
          g.DrawString(r[0], rowFont, Brushes.White, new RectangleF(x + rankW, y, colW - rankW - teamW - valW, rowH), LeftMid);
          g.DrawString(r[1], rowFont, dimBr, new RectangleF(x + colW - teamW - valW, y, teamW, rowH), CenterMid);
          g.DrawString(r[2], valFont, i == 0 ? accBr : (Brush)Brushes.White, new RectangleF(x + colW - valW, y, valW - 8, rowH), RightMid);
        }
        maxRows = Math.Max(maxRows, ll.Rows.Count);
      }
      return titleH + maxRows * rowH;
    }
  }
}

}
