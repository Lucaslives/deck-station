// ===========================================================================
//  Editor visual: grade com as 12 teclas da pagina, clique para editar,
//  arraste para trocar de lugar. Cada mudanca e salva no botoes.txt e a
//  placa atualiza na hora.
// ===========================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace StreamDeckCyd {

static class Theme {
  public static readonly Color Back = Color.FromArgb(22, 23, 28);
  public static readonly Color Panel = Color.FromArgb(32, 33, 41);
  public static readonly Color Field = Color.FromArgb(42, 44, 54);
  public static readonly Color Text = Color.FromArgb(232, 233, 238);
  public static readonly Color Dim = Color.FromArgb(150, 152, 165);
  public static readonly Color Accent = Color.FromArgb(124, 92, 255);

  public static Button Btn(string text, int x, int y, int w, int h) {
    var b = new Button { Text = text, Location = new Point(x, y), Size = new Size(w, h), FlatStyle = FlatStyle.Flat,
                         BackColor = Field, ForeColor = Text, Cursor = Cursors.Hand };
    b.FlatAppearance.BorderColor = Color.FromArgb(60, 62, 75);
    b.FlatAppearance.MouseOverBackColor = Color.FromArgb(56, 58, 72);
    return b;
  }
  public static Label Lbl(string text, int x, int y, bool title) {
    return new Label { Text = text, Location = new Point(x, y), AutoSize = true, ForeColor = title ? Text : Dim,
                       Font = new Font("Segoe UI", title ? 11f : 9f, title ? FontStyle.Bold : FontStyle.Regular) };
  }
  public static void Field_(Control c) {
    c.BackColor = Field;
    c.ForeColor = Text;
    if (c is TextBox) ((TextBox)c).BorderStyle = BorderStyle.FixedSingle;
    if (c is ComboBox) ((ComboBox)c).FlatStyle = FlatStyle.Flat;
  }
}

// uma tecla na grade do editor
class KeyTile : Control {
  public int Index;
  public Bitmap Image;
  public Bitmap[] Frames;   // animacao tocando
  public int Frame = -1;
  public bool Selected, DropTarget;

  public KeyTile() {
    SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    Cursor = Cursors.Hand;
    AllowDrop = true;
  }

  protected override void OnPaint(PaintEventArgs e) {
    var g = e.Graphics;
    g.Clear(Theme.Back);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    var r = new Rectangle(4, 4, Width - 9, Height - 9);
    Bitmap img = Frames != null && Frame >= 0 && Frame < Frames.Length ? Frames[Frame] : Image;
    if (img != null) {
      g.InterpolationMode = InterpolationMode.HighQualityBicubic;
      g.DrawImage(img, r);
    } else {
      using (var pen = new Pen(Color.FromArgb(70, 72, 86), 2f) { DashStyle = DashStyle.Dash }) g.DrawRectangle(pen, r);
      using (var f = new Font("Segoe UI", 26f))
      using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
      using (var br = new SolidBrush(Color.FromArgb(90, 92, 108))) g.DrawString("+", f, br, r, sf);
    }
    if (Selected || DropTarget)
      using (var pen = new Pen(DropTarget ? Color.White : Theme.Accent, 3f)) g.DrawRectangle(pen, 1.5f, 1.5f, Width - 4, Height - 4);
  }
}

class EditorForm : Form {
  readonly DeckApp app;
  DeckConfig doc;
  int pageIdx, sel = -1;
  bool loading;
  DeckButton clip;

  ListBox pageList;
  readonly KeyTile[] tiles = new KeyTile[12];
  TextBox nameBox;
  ComboBox typeBox, animBox;
  Panel valuePanel, propsPanel;
  FlowLayoutPanel colorFlow;
  PictureBox iconPic;
  Label emptyHint, statusLbl, deviceLbl, titleLbl, typeLbl;
  CheckBox labelsChk, soundChk, shineChk;
  ComboBox skinBox;
  NumericUpDown dimNum;
  readonly Timer saveTimer = new Timer { Interval = 700 };
  readonly Timer animTimer = new Timer { Interval = 30 };
  KeyTile animTile;

  // controles do valor da acao (so um tipo aparece por vez)
  Control valueCtl;
  string autoName = "";

  static readonly string[] Types = {
    "Musica e volume", "Abrir programa ou pasta", "Jogo da Steam", "Abrir site", "Atalho de teclado",
    "Digitar texto", "Microfone (liga/desliga)", "Ir para outra pagina", "Comando do Windows", "Avancado (texto livre)",
    "OBS Studio (cenas, live, mudo...)", "Funcao da Steam (loja, amigos...)", "Discord (meu microfone, ensurdecer...)"
  };
  // ordem em que os tipos aparecem na lista (o numero e o codigo de Types)
  static readonly int[] Order = { 0, 1, 2, 11, 10, 12, 3, 4, 5, 6, 7, 8, 9 };
  static readonly string[] ObsNames = {
    "Trocar para a cena...", "Proxima cena", "Cena anterior", "Mutar microfone", "Mutar audio do desktop",
    "Mutar outra fonte de audio...", "Esconder/mostrar a facecam", "Esconder/mostrar uma fonte...", "Iniciar/parar a live",
    "Iniciar/parar a gravacao", "Pausar a gravacao", "Salvar replay", "Camera virtual", "Modo estudio",
    "Abrir a mesa de som (no painel)"
  };
  static readonly string[] ObsCmds = { "cena:", "cena:+", "cena:-", "mic", "desktop", "mudo:", "facecam", "mostrar:", "live", "gravar", "pausar", "replay", "camvirtual", "estudio", "mesa" };
  static readonly string[] ObsShort = { "", "Proxima cena", "Cena anterior", "Microfone", "Audio desktop", "", "Facecam", "", "Ao vivo", "Gravar", "Pausar", "Replay", "Camera virtual", "Estudio", "Mesa de som" };
  static readonly string[] SteamNames = { "Biblioteca", "Loja", "Amigos", "Downloads", "Big Picture", "Capturas de tela", "Configuracoes", "Ativar um produto" };
  static readonly string[] SteamUrls = { "steam://open/games", "steam://store", "steam://open/friends", "steam://open/downloads",
                                         "steam://open/bigpicture", "steam://open/screenshots", "steam://open/settings", "steam://open/activateproduct" };
  static readonly string[] MediaNames = { "Play / Pause", "Proxima musica", "Musica anterior", "Parar", "Volume +", "Volume -", "Mudo", "Volume +10%", "Volume -10%", "Mixer do PC (no painel)", "Esportes: tabelas e placares (no painel)" };
  static readonly string[] MediaActions = { "midia:play", "midia:proxima", "midia:anterior", "midia:parar", "midia:volume+", "midia:volume-", "midia:mudo", "volume:+10", "volume:-10", "mixer:pc", "esportes" };
  static readonly string[] AnimLabels = { "Automatica", "Pular", "Pulsar", "Girar", "Balancar", "Nenhuma" };
  static readonly string[] CommonKeys = {
    "Copiar|ctrl+c", "Colar|ctrl+v", "Recortar|ctrl+x", "Desfazer|ctrl+z", "Salvar|ctrl+s", "Recorte de tela|win+shift+s",
    "Area de trabalho|win+d", "Bloquear PC|win+l", "Gerenciador de tarefas|ctrl+shift+esc", "Mudo no Discord|ctrl+shift+m",
    "Ensurdecer no Discord|ctrl+shift+d", "Fechar janela|alt+f4", "Trocar janela|alt+tab", "Nova aba|ctrl+t", "Explorador|win+e"
  };

  public EditorForm(DeckApp app) {
    this.app = app;
    Text = "Editor do Deck Station";
    Icon = DeckApp.MakeTrayIcon();
    BackColor = Theme.Back;
    ForeColor = Theme.Text;
    Font = new Font("Segoe UI", 9f);
    FormBorderStyle = FormBorderStyle.FixedSingle;
    MaximizeBox = false;
    ClientSize = new Size(1190, 590);
    StartPosition = FormStartPosition.CenterScreen;
    KeyPreview = true;

    BuildLeft();
    BuildGrid();
    BuildProps();
    statusLbl = Theme.Lbl("", 210, 560, false);
    deviceLbl = Theme.Lbl(app.Connected ? "Painel na tela do tablet" : "Painel escondido", 12, 560, false);
    Controls.Add(statusLbl);
    Controls.Add(deviceLbl);

    saveTimer.Tick += (s, e) => { saveTimer.Stop(); Save(); };
    animTimer.Tick += (s, e) => AnimTick();
    FormClosing += (s, e) => { if (saveTimer.Enabled) { saveTimer.Stop(); Save(); } };

    Reload();
    pageIdx = Math.Min(app.CurrentPage, Math.Max(0, doc.Pages.Count - 1));
    RefreshPages();
  }

  // ---------------------------- montagem da janela --------------------------
  void BuildLeft() {
    Controls.Add(Theme.Lbl("Paginas", 12, 10, true));
    pageList = new ListBox { Location = new Point(12, 38), Size = new Size(180, 200), BorderStyle = BorderStyle.None,
                             BackColor = Theme.Panel, ForeColor = Theme.Text, Font = new Font("Segoe UI", 10.5f), ItemHeight = 26,
                             DrawMode = DrawMode.OwnerDrawFixed };
    pageList.DrawItem += (s, e) => {
      if (e.Index < 0) return;
      bool on = (e.State & DrawItemState.Selected) != 0;
      using (var br = new SolidBrush(on ? Theme.Accent : Theme.Panel)) e.Graphics.FillRectangle(br, e.Bounds);
      TextRenderer.DrawText(e.Graphics, pageList.Items[e.Index].ToString(), pageList.Font, new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height),
                            Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    };
    pageList.SelectedIndexChanged += (s, e) => {
      if (loading || pageList.SelectedIndex < 0) return;
      pageIdx = pageList.SelectedIndex;
      sel = -1;
      RefreshTiles();
      ShowProps();
      app.ShowPage(pageIdx);
    };
    Controls.Add(pageList);

    var add = Theme.Btn("+ Nova", 12, 244, 88, 28);
    add.Click += (s, e) => {
      string n = Ask("Nome da nova pagina:", "Pagina " + (doc.Pages.Count + 1));
      if (n == null) return;
      doc.Pages.Add(new DeckPage { Name = n });
      pageIdx = doc.Pages.Count - 1;
      Changed(true);
    };
    var ren = Theme.Btn("Renomear", 104, 244, 88, 28);
    ren.Click += (s, e) => {
      if (doc.Pages.Count == 0) return;
      string n = Ask("Novo nome da pagina:", doc.Pages[pageIdx].Name);
      if (n == null) return;
      string old = doc.Pages[pageIdx].Name;
      doc.Pages[pageIdx].Name = n;
      // teclas que levavam para esta pagina continuam funcionando
      foreach (var p in doc.Pages)
        foreach (var b in p.Buttons)
          if (b != null && DeckConfig.Norm(b.Action) == "pagina:" + DeckConfig.Norm(old)) b.Action = "pagina:" + n;
      Changed(true);
    };
    var up = Theme.Btn("\u25B2", 12, 276, 42, 28);
    up.Click += (s, e) => MovePage(-1);
    var down = Theme.Btn("\u25BC", 58, 276, 42, 28);
    down.Click += (s, e) => MovePage(1);
    var del = Theme.Btn("Excluir", 104, 276, 88, 28);
    del.Click += (s, e) => {
      if (doc.Pages.Count <= 1) return;
      if (MessageBox.Show(this, "Excluir a pagina '" + doc.Pages[pageIdx].Name + "' e as teclas dela?", "Excluir pagina",
                          MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
      doc.Pages.RemoveAt(pageIdx);
      pageIdx = Math.Max(0, pageIdx - 1);
      Changed(true);
    };
    var games = Theme.Btn("Adicionar jogos da Steam", 12, 312, 180, 32);
    games.BackColor = Color.FromArgb(48, 40, 92);
    games.Click += (s, e) => AddSteamGames();
    Controls.AddRange(new Control[] { add, ren, up, down, del, games });

    Controls.Add(Theme.Lbl("Opcoes", 12, 352, true));
    skinBox = new ComboBox { Location = new Point(12, 380), Size = new Size(180, 24), DropDownStyle = ComboBoxStyle.DropDownList };
    skinBox.Items.AddRange(new object[] { "Visual: Padrao", "Visual: Matrix", "Visual: Pixel art", "Visual: Cyberpunk", "Visual: Digivice", "Visual: Vidro", "Visual: Chuva vermelha" });
    Theme.Field_(skinBox);
    skinBox.SelectedIndexChanged += (s, e) => {
      if (loading) return;
      doc.Skin = KeyArt.SkinNames[skinBox.SelectedIndex];
      KeyArt.Skin = doc.Skin;
      statusLbl.Text = "Redesenhando as teclas...";
      statusLbl.Refresh();
      foreach (var p in doc.Pages) foreach (var b in p.Buttons) if (b != null) doc.Prepare(b);
      RefreshTiles();
      UpdateIconPic();
      Changed(false);
    };
    Controls.Add(skinBox);
    labelsChk = new CheckBox { Text = "Mostrar nomes nas teclas", Location = new Point(12, 414), AutoSize = true, ForeColor = Theme.Text };
    soundChk = new CheckBox { Text = "Som ao tocar", Location = new Point(12, 438), AutoSize = true, ForeColor = Theme.Text };
    shineChk = new CheckBox { Text = "Brilho passando nas teclas", Location = new Point(12, 462), AutoSize = true, ForeColor = Theme.Text };
    Controls.Add(Theme.Lbl("Apagar a tela apos (min):", 12, 492, false));
    dimNum = new NumericUpDown { Location = new Point(140, 489), Size = new Size(56, 24), TextAlign = HorizontalAlignment.Center, Minimum = 0, Maximum = 120 };
    Theme.Field_(dimNum);
    EventHandler opt = (s, e) => {
      if (loading) return;
      bool relabel = doc.Labels != labelsChk.Checked;
      doc.Labels = labelsChk.Checked;
      doc.Sound = soundChk.Checked;
      doc.IdleShine = shineChk.Checked;
      doc.DimMinutes = (int)dimNum.Value;
      if (relabel) foreach (var p in doc.Pages) foreach (var b in p.Buttons) if (b != null) doc.Prepare(b);
      Changed(relabel);
    };
    labelsChk.CheckedChanged += opt;
    soundChk.CheckedChanged += opt;
    shineChk.CheckedChanged += opt;
    dimNum.ValueChanged += opt;
    Controls.AddRange(new Control[] { labelsChk, soundChk, shineChk, dimNum });

    var obsBtn = Theme.Btn("OBS...", 12, 522, 56, 26);
    var dcBtn = Theme.Btn("Discord...", 72, 522, 66, 26);
    dcBtn.Click += (s, e) => DiscordSettings();
    Controls.Add(dcBtn);
    obsBtn.Click += (s, e) => ObsSettings();
    Controls.Add(obsBtn);
    var txt = Theme.Btn("Arquivo", 142, 522, 50, 26);
    txt.Click += (s, e) => { if (saveTimer.Enabled) { saveTimer.Stop(); Save(); } Process.Start("notepad.exe", "\"" + app.ConfigPath + "\""); };
    Controls.Add(txt);
  }

  void BuildGrid() {
    Controls.Add(Theme.Lbl("Teclas", 210, 10, true));
    Controls.Add(Theme.Lbl("clique para editar  \u2022  arraste para trocar de lugar  \u2022  botao direito: copiar/colar", 268, 14, false));
    for (int i = 0; i < 12; i++) {
      var t = new KeyTile { Index = i, Location = new Point(210 + (i % 4) * 162, 38 + (i / 4) * 146), Size = new Size(156, 140) };
      int idx = i;
      Point down = Point.Empty;
      t.MouseDown += (s, e) => {
        down = e.Location;
        if (e.Button == MouseButtons.Left) { Select(idx); PlayAnim(idx); }
        if (e.Button == MouseButtons.Right) { Select(idx); TileMenu(idx).Show(t, e.Location); }
      };
      t.MouseMove += (s, e) => {
        if (e.Button == MouseButtons.Left && Cur(idx) != null && (Math.Abs(e.X - down.X) > 8 || Math.Abs(e.Y - down.Y) > 8))
          t.DoDragDrop(idx.ToString(), DragDropEffects.Move);
      };
      t.DragEnter += (s, e) => { e.Effect = e.Data.GetDataPresent(DataFormats.Text) ? DragDropEffects.Move : DragDropEffects.None; t.DropTarget = true; t.Invalidate(); };
      t.DragLeave += (s, e) => { t.DropTarget = false; t.Invalidate(); };
      t.DragDrop += (s, e) => {
        t.DropTarget = false;
        int from;
        if (!int.TryParse((string)e.Data.GetData(DataFormats.Text), out from) || from == idx) { t.Invalidate(); return; }
        var bs = doc.Pages[pageIdx].Buttons;
        var tmp = bs[from]; bs[from] = bs[idx]; bs[idx] = tmp;
        sel = idx;
        RefreshTiles();
        ShowProps();
        Changed(false);
      };
      tiles[i] = t;
      Controls.Add(t);
    }
  }

  ContextMenuStrip TileMenu(int idx) {
    var m = new ContextMenuStrip();
    m.Items.Add("Copiar", null, (s, e) => { if (Cur(idx) != null) clip = Cur(idx).Copy(); }).Enabled = Cur(idx) != null;
    m.Items.Add("Colar", null, (s, e) => {
      var b = clip.Copy();
      doc.Prepare(b);
      doc.Pages[pageIdx].Buttons[idx] = b;
      RefreshTiles(); ShowProps(); Changed(false);
    }).Enabled = clip != null;
    m.Items.Add("Limpar", null, (s, e) => ClearKey(idx)).Enabled = Cur(idx) != null;
    return m;
  }

  void BuildProps() {
    propsPanel = new Panel { Location = new Point(866, 0), Size = new Size(324, 584), BackColor = Theme.Panel };
    Controls.Add(propsPanel);
    var p = propsPanel;
    titleLbl = Theme.Lbl("Tecla", 14, 10, true);
    p.Controls.Add(titleLbl);
    emptyHint = Theme.Lbl("Clique numa tecla para editar.\nNuma tecla vazia, escolha o que ela vai fazer.", 14, 40, false);
    p.Controls.Add(emptyHint);

    typeLbl = Theme.Lbl("O que faz", 14, 82, false);
    p.Controls.Add(typeLbl);
    typeBox = new ComboBox { Location = new Point(14, 102), Size = new Size(294, 24), DropDownStyle = ComboBoxStyle.DropDownList };
    foreach (int code in Order) typeBox.Items.Add(Types[code]);
    Theme.Field_(typeBox);
    typeBox.SelectedIndexChanged += (s, e) => { if (!loading) TypeChanged(); };
    p.Controls.Add(typeBox);

    valuePanel = new Panel { Location = new Point(14, 132), Size = new Size(296, 84) };
    p.Controls.Add(valuePanel);

    p.Controls.Add(Theme.Lbl("Nome (aparece no editor e com 'mostrar nomes')", 14, 218, false));
    nameBox = new TextBox { Location = new Point(14, 238), Size = new Size(294, 24) };
    Theme.Field_(nameBox);
    nameBox.TextChanged += (s, e) => {
      if (loading || Cur(sel) == null) return;
      Cur(sel).Label = nameBox.Text;
      autoName = "";
      if (doc.Labels) Redraw();
      else Changed(false);
    };
    p.Controls.Add(nameBox);

    p.Controls.Add(Theme.Lbl("Cor", 14, 270, false));
    colorFlow = new FlowLayoutPanel { Location = new Point(12, 288), Size = new Size(300, 64) };
    AddSwatch("", Color.FromArgb(70, 72, 86), "Automatica");
    foreach (var kv in DeckConfig.Colors) AddSwatch(kv.Key, KeyArt.Hex(kv.Value), kv.Key);
    AddSwatch("?", Theme.Field, "Outra cor...");
    p.Controls.Add(colorFlow);

    p.Controls.Add(Theme.Lbl("Icone", 14, 358, false));
    iconPic = new PictureBox { Location = new Point(14, 378), Size = new Size(76, 68), SizeMode = PictureBoxSizeMode.Zoom };
    var pickIcon = Theme.Btn("Escolher icone...", 100, 378, 208, 30);
    pickIcon.Click += (s, e) => PickIcon();
    var autoIcon = Theme.Btn("Automatico", 100, 414, 100, 28);
    autoIcon.Click += (s, e) => { if (Cur(sel) != null) { Cur(sel).IconRaw = ""; Redraw(); } };
    var imgIcon = Theme.Btn("Imagem do PC", 206, 414, 102, 28);
    imgIcon.Click += (s, e) => {
      if (Cur(sel) == null) return;
      using (var d = new OpenFileDialog { Filter = "Imagens e icones|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico;*.exe|Todos|*.*" })
        if (d.ShowDialog(this) == DialogResult.OK) { Cur(sel).IconRaw = d.FileName; Redraw(); }
    };
    p.Controls.AddRange(new Control[] { iconPic, pickIcon, autoIcon, imgIcon });

    p.Controls.Add(Theme.Lbl("Animacao ao tocar", 14, 452, false));
    animBox = new ComboBox { Location = new Point(14, 472), Size = new Size(294, 24), DropDownStyle = ComboBoxStyle.DropDownList };
    animBox.Items.AddRange(AnimLabels);
    Theme.Field_(animBox);
    animBox.SelectedIndexChanged += (s, e) => {
      if (loading || Cur(sel) == null) return;
      Cur(sel).AnimRaw = animBox.SelectedIndex <= 0 ? "" : KeyArt.AnimNames[animBox.SelectedIndex];
      Redraw();
      PlayAnim(sel);
    };
    var test = Theme.Btn("Testar no PC", 14, 512, 144, 30);
    test.Click += (s, e) => { if (Cur(sel) != null) app.RunAction(Cur(sel).Action); };
    var clear = Theme.Btn("Limpar tecla", 164, 512, 144, 30);
    clear.Click += (s, e) => ClearKey(sel);
    p.Controls.AddRange(new Control[] { animBox, test, clear });
  }

  void AddSwatch(string name, Color c, string tip) {
    var b = new Button { Size = new Size(30, 26), Margin = new Padding(2), FlatStyle = FlatStyle.Flat, BackColor = c, Tag = name, Cursor = Cursors.Hand };
    b.FlatAppearance.BorderColor = Theme.Panel;
    b.FlatAppearance.BorderSize = 2;
    if (name == "") { b.Text = "A"; b.ForeColor = Theme.Text; }
    if (name == "?") { b.Text = "..."; b.ForeColor = Theme.Text; }
    new ToolTip().SetToolTip(b, tip);
    b.Click += (s, e) => {
      var k = Cur(sel);
      if (k == null) return;
      if (name == "?") {
        using (var d = new ColorDialog { FullOpen = true, Color = KeyArt.Hex(k.ColorHex) }) {
          if (d.ShowDialog(this) != DialogResult.OK) return;
          k.ColorRaw = (d.Color.R.ToString("X2") + d.Color.G.ToString("X2") + d.Color.B.ToString("X2"));
        }
      } else {
        k.ColorRaw = name;
      }
      Redraw();
    };
    colorFlow.Controls.Add(b);
  }

  // ---------------------------- dados ---------------------------------------
  DeckButton Cur(int i) {
    if (doc == null || pageIdx >= doc.Pages.Count || i < 0 || i >= 12) return null;
    return doc.Pages[pageIdx].Buttons[i];
  }

  void Reload() {
    try { doc = DeckConfig.Load(app.ConfigPath); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Erro em botoes.txt"); doc = new DeckConfig(); }
    if (doc.Pages.Count == 0) doc.Pages.Add(new DeckPage { Name = "Inicio" });
    loading = true;
    labelsChk.Checked = doc.Labels;
    soundChk.Checked = doc.Sound;
    shineChk.Checked = doc.IdleShine;
    skinBox.SelectedIndex = KeyArt.SkinId(doc.Skin);
    dimNum.Value = Math.Max(0, Math.Min(120, doc.DimMinutes));
    loading = false;
  }

  void RefreshPages() {
    loading = true;
    pageList.Items.Clear();
    foreach (var p in doc.Pages) pageList.Items.Add(p.Name);
    if (pageIdx >= doc.Pages.Count) pageIdx = doc.Pages.Count - 1;
    pageList.SelectedIndex = pageIdx;
    loading = false;
    RefreshTiles();
    ShowProps();
  }

  void RefreshTiles() {
    for (int i = 0; i < 12; i++) {
      var t = tiles[i];
      var b = Cur(i);
      if (t.Image != null) t.Image.Dispose();
      t.Image = b == null ? null : KeyArt.Render(b, doc.Labels, null, 2);
      t.Frames = null;
      t.Selected = i == sel;
      t.Invalidate();
    }
  }

  void Select(int i) {
    sel = i;
    for (int k = 0; k < 12; k++) { tiles[k].Selected = k == i; tiles[k].Invalidate(); }
    ShowProps();
  }

  void ShowProps() {
    loading = true;
    var b = Cur(sel);
    bool has = b != null;
    emptyHint.Visible = !has;
    emptyHint.Text = sel < 0 ? "Clique numa tecla para editar." : "Tecla vazia: escolha abaixo o que ela vai fazer.";
    typeBox.Enabled = sel >= 0;
    // sem tecla escolhida, so aparece o "O que faz"
    foreach (Control c in propsPanel.Controls)
      if (c != emptyHint && c != typeBox && c != typeLbl && c != titleLbl) c.Visible = has;
    typeBox.Visible = typeLbl.Visible = sel >= 0;
    if (!has) {
      typeBox.SelectedIndex = -1;
      valuePanel.Controls.Clear();
      nameBox.Text = "";
      iconPic.Image = null;
      animBox.SelectedIndex = -1;
      loading = false;
      return;
    }
    nameBox.Text = b.Label;
    int type = TypeOf(b.Action);
    typeBox.SelectedIndex = Array.IndexOf(Order, type);
    BuildValue(type, b.Action);
    string an = DeckConfig.Norm(b.AnimRaw);
    animBox.SelectedIndex = Math.Max(0, Array.IndexOf(KeyArt.AnimNames, an == "" ? "auto" : an));
    UpdateIconPic();
    MarkSwatch();
    loading = false;
  }

  void UpdateIconPic() {
    var b = Cur(sel);
    if (iconPic.Image != null) iconPic.Image.Dispose();
    iconPic.Image = b == null ? null : KeyArt.Render(b, false, null, 1);
  }

  void MarkSwatch() {
    var b = Cur(sel);
    string cur = b == null ? null : DeckConfig.Norm(b.ColorRaw);
    foreach (Control c in colorFlow.Controls) {
      var bt = (Button)c;
      string n = (string)bt.Tag;
      bool on = cur != null && (n == cur || (n == "?" && cur != "" && !DeckConfig.Colors.ContainsKey(cur)));
      bt.FlatAppearance.BorderColor = on ? Color.White : Theme.Panel;
    }
  }

  static int TypeOf(string action) {
    string a = DeckConfig.Norm(action);
    if (action.Contains(" ; ")) return 9;
    if (Array.IndexOf(MediaActions, a) >= 0) return 0;
    if (a.StartsWith("discord:")) return 12;
    if (a.StartsWith("obs:")) return 10;
    if (a.StartsWith("abrir:steam://")) return 11;
    if (a.StartsWith("abrir:http") || a.StartsWith("abrir:www.")) return 3;
    if (a.StartsWith("abrir:")) return 1;
    if (a.StartsWith("jogo:")) return 2;
    if (a.StartsWith("atalho:")) return 4;
    if (a.StartsWith("digitar:")) return 5;
    if (a.StartsWith("microfone")) return 6;
    if (a.StartsWith("pagina:")) return 7;
    if (a.StartsWith("comando:")) return 8;
    return 9;
  }

  static string ArgOf(string action) {
    int c = action.IndexOf(':');
    return c >= 0 ? action.Substring(c + 1).Trim() : "";
  }

  // ---------------------------- valor da acao --------------------------------
  void BuildValue(int type, string action) {
    valuePanel.Controls.Clear();
    valueCtl = null;
    string arg = ArgOf(action);
    switch (type) {
      case 0: {
        var cb = Combo(0, 0, 294);
        cb.Items.AddRange(MediaNames);
        cb.SelectedIndex = Math.Max(0, Array.IndexOf(MediaActions, DeckConfig.Norm(action)));
        cb.SelectedIndexChanged += (s, e) => SetAction(MediaActions[cb.SelectedIndex], MediaNames[cb.SelectedIndex]);
        break;
      }
      case 1: {
        var tb = Text_(0, 0, 294, arg);
        var browse = Theme.Btn("Programa...", 0, 30, 96, 26);
        var folder = Theme.Btn("Pasta...", 100, 30, 70, 26);
        var list = Combo(174, 31, 120);
        list.Items.Add("Menu Iniciar...");
        var links = StartMenuLinks();
        foreach (var l in links) list.Items.Add(Path.GetFileNameWithoutExtension(l));
        list.SelectedIndex = 0;
        list.DropDownWidth = 280;
        tb.Leave += (s, e) => SetAction("abrir:" + tb.Text.Trim(), Path.GetFileNameWithoutExtension(tb.Text.Trim()));
        browse.Click += (s, e) => {
          using (var d = new OpenFileDialog { Filter = "Programas e atalhos|*.exe;*.lnk;*.bat;*.url|Todos os arquivos|*.*" })
            if (d.ShowDialog(this) == DialogResult.OK) { tb.Text = d.FileName; SetAction("abrir:" + d.FileName, Path.GetFileNameWithoutExtension(d.FileName)); }
        };
        folder.Click += (s, e) => {
          using (var d = new FolderBrowserDialog())
            if (d.ShowDialog(this) == DialogResult.OK) { tb.Text = d.SelectedPath; SetAction("abrir:" + d.SelectedPath, Path.GetFileName(d.SelectedPath)); }
        };
        list.SelectedIndexChanged += (s, e) => {
          if (list.SelectedIndex <= 0) return;
          string f = links[list.SelectedIndex - 1];
          tb.Text = f;
          SetAction("abrir:" + f, Path.GetFileNameWithoutExtension(f));
        };
        valuePanel.Controls.AddRange(new Control[] { browse, folder });
        break;
      }
      case 2: {
        var cb = Combo(0, 0, 294);
        var games = SteamLib.Games();
        foreach (var g in games) cb.Items.Add(g);
        string id = arg;
        int at = games.FindIndex(g => g.AppId == id);
        if (at >= 0) cb.SelectedIndex = at;
        else if (games.Count == 0) valuePanel.Controls.Add(Theme.Lbl("Nenhum jogo da Steam encontrado.", 0, 30, false));
        cb.SelectedIndexChanged += (s, e) => {
          var g = (SteamGame)cb.SelectedItem;
          SetAction("jogo:" + g.AppId, g.Name);
        };
        break;
      }
      case 3: {
        var tb = Text_(0, 0, 294, arg == "" ? "https://" : arg);
        valuePanel.Controls.Add(Theme.Lbl("Endereco do site. Enter para aplicar.", 0, 28, false));
        Action apply = () => {
          string u = tb.Text.Trim();
          string host = u;
          try { host = new Uri(u.StartsWith("http") ? u : "https://" + u).Host.Replace("www.", ""); } catch { }
          SetAction("abrir:" + u, host);
        };
        tb.Leave += (s, e) => apply();
        tb.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { apply(); e.SuppressKeyPress = true; } };
        break;
      }
      case 4: {
        var tb = Text_(0, 0, 294, arg);
        tb.ReadOnly = true;
        tb.Cursor = Cursors.Hand;
        valuePanel.Controls.Add(Theme.Lbl("Clique na caixa e aperte as teclas juntas.", 0, 26, false));
        var common = Combo(0, 50, 294);
        common.Items.Add("Atalhos comuns...");
        foreach (string c in CommonKeys) common.Items.Add(c.Split('|')[0] + "   (" + c.Split('|')[1] + ")");
        common.SelectedIndex = 0;
        common.SelectedIndexChanged += (s, e) => {
          if (common.SelectedIndex <= 0) return;
          string[] c = CommonKeys[common.SelectedIndex - 1].Split('|');
          tb.Text = c[1];
          SetAction("atalho:" + c[1], c[0]);
        };
        tb.KeyDown += (s, e) => {
          e.SuppressKeyPress = true;
          e.Handled = true;
          string key = KeyName(e.KeyCode);
          if (key == null) return;  // so modificador por enquanto
          var parts = new List<string>();
          if (e.Control) parts.Add("ctrl");
          if (e.Shift) parts.Add("shift");
          if (e.Alt) parts.Add("alt");
          if ((Control.ModifierKeys & Keys.LWin) != 0 || WinDown()) parts.Add("win");
          parts.Add(key);
          string combo = string.Join("+", parts.ToArray());
          tb.Text = combo;
          SetAction("atalho:" + combo, combo);
        };
        break;
      }
      case 5: {
        var tb = Text_(0, 0, 294, arg);
        tb.Multiline = true;
        tb.Height = 56;
        valuePanel.Controls.Add(Theme.Lbl("Texto que sera digitado (\\n = Enter).", 0, 60, false));
        tb.Leave += (s, e) => SetAction("digitar:" + tb.Text.Replace("\r\n", "\\n").Replace("\n", "\\n"), "Texto");
        break;
      }
      case 6:
        valuePanel.Controls.Add(Theme.Lbl("Muta e desmuta o microfone em todos os\nprogramas. A tecla fica vermelha quando mudo.", 0, 0, false));
        break;
      case 7: {
        var cb = Combo(0, 0, 294);
        cb.Items.Add("Proxima pagina");
        cb.Items.Add("Pagina anterior");
        foreach (var p in doc.Pages) cb.Items.Add(p.Name);
        string a = DeckConfig.Norm(arg);
        int at = a == "+" ? 0 : a == "-" ? 1 : 2 + doc.Pages.FindIndex(p => DeckConfig.Norm(p.Name) == a);
        cb.SelectedIndex = Math.Max(0, at);
        cb.SelectedIndexChanged += (s, e) => {
          int i = cb.SelectedIndex;
          string target = i == 0 ? "+" : i == 1 ? "-" : doc.Pages[i - 2].Name;
          SetAction("pagina:" + target, i == 0 ? "Proxima" : i == 1 ? "Anterior" : target);
        };
        break;
      }
      case 8: {
        var tb = Text_(0, 0, 294, arg);
        valuePanel.Controls.Add(Theme.Lbl("Roda no Prompt de Comando. Enter para aplicar.", 0, 28, false));
        tb.Leave += (s, e) => SetAction("comando:" + tb.Text.Trim(), "Comando");
        tb.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { SetAction("comando:" + tb.Text.Trim(), "Comando"); e.SuppressKeyPress = true; } };
        break;
      }
      case 10: {
        // OBS: o que fazer + (cena ou fonte, quando precisa)
        var cmd = Combo(0, 0, 294);
        cmd.Items.AddRange(ObsNames);
        var name = Combo(0, 30, 294);
        var info = Theme.Lbl("", 0, 60, false);
        info.ForeColor = Obs.Connected ? Color.FromArgb(90, 220, 140) : Color.FromArgb(240, 170, 80);
        info.Text = Obs.Connected ? "OBS conectado" : (Obs.LastError != "" ? Obs.LastError : "Abra o OBS com o servidor WebSocket ativo");
        valuePanel.Controls.Add(info);
        string a = DeckConfig.Norm(arg);
        int at = 0;
        for (int k = ObsCmds.Length - 1; k >= 0; k--)
          if (a == ObsCmds[k] || (ObsCmds[k].EndsWith(":") && a.StartsWith(ObsCmds[k]) && a != "cena:+" && a != "cena:-")) { at = k; break; }
        string current = ObsCmds[at].EndsWith(":") ? arg.Substring(arg.IndexOf(':') + 1).Trim() : "";
        Action fillNames = () => {
          name.Items.Clear();
          string c = ObsCmds[cmd.SelectedIndex];
          name.Visible = c.EndsWith(":");
          if (!name.Visible) return;
          var st = Obs.State;
          var list = c == "cena:" ? (st != null ? st.Scenes : Obs.SceneNamesOffline()) : Obs.SourceNamesOffline();
          foreach (string n in list) name.Items.Add(n);
          if (current != "" && !name.Items.Contains(current)) name.Items.Insert(0, current);
          name.SelectedIndex = name.Items.Count == 0 ? -1 : Math.Max(0, name.Items.IndexOf(current));
        };
        Action apply = () => {
          string c = ObsCmds[cmd.SelectedIndex];
          if (c.EndsWith(":")) {
            if (name.SelectedItem == null) return;
            string n = (string)name.SelectedItem;
            SetAction("obs:" + c + n, n);
          } else {
            SetAction("obs:" + c, ObsShort[cmd.SelectedIndex]);
          }
        };
        cmd.SelectedIndex = at;
        fillNames();
        cmd.SelectedIndexChanged += (s, e) => { current = ""; fillNames(); apply(); };
        name.SelectedIndexChanged += (s, e) => { if (name.SelectedItem != null) current = (string)name.SelectedItem; apply(); };
        break;
      }
      case 12: {
        // discord:meumic / discord:meusom (o seu mudo) ou discord:mutar:fulano / discord:ensurdecer:fulano (bot)
        string[] parts = arg.Split(':');
        string p0 = DeckConfig.Norm(parts[0]);
        var what = Combo(0, 0, 294);
        what.Items.AddRange(new object[] { "Meu microfone (mudo no Discord)", "Me ensurdecer no Discord", "Mutar outra pessoa no servidor", "Ensurdecer outra pessoa no servidor" });
        what.SelectedIndex = p0 == "meusom" ? 1 : p0 == "mutar" ? 2 : p0 == "ensurdecer" ? 3 : 0;
        var who = Text_(0, 28, 294, parts.Length > 1 ? parts[1] : "");
        var whoLbl = Theme.Lbl("Nome de usuario, apelido ou ID da pessoa.", 0, 52, false);
        valuePanel.Controls.Add(whoLbl);
        var info = Theme.Lbl("", 0, 68, false);
        valuePanel.Controls.Add(info);
        Action showInfo = () => {
          bool own = what.SelectedIndex < 2;
          who.Visible = whoLbl.Visible = !own;
          bool ok = own || Discord.HasToken;
          info.Location = new Point(0, own ? 28 : 68);
          info.Text = own ? "Pronto, nao precisa configurar nada. Funciona\ncom o Discord minimizado e nao muta a live."
                          : (ok ? "Bot configurado. A tecla fica vermelha com ele mudo." : "Configure o bot: Discord... > Bot para mutar outra pessoa");
          info.ForeColor = ok ? Color.FromArgb(90, 220, 140) : Color.FromArgb(240, 170, 80);
        };
        showInfo();
        Action apply = () => {
          showInfo();
          if (what.SelectedIndex == 0) { SetAction("discord:meumic", "Meu microfone"); return; }
          if (what.SelectedIndex == 1) { SetAction("discord:meusom", "Ensurdecer"); return; }
          string n = who.Text.Trim().TrimStart('@');
          if (n == "") return;
          SetAction("discord:" + (what.SelectedIndex == 3 ? "ensurdecer" : "mutar") + ":" + n, (what.SelectedIndex == 3 ? "Ensurdecer " : "Mutar ") + n);
        };
        what.SelectedIndexChanged += (s, e) => apply();
        who.Leave += (s, e) => apply();
        who.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { apply(); e.SuppressKeyPress = true; } };
        valueCtl = who;
        break;
      }
      case 11: {
        var cb = Combo(0, 0, 294);
        cb.Items.AddRange(SteamNames);
        cb.SelectedIndex = Math.Max(0, Array.IndexOf(SteamUrls, arg));
        cb.SelectedIndexChanged += (s, e) => SetAction("abrir:" + SteamUrls[cb.SelectedIndex], SteamNames[cb.SelectedIndex]);
        valuePanel.Controls.Add(Theme.Lbl("Abre essa parte da Steam.", 0, 30, false));
        break;
      }
      default: {
        var tb = Text_(0, 0, 294, action);
        tb.Multiline = true;
        tb.Height = 50;
        valuePanel.Controls.Add(Theme.Lbl("Acoes como no botoes.txt (varias: separe com ' ; ').", 0, 54, false));
        tb.Leave += (s, e) => SetAction(tb.Text.Trim(), null);
        break;
      }
    }
  }

  ComboBox Combo(int x, int y, int w) {
    var cb = new ComboBox { Location = new Point(x, y), Width = w, DropDownStyle = ComboBoxStyle.DropDownList };
    Theme.Field_(cb);
    valuePanel.Controls.Add(cb);
    if (valueCtl == null) valueCtl = cb;
    return cb;
  }

  TextBox Text_(int x, int y, int w, string text) {
    var tb = new TextBox { Location = new Point(x, y), Width = w, Text = text };
    Theme.Field_(tb);
    valuePanel.Controls.Add(tb);
    if (valueCtl == null) valueCtl = tb;
    return tb;
  }

  [System.Runtime.InteropServices.DllImport("user32.dll")] static extern short GetKeyState(int vk);
  static bool WinDown() { return (GetKeyState(0x5B) & 0x8000) != 0 || (GetKeyState(0x5C) & 0x8000) != 0; }

  static string KeyName(Keys k) {
    if (k >= Keys.A && k <= Keys.Z) return k.ToString().ToLower();
    if (k >= Keys.D0 && k <= Keys.D9) return ((int)(k - Keys.D0)).ToString();
    if (k >= Keys.F1 && k <= Keys.F24) return "f" + (k - Keys.F1 + 1);
    switch (k) {
      case Keys.Enter: return "enter";
      case Keys.Escape: return "esc";
      case Keys.Tab: return "tab";
      case Keys.Space: return "espaco";
      case Keys.Back: return "backspace";
      case Keys.Delete: return "del";
      case Keys.Insert: return "insert";
      case Keys.Home: return "home";
      case Keys.End: return "end";
      case Keys.PageUp: return "pgup";
      case Keys.PageDown: return "pgdn";
      case Keys.Up: return "cima";
      case Keys.Down: return "baixo";
      case Keys.Left: return "esquerda";
      case Keys.Right: return "direita";
      case Keys.PrintScreen: return "print";
      case Keys.OemMinus: return "menos";
      case Keys.Oemplus: return "mais";
      case Keys.Oemcomma: return "virgula";
      case Keys.OemPeriod: return "ponto";
    }
    return null;
  }

  static List<string> startLinks;
  static List<string> StartMenuLinks() {
    if (startLinks != null) return startLinks;
    var list = new List<string>();
    foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) }) {
      try {
        foreach (string f in Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories)) {
          string n = Path.GetFileNameWithoutExtension(f);
          if (System.Text.RegularExpressions.Regex.IsMatch(n, "uninstall|desinstal|readme|leia-me|help|ajuda", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
          if (!list.Exists(x => Path.GetFileNameWithoutExtension(x) == n)) list.Add(f);
        }
      } catch { }
    }
    list.Sort((a, b) => string.Compare(Path.GetFileNameWithoutExtension(a), Path.GetFileNameWithoutExtension(b), StringComparison.OrdinalIgnoreCase));
    startLinks = list;
    return list;
  }

  void TypeChanged() {
    if (sel < 0) return;
    var b = Cur(sel);
    int type = Order[typeBox.SelectedIndex];
    if (b == null) {
      b = new DeckButton();
      doc.Pages[pageIdx].Buttons[sel] = b;
    }
    // acao padrao do tipo escolhido
    string def;
    string name;
    switch (type) {
      case 0: def = "midia:play"; name = "Play / Pause"; break;
      case 1: def = "abrir:"; name = ""; break;
      case 2: {
        var g = SteamLib.Games();
        def = g.Count > 0 ? "jogo:" + g[0].AppId : "jogo:";
        name = g.Count > 0 ? g[0].Name : "Jogo";
        break;
      }
      case 3: def = "abrir:https://"; name = "Site"; break;
      case 4: def = "atalho:ctrl+c"; name = "Copiar"; break;
      case 5: def = "digitar:"; name = "Texto"; break;
      case 6: def = "microfone"; name = "Microfone"; break;
      case 7: def = "pagina:+"; name = "Proxima"; break;
      case 8: def = "comando:"; name = "Comando"; break;
      case 10: def = "obs:cena:+"; name = "Proxima cena"; break;
      case 11: def = "abrir:steam://open/games"; name = "Biblioteca"; break;
      case 12: def = "discord:meumic"; name = "Meu microfone"; break;
      default: def = b.Action; name = null; break;
    }
    if (b.Label == "" || b.Label == autoName) { b.Label = name ?? b.Label; autoName = b.Label; }
    b.Action = def;
    b.IconRaw = "";
    ShowProps();
    Redraw();
    if (valueCtl != null) valueCtl.Focus();
  }

  void SetAction(string action, string suggestedName) {
    var b = Cur(sel);
    if (b == null || loading) return;
    if (b.Action == action) return;
    b.Action = action;
    // nome automatico, a nao ser que a pessoa tenha escrito um
    if (suggestedName != null && suggestedName != "" && (b.Label == "" || b.Label == autoName || IsDefaultName(b.Label))) {
      b.Label = suggestedName;
      autoName = suggestedName;
      loading = true;
      nameBox.Text = suggestedName;
      loading = false;
    }
    Redraw();
  }

  static bool IsDefaultName(string n) {
    foreach (string m in MediaNames) if (m == n) return true;
    return n == "Site" || n == "Texto" || n == "Comando" || n == "Jogo" || n == "Proxima" || n == "Anterior";
  }

  void ClearKey(int i) {
    if (Cur(i) == null) return;
    doc.Pages[pageIdx].Buttons[i] = null;
    RefreshTiles();
    ShowProps();
    Changed(false);
  }

  // redesenha a tecla selecionada e salva
  void Redraw() {
    var b = Cur(sel);
    if (b == null) return;
    doc.Warnings.Clear();
    doc.Prepare(b);
    var t = tiles[sel];
    if (t.Image != null) t.Image.Dispose();
    t.Image = KeyArt.Render(b, doc.Labels, null, 2);
    t.Frames = null;
    t.Invalidate();
    UpdateIconPic();
    MarkSwatch();
    if (doc.Warnings.Count > 0) statusLbl.Text = doc.Warnings[0];
    Changed(false);
  }

  void Changed(bool pagesToo) {
    if (pagesToo) RefreshPages();
    statusLbl.Text = "Salvando...";
    saveTimer.Stop();
    saveTimer.Start();
  }

  void Save() {
    try {
      doc.Save(app.ConfigPath);
      statusLbl.Text = "Salvo \u2713  o painel atualiza em instantes";
    } catch (Exception ex) {
      statusLbl.Text = "Nao consegui salvar: " + ex.Message;
    }
  }

  void MovePage(int d) {
    int to = pageIdx + d;
    if (to < 0 || to >= doc.Pages.Count) return;
    var p = doc.Pages[pageIdx];
    doc.Pages.RemoveAt(pageIdx);
    doc.Pages.Insert(to, p);
    pageIdx = to;
    Changed(true);
  }

  void AddSteamGames() {
    var games = SteamLib.Games();
    if (games.Count == 0) { MessageBox.Show(this, "Nao encontrei jogos da Steam instalados.", "Steam"); return; }
    int at = doc.Pages.FindIndex(p => DeckConfig.Norm(p.Name) == "jogos");
    DeckPage page;
    if (at < 0) {
      page = new DeckPage { Name = "Jogos" };
      doc.Pages.Add(page);
      at = doc.Pages.Count - 1;
    } else {
      page = doc.Pages[at];
    }
    int added = 0;
    foreach (var g in games) {
      bool exists = false;
      foreach (var p in doc.Pages)
        foreach (var b in p.Buttons)
          if (b != null && DeckConfig.Norm(b.Action) == "jogo:" + g.AppId) exists = true;
      if (exists) continue;
      int slot = Array.IndexOf(page.Buttons, null);
      if (slot < 0) break;
      var nb = new DeckButton { Label = g.Name, Action = "jogo:" + g.AppId };
      doc.Prepare(nb);
      page.Buttons[slot] = nb;
      added++;
    }
    pageIdx = at;
    Changed(true);
    app.ShowPage(at);
    statusLbl.Text = added > 0 ? added + " jogos adicionados na pagina Jogos" : "Os jogos instalados ja estao nas teclas";
  }

  // seu Discord (mudo/ensurdecer de verdade): aplicativo seu, conexao local (DiscordRpc)
  void DiscordSettings() {
    using (var f = new Form { Text = "Discord", ClientSize = new Size(500, 390), FormBorderStyle = FormBorderStyle.FixedDialog,
                              StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false, BackColor = Theme.Panel, ForeColor = Theme.Text }) {
      var help = Theme.Lbl(
        "Para as teclas Meu microfone e Ensurdecer usarem o mudo do proprio\n" +
        "Discord (o icone acende, a live continua com sua voz), uma vez so:\n\n" +
        "1. Abra o Portal de Desenvolvedores (botao abaixo) > New Application,\n" +
        "    de um nome (ex.: Deck Station) e crie. Pode usar o mesmo do bot.\n" +
        "2. Aba OAuth2: copie o Client ID; em Client Secret clique em Reset\n" +
        "    Secret e copie. Em Redirects, adicione  http://localhost  e salve.\n" +
        "3. Cole os dois aqui e clique em Conectar. O Discord abre uma janela\n" +
        "    pedindo autorizacao: clique em Autorizar.", 12, 10, false);
      var portal = Theme.Btn("Abrir o Portal de Desenvolvedores", 12, 178, 230, 28);
      portal.Click += (s, e) => System.Diagnostics.Process.Start("https://discord.com/developers/applications");
      var li = Theme.Lbl("Client ID:", 12, 224, false);
      var id = new TextBox { Location = new Point(110, 221), Width = 378, Text = DiscordRpc.ClientId };
      Theme.Field_(id);
      var ls = Theme.Lbl("Client Secret:", 12, 256, false);
      var sec = new TextBox { Location = new Point(110, 253), Width = 378, UseSystemPasswordChar = true };
      Theme.Field_(sec);
      var status = Theme.Lbl(DiscordRpc.Configured ? "Ja configurado (deixe o Secret vazio para manter)." : "Ainda nao configurado.", 12, 288, false);
      var connect = Theme.Btn("Conectar", 12, 346, 90, 28);
      connect.Click += (s, e) => {
        status.Text = "Conectando... se o Discord abrir uma janela, clique em Autorizar.";
        status.Refresh();
        Cursor = Cursors.WaitCursor;
        try {
          DiscordRpc.Setup(id.Text, sec.Text);
          status.Text = "Funcionando! Agora " + (DiscordRpc.Muted ? "voce esta MUDO" : "seu microfone esta ligado") + " no Discord.";
        } catch (Exception ex) {
          status.Text = ex.Message;
        } finally { Cursor = Cursors.Default; }
      };
      var bot = Theme.Btn("Bot para mutar outra pessoa...", 108, 346, 210, 28);
      bot.Click += (s, e) => BotSettings();
      var close = Theme.Btn("Fechar", 413, 346, 75, 28);
      close.DialogResult = DialogResult.Cancel;
      f.Controls.AddRange(new Control[] { help, portal, li, id, ls, sec, status, connect, bot, close });
      f.CancelButton = close;
      f.ShowDialog(this);
    }
  }

  // bot do Discord: token guardado criptografado (discord-token.dat)
  void BotSettings() {
    using (var f = new Form { Text = "Bot do Discord", ClientSize = new Size(470, 330), FormBorderStyle = FormBorderStyle.FixedDialog,
                              StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false, BackColor = Theme.Panel, ForeColor = Theme.Text }) {
      var help = Theme.Lbl(
        "Para a tecla mutar alguem no seu servidor, crie um bot (uma vez so):\n\n" +
        "1. Abra o Portal de Desenvolvedores (botao abaixo) > New Application,\n" +
        "    de um nome (ex.: Deck Station) e crie.\n" +
        "2. Na aba Bot, clique em Reset Token e copie o token.\n" +
        "3. Na aba OAuth2 > URL Generator: marque 'bot' e as permissoes\n" +
        "    'Mute Members' e 'Deafen Members'. Abra o link gerado e\n" +
        "    adicione o bot no seu servidor.\n" +
        "4. Cole o token aqui embaixo e clique em Testar.", 12, 10, false);
      var portal = Theme.Btn("Abrir o Portal de Desenvolvedores", 12, 178, 230, 28);
      portal.Click += (s, e) => System.Diagnostics.Process.Start("https://discord.com/developers/applications");
      var lt = Theme.Lbl("Token do bot:", 12, 218, false);
      var tok = new TextBox { Location = new Point(100, 215), Width = 358, UseSystemPasswordChar = true };
      Theme.Field_(tok);
      var status = Theme.Lbl(Discord.HasToken ? "Ja existe um token salvo (deixe vazio para manter)." : "Nenhum token salvo.", 12, 248, false);
      var test = Theme.Btn("Testar", 12, 288, 80, 28);
      test.Click += (s, e) => {
        if (tok.Text.Trim() != "") Discord.SaveToken(tok.Text);
        status.Text = "Testando...";
        status.Refresh();
        try {
          var gs = Discord.Guilds();
          var names = new List<string>();
          foreach (var g in gs) names.Add(g[1]);
          status.Text = gs.Count == 0 ? "Token ok, mas o bot nao esta em nenhum servidor (passo 3)." : "Funcionando! Servidores: " + string.Join(", ", names.ToArray());
        } catch (Exception ex) {
          status.Text = ex.Message;
        }
      };
      var save = Theme.Btn("Salvar", 302, 288, 75, 28);
      save.DialogResult = DialogResult.OK;
      var cancel = Theme.Btn("Fechar", 383, 288, 75, 28);
      cancel.DialogResult = DialogResult.Cancel;
      f.Controls.AddRange(new Control[] { help, portal, lt, tok, status, test, save, cancel });
      f.CancelButton = cancel;
      if (f.ShowDialog(this) == DialogResult.OK && tok.Text.Trim() != "") Discord.SaveToken(tok.Text);
    }
  }

  // porta e senha do servidor WebSocket do OBS (vazio = as do proprio OBS)
  void ObsSettings() {
    using (var f = new Form { Text = "OBS Studio", ClientSize = new Size(360, 210), FormBorderStyle = FormBorderStyle.FixedDialog,
                              StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false, BackColor = Theme.Panel, ForeColor = Theme.Text }) {
      var help = Theme.Lbl("No OBS: Ferramentas > Configuracoes do servidor WebSocket\n> marque 'Ativar servidor WebSocket'.\nDeixe porta e senha vazias para usar as do proprio OBS.", 12, 10, false);
      var lp = Theme.Lbl("Porta:", 12, 72, false);
      var port = new TextBox { Location = new Point(70, 69), Width = 70, Text = doc.ObsPort > 0 ? doc.ObsPort.ToString() : "" };
      var ls = Theme.Lbl("Senha:", 12, 104, false);
      var pass = new TextBox { Location = new Point(70, 101), Width = 278, Text = doc.ObsPassword, UseSystemPasswordChar = true };
      Theme.Field_(port);
      Theme.Field_(pass);
      var status = Theme.Lbl(Obs.Connected ? "Conectado ao OBS" : Obs.LastError, 12, 136, false);
      var test = Theme.Btn("Testar", 12, 170, 80, 28);
      test.Click += (s, e) => {
        int pt;
        Obs.Port = int.TryParse(port.Text.Trim(), out pt) ? pt : 0;
        Obs.Password = pass.Text;
        status.Text = "Conectando...";
        status.Refresh();
        bool ok = false;
        try { Obs.Refresh(null); ok = Obs.Connected; } catch { }
        status.Text = ok ? "Conectado! Cenas: " + string.Join(", ", (Obs.State != null ? Obs.State.Scenes : new List<string>()).ToArray()) : Obs.LastError;
      };
      var ok_ = Theme.Btn("Salvar", 192, 170, 75, 28);
      ok_.DialogResult = DialogResult.OK;
      var cancel = Theme.Btn("Cancelar", 273, 170, 75, 28);
      cancel.DialogResult = DialogResult.Cancel;
      f.Controls.AddRange(new Control[] { help, lp, port, ls, pass, status, test, ok_, cancel });
      f.CancelButton = cancel;
      if (f.ShowDialog(this) != DialogResult.OK) return;
      int p2;
      doc.ObsPort = int.TryParse(port.Text.Trim(), out p2) ? p2 : 0;
      doc.ObsPassword = pass.Text.Trim();
      Changed(false);
    }
  }

  void PickIcon() {
    var b = Cur(sel);
    if (b == null) return;
    using (var d = new IconPicker(b.ColorHex)) {
      if (d.ShowDialog(this) == DialogResult.OK) { b.IconRaw = d.Choice; Redraw(); PlayAnim(sel); }
    }
  }

  // ---------------------------- animacao no editor ---------------------------
  void PlayAnim(int i) {
    var b = Cur(i);
    if (b == null) return;
    string kind = KeyArt.AnimKind(b);
    if (kind == "nenhuma") return;
    var t = tiles[i];
    if (t.Frames == null) {
      var fr = KeyArt.Animation(kind);
      t.Frames = new Bitmap[fr.Length];
      for (int k = 0; k < fr.Length; k++) t.Frames[k] = KeyArt.Render(b, doc.Labels, fr[k], 2);
    }
    if (animTile != null && animTile != t) { animTile.Frame = -1; animTile.Invalidate(); }
    animTile = t;
    t.Frame = 0;
    t.Invalidate();
    animTimer.Start();
  }

  void AnimTick() {
    if (animTile == null) { animTimer.Stop(); return; }
    animTile.Frame++;
    if (animTile.Frames == null || animTile.Frame >= animTile.Frames.Length) {
      animTile.Frame = -1;
      animTimer.Stop();
    }
    animTile.Invalidate();
  }

  // ---------------------------- placa -> editor -------------------------------
  public void DeviceTouched(int page, int i) {
    if (page != pageIdx && page < doc.Pages.Count) {
      pageIdx = page;
      RefreshPages();
    }
    Select(i);
    PlayAnim(i);
  }

  public void DevicePage(int page) {
    if (page == pageIdx || page >= doc.Pages.Count) return;
    pageIdx = page;
    sel = -1;
    RefreshPages();
  }

  public void SetDeviceStatus(string t) { deviceLbl.Text = t.Replace("Deck Station: ", "Painel: "); }

  string Ask(string question, string value) {
    using (var f = new Form { Text = "Deck Station", ClientSize = new Size(320, 110), FormBorderStyle = FormBorderStyle.FixedDialog,
                              StartPosition = FormStartPosition.CenterParent, MaximizeBox = false, MinimizeBox = false, BackColor = Theme.Panel, ForeColor = Theme.Text }) {
      var l = Theme.Lbl(question, 12, 12, false);
      var tb = new TextBox { Location = new Point(12, 36), Width = 296, Text = value };
      Theme.Field_(tb);
      var ok = Theme.Btn("OK", 152, 72, 75, 28);
      ok.DialogResult = DialogResult.OK;
      var cancel = Theme.Btn("Cancelar", 233, 72, 75, 28);
      cancel.DialogResult = DialogResult.Cancel;
      f.Controls.AddRange(new Control[] { l, tb, ok, cancel });
      f.AcceptButton = ok;
      f.CancelButton = cancel;
      return f.ShowDialog(this) == DialogResult.OK && tb.Text.Trim() != "" ? tb.Text.Trim() : null;
    }
  }
}

// janela com todos os icones para escolher
class IconPicker : Form {
  public string Choice;

  public IconPicker(string colorHex) {
    Text = "Escolher icone";
    BackColor = Theme.Back;
    ForeColor = Theme.Text;
    FormBorderStyle = FormBorderStyle.FixedDialog;
    StartPosition = FormStartPosition.CenterParent;
    MaximizeBox = false;
    MinimizeBox = false;
    KeyPreview = true;
    KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    var flow = new FlowLayoutPanel { Location = new Point(8, 8), Size = new Size(10 * 84 + 20, 520), AutoScroll = true };
    var names = new List<string>(Icons.Glyphs.Keys);
    foreach (string n in names) {
      var tmp = new DeckButton { ColorHex = colorHex, GlyphName = n, Glyph = Icons.GlyphFor(n) };
      var img = KeyArt.Render(tmp, false, null, 1);
      var b = new Button { Size = new Size(80, 72), Margin = new Padding(2), FlatStyle = FlatStyle.Flat, Image = img, Cursor = Cursors.Hand, BackColor = Theme.Back };
      b.FlatAppearance.BorderSize = 0;
      b.FlatAppearance.MouseOverBackColor = Theme.Accent;
      new ToolTip().SetToolTip(b, n);
      string name = n;
      b.Click += (s, e) => { Choice = name; DialogResult = DialogResult.OK; };
      flow.Controls.Add(b);
    }
    Controls.Add(flow);
    ClientSize = new Size(flow.Width + 16, flow.Height + 16);
  }
}

}
