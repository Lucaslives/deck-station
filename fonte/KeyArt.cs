// ===========================================================================
//  Desenho das teclas (76x68) e das animacoes de toque.
//  Sem rotulos (padrao): a arte ocupa a tecla inteira. Com rotulos: quadrado
//  colorido em cima e o nome embaixo.
// ===========================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;

namespace StreamDeckCyd {

// um quadro da animacao: transformacoes aplicadas ao desenho normal
class AnimFrame {
  public float Scale = 1, Rot = 0, Ring = -1, RingAlpha = 0, Flash = 0;
}

static partial class KeyArt {
  public const int W = 76, H = 68, Frames = 10;
  // skin atual: padrao, matrix ou pixel (vem do botoes.txt)
  public static string Skin = "padrao";
  public static readonly string[] SkinNames = { "padrao", "matrix", "pixel", "cyberpunk", "digivice", "vidro", "chuva" };
  // fundo em volta das teclas: tem que ser igual ao da placa em cada skin
  public static Color Bg { get { return BgFor(Skin); } }
  public static Color BgFor(string s) {
    switch (s) {
      case "matrix": return Color.FromArgb(0, 4, 0);
      case "pixel": return Color.FromArgb(24, 28, 40);
      case "cyberpunk": return Color.FromArgb(16, 8, 32);
      case "digivice": return Color.FromArgb(80, 88, 88);
      case "vidro": return Color.FromArgb(8, 20, 32);
      case "chuva": return Color.FromArgb(14, 6, 9);
    }
    return Color.FromArgb(8, 8, 8);
  }
  public static int SkinId(string s) { return Math.Max(0, Array.IndexOf(SkinNames, s)); }
  public static readonly string[] AnimNames = { "auto", "pular", "pulsar", "girar", "balancar", "nenhuma" };
  static FontFamily iconFont;

  static FontFamily IconFont {
    get {
      if (iconFont == null) {
        foreach (string n in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" }) {
          try { iconFont = new FontFamily(n); break; } catch { }
        }
      }
      return iconFont;
    }
  }

  public static Color Hex(string h) {
    int v = int.Parse(h, NumberStyles.HexNumber);
    return Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255);
  }
  static Color Mix(Color a, Color b, float t) {
    return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
  }

  static GraphicsPath Round(RectangleF r, float rad) {
    var p = new GraphicsPath();
    float d = rad * 2;
    p.AddArc(r.X, r.Y, d, d, 180, 90);
    p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
    p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
    p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
    p.CloseFigure();
    return p;
  }

  // simbolo centralizado em (cx, cy) com o maior lado = size
  static void DrawGlyph(Graphics g, string glyph, float cx, float cy, float size) {
    using (var path = new GraphicsPath()) {
      path.AddString(glyph, IconFont, (int)FontStyle.Regular, 100, PointF.Empty, StringFormat.GenericTypographic);
      RectangleF bb = path.GetBounds();
      if (bb.Width <= 0 || bb.Height <= 0) return;
      float k = size / Math.Max(bb.Width, bb.Height);
      using (var m = new Matrix()) {
        m.Translate(cx, cy);
        m.Scale(k, k);
        m.Translate(-(bb.X + bb.Width / 2), -(bb.Y + bb.Height / 2));
        path.Transform(m);
      }
      using (var sh = (GraphicsPath)path.Clone()) {
        using (var m = new Matrix()) { m.Translate(0, size * 0.05f); sh.Transform(m); }
        using (var br = new SolidBrush(Color.FromArgb(100, 0, 0, 0))) g.FillPath(br, sh);
      }
      g.FillPath(Brushes.White, path);
      // contorno fino deixa o traco mais grosso (na tela pequena fica mais nitido)
      using (var pen = new Pen(Color.White, size / 30f) { LineJoin = LineJoin.Round }) g.DrawPath(pen, path);
    }
  }

  // cor de fundo automatica para icones de programas: a cor media do icone
  static Color AutoTint(Image pic) {
    var cache = pic.Tag as Color?;
    if (cache.HasValue) return cache.Value;
    double r = 0, g = 0, b = 0, w = 0;
    using (var bmp = new Bitmap(pic, 24, 24)) {
      for (int y = 0; y < 24; y++)
        for (int x = 0; x < 24; x++) {
          Color c = bmp.GetPixel(x, y);
          if (c.A < 128) continue;
          float sat = c.GetSaturation() * Math.Min(1f, c.GetBrightness() * 2.5f);
          double k = 0.15 + sat;
          r += c.R * k; g += c.G * k; b += c.B * k; w += k;
        }
    }
    Color res = w > 0 ? Color.FromArgb((int)(r / w), (int)(g / w), (int)(b / w)) : Color.FromArgb(60, 64, 76);
    res = Mix(res, Color.Black, 0.3f);
    pic.Tag = res;
    return res;
  }

  public static Bitmap Render(DeckButton b, bool labels, AnimFrame f, int scale) {
    if (f == null) f = new AnimFrame();
    if (Skin == "matrix") return RenderMatrix(b, labels, f, scale);
    if (Skin == "pixel") return RenderPixel(b, labels, f, scale);
    if (Skin == "cyberpunk") return RenderCyber(b, labels, f, scale);
    if (Skin == "digivice") return RenderDigivice(b, labels, f, scale);
    if (Skin == "vidro") return RenderGlass(b, labels, f, scale);
    if (Skin == "chuva") return RenderChuva(b, labels, f, scale);
    var bmp = new Bitmap(W * scale, H * scale, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp)) {
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.InterpolationMode = InterpolationMode.HighQualityBicubic;
      g.PixelOffsetMode = PixelOffsetMode.HighQuality;
      g.CompositingQuality = CompositingQuality.HighQuality;
      g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
      g.ScaleTransform(scale, scale);
      g.Clear(Bg);

      RectangleF r;
      float rad;
      if (labels) {
        // tecla de vidro escuro com o quadrado colorido em cima
        var face = new RectangleF(0.5f, 0.5f, W - 1.5f, H - 1.5f);
        using (var p = Round(face, 10))
        using (var br = new LinearGradientBrush(face, Color.FromArgb(30, 31, 38), Color.FromArgb(16, 17, 21), 90f)) {
          g.FillPath(br, p);
          using (var pen = new Pen(Color.FromArgb(48, 50, 60), 1f)) g.DrawPath(pen, p);
        }
        float ts = 42;
        r = new RectangleF((W - ts) / 2f, 6, ts, ts);
        rad = ts * 0.24f;
        using (var sp = Round(new RectangleF(r.X, r.Y + 2, ts, ts), rad))
        using (var br = new SolidBrush(Color.FromArgb(110, 0, 0, 0))) g.FillPath(br, sp);
      } else {
        r = new RectangleF(0.5f, 0.5f, W - 1.5f, H - 1.5f);
        rad = 12;
      }

      Color c = b.ColorAuto && b.Picture != null && !b.PictureFill ? AutoTint(b.Picture) : Hex(b.ColorHex);
      float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
      using (var tp = Round(r, rad)) {
        var st = g.Save();
        g.SetClip(tp, CombineMode.Intersect);
        if (b.Picture != null && b.PictureFill) {
          // arte do jogo / imagem do usuario cobre a tecla toda
          float z = 1 + Math.Abs(1 - f.Scale) * 1.2f;
          float k = Math.Max(r.Width / b.Picture.Width, r.Height / b.Picture.Height) * z;
          float w = b.Picture.Width * k, h = b.Picture.Height * k;
          g.DrawImage(b.Picture, cx - w / 2, cy - h / 2, w, h);
        } else {
          using (var br = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), Mix(c, Color.White, 0.2f), Mix(c, Color.Black, 0.38f), 90f))
            g.FillRectangle(br, r);
          // conteudo (simbolo ou icone do programa) com a animacao
          var cs = g.Save();
          g.TranslateTransform(cx, cy);
          g.RotateTransform(f.Rot);
          g.ScaleTransform(f.Scale, f.Scale);
          float size = r.Height * (labels ? 0.5f : 0.5f);
          if (b.Picture != null) {
            float s = r.Height * (labels ? 0.72f : 0.7f);
            g.DrawImage(b.Picture, -s / 2, -s / 2, s, s);
          } else if (b.Glyph != null && IconFont != null) {
            if (b.GlyphName == "volmais" || b.GlyphName == "volmenos") {
              DrawGlyph(g, b.Glyph, -size * 0.2f, 0, size * 0.85f);
              float sx = size * 0.4f, arm = size * 0.2f;
              using (var pen = new Pen(Color.White, size * 0.13f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
                g.DrawLine(pen, sx - arm, 0, sx + arm, 0);
                if (b.GlyphName == "volmais") g.DrawLine(pen, sx, -arm, sx, arm);
              }
            } else {
              DrawGlyph(g, b.Glyph, 0, 0, size);
            }
          }
          g.Restore(cs);
        }
        // brilho de vidro em cima
        var top = new RectangleF(r.X, r.Y, r.Width, r.Height * 0.48f);
        using (var br = new LinearGradientBrush(new RectangleF(top.X, top.Y - 1, top.Width, top.Height + 2), Color.FromArgb(40, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
          g.FillRectangle(br, top);
        // onda de luz do toque
        if (f.Ring >= 0 && f.RingAlpha > 0) {
          float rr = f.Ring * r.Height / 68f;
          using (var pen = new Pen(Color.FromArgb((int)f.RingAlpha, 255, 255, 255), 3f * r.Height / 68f))
            g.DrawEllipse(pen, cx - rr, cy - rr, rr * 2, rr * 2);
        }
        if (f.Flash > 0)
          using (var br = new SolidBrush(Color.FromArgb((int)(255 * f.Flash), 255, 255, 255))) g.FillRectangle(br, r);
        g.Restore(st);
        using (var pen = new Pen(Color.FromArgb(62, 255, 255, 255), 1f)) g.DrawPath(pen, tp);
      }

      if (labels && b.Label.Length > 0) {
        var area = new RectangleF(1, r.Bottom + 2, W - 2, H - r.Bottom - 4);
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
        using (var br = new SolidBrush(Color.FromArgb(225, 226, 232))) {
          float px = 11.5f;
          Font fo = new Font("Segoe UI Semibold", px, GraphicsUnit.Pixel);
          while (px > 9f && g.MeasureString(b.Label, fo).Width > W - 4) {
            fo.Dispose();
            px -= 0.5f;
            fo = new Font("Segoe UI Semibold", px, GraphicsUnit.Pixel);
          }
          g.DrawString(b.Label, fo, br, area, sf);
          fo.Dispose();
        }
      }
    }
    return bmp;
  }

  // ---------------------------- animacoes -----------------------------------
  public static string AnimKind(DeckButton b) {
    string a = DeckConfig.Norm(b.AnimRaw);
    if (a == "pular" || a == "pulsar" || a == "girar" || a == "balancar" || a == "nenhuma") return a;
    if (b.Picture != null) return "pular";
    switch (b.GlyphName) {
      case "config": case "atualizar": case "web": case "nuvem": case "energia":
        return "girar";
      case "sino": case "cadeado": case "chat": case "telefone": case "email": case "jogo": case "seta": case "voltar":
        return "balancar";
      case "mic": case "fone": case "mudo": case "volume": case "volmais": case "volmenos": case "musica":
      case "coracao": case "gravar": case "onda": case "raio": case "estrela":
        return "pulsar";
    }
    return "pular";
  }

  public static AnimFrame[] Animation(string kind) {
    float[] pular = { 0.82f, 0.78f, 0.86f, 0.98f, 1.08f, 1.10f, 1.05f, 1.0f, 0.99f, 1.0f };
    float[] pulsar = { 0.88f, 1.12f, 1.18f, 1.06f, 0.96f, 1.08f, 1.12f, 1.04f, 0.99f, 1.0f };
    float[] leve = { 0.88f, 0.92f, 0.96f, 1.0f, 1.04f, 1.04f, 1.02f, 1.0f, 1.0f, 1.0f };
    float[] flash = { 0.32f, 0.2f, 0.11f, 0.05f, 0, 0, 0, 0, 0, 0 };
    var fr = new AnimFrame[Frames];
    for (int i = 0; i < Frames; i++) {
      float t = i / (float)(Frames - 1);
      var f = new AnimFrame { Flash = flash[i] };
      if (i < 8) {
        float k = i / 7f;
        f.Ring = 6 + 50 * k;
        f.RingAlpha = (float)(220 * Math.Pow(1 - k, 1.5));
      }
      switch (kind) {
        case "pulsar": f.Scale = pulsar[i]; break;
        case "girar": f.Scale = leve[i]; f.Rot = (float)(360 * (1 - Math.Pow(1 - t, 3))); break;
        case "balancar": f.Scale = leve[i]; f.Rot = (float)(22 * Math.Sin(t * 4 * Math.PI) * (1 - t)); break;
        default: f.Scale = pular[i]; break;
      }
      fr[i] = f;
    }
    fr[Frames - 1] = new AnimFrame();  // termina igual a tecla parada
    return fr;
  }

  // todas as paginas numa imagem so (DeckStation.exe --previa)
  public static void Preview(DeckConfig cfg, string path) {
    int pw = 4 * (W + 4) + 4, ph = 3 * (H + 4) + 4 + 22;
    using (var outb = new Bitmap(pw * Math.Max(1, cfg.Pages.Count), ph))
    using (var g = Graphics.FromImage(outb)) {
      g.Clear(Bg);
      for (int p = 0; p < cfg.Pages.Count; p++) {
        g.DrawString(cfg.Pages[p].Name, new Font("Segoe UI", 11, GraphicsUnit.Pixel), Brushes.White, p * pw + 6, 4);
        for (int i = 0; i < 12; i++) {
          var b = cfg.Pages[p].Buttons[i];
          if (b == null) continue;
          using (var k = Render(b, cfg.Labels, null, 1)) g.DrawImage(k, p * pw + 4 + (i % 4) * (W + 4), 22 + (i / 4) * (H + 4));
        }
      }
      outb.Save(path, ImageFormat.Png);
    }
  }

  // quadros de uma animacao lado a lado (DeckStation.exe --previa-anim)
  public static void AnimStrip(DeckButton b, bool labels, string path) {
    var frames = Animation(AnimKind(b));
    using (var outb = new Bitmap(Frames * (W * 2 + 4), H * 2))
    using (var g = Graphics.FromImage(outb)) {
      g.Clear(Bg);
      for (int i = 0; i < Frames; i++)
        using (var k = Render(b, labels, frames[i], 2)) g.DrawImage(k, i * (W * 2 + 4), 0);
      outb.Save(path, ImageFormat.Png);
    }
  }
}

}
