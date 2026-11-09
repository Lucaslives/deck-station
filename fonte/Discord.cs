// ===========================================================================
//  Discord: mudo/ensurdecer de servidor em um membro, por um bot seu.
//  O bot precisa estar no servidor com a permissao "Silenciar membros"
//  (e "Ensurdecer membros" para ensurdecer). O token fica criptografado
//  para o seu usuario do Windows em discord-token.dat (nao vai no botoes.txt).
//
//  Acoes:  discord:mutar:Fulano       liga/desliga o mudo do servidor
//          discord:ensurdecer:Fulano  liga/desliga o ensurdecer
//  (o membro pode ser o nome de usuario, o apelido no servidor ou o ID)
// ===========================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace StreamDeckCyd {

static class Discord {
  const string Api = "https://discord.com/api/v10";
  static readonly JavaScriptSerializer json = new JavaScriptSerializer();
  static readonly object cacheLock = new object();
  // "usuario" -> (servidor, id do membro)
  static readonly Dictionary<string, string[]> members = new Dictionary<string, string[]>();
  static readonly Dictionary<string, bool[]> states = new Dictionary<string, bool[]>();  // id -> {mudo, surdo}
  static DateTime lastRefresh = DateTime.MinValue;
  public static string LastError = "";

  static string TokenFile { get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "discord-token.dat"); } }

  static Discord() {
    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
  }

  // ---- token (criptografado com a conta do Windows) ----
  public static bool HasToken { get { return File.Exists(TokenFile); } }

  public static void SaveToken(string token) {
    token = (token ?? "").Trim();
    if (token.StartsWith("Bot ", StringComparison.OrdinalIgnoreCase)) token = token.Substring(4).Trim();
    if (token == "") { if (File.Exists(TokenFile)) File.Delete(TokenFile); return; }
    byte[] enc = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
    File.WriteAllBytes(TokenFile, enc);
    lock (cacheLock) { members.Clear(); states.Clear(); }
  }

  static string Token() {
    if (!File.Exists(TokenFile)) throw new Exception("Configure o bot do Discord (botao Discord... no editor)");
    return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(TokenFile), null, DataProtectionScope.CurrentUser));
  }

  // ---- HTTP ----
  static object Call(string method, string path, object body) {
    var req = (HttpWebRequest)WebRequest.Create(Api + path);
    req.Method = method;
    req.Headers["Authorization"] = "Bot " + Token();
    req.UserAgent = "DiscordBot (streamdeck-cyd, 1.0)";
    req.Timeout = 8000;
    if (body != null) {
      byte[] data = Encoding.UTF8.GetBytes(json.Serialize(body));
      req.ContentType = "application/json";
      req.ContentLength = data.Length;
      using (var s = req.GetRequestStream()) s.Write(data, 0, data.Length);
    }
    try {
      using (var resp = (HttpWebResponse)req.GetResponse())
      using (var rd = new StreamReader(resp.GetResponseStream())) {
        string text = rd.ReadToEnd();
        return text.Length == 0 ? null : json.DeserializeObject(text);
      }
    } catch (WebException ex) {
      var r = ex.Response as HttpWebResponse;
      if (r == null) throw new Exception("sem internet / Discord fora do ar");
      string text;
      using (var rd = new StreamReader(r.GetResponseStream())) text = rd.ReadToEnd();
      int code = 0;
      try { code = Convert.ToInt32(((Dictionary<string, object>)json.DeserializeObject(text))["code"]); } catch { }
      if ((int)r.StatusCode == 401) throw new Exception("token do bot invalido");
      if (code == 40032) throw new Exception("ele nao esta em nenhuma call agora");
      if (code == 50013 || (int)r.StatusCode == 403) throw new Exception("o bot nao tem a permissao 'Silenciar membros' nesse servidor");
      if (code == 10007) throw new Exception("membro nao encontrado no servidor");
      throw new Exception("Discord respondeu " + (int)r.StatusCode + ": " + text);
    }
  }

  // servidores em que o bot esta
  public static List<string[]> Guilds() {
    var list = new List<string[]>();
    foreach (Dictionary<string, object> g in (object[])Call("GET", "/users/@me/guilds", null))
      list.Add(new[] { (string)g["id"], (string)g["name"] });
    return list;
  }

  static bool Matches(Dictionary<string, object> m, string who) {
    var u = (Dictionary<string, object>)m["user"];
    string w = who.ToLowerInvariant();
    foreach (string k in new[] { "username", "global_name" })
      if (u.ContainsKey(k) && u[k] is string && ((string)u[k]).ToLowerInvariant() == w) return true;
    return m.ContainsKey("nick") && m["nick"] is string && ((string)m["nick"]).ToLowerInvariant() == w;
  }

  // acha o membro (por nome ou ID) nos servidores do bot
  static string[] Find(string who) {
    who = who.Trim().TrimStart('@');
    lock (cacheLock) { if (members.ContainsKey(who)) return members[who]; }
    bool isId = who.Length >= 15 && who.Trim('0', '1', '2', '3', '4', '5', '6', '7', '8', '9').Length == 0;
    foreach (var g in Guilds()) {
      string[] found = null;
      if (isId) {
        try { Call("GET", "/guilds/" + g[0] + "/members/" + who, null); found = new[] { g[0], who }; } catch { }
      } else {
        var res = (object[])Call("GET", "/guilds/" + g[0] + "/members/search?limit=10&query=" + Uri.EscapeDataString(who), null);
        foreach (Dictionary<string, object> m in res)
          if (Matches(m, who)) { found = new[] { g[0], (string)((Dictionary<string, object>)m["user"])["id"] }; break; }
      }
      if (found != null) {
        lock (cacheLock) members[who] = found;
        return found;
      }
    }
    throw new Exception("nao achei '" + who + "' nos servidores do bot");
  }

  static bool[] ReadState(string[] gm) {
    var m = (Dictionary<string, object>)Call("GET", "/guilds/" + gm[0] + "/members/" + gm[1], null);
    var st = new[] { m.ContainsKey("mute") && (bool)m["mute"], m.ContainsKey("deaf") && (bool)m["deaf"] };
    lock (cacheLock) states[gm[1]] = st;
    return st;
  }

  // discord:mutar:fulano / discord:ensurdecer:fulano  (":ligar" / ":desligar" no fim forca o estado)
  public static void Run(string arg) {
    string[] p = arg.Split(':');
    string cmd = DeckConfig.Norm(p[0]);
    if (cmd == "meumic" || cmd == "meusom") { Own(cmd); return; }
    if (p.Length < 2 || p[1].Trim() == "") throw new Exception("falta o membro: discord:" + cmd + ":nome");
    string force = p.Length > 2 ? DeckConfig.Norm(p[2]) : "";
    string field = cmd == "ensurdecer" ? "deaf" : cmd == "mutar" ? "mute" : null;
    if (field == null) throw new Exception("acao do Discord desconhecida: " + cmd);
    var gm = Find(p[1]);
    bool[] st = ReadState(gm);
    bool cur = field == "mute" ? st[0] : st[1];
    bool want = force == "ligar" ? true : force == "desligar" ? false : !cur;
    Call("PATCH", "/guilds/" + gm[0] + "/members/" + gm[1], new Dictionary<string, object> { { field, want } });
    lock (cacheLock) {
      if (field == "mute") st[0] = want; else st[1] = want;
      states[gm[1]] = st;
    }
  }

  // atualiza o estado dos membros das teclas (no maximo a cada 4 s)
  public static void Refresh(ICollection<string> actions) {
    foreach (string a in actions) {
      string n = DeckConfig.Norm(a);
      if (n == "discord:meumic" || n == "discord:meusom") { DiscordRpc.Refresh(); break; }
    }
    if (!HasToken || (DateTime.Now - lastRefresh).TotalSeconds < 4) return;
    lastRefresh = DateTime.Now;
    foreach (string a in actions) {
      string[] p = a.Substring(a.IndexOf(':') + 1).Split(':');
      if (p.Length < 2) continue;
      try { ReadState(Find(p[1])); LastError = ""; } catch (Exception ex) { LastError = ex.Message; }
    }
  }

  // ---- o seu proprio Discord ----
  // discord:meumic  mudo do seu microfone so no Discord (a live continua com voz)
  // discord:meusom  ensurdecer no Discord
  // Sem configurar nada: aperta o atalho do proprio Discord (Ctrl+Shift+M mudo,
  // Ctrl+Shift+D ensurdecer) direto na janela dele, sem trazer para a frente:
  // funciona minimizado, nao tira o foco do jogo e nao mexe no microfone da live.
  // (Mutar o audio do Discord pelo Windows nao serve: no microfone o Windows
  // muta o dispositivo inteiro, e a live ficava muda junto.)
  // Com o Discord conectado no editor (DiscordRpc) usa a conexao local.
  static volatile bool micOff, deafOff;  // o Discord nao conta o estado: guardamos o que apertamos

  static void Own(string cmd) {
    if (Process.GetProcessesByName("Discord").Length == 0) throw new Exception("o Discord nao esta aberto");
    if (DiscordRpc.Configured) { DiscordRpc.Toggle(cmd == "meusom"); return; }
    IntPtr wnd = MainWindow();
    if (wnd == IntPtr.Zero) throw new Exception("nao achei a janela do Discord");
    PostHotkey(wnd, cmd == "meumic" ? 'M' : 'D', cmd == "meumic" ? 0x32 : 0x20);
    if (cmd == "meumic") {
      // desligar o ensurdecer pelo mudo tambem liga o som (igual ao Discord)
      if (deafOff) { deafOff = false; micOff = false; } else micOff = !micOff;
    } else {
      deafOff = !deafOff;
    }
  }

  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc f, IntPtr l);
  delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
  [DllImport("user32.dll")] static extern bool GetKeyboardState(byte[] s);
  [DllImport("user32.dll")] static extern bool SetKeyboardState(byte[] s);
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

  // janela principal do Discord (a da lista de canais; titulo termina em "Discord")
  static IntPtr MainWindow() {
    var pids = new HashSet<uint>();
    foreach (var p in Process.GetProcessesByName("Discord")) { pids.Add((uint)p.Id); p.Dispose(); }
    IntPtr found = IntPtr.Zero;
    EnumWindows((h, l) => {
      uint pid;
      GetWindowThreadProcessId(h, out pid);
      if (!pids.Contains(pid) || !IsWindowVisible(h)) return true;
      var cls = new StringBuilder(64);
      var title = new StringBuilder(256);
      GetClassName(h, cls, 64);
      GetWindowText(h, title, 256);
      if (cls.ToString() == "Chrome_WidgetWin_1" && title.ToString().EndsWith("Discord")) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }

  // Ctrl+Shift+tecla so para a janela do Discord: as teclas Ctrl e Shift ficam
  // "apertadas" so no estado de teclado compartilhado com a thread do Discord
  static void PostHotkey(IntPtr wnd, char key, int scan) {
    uint pid;
    uint tid = GetWindowThreadProcessId(wnd, out pid), me = GetCurrentThreadId();
    bool attached = AttachThreadInput(me, tid, true);
    var old = new byte[256];
    GetKeyboardState(old);
    try {
      var st = (byte[])old.Clone();
      foreach (int vk in new[] { 0x11, 0xA2, 0x10, 0xA0 }) st[vk] = 0x80;  // Ctrl, Ctrl esq., Shift, Shift esq.
      SetKeyboardState(st);
      long down = 1 | ((long)scan << 16), up = down | (1L << 30) | (1L << 31);
      PostMessage(wnd, 0x0100, (IntPtr)key, (IntPtr)down);  // WM_KEYDOWN
      PostMessage(wnd, 0x0101, (IntPtr)key, (IntPtr)up);    // WM_KEYUP
      Thread.Sleep(150);  // o Discord le o Ctrl/Shift na hora de tratar a tecla
    } finally {
      SetKeyboardState(old);
      if (attached) AttachThreadInput(me, tid, false);
    }
  }

  // 2 (vermelho riscado) quando o membro esta mudo/surdo no servidor
  public static int KeyState(string action) {
    string own = DeckConfig.Norm(action);
    if (own == "discord:meumic" || own == "discord:meusom") {
      if (DiscordRpc.Configured) {
        if (!DiscordRpc.HaveState) return 0;
        return (own == "discord:meusom" ? DiscordRpc.Deafened : DiscordRpc.Muted || DiscordRpc.Deafened) ? 2 : 0;
      }
      return (own == "discord:meusom" ? deafOff : micOff || deafOff) ? 2 : 0;
    }
    string[] p = action.Substring(action.IndexOf(':') + 1).Split(':');
    if (p.Length < 2) return 0;
    string who = p[1].Trim().TrimStart('@');
    string[] gm;
    bool[] st;
    lock (cacheLock) {
      if (!members.TryGetValue(who, out gm) || !states.TryGetValue(gm[1], out st)) return 0;
    }
    bool on = DeckConfig.Norm(p[0]) == "ensurdecer" ? st[1] : st[0];
    return on ? 2 : 0;
  }
}

}
