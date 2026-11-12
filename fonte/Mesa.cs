// ===========================================================================
//  Mesa de som no painel: um canal por fonte de audio, com fader (arrastar o
//  dedo), volume, medidor de nivel ao vivo e botao de mudo. Duas versoes:
//  a do OBS (fontes da live, em dB) e o mixer do PC (programas do Windows,
//  em %, com a troca da saida de som: fone <-> tela).
// ===========================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace StreamDeckCyd {

partial class PanelForm {
  volatile bool mixer, pcMix;
  public bool MixerOpen { get { return mixer && !pcMix; } }   // mesa do OBS aberta
  readonly Timer mixTimer = new Timer { Interval = 33 };
  int mixPage;

  // retangulos do ultimo desenho (para o toque)
  class Strip { public MixerChannel Ch; public Rectangle Track, Fader, Mute, Meter; }
  readonly List<Strip> strips = new List<Strip>();
  Rectangle mixBack, mixPrev, mixNext;
  readonly List<KeyValuePair<Rectangle, string>> outRects = new List<KeyValuePair<Rectangle, string>>();

  List<MixerChannel> MixList { get { return pcMix ? Audio.Mixer.Channels : Obs.Mixer; } }
  float Level(MixerChannel ch) {
    float lv;
    if (pcMix) Audio.Mixer.Levels.TryGetValue(ch.Id, out lv);
    else Obs.Levels.TryGetValue(ch.Name, out lv);
    return lv;
  }

  // arrasto do fader: relativo ao ponto onde o dedo encostou (sem pulos de volume)
  Strip drag;
  int dragY;
  double dragPos;

  // pico que cai devagar, como numa mesa de verdade
  readonly Dictionary<string, float> held = new Dictionary<string, float>();
  static readonly StringFormat RightAlign = new StringFormat { Alignment = StringAlignment.Far };

  public void OpenMixer(bool pc = false) {
    if (mixer && pcMix == pc) return;
    if (mixer) CloseMixer();
    CloseSports();
    mixer = true;
    pcMix = pc;
    mixPage = 0;
    StopAnim();
    pressed = -1;
    if (pc) Audio.Mixer.Start();
    else {
      Obs.Meters(true);
      app.Touch("MESA");
    }
    mixSig = "";
    mixTimer.Start();
    UpdateRain();
    Invalidate();
  }

  public void CloseMixer() {
    if (!mixer) return;
    mixer = false;
    drag = null;
    mixTimer.Stop();
    if (pcMix) Audio.Mixer.Stop();
    else Obs.Meters(false);
    held.Clear();
    UpdateRain();
    Invalidate();
  }

  // a cada quadro: so os medidores que estao mexendo sao redesenhados; o resto
  // da mesa so quando algo muda (volume, mudo, app novo, saida de som)
  string mixSig = "";
  void MixerTick() {
    if (!mixer) return;
    string sig;
    try {
      var sb = new System.Text.StringBuilder();
      foreach (var ch in MixList) sb.Append(ch.Name).Append('|').Append(ch.Db.ToString("0.0")).Append(ch.Muted ? 'm' : '-').Append(';');
      if (pcMix) {
        foreach (var o in Audio.Mixer.Outputs) sb.Append(o.Name).Append(o.Default ? '*' : '-');
        sb.Append(Audio.Mixer.LastError);
      } else sb.Append(Obs.Connected).Append(Obs.LastError);
      sig = sb.ToString();
    } catch { sig = mixSig + "?"; }   // lista trocando no meio da leitura
    if (sig != mixSig) { mixSig = sig; Invalidate(); return; }
    foreach (var s in strips) {
      float hold;
      held.TryGetValue(pcMix ? s.Ch.Id : s.Ch.Name, out hold);
      if ((!s.Ch.Muted && Level(s.Ch) > 0.001f) || hold > 0.02f) Invalidate(s.Meter);
    }
  }

  // fader: posicao 0..1 com curva cubica (meio do fader = -18 dB), igual ao medidor
  static double PosToDb(double p) { return p <= 0.01 ? -100 : Math.Max(-100, 60 * Math.Log10(p)); }
  static double DbToPos(double db) { return db <= -99 ? 0 : Math.Pow(10, db / 60); }
  static double MulToPos(double mul) { return mul <= 0 ? 0 : Math.Min(1, Math.Pow(mul, 1 / 3.0)); }

  // ---- toque ----
  void MixerDown(Point p) {
    if (mixBack.Contains(p)) { CloseMixer(); return; }
    foreach (var o in outRects)
      if (o.Key.Contains(p)) { Audio.Mixer.SetOutput(o.Value); Invalidate(); return; }
    var list = MixList;
    int per = PerPage();
    if (mixPrev.Contains(p) && mixPage > 0) { mixPage--; Invalidate(); return; }
    if (mixNext.Contains(p) && (mixPage + 1) * per < list.Count) { mixPage++; Invalidate(); return; }
    foreach (var s in strips) {
      if (s.Mute.Contains(p)) {
        if (pcMix) Audio.Mixer.ToggleMute(s.Ch); else Obs.ToggleMute(s.Ch);
        Invalidate();
        return;
      }
      if (s.Fader.Contains(p)) {
        drag = s;
        dragY = p.Y;
        dragPos = DbToPos(s.Ch.Db);
        return;
      }
    }
  }

  protected override void OnMouseMove(MouseEventArgs e) {
    base.OnMouseMove(e);
    SportsMove(e);
    var s = drag;
    if (s == null || !mixer || s.Track.Height <= 0) return;
    double pos = Math.Max(0, Math.Min(1, dragPos - (e.Y - dragY) / (double)s.Track.Height));
    if (pcMix) {
      if (Math.Abs(pos - DbToPos(s.Ch.Db)) < 0.004) return;
      Audio.Mixer.SetVolume(s.Ch, pos);
      Invalidate();
      return;
    }
    double db = Math.Round(PosToDb(pos), 1);
    if (Math.Abs(db - s.Ch.Db) < 0.05) return;
    Obs.SetVolume(s.Ch, db);
    Invalidate();
  }

  bool MixerUp() {
    if (!mixer) return false;
    drag = null;
    return true;
  }

  // ---- desenho ----
  int PerPage() {
    int w = ClientSize.Width - barH / 2;
    return Math.Max(1, w / Math.Max(1, (int)(barH * 1.9f)));
  }

  void DrawMixer(Graphics g) {
    int w = ClientSize.Width, h = ClientSize.Height;
    Color bg = KeyArt.Bg, dim = Color.FromArgb(150, 155, 168), line = Mix(bg, Color.White, 0.16f);
    Color accent = KeyArt.Accent;
    int pad = barH / 4;
    int headH = (int)(barH * 0.8f);
    int top = barH + pad;
    strips.Clear();

    using (var font = new Font("Segoe UI Semibold", barH * 0.26f, GraphicsUnit.Pixel))
    using (var small = new Font("Segoe UI", barH * 0.2f, GraphicsUnit.Pixel))
    using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
    using (var dimBr = new SolidBrush(dim)) {
      // cabecalho: voltar, titulo e paginas
      mixBack = new Rectangle(pad, top, (int)(barH * 2.4f), headH);
      Button(g, mixBack, "◀  Teclas", font, line, Mix(bg, Color.White, 0.04f), Color.White, center);
      g.DrawString(pcMix ? "Mixer do PC" : "Mesa de som", font, Brushes.White, new RectangleF(0, top, w, headH), center);

      var list = MixList;
      int per = PerPage();
      int pages = Math.Max(1, (list.Count + per - 1) / per);
      if (mixPage >= pages) mixPage = pages - 1;
      mixPrev = mixNext = Rectangle.Empty;
      if (pages > 1) {
        int bw = (int)(barH * 1.3f);
        mixNext = new Rectangle(w - pad - bw, top, bw, headH);
        mixPrev = new Rectangle(mixNext.X - pad - bw, top, bw, headH);
        Button(g, mixPrev, "◀", font, line, Mix(bg, Color.White, 0.04f), mixPage > 0 ? Color.White : line, center);
        Button(g, mixNext, "▶", font, line, Mix(bg, Color.White, 0.04f), mixPage < pages - 1 ? Color.White : line, center);
        g.DrawString((mixPage + 1) + "/" + pages, small, dimBr, new RectangleF(mixPrev.X - barH, top, barH, headH), center);
      }

      int y0 = top + headH + pad, y1 = h - pad;
      outRects.Clear();
      if (pcMix) y0 = DrawOutputs(g, y0, pad, small, center, dimBr, line, accent);
      if (list.Count == 0) {
        string msg = pcMix ? (Audio.Mixer.LastError != "" ? Audio.Mixer.LastError : "Lendo o som do Windows...") :
                     Obs.Connected ? "O OBS nao tem nenhuma fonte de audio" :
                     (Obs.LastError != "" ? Obs.LastError : "Conectando no OBS...") +
                     "\n\nNo OBS: Ferramentas > Configuracoes do servidor WebSocket > Ativar";
        g.DrawString(msg, font, dimBr, new RectangleF(pad, y0, w - pad * 2, y1 - y0), center);
        return;
      }

      int first = mixPage * per, count = Math.Min(per, list.Count - first);
      float sw = Math.Min(barH * 2.6f, (w - pad * 2f) / count);
      float x0 = (w - sw * count) / 2;
      for (int k = 0; k < count; k++) {
        var ch = list[first + k];
        var r = Rectangle.Round(new RectangleF(x0 + k * sw + pad / 2f, y0, sw - pad, y1 - y0));
        DrawStrip(g, ch, r, font, small, center, dimBr, line, accent);
      }
    }
  }

  void DrawStrip(Graphics g, MixerChannel ch, Rectangle r, Font font, Font small, StringFormat center, Brush dimBr, Color line, Color accent) {
    Color bg = KeyArt.Bg;
    using (var p = Round(r, barH * 0.18f)) {
      using (var br = new SolidBrush(Mix(bg, Color.White, 0.05f))) g.FillPath(br, p);
      using (var pen = new Pen(drag != null && drag.Ch == ch ? accent : line, drag != null && drag.Ch == ch ? 2f : 1f)) g.DrawPath(pen, p);
    }
    int ip = barH / 6;
    // nome (ate 2 linhas) e volume em dB
    int nameH = (int)(barH * 0.62f);
    g.DrawString(ch.Name, small, Brushes.White, new RectangleF(r.X + ip, r.Y + ip, r.Width - ip * 2, nameH), center);
    int dbH = (int)(barH * 0.4f);
    string dbTxt = pcMix ? Math.Round(DbToPos(ch.Db) * 100) + "%" :
                   ch.Db <= -99 ? "-∞ dB" : ch.Db.ToString("0.0") + " dB";
    using (var br = new SolidBrush(ch.Muted ? Color.FromArgb(240, 90, 90) : accent))
      g.DrawString(dbTxt, font, br, new RectangleF(r.X, r.Y + ip + nameH, r.Width, dbH), center);

    // botao de mudo embaixo
    int muteH = (int)(barH * 0.75f);
    var mute = new Rectangle(r.X + ip, r.Bottom - ip - muteH, r.Width - ip * 2, muteH);
    if (ch.Muted) Button(g, mute, "MUDO", font, Color.FromArgb(255, 120, 120), Color.FromArgb(200, 40, 45), Color.White, center);
    else Button(g, mute, "Mutar", small, line, Mix(bg, Color.White, 0.04f), Color.FromArgb(200, 204, 214), center);

    // fader e medidor no meio
    int ftop = r.Y + ip + nameH + dbH + ip, fbot = mute.Y - ip * 2;
    int knobH = Math.Max(18, (int)(barH * 0.34f));
    var fader = new Rectangle(r.X, ftop, r.Width, fbot - ftop);
    var track = new Rectangle(r.X, ftop + knobH / 2, r.Width, Math.Max(1, fbot - ftop - knobH));
    float cx = r.X + r.Width * 0.42f;
    var meterBox = Rectangle.Ceiling(new RectangleF(cx + r.Width * 0.16f, track.Y, Math.Max(6, r.Width * 0.1f), track.Height));
    meterBox.Inflate(2, 2);
    strips.Add(new Strip { Ch = ch, Track = track, Fader = fader, Mute = mute, Meter = meterBox });

    float tw = Math.Max(4, barH * 0.07f);
    // marcas: dB na mesa do OBS (0, -6, -12...), % no mixer do PC
    int[] marks = pcMix ? new[] { 100, 75, 50, 25, 0 } : new[] { 0, -6, -12, -20, -30, -40, -60 };
    using (var pen = new Pen(Mix(bg, Color.White, 0.22f)))
      foreach (int m in marks) {
        float y = (float)(track.Bottom - (pcMix ? m / 100.0 : DbToPos(m)) * track.Height);
        g.DrawLine(pen, cx - r.Width * 0.18f, y, cx - tw, y);
        if (r.Width > barH * 1.5f)
          g.DrawString(m.ToString(), small, dimBr, new RectangleF(r.X, y - small.Height / 2f, cx - r.Width * 0.2f - r.X, small.Height), RightAlign);
      }
    using (var br = new SolidBrush(Color.FromArgb(18, 18, 22)))
    using (var tp = Round(new RectangleF(cx - tw / 2, track.Y, tw, track.Height), tw / 2)) g.FillPath(br, tp);

    // medidor ao lado do trilho: verde ate -20 dB, amarelo ate -9, vermelho acima
    float mx = cx + r.Width * 0.16f, mw = Math.Max(6, r.Width * 0.1f);
    var meter = new RectangleF(mx, track.Y, mw, track.Height);
    using (var br = new SolidBrush(Color.FromArgb(14, 14, 18))) g.FillRectangle(br, meter);
    float lv = ch.Muted ? 0 : Level(ch);
    float pos = (float)MulToPos(lv);
    float hold;
    string hk = pcMix ? ch.Id : ch.Name;
    held.TryGetValue(hk, out hold);
    hold = Math.Max(pos, hold - 0.012f);
    held[hk] = hold;
    if (pos > 0) {
      float yTop = meter.Bottom - pos * meter.Height;
      using (var lg = new LinearGradientBrush(new RectangleF(meter.X, meter.Y - 1, meter.Width, meter.Height + 2), Color.Red, Color.FromArgb(40, 200, 90), 90f)) {
        var blend = new ColorBlend {
          Colors = new[] { Color.FromArgb(240, 60, 60), Color.FromArgb(240, 60, 60), Color.FromArgb(240, 200, 40), Color.FromArgb(240, 200, 40), Color.FromArgb(50, 210, 100), Color.FromArgb(50, 210, 100) },
          Positions = new[] { 0f, 1 - (float)DbToPos(-9) - 0.001f, 1 - (float)DbToPos(-9), 1 - (float)DbToPos(-20) - 0.001f, 1 - (float)DbToPos(-20), 1f }
        };
        lg.InterpolationColors = blend;
        g.FillRectangle(lg, meter.X, yTop, meter.Width, meter.Bottom - yTop);
      }
    }
    if (hold > 0.02f)
      using (var pen = new Pen(Color.White, 2)) {
        float hy = meter.Bottom - hold * meter.Height;
        g.DrawLine(pen, meter.X, hy, meter.Right, hy);
      }

    // botao do fader
    float kp = (float)DbToPos(ch.Db);
    float ky = track.Bottom - kp * track.Height;
    float kw = Math.Min(r.Width * 0.42f, barH * 0.9f);
    var knob = new RectangleF(cx - kw / 2, ky - knobH / 2f, kw, knobH);
    using (var kpth = Round(knob, knobH * 0.2f)) {
      using (var br = new LinearGradientBrush(knob, Color.FromArgb(235, 238, 245), Color.FromArgb(150, 155, 168), 90f)) g.FillPath(br, kpth);
      using (var pen = new Pen(Color.FromArgb(60, 0, 0, 0))) g.DrawPath(pen, kpth);
    }
    using (var pen = new Pen(ch.Muted ? Color.FromArgb(200, 40, 45) : Color.FromArgb(40, 40, 48), 2))
      g.DrawLine(pen, knob.X + 4, ky, knob.Right - 4, ky);
  }

  // linha "Saida": um botao por saida de som ligada (fone, tela, caixa...); a acesa e a atual
  int DrawOutputs(Graphics g, int y, int pad, Font small, StringFormat center, Brush dimBr, Color line, Color accent) {
    var outs = Audio.Mixer.Outputs;
    if (outs.Count == 0) return y;
    int w = ClientSize.Width, rowH = (int)(barH * 0.95f);
    int lw = (int)(barH * 1.3f);
    g.DrawString("Saída", small, dimBr, new RectangleF(pad, y, lw, rowH), center);
    float bw = Math.Min(barH * 5f, (w - pad * 2f - lw) / outs.Count);
    Color bg = KeyArt.Bg;
    for (int k = 0; k < outs.Count; k++) {
      var o = outs[k];
      var r = Rectangle.Round(new RectangleF(pad + lw + k * bw + pad / 2f, y, bw - pad, rowH));
      string txt = o.Name + (o.Sub != "" ? "\n" + o.Sub : "");
      if (o.Default) Button(g, r, txt, small, accent, Mix(bg, accent, 0.35f), Color.White, center);
      else Button(g, r, txt, small, line, Mix(bg, Color.White, 0.04f), Color.FromArgb(200, 204, 214), center);
      outRects.Add(new KeyValuePair<Rectangle, string>(r, o.Id));
    }
    return y + rowH + pad;
  }

  void Button(Graphics g, Rectangle r, string text, Font f, Color border, Color fill, Color fore, StringFormat sf) {
    using (var p = Round(r, r.Height * 0.22f)) {
      using (var br = new SolidBrush(fill)) g.FillPath(br, p);
      using (var pen = new Pen(border)) g.DrawPath(pen, p);
    }
    using (var br = new SolidBrush(fore)) g.DrawString(text, f, br, r, sf);
  }
}

}
