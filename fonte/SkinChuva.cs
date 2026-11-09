// ===========================================================================
//  Skin Chuva: teclas de vidro escuro com brilho vermelho, simbolo com
//  neon vermelho e, com "rotulos = sim", o nome embaixo (estilo widget). O painel desenha
//  a arte de fundo desfocada atras de cada tecla e a chuva animada (Cena.cs).
// ===========================================================================
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace StreamDeckCyd {

static partial class KeyArt {
  // skins com arte de fundo e chuva no painel
  public static bool HasScene { get { return Skin == "chuva"; } }
  public static string SceneFile { get { return "temas\\fundo.png"; } }
  public static readonly Color Crimson = Color.FromArgb(230, 28, 44);

  // cor de destaque do painel (abas, mesa de som, esportes)
  public static Color Accent {
    get {
      if (Skin == "matrix") return Color.FromArgb(70, 230, 90);
      if (Skin == "chuva") return Crimson;
      return Color.FromArgb(90, 150, 255);
    }
  }

  static Bitmap RenderChuva(DeckButton b, bool labels, AnimFrame f, int scale) {
    var bmp = new Bitmap(W * scale, H * scale, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp)) {
      g.SmoothingMode = SmoothingMode.AntiAlias;
      g.InterpolationMode = InterpolationMode.HighQualityBicubic;
      g.PixelOffsetMode = PixelOffsetMode.HighQuality;
      g.CompositingQuality = CompositingQuality.HighQuality;
      g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
      g.ScaleTransform(scale, scale);
      g.Clear(Color.Transparent);

      var r = new RectangleF(0.5f, 0.5f, W - 1.5f, H - 1.5f);
      const float rad = 13;
      bool hasLabel = labels && b.Label.Length > 0;
      float labelH = hasLabel ? 15 : 0;
      float cx = r.X + r.Width / 2, cy = r.Y + (r.Height - labelH) / 2 + 1;

      using (var tp = Round(r, rad)) {
        var st = g.Save();
        g.SetClip(tp, CombineMode.Intersect);
        if (b.Picture != null && b.PictureFill) {
          // arte do jogo cobrindo a tecla, puxada para o vermelho
          float z = 1 + Math.Abs(1 - f.Scale) * 1.2f;
          float k = Math.Max(r.Width / b.Picture.Width, r.Height / b.Picture.Height) * z;
          float w = b.Picture.Width * k, h = b.Picture.Height * k;
          g.DrawImage(b.Picture, cx - w / 2, r.Y + r.Height / 2 - h / 2, w, h);
          using (var br = new SolidBrush(Color.FromArgb(70, 120, 0, 10))) g.FillRectangle(br, r);
        } else {
          // vidro: escuro em cima, vinho embaixo (deixa ver a arte desfocada atras)
          using (var br = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), Color.FromArgb(70, 12, 4, 8), Color.FromArgb(125, 60, 2, 12), 90f))
            g.FillRectangle(br, r);
          // brilho vermelho atras do simbolo
          using (var gp = new GraphicsPath()) {
            float gr = r.Height * 0.42f;
            gp.AddEllipse(cx - gr, cy - gr, gr * 2, gr * 2);
            using (var pb = new PathGradientBrush(gp) { CenterColor = Color.FromArgb(95, 255, 30, 50), SurroundColors = new[] { Color.FromArgb(0, 255, 30, 50) } })
              g.FillPath(pb, gp);
          }
          var cs = g.Save();
          g.TranslateTransform(cx, cy);
          g.RotateTransform(f.Rot);
          g.ScaleTransform(f.Scale, f.Scale);
          float size = (r.Height - labelH) * 0.56f;
          if (b.Picture != null) {
            float s = (r.Height - labelH) * 0.66f;
            g.DrawImage(b.Picture, -s / 2, -s / 2, s, s);
          } else if (b.Glyph != null && IconFont != null) {
            if (b.GlyphName == "volmais" || b.GlyphName == "volmenos") {
              NeonGlyph(g, b.Glyph, -size * 0.2f, 0, size * 0.85f);
              float sx = size * 0.4f, arm = size * 0.2f;
              using (var glow = new Pen(Color.FromArgb(90, 255, 40, 60), size * 0.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
              using (var pen = new Pen(Color.FromArgb(255, 235, 238), size * 0.13f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
                g.DrawLine(glow, sx - arm, 0, sx + arm, 0);
                if (b.GlyphName == "volmais") g.DrawLine(glow, sx, -arm, sx, arm);
                g.DrawLine(pen, sx - arm, 0, sx + arm, 0);
                if (b.GlyphName == "volmais") g.DrawLine(pen, sx, -arm, sx, arm);
              }
            } else NeonGlyph(g, b.Glyph, 0, 0, size);
          }
          g.Restore(cs);
        }
        // reflexo de vidro e faixa do nome
        using (var br = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height * 0.4f + 2), Color.FromArgb(34, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
          g.FillRectangle(br, r.X, r.Y, r.Width, r.Height * 0.4f);
        if (hasLabel) {
          var lr = new RectangleF(r.X, r.Bottom - labelH - 4, r.Width, labelH + 4);
          using (var br = new LinearGradientBrush(new RectangleF(lr.X, lr.Y - 1, lr.Width, lr.Height + 2), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(170, 0, 0, 0), 90f))
            g.FillRectangle(br, lr);
        }
        // onda de luz e flash do toque
        if (f.Ring >= 0 && f.RingAlpha > 0) {
          float rr = f.Ring * r.Height / 68f;
          using (var pen = new Pen(Color.FromArgb((int)f.RingAlpha, 255, 60, 70), 3f))
            g.DrawEllipse(pen, cx - rr, cy - rr, rr * 2, rr * 2);
        }
        if (f.Flash > 0)
          using (var br = new SolidBrush(Color.FromArgb((int)(255 * f.Flash), 255, 60, 70))) g.FillRectangle(br, r);
        g.Restore(st);
        // borda: brilho vermelho por dentro e linha fina
        using (var pen = new Pen(Color.FromArgb(60, 255, 30, 50), 4f)) { var st2 = g.Save(); g.SetClip(tp, CombineMode.Intersect); g.DrawPath(pen, tp); g.Restore(st2); }
        using (var pen = new Pen(Color.FromArgb(200, 255, 50, 64), 1f)) g.DrawPath(pen, tp);
      }

      if (hasLabel)
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap }) {
          var area = new RectangleF(3, r.Bottom - labelH - 1, W - 6, labelH);
          float px = 10.5f;
          Font fo = new Font("Segoe UI Semibold", px, GraphicsUnit.Pixel);
          while (px > 8f && g.MeasureString(b.Label, fo).Width > W - 8) {
            fo.Dispose();
            px -= 0.5f;
            fo = new Font("Segoe UI Semibold", px, GraphicsUnit.Pixel);
          }
          using (var sh = new SolidBrush(Color.FromArgb(160, 0, 0, 0))) g.DrawString(b.Label, fo, sh, new RectangleF(area.X, area.Y + 0.8f, area.Width, area.Height), sf);
          using (var br = new SolidBrush(Color.FromArgb(242, 236, 238))) g.DrawString(b.Label, fo, br, area, sf);
          fo.Dispose();
        }
    }
    return bmp;
  }

  // simbolo com neon: camadas de brilho vermelho e o traco claro por cima
  static void NeonGlyph(Graphics g, string glyph, float cx, float cy, float size) {
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
      foreach (var layer in new[] { new[] { 0.26f, 26 }, new[] { 0.16f, 46 }, new[] { 0.09f, 80 } })
        using (var pen = new Pen(Color.FromArgb((int)layer[1], 255, 30, 50), size * layer[0]) { LineJoin = LineJoin.Round })
          g.DrawPath(pen, path);
      var bb2 = path.GetBounds();
      using (var br = new LinearGradientBrush(new RectangleF(bb2.X, bb2.Y - 1, bb2.Width, bb2.Height + 2), Color.FromArgb(255, 245, 246), Color.FromArgb(255, 120, 130), 90f)) {
        g.FillPath(br, path);
        using (var pen = new Pen(br, size / 30f) { LineJoin = LineJoin.Round }) g.DrawPath(pen, path);
      }
    }
  }
}

}
