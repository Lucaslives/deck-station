// ===========================================================================
//  Cena de fundo das skins com arte (chuva): a arte puxada para o vermelho,
//  com vinheta e linhas de tela digital, chuva animada em duas camadas
//  (uma atras das teclas, outra na frente, para dar profundidade) e um
//  relampago vermelho de vez em quando. As teclas ficam numa camada guardada
//  que so e redesenhada quando algo muda; a chuva sozinha so pinta por cima.
// ===========================================================================
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace StreamDeckCyd {

partial class PanelForm {
  Bitmap sceneBg, sceneBlur, uiLayer;
  Size sceneSize;
  string sceneSkin;
  bool uiDirty = true, rainTick;
  readonly Timer rainTimer = new Timer { Interval = 33 };
  readonly Random rnd = new Random();

  struct Drop { public float X, Y, Len, Speed; public int Layer; public bool Near; }
  Drop[] drops = new Drop[0];
  float flash;          // relampago (0..1)
  int flashFrame = -1, nextFlash = 400;

  // qualquer Invalidate que nao seja da chuva = algo mudou nas teclas/abas
  protected override void OnInvalidated(InvalidateEventArgs e) {
    base.OnInvalidated(e);
    if (!rainTick) uiDirty = true;
  }

  bool EnsureScene() {
    var sz = ClientSize;
    if (sz.Width <= 0 || sz.Height <= 0) return false;
    if (sceneBg != null && sceneSize == sz && sceneSkin == KeyArt.Skin) return true;
    DisposeScene();
    string file = Path.Combine(app.Dir, KeyArt.SceneFile);
    if (!File.Exists(file)) return false;
    try {
      int w = sz.Width, h = sz.Height;
      sceneBg = new Bitmap(w, h, PixelFormat.Format32bppArgb);
      using (var src = Image.FromStream(new MemoryStream(File.ReadAllBytes(file))))
      using (var g = Graphics.FromImage(sceneBg)) {
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        // cobre a tela toda, com o rosto um pouco acima do meio
        float k = Math.Max(w / (float)src.Width, h / (float)src.Height);
        float iw = src.Width * k, ih = src.Height * k;
        g.DrawImage(src, (w - iw) / 2, (h - ih) * 0.35f, iw, ih);
      }
      Grade(sceneBg);
      // vidro das teclas: a arte bem desfocada (reduz e amplia)
      sceneBlur = new Bitmap(w, h, PixelFormat.Format32bppArgb);
      using (var small = new Bitmap(Math.Max(1, w / 14), Math.Max(1, h / 14)))
      using (var gs = Graphics.FromImage(small))
      using (var gb = Graphics.FromImage(sceneBlur)) {
        gs.InterpolationMode = InterpolationMode.HighQualityBilinear;
        gs.DrawImage(sceneBg, 0, 0, small.Width, small.Height);
        gb.InterpolationMode = InterpolationMode.HighQualityBicubic;
        gb.PixelOffsetMode = PixelOffsetMode.Half;
        gb.DrawImage(small, new Rectangle(-w / 28, -h / 28, w + w / 14, h + h / 14));
      }
      uiLayer = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
      sceneSize = sz;
      sceneSkin = KeyArt.Skin;
      MakeRain(w, h);
      uiDirty = true;
      return true;
    } catch {
      DisposeScene();
      return false;
    }
  }

  void DisposeScene() {
    foreach (var b in new[] { sceneBg, sceneBlur, uiLayer }) if (b != null) b.Dispose();
    sceneBg = sceneBlur = uiLayer = null;
  }

  // vermelho: tons de preto -> vinho -> vermelho -> rosa claro, guardando o
  // vermelho que ja existe na arte (olho, gola); vinheta e linhas de tela
  static void Grade(Bitmap bmp) {
    int w = bmp.Width, h = bmp.Height;
    var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
    var px = new byte[data.Stride * h];
    Marshal.Copy(data.Scan0, px, 0, px.Length);
    float fx = w * 0.62f, fy = h * 0.4f, maxD = (float)Math.Sqrt(w * w + h * h) * 0.62f;
    for (int y = 0; y < h; y++) {
      float scan = y % 3 == 0 ? 0.86f : 1f;
      int row = y * data.Stride;
      for (int x = 0; x < w; x++) {
        int i = row + x * 4;
        float b = px[i], g = px[i + 1], r = px[i + 2];
        float l = (0.3f * r + 0.59f * g + 0.11f * b) / 255f;
        l = Math.Min(1, Math.Max(0, (l - 0.5f) * 1.18f + 0.5f));   // um pouco mais de contraste
        float hi = Math.Max(0, (l - 0.6f) * 1.6f);
        float dr = 255 * Math.Min(1, l * 1.6f), dg = 255 * hi * 0.8f + l * 22, db = 255 * hi * 0.78f + l * 30;
        float red = Math.Max(0, Math.Min(1, (r - Math.Max(g, b)) / 110f));
        float t = 0.18f + 0.55f * red;
        float dx = (x - fx) / maxD, dy = (y - fy) / maxD;
        float vig = Math.Max(0.25f, 1 - 1.35f * (dx * dx + dy * dy) * 2.2f);
        float k = 0.8f * vig * scan;
        px[i + 2] = (byte)Math.Min(255, (dr * (1 - t) + r * t) * k);
        px[i + 1] = (byte)Math.Min(255, (dg * (1 - t) + g * t) * k);
        px[i] = (byte)Math.Min(255, (db * (1 - t) + b * t) * k);
        px[i + 3] = 255;
      }
    }
    Marshal.Copy(px, 0, data.Scan0, px.Length);
    bmp.UnlockBits(data);
  }

  // ---- chuva ----
  void MakeRain(int w, int h) {
    drops = new Drop[170];
    for (int i = 0; i < drops.Length; i++) {
      drops[i] = NewDrop(w, h, i >= 140);
      drops[i].Y = (float)(rnd.NextDouble() * (h + 200)) - 100;
    }
    if (!rainTimer.Enabled) {
      rainTimer.Tick -= RainTick;
      rainTimer.Tick += RainTick;
      rainTimer.Start();
    }
  }

  Drop NewDrop(int w, int h, bool near) {
    float s = h / 800f;
    var d = new Drop { Near = near };
    d.Len = (near ? 45 + (float)rnd.NextDouble() * 40 : 14 + (float)rnd.NextDouble() * 18) * s;
    d.Speed = (near ? 34 + (float)rnd.NextDouble() * 14 : 15 + (float)rnd.NextDouble() * 9) * s;
    d.Layer = rnd.Next(3);
    d.Y = -d.Len - (float)rnd.NextDouble() * h * 0.3f;
    d.X = (float)rnd.NextDouble() * (w + h * Lean) - h * Lean;
    return d;
  }

  const float Lean = 0.3f;   // inclinacao da chuva (igual a arte)

  void RainTick(object s, EventArgs e) {
    if (!KeyArt.HasScene || sceneBg == null) { rainTimer.Stop(); return; }
    if (!Visible) return;
    int w = sceneSize.Width, h = sceneSize.Height;
    for (int i = 0; i < drops.Length; i++) {
      drops[i].Y += drops[i].Speed;
      drops[i].X += drops[i].Speed * Lean;
      if (drops[i].Y - drops[i].Len > h) drops[i] = NewDrop(w, h, drops[i].Near);
    }
    // relampago: dois clarões rapidos a cada 15-35 s
    if (--nextFlash <= 0) { flashFrame = 0; nextFlash = 450 + rnd.Next(600); }
    if (flashFrame >= 0) {
      float[] curve = { 0.9f, 0.5f, 0.15f, 0.05f, 0.7f, 1f, 0.65f, 0.4f, 0.25f, 0.14f, 0.07f, 0.03f };
      flash = flashFrame < curve.Length ? curve[flashFrame] : 0;
      if (++flashFrame >= curve.Length) { flashFrame = -1; flash = 0; }
    }
    rainTick = true;
    Invalidate();
    rainTick = false;
  }

  static readonly int[] FarAlpha = { 34, 52, 74 }, NearAlpha = { 70, 100, 135 };

  void DrawRain(Graphics g, bool near) {
    float s = sceneSize.Height / 800f;
    var pens = new Pen[3];
    try {
      for (int k = 0; k < 3; k++)
        pens[k] = near ? new Pen(Color.FromArgb(NearAlpha[k], 255, 214, 220), 2f * s)
                       : new Pen(Color.FromArgb(FarAlpha[k], 255, 226, 230), 1f * Math.Max(1, s));
      foreach (var d in drops) {
        if (d.Near != near) continue;
        g.DrawLine(pens[d.Layer], d.X, d.Y, d.X - d.Len * Lean, d.Y - d.Len);
      }
    } finally { foreach (var p in pens) if (p != null) p.Dispose(); }
  }

  // desenho completo com a cena: fundo, chuva de tras, teclas, chuva da frente
  void DrawScene(Graphics g) {
    var old = g.InterpolationMode;
    g.InterpolationMode = InterpolationMode.NearestNeighbor;
    g.PixelOffsetMode = PixelOffsetMode.Half;
    g.CompositingQuality = CompositingQuality.HighSpeed;
    g.DrawImage(sceneBg, 0, 0, sceneSize.Width, sceneSize.Height);
    if (flash > 0)
      using (var br = new SolidBrush(Color.FromArgb((int)(70 * flash), 255, 40, 60))) g.FillRectangle(br, 0, 0, sceneSize.Width, sceneSize.Height);
    DrawRain(g, false);
    if (uiDirty) {
      using (var ug = Graphics.FromImage(uiLayer)) {
        ug.Clear(Color.Transparent);
        Quality(ug);
        DrawUi(ug);
      }
      uiDirty = false;
    }
    g.DrawImage(uiLayer, 0, 0, sceneSize.Width, sceneSize.Height);
    g.InterpolationMode = old;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    // na mesa de som e nos esportes a chuva da frente atrapalharia a leitura
    if (!mixer && !sports) DrawRain(g, true);
  }

  // vidro da tecla: a arte desfocada atras dela
  void DrawKeyGlass(Graphics g, Rectangle r) {
    if (sceneBlur == null) return;
    float rad = 13f * r.Width / KeyArt.W;
    using (var p = Round(new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), rad)) {
      var st = g.Save();
      g.SetClip(p, CombineMode.Intersect);
      g.DrawImage(sceneBlur, r, r, GraphicsUnit.Pixel);
      g.Restore(st);
    }
  }
}

}
