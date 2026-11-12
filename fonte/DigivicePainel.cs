// ===========================================================================
//  Aba Digivice: o mundo 3D do Digivice, sem controle (estilo tamagotchi), roda
//  em digivice\Digivice-Tablet.exe e a janela dele fica dentro do painel,
//  ocupando toda a area embaixo das abas. Sair da aba fecha o jogo (ele salva
//  ao fechar), para nao gastar o PC em segundo plano. Toque nela nao tira o
//  foco do jogo da frente.
// ===========================================================================
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace StreamDeckCyd {

partial class PanelForm {
  const string DigiProcess = "Digivice-Tablet";
  const int GWL_STYLE = -16, WS_CHILD = 0x40000000, WS_CLIPSIBLINGS = 0x04000000;
  const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, WM_CLOSE = 0x10;

  delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
  [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
  [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
  [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
  [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);

  bool digi;
  Process digiProc;
  IntPtr digiWnd;
  int digiWait;
  string digiError = "";
  Rectangle digiRect;
  Timer digiTimer;

  void OpenDigi() {
    if (digi) return;
    CloseMixer();
    CloseSports();
    digi = true;
    StopAnim();
    pressed = -1;
    digiError = "";
    if (digiWnd != IntPtr.Zero) ShowWindow(digiWnd, SW_SHOWNOACTIVATE);
    else if (digiProc == null) StartDigi();
    UpdateRain();   // a chuva fica atras do jogo: para
    Invalidate();
  }

  void CloseDigi() {
    if (!digi) return;
    digi = false;
    StopDigi(false);
    UpdateRain();
    Invalidate();
  }

  void StartDigi() {
    string exe = Path.Combine(app.Dir, "digivice", DigiProcess + ".exe");
    if (!File.Exists(exe)) { digiError = "Nao achei " + exe; return; }
    // o anterior ainda salvando (aba trocada agora) ou sobra de uma execucao
    // que caiu: espera um pouco e fecha
    foreach (var old in Process.GetProcessesByName(DigiProcess)) {
      try { if (!old.WaitForExit(1500)) old.Kill(); } catch { }
      old.Dispose();
    }
    try {
      digiProc = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = false, CreateNoWindow = true });
    } catch (Exception ex) {
      digiError = "Nao consegui abrir o Digivice: " + ex.Message;
      return;
    }
    digiWait = 0;
    if (digiTimer == null) {
      digiTimer = new Timer { Interval = 150 };
      digiTimer.Tick += (s, e) => DigiTick();
    }
    digiTimer.Start();
  }

  void DigiTick() {
    if (digiProc == null) { digiTimer.Stop(); return; }
    if (digiProc.HasExited) {
      app.Log("digivice fechou (codigo " + digiProc.ExitCode + ")");
      digiProc.Dispose();
      digiProc = null;
      digiWnd = IntPtr.Zero;
      digiTimer.Stop();
      if (digi) StartDigi();   // caiu com a aba aberta: abre de novo
      return;
    }
    if (digiWnd != IntPtr.Zero) return;
    IntPtr h = FindDigiWindow((uint)digiProc.Id);
    if (h == IntPtr.Zero) {
      if (++digiWait > 100) { digiError = "A janela do Digivice nao apareceu"; digiWait = 0; Invalidate(); }
      return;
    }
    // vira janela filha do painel: anda junto e nao ganha foco no toque
    SetWindowLongPtr(h, GWL_STYLE, (IntPtr)(WS_CHILD | WS_CLIPSIBLINGS));
    SetParent(h, Handle);
    digiWnd = h;
    PlaceDigi();
    ShowWindow(h, digi ? SW_SHOWNOACTIVATE : SW_HIDE);
    Invalidate();
  }

  static IntPtr FindDigiWindow(uint pid) {
    IntPtr found = IntPtr.Zero;
    var sb = new StringBuilder(64);
    EnumWindows((h, l) => {
      uint p;
      GetWindowThreadProcessId(h, out p);
      if (p != pid) return true;
      sb.Length = 0;
      GetWindowText(h, sb, sb.Capacity);
      if (sb.ToString() != "Digivice") return true;
      found = h;
      return false;
    }, IntPtr.Zero);
    return found;
  }

  void LayoutDigi() {
    digiRect = new Rectangle(0, barH, ClientSize.Width, ClientSize.Height - barH);
    PlaceDigi();
  }

  void PlaceDigi() {
    if (digiWnd != IntPtr.Zero) MoveWindow(digiWnd, digiRect.X, digiRect.Y, digiRect.Width, digiRect.Height, true);
  }

  // pede para o jogo salvar e sair; se nao sair em 3 s, fecha a forca.
  // wait = false (troca de aba): espera em segundo plano, sem travar o painel
  public void StopDigi(bool wait = true) {
    if (digiTimer != null) digiTimer.Stop();
    var proc = digiProc;
    IntPtr wnd = digiWnd;
    digiProc = null;
    digiWnd = IntPtr.Zero;
    if (proc == null) return;
    if (wnd != IntPtr.Zero) {
      ShowWindow(wnd, SW_HIDE);
      PostMessage(wnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }
    Action finish = () => {
      try { if (!proc.WaitForExit(3000)) proc.Kill(); } catch { }
      proc.Dispose();
    };
    if (wait) finish();
    else new System.Threading.Thread(() => finish()) { IsBackground = true }.Start();
  }

  void DrawDigi(Graphics g) {
    if (digiWnd != IntPtr.Zero && digiError == "") return;
    using (var br = new SolidBrush(Color.FromArgb(220, 8, 3, 5))) g.FillRectangle(br, digiRect);
    using (var f = new Font("Segoe UI Semibold", barH * 0.3f, GraphicsUnit.Pixel))
    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
      g.DrawString(digiError != "" ? digiError : "Abrindo o Digivice...", f, Brushes.White, digiRect, sf);
  }
}

}
