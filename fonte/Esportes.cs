// ===========================================================================
//  Dados dos esportes para a tela Esportes do painel: classificacao, jogos
//  com placar (ao vivo, proximos, resultados) e lideres de estatisticas.
//  Fontes: API publica da ESPN (Brasileirao e NBA) e a pagina de partidas da
//  Liquipedia pela API dela (CS, LoL, Valorant). Escudos ficam em logos\.
// ===========================================================================
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace StreamDeckCyd {

class SportTeam { public string Name = "", Abbr = "", Logo = ""; }

class StandRow {
  public int Rank;
  public SportTeam Team;
  public string[] Cells;
  public Color Zone = Color.Empty;   // faixa colorida (Libertadores, rebaixamento, playoff...)
}

class StandTable {
  public string Title = "";
  public string[] Headers;
  public List<StandRow> Rows = new List<StandRow>();
}

class SportGame {
  public string Id = "";
  public DateTime When;
  public string State = "", Detail = "";   // state: pre, in, post
  public SportTeam Home, Away;
  public string HomeScore = "", AwayScore = "";
  public string Tour = "";   // campeonato (e-sports)
}

class LeaderList {
  public string Title = "";
  public List<string[]> Rows = new List<string[]>();   // nome, time, valor
}

class SportData {
  public List<StandTable> Tables = new List<StandTable>();
  public List<SportGame> Games = new List<SportGame>();
  public List<LeaderList> Leaders = new List<LeaderList>();
  public string Error = "";
  public DateTime Updated = DateTime.MinValue;
  public bool HasLive { get { foreach (var g in Games) if (g.State == "in") return true; return false; } }
}

static class Sports {
  public class Def {
    public string Id, Name, Path, Wiki;
    public bool Soccer;
  }
  public static readonly Def[] All = {
    new Def { Id = "bra", Name = "Brasileirão", Path = "soccer/bra.1", Soccer = true },
    new Def { Id = "nba", Name = "NBA", Path = "basketball/nba" },
    new Def { Id = "cs", Name = "Counter-Strike", Wiki = "counterstrike" },
    new Def { Id = "lol", Name = "LoL", Wiki = "leagueoflegends" },
    new Def { Id = "val", Name = "Valorant", Wiki = "valorant" },
  };

  public static readonly ConcurrentDictionary<string, SportData> Data = new ConcurrentDictionary<string, SportData>();
  public static event Action Changed;   // chegou dado novo ou escudo novo: redesenhar

  static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
  static readonly string LogoDir = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "logos");
  const string Api = "https://site.api.espn.com/apis/";

  static void Notify() { var h = Changed; if (h != null) try { h(); } catch { } }

  static object Get(string url) {
    ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;  // TLS 1.2
    using (var wc = new WebClient { Encoding = Encoding.UTF8 }) {
      wc.Headers[HttpRequestHeader.UserAgent] = "DeckStation/2.5";
      return json.DeserializeObject(wc.DownloadString(url));
    }
  }

  // caminho dentro do JSON: P(o, "children", 0, "name")
  static object P(object o, params object[] path) {
    foreach (var k in path) {
      if (o == null) return null;
      if (k is int) {
        var l = o as IList;
        int i = (int)k;
        o = l != null && i < l.Count ? l[i] : null;
      } else {
        var d = o as IDictionary<string, object>;
        object v = null;
        if (d == null || !d.TryGetValue((string)k, out v)) return null;
        o = v;
      }
    }
    return o;
  }
  static string S(object o, params object[] path) { var v = P(o, path); return v == null ? "" : Convert.ToString(v, CultureInfo.InvariantCulture); }
  static IList L(object o, params object[] path) { return P(o, path) as IList ?? new object[0]; }

  // ---- atualizacao (chamada fora da thread da janela) ----
  public static bool Stale(string id) {
    SportData d;
    if (!Data.TryGetValue(id, out d)) return true;
    double age = (DateTime.Now - d.Updated).TotalSeconds;
    var def = Array.Find(All, x => x.Id == id);
    if (def != null && def.Wiki != null) return age > (d.HasLive ? 120 : d.Error != "" ? 90 : 300);
    return age > (d.HasLive ? 40 : d.Error != "" ? 60 : 300);
  }

  public static void Refresh(string id) {
    Def def = Array.Find(All, x => x.Id == id);
    if (def == null) return;
    var d = new SportData();
    SportData old;
    Data.TryGetValue(id, out old);
    if (def.Wiki != null) {
      try { d.Games = Liquipedia.Matches(def.Wiki); }
      catch (Exception ex) {
        if (old != null) d = old;
        d.Error = "Sem conexão com a Liquipedia: " + ex.Message;
      }
      d.Updated = DateTime.Now;
      Data[id] = d;
      Notify();
      return;
    }
    if (def.Path == null) return;
    try {
      d.Tables = Standings(def);
      d.Games = Games(def, old);
      try { d.Leaders = Leaders(def); } catch { if (old != null) d.Leaders = old.Leaders; }
    } catch (Exception ex) {
      if (old != null) { d = old; }
      d.Error = "Sem conexão com a ESPN: " + ex.Message;
    }
    d.Updated = DateTime.Now;
    Data[id] = d;
    Notify();
  }

  static List<StandTable> Standings(Def def) {
    var root = Get(Api + "v2/sports/" + def.Path + "/standings");
    var tables = new List<StandTable>();
    foreach (var child in L(root, "children")) {
      var t = new StandTable();
      string name = S(child, "name");
      t.Title = name.StartsWith("Eastern") ? "Conferência Leste" : name.StartsWith("Western") ? "Conferência Oeste" : "";
      t.Headers = def.Soccer ? new[] { "P", "J", "V", "E", "D", "SG" } : new[] { "V", "D", "%", "JA", "Seq" };
      foreach (var e in L(child, "standings", "entries")) {
        var st = new Dictionary<string, string>();
        foreach (var s in L(e, "stats")) st[S(s, "name")] = S(s, "displayValue");
        Func<string, string> v = k => st.ContainsKey(k) ? st[k] : "";
        var row = new StandRow { Team = Team(P(e, "team")) };
        int rank;
        int.TryParse(def.Soccer ? v("rank") : v("playoffSeed"), out rank);
        row.Rank = rank;
        row.Cells = def.Soccer ? new[] { v("points"), v("gamesPlayed"), v("wins"), v("ties"), v("losses"), v("pointDifferential") }
                               : new[] { v("wins"), v("losses"), v("winPercent"), v("gamesBehind"), v("streak") };
        if (def.Soccer)
          row.Zone = rank <= 4 ? Color.FromArgb(40, 120, 255) : rank <= 6 ? Color.FromArgb(70, 200, 240) :
                     rank <= 12 ? Color.FromArgb(40, 190, 90) : rank >= 17 ? Color.FromArgb(230, 50, 50) : Color.Empty;
        else
          row.Zone = rank <= 6 ? Color.FromArgb(40, 190, 90) : rank <= 10 ? Color.FromArgb(240, 190, 40) : Color.Empty;
        t.Rows.Add(row);
      }
      t.Rows.Sort((a, b) => a.Rank.CompareTo(b.Rank));
      tables.Add(t);
    }
    return tables;
  }

  // jogos de 3 dias atras ate 6 dias pra frente (com jogo ao vivo so relê hoje e ontem)
  static List<SportGame> Games(Def def, SportData old) {
    var byId = new Dictionary<string, SportGame>();
    bool quick = old != null && old.HasLive && (DateTime.Now - old.Updated).TotalMinutes < 5 && old.Games.Count > 0;
    if (quick) foreach (var g in old.Games) byId[g.Id] = g;
    int from = quick ? -1 : -3, to = quick ? 0 : 6;
    for (int k = from; k <= to; k++) {
      string day = DateTime.Today.AddDays(k).ToString("yyyyMMdd");
      var root = Get(Api + "site/v2/sports/" + def.Path + "/scoreboard?dates=" + day);
      foreach (var ev in L(root, "events")) {
        var g = new SportGame { Id = S(ev, "id") };
        DateTime when;
        if (DateTime.TryParse(S(ev, "date"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out when))
          g.When = when.ToLocalTime();
        g.State = S(ev, "status", "type", "state");
        g.Detail = S(ev, "status", "type", "shortDetail");
        foreach (var c in L(ev, "competitions", 0, "competitors")) {
          var t = Team(P(c, "team"));
          if (S(c, "homeAway") == "home") { g.Home = t; g.HomeScore = S(c, "score"); }
          else { g.Away = t; g.AwayScore = S(c, "score"); }
        }
        if (g.Home != null && g.Away != null) byId[g.Id] = g;
      }
    }
    return new List<SportGame>(byId.Values);
  }

  static readonly Dictionary<string, string> LeaderNames = new Dictionary<string, string> {
    { "goalsLeaders", "Artilheiros" }, { "assistsLeaders", "Assistências" },
    { "pointsPerGame", "Pontos por jogo" }, { "reboundsPerGame", "Rebotes por jogo" }, { "assistsPerGame", "Assistências por jogo" },
    { "points", "Pontos" }, { "rebounds", "Rebotes" }, { "assists", "Assistências" },
  };

  static List<LeaderList> Leaders(Def def) {
    var root = Get(Api + "site/v2/sports/" + def.Path + "/statistics");
    var cats = L(root, "stats");
    bool pick = false;
    // NBA: os lideres vem de outro endereco; so pontos, rebotes e assistencias por jogo
    if (cats.Count == 0) {
      cats = L(Get(Api + "site/v3/sports/" + def.Path + "/leaders"), "leaders", "categories");
      pick = true;
    }
    var lists = new List<LeaderList>();
    foreach (var name0 in pick ? new[] { "pointsPerGame", "reboundsPerGame", "assistsPerGame" } : new string[] { null })
    foreach (var st in cats) {
      string name = S(st, "name");
      if (name0 != null && name != name0) continue;
      var ll = new LeaderList { Title = LeaderNames.ContainsKey(name) ? LeaderNames[name] : S(st, "displayName") };
      foreach (var ld in L(st, "leaders")) {
        if (ll.Rows.Count >= 10) break;
        double val;
        string vs = double.TryParse(S(ld, "value"), NumberStyles.Float, CultureInfo.InvariantCulture, out val)
          ? (val == Math.Floor(val) ? val.ToString("0") : val.ToString("0.0")) : S(ld, "displayValue");
        string team = S(ld, "athlete", "team", "abbreviation");
        if (team == "") team = S(ld, "team", "abbreviation");
        ll.Rows.Add(new[] { S(ld, "athlete", "displayName"), team, vs });
      }
      if (ll.Rows.Count > 0) lists.Add(ll);
      if (lists.Count >= 3) break;
    }
    return lists;
  }

  static SportTeam Team(object t) {
    var team = new SportTeam {
      Name = S(t, "shortDisplayName"),
      Abbr = S(t, "abbreviation"),
      Logo = S(t, "logo"),
    };
    string full = S(t, "displayName");
    if (team.Name == "" || full.Length <= 18) team.Name = full;
    if (team.Logo == "") team.Logo = S(t, "logos", 0, "href");
    return team;
  }

  // ---- texto do jogo ----
  static readonly CultureInfo Br = new CultureInfo("pt-BR");
  public static string When(SportGame g) {
    if (g.State == "in") {
      string d = g.Detail.Replace("Halftime", "Intervalo").Replace("HT", "Intervalo").Replace("End of ", "Fim do ")
                         .Replace("1st", "1º").Replace("2nd", "2º").Replace("3rd", "3º").Replace("4th", "4º").Replace("Qtr", "quarto");
      return "AO VIVO  " + d;
    }
    string day = g.When.Date == DateTime.Today ? "Hoje" :
                 g.When.Date == DateTime.Today.AddDays(1) ? "Amanhã" :
                 g.When.Date == DateTime.Today.AddDays(-1) ? "Ontem" : g.When.ToString("ddd dd/MM", Br);
    if (g.State == "post") {
      string fim = g.Detail.Contains("OT") || g.Detail.Contains("AET") ? "Fim (prorrog.)" : g.Detail.Contains("Pen") ? "Fim (pênaltis)" :
                   g.Detail.Contains("Postponed") ? "Adiado" : "Encerrado";
      return day + " · " + fim;
    }
    if (g.Detail.Contains("Postponed")) return day + " · Adiado";
    return day + " · " + g.When.ToString("HH:mm");
  }

  // ---- escudos (baixa uma vez, guarda em logos\, reduz para 96 px) ----
  static readonly ConcurrentDictionary<string, Image> logos = new ConcurrentDictionary<string, Image>();
  static readonly ConcurrentDictionary<string, bool> loading = new ConcurrentDictionary<string, bool>();
  static readonly object liquiLock = new object();

  public static Image Logo(string url) {
    if (string.IsNullOrEmpty(url)) return null;
    Image img;
    if (logos.TryGetValue(url, out img)) return img;
    if (loading.TryAdd(url, true))
      ThreadPool.QueueUserWorkItem(_ => {
        try {
          string file = Path.Combine(LogoDir, Hash(url) + ".png");
          if (!File.Exists(file)) {
            Directory.CreateDirectory(LogoDir);
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            // Liquipedia pede poucos acessos: um escudo por vez, com pausa
            bool liqui = url.Contains("liquipedia.net");
            if (liqui) Monitor.Enter(liquiLock);
            try {
              using (var wc = new WebClient()) {
                wc.Headers[HttpRequestHeader.UserAgent] = liqui ? Liquipedia.Agent : "DeckStation/2.5";
                wc.DownloadFile(url, file + ".tmp");
              }
              if (liqui) Thread.Sleep(400);
            } finally { if (liqui) Monitor.Exit(liquiLock); }
            if (File.Exists(file)) File.Delete(file);
            File.Move(file + ".tmp", file);
          }
          using (var src = Image.FromStream(new MemoryStream(File.ReadAllBytes(file)))) {
            var bmp = new Bitmap(96, 96);
            using (var g = Graphics.FromImage(bmp)) {
              g.InterpolationMode = InterpolationMode.HighQualityBicubic;
              g.PixelOffsetMode = PixelOffsetMode.HighQuality;
              float s = Math.Min(96f / src.Width, 96f / src.Height);
              g.DrawImage(src, (96 - src.Width * s) / 2, (96 - src.Height * s) / 2, src.Width * s, src.Height * s);
            }
            logos[url] = bmp;
          }
          Notify();
        } catch { }
      });
    return null;
  }

  static string Hash(string s) {
    using (var md5 = MD5.Create()) {
      var sb = new StringBuilder();
      foreach (byte b in md5.ComputeHash(Encoding.UTF8.GetBytes(s))) sb.Append(b.ToString("x2"));
      return sb.ToString();
    }
  }
}


// ---- e-sports pela Liquipedia ----
// Le a pagina "Liquipedia:Matches" de cada jogo (partidas ao vivo, proximas e
// resultados) pela API oficial deles. Regras da Liquipedia: identificar o
// programa, aceitar gzip e no maximo uma leitura de pagina a cada 30 s.
static class Liquipedia {
  public const string Agent = "DeckStation/2.5 (painel pessoal de placares; poucos acessos)";
  static readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
  static readonly object gate = new object();
  static DateTime last = DateTime.MinValue;

  static string Parse(string wiki, string page) {
    lock (gate) {
      double wait = 31 - (DateTime.Now - last).TotalSeconds;
      if (wait > 0) Thread.Sleep(TimeSpan.FromSeconds(wait));
      last = DateTime.Now;
      ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
      var req = (HttpWebRequest)WebRequest.Create("https://liquipedia.net/" + wiki + "/api.php?action=parse&format=json&prop=text&page=" + Uri.EscapeDataString(page));
      req.UserAgent = Agent;
      req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
      req.Timeout = 30000;
      using (var resp = req.GetResponse())
      using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) {
        var root = json.DeserializeObject(sr.ReadToEnd()) as IDictionary<string, object>;
        object parse, text, star;
        if (root == null || !root.TryGetValue("parse", out parse) ||
            !((IDictionary<string, object>)parse).TryGetValue("text", out text) ||
            !((IDictionary<string, object>)text).TryGetValue("*", out star))
          throw new Exception("resposta inesperada");
        return (string)star;
      }
    }
  }

  static string Txt(string html) { return WebUtility.HtmlDecode(Regex.Replace(html ?? "", "<[^>]+>", "")).Trim(); }
  static string Grab(string s, string pattern) { var m = Regex.Match(s, pattern, RegexOptions.Singleline); return m.Success ? m.Groups[1].Value : ""; }

  public static List<SportGame> Matches(string wiki) {
    string html = Parse(wiki, "Liquipedia:Matches");
    var list = new List<SportGame>();
    var seen = new HashSet<string>();
    var now = DateTime.Now;
    foreach (string b in html.Split(new[] { "<div class=\"match-info\">" }, StringSplitOptions.None)) {
      string ts = Grab(b, "data-timestamp=\"(\\d+)\"");
      if (ts == "") continue;
      var g = new SportGame();
      g.When = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(long.Parse(ts)).ToLocalTime();
      bool finished = b.Contains("data-finished=\"finished\"");
      // dois lados: antes e depois do placar
      int mid = b.IndexOf("match-info-header-scoreholder\"", StringComparison.Ordinal);
      int tour = b.IndexOf("match-info-tournament\"", StringComparison.Ordinal);
      if (mid < 0) continue;
      g.Home = Team(b.Substring(0, mid));
      g.Away = Team(tour > mid ? b.Substring(mid, tour - mid) : b.Substring(mid));
      if (g.Home.Name == "" || g.Away.Name == "") continue;
      var scores = Regex.Matches(b, "match-info-header-scoreholder-score[^\"]*\">([^<]*)<");
      if (scores.Count >= 2) { g.HomeScore = Txt(scores[0].Groups[1].Value); g.AwayScore = Txt(scores[1].Groups[1].Value); }
      g.Detail = Txt(Grab(b, "match-info-header-scoreholder-lower\">(.*?)</span>")).Trim('(', ')');
      g.Tour = Txt(Grab(b, "match-info-tournament-name\">(.*?)</span></a>"));
      if (g.Tour == "") g.Tour = Txt(Grab(b, "match-info-tournament-name\">(.*?)</span></span>"));
      if (finished) g.State = "post";
      else if (g.When <= now) {
        if ((now - g.When).TotalHours > 8) continue;   // ficou sem resultado na pagina
        g.State = "in";
      } else g.State = "pre";
      g.Id = g.Home.Name + "|" + g.Away.Name + "|" + ts;
      if (seen.Add(g.Id)) list.Add(g);
    }
    // muitos jogos pequenos: guarda os 45 proximos e os 30 resultados mais recentes
    var pre = list.FindAll(x => x.State == "pre");
    var post = list.FindAll(x => x.State == "post");
    pre.Sort((a, c) => a.When.CompareTo(c.When));
    post.Sort((a, c) => c.When.CompareTo(a.When));
    var res = list.FindAll(x => x.State == "in");
    res.AddRange(pre.GetRange(0, Math.Min(45, pre.Count)));
    res.AddRange(post.GetRange(0, Math.Min(30, post.Count)));
    return res;
  }

  // nome (o titulo do link e o nome completo) e escudo (a versao escura, se tiver)
  static SportTeam Team(string side) {
    var t = new SportTeam();
    string nameSpan = Grab(side, "<span class=\"name\"[^>]*>(.*?)</span>");
    t.Abbr = Txt(nameSpan);
    string title = WebUtility.HtmlDecode(Grab(nameSpan, "title=\"([^\"]*)\"")).Replace(" (page does not exist)", "");
    t.Name = title != "" && title.Length <= 20 ? title : t.Abbr;
    if (t.Name == "" || t.Name == "TBD") t.Name = "A definir";
    string img = Grab(side, "team-template-darkmode\">.*?(<img[^>]*>)");
    if (img == "") img = Grab(side, "(<img[^>]*>)");
    string src = Grab(img, ", (\\S+) 2x");   // versao maior do srcset
    if (src == "") src = Grab(img, "src=\"([^\"]+)\"");
    if (src != "") t.Logo = src.StartsWith("http") ? src : "https://liquipedia.net" + src;
    return t;
  }
}
}
