// ===========================================================================
//  Mixer do PC (aba Area de trabalho): volume geral, volume e mudo de cada
//  programa que toca som (navegador/YouTube, Discord, jogo...) e troca da
//  saida de som (fone <-> tela), tudo direto no audio do Windows.
// ===========================================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace StreamDeckCyd {

class OutputDevice {
  public string Id = "", Name = "", Sub = "";
  public bool Default;
}

static partial class Audio {
  [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IAudioMeterInformation {
    [PreserveSig] int GetPeakValue(out float peak);
  }

  [StructLayout(LayoutKind.Sequential)]
  struct PropKey {
    public Guid Fmt;
    public int Pid;
    public PropKey(string fmt, int pid) { Fmt = new Guid(fmt); Pid = pid; }
  }
  [StructLayout(LayoutKind.Sequential)]
  struct PropVariant { public ushort Vt, R1, R2, R3; public IntPtr P, P2; }

  [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IPropertyStore {
    int GetCount(out uint n);
    int GetAt(uint i, out PropKey key);
    [PreserveSig] int GetValue(ref PropKey key, out PropVariant v);
  }
  [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant v);

  // troca da saida padrao: a mesma interface que o painel de som do Windows usa
  [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigCom { }
  [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IPolicyConfig {
    void R1(); void R2(); void R3(); void R4(); void R5(); void R6(); void R7(); void R8(); void R9(); void R10();
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
  }

  delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

  static void Rel(object o) { if (o != null) try { Marshal.ReleaseComObject(o); } catch { } }

  public static class Mixer {
    // canais: "#geral" (saida inteira), "#sistema" (sons do Windows) e um por programa
    public static volatile List<MixerChannel> Channels = new List<MixerChannel>();
    public static volatile List<OutputDevice> Outputs = new List<OutputDevice>();
    public static readonly ConcurrentDictionary<string, float> Levels = new ConcurrentDictionary<string, float>();
    public static volatile string LastError = "";

    static int gen;
    static volatile bool dirty;
    static volatile string outQueue;
    static readonly ConcurrentDictionary<string, double> volQueue = new ConcurrentDictionary<string, double>();
    static readonly ConcurrentQueue<string> muteQueue = new ConcurrentQueue<string>();

    // uma thread so cuida do audio enquanto o mixer esta aberto (os objetos do
    // Windows ficam nela; o painel so le e escreve os campos dos canais)
    public static void Start() {
      int my = Interlocked.Increment(ref gen);
      dirty = true;
      var t = new Thread(() => new Worker().Run(my)) { IsBackground = true, Name = "mixer do PC" };
      t.SetApartmentState(ApartmentState.MTA);
      t.Start();
    }
    public static void Stop() {
      Interlocked.Increment(ref gen);
      Levels.Clear();
    }

    public static void SetVolume(MixerChannel ch, double pos) {
      if (pos < 0.01) pos = 0;
      ch.Db = PosDb(pos);
      ch.Touched = DateTime.Now;
      volQueue[ch.Id] = pos;
    }
    public static void ToggleMute(MixerChannel ch) {
      ch.Muted = !ch.Muted;
      ch.Touched = DateTime.Now;
      muteQueue.Enqueue(ch.Id);
    }
    public static void SetOutput(string id) {
      foreach (var o in Outputs) o.Default = o.Id == id;
      outQueue = id;
    }

    // o fader guarda a posicao como dB na mesma curva da mesa do OBS (posicao = volume do Windows)
    static double PosDb(double p) { return p <= 0.01 ? -100 : Math.Max(-100, 60 * Math.Log10(p)); }
    static bool Fresh(MixerChannel ch) { return (DateTime.Now - ch.Touched).TotalSeconds > 1; }
    static MixerChannel Find(List<MixerChannel> list, string id) {
      foreach (var ch in list) if (ch.Id == id) return ch;
      return null;
    }

    class Worker {
      IMMDeviceEnumerator en;
      IMMDevice dev;
      IAudioEndpointVolume ep;
      IAudioMeterInformation epMeter;
      readonly Dictionary<string, List<object>> groups = new Dictionary<string, List<object>>();

      public void Run(int my) {
        object enumObj = null;
        DateTime lastEnum = DateTime.MinValue;
        try {
          enumObj = new MMDeviceEnumeratorCom();
          en = (IMMDeviceEnumerator)enumObj;
          while (gen == my) {
            try {
              if (dirty || (DateTime.Now - lastEnum).TotalSeconds > 1.5) {
                dirty = false;
                lastEnum = DateTime.Now;
                Enumerate();
              }
              Apply();
              Meter();
            } catch (Exception ex) {
              LastError = ex.Message;
              dirty = true;
              Thread.Sleep(500);
            }
            Thread.Sleep(35);
          }
        } catch (Exception ex) {
          LastError = ex.Message;
        } finally {
          ReleaseAll();
          Rel(enumObj);
        }
      }

      void ReleaseAll() {
        foreach (var l in groups.Values) foreach (var s in l) Rel(s);
        groups.Clear();
        Rel(epMeter); epMeter = null;
        Rel(ep); ep = null;
        Rel(dev); dev = null;
      }

      void Enumerate() {
        ReleaseAll();
        // saidas de som ligadas
        string defId = null;
        IMMDevice d;
        if (en.GetDefaultAudioEndpoint(RENDER, CONSOLE, out d) == 0 && d != null) { dev = d; dev.GetId(out defId); }
        var outs = new List<OutputDevice>();
        IMMDeviceCollection all;
        if (en.EnumAudioEndpoints(RENDER, 1, out all) == 0 && all != null) {
          uint n;
          all.GetCount(out n);
          for (uint i = 0; i < n; i++) {
            IMMDevice x = null;
            try {
              if (all.Item(i, out x) != 0 || x == null) continue;
              string id;
              x.GetId(out id);
              var od = new OutputDevice { Id = id, Default = id == defId };
              Names(x, od);
              // saidas falsas da Steam (Remote Play) so atrapalham
              if (od.Sub.IndexOf("Steam Streaming", StringComparison.OrdinalIgnoreCase) < 0) outs.Add(od);
            } catch { } finally { Rel(x); }
          }
          Rel(all);
        }
        Outputs = outs;
        var old = Channels;
        var list = new List<MixerChannel>();
        if (dev == null) { Channels = list; LastError = "Nenhuma saida de som ligada"; return; }
        LastError = "";

        object o;
        Guid iid = typeof(IAudioEndpointVolume).GUID;
        if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) == 0) ep = (IAudioEndpointVolume)o;
        iid = typeof(IAudioMeterInformation).GUID;
        if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) == 0) epMeter = (IAudioMeterInformation)o;

        var geral = Find(old, "#geral") ?? new MixerChannel { Id = "#geral", Name = "Geral" };
        if (ep != null && Fresh(geral)) {
          float v; bool m;
          ep.GetMasterVolumeLevelScalar(out v);
          ep.GetMute(out m);
          geral.Db = PosDb(v);
          geral.Muted = m;
        }
        list.Add(geral);

        // programas tocando nessa saida (o "mixer de volume" do Windows)
        iid = typeof(IAudioSessionManager2).GUID;
        if (dev.Activate(ref iid, 23, IntPtr.Zero, out o) == 0) {
          var mgr = (IAudioSessionManager2)o;
          IAudioSessionEnumerator se = null;
          try {
            if (mgr.GetSessionEnumerator(out se) == 0 && se != null) {
              int c;
              se.GetCount(out c);
              for (int k = 0; k < c; k++) {
                object s;
                if (se.GetSession(k, out s) != 0 || s == null) continue;
                string key = null;
                try {
                  var ctl = (IAudioSessionControl2)s;
                  int st;
                  ctl.GetState(out st);
                  uint pid;
                  ctl.GetProcessId(out pid);
                  if (st != 2) key = ctl.IsSystemSoundsSession() == 0 ? "#sistema" : ProcKey(pid);
                } catch { }
                if (key == null) { Rel(s); continue; }
                List<object> l;
                if (!groups.TryGetValue(key, out l)) groups[key] = l = new List<object>();
                l.Add(s);
              }
            }
          } finally { Rel(se); Rel(mgr); }
        }

        var apps = new List<MixerChannel>();
        foreach (var kv in groups) {
          var ch = Find(old, kv.Key) ?? new MixerChannel { Id = kv.Key };
          ch.Name = AppName(kv.Key);
          if (Fresh(ch))
            try {
              float v; bool m;
              var sv = (ISimpleAudioVolume)kv.Value[0];
              sv.GetMasterVolume(out v);
              sv.GetMute(out m);
              ch.Db = PosDb(v);
              ch.Muted = m;
            } catch { }
          apps.Add(ch);
        }
        apps.Sort((a, b) => a.Id == b.Id ? 0 : a.Id == "#sistema" ? 1 : b.Id == "#sistema" ? -1 : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        list.AddRange(apps);
        Channels = list;
      }

      void Apply() {
        Guid ctx = Guid.Empty;
        foreach (string id in new List<string>(volQueue.Keys)) {
          double pos;
          if (!volQueue.TryRemove(id, out pos)) continue;
          float v = (float)Math.Max(0, Math.Min(1, pos));
          if (id == "#geral") { if (ep != null) ep.SetMasterVolumeLevelScalar(v, ref ctx); continue; }
          List<object> l;
          if (groups.TryGetValue(id, out l))
            foreach (var s in l) try { ((ISimpleAudioVolume)s).SetMasterVolume(v, ref ctx); } catch { }
        }
        string mid;
        while (muteQueue.TryDequeue(out mid)) {
          var ch = Find(Channels, mid);
          if (ch == null) continue;
          bool m = ch.Muted;
          if (mid == "#geral") { if (ep != null) ep.SetMute(m, ref ctx); continue; }
          List<object> l;
          if (groups.TryGetValue(mid, out l))
            foreach (var s in l) try { ((ISimpleAudioVolume)s).SetMute(m, ref ctx); } catch { }
        }
        string outId = outQueue;
        if (outId != null) {
          outQueue = null;
          object pc = null;
          try {
            pc = new PolicyConfigCom();
            var p = (IPolicyConfig)pc;
            for (int role = 0; role < 3; role++) p.SetDefaultEndpoint(outId, role);
          } catch (Exception ex) {
            LastError = "nao consegui trocar a saida: " + ex.Message;
          } finally { Rel(pc); }
          dirty = true;
        }
      }

      void Meter() {
        float p;
        if (epMeter != null && epMeter.GetPeakValue(out p) == 0) Levels["#geral"] = p;
        foreach (var kv in groups) {
          float mx = 0;
          foreach (var s in kv.Value)
            try { if (((IAudioMeterInformation)s).GetPeakValue(out p) == 0) mx = Math.Max(mx, p); } catch { }
          Levels[kv.Key] = mx;
        }
      }
    }

    // ---- nomes ----
    static readonly int selfPid = Process.GetCurrentProcess().Id;

    static string ProcKey(uint pid) {
      if (pid == 0 || pid == selfPid) return null;
      try { using (var p = Process.GetProcessById((int)pid)) return p.ProcessName.ToLowerInvariant(); } catch { return null; }
    }

    static readonly Dictionary<string, string> Known = new Dictionary<string, string> {
      { "#sistema", "Sons do Windows" }, { "chrome", "Chrome" }, { "msedge", "Edge" }, { "firefox", "Firefox" },
      { "opera", "Opera GX" }, { "brave", "Brave" }, { "discord", "Discord" }, { "spotify", "Spotify" },
      { "obs64", "OBS" }, { "steam", "Steam" }, { "steamwebhelper", "Steam" }, { "vlc", "VLC" },
    };
    static readonly string[] Browsers = { "chrome", "msedge", "firefox", "opera", "brave" };
    static readonly Dictionary<string, string> nameCache = new Dictionary<string, string>();

    static string AppName(string key) {
      string name;
      if (!Known.TryGetValue(key, out name) && !nameCache.TryGetValue(key, out name)) {
        name = "";
        try {
          var ps = Process.GetProcessesByName(key);
          foreach (var p in ps) {
            try { if (name == "") name = (p.MainModule.FileVersionInfo.FileDescription ?? "").Trim(); } catch { }
            p.Dispose();
          }
        } catch { }
        if (name == "" || name.Length > 28) name = char.ToUpper(key[0]) + key.Substring(1);
        nameCache[key] = name;
      }
      if (Array.IndexOf(Browsers, key) >= 0 && WindowTitleHas(key, "YouTube")) name += " · YouTube";
      return name;
    }

    // alguma janela do programa com esse texto no titulo (aba do YouTube aberta na frente)
    static bool WindowTitleHas(string process, string text) {
      var pids = new HashSet<uint>();
      foreach (var p in Process.GetProcessesByName(process)) { pids.Add((uint)p.Id); p.Dispose(); }
      bool found = false;
      var sb = new StringBuilder(512);
      EnumWindows((h, l) => {
        if (!IsWindowVisible(h)) return true;
        uint pid;
        GetWindowThreadProcessId(h, out pid);
        if (!pids.Contains(pid)) return true;
        sb.Length = 0;
        GetWindowText(h, sb, sb.Capacity);
        if (sb.ToString().IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) { found = true; return false; }
        return true;
      }, IntPtr.Zero);
      return found;
    }

    // "Fones de ouvido" + "Realtek(R) Audio", "Alto-falantes" + "LG ULTRAGEAR"...
    static void Names(IMMDevice d, OutputDevice od) {
      IPropertyStore ps = null;
      try {
        if (d.OpenPropertyStore(0, out ps) != 0 || ps == null) return;
        od.Name = Prop(ps, "a45c254e-df1c-4efd-8020-67d146a850e0", 2);
        od.Sub = Prop(ps, "026e516e-b814-414b-83cd-856d6fef4822", 2);
        if (od.Name == "") od.Name = Prop(ps, "a45c254e-df1c-4efd-8020-67d146a850e0", 14);
        // som pelo cabo da placa de video = monitor/TV
        if (od.Sub.IndexOf("NVIDIA High Definition", StringComparison.OrdinalIgnoreCase) >= 0 ||
            od.Sub.IndexOf("AMD High Definition", StringComparison.OrdinalIgnoreCase) >= 0 ||
            od.Sub.IndexOf("Display Audio", StringComparison.OrdinalIgnoreCase) >= 0)
          od.Name = "Tela " + od.Name;
      } catch { } finally { Rel(ps); }
    }

    static string Prop(IPropertyStore ps, string fmt, int pid) {
      var k = new PropKey(fmt, pid);
      PropVariant v;
      if (ps.GetValue(ref k, out v) != 0) return "";
      try { return v.Vt == 31 ? (Marshal.PtrToStringUni(v.P) ?? "") : ""; } finally { PropVariantClear(ref v); }
    }
  }
}

}
