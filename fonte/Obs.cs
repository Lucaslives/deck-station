// ===========================================================================
//  OBS Studio pelo obs-websocket (vem junto no OBS 28 ou mais novo).
//  No OBS: Ferramentas > Configuracoes do servidor WebSocket > Ativar.
//  Porta e senha: botoes.txt (obs_porta / obs_senha) ou, se vazio, as do
//  proprio OBS (plugin_config\obs-websocket\config.json).
// ===========================================================================
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace StreamDeckCyd {

// um canal da mesa de som (fonte de audio do OBS)
class MixerChannel {
  public string Name = "";
  public string Id = "";     // mixer do PC: programa ("chrome", "#geral"...)
  public double Db;         // volume em dB (-100 = mudo total, 0 = cheio)
  public bool Muted;
  public DateTime Touched;   // mexido no painel: ignora o que o OBS manda por um instante
}

class ObsState {
  public string Scene = "";
  public List<string> Scenes = new List<string>();
  public bool Streaming, Recording, RecordPaused, VirtualCam, Replay;
  public Dictionary<string, bool> Muted = new Dictionary<string, bool>();    // fonte de audio -> mudo
  public Dictionary<string, bool> Visible = new Dictionary<string, bool>();  // fonte na cena atual -> visivel
  public bool FacecamVisible = true, HasFacecam;
  public string Mic = "", Desktop = "";
}

static class Obs {
  static ClientWebSocket ws;
  static readonly object sendLock = new object();
  static readonly ConcurrentDictionary<string, TaskCompletionSource<Dictionary<string, object>>> pending =
      new ConcurrentDictionary<string, TaskCompletionSource<Dictionary<string, object>>>();
  static readonly JavaScriptSerializer json = new JavaScriptSerializer();
  static int nextId;
  static DateTime lastTry = DateTime.MinValue;
  public static string Host = "127.0.0.1";
  public static int Port;          // 0 = do config do OBS
  public static string Password;   // null/"" = do config do OBS
  public static string LastError = "";
  public static volatile ObsState State;

  public static bool Connected { get { return ws != null && ws.State == WebSocketState.Open; } }

  static bool ObsRunning() {
    return Process.GetProcessesByName("obs64").Length > 0 || Process.GetProcessesByName("obs32").Length > 0 || Process.GetProcessesByName("obs").Length > 0;
  }

  // porta e senha que o OBS gravou no proprio config
  static void ObsConfig(out int port, out string pass, out bool enabled) {
    port = 4455;
    pass = "";
    enabled = true;
    try {
      string f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"obs-studio\plugin_config\obs-websocket\config.json");
      if (!File.Exists(f)) return;
      var d = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(f));
      if (d.ContainsKey("server_port")) port = Convert.ToInt32(d["server_port"]);
      if (d.ContainsKey("server_password") && d.ContainsKey("auth_required") && (bool)d["auth_required"]) pass = (string)d["server_password"];
      if (d.ContainsKey("server_enabled")) enabled = (bool)d["server_enabled"];
    } catch { }
  }

  public static List<string> SceneNamesOffline() {
    // lista das cenas pelos arquivos do OBS (para o editor, com o OBS fechado)
    var list = new List<string>();
    try {
      string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"obs-studio\basic\scenes");
      foreach (string f in Directory.GetFiles(dir, "*.json")) {
        var d = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(f, Encoding.UTF8));
        var order = d.ContainsKey("scene_order") ? d["scene_order"] as ArrayList : null;
        if (order == null) continue;
        foreach (Dictionary<string, object> s in order) {
          string n = (string)s["name"];
          if (!list.Contains(n)) list.Add(n);
        }
      }
    } catch { }
    return list;
  }

  public static List<string> SourceNamesOffline() {
    var list = new List<string>();
    try {
      string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"obs-studio\basic\scenes");
      foreach (string f in Directory.GetFiles(dir, "*.json")) {
        var d = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(f, Encoding.UTF8));
        var srcs = d.ContainsKey("sources") ? d["sources"] as ArrayList : null;
        if (srcs == null) continue;
        foreach (Dictionary<string, object> s in srcs) {
          if ((string)s["id"] == "scene") continue;
          string n = (string)s["name"];
          if (!list.Contains(n)) list.Add(n);
        }
      }
    } catch { }
    return list;
  }

  // conecta se o OBS estiver aberto (tenta no maximo a cada 4 s)
  public static bool Ensure() {
    if (Connected) return true;
    if ((DateTime.Now - lastTry).TotalSeconds < 4) return false;
    lastTry = DateTime.Now;
    if (!ObsRunning()) { LastError = "O OBS nao esta aberto"; State = null; return false; }
    int port;
    string pass;
    bool enabled;
    ObsConfig(out port, out pass, out enabled);
    if (Port > 0) port = Port;
    if (!string.IsNullOrEmpty(Password)) pass = Password;
    try {
      var c = new ClientWebSocket();
      if (!c.ConnectAsync(new Uri("ws://" + Host + ":" + port), CancellationToken.None).Wait(2000)) throw new Exception("tempo esgotado");
      // Hello -> Identify -> Identified
      var hello = ReceiveOne(c, 2000);
      var d = (Dictionary<string, object>)hello["d"];
      var ident = new Dictionary<string, object> { { "rpcVersion", 1 }, { "eventSubscriptions", Subscriptions } };
      if (d.ContainsKey("authentication")) {
        var auth = (Dictionary<string, object>)d["authentication"];
        string secret = Sha(pass + (string)auth["salt"]);
        ident["authentication"] = Sha(secret + (string)auth["challenge"]);
      }
      SendRaw(c, new Dictionary<string, object> { { "op", 1 }, { "d", ident } });
      var ok = ReceiveOne(c, 2000);
      if (Convert.ToInt32(ok["op"]) != 2) throw new Exception("senha recusada");
      ws = c;
      LastError = "";
      new Thread(() => ReceiveLoop(c)) { IsBackground = true }.Start();
      return true;
    } catch (Exception ex) {
      var inner = ex is AggregateException ? ex.InnerException : ex;
      LastError = inner.Message == "senha recusada" ? "Senha do OBS errada (botao OBS... no editor)" :
                  !enabled ? "Ative o servidor WebSocket no OBS (Ferramentas)" :
                  "Nao consegui conectar no OBS: " + inner.Message;
      ws = null;
      return false;
    }
  }

  static string Sha(string s) {
    using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(s)));
  }

  static void SendRaw(ClientWebSocket c, object msg) {
    var bytes = Encoding.UTF8.GetBytes(json.Serialize(msg));
    lock (sendLock) c.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).Wait(2000);
  }

  static Dictionary<string, object> ReceiveOne(ClientWebSocket c, int ms) {
    var buf = new byte[65536];
    var ms_ = new MemoryStream();
    while (true) {
      var t = c.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
      if (!t.Wait(ms)) throw new Exception("o OBS nao respondeu");
      if (t.Result.MessageType == WebSocketMessageType.Close) throw new Exception((int?)c.CloseStatus == 4009 ? "senha recusada" : "o OBS fechou a conexao");
      ms_.Write(buf, 0, t.Result.Count);
      if (t.Result.EndOfMessage) break;
    }
    return json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(ms_.ToArray()));
  }

  static void ReceiveLoop(ClientWebSocket c) {
    try {
      while (c.State == WebSocketState.Open) {
        var buf = new byte[65536];
        var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do {
          r = c.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).Result;
          if (r.MessageType == WebSocketMessageType.Close) throw new Exception("fechado");
          ms.Write(buf, 0, r.Count);
        } while (!r.EndOfMessage);
        var msg = json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(ms.ToArray()));
        if (Convert.ToInt32(msg["op"]) == 5) { try { OnEvent((Dictionary<string, object>)msg["d"]); } catch { } continue; }
        if (Convert.ToInt32(msg["op"]) != 7) continue;
        var d = (Dictionary<string, object>)msg["d"];
        TaskCompletionSource<Dictionary<string, object>> tcs;
        if (pending.TryRemove((string)d["requestId"], out tcs)) tcs.TrySetResult(d);
      }
    } catch { }
    if (ws == c) { ws = null; State = null; }
    foreach (var kv in pending) kv.Value.TrySetResult(null);
  }

  // pedido ao OBS; devolve responseData (ou erro com a mensagem do OBS)
  public static Dictionary<string, object> Call(string type, Dictionary<string, object> data = null) {
    if (!Ensure()) throw new Exception(LastError);
    string id = Interlocked.Increment(ref nextId).ToString();
    var tcs = new TaskCompletionSource<Dictionary<string, object>>();
    pending[id] = tcs;
    var req = new Dictionary<string, object> { { "requestType", type }, { "requestId", id } };
    if (data != null) req["requestData"] = data;
    SendRaw(ws, new Dictionary<string, object> { { "op", 6 }, { "d", req } });
    if (!tcs.Task.Wait(2000) || tcs.Task.Result == null) { pending.TryRemove(id, out tcs); throw new Exception("o OBS nao respondeu"); }
    var d = tcs.Task.Result;
    var st = (Dictionary<string, object>)d["requestStatus"];
    if (!(bool)st["result"]) throw new Exception("OBS: " + (st.ContainsKey("comment") ? st["comment"] : "codigo " + st["code"]));
    return d.ContainsKey("responseData") ? (Dictionary<string, object>)d["responseData"] : new Dictionary<string, object>();
  }

  static Dictionary<string, object> D(string k, object v) { return new Dictionary<string, object> { { k, v } }; }

  static string SpecialInput(string which) {
    var r = Call("GetSpecialInputs");
    var name = r.ContainsKey(which) ? r[which] as string : null;
    if (string.IsNullOrEmpty(name)) throw new Exception(which.StartsWith("mic") ? "o OBS nao tem microfone configurado" : "o OBS nao tem audio do desktop configurado");
    return name;
  }

  static ArrayList SceneItems(string scene) {
    return (ArrayList)Call("GetSceneItemList", D("sceneName", scene))["sceneItems"];
  }

  static bool IsCamera(Dictionary<string, object> item) {
    string kind = item.ContainsKey("inputKind") ? item["inputKind"] as string : null;
    return kind == "dshow_input" || kind == "av_capture_input" || kind == "v4l2_input";
  }

  static string CurrentScene() { return (string)Call("GetCurrentProgramScene")["currentProgramSceneName"]; }

  // obs:cena:Nome  obs:cena:+  obs:mic  obs:desktop  obs:mudo:Fonte  obs:facecam
  // obs:mostrar:Fonte  obs:live  obs:gravar  obs:pausar  obs:replay  obs:camvirtual  obs:estudio
  public static void Run(string arg) {
    int c = arg.IndexOf(':');
    string cmd = DeckConfig.Norm(c >= 0 ? arg.Substring(0, c) : arg);
    string val = c >= 0 ? arg.Substring(c + 1).Trim() : "";
    switch (cmd) {
      case "cena": {
        string target = val;
        if (val == "+" || val == "-") {
          var list = (ArrayList)Call("GetSceneList")["scenes"];
          var names = new List<string>();
          foreach (Dictionary<string, object> s in list) names.Add((string)s["sceneName"]);
          names.Reverse();  // o OBS devolve de baixo para cima
          int at = names.IndexOf(CurrentScene());
          target = names[((at < 0 ? 0 : at) + (val == "+" ? 1 : names.Count - 1)) % names.Count];
        }
        Call("SetCurrentProgramScene", D("sceneName", target));
        break;
      }
      case "mic": Call("ToggleInputMute", D("inputName", SpecialInput("mic1"))); break;
      case "desktop": Call("ToggleInputMute", D("inputName", SpecialInput("desktop1"))); break;
      case "mudo": Call("ToggleInputMute", D("inputName", val)); break;
      case "facecam": {
        string scene = CurrentScene();
        bool any = false;
        foreach (Dictionary<string, object> it in SceneItems(scene)) {
          if (!IsCamera(it)) continue;
          any = true;
          Call("SetSceneItemEnabled", new Dictionary<string, object> {
            { "sceneName", scene }, { "sceneItemId", it["sceneItemId"] }, { "sceneItemEnabled", !(bool)it["sceneItemEnabled"] } });
        }
        if (!any) throw new Exception("nao tem camera na cena " + scene);
        break;
      }
      case "mostrar": {
        string scene = CurrentScene();
        bool any = false;
        foreach (Dictionary<string, object> it in SceneItems(scene)) {
          if ((string)it["sourceName"] != val) continue;
          any = true;
          Call("SetSceneItemEnabled", new Dictionary<string, object> {
            { "sceneName", scene }, { "sceneItemId", it["sceneItemId"] }, { "sceneItemEnabled", !(bool)it["sceneItemEnabled"] } });
        }
        if (!any) throw new Exception("a fonte '" + val + "' nao esta na cena " + scene);
        break;
      }
      case "live": Call("ToggleStream"); break;
      case "gravar": Call("ToggleRecord"); break;
      case "pausar": Call("ToggleRecordPause"); break;
      case "replay": Call("SaveReplayBuffer"); break;
      case "camvirtual": Call("ToggleVirtualCam"); break;
      case "estudio": {
        bool on = (bool)Call("GetStudioModeEnabled")["studioModeEnabled"];
        Call("SetStudioModeEnabled", D("studioModeEnabled", !on));
        break;
      }
      case "mesa": return;  // a mesa de som abre no painel (App)
      default: throw new Exception("comando do OBS desconhecido: " + cmd);
    }
    Refresh(null);
  }

  // le o estado do OBS para colorir as teclas (so o que as teclas usam)
  public static void Refresh(ICollection<string> actions) {
    if (!Ensure()) return;
    var s = new ObsState();
    try {
      s.Scene = CurrentScene();
      foreach (Dictionary<string, object> sc in (ArrayList)Call("GetSceneList")["scenes"]) s.Scenes.Insert(0, (string)sc["sceneName"]);
      s.Streaming = (bool)Call("GetStreamStatus")["outputActive"];
      var rec = Call("GetRecordStatus");
      s.Recording = (bool)rec["outputActive"];
      s.RecordPaused = rec.ContainsKey("outputPaused") && (bool)rec["outputPaused"];
      try { s.VirtualCam = (bool)Call("GetVirtualCamStatus")["outputActive"]; } catch { }
      try { s.Replay = (bool)Call("GetReplayBufferStatus")["outputActive"]; } catch { }
      var sp = Call("GetSpecialInputs");
      s.Mic = sp["mic1"] as string ?? "";
      s.Desktop = sp["desktop1"] as string ?? "";
      var audio = new List<string>();
      if (s.Mic != "") audio.Add(s.Mic);
      if (s.Desktop != "") audio.Add(s.Desktop);
      if (actions != null)
        foreach (string a in actions)
          if (DeckConfig.Norm(a).StartsWith("obs:mudo:")) audio.Add(a.Substring(a.IndexOf(':', 4) + 1).Trim());
      foreach (string n in audio) {
        try { s.Muted[n] = (bool)Call("GetInputMute", D("inputName", n))["inputMuted"]; } catch { }
      }
      foreach (Dictionary<string, object> it in SceneItems(s.Scene)) {
        string n = (string)it["sourceName"];
        bool on = (bool)it["sceneItemEnabled"];
        s.Visible[n] = on;
        if (IsCamera(it)) { s.HasFacecam = true; s.FacecamVisible = on; }
      }
      State = s;
    } catch {
      // OBS fechou no meio
    }
  }

  // ---------------------------- mesa de som ---------------------------------
  // eventos: Inputs (8) sempre; medidores de nivel (65536) so com a mesa aberta
  static volatile bool meters;
  static int Subscriptions { get { return 8 | (meters ? 65536 : 0); } }
  public static volatile List<MixerChannel> Mixer = new List<MixerChannel>();
  // nivel atual de cada fonte (0..1, o maior entre os canais esquerdo/direito)
  public static readonly ConcurrentDictionary<string, float> Levels = new ConcurrentDictionary<string, float>();
  static readonly HashSet<string> noAudio = new HashSet<string>();
  static readonly ConcurrentDictionary<string, double> volQueue = new ConcurrentDictionary<string, double>();
  static readonly AutoResetEvent volWake = new AutoResetEvent(false);
  static Thread volThread;

  // liga/desliga os medidores (Reidentify troca as assinaturas sem reconectar)
  public static void Meters(bool on) {
    if (meters == on) return;
    meters = on;
    if (!on) Levels.Clear();
    var c = ws;
    if (c != null && c.State == WebSocketState.Open)
      try { SendRaw(c, new Dictionary<string, object> { { "op", 3 }, { "d", D("eventSubscriptions", Subscriptions) } }); } catch { }
  }

  static MixerChannel FindChannel(string name) {
    foreach (var ch in Mixer) if (ch.Name == name) return ch;
    return null;
  }

  static void OnEvent(Dictionary<string, object> d) {
    string type = (string)d["eventType"];
    var e = d.ContainsKey("eventData") ? (Dictionary<string, object>)d["eventData"] : null;
    if (e == null) return;
    if (type == "InputVolumeMeters") {
      foreach (Dictionary<string, object> inp in (ArrayList)e["inputs"]) {
        float peak = 0;
        foreach (ArrayList chn in (ArrayList)inp["inputLevelsMul"])
          if (chn.Count > 1) peak = Math.Max(peak, (float)Convert.ToDouble(chn[1]));
        Levels[(string)inp["inputName"]] = peak;
      }
    } else if (type == "InputVolumeChanged") {
      var ch = FindChannel((string)e["inputName"]);
      if (ch != null && (DateTime.Now - ch.Touched).TotalSeconds > 1) ch.Db = Convert.ToDouble(e["inputVolumeDb"]);
    } else if (type == "InputMuteStateChanged") {
      var ch = FindChannel((string)e["inputName"]);
      if (ch != null) ch.Muted = (bool)e["inputMuted"];
    } else if (type == "InputCreated" || type == "InputRemoved" || type == "InputNameChanged") {
      lastMixerList = DateTime.MinValue;
    }
  }

  static DateTime lastMixerList = DateTime.MinValue;

  // lista das fontes com audio e o volume/mudo de cada uma (microfone e desktop primeiro)
  public static volatile bool Demo;  // canais de exemplo (teste do desenho): nao le do OBS

  public static void MixerRefresh() {
    if (Demo) return;
    if (!Ensure()) { Mixer = new List<MixerChannel>(); return; }
    var old = Mixer;
    if ((DateTime.Now - lastMixerList).TotalSeconds < 5 && old.Count > 0) {
      foreach (var ch in old) ReadChannel(ch);
      return;
    }
    lastMixerList = DateTime.Now;
    var names = new List<string>();
    var sp = Call("GetSpecialInputs");
    foreach (string k in new[] { "mic1", "mic2", "mic3", "mic4", "desktop1", "desktop2" }) {
      string n = sp.ContainsKey(k) ? sp[k] as string : null;
      if (!string.IsNullOrEmpty(n) && !names.Contains(n)) names.Add(n);
    }
    foreach (Dictionary<string, object> inp in (ArrayList)Call("GetInputList")["inputs"]) {
      string n = (string)inp["inputName"];
      if (!names.Contains(n)) names.Add(n);
    }
    var list = new List<MixerChannel>();
    foreach (string n in names) {
      if (noAudio.Contains(n)) continue;
      var ch = FindChannel(n) ?? new MixerChannel { Name = n };
      // fonte sem audio (imagem, texto, captura de tela...) o OBS recusa: guarda para nao perguntar de novo
      try { ReadChannel(ch, true); list.Add(ch); } catch { noAudio.Add(n); }
    }
    Mixer = list;
  }

  static void ReadChannel(MixerChannel ch, bool strict = false) {
    try {
      var v = Call("GetInputVolume", D("inputName", ch.Name));
      if ((DateTime.Now - ch.Touched).TotalSeconds > 1) ch.Db = Convert.ToDouble(v["inputVolumeDb"]);
      ch.Muted = (bool)Call("GetInputMute", D("inputName", ch.Name))["inputMuted"];
    } catch { if (strict) throw; }
  }

  // o fader manda muitos valores: so o ultimo de cada fonte vai para o OBS
  public static void SetVolume(MixerChannel ch, double db) {
    ch.Db = db;
    ch.Touched = DateTime.Now;
    volQueue[ch.Name] = db;
    if (volThread == null) {
      volThread = new Thread(() => {
        while (true) {
          volWake.WaitOne();
          foreach (string n in new List<string>(volQueue.Keys)) {
            double v;
            if (!volQueue.TryRemove(n, out v)) continue;
            try { Call("SetInputVolume", new Dictionary<string, object> { { "inputName", n }, { "inputVolumeDb", Math.Max(-100, Math.Min(0, v)) } }); } catch { }
          }
          Thread.Sleep(40);
        }
      }) { IsBackground = true };
      volThread.Start();
    }
    volWake.Set();
  }

  public static void ToggleMute(MixerChannel ch) {
    ch.Muted = !ch.Muted;
    ThreadPool.QueueUserWorkItem(_ => {
      try { ch.Muted = (bool)Call("ToggleInputMute", D("inputName", ch.Name))["inputMuted"]; } catch { }
    });
  }

  // 0 normal, 1 ativo (borda verde), 2 desligado/mudo (vermelho riscado), 3 no ar (vermelho)
  public static int KeyState(string action) {
    var s = State;
    if (s == null) return 0;
    string a = action.Trim();
    if (!DeckConfig.Norm(a).StartsWith("obs:")) return 0;
    string arg = a.Substring(4);
    int c = arg.IndexOf(':');
    string cmd = DeckConfig.Norm(c >= 0 ? arg.Substring(0, c) : arg);
    string val = c >= 0 ? arg.Substring(c + 1).Trim() : "";
    bool m;
    switch (cmd) {
      case "cena": return val != "+" && val != "-" && val == s.Scene ? 1 : 0;
      case "mic": return s.Muted.TryGetValue(s.Mic, out m) && m ? 2 : 0;
      case "desktop": return s.Muted.TryGetValue(s.Desktop, out m) && m ? 2 : 0;
      case "mudo": return s.Muted.TryGetValue(val, out m) && m ? 2 : 0;
      case "facecam": return s.HasFacecam && !s.FacecamVisible ? 2 : 0;
      case "mostrar": return s.Visible.TryGetValue(val, out m) && !m ? 2 : 0;
      case "live": return s.Streaming ? 3 : 0;
      case "gravar": return s.Recording ? 3 : 0;
      case "pausar": return s.RecordPaused ? 1 : 0;
      case "camvirtual": return s.VirtualCam ? 1 : 0;
      case "replay": return s.Replay ? 1 : 0;
    }
    return 0;
  }
}

}
