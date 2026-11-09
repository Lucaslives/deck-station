// ===========================================================================
//  DeckStation.exe - programa do PC para o Deck Station CYD.
//  Fica na bandeja (perto do relogio), conversa com a placa pela USB,
//  manda os botoes de botoes.txt e executa as acoes quando um botao e tocado.
//  Compilar: compilar.bat (usa o compilador C# que ja vem no Windows).
// ===========================================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace StreamDeckCyd {

// ------------------------------- audio do Windows --------------------------
static partial class Audio {
  [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }

  [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IMMDeviceEnumerator {
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
  }
  [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IMMDeviceCollection {
    int GetCount(out uint count);
    int Item(uint i, out IMMDevice device);
  }
  // sessoes de audio por programa (o "mixer de volume" do Windows)
  [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IAudioSessionManager2 {
    int GetAudioSessionControl(IntPtr guid, int flags, out IntPtr ctl);
    int GetSimpleAudioVolume(IntPtr guid, int flags, out IntPtr vol);
    int GetSessionEnumerator(out IAudioSessionEnumerator e);
  }
  [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IAudioSessionEnumerator {
    int GetCount(out int count);
    int GetSession(int i, [MarshalAs(UnmanagedType.IUnknown)] out object session);
  }
  [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IAudioSessionControl2 {
    int GetState(out int st);
    int GetDisplayName(out IntPtr name);
    int SetDisplayName(IntPtr name, IntPtr ctx);
    int GetIconPath(out IntPtr path);
    int SetIconPath(IntPtr path, IntPtr ctx);
    int GetGroupingParam(out Guid g);
    int SetGroupingParam(IntPtr g, IntPtr ctx);
    int RegisterAudioSessionNotification(IntPtr n);
    int UnregisterAudioSessionNotification(IntPtr n);
    int GetSessionIdentifier(out IntPtr id);
    int GetSessionInstanceIdentifier(out IntPtr id);
    int GetProcessId(out uint pid);
    [PreserveSig] int IsSystemSoundsSession();
  }
  [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface ISimpleAudioVolume {
    int SetMasterVolume(float level, ref Guid ctx);
    int GetMasterVolume(out float level);
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
  }
  [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IMMDevice {
    int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore props);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
  }
  [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IAudioEndpointVolume {
    int RegisterControlChangeNotify(IntPtr notify);
    int UnregisterControlChangeNotify(IntPtr notify);
    int GetChannelCount(out uint count);
    int SetMasterVolumeLevel(float level, ref Guid ctx);
    int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
    int GetMasterVolumeLevel(out float level);
    int GetMasterVolumeLevelScalar(out float level);
    int SetChannelVolumeLevel(uint ch, float level, ref Guid ctx);
    int SetChannelVolumeLevelScalar(uint ch, float level, ref Guid ctx);
    int GetChannelVolumeLevel(uint ch, out float level);
    int GetChannelVolumeLevelScalar(uint ch, out float level);
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
  }

  const int RENDER = 0, CAPTURE = 1, CONSOLE = 0, COMMUNICATIONS = 2;

  // chama f com o controle de volume do dispositivo padrao (alto-falante ou microfone)
  static bool With(int flow, int role, Action<IAudioEndpointVolume> f) {
    object enumObj = null, volObj = null;
    IMMDevice dev = null;
    try {
      enumObj = new MMDeviceEnumeratorCom();
      var en = (IMMDeviceEnumerator)enumObj;
      if (en.GetDefaultAudioEndpoint(flow, role, out dev) != 0 || dev == null) return false;
      Guid iid = typeof(IAudioEndpointVolume).GUID;
      if (dev.Activate(ref iid, 23, IntPtr.Zero, out volObj) != 0) return false;
      f((IAudioEndpointVolume)volObj);
      return true;
    } catch {
      return false;
    } finally {
      if (volObj != null) Marshal.ReleaseComObject(volObj);
      if (dev != null) Marshal.ReleaseComObject(dev);
      if (enumObj != null) Marshal.ReleaseComObject(enumObj);
    }
  }

  public static int Volume() {
    float v = 0;
    With(RENDER, CONSOLE, a => a.GetMasterVolumeLevelScalar(out v));
    return (int)Math.Round(v * 100);
  }
  public static void SetVolume(int pct) {
    Guid g = Guid.Empty;
    float v = Math.Max(0, Math.Min(100, pct)) / 100f;
    With(RENDER, CONSOLE, a => { a.SetMasterVolumeLevelScalar(v, ref g); a.SetMute(false, ref g); });
  }
  public static bool SpeakerMuted() {
    bool m = false;
    With(RENDER, CONSOLE, a => a.GetMute(out m));
    return m;
  }
  public static bool MicMuted() {
    bool m = false;
    With(CAPTURE, CONSOLE, a => a.GetMute(out m));
    return m;
  }
  // muda o microfone padrao e o de comunicacao (Discord/Teams usam esse)
  public static bool SetMic(bool mute) {
    Guid g = Guid.Empty;
    bool ok = With(CAPTURE, CONSOLE, a => a.SetMute(mute, ref g));
    With(CAPTURE, COMMUNICATIONS, a => a.SetMute(mute, ref g));
    return ok;
  }

  // volume e mudo direto no Windows: funciona com qualquer jogo na frente e
  // nao abre o aviso de volume por cima do jogo (que trava jogo em tela cheia)
  public static void StepVolume(int delta) { SetVolume(Volume() + delta); }
  public static void ToggleSpeakerMute() {
    Guid g = Guid.Empty;
    With(RENDER, CONSOLE, a => { bool m; a.GetMute(out m); a.SetMute(!m, ref g); });
  }

  // ---- audio de um programa so (ex.: Discord), em todos os dispositivos ----
  // f recebe o volume de cada sessao do programa; devolve quantas achou
  static int ForApp(string process, int flow, Action<ISimpleAudioVolume> f) {
    var pids = new HashSet<uint>();
    foreach (var p in Process.GetProcessesByName(process)) { pids.Add((uint)p.Id); p.Dispose(); }
    if (pids.Count == 0) return 0;
    int found = 0;
    object enumObj = null;
    IMMDeviceCollection devs = null;
    try {
      enumObj = new MMDeviceEnumeratorCom();
      if (((IMMDeviceEnumerator)enumObj).EnumAudioEndpoints(flow, 1, out devs) != 0 || devs == null) return 0;
      uint n;
      devs.GetCount(out n);
      for (uint i = 0; i < n; i++) {
        IMMDevice dev = null;
        object mgrObj = null;
        IAudioSessionEnumerator sessions = null;
        try {
          if (devs.Item(i, out dev) != 0 || dev == null) continue;
          Guid iid = typeof(IAudioSessionManager2).GUID;
          if (dev.Activate(ref iid, 23, IntPtr.Zero, out mgrObj) != 0) continue;
          if (((IAudioSessionManager2)mgrObj).GetSessionEnumerator(out sessions) != 0 || sessions == null) continue;
          int count;
          sessions.GetCount(out count);
          for (int k = 0; k < count; k++) {
            object s = null;
            try {
              if (sessions.GetSession(k, out s) != 0 || s == null) continue;
              uint pid;
              ((IAudioSessionControl2)s).GetProcessId(out pid);
              if (!pids.Contains(pid)) continue;
              f((ISimpleAudioVolume)s);
              found++;
            } catch { } finally { if (s != null) Marshal.ReleaseComObject(s); }
          }
        } catch { } finally {
          if (sessions != null) Marshal.ReleaseComObject(sessions);
          if (mgrObj != null) Marshal.ReleaseComObject(mgrObj);
          if (dev != null) Marshal.ReleaseComObject(dev);
        }
      }
    } catch { } finally {
      if (devs != null) Marshal.ReleaseComObject(devs);
      if (enumObj != null) Marshal.ReleaseComObject(enumObj);
    }
    return found;
  }

  // microfone = o que o programa capta; som = o que ele toca
  public static bool AppMuted(string process, bool mic) {
    bool any = false, all = true;
    ForApp(process, mic ? CAPTURE : RENDER, v => { bool m; v.GetMute(out m); any = true; all &= m; });
    return any && all;
  }
  public static int SetAppMute(string process, bool mic, bool mute) {
    Guid g = Guid.Empty;
    return ForApp(process, mic ? CAPTURE : RENDER, v => v.SetMute(mute, ref g));
  }
}

// ------------------------------- uso do PC ---------------------------------
static class SysInfo {
  [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
  [StructLayout(LayoutKind.Sequential)]
  class MEMORYSTATUSEX {
    public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
    public uint dwMemoryLoad;
    public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
  }
  [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX m);

  static long lastIdle, lastTotal;
  public static int Cpu() {
    long idle, kernel, user;
    if (!GetSystemTimes(out idle, out kernel, out user)) return 0;
    long total = kernel + user;  // kernel ja inclui o tempo ocioso
    long dIdle = idle - lastIdle, dTotal = total - lastTotal;
    lastIdle = idle; lastTotal = total;
    if (dTotal <= 0) return 0;
    return (int)Math.Round(100.0 * (dTotal - dIdle) / dTotal);
  }
  public static int Ram() {
    var m = new MEMORYSTATUSEX();
    return GlobalMemoryStatusEx(m) ? (int)m.dwMemoryLoad : 0;
  }
}

// ------------------------------- acoes -------------------------------------
static class Actions {
  [StructLayout(LayoutKind.Sequential)]
  struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Sequential)]
  struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Explicit)]
  struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
  [StructLayout(LayoutKind.Sequential)]
  struct INPUT { public uint type; public InputUnion u; }
  [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);

  const uint KEYUP = 0x2, UNICODE = 0x4, EXTENDED = 0x1;

  static readonly Dictionary<string, ushort> Keys = new Dictionary<string, ushort> {
    {"ctrl", 0x11}, {"shift", 0x10}, {"alt", 0x12}, {"win", 0x5B},
    {"enter", 0x0D}, {"esc", 0x1B}, {"tab", 0x09}, {"espaco", 0x20}, {"space", 0x20},
    {"backspace", 0x08}, {"del", 0x2E}, {"delete", 0x2E}, {"insert", 0x2D}, {"home", 0x24},
    {"end", 0x23}, {"pgup", 0x21}, {"pgdn", 0x22}, {"cima", 0x26}, {"baixo", 0x28},
    {"esquerda", 0x25}, {"direita", 0x27}, {"up", 0x26}, {"down", 0x28}, {"left", 0x25},
    {"right", 0x27}, {"print", 0x2C}, {"printscreen", 0x2C}, {"pausa", 0x13},
    {"menos", 0xBD}, {"mais", 0xBB}, {"virgula", 0xBC}, {"ponto", 0xBE},
    {"play", 0xB3}, {"proxima", 0xB0}, {"anterior", 0xB1}, {"parar", 0xB2},
    {"volume+", 0xAF}, {"volume-", 0xAE}, {"mudo", 0xAD}
  };

  static bool IsExtended(ushort vk) {
    return (vk >= 0x21 && vk <= 0x2E) || vk == 0x5B || (vk >= 0xAD && vk <= 0xB3);
  }

  static INPUT Key(ushort vk, bool up) {
    var i = new INPUT { type = 1 };
    i.u.ki.wVk = vk;
    i.u.ki.dwFlags = (up ? KEYUP : 0) | (IsExtended(vk) ? EXTENDED : 0);
    return i;
  }

  // ---- jogo como administrador ----
  // o Windows nao deixa um programa normal mandar teclas para um programa
  // rodando como administrador (muitos jogos com anti-cheat): a tecla some
  [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
  [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  [DllImport("advapi32.dll")] static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
  [DllImport("advapi32.dll")] static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int len, out int retLen);

  static bool ProcessElevated(IntPtr proc) {
    IntPtr tok;
    if (!OpenProcessToken(proc, 0x0008, out tok)) return true;  // sem acesso: protegido/administrador
    try {
      int elevated, len;
      return GetTokenInformation(tok, 20, out elevated, 4, out len) && elevated != 0;
    } finally { CloseHandle(tok); }
  }

  public static readonly bool SelfElevated = ProcessElevated(Process.GetCurrentProcess().Handle);

  // a janela da frente e de um programa que nao aceita teclas nossas?
  static bool ForegroundBlocked() {
    if (SelfElevated) return false;
    uint pid;
    GetWindowThreadProcessId(GetForegroundWindow(), out pid);
    if (pid == 0) return false;
    IntPtr h = OpenProcess(0x1000, false, pid);  // PROCESS_QUERY_LIMITED_INFORMATION
    if (h == IntPtr.Zero) return true;
    try { return ProcessElevated(h); } finally { CloseHandle(h); }
  }

  static void Send(List<INPUT> list) {
    if (list.Count == 0) return;
    if (ForegroundBlocked())
      throw new Exception("o jogo/programa da frente esta como administrador e descarta teclas. " +
                          "Botao direito no icone do Deck Station > Abrir como administrador");
    if (SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf(typeof(INPUT))) == 0)
      throw new Exception("o Windows bloqueou o teclado virtual");
  }

  static ushort ParseKey(string k) {
    if (Keys.ContainsKey(k)) return Keys[k];
    if (k.Length == 1 && char.IsLetterOrDigit(k[0])) return (ushort)char.ToUpperInvariant(k[0]);
    if (k.Length >= 2 && k[0] == 'f') {
      int n;
      if (int.TryParse(k.Substring(1), out n) && n >= 1 && n <= 24) return (ushort)(0x70 + n - 1);
    }
    throw new Exception("tecla desconhecida: " + k);
  }

  // "ctrl+shift+m": aperta tudo na ordem e solta ao contrario
  public static void Hotkey(string combo) {
    var vks = new List<ushort>();
    foreach (string part in combo.Split('+')) {
      string k = DeckConfig.Norm(part);
      if (k.Length == 0) continue;
      vks.Add(ParseKey(k));
    }
    var list = new List<INPUT>();
    foreach (ushort vk in vks) list.Add(Key(vk, false));
    for (int i = vks.Count - 1; i >= 0; i--) list.Add(Key(vks[i], true));
    Send(list);
  }

  public static void TypeText(string text) {
    text = text.Replace("\\n", "\n");
    var list = new List<INPUT>();
    foreach (char ch in text) {
      if (ch == '\n') { list.Add(Key(0x0D, false)); list.Add(Key(0x0D, true)); continue; }
      foreach (bool up in new[] { false, true }) {
        var i = new INPUT { type = 1 };
        i.u.ki.wScan = ch;
        i.u.ki.dwFlags = UNICODE | (up ? KEYUP : 0);
        list.Add(i);
      }
    }
    Send(list);
  }

  // "C:\x\app.exe" --arg   ou   calc   ou   https://...   -> arquivo para o icone
  public static string ResolveTarget(string spec, out string args) {
    spec = Environment.ExpandEnvironmentVariables(spec.Trim());
    string target;
    args = "";
    if (spec.StartsWith("\"")) {
      int end = spec.IndexOf('"', 1);
      target = end > 0 ? spec.Substring(1, end - 1) : spec.Trim('"');
      if (end > 0) args = spec.Substring(end + 1).Trim();
    } else if (File.Exists(spec) || Directory.Exists(spec) || spec.Contains("://")) {
      target = spec;
    } else {
      int sp = spec.IndexOf(' ');
      target = sp > 0 && !spec.Contains("\\") ? spec.Substring(0, sp) : spec;
      if (sp > 0 && !spec.Contains("\\")) args = spec.Substring(sp + 1).Trim();
    }
    if (File.Exists(target) || Directory.Exists(target)) return Path.GetFullPath(target);
    if (target.Contains("://") || target.Contains("\\")) return null;
    // programa pelo nome (calc, notepad...): procura no PATH
    string[] dirs = (Environment.GetFolderPath(Environment.SpecialFolder.System) + ";" +
                     Environment.GetEnvironmentVariable("PATH")).Split(';');
    foreach (string d in dirs) {
      if (d.Trim().Length == 0) continue;
      foreach (string name in new[] { target, target + ".exe" }) {
        try {
          string p = Path.Combine(d.Trim(), name);
          if (File.Exists(p)) return p;
        } catch { }
      }
    }
    return null;
  }

  static string OperaGx() {
    foreach (string d in new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\Programs\\Opera GX",
                                 Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) + "\\Opera GX" }) {
      string exe = Path.Combine(d, "opera.exe");
      if (File.Exists(exe)) return exe;
    }
    return null;
  }

  static void Open(string spec) {
    string args;
    string file = ResolveTarget(spec, out args);
    string target = file;
    if (target == null) {
      string s = Environment.ExpandEnvironmentVariables(spec.Trim());
      if (s.StartsWith("\"")) {
        int end = s.IndexOf('"', 1);
        target = end > 0 ? s.Substring(1, end - 1) : s.Trim('"');
      } else {
        target = s;
      }
      if (target.StartsWith("www.")) target = "https://" + target;
    }
    // sites abrem numa aba nova do Opera GX (o padrao do Windows aqui e o Edge)
    if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) {
      string opera = OperaGx();
      if (opera != null) { Process.Start(new ProcessStartInfo(opera, "\"" + target + "\"") { UseShellExecute = false }); return; }
    }
    var psi = new ProcessStartInfo(target, args) { UseShellExecute = true };
    if (file != null && File.Exists(file)) psi.WorkingDirectory = Path.GetDirectoryName(file);
    try {
      Process.Start(psi);
    } catch (Win32Exception) {
      throw new Exception("nao achei para abrir: " + target);
    }
  }

  static void Media(string what) {
    string k = DeckConfig.Norm(what);
    if (!new[] { "play", "proxima", "anterior", "parar", "volume+", "volume-", "mudo" }.Contains(k))
      throw new Exception("midia desconhecida: " + what);
    // volume e mudo direto no audio do Windows (jogo na frente nao bloqueia)
    if (k == "volume+") { Audio.StepVolume(4); return; }
    if (k == "volume-") { Audio.StepVolume(-4); return; }
    if (k == "mudo") { Audio.ToggleSpeakerMute(); return; }
    // musica: comando de midia mandado para a barra de tarefas, sem simular
    // teclado (jogo como administrador ou com anti-cheat descarta teclas simuladas)
    int cmd = k == "play" ? 14 : k == "proxima" ? 11 : k == "anterior" ? 12 : 13;
    IntPtr tray = FindWindow("Shell_TrayWnd", null);
    IntPtr res;
    if (tray == IntPtr.Zero || SendMessageTimeout(tray, 0x0319, tray, (IntPtr)(cmd << 16), 2, 1000, out res) == IntPtr.Zero) {
      ushort vk = Keys[k];
      Send(new List<INPUT> { Key(vk, false), Key(vk, true) });
    }
  }

  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
  [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr w, IntPtr l, int flags, int timeout, out IntPtr result);

  // Executa a acao de um botao. Varias acoes: separe com " ; ".
  // Retorna a pagina para onde ir (ou null).
  public static string Run(string action) {
    string nextPage = null;
    foreach (string part in action.Split(new[] { " ; " }, StringSplitOptions.RemoveEmptyEntries)) {
      string a = part.Trim();
      int c = a.IndexOf(':');
      string kind = DeckConfig.Norm(c >= 0 ? a.Substring(0, c) : a);
      string arg = c >= 0 ? a.Substring(c + 1).Trim() : "";
      switch (kind) {
        case "midia": Media(arg); break;
        case "abrir": Open(arg); break;
        case "obs": Obs.Run(arg); break;
        case "discord": Discord.Run(arg); break;
        case "jogo": Process.Start(new ProcessStartInfo("steam://rungameid/" + arg.Trim()) { UseShellExecute = true }); break;
        case "atalho": Hotkey(arg); break;
        case "digitar": TypeText(arg); break;
        case "esperar": Thread.Sleep(Math.Min(10000, int.Parse(arg))); break;
        case "pagina": nextPage = arg; break;
        case "mixer": break;  // abre no proprio painel (App)
        case "esportes": break;
        case "comando":
          Process.Start(new ProcessStartInfo("cmd.exe", "/c " + arg) { CreateNoWindow = true, UseShellExecute = false });
          break;
        case "volume": {
          int v = int.Parse(arg.TrimStart('+'));
          if (arg.StartsWith("+") || arg.StartsWith("-")) v += Audio.Volume();
          Audio.SetVolume(v);
          break;
        }
        case "microfone": {
          string m = DeckConfig.Norm(arg);
          bool mute = m == "desligar" ? true : m == "ligar" ? false : !Audio.MicMuted();
          if (!Audio.SetMic(mute)) throw new Exception("nenhum microfone encontrado");
          break;
        }
        case "":
          break;
        default:
          throw new Exception("acao desconhecida: " + kind);
      }
    }
    return nextPage;
  }
}

static class Ext {
  public static bool Contains(this string[] arr, string s) { return Array.IndexOf(arr, s) >= 0; }
}


}
