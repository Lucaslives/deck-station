// ===========================================================================
//  Mais skins das teclas:
//   cyberpunk - placa chanfrada escura, neon rosa e azul, linhas de varredura
//   digivice  - carcaca cinza de V-Pet com tela LCD verde de pontinhos
//   vidro     - vidro fosco sobre uma luz colorida (glassmorphism)
// ===========================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace StreamDeckCyd {

static partial class KeyArt {
  static readonly Color Pink = Color.FromArgb(255, 43, 214), Cyan = Color.FromArgb(0, 229, 255);
  static readonly Dictionary<Image, Bitmap> duoCache = new Dictionary<Image, Bitmap>();

  static void Label(Graphics g, DeckButton b, RectangleF area, string font, float px, Color color, bool upper) {
    if (b.Label.Length == 0) return;
    using (var f = new Font(font, px, font == "Consolas" ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
    using (var br = new SolidBrush(color))
      g.DrawString(upper ? b.Label.ToUpperInvariant() : b.Label, f, br, area, sf);
  }

  // ---------------------------- CYBERPUNK -----------------------------------
  static Bitmap Duotone(Image src) {
    Bitmap m;
    if (duoCache.TryGetValue(src, out m)) return m;
    m = new Bitmap(src);
    for (int y = 0; y < m.Height; y++)
      for (int x = 0; x < m.Width; x++) {
        Color c = m.GetPixel(x, y);
        float l = (0.3f * c.R + 0.59f * c.G + 0.11f * c.B) / 255f;
        Color o = l < 0.5f ? Mix(Color.FromArgb(22, 6, 44), Pink, l / 0.5f) : Mix(Pink, Cyan, (l - 0.5f) / 0.5f);
        m.SetPixel(x, y, Color.FromArgb(c.A, o));
      }
    duoCache[src] = m;
    return m;
  }

  static GraphicsPath Chamfer(RectangleF r, float k) {
    var p = new GraphicsPath();
    p.AddPolygon(new[] {
      new PointF(r.Left, r.Top), new PointF(r.Right - k, r.Top), new PointF(r.Right, r.Top + k),
      new PointF(r.Right, r.Bottom), new PointF(r.Left + k, r.Bottom), new PointF(r.Left, r.Bottom - k) });
    return p;
  }

  static Bitmap RenderCyber(DeckButton b, bool labels, AnimFrame f, int scale) {
    var bmp = new Bitmap(W * scale, H * scale, PixelFormat.Format32bppArgb);
    using (var g = HiQ(bmp, scale)) {
      g.Clear(BgFor("cyberpunk"));
      var r = new RectangleF(2, 2, W - 4.5f, H - 4.5f);
      using (var shape = Chamfer(r, 12)) {
        using (var br = new LinearGradientBrush(r, Color.FromArgb(30, 12, 54), Color.FromArgb(12, 5, 26), 90f)) g.FillPath(br, shape);
        var st = g.Save();
        g.SetClip(shape, CombineMode.Intersect);
        // linhas de varredura
        using (var pen = new Pen(Color.FromArgb(18, Pink), 1f))
          for (float y = r.Top + 1; y < r.Bottom; y += 3) g.DrawLine(pen, r.Left, y, r.Right, y);
        // a cor da tecla vira uma faixa na lateral
        using (var br = new SolidBrush(Hex(b.ColorHex))) g.FillRectangle(br, r.Left, r.Top + 16, 3, r.Height - 32);

        var area = labels ? new RectangleF(8, 6, W - 16, H - 24) : new RectangleF(8, 6, W - 16, H - 14);
        float cx = area.X + area.Width / 2, cy = area.Y + area.Height / 2;
        if (b.Picture != null && b.PictureFill) {
          var pic = Duotone(b.Picture);
          float z = 1 + Math.Abs(1 - f.Scale) * 1.2f;
          float k = Math.Max(r.Width / pic.Width, r.Height / pic.Height) * z;
          g.DrawImage(pic, W / 2f - pic.Width * k / 2, H / 2f - pic.Height * k / 2, pic.Width * k, pic.Height * k);
          using (var pen = new Pen(Color.FromArgb(40, 0, 0, 0), 1f))
            for (float y = r.Top + 1; y < r.Bottom; y += 2) g.DrawLine(pen, r.Left, y, r.Right, y);
        } else if (b.Picture != null) {
          float s = area.Height * 0.78f * f.Scale;
          g.DrawImage(Duotone(b.Picture), cx - s / 2, cy - s / 2, s, s);
        } else {
          using (var p = ContentPath(b, area.Height * 0.72f)) {
            Transform(p, cx, cy, f);
            using (var pen = new Pen(Color.FromArgb(55, Pink), 5f) { LineJoin = LineJoin.Round }) g.DrawPath(pen, p);
            using (var pen = new Pen(Color.FromArgb(90, Cyan), 2.4f) { LineJoin = LineJoin.Round }) g.DrawPath(pen, p);
            var bb = p.GetBounds();
            using (var br = new LinearGradientBrush(new RectangleF(bb.X - 1, bb.Y - 1, bb.Width + 2, bb.Height + 2), Pink, Cyan, 45f)) g.FillPath(br, p);
          }
        }
        if (f.Ring >= 0 && f.RingAlpha > 0)
          using (var pen = new Pen(Color.FromArgb((int)f.RingAlpha, Cyan), 2f))
            g.DrawEllipse(pen, cx - f.Ring, cy - f.Ring, f.Ring * 2, f.Ring * 2);
        if (f.Flash > 0)
          using (var br = new SolidBrush(Color.FromArgb((int)(150 * f.Flash), Pink))) g.FillRectangle(br, r);
        g.Restore(st);
        if (labels) Label(g, b, new RectangleF(8, H - 18, W - 16, 13), "Consolas", 9.5f, Cyan, true);
        // contorno neon rosa -> azul com brilho
        using (var glow = new Pen(Color.FromArgb(50, Pink), 4.5f)) g.DrawPath(glow, shape);
        using (var gb = new LinearGradientBrush(r, Pink, Cyan, 30f))
        using (var pen = new Pen(gb, 1.6f)) g.DrawPath(pen, shape);
        // marquinhas nos cantos
        using (var pen = new Pen(Cyan, 1.5f)) {
          g.DrawLine(pen, r.Right - 9, r.Top + 3, r.Right - 3, r.Top + 9);
          g.DrawLine(pen, r.Left + 3, r.Bottom - 9, r.Left + 9, r.Bottom - 3);
        }
      }
    }
    return bmp;
  }

  // ---------------------------- DIGIVICE (LCD de V-Pet) ---------------------
  static readonly int[,] Bayer = { { 0, 8, 2, 10 }, { 12, 4, 14, 6 }, { 3, 11, 1, 9 }, { 15, 7, 13, 5 } };

  static Bitmap RenderDigivice(DeckButton b, bool labels, AnimFrame f, int scale) {
    var bmp = new Bitmap(W * scale, H * scale, PixelFormat.Format32bppArgb);
    Color lcd = Color.FromArgb(172, 190, 150), on = Color.FromArgb(34, 42, 34), ghost = Color.FromArgb(158, 176, 138);
    using (var g = HiQ(bmp, scale)) {
      g.Clear(BgFor("digivice"));
      // carcaca de plastico (levemente na cor da tecla)
      var shell = new RectangleF(1, 1, W - 3, H - 3);
      Color tint = Hex(b.ColorHex);
      using (var p = Round(shell, 10)) {
        using (var br = new LinearGradientBrush(shell, Mix(Color.FromArgb(222, 222, 226), tint, 0.22f), Mix(Color.FromArgb(160, 160, 168), tint, 0.22f), 90f)) g.FillPath(br, p);
        using (var pen = new Pen(Color.FromArgb(52, 52, 58), 1.2f)) g.DrawPath(pen, p);
      }
      var screen = new RectangleF(6, 6, W - 12, H - 12);
      using (var p = Round(screen, 3)) {
        using (var br = new SolidBrush(lcd)) g.FillPath(br, p);
        using (var pen = new Pen(Color.FromArgb(87, 88, 95), 1.5f)) g.DrawPath(pen, p);
      }

      // tela de pontinhos: grade de 21 x 18
      const float pitch = 3f;
      int cols = (int)((screen.Width - 2) / pitch), rows = (int)((screen.Height - 2) / pitch);
      int lblRows = labels ? 5 : 0;
      float ox = screen.X + (screen.Width - cols * pitch) / 2 + 0.3f, oy = screen.Y + (screen.Height - rows * pitch) / 2 + 0.3f;
      int crow = rows - lblRows;
      var dots = new bool[cols, rows];
      using (var mask = new Bitmap(cols, crow, PixelFormat.Format32bppArgb)) {
        using (var mg = Graphics.FromImage(mask)) {
          mg.SmoothingMode = SmoothingMode.AntiAlias;
          mg.InterpolationMode = InterpolationMode.HighQualityBicubic;
          mg.Clear(Color.Transparent);
          float cx = cols / 2f, cy = crow / 2f;
          if (b.Picture != null) {
            float z = b.PictureFill ? 1 + Math.Abs(1 - f.Scale) * 1.2f : f.Scale;
            float k = b.PictureFill ? Math.Max((float)cols / b.Picture.Width, (float)crow / b.Picture.Height) * z
                                    : crow * 0.9f * z / Math.Max(b.Picture.Width, b.Picture.Height);
            mg.DrawImage(b.Picture, cx - b.Picture.Width * k / 2, cy - b.Picture.Height * k / 2, b.Picture.Width * k, b.Picture.Height * k);
          } else {
            using (var p = ContentPath(b, crow * 0.86f)) {
              Transform(p, cx, cy, f);
              mg.FillPath(Brushes.Black, p);
              using (var pen = new Pen(Color.Black, 0.55f) { LineJoin = LineJoin.Round }) mg.DrawPath(pen, p);  // traco mais grosso
            }
          }
        }
        // arte de jogo: estica o contraste para os pontos ficarem nitidos
        float lo = 1, hi = 0;
        if (b.PictureFill)
          for (int y = 0; y < crow; y++)
            for (int x = 0; x < cols; x++) {
              Color q = mask.GetPixel(x, y);
              float ql = (0.3f * q.R + 0.59f * q.G + 0.11f * q.B) / 255f;
              lo = Math.Min(lo, ql); hi = Math.Max(hi, ql);
            }
        for (int y = 0; y < crow; y++)
          for (int x = 0; x < cols; x++) {
            Color c = mask.GetPixel(x, y);
            if (b.Picture == null) { dots[x, y] = c.A > 90; continue; }
            if (c.A < 110) continue;
            // imagem: contorno aceso e o miolo pontilhado conforme o escuro
            bool edge = !b.PictureFill && (x == 0 || y == 0 || x == cols - 1 || y == crow - 1 ||
                        mask.GetPixel(x - 1, y).A < 110 || mask.GetPixel(x + 1, y).A < 110 ||
                        mask.GetPixel(x, y - 1).A < 110 || mask.GetPixel(x, y + 1).A < 110);
            float l = (0.3f * c.R + 0.59f * c.G + 0.11f * c.B) / 255f;
            if (b.PictureFill && hi > lo) l = (float)Math.Pow((l - lo) / (hi - lo), 0.7);
            dots[x, y] = edge || (1 - l) * 16 > Bayer[y & 3, x & 3] + 1;
          }
      }
      // onda: um anel de pontos; flash: inverte a tela como um LCD
      if (f.Ring >= 0 && f.RingAlpha > 60) {
        float rr = f.Ring / pitch;
        for (int a = 0; a < 360; a += 5) {
          int x = (int)Math.Round(cols / 2f + rr * Math.Cos(a * Math.PI / 180)), y = (int)Math.Round(crow / 2f + rr * Math.Sin(a * Math.PI / 180));
          if (x >= 0 && y >= 0 && x < cols && y < crow) dots[x, y] = true;
        }
      }
      bool invert = f.Flash > 0.1f;
      using (var bOn = new SolidBrush(on))
      using (var bGhost = new SolidBrush(ghost))
        for (int y = 0; y < rows; y++)
          for (int x = 0; x < cols; x++) {
            bool lit = y < crow && dots[x, y];
            if (invert && y < crow) lit = !lit;
            g.FillRectangle(lit ? bOn : bGhost, ox + x * pitch, oy + y * pitch, pitch - 0.6f, pitch - 0.6f);
          }
      if (labels) {
        g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
        Label(g, b, new RectangleF(screen.X, oy + crow * pitch, screen.Width, lblRows * pitch), "Lucida Console", 8f, on, true);
      }
    }
    return bmp;
  }

  // ---------------------------- VIDRO ---------------------------------------
  static Bitmap RenderGlass(DeckButton b, bool labels, AnimFrame f, int scale) {
    var bmp = new Bitmap(W * scale, H * scale, PixelFormat.Format32bppArgb);
    using (var g = HiQ(bmp, scale)) {
      g.Clear(BgFor("vidro"));
      var r = new RectangleF(1, 1, W - 3, H - 3);
      Color c = b.ColorAuto && b.Picture != null && !b.PictureFill ? AutoTint(b.Picture) : Hex(b.ColorHex);
      using (var shape = Round(r, 13)) {
        var st = g.Save();
        g.SetClip(shape, CombineMode.Intersect);
        using (var br = new SolidBrush(Color.FromArgb(20, 32, 52))) g.FillRectangle(br, r);
        if (b.Picture != null && b.PictureFill) {
          float z = 1 + Math.Abs(1 - f.Scale) * 1.2f;
          float k = Math.Max(r.Width / b.Picture.Width, r.Height / b.Picture.Height) * z;
          g.DrawImage(b.Picture, W / 2f - b.Picture.Width * k / 2, H / 2f - b.Picture.Height * k / 2, b.Picture.Width * k, b.Picture.Height * k);
        } else {
          // luzes coloridas atras do vidro
          using (var blob = new GraphicsPath()) {
            blob.AddEllipse(-W * 0.35f, -H * 0.5f, W * 1.2f, H * 1.25f);
            using (var pb = new PathGradientBrush(blob) { CenterColor = Color.FromArgb(235, c), SurroundColors = new[] { Color.FromArgb(0, c) } })
              g.FillPath(pb, blob);
          }
          using (var blob = new GraphicsPath()) {
            blob.AddEllipse(W * 0.35f, H * 0.3f, W * 0.95f, H * 1.0f);
            Color c2 = Mix(c, Color.White, 0.35f);
            using (var pb = new PathGradientBrush(blob) { CenterColor = Color.FromArgb(150, c2), SurroundColors = new[] { Color.FromArgb(0, c2) } })
              g.FillPath(pb, blob);
          }
        }
        // vidro fosco
        using (var br = new SolidBrush(Color.FromArgb(b.PictureFill ? 18 : 34, 255, 255, 255))) g.FillRectangle(br, r);

        var area = labels ? new RectangleF(4, 3, W - 8, H - 20) : new RectangleF(4, 4, W - 8, H - 8);
        float cx = area.X + area.Width / 2, cy = area.Y + area.Height / 2;
        if (b.Picture != null && !b.PictureFill) {
          float s = area.Height * 0.66f * f.Scale;
          using (var ia = new ImageAttributes()) {
            var cm = new ColorMatrix { Matrix00 = 0, Matrix11 = 0, Matrix22 = 0, Matrix33 = 0.35f };
            ia.SetColorMatrix(cm);
            g.DrawImage(b.Picture, new Rectangle((int)(cx - s / 2 + 1), (int)(cy - s / 2 + 2), (int)s, (int)s), 0, 0, b.Picture.Width, b.Picture.Height, GraphicsUnit.Pixel, ia);
          }
          g.DrawImage(b.Picture, cx - s / 2, cy - s / 2, s, s);
        } else if (b.Picture == null) {
          using (var p = ContentPath(b, area.Height * 0.56f)) {
            Transform(p, cx, cy, f);
            // sombra suave
            for (int k = 3; k >= 1; k--)
              using (var sh = (GraphicsPath)p.Clone()) {
                using (var m = new Matrix()) { m.Translate(0, k * 0.8f); sh.Transform(m); }
                using (var pen = new Pen(Color.FromArgb(22, 0, 0, 30), k * 1.6f)) g.DrawPath(pen, sh);
              }
            g.FillPath(Brushes.White, p);
            using (var pen = new Pen(Color.White, 0.8f) { LineJoin = LineJoin.Round }) g.DrawPath(pen, p);
          }
        }
        // reflexo diagonal
        using (var gl = new GraphicsPath()) {
          gl.AddPolygon(new[] { new PointF(r.Left, r.Top), new PointF(r.Left + r.Width * 0.62f, r.Top), new PointF(r.Left, r.Top + r.Height * 0.7f) });
          using (var br = new LinearGradientBrush(new RectangleF(r.Left, r.Top, r.Width * 0.62f, r.Height * 0.7f), Color.FromArgb(55, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 45f))
            g.FillPath(br, gl);
        }
        if (f.Ring >= 0 && f.RingAlpha > 0)
          using (var pen = new Pen(Color.FromArgb((int)f.RingAlpha, 255, 255, 255), 2.5f))
            g.DrawEllipse(pen, cx - f.Ring, cy - f.Ring, f.Ring * 2, f.Ring * 2);
        if (f.Flash > 0)
          using (var br = new SolidBrush(Color.FromArgb((int)(220 * f.Flash), 255, 255, 255))) g.FillRectangle(br, r);
        g.Restore(st);
        if (labels) {
          var la = new RectangleF(3, H - 18, W - 6, 14);
          var sh = la;
          sh.Offset(0, 1);
          Label(g, b, sh, "Segoe UI Semibold", 10.5f, Color.FromArgb(120, 0, 0, 20), false);
          Label(g, b, la, "Segoe UI Semibold", 10.5f, Color.White, false);
        }
        // borda de vidro: clara em cima/esquerda, some embaixo
        using (var gb = new LinearGradientBrush(r, Color.FromArgb(190, 255, 255, 255), Color.FromArgb(25, 255, 255, 255), 60f))
        using (var pen = new Pen(gb, 1.3f)) g.DrawPath(pen, shape);
      }
    }
    return bmp;
  }
}

}
