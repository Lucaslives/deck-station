// ===========================================================================
//  O seu proprio Discord pela conexao local (RPC): liga/desliga o mudo e o
//  ensurdecer de verdade (o icone acende no Discord), com o Discord
//  minimizado e com jogo na frente, sem mexer no microfone da live.
//
//  Precisa de um aplicativo seu no Portal de Desenvolvedores (Client ID e
//  Client Secret, com o redirect http://localhost). Na primeira vez o Discord
//  pergunta se autoriza; o token fica criptografado em discord-rpc.dat.
//
//  Acoes:  discord:meumic   mudo do seu microfone no Discord
//          discord:meusom   ensurdecer no Discord
// ===========================================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace StreamDeckCyd {

static class DiscordRpc {
  static readonly JavaScriptSerializer json = new JavaScriptSerializer();
  static readonly object connLock = new object(), writeLock = new object();
  static readonly ConcurrentDictionary<string, TaskCompletionSource<Dictionary<string, object>>> pending =
      new ConcurrentDictionary<string, TaskCompletionSource<Dictionary<string, object>>>();
  static NamedPipeClientStream pipe;
  static volatile bool ready;
  static DateTime lastTry = DateTime.MinValue;
  public static string LastError = "";
  // estado atual (atualizado pelo proprio Discord)
  public static volatile bool Muted, Deafened, HaveState;

  static string File_ { get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "discord-rpc.dat"); } }

  // ---- credenciais (criptografadas com a conta do Windows) ----
  class Creds { public string ClientId = "", Secret = "", Access = "", Refresh = ""; }

  static Creds Load() {
    try {
      if (!File.Exists(File_)) return null;
      var d = json.Deserialize<Dictionary<string, string>>(Encoding.UTF8.GetString(
          ProtectedData.Unprotect(File.ReadAllBytes(File_), null, DataProtectionScope.CurrentUser)));
      return new Creds { ClientId = d["id"], Secret = d["secret"], Access = d["access"], Refresh = d["refresh"] };
    } catch { return null; }
  }

  static void Store(Creds c) {
    var d = new Dictionary<string, string> { { "id", c.ClientId }, { "secret", c.Secret }, { "access", c.Access }, { "refresh", c.Refresh } };
    File.WriteAllBytes(File_, ProtectedData.Protect(Encoding.UTF8.GetBytes(json.Serialize(d)), null, DataProtectionScope.CurrentUser));
  }

  public static bool Configured { get { var c = Load(); return c != null && c.ClientId != ""; } }
  public static string ClientId { get { var c = Load(); return c != null ? c.ClientId : ""; } }

  // ---- protocolo: [opcode int32][tamanho int32][json] ----
  static void Write(int op, object payload) {
    byte[] body = Encoding.UTF8.GetBytes(json.Serialize(payload));
    var buf = new byte[8 + body.Length];
    BitConverter.GetBytes(op).CopyTo(buf, 0);
    BitConverter.GetBytes(body.Length).CopyTo(buf, 4);
    body.CopyTo(buf, 8);
    lock (writeLock) { pipe.Write(buf, 0, buf.Length); pipe.Flush(); }
  }

  static void ReadExact(Stream s, byte[] buf, int n) {
    int got = 0;
    while (got < n) {
      int r = s.Read(buf, got, n - got);
      if (r <= 0) throw new IOException("o Discord fechou a conexao");
      got += r;
    }
  }

  static Dictionary<string, object> ReadFrame(Stream s, out int op) {
    var head = new byte[8];
    ReadExact(s, head, 8);
    op = BitConverter.ToInt32(head, 0);
    int len = BitConverter.ToInt32(head, 4);
    var body = new byte[len];
    ReadExact(s, body, len);
    return json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(body));
  }

  static void ReadLoop(NamedPipeClientStream p) {
    try {
      while (p.IsConnected) {
        int op;
        var msg = ReadFrame(p, out op);
        if (op == 2) break;  // CLOSE
        if (op == 3) { Write(4, msg); continue; }  // PING -> PONG
        object nonce;
        if (msg.TryGetValue("nonce", out nonce) && nonce is string) {
          TaskCompletionSource<Dictionary<string, object>> tcs;
          if (pending.TryRemove((string)nonce, out tcs)) tcs.TrySetResult(msg);
        }
        if (msg.ContainsKey("evt") && (msg["evt"] as string) == "VOICE_SETTINGS_UPDATE") ReadState(msg);
      }
    } catch { }
    lock (connLock) {
      if (pipe == p) { pipe = null; ready = false; HaveState = false; }
    }
    foreach (var kv in pending) kv.Value.TrySetResult(null);
    try { p.Dispose(); } catch { }
  }

  static Dictionary<string, object> Cmd(string cmd, Dictionary<string, object> args, int ms = 3000, string evt = null) {
    string nonce = Guid.NewGuid().ToString();
    var tcs = new TaskCompletionSource<Dictionary<string, object>>();
    pending[nonce] = tcs;
    var m = new Dictionary<string, object> { { "cmd", cmd }, { "args", args ?? new Dictionary<string, object>() }, { "nonce", nonce } };
    if (evt != null) m["evt"] = evt;
    Write(1, m);
    if (!tcs.Task.Wait(ms) || tcs.Task.Result == null) {
      pending.TryRemove(nonce, out tcs);
      throw new Exception(cmd == "AUTHORIZE" ? "a autorizacao nao foi aceita no Discord" : "o Discord nao respondeu");
    }
    var r = tcs.Task.Result;
    object evtObj;
    if (r.TryGetValue("evt", out evtObj) && (evtObj as string) == "ERROR") {
      var d = (Dictionary<string, object>)r["data"];
      throw new Exception("Discord: " + d["message"]);
    }
    return r.ContainsKey("data") ? r["data"] as Dictionary<string, object> : null;
  }

  static void ReadState(Dictionary<string, object> msg) {
    var d = msg["data"] as Dictionary<string, object>;
    if (d == null) return;
    if (d.ContainsKey("mute")) Muted = (bool)d["mute"];
    if (d.ContainsKey("deaf")) Deafened = (bool)d["deaf"];
    HaveState = true;
  }

  // ---- token OAuth ----
  static Dictionary<string, object> Token(Creds c, string grant, string value) {
    // o codigo do RPC as vezes exige o redirect cadastrado, as vezes recusa: tenta sem e com
    foreach (string redirect in new[] { null, "http://localhost" }) {
      var form = "client_id=" + Uri.EscapeDataString(c.ClientId) + "&client_secret=" + Uri.EscapeDataString(c.Secret) +
                 "&grant_type=" + grant + (grant == "refresh_token" ? "&refresh_token=" : "&code=") + Uri.EscapeDataString(value) +
                 (redirect != null ? "&redirect_uri=" + Uri.EscapeDataString(redirect) : "");
      var req = (HttpWebRequest)WebRequest.Create("https://discord.com/api/oauth2/token");
      req.Method = "POST";
      req.ContentType = "application/x-www-form-urlencoded";
      req.Timeout = 8000;
      byte[] data = Encoding.UTF8.GetBytes(form);
      using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
      try {
        using (var resp = req.GetResponse())
        using (var rd = new StreamReader(resp.GetResponseStream()))
          return json.Deserialize<Dictionary<string, object>>(rd.ReadToEnd());
      } catch (WebException ex) {
        string text = "";
        if (ex.Response != null) using (var rd = new StreamReader(ex.Response.GetResponseStream())) text = rd.ReadToEnd();
        if (redirect == null && text.Contains("redirect")) continue;
        if (text.Contains("invalid_client")) throw new Exception("Client ID ou Client Secret errado");
        throw new Exception("o Discord recusou o login" + (text != "" ? ": " + text : ""));
      }
    }
    throw new Exception("cadastre o redirect http://localhost no aplicativo (aba OAuth2)");
  }

  static bool Authenticate(Creds c) {
    if (c.Access == "") return false;
    try { Cmd("AUTHENTICATE", new Dictionary<string, object> { { "access_token", c.Access } }); return true; } catch { }
    if (c.Refresh == "") return false;
    try {
      var t = Token(c, "refresh_token", c.Refresh);
      c.Access = (string)t["access_token"];
      c.Refresh = (string)t["refresh_token"];
      Store(c);
      Cmd("AUTHENTICATE", new Dictionary<string, object> { { "access_token", c.Access } });
      return true;
    } catch { return false; }
  }

  // conecta no Discord aberto; interactive = pode pedir a autorizacao (janela no Discord)
  static bool Ensure(bool interactive) {
    lock (connLock) {
      if (ready && pipe != null && pipe.IsConnected) return true;
      if (!interactive && (DateTime.Now - lastTry).TotalSeconds < 5) return false;
      lastTry = DateTime.Now;
      var c = Load();
      if (c == null || c.ClientId == "") { LastError = "Configure o Discord no editor (botao Discord...)"; return false; }
      NamedPipeClientStream p = null;
      for (int i = 0; i < 10 && p == null; i++) {
        var t = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.None);
        try { t.Connect(300); p = t; } catch { t.Dispose(); }
      }
      if (p == null) { LastError = "o Discord nao esta aberto"; return false; }
      try {
        pipe = p;
        Write(0, new Dictionary<string, object> { { "v", 1 }, { "client_id", c.ClientId } });
        int op;
        var hello = ReadFrame(p, out op);
        if (op == 2 || (hello.ContainsKey("evt") && (hello["evt"] as string) != "READY"))
          throw new Exception(hello.ContainsKey("message") ? "Discord: " + hello["message"] : "o Discord recusou o Client ID");
        new Thread(() => ReadLoop(p)) { IsBackground = true }.Start();
        if (!Authenticate(c)) {
          if (!interactive) throw new Exception("autorize de novo no editor (botao Discord...)");
          // janela de autorizacao dentro do Discord (o usuario tem 60 s para aceitar)
          var r = Cmd("AUTHORIZE", new Dictionary<string, object> {
            { "client_id", c.ClientId }, { "scopes", new[] { "rpc", "rpc.voice.read", "rpc.voice.write" } } }, 60000);
          var t = Token(c, "authorization_code", (string)r["code"]);
          c.Access = (string)t["access_token"];
          c.Refresh = (string)t["refresh_token"];
          Store(c);
          Cmd("AUTHENTICATE", new Dictionary<string, object> { { "access_token", c.Access } });
        }
        var vs = Cmd("GET_VOICE_SETTINGS", null);
        Muted = (bool)vs["mute"];
        Deafened = (bool)vs["deaf"];
        HaveState = true;
        try { Cmd("SUBSCRIBE", null, 3000, "VOICE_SETTINGS_UPDATE"); } catch { }
        ready = true;
        LastError = "";
        return true;
      } catch (Exception ex) {
        LastError = ex.Message;
        try { p.Dispose(); } catch { }
        pipe = null;
        ready = false;
        return false;
      }
    }
  }

  // ---- usados pelo editor ----
  public static void Setup(string clientId, string secret) {
    var old = Load();
    var c = new Creds { ClientId = clientId.Trim(), Secret = secret.Trim() };
    if (c.Secret == "" && old != null && old.ClientId == c.ClientId) c.Secret = old.Secret;
    if (c.ClientId == "" || c.Secret == "") throw new Exception("preencha o Client ID e o Client Secret");
    Store(c);
    Disconnect();
    if (!Ensure(true)) throw new Exception(LastError);
  }

  public static void Disconnect() {
    lock (connLock) {
      ready = false;
      if (pipe != null) try { pipe.Dispose(); } catch { }
      pipe = null;
    }
  }

  // ---- teclas ----
  public static void Toggle(bool deaf) {
    if (!Ensure(false)) throw new Exception(LastError);
    var vs = Cmd("GET_VOICE_SETTINGS", null);
    bool cur = (bool)vs[deaf ? "deaf" : "mute"];
    var r = Cmd("SET_VOICE_SETTINGS", new Dictionary<string, object> { { deaf ? "deaf" : "mute", !cur } });
    if (r != null) {
      if (r.ContainsKey("mute")) Muted = (bool)r["mute"];
      if (r.ContainsKey("deaf")) Deafened = (bool)r["deaf"];
    }
  }

  // chamado a cada segundo pelas teclas: reconecta se o Discord abriu depois
  public static void Refresh() {
    if (Configured) Ensure(false);
  }
}

}
