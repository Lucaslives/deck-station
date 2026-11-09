// ===========================================================================
//  Skins das teclas:
//   matrix - painel de terminal: moldura verde neon, barrinha de progresso,
//            icones e imagens em verde fosforescente
//   pixel  - pixel art: tudo desenhado numa grade 38x34 e ampliado 2x, cantos
//            em degrau, contorno escuro e cores reduzidas
//  (a skin padrao fica em KeyArt.Render)
// ===========================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace StreamDeckCyd {

static partial class KeyArt {
  static readonly Color Neon = Color.FromArgb(57, 255, 20);
  static readonly Dictionary<Image, Bitmap> monoCache = new Dictionary<Image, Bitmap>();

  // forma do conteudo (simbolo, ou alto-falante + sinal) centrada em 0,0 com o maior lado = size
  static GraphicsPath ContentPath(DeckButton b, float size) {
    var path = new GraphicsPath();
    if (b.Glyph == null || IconFont == null) return path;
    bool vol = b.GlyphName == "volmais" || b.GlyphName == "volmenos";
    path.AddString(b.Glyph, IconFont, (int)FontStyle.Regular, 100, PointF.Empty, StringFormat.GenericTypographic);
    RectangleF bb = path.GetBounds();
    if (bb.Width <= 0 || bb.Height <= 0) return path;
    float gs = vol ? size * 0.85f : size;
    float k = gs / Math.Max(bb.Width, bb.Height);
    using (var m = new Matrix()) {
      m.Translate(vol ? -size * 0.2f : 0, 0);
      m.Scale(k, k);
      m.Translate(-(bb.X + bb.Width / 2), -(bb.Y + bb.Height / 2));
      path.Transform(m);
    }
    if (vol) {
      float sx = size * 0.4f, arm = size * 0.2f, t = size * 0.13f;
      path.AddRectangle(new RectangleF(sx - arm, -t / 2, arm * 2, t));
      if (b.GlyphName == "volmais") path.AddRectangle(new RectangleF(sx - t / 2, -arm, t, arm * 2));
    }
    return path;
  }

  static void Transform(GraphicsPath p, float cx, float cy, AnimFrame f) {
    using (var m = new Matrix()) {
      m.Translate(cx, cy);
      m.Rotate(f.Rot);
      m.Scale(f.Scale, f.Scale);
      p.Transform(m);
    }
  }

  static Graphics HiQ(Bitmap bmp, int scale) {
    var g = Graphics.FromImage(bmp);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    g.ScaleTransform(scale, scale);
    return g;
  }

  // ---------------------------- MATRIX --------------------------------------
  // imagem em tons de verde fosforescente
  static Bitmap Mono(Image src) {
    Bitmap m;
    if (monoCache.TryGetValue(src, out m)) return m;
    m = new Bitmap(src);
    for (int y = 0; y < m.Height; y++)
      for (int x = 0; x < m.Width; x++) {
        Color c = m.GetPixel(x, y);
        float l = (0.3f * c.R + 0.59f * c.G + 0.11f * c.B) / 255f;
        l = (float)Math.Pow(l, 0.8);
        Color o = l < 0.7f ? Mix(Color.FromArgb(0, 18, 2), Neon, l / 0.7f) : Mix(Neon, Color.FromArgb(210, 255, 200), (l - 0.7f) / 0.3f);
        m.SetPixel(x, y, Color.FromArgb(c.A, o));
      }
    monoCache[src] = m;
    return m;
  }

  static Bitmap RenderMatrix(DeckButton b, bool labels, AnimFrame f, int scale) {
    var bmp = new Bitmap(W * scale, H * scale, PixelFormat.Format32bppArgb);
    using (var g = HiQ(bmp, scale)) {
      g.Clear(BgFor("matrix"));
      var frame = new RectangleF(1.5f, 1.5f, W - 3.5f, H - 3.5f);
      using (var fp = Round(frame, 5)) {
        using (var br = new SolidBrush(Color.FromArgb(0, 14, 3))) g.FillPath(br, fp);
        var st = g.Save();
        g.SetClip(fp, CombineMode.Intersect);

        // barrinha de progresso (enche quando a tecla e tocada)
        var bar = new RectangleF(7, 6, W - 14, 5);
        float pct = 0.3f + (Math.Abs((b.Label + b.Action).GetHashCode()) % 60) / 100f;
        pct += (1 - pct) * Math.Min(1f, f.Flash * 3.5f);
        using (var pen = new Pen(Neon, 1f)) g.DrawRectangle(pen, bar.X, bar.Y, bar.Width, bar.Height);
        using (var br = new SolidBrush(Neon)) g.FillRectangle(br, bar.X + 1.5f, bar.Y + 1.5f, (bar.Width - 3) * pct, bar.Height - 3);

        var area = labels ? new RectangleF(5, 14, W - 10, H - 30) : new RectangleF(5, 14, W - 10, H - 19);
        float cx = area.X + area.Width / 2, cy = area.Y + area.Height / 2;
        if (b.Picture != null && b.PictureFill) {
          float z = 1 + Math.Abs(1 - f.Scale) * 1.2f;
          var pic = Mono(b.Picture);
          float k = Math.Max(area.Width / pic.Width, area.Height / pic.Height) * z;
          var cs = g.Save();
          g.SetClip(area, CombineMode.Intersect);
          g.DrawImage(pic, cx - pic.Width * k / 2, cy - pic.Height * k / 2, pic.Width * k, pic.Height * k);
          g.Restore(cs);
          using (var pen = new Pen(Color.FromArgb(120, Neon), 1f)) g.DrawRectangle(pen, area.X, area.Y, area.Width, area.Height);
        } else if (b.Picture != null) {
          float s = area.Height * 0.9f * f.Scale;
          g.DrawImage(Mono(b.Picture), cx - s / 2, cy - s / 2, s, s);
        } else {
          using (var p = ContentPath(b, area.Height * 0.8f)) {
            Transform(p, cx, cy, f);
            // brilho em volta (fosforo)
            using (var pen = new Pen(Color.FromArgb(40, Neon), 4f) { LineJoin = LineJoin.Round }) g.DrawPath(pen, p);
            using (var pen = new Pen(Color.FromArgb(90, Neon), 2f) { LineJoin = LineJoin.Round }) g.DrawPath(pen, p);
            using (var br = new SolidBrush(Neon)) g.FillPath(br, p);
          }
        }
        if (f.Ring >= 0 && f.RingAlpha > 0)
          using (var pen = new Pen(Color.FromArgb((int)f.RingAlpha, Neon), 2f))
            g.DrawEllipse(pen, cx - f.Ring, cy - f.Ring, f.Ring * 2, f.Ring * 2);
        if (f.Flash > 0)
          using (var br = new SolidBrush(Color.FromArgb((int)(140 * f.Flash), Neon))) g.FillRectangle(br, frame);
        g.Restore(st);

        if (labels && b.Label.Length > 0) {
          using (var font = new Font("Consolas", 9.5f, GraphicsUnit.Pixel))
          using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
          using (var br = new SolidBrush(Neon))
            g.DrawString(b.Label, font, br, new RectangleF(3, H - 17, W - 6, 13), sf);
        }
        // moldura com brilho
        using (var pen = new Pen(Color.FromArgb(55, Neon), 4f)) g.DrawPath(pen, fp);
        using (var pen = new Pen(Neon, 1.4f)) g.DrawPath(pen, fp);
      }
    }
    return bmp;
  }

  // ---------------------------- PIXEL ART -----------------------------------
  const int LW = W / 2, LH = H / 2;  // grade 38x34

  static bool InTile(Rectangle t, int x, int y) {
    if (x < t.Left || x >= t.Right || y < t.Top || y >= t.Bottom) return false;
    int dx = Math.Min(x - t.Left, t.Right - 1 - x), dy = Math.Min(y - t.Top, t.Bottom - 1 - y);
    return !((dx < 2 && dy < 1) || (dx < 1 && dy < 2));  // canto em degrau
  }

  static Color Posterize(Color c) {
    Func<int, int> q = v => Math.Min(255, (int)Math.Round(v / 51.0) * 51);
    return Color.FromArgb(255, q(c.R), q(c.G), q(c.B));
  }

  // desenha uma imagem pequena com cores reduzidas e contorno escuro
  static void PixelPicture(Bitmap low, Image pic, int cx, int cy, int size, Color outline, Rectangle clip) {
    using (var tmp = new Bitmap(size, size, PixelFormat.Format32bppArgb)) {
      using (var g = Graphics.FromImage(tmp)) {
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(pic, 0, 0, size, size);
      }
      int x0 = cx - size / 2, y0 = cy - size / 2;
      var solid = new bool[size + 2, size + 2];
      for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++) solid[x + 1, y + 1] = tmp.GetPixel(x, y).A > 110;
      for (int y = -1; y <= size; y++)
        for (int x = -1; x <= size; x++) {
          int px = x0 + x, py = y0 + y;
          if (!clip.Contains(px, py)) continue;
          if (x >= 0 && y >= 0 && x < size && y < size && solid[x + 1, y + 1]) {
            low.SetPixel(px, py, Posterize(tmp.GetPixel(x, y)));
          } else {
            bool near = false;
            for (int dy = -1; dy <= 1 && !near; dy++)
              for (int dx = -1; dx <= 1; dx++) {
                int sx = x + 1 + dx, sy = y + 1 + dy;
                if (sx >= 0 && sy >= 0 && sx < size + 2 && sy < size + 2 && solid[sx, sy]) { near = true; break; }
              }
            if (near) low.SetPixel(px, py, outline);
          }
        }
    }
  }

  static Bitmap RenderPixel(DeckButton b, bool labels, AnimFrame f, int scale) {
    var low = new Bitmap(LW, LH, PixelFormat.Format32bppArgb);
    Color bg = BgFor("pixel");
    var t = labels ? new Rectangle(7, 1, 24, 24) : new Rectangle(1, 1, LW - 2, LH - 2);
    Color c = b.ColorAuto && b.Picture != null && !b.PictureFill ? AutoTint(b.Picture) : Hex(b.ColorHex);
    Color dark = Mix(c, Color.Black, 0.78f);
    Color light = Mix(c, Color.White, 0.35f), shade = Mix(c, Color.Black, 0.3f);

    // quadrado com chanfro: linha clara em cima, escura embaixo, contorno escuro
    for (int y = 0; y < LH; y++)
      for (int x = 0; x < LW; x++) {
        if (!InTile(t, x, y)) { low.SetPixel(x, y, bg); continue; }
        bool edge = !InTile(t, x - 1, y) || !InTile(t, x + 1, y) || !InTile(t, x, y - 1) || !InTile(t, x, y + 1);
        Color px = edge ? dark : y <= t.Top + 1 ? light : y >= t.Bottom - 3 ? shade : x <= t.Left + 1 ? Mix(c, Color.White, 0.15f) : c;
        low.SetPixel(x, y, px);
      }
    var inner = Rectangle.Inflate(t, -1, -1);
    int cx = t.Left + t.Width / 2, cy = t.Top + t.Height / 2;

    if (b.Picture != null && b.PictureFill) {
      // arte do jogo pixelada, preenchendo o quadrado
      float z = 1 + Math.Abs(1 - f.Scale) * 1.2f;
      using (var tmp = new Bitmap(inner.Width, inner.Height)) {
        using (var g = Graphics.FromImage(tmp)) {
          g.InterpolationMode = InterpolationMode.HighQualityBicubic;
          float k = Math.Max((float)inner.Width / b.Picture.Width, (float)inner.Height / b.Picture.Height) * z;
          float w = b.Picture.Width * k, h = b.Picture.Height * k;
          g.DrawImage(b.Picture, (inner.Width - w) / 2, (inner.Height - h) / 2, w, h);
        }
        for (int y = 0; y < inner.Height; y++)
          for (int x = 0; x < inner.Width; x++)
            if (InTile(inner, inner.X + x, inner.Y + y)) low.SetPixel(inner.X + x, inner.Y + y, Posterize(tmp.GetPixel(x, y)));
      }
    } else if (b.Picture != null) {
      int s = Math.Max(8, (int)(t.Height * 0.72f * f.Scale));
      PixelPicture(low, b.Picture, cx, cy, s, dark, inner);
    } else {
      // simbolo sem suavizacao, com contorno escuro e sombra embaixo
      using (var mask = new Bitmap(LW, LH, PixelFormat.Format32bppArgb)) {
        using (var g = Graphics.FromImage(mask)) {
          g.SmoothingMode = SmoothingMode.None;
          g.PixelOffsetMode = PixelOffsetMode.Half;
          using (var p = ContentPath(b, t.Height * 0.66f)) {
            Transform(p, cx, cy, f);
            g.FillPath(Brushes.White, p);
            using (var pen = new Pen(Color.White, 1f)) g.DrawPath(pen, p);
          }
        }
        Func<int, int, bool> on = (x, y) => x >= 0 && y >= 0 && x < LW && y < LH && mask.GetPixel(x, y).A > 100;
        for (int y = inner.Top; y < inner.Bottom; y++)
          for (int x = inner.Left; x < inner.Right; x++) {
            if (!InTile(inner, x, y)) continue;
            if (on(x, y)) {
              low.SetPixel(x, y, on(x, y + 1) ? Color.White : Color.FromArgb(196, 202, 222));
            } else if (on(x - 1, y) || on(x + 1, y) || on(x, y - 1) || on(x, y + 1)) {
              low.SetPixel(x, y, dark);
            }
          }
      }
    }

    // onda e flash do toque
    if (f.Ring >= 0 && f.RingAlpha > 60) {
      float rr = f.Ring / 2f;
      for (int a = 0; a < 360; a += 4) {
        int x = (int)Math.Round(cx + rr * Math.Cos(a * Math.PI / 180)), y = (int)Math.Round(cy + rr * Math.Sin(a * Math.PI / 180));
        if (InTile(inner, x, y)) low.SetPixel(x, y, Color.White);
      }
    }
    if (f.Flash > 0.04f)
      for (int y = 0; y < LH; y++)
        for (int x = 0; x < LW; x++)
          if (InTile(t, x, y)) low.SetPixel(x, y, Mix(low.GetPixel(x, y), Color.White, Math.Min(0.6f, f.Flash * 1.6f)));

    // amplia sem suavizar
    var bmp = new Bitmap(W * scale, H * scale, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp)) {
      g.InterpolationMode = InterpolationMode.NearestNeighbor;
      g.PixelOffsetMode = PixelOffsetMode.Half;
      g.DrawImage(low, 0, 0, W * scale, H * scale);
      if (labels && b.Label.Length > 0) {
        g.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
        using (var font = new Font("Lucida Console", 8f * scale, GraphicsUnit.Pixel))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.Character, FormatFlags = StringFormatFlags.NoWrap }) {
          var area = new RectangleF(0, (t.Bottom + 1) * 2 * scale, W * scale, (LH - t.Bottom - 1) * 2 * scale);
          var sh = area;
          sh.Offset(scale, scale);
          using (var br = new SolidBrush(Color.Black)) g.DrawString(b.Label.ToUpperInvariant(), font, br, sh, sf);
          g.DrawString(b.Label.ToUpperInvariant(), font, Brushes.White, area, sf);
        }
      }
    }
    low.Dispose();
    return bmp;
  }
}

}
