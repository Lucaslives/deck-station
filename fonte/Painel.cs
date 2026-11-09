// ===========================================================================
//  Painel em tela cheia para a tela do tablet (spacedesk ou outro monitor).
//  O toque vira clique; a janela nunca pega o foco, entao atalhos e texto
//  continuam indo para o programa que estava em uso.
// ===========================================================================
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace StreamDeckCyd {

partial class PanelForm : Form {
  readonly DeckApp app;
  DeckConfig cfg;
  int page;

  // estado vindo do PC
  bool haveSt, spkMute, micMute;
  int cpu, ram, vol;
  readonly int[] keyState = new int[12];  // 0 normal, 1 ativo, 2 desligado, 3 no ar
  readonly DateTime[] errUntil = new DateTime[12];

  // layout
  int cols = 4, rows = 3, barH;
  readonly Rectangle[] keyRects = new Rectangle[12];
  // uma aba por pagina no topo; o resto da barra mostra volume, CPU/RAM e relogio
  readonly List<Rectangle> tabRects = new List<Rectangle>();
  // X no canto: primeiro toque arma ("Fechar?"), segundo toque em ate 3 s fecha o programa
  Rectangle closeRect;
  DateTime closeArmedUntil;
  bool CloseArmed { get { return closeArmedUntil > DateTime.Now; } }
  int pressed = -1;

  // teclas desenhadas no tamanho da tela
  readonly Dictionary<DeckButton, Bitmap> cache = new Dictionary<DeckButton, Bitmap>();
  int renderScale = 1;

  // animacao de toque
  readonly Timer animTimer = new Timer { Interval = 33 };
  readonly Timer tick = new Timer { Interval = 500 };
  int animKey = -1, animFrame;
  AnimFrame[] animFrames;
  Bitmap[] animBmps;
  bool blinkOn;

  public PanelForm(DeckApp app) {
    this.app = app;
    Text = "Deck Station";
    FormBorderStyle = FormBorderStyle.None;
    StartPosition = FormStartPosition.Manual;
    ShowInTaskbar = false;
    DoubleBuffered = true;
    SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    BackColor = KeyArt.Bg;
    Icon = DeckApp.MakeTrayIcon();
    animTimer.Tick += (s, e) => AnimTick();
    tick.Tick += (s, e) => { blinkOn = !blinkOn; Invalidate(); };
    tick.Start();
    mixTimer.Tick += (s, e) => { if (mixer) Invalidate(); };
    paintRetry.Tick += (s, e) => { paintRetry.Stop(); Invalidate(); };
    cursorTimer.Tick += (s, e) => TrackCursor();
    cursorTimer.Start();
  }

  // ---- janela que nao rouba o foco ----
  const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
  const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
  protected override bool ShowWithoutActivation { get { return true; } }
  protected override CreateParams CreateParams {
    get {
      var p = base.CreateParams;
      p.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
      p.Style |= 0x02000000;   // WS_CLIPCHILDREN: nao pinta por cima da janela do Digivice
      return p;
    }
  }
  protected override void WndProc(ref Message m) {
    if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
    base.WndProc(ref m);
  }

  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetProp(IntPtr hWnd, string name, IntPtr data);
  protected override void OnHandleCreated(EventArgs e) {
    base.OnHandleCreated(e);
    // sem "segurar = botao direito", sem bolinha de toque e sem gestos de caneta
    SetProp(Handle, "MicrosoftTabletPenServiceProperty", (IntPtr)(0x1 | 0x8 | 0x10 | 0x100 | 0x10000));
  }

  // ---- chamado pelo programa (sempre na thread da janela) ----
  public void SetPage(DeckConfig c, int pg) {
    if (c != cfg) { ClearCache(); BackColor = KeyArt.Bg; DoLayout(); }
    if (c != cfg || pg != page) { Array.Clear(keyState, 0, 12); StopAnim(); }
    if (pg != page) { CloseMixer(); CloseSports(); }
    cfg = c;
    page = pg;
    LayoutTabs();
    // a aba chamada Digivice mostra o jogo no lugar das teclas
    if (pg >= 0 && pg < c.Pages.Count && DeckConfig.Norm(c.Pages[pg].Name) == "digivice") OpenDigi();
    else CloseDigi();
    Invalidate();
  }

  public void SetStatus(int cpu, int ram, int vol, bool spkMute, bool micMute) {
    this.cpu = cpu; this.ram = ram; this.vol = vol; this.spkMute = spkMute; this.micMute = micMute;
    haveSt = true;
    Invalidate(barRectInv());
    for (int i = 0; i < 12; i++) {
      var b = Btn(i);
      if (b != null && b.Flags != "") Invalidate(keyRects[i]);
    }
  }

  public void SetKeyStates(int[] st) {
    bool changed = false;
    for (int i = 0; i < 12; i++) if (keyState[i] != st[i]) { keyState[i] = st[i]; changed = true; }
    if (changed) Invalidate();
  }

  // aviso na parte de baixo do painel (o balao do Windows fica na tela principal)
  string notice = "";
  DateTime noticeUntil;

  public void Error(int i, string msg) {
    if (i < 0 || i >= 12) return;
    errUntil[i] = DateTime.Now.AddSeconds(1.5);
    notice = msg;
    noticeUntil = DateTime.Now.AddSeconds(5);
    Invalidate();
  }

  void DrawNotice(Graphics g) {
    if (noticeUntil < DateTime.Now || notice == "") return;
    int w = ClientSize.Width, h = ClientSize.Height;
    using (var f = new Font("Segoe UI Semibold", barH * 0.27f, GraphicsUnit.Pixel)) {
      var sz = g.MeasureString(notice, f, (int)(w * 0.9f));
      var r = new RectangleF((w - sz.Width) / 2 - barH * 0.4f, h - sz.Height - barH * 0.9f, sz.Width + barH * 0.8f, sz.Height + barH * 0.4f);
      using (var p = Round(r, barH * 0.2f)) {
        using (var br = new SolidBrush(Color.FromArgb(235, 150, 20, 28))) g.FillPath(br, p);
        using (var pen = new Pen(Color.FromArgb(255, 120, 120), 2)) g.DrawPath(pen, p);
      }
      using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        g.DrawString(notice, f, Brushes.White, r, sf);
    }
  }

  // coloca a janela ocupando a tela escolhida
  public void PlaceOn(Screen s) {
    Bounds = s.Bounds;
  }

  public Bitmap Shot() {
    var bmp = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
    using (var g = Graphics.FromImage(bmp)) Draw(g);
    return bmp;
  }

  DeckButton Btn(int i) {
    var c = cfg;
    if (c == null || page < 0 || page >= c.Pages.Count) return null;
    return c.Pages[page].Buttons[i];
  }

  void ClearCache() {
    foreach (var b in cache.Values) b.Dispose();
    cache.Clear();
  }

  // ---- layout ----
  protected override void OnResize(EventArgs e) {
    base.OnResize(e);
    DoLayout();
  }

  Rectangle barRectInv() { return new Rectangle(0, 0, ClientSize.Width, barH); }

  void DoLayout() {
    int w = ClientSize.Width, h = ClientSize.Height;
    if (w <= 0 || h <= 0) return;
    // tablet em pe: 3 colunas x 4 linhas
    if (h > w) { cols = 3; rows = 4; } else { cols = 4; rows = 3; }
    barH = Math.Max(52, Math.Min(h, w) / 10);
    int pad = Math.Max(8, Math.Min(w, h) / 60);
    int availW = w - pad * 2, availH = h - barH - pad * 2;
    // tecla 76x68, espaco entre teclas = 6% da tecla
    float kw = Math.Min(availW / (cols + (cols - 1) * 0.06f), availH / (rows + (rows - 1) * 0.06f) * KeyArt.W / KeyArt.H);
    float kh = kw * KeyArt.H / KeyArt.W, gap = kw * 0.06f;
    // skin com arte: teclas menores e mais espacadas para o personagem aparecer
    if (KeyArt.HasScene) {
      float k0 = kw;
      kw *= 0.78f;
      kh = kw * KeyArt.H / KeyArt.W;
      gap = Math.Min((availW - kw * cols) / (cols - 1f), (availH - kh * rows) / (rows - 1f)) * 0.55f;
      gap = Math.Max(gap, k0 * 0.06f);
    }
    float gw = kw * cols + gap * (cols - 1), gh = kh * rows + gap * (rows - 1);
    float x0 = (w - gw) / 2, y0 = barH + (h - barH - gh) / 2;
    for (int i = 0; i < 12; i++)
      keyRects[i] = Rectangle.Round(new RectangleF(x0 + (i % cols) * (kw + gap), y0 + (i / cols) * (kh + gap), kw, kh));
    LayoutTabs();
    LayoutDigi();
    int sc = Math.Max(1, (int)Math.Ceiling(kh / KeyArt.H));
    if (sc != renderScale) { renderScale = sc; ClearCache(); }
    Invalidate();
  }

  // abas ocupam a esquerda da barra (ate 64% da largura); o status fica a direita
  void LayoutTabs() {
    tabRects.Clear();
    var c = cfg;
    int w = ClientSize.Width;
    int cp = barH / 8;
    closeRect = new Rectangle(w - (int)(barH * 1.9f), cp, (int)(barH * 1.9f) - cp, barH - cp * 2);
    if (c == null || c.Pages.Count < 2 || w <= 0) return;
    int pad = barH / 8;
    float tw = Math.Min(barH * 4.2f, (w * (c.Pages.Count > 4 ? 0.66f : 0.58f) - pad) / c.Pages.Count);
    for (int i = 0; i < c.Pages.Count; i++)
      tabRects.Add(Rectangle.Round(new RectangleF(pad + i * tw, pad, tw - pad, barH - pad * 2)));
  }

  // ---- toque ----
  // o mesmo toque as vezes chega duas vezes (toque + clique do spacedesk):
  // um segundo toque no mesmo lugar em menos de 350 ms e ignorado
  DateTime lastDown;
  Point lastDownAt;
  bool fromTouch;

  [DllImport("user32.dll")] static extern IntPtr GetMessageExtraInfo();
  // clique gerado por toque/caneta do Windows tem esta assinatura
  static bool IsTouchClick() { return ((long)GetMessageExtraInfo() & 0xFFFFFF00) == 0xFF515700; }

  // onde o cursor estava fora do painel (no jogo): volta para la depois do toque
  readonly Timer cursorTimer = new Timer { Interval = 50 };
  Point lastOutside = Point.Empty;
  bool haveOutside;

  void TrackCursor() {
    var p = Cursor.Position;
    if (!Bounds.Contains(p)) { lastOutside = p; haveOutside = true; }
  }

  void RestoreCursor() {
    if (fromTouch && haveOutside) Cursor.Position = lastOutside;
    fromTouch = false;
  }

  // registro de cada toque nas teclas (para achar tecla que "nao responde")
  void LogTouch(Point p, bool dup) {
    int k = -1;
    for (int i = 0; i < 12; i++) if (keyRects[i].Contains(p)) k = i;
    app.Log("toque (" + p.X + "," + p.Y + ") tecla " + k + (fromTouch ? " dedo" : " mouse") + (dup ? " duplicado, ignorado" : ""));
  }

  protected override void OnMouseDown(MouseEventArgs e) {
    base.OnMouseDown(e);
    if (e.Button != MouseButtons.Left) return;
    var now = DateTime.Now;
    bool dup = (now - lastDown).TotalMilliseconds < 350 && Math.Abs(e.X - lastDownAt.X) < 40 && Math.Abs(e.Y - lastDownAt.Y) < 40;
    lastDown = now;
    lastDownAt = e.Location;
    fromTouch = IsTouchClick();
    if (!mixer && !sports && !digi) LogTouch(e.Location, dup);
    if (dup) return;
    if (closeRect.Contains(e.Location)) {
      // segundo toque precisa vir com calma (nao conta toque duplicado)
      if (CloseArmed && (closeArmedUntil - now).TotalSeconds < 2.2) { app.Quit("botao X do painel"); return; }
      if (CloseArmed) return;
      closeArmedUntil = DateTime.Now.AddSeconds(3);
      Invalidate(closeRect);
      return;
    }
    for (int t = 0; t < tabRects.Count; t++)
      if (tabRects[t].Contains(e.Location)) {
        CloseMixer();
        CloseSports();
        if (t != page) app.Touch("GOTO " + t);
        return;
      }
    if (mixer) { MixerDown(e.Location); return; }
    if (sports) { SportsDown(e.Location); return; }
    if (digi) return;
    for (int i = 0; i < 12; i++)
      if (keyRects[i].Contains(e.Location) && Btn(i) != null) {
        pressed = i;
        Invalidate(keyRects[i]);
        return;
      }
  }

  protected override void OnMouseUp(MouseEventArgs e) {
    base.OnMouseUp(e);
    RestoreCursor();
    if (MixerUp() || SportsUp()) return;
    int i = pressed;
    pressed = -1;
    if (i < 0) return;
    Invalidate(keyRects[i]);
    if (!keyRects[i].Contains(e.Location)) return;  // arrastou para fora: cancela
    StartAnim(i);
    app.Touch("P " + i);
  }

  // ---- animacao ----
  void StartAnim(int i) {
    StopAnim();
    var b = Btn(i);
    if (b == null) return;
    string kind = KeyArt.AnimKind(b);
    if (kind == "nenhuma") return;
    animKey = i;
    animFrame = 0;
    animFrames = KeyArt.Animation(kind);
    animBmps = new Bitmap[animFrames.Length];
    animTimer.Start();
    Invalidate(keyRects[i]);
  }

  void StopAnim() {
    animTimer.Stop();
    if (animBmps != null) foreach (var bm in animBmps) if (bm != null) bm.Dispose();
    animBmps = null;
    animFrames = null;
    int k = animKey;
    animKey = -1;
    if (k >= 0) Invalidate(keyRects[k]);
  }

  void AnimTick() {
    if (animKey < 0) { animTimer.Stop(); return; }
    animFrame++;
    if (animFrame >= animFrames.Length) { StopAnim(); return; }
    Invalidate(keyRects[animKey]);
  }

  Bitmap KeyBitmap(int i, DeckButton b) {
    var c = cfg;
    if (i == animKey && animBmps != null && animFrame < animBmps.Length) {
      if (animBmps[animFrame] == null) animBmps[animFrame] = KeyArt.Render(b, c.Labels, animFrames[animFrame], renderScale);
      return animBmps[animFrame];
    }
    Bitmap bmp;
    if (!cache.TryGetValue(b, out bmp)) {
      bmp = KeyArt.Render(b, c.Labels, null, renderScale);
      cache[b] = bmp;
    }
    return bmp;
  }

  // ---- desenho ----
  // erro no desenho (acontece quando o tablet desconecta/reconecta): sem este
  // cuidado o Windows troca o painel por um X vermelho ate fechar o programa
  public volatile bool FailNextPaint;   // teste (mesa.pedido com a linha FALHA)
  readonly Timer paintRetry = new Timer { Interval = 500 };
  DateTime lastPaintLog;

  protected override void OnPaint(PaintEventArgs e) {
    try {
      if (FailNextPaint) { FailNextPaint = false; throw new InvalidOperationException("erro de teste"); }
      Draw(e.Graphics);
    } catch (Exception ex) {
      if ((DateTime.Now - lastPaintLog).TotalSeconds > 30) {
        lastPaintLog = DateTime.Now;
        app.Log("erro ao desenhar o painel: " + ex);
      }
      try { e.Graphics.Clear(KeyArt.Bg); } catch { }
      DisposeScene();   // refaz a arte na proxima tentativa
      paintRetry.Start();
    }
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

  static readonly Color Red = Color.FromArgb(216, 20, 24), On = Color.FromArgb(56, 228, 80);

  static void Quality(Graphics g) {
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
  }

  void Draw(Graphics g) {
    Quality(g);
    // skin com arte de fundo e chuva (Cena.cs)
    if (KeyArt.HasScene && EnsureScene()) { DrawScene(g); return; }
    g.Clear(KeyArt.Bg);
    DrawUi(g);
  }

  void DrawUi(Graphics g) {
    DrawBar(g);
    // mesa de som / esportes por cima da arte: fundo escuro para ler bem
    if ((mixer || sports) && KeyArt.HasScene)
      using (var br = new SolidBrush(Color.FromArgb(190, 10, 4, 7))) g.FillRectangle(br, 0, barH, ClientSize.Width, ClientSize.Height - barH);
    if (mixer) DrawMixer(g);
    else if (sports) DrawSports(g);
    else if (digi) DrawDigi(g);
    else {
      var c = cfg;
      if (c != null && c.Pages.Count > 0) for (int i = 0; i < 12; i++) DrawKey(g, i);
    }
    DrawNotice(g);
  }

  void DrawBar(Graphics g) {
    int w = ClientSize.Width;
    Color bg = KeyArt.Bg, text = Color.FromArgb(230, 232, 238), dim = Color.FromArgb(150, 155, 168);
    if (KeyArt.HasScene) {
      // barra de vidro escuro com uma linha vermelha brilhando embaixo
      using (var br = new LinearGradientBrush(new Rectangle(0, -1, w, barH + 2), Color.FromArgb(200, 6, 2, 4), Color.FromArgb(130, 20, 4, 8), 90f)) g.FillRectangle(br, 0, 0, w, barH);
      using (var pen = new Pen(Color.FromArgb(70, 255, 30, 50), 5)) g.DrawLine(pen, 0, barH - 2, w, barH - 2);
      using (var pen = new Pen(Color.FromArgb(220, 255, 50, 64), 1.5f)) g.DrawLine(pen, 0, barH - 1, w, barH - 1);
    } else {
      using (var br = new SolidBrush(Mix(bg, Color.White, 0.07f))) g.FillRectangle(br, 0, 0, w, barH);
      using (var pen = new Pen(Mix(bg, Color.White, 0.15f))) g.DrawLine(pen, 0, barH - 1, w, barH - 1);
    }
    var c = cfg;
    float fs = barH * 0.36f;
    using (var font = new Font("Segoe UI Semibold", fs, GraphicsUnit.Pixel))
    using (var small = new Font("Segoe UI", fs * 0.72f, GraphicsUnit.Pixel))
    using (var tb = new SolidBrush(text))
    using (var db = new SolidBrush(dim)) {
      float mid = barH / 2f;
      using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap }) {
        if (tabRects.Count > 0) {
          Color accent = KeyArt.Accent;
          for (int t = 0; t < tabRects.Count && t < c.Pages.Count; t++) {
            var r = tabRects[t];
            bool sel = t == page;
            using (var p = Round(r, r.Height * 0.22f)) {
              using (var br = new SolidBrush(sel ? Mix(bg, accent, 0.28f) : Mix(bg, Color.White, 0.04f))) g.FillPath(br, p);
              using (var pen = new Pen(sel ? accent : Mix(bg, Color.White, 0.16f), sel ? 2f : 1f)) g.DrawPath(pen, p);
            }
            using (var tf = new Font("Segoe UI Semibold", barH * (c.Pages.Count > 4 ? 0.225f : 0.25f), GraphicsUnit.Pixel))
            using (var br = new SolidBrush(sel ? Color.White : dim))
              g.DrawString(c.Pages[t].Name, tf, br, new RectangleF(r.X + 4, r.Y, r.Width - 8, r.Height), sf);
          }
        } else {
          string title = c == null || c.Pages.Count == 0 ? "Deck Station" : c.Pages[page].Name;
          sf.Alignment = StringAlignment.Near;
          g.DrawString(title, font, tb, new RectangleF(barH * 0.4f, 0, w * 0.4f, barH), sf);
        }
      }

      // direita: CPU, RAM, volume e relogio
      bool armed = CloseArmed;
      using (var p = Round(closeRect, closeRect.Height * 0.22f)) {
        using (var br = new SolidBrush(armed ? Color.FromArgb(200, 40, 45) : Mix(bg, Color.White, 0.04f))) g.FillPath(br, p);
        using (var pen = new Pen(armed ? Color.FromArgb(255, 120, 120) : Mix(bg, Color.White, 0.16f))) g.DrawPath(pen, p);
      }
      if (armed)
        using (var tf = new Font("Segoe UI Semibold", barH * 0.24f, GraphicsUnit.Pixel))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
          g.DrawString("Fechar?", tf, Brushes.White, closeRect, sf);
      else {
        float cx = closeRect.X + closeRect.Width / 2f, cy = closeRect.Y + closeRect.Height / 2f, a = closeRect.Height * 0.18f;
        using (var pen = new Pen(dim, Math.Max(2, barH * 0.045f)) { StartCap = LineCap.Round, EndCap = LineCap.Round }) {
          g.DrawLine(pen, cx - a, cy - a, cx + a, cy + a);
          g.DrawLine(pen, cx - a, cy + a, cx + a, cy - a);
        }
      }

      float right = closeRect.X - barH * 0.3f;
      string clock = DateTime.Now.ToString("HH:mm");
      float cw = g.MeasureString(clock, font).Width;
      g.DrawString(clock, font, tb, right - cw, mid - font.Height / 2f);
      right -= cw + barH * 0.5f;
      if (!haveSt) return;
      float mw = barH * (tabRects.Count > 4 ? 0.9f : 1.6f), mh = Math.Max(4, barH * 0.13f);
      float lw = g.MeasureString("CPU", small).Width;
      float mx = right - mw - lw - 4;
      Meter(g, small, db, "CPU", cpu, mx, mid - mh - 4, lw, mw, mh);
      Meter(g, small, db, "RAM", ram, mx, mid + 4, lw, mw, mh);
      string v = spkMute ? "Mudo" : "Vol " + vol + "%";
      float vw = g.MeasureString(v, small).Width;
      // sem espaco ao lado das abas: o volume ja aparece nas teclas de volume
      if (tabRects.Count > 0 && mx - vw - barH * 0.5f < tabRects[tabRects.Count - 1].Right + barH * 0.2f) return;
      using (var vb = new SolidBrush(spkMute ? Color.FromArgb(240, 90, 90) : dim))
        g.DrawString(v, small, vb, mx - vw - barH * 0.5f, mid - small.Height / 2f);
    }
  }

  static void Meter(Graphics g, Font f, Brush lb, string name, int pct, float x, float y, float lw, float w, float h) {
    g.DrawString(name, f, lb, x, y + h / 2 - f.Height / 2f);
    var r = new RectangleF(x + lw + 4, y, w, h);
    using (var p = Round(r, h / 2))
    using (var br = new SolidBrush(Color.FromArgb(60, 255, 255, 255))) g.FillPath(br, p);
    float fw = w * Math.Max(0, Math.Min(100, pct)) / 100f;
    if (fw < h) return;
    Color c = pct >= 85 ? Color.FromArgb(230, 70, 70) : pct >= 60 ? Color.FromArgb(240, 180, 0) : Color.FromArgb(80, 200, 120);
    using (var p = Round(new RectangleF(r.X, r.Y, fw, h), h / 2))
    using (var br = new SolidBrush(c)) g.FillPath(br, p);
  }

  void DrawKey(Graphics g, int i) {
    var r = keyRects[i];
    var b = Btn(i);
    var st = g.Save();
    g.TranslateTransform(r.X, r.Y);
    g.ScaleTransform(r.Width / (float)KeyArt.W, r.Height / (float)KeyArt.H);
    var face = new RectangleF(0.5f, 0.5f, KeyArt.W - 1, KeyArt.H - 1);
    if (b == null) {
      using (var p = Round(face, 10))
      using (var pen = new Pen(Mix(KeyArt.Bg, Color.White, 0.12f), 1f)) g.DrawPath(pen, p);
      g.Restore(st);
      return;
    }
    if (KeyArt.HasScene && !(b.Picture != null && b.PictureFill)) {
      var gs = g.Save();
      g.ResetTransform();
      DrawKeyGlass(g, r);
      g.Restore(gs);
    }
    g.DrawImage(KeyBitmap(i, b), new RectangleF(0, 0, KeyArt.W, KeyArt.H));

    using (var clip = Round(face, 11)) {
      var cs = g.Save();
      g.SetClip(clip, CombineMode.Intersect);
      bool micRed = b.Flags.Contains("m") && micMute && haveSt;
      bool spkRed = b.Flags.Contains("s") && spkMute && haveSt;
      if (keyState[i] == 3) {
        // no ar / gravando: vermelho com um ponto piscando
        using (var br = new SolidBrush(Color.FromArgb(90, Red))) g.FillRectangle(br, face);
        g.FillEllipse(Brushes.White, KeyArt.W - 18, 3, 14, 14);
        if (blinkOn) using (var br = new SolidBrush(Red)) g.FillEllipse(br, KeyArt.W - 16, 5, 10, 10);
      }
      if (micRed || spkRed || keyState[i] == 2) {
        using (var br = new SolidBrush(Color.FromArgb(120, Red))) g.FillRectangle(br, face);
        using (var pen = new Pen(Color.White, 4) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(pen, 18, 10, 58, 58);
      }
      if (b.Flags.Contains("v") && haveSt)
        using (var f = new Font("Segoe UI Semibold", 9, GraphicsUnit.Pixel))
        using (var sf = new StringFormat { Alignment = StringAlignment.Far }) {
          g.DrawString(vol.ToString(), f, Brushes.Black, new RectangleF(0, 4.6f, KeyArt.W - 5.4f, 14), sf);
          g.DrawString(vol.ToString(), f, Brushes.White, new RectangleF(0, 4, KeyArt.W - 6, 14), sf);
        }
      if (i == pressed)
        using (var br = new SolidBrush(Color.FromArgb(110, 0, 0, 0))) g.FillRectangle(br, face);
      g.Restore(cs);
      if (keyState[i] == 1) {
        // ativo (cena atual, camera virtual...): borda verde e um ponto
        using (var pen = new Pen(On, 3)) g.DrawPath(pen, clip);
        using (var br = new SolidBrush(On)) g.FillEllipse(br, KeyArt.W - 16, 5, 10, 10);
        g.DrawEllipse(Pens.White, KeyArt.W - 17, 4, 12, 12);
      }
      if (i == pressed) using (var pen = new Pen(Color.White, 1.5f)) g.DrawPath(pen, clip);
      if (errUntil[i] > DateTime.Now) using (var pen = new Pen(Red, 3)) g.DrawPath(pen, clip);
    }
    g.Restore(st);
  }
}

}
