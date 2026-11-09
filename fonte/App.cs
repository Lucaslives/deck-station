// ===========================================================================
//  Programa da bandeja: mostra o painel de teclas na tela do tablet,
//  executa as acoes e abre o editor visual.
// ===========================================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace StreamDeckCyd {

class DeckApp : ApplicationContext {
  public readonly string Dir = Path.GetDirectoryName(Application.ExecutablePath);
  public string ConfigPath { get { return Path.Combine(Dir, "botoes.txt"); } }
  string LogPath { get { return Path.Combine(Dir, "deckstation.log"); } }
  // tela escolhida para o painel (vazio = automatica)
  string ScreenPath { get { return Path.Combine(Dir, "tela.txt"); } }

  readonly NotifyIcon tray = new NotifyIcon();
  readonly SynchronizationContext ui;
  readonly BlockingCollection<string> events = new BlockingCollection<string>();
  volatile DeckConfig cfg;
  volatile bool quitting;
  volatile int page;
  FileSystemWatcher watcher, testWatcher;
  System.Threading.Timer reloadTimer;
  EditorForm editor;
  readonly PanelForm panel;
  ToolStripMenuItem screenMenu, showItem;
  string screenName = "";

  public bool Connected { get { return panel.Visible; } }
  public int CurrentPage { get { return page; } }

  public DeckApp() {
    // sempre o contexto da janela: tudo que mexe no painel roda na thread dela
    ui = new WindowsFormsSynchronizationContext();
    if (!File.Exists(ConfigPath)) File.WriteAllText(ConfigPath, DefaultConfig.Build(), new UTF8Encoding(true));
    try { if (File.Exists(ScreenPath)) screenName = File.ReadAllText(ScreenPath).Trim(); } catch { }
    LoadConfig(false);
    panel = new PanelForm(this);

    tray.Icon = MakeTrayIcon();
    tray.Text = "Deck Station";
    tray.Visible = true;
    var menu = new ContextMenuStrip();
    var open = menu.Items.Add("Abrir editor", null, (s, e) => OpenEditor());
    open.Font = new Font(open.Font, FontStyle.Bold);
    showItem = new ToolStripMenuItem("Mostrar painel") { Checked = true };
    showItem.Click += (s, e) => { if (panel.Visible) panel.Hide(); else PlacePanel(); showItem.Checked = panel.Visible; SetStatus(); };
    menu.Items.Add(showItem);
    screenMenu = new ToolStripMenuItem("Tela do painel");
    screenMenu.DropDownOpening += (s, e) => FillScreenMenu();
    screenMenu.DropDownItems.Add("...");
    menu.Items.Add(screenMenu);
    menu.Items.Add("Editar botoes.txt (avancado)", null, (s, e) => Process.Start("notepad.exe", "\"" + ConfigPath + "\""));
    menu.Items.Add("Abrir pasta", null, (s, e) => Process.Start("explorer.exe", "\"" + Dir + "\""));
    menu.Items.Add("Salvar print do painel", null, (s, e) => SaveShot());
    // jogo como administrador descarta atalhos de um programa normal
    if (!Actions.SelfElevated)
      menu.Items.Add("Abrir como administrador (atalhos dentro do jogo)", null, (s, e) => RestartAsAdmin());
    menu.Items.Add(new ToolStripSeparator());
    var autoStart = new ToolStripMenuItem("Iniciar com o Windows") { Checked = AutoStart };
    autoStart.Click += (s, e) => { AutoStart = !AutoStart; autoStart.Checked = AutoStart; };
    menu.Items.Add(autoStart);
    menu.Items.Add("Sair", null, (s, e) => Quit("menu da bandeja"));
    tray.ContextMenuStrip = menu;
    tray.DoubleClick += (s, e) => OpenEditor();

    // tablet conectou/desconectou ou mudou a resolucao: reposiciona o painel
    SystemEvents.DisplaySettingsChanged += (s, e) => ui.Post(_ => { if (panel.Visible) PlacePanel(); }, null);

    // salvar o botoes.txt (pelo editor ou a mao) atualiza o painel sozinho
    watcher = new FileSystemWatcher(Dir, "botoes.txt") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
    reloadTimer = new System.Threading.Timer(_ => {
      try { LoadConfig(editor == null); ShowCurrent(); } catch (Exception ex) { Log("recarregar botoes.txt: " + ex.Message); }
    });
    FileSystemEventHandler onChange = (s, e) => reloadTimer.Change(500, Timeout.Infinite);
    watcher.Changed += onChange;
    watcher.Created += onChange;
    watcher.Renamed += (s, e) => reloadTimer.Change(500, Timeout.Infinite);
    watcher.EnableRaisingEvents = true;
    // para testes: print.pedido tira um print; evento.pedido simula toques
    // (uma linha por toque, ex.: "P 11" ou "NAV 1")
    testWatcher = new FileSystemWatcher(Dir, "*.pedido");
    testWatcher.Created += (s, e) => {
      Thread.Sleep(200);
      if (quitting) return;
      string[] lines = new string[0];
      try { lines = File.ReadAllLines(e.FullPath); File.Delete(e.FullPath); } catch { }
      if (e.Name == "print.pedido") ui.Post(_ => SaveShot(), null);
      else if (e.Name == "editor.pedido") ui.Post(_ => OpenEditor(), null);
      else if (e.Name == "mesa.pedido") ui.Post(_ => {
        // "DEMO" mostra canais de exemplo (para testar o desenho sem o OBS)
        if (Array.IndexOf(lines, "DEMO") >= 0) {
          var demo = new List<MixerChannel>();
          string[] n = { "Mic/Aux", "Audio do desktop", "Discord", "Musica", "Jogo" };
          double[] db = { -4.5, -12, -20, -30, -100 };
          for (int k = 0; k < n.Length; k++) {
            demo.Add(new MixerChannel { Name = n[k], Db = db[k], Muted = k == 4, Touched = DateTime.Now.AddYears(1) });
            Obs.Levels[n[k]] = new[] { 0.7f, 0.35f, 0.15f, 0.05f, 0f }[k];
          }
          Obs.Mixer = demo;
          Obs.Demo = true;
        }
        if (Array.IndexOf(lines, "ERRO") >= 0) throw new InvalidOperationException("erro de teste");
        if (Array.IndexOf(lines, "SAIR") >= 0) { Quit("teste"); return; }
        if (Array.IndexOf(lines, "FALHA") >= 0) { panel.FailNextPaint = true; panel.Invalidate(); return; }
        // "ESPORTES nba 1": abre o esporte numa visao (0 tabela, 1 jogos, 2 estatisticas)
        if (lines.Length > 0 && lines[0].StartsWith("ESPORTES")) {
          var parts = lines[0].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
          panel.OpenSports(parts.Length > 1 ? parts[1] : "", parts.Length > 2 ? int.Parse(parts[2]) : 0);
          return;
        }
        panel.OpenMixer(Array.IndexOf(lines, "PC") >= 0);
      }, null);
      else if (e.Name == "evento.pedido")
        foreach (string l in lines)
          if (l.StartsWith("P ") || l.StartsWith("NAV ") || l.StartsWith("GOTO ")) events.Add(l.Trim());
    };
    testWatcher.EnableRaisingEvents = true;

    PlacePanel();
    ShowCurrent();
    StartThread(EventLoop);
    StartThread(StatusLoop);
  }

  // erro numa dessas voltas nao pode derrubar o programa: registra e continua
  void StartThread(ThreadStart f) {
    new Thread(() => {
      while (!quitting) {
        try { f(); return; }
        catch (Exception ex) { Log("erro em " + f.Method.Name + ": " + ex); Thread.Sleep(1000); }
      }
    }) { IsBackground = true }.Start();
  }

  public void OpenEditor() {
    if (editor == null || editor.IsDisposed) {
      editor = new EditorForm(this);
      editor.FormClosed += (s, e) => editor = null;
      // o editor abre na tela principal, nunca em cima do painel
      editor.StartPosition = FormStartPosition.Manual;
      var wa = Screen.PrimaryScreen.WorkingArea;
      editor.Location = new Point(wa.X + Math.Max(0, (wa.Width - editor.Width) / 2), wa.Y + Math.Max(0, (wa.Height - editor.Height) / 2));
      editor.Show();
    } else {
      if (editor.WindowState == FormWindowState.Minimized) editor.WindowState = FormWindowState.Normal;
      editor.Activate();
    }
  }

  public void Log(string msg) {
    try {
      if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 200000) File.Delete(LogPath);
      File.AppendAllText(LogPath, DateTime.Now.ToString("dd/MM HH:mm:ss ") + msg + Environment.NewLine);
    } catch { }
  }

  void Balloon(string title, string msg, ToolTipIcon icon) {
    ui.Post(_ => tray.ShowBalloonTip(4000, title, msg, icon), null);
  }

  void SetStatus() {
    string t = panel.Visible ? "Deck Station: na " + ScreenLabel(PanelScreen()) : "Deck Station: painel escondido";
    if (t.Length > 63) t = t.Substring(0, 63);
    tray.Text = t;
    if (editor != null) editor.SetDeviceStatus(t);
  }

  void LoadConfig(bool notify) {
    try {
      var c = DeckConfig.Load(ConfigPath);
      cfg = c;
      Obs.Port = c.ObsPort;
      Obs.Password = c.ObsPassword;
      if (page >= c.Pages.Count) page = 0;
      Log("botoes.txt carregado: " + c.Pages.Count + " paginas");
      foreach (string w in c.Warnings) Log("aviso: " + w);
      if (c.Warnings.Count > 0) Balloon("botoes.txt", string.Join("\n", c.Warnings.ToArray()), ToolTipIcon.Warning);
      else if (notify) Balloon("Deck Station", "Botoes atualizados", ToolTipIcon.Info);
    } catch (Exception ex) {
      Log("erro em botoes.txt: " + ex.Message);
      Balloon("Erro em botoes.txt", ex.Message, ToolTipIcon.Error);
      if (cfg == null) cfg = new DeckConfig();
    }
  }

  // ---- chamados pelo editor e pelo painel ----
  public void ShowPage(int idx) { events.Add("GOTO " + idx); }
  public void RunAction(string action) { events.Add("RUN " + action); }
  public void Touch(string ev) { events.Add(ev); }

  // ---- tela do tablet ----
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  struct DISPLAY_DEVICE {
    public int cb;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
    public int StateFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
  }
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string device, int i, ref DISPLAY_DEVICE dd, int flags);

  // nome da placa de video da tela (o spacedesk aparece como "spacedesk Graphics Adapter")
  static string Adapter(Screen s) {
    var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)) };
    for (int i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++) {
      if (dd.DeviceName == s.DeviceName) return dd.DeviceString ?? "";
      dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
    }
    return "";
  }

  static bool LooksLikeTablet(Screen s) {
    string a = Adapter(s).ToLowerInvariant();
    return a.Contains("spacedesk") || a.Contains("superdisplay") || a.Contains("duet") || a.Contains("splashtop") || a.Contains("idd") || a.Contains("virtual");
  }

  Screen PanelScreen() {
    var all = Screen.AllScreens;
    if (screenName != "")
      foreach (var s in all) if (s.DeviceName == screenName) return s;
    // automatica: tela do spacedesk; senao a menor tela que nao e a principal
    foreach (var s in all) if (!s.Primary && LooksLikeTablet(s)) return s;
    Screen best = null;
    foreach (var s in all)
      if (!s.Primary && (best == null || s.Bounds.Width * s.Bounds.Height < best.Bounds.Width * best.Bounds.Height)) best = s;
    return best ?? Screen.PrimaryScreen;
  }

  static string ScreenLabel(Screen s) {
    int n;
    string num = int.TryParse(s.DeviceName.Replace(@"\\.\DISPLAY", ""), out n) ? n.ToString() : s.DeviceName;
    string a = Adapter(s);
    return "tela " + num + " (" + s.Bounds.Width + "x" + s.Bounds.Height + (s.Primary ? ", principal" : "") +
           (a.ToLowerInvariant().Contains("spacedesk") ? ", tablet" : "") + ")";
  }

  void FillScreenMenu() {
    screenMenu.DropDownItems.Clear();
    var auto = new ToolStripMenuItem("Automatica (tablet)") { Checked = screenName == "" };
    auto.Click += (s, e) => ChooseScreen("");
    screenMenu.DropDownItems.Add(auto);
    foreach (var sc in Screen.AllScreens) {
      string name = sc.DeviceName;
      var it = new ToolStripMenuItem(ScreenLabel(sc)) { Checked = screenName == name };
      it.Click += (s, e) => ChooseScreen(name);
      screenMenu.DropDownItems.Add(it);
    }
  }

  void ChooseScreen(string name) {
    screenName = name;
    try { File.WriteAllText(ScreenPath, name); } catch { }
    PlacePanel();
  }

  void PlacePanel() {
    var s = PanelScreen();
    if (s.Primary && Screen.AllScreens.Length > 1 && screenName == "") Log("tablet nao encontrado: painel na tela principal");
    panel.PlaceOn(s);
    if (!panel.Visible) panel.Show();
    showItem.Checked = true;
    Log("painel na " + ScreenLabel(s));
    SetStatus();
  }

  void ShowCurrent() {
    var c = cfg;
    int pg = page;
    ui.Post(_ => panel.SetPage(c, pg), null);
  }

  void SaveShot() {
    string path = Path.Combine(Dir, "print.png");
    using (var bmp = panel.Shot()) bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    Log("print salvo: " + path);
  }

  // ---- eventos: toques e troca de pagina ----
  void EventLoop() {
    while (true) {
      string ev = events.Take();
      try { HandleEvent(ev); } catch (Exception ex) { Log("erro: " + ex); }
    }
  }

  void HandleEvent(string ev) {
    var c = cfg;
    if (c == null || c.Pages.Count == 0) return;
    if (ev.StartsWith("NAV ")) {
      int d = int.Parse(ev.Substring(4));
      page = (page + d + c.Pages.Count) % c.Pages.Count;
      PageChanged();
      return;
    }
    if (ev.StartsWith("GOTO ")) {
      int idx = int.Parse(ev.Substring(5));
      if (idx >= 0 && idx < c.Pages.Count && idx != page) { page = idx; PageChanged(); }
      return;
    }
    if (ev == "MESA") {
      // mesa de som acabou de abrir: le as fontes de audio na hora
      try { Obs.MixerRefresh(); } catch (Exception ex) { Log("mesa de som: " + ex.Message); }
      return;
    }
    if (ev.StartsWith("RUN ")) {
      if (PanelAction(ev.Substring(4))) return;
      try { Actions.Run(ev.Substring(4)); Log("teste '" + ev.Substring(4) + "': ok"); }
      catch (Exception ex) { Log("teste '" + ev.Substring(4) + "' falhou: " + ex.Message); Balloon("Teste", ex.Message, ToolTipIcon.Warning); }
      return;
    }
    int i = int.Parse(ev.Substring(2));
    if (page >= c.Pages.Count || i < 0 || i >= 12) return;
    int pg = page;
    if (editor != null) ui.Post(_ => { if (editor != null) editor.DeviceTouched(pg, i); }, null);
    var b = c.Pages[page].Buttons[i];
    if (b == null) return;
    try {
      Log("botao '" + b.Label + "': " + b.Action);
      if (PanelAction(b.Action)) return;
      string next = Actions.Run(b.Action);
      if (next != null) GoToPage(next);
      string a = DeckConfig.Norm(b.Action);
      if (a.StartsWith("obs:") || a.StartsWith("discord:")) SendKeyStates(false);
      if (a.Contains("volume") || a.Contains("mudo") || a.Contains("microfone")) {
        Thread.Sleep(120);
        SendStatus();
      }
    } catch (Exception ex) {
      Log("falhou '" + b.Label + "': " + ex.Message);
      string msg = b.Label + ": " + ex.Message;
      ui.Post(_ => panel.Error(i, msg), null);
      Balloon(b.Label, ex.Message, ToolTipIcon.Warning);
    }
  }

  // obs:mesa e mixer:pc nao sao acoes do PC: abrem a mesa de som no proprio painel
  bool PanelAction(string action) {
    string a = DeckConfig.Norm(action);
    if (a == "obs:mesa") { ui.Post(_ => panel.OpenMixer(), null); return true; }
    if (a == "mixer" || a.StartsWith("mixer:")) { ui.Post(_ => panel.OpenMixer(true), null); return true; }
    if (a == "esportes" || a.StartsWith("esportes:")) {
      string which = a.Length > 9 ? action.Trim().Substring(9) : "";
      ui.Post(_ => panel.OpenSports(which), null);
      return true;
    }
    return false;
  }

  void GoToPage(string spec) {
    var c = cfg;
    string s = spec.Trim();
    if (s == "+") page = (page + 1) % c.Pages.Count;
    else if (s == "-") page = (page - 1 + c.Pages.Count) % c.Pages.Count;
    else {
      int idx = c.Pages.FindIndex(p => DeckConfig.Norm(p.Name) == DeckConfig.Norm(s));
      if (idx < 0) throw new Exception("pagina nao existe: " + s);
      page = idx;
    }
    PageChanged();
  }

  void PageChanged() {
    ShowCurrent();
    SendKeyStates(true);
    int pg = page;
    if (editor != null) ui.Post(_ => { if (editor != null) editor.DevicePage(pg); }, null);
  }

  void SendStatus() {
    int cpu = SysInfo.Cpu(), ram = SysInfo.Ram(), vol = Audio.Volume();
    bool spk = Audio.SpeakerMuted(), mic = Audio.MicMuted();
    ui.Post(_ => panel.SetStatus(cpu, ram, vol, spk, mic), null);
  }

  void StatusLoop() {
    SysInfo.Cpu();
    while (!quitting) {
      Thread.Sleep(1000);
      try { SendStatus(); } catch (Exception ex) { Log("status: " + ex.Message); }
      try { SendKeyStates(true); } catch (Exception ex) { Log("estado do OBS: " + ex.Message); }
      if (panel.MixerOpen) try { Obs.MixerRefresh(); } catch (Exception ex) { Log("mesa de som: " + ex.Message); }
    }
  }

  // estado das teclas do OBS e do Discord (cena atual, mudo, ao vivo...) -> painel
  readonly object stateLock = new object();

  void SendKeyStates(bool refresh) {
    var c = cfg;
    if (c == null || page >= c.Pages.Count) return;
    var pg = c.Pages[page];
    var obsActions = new List<string>();
    foreach (var b in pg.Buttons) if (b != null && DeckConfig.Norm(b.Action).StartsWith("obs:")) obsActions.Add(b.Action);
    var dcActions = new List<string>();
    foreach (var b in pg.Buttons) if (b != null && DeckConfig.Norm(b.Action).StartsWith("discord:")) dcActions.Add(b.Action);
    var st = new int[12];
    if (obsActions.Count > 0 || dcActions.Count > 0)
      lock (stateLock) {
        if (refresh && obsActions.Count > 0) Obs.Refresh(obsActions);
        if (refresh && dcActions.Count > 0) Discord.Refresh(dcActions);
        for (int i = 0; i < 12; i++) {
          var b = pg.Buttons[i];
          st[i] = b == null ? 0 : DeckConfig.Norm(b.Action).StartsWith("discord:") ? Discord.KeyState(b.Action) : Obs.KeyState(b.Action);
        }
      }
    ui.Post(_ => panel.SetKeyStates(st), null);
  }

  // ---- outros ----
  const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
  bool AutoStart {
    get {
      using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("DeckStation") != null;
    }
    set {
      using (var k = Registry.CurrentUser.CreateSubKey(RunKey)) {
        if (value) k.SetValue("DeckStation", "\"" + Application.ExecutablePath + "\"");
        else k.DeleteValue("DeckStation", false);
      }
    }
  }

  public static Icon MakeTrayIcon() {
    using (var bmp = new Bitmap(16, 16)) {
      using (var g = Graphics.FromImage(bmp)) {
        g.Clear(Color.Transparent);
        Color[] c = { Color.FromArgb(47, 107, 224), Color.FromArgb(106, 75, 232), Color.FromArgb(240, 122, 34), Color.FromArgb(35, 165, 90) };
        for (int i = 0; i < 4; i++)
          using (var br = new SolidBrush(c[i])) g.FillRectangle(br, 1 + (i % 2) * 8, 1 + (i / 2) * 8, 6, 6);
      }
      return Icon.FromHandle(bmp.GetHicon());
    }
  }

  void RestartAsAdmin() {
    try {
      Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--reiniciar") { UseShellExecute = true, Verb = "runas", WorkingDirectory = Dir });
    } catch (System.ComponentModel.Win32Exception) {
      return;  // cancelou o aviso do Windows
    }
    Quit("reiniciando como administrador");
  }

  public void Quit(string why) {
    Log("fechando: " + why);
    quitting = true;
    tray.Visible = false;
    if (editor != null) editor.Close();
    panel.StopDigi();
    panel.Close();
    ExitThread();
  }

  [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

  static bool MainAlive() {
    try { using (Mutex.OpenExisting("DeckStation")) return true; }
    catch (WaitHandleCannotBeOpenedException) { return false; }
    catch (UnauthorizedAccessException) { return true; }   // rodando como administrador
  }

  // DeckStation.exe --guardiao: processo sem janela que reabre o programa se ele
  // morrer sem passar pelo fim normal (fechar pelo X ou pela bandeja nao reabre)
  static void Guardian(string d) {
    bool first;
    using (new Mutex(true, "DeckStation-guardiao", out first)) {
      if (!first) return;
      string flag = Path.Combine(d, "rodando.flag");
      var starts = new List<DateTime>();
      int idle = 0;
      while (true) {
        Thread.Sleep(3000);
        if (MainAlive()) { idle = 0; continue; }
        if (!File.Exists(flag)) { if (++idle >= 3) return; continue; }
        starts.RemoveAll(t => (DateTime.Now - t).TotalMinutes > 5);
        if (starts.Count >= 3) { CrashLog(d, "guardiao: caiu 3 vezes em 5 min, nao vou reabrir"); return; }
        starts.Add(DateTime.Now);
        CrashLog(d, "guardiao: o programa sumiu, reabrindo");
        try { Process.Start(new ProcessStartInfo(Application.ExecutablePath) { WorkingDirectory = d, UseShellExecute = true }); } catch { }
        Thread.Sleep(5000);
      }
    }
  }

  static void CrashLog(string dir, string msg) {
    try { File.AppendAllText(Path.Combine(dir, "deckstation.log"), DateTime.Now.ToString("dd/MM HH:mm:ss ") + msg + Environment.NewLine); } catch { }
  }

  [STAThread]
  static void Main(string[] args) {
    string d = Path.GetDirectoryName(Application.ExecutablePath);
    string cp = Path.Combine(d, "botoes.txt");
    if (args.Length > 0 && args[0] == "--guardiao") { Guardian(d); return; }
    // DeckStation.exe --previa: desenha todas as teclas em previa.png e sai
    if (args.Length > 0 && args[0] == "--previa") {
      if (!File.Exists(cp)) File.WriteAllText(cp, DefaultConfig.Build(), new UTF8Encoding(true));
      var c = DeckConfig.Load(cp);
      // --previa skin:matrix  desenha com outra skin
      if (args.Length >= 2 && args[1].StartsWith("skin:")) {
        c.Skin = KeyArt.Skin = args[1].Substring(5);
        foreach (var pg in c.Pages) foreach (var kb in pg.Buttons) if (kb != null) c.Prepare(kb);
        args = new string[] { args[0] };
      }
      KeyArt.Preview(c, Path.Combine(d, "previa.png"));
      // --previa P I: quadros da animacao da tecla I da pagina P
      if (args.Length >= 3) {
        var b = c.Pages[int.Parse(args[1])].Buttons[int.Parse(args[2])];
        if (b != null) KeyArt.AnimStrip(b, c.Labels, Path.Combine(d, "previa-anim.png"));
      }
      File.WriteAllLines(Path.Combine(d, "previa.txt"), c.Warnings.ToArray());
      return;
    }
    bool first;
    // --reiniciar (abrir como administrador): espera a instancia antiga fechar
    bool restart = args.Length > 0 && args[0] == "--reiniciar";
    Mutex mutex = new Mutex(true, "DeckStation", out first);
    for (int t = 0; restart && !first && t < 40; t++) {
      mutex.Dispose();
      Thread.Sleep(200);
      mutex = new Mutex(true, "DeckStation", out first);
    }
    using (mutex) {
      if (!first) {
        // ja esta aberto: pede para a instancia que esta rodando abrir o editor
        try { File.WriteAllText(Path.Combine(d, "editor.pedido"), ""); } catch { }
        return;
      }
      // pixels reais em cada tela (painel nitido no tablet)
      try { SetProcessDPIAware(); } catch { }
      // rodando.flag so sobra se o programa morreu sem passar pelo fim normal
      string flag = Path.Combine(d, "rodando.flag");
      if (File.Exists(flag)) CrashLog(d, "a ultima execucao NAO terminou pelo proprio programa (encerrado por fora, queda ou PC desligado)");
      try { File.WriteAllText(flag, ""); } catch { }
      try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--guardiao") { WorkingDirectory = d, UseShellExecute = false }); } catch { }
      SystemEvents.SessionEnded += (s, e) => { CrashLog(d, "Windows saindo: " + e.Reason); try { File.Delete(flag); } catch { } };
      Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
      Application.ThreadException += (s, e) => CrashLog(d, "erro na janela (continuou aberto): " + e.Exception);
      AppDomain.CurrentDomain.UnhandledException += (s, e) => CrashLog(d, "ERRO FATAL: " + e.ExceptionObject);
      Application.EnableVisualStyles();
      Application.SetCompatibleTextRenderingDefault(false);
      var app = new DeckApp();
      if (args.Length > 0 && args[0] == "--editor") app.OpenEditor();
      Application.Run(app);
      try { File.Delete(flag); } catch { }
      CrashLog(d, "programa encerrado");
    }
  }
}

// ------------------------------- botoes.txt padrao -------------------------
static class DefaultConfig {
  public const string Header =
@"# ===================================================================
#  Botoes do Deck Station. O jeito facil de mudar e o EDITOR (dois cliques
#  no icone perto do relogio). Este arquivo tambem pode ser editado a mao:
#  salvou, a placa atualiza sozinha.
#
#  Cada linha e uma tecla (ate 12 por pagina, da esquerda pra direita):
#     Nome | acao | cor | icone | animacao
#  So nome e acao sao obrigatorios. Uma linha com so  -  e um espaco vazio.
#
#  ACOES
#    midia:play / proxima / anterior / parar / volume+ / volume- / mudo
#    volume:+10   volume:-10   volume:50     (volume do Windows)
#    microfone                              (muta/desmuta o microfone em
#                                            todos os programas)
#    abrir:C:\caminho\programa.exe           (programa, atalho .lnk, pasta)
#    abrir:https://site.com                  (abre no navegador)
#    jogo:1984270                            (jogo da Steam pelo numero)
#    atalho:ctrl+shift+m                     (aperta teclas juntas)
#    digitar:Ola, tudo bem?\n                (\n = Enter)
#    comando:shutdown /s /t 60               (roda no Prompt de Comando)
#    pagina:Nome   pagina:+   pagina:-       (troca de pagina)
#    esperar:500                             (pausa em milissegundos)
#    Varias acoes numa tecla: separe com  ;  (com espaco antes e depois)
#
#  CORES: azul roxo anil lilas verde turquesa ciano vermelho laranja
#         amarelo rosa cinza grafite preto marrom branco - ou 1E90FF
#         (vazio = automatica; programas usam a cor do proprio icone)
#
#  ICONES: vazio = automatico. Nomes: play pausa anterior proxima parar
#    volmais volmenos volume mudo mic fone web pasta musica chat app
#    copiar colar recortar print cadeado energia estrela casa jogo calc
#    nota desfazer monitor grafico seta voltar teclado config email
#    telefone video brilho download atualizar busca link salvar nuvem
#    wifi bluetooth loja coracao bandeira relogio calendario emoji ideia
#    codigo cronometro usuario lixeira olho imprimir raio mapa carro
#    aviao sino escudo onda tela gravar
#    ou simbolo:E7FC, ou o caminho de uma imagem .png/.jpg/.ico/.exe
#
#  OBS (precisa do servidor WebSocket ligado no OBS):
#    obs:cena:Nome  obs:cena:+  obs:cena:-  obs:mic  obs:desktop
#    obs:mudo:Fonte  obs:facecam  obs:mostrar:Fonte  obs:live  obs:gravar
#    obs:pausar  obs:replay  obs:camvirtual  obs:estudio
#    obs_porta / obs_senha vazios = usa as do proprio OBS
#  DISCORD (bot seu no servidor, configure em Discord... no editor):
#    discord:mutar:nome   discord:ensurdecer:nome   (mudo do servidor)
#  STEAM: abrir:steam://open/games  steam://store  steam://open/friends ...
#
#  ANIMACAO ao tocar: auto pular pulsar girar balancar nenhuma
#
#  rotulos = sim   mostra o nome embaixo de cada icone
#  brilho = sim    um brilho passa pelas teclas de vez em quando
#  skin = padrao   visual: padrao, matrix, pixel, cyberpunk, digivice ou vidro
# ===================================================================
";

  const string Pages = @"
[Inicio]
Play/Pause | midia:play | azul
Anterior | midia:anterior | azul
Proxima | midia:proxima | azul
Mudo | midia:mudo | grafite
Volume - | midia:volume- | grafite
Volume + | midia:volume+ | grafite
Microfone | microfone | roxo
Discord | abrir:%APPDATA%\Microsoft\Windows\Start Menu\Programs\Discord Inc\Discord.lnk
Opera GX | abrir:%APPDATA%\Microsoft\Windows\Start Menu\Programs\Navegador Opera GX.lnk
OBS | pagina:OBS | roxo | live
Jogos | pagina:Jogos | anil | jogo
Trabalho | pagina:Trabalho | laranja | seta

[Trabalho]
Copiar | atalho:ctrl+c | azul
Colar | atalho:ctrl+v | azul
Desfazer | atalho:ctrl+z | azul
Print | atalho:win+shift+s | ciano
Mudo Discord | atalho:ctrl+shift+m | lilas | chat
Ensurdecer | atalho:ctrl+shift+d | lilas | fone
Area de trabalho | atalho:win+d | anil
Tarefas | atalho:ctrl+shift+esc | anil
Calcular | abrir:calc | turquesa | calc
Downloads | abrir:%USERPROFILE%\Downloads | amarelo | download
Bloquear PC | atalho:win+l | vermelho
Voltar | pagina:Inicio | laranja | voltar
";

  // pagina Jogos com o que estiver instalado na Steam
  public static string GamesPage() {
    var sb = new StringBuilder("\n[Jogos]\n");
    int n = 0;
    foreach (var g in SteamLib.Games()) {
      if (n >= 10) break;
      sb.Append(g.Name.Replace("|", "/") + " | jogo:" + g.AppId + "\n");
      n++;
    }
    sb.Append("YouTube | abrir:https://www.youtube.com | vermelho | video\n");
    sb.Append("Steam | pagina:Steam | | %APPDATA%\\Microsoft\\Windows\\Start Menu\\Programs\\Steam\\Steam.lnk\n");
    sb.Append("Voltar | pagina:Inicio | laranja | voltar\n");
    return sb.ToString();
  }

  // pagina OBS com as cenas que o OBS ja tem
  public static string ObsPage() {
    var sb = new StringBuilder("\n[OBS]\n");
    var scenes = Obs.SceneNamesOffline();
    string[] icons = { "bandeira", "jogo", "relogio" };
    for (int i = 0; i < 3; i++) {
      if (i < scenes.Count) sb.Append(scenes[i].Replace("|", "/") + " | obs:cena:" + scenes[i].Replace("|", "/") + " | anil | " + icons[i] + "\n");
      else sb.Append("-\n");
    }
    sb.Append("Facecam | obs:facecam | lilas\n");
    sb.Append("Microfone | obs:mic | roxo\n");
    sb.Append("Audio desktop | obs:desktop | azul\n");
    sb.Append("Ao vivo | obs:live | vermelho\n");
    sb.Append("Gravar | obs:gravar | vermelho\n");
    sb.Append("Replay | obs:replay | turquesa\n");
    sb.Append("Camera virtual | obs:camvirtual | ciano\n");
    sb.Append("Abrir OBS | abrir:%ProgramData%\\Microsoft\\Windows\\Start Menu\\Programs\\OBS Studio.lnk\n");
    sb.Append("Voltar | pagina:Inicio | laranja | voltar\n");
    return sb.ToString();
  }

  public const string SteamPage = @"
[Steam]
Biblioteca | abrir:steam://open/games | azul
Loja | abrir:steam://store | anil
Amigos | abrir:steam://open/friends | turquesa
Downloads | abrir:steam://open/downloads | verde
Big Picture | abrir:steam://open/bigpicture | roxo
Capturas | abrir:steam://open/screenshots | ciano
Configuracoes | abrir:steam://open/settings | grafite
Print no jogo | atalho:f12 | lilas | print
Abrir Steam | abrir:%APPDATA%\Microsoft\Windows\Start Menu\Programs\Steam\Steam.lnk
Jogos | pagina:Jogos | anil | jogo
-
Voltar | pagina:Jogos | laranja | voltar
";

  public static string Build() {
    return Header + "skin = padrao\nsom = sim\nrotulos = nao\nbrilho = sim\napagar_tela = 5\nporta = auto\n" + Pages + GamesPage() + ObsPage() + SteamPage;
  }
}

}
