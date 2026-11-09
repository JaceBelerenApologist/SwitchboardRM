using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SwitchboardRM;

internal sealed class Session
{
    public Instance Inst;
    public Protocol Kind = Protocol.Rdp;
    public Tunnel Tunnel;
    public RdpHost Rdp;          // Remote Desktop sessions
    public TerminalHost Term;    // SSH sessions and local shells
    public bool Docked;          // in the docked-terminal panel under the tabs, not a tab of its own
    public TabPage Page;
    public DateTime Started = DateTime.Now;
    public bool SawActivity;
    public bool WasConnected;
    public bool Flagged;
}

/// <summary>How startup went: ready to use, or the user needs to sign in first.</summary>
internal enum StartupResult { Ready, NeedsSignIn }

internal sealed class MainForm : Form
{
    // Messages that mean "your AWS session has expired or you are not signed in".
    private static readonly Regex LoginPattern = new(
        @"\bsso\b|expired|unable to locate credentials|security token|refresh token|nocredentials|" +
        @"invalidclienttokenid|unrecognizedclient|has no credentials",
        RegexOptions.IgnoreCase);

    private readonly AppSettings _settings = AppSettings.Load();
    private List<Instance> _all = new();
    private readonly List<Session> _sessions = new();
    private readonly HashSet<string> _opening = new();
    private bool _busy;
    private string _sortProp = "Name";
    private bool _sortDesc;
    private Session _menuTarget;

    // toolbar
    private readonly ToolStrip _tool = new();
    private readonly ToolStripComboBox _cboProfile = new() { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
    private const string AddProfileItem = "Add profile ...";  // last entry in the profile dropdown
    private string _lastProfile = "";                          // to go back to if "Add profile ..." is cancelled
    private readonly ToolStripButton _btnList = new(Icons.List)
    { Checked = true, ToolTipText = "Show the server list (Ctrl+L). Pressed = shown." };
    private readonly ToolStripButton _btnSignIn = new("Sign in")
    { ToolTipText = "Open the Microsoft sign-in page and get fresh AWS access for this profile (lasts about an hour)" };
    private readonly ToolStripButton _btnSignOut = new("Sign out")
    { ToolTipText = "Sign out of AWS now: removes this profile's temporary keys. Open tabs keep running.", Enabled = false };
    private readonly ToolStripButton _btnRefresh = new(Icons.Refresh)
    { ToolTipText = "Reload the server list from AWS (F5). Doesn't sign in again." };
    private readonly ToolStripButton _btnSettings = new(Icons.Settings)
    { Alignment = ToolStripItemAlignment.Right, ToolTipText = "Settings (Ctrl+,): profile, RDP credentials, appearance, about" };
    private readonly ToolStripButton _btnAuth = new("Status: Checking ...")
    { ToolTipText = "AWS sign-in status. Click for profile settings." };

    // sign-in heartbeat
    private readonly System.Windows.Forms.Timer _authTimer = new() { Interval = 30_000 };
    private static readonly TimeSpan AuthCheckEvery = TimeSpan.FromMinutes(5);
    private string _authArn;
    private string _authError;          // last check failed for a reason other than being signed out (e.g. no network)
    private DateTimeOffset? _authExpiry; // from the credentials file, when the sign-in tool records it
    private DateTime _lastAuthCheck = DateTime.MinValue;
    private bool _authChecking;

    // automatic renewal
    private bool _renewing;
    private CancellationTokenSource _renewCts;   // the background renewal in progress, so locking can stop it
    private bool _paused;                        // Windows is locked or asleep: no background sign-in at all
    private DateTime? _renewFailedAt;
    private static readonly TimeSpan RenewAhead = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RenewRetryAfter = TimeSpan.FromMinutes(15);

    // menu
    private readonly MenuStrip _menu = new();
    private readonly ToolStripMenuItem _miDark = new("Dark mode");
    private readonly ToolStripMenuItem _miList = new("Server list") { Checked = true, ShortcutKeys = Keys.Control | Keys.L };

    // left side
    private readonly TextBox _txtSearch = new();
    private readonly ThemedCheckBox _chkWin = new() { Text = "Windows only", AutoSize = true };
    private readonly ThemedCheckBox _chkOffline = new() { Text = "Hide offline", AutoSize = true };

    // start / stop / reboot in progress: instance ID -> what the Status column shows meanwhile
    private readonly Dictionary<string, string> _actions = new();

    // saved RDP connections (connections.json), shown in the list after the AWS servers
    private List<SavedConnection> _saved = ConnectionStore.Load();
    private List<Instance> _aws = new();
    private readonly Dictionary<string, bool> _reachable = new(); // saved connection id -> port answered
    private readonly ToolStripButton _btnAdd = new(Icons.Add) { ToolTipText = "Add a saved RDP connection (not AWS)" };
    private readonly ToolStripSplitButton _btnShell = new(Icons.Terminal)
    { ToolTipText = "New shell on this PC (Ctrl+T). The arrow picks which one." };
    private readonly ToolStripMenuItem _miNewShell = new("New &shell") { ShortcutKeys = Keys.Control | Keys.T };
    private readonly ToolStripMenuItem _miShellMenu = new("Open shell");
    private readonly ContextMenuStrip _shellMenu = new();

    // docked terminals: one shared panel under the tabs, with its own tab strip
    private readonly SplitContainer _dockSplit = new()
    {
        Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, FixedPanel = FixedPanel.Panel2,
        Panel2Collapsed = true, SplitterWidth = 6,
    };
    private readonly ThemedTabControl _dockTabs = new() { Dock = DockStyle.Fill };
    private readonly ToolStrip _dockBar = new() { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top, Padding = new Padding(6, 0, 4, 0) };
    private readonly ToolStripButton _btnUndock = new(Icons.Undock) { Alignment = ToolStripItemAlignment.Right, ToolTipText = "Move this terminal back to a tab" };
    private readonly ToolStripButton _btnHideDock = new(Icons.HideDown) { Alignment = ToolStripItemAlignment.Right, ToolTipText = "Hide the docked terminals (Ctrl+`). They keep running." };
    private readonly ToolStripButton _btnCloseDocked = new(Icons.CloseX) { Alignment = ToolStripItemAlignment.Right, ToolTipText = "Close this terminal" };
    private readonly ToolStripMenuItem _miDock = new("Docked &terminals") { ShortcutKeyDisplayString = "Ctrl+`" };
    private readonly ToolStripMenuItem _tabMenuDock = new("Dock to bottom");
    private readonly ToolStripLabel _dockTitle = new("Docked terminals");
    private bool _dockMinimized;   // collapsed to its header strip (the terminals keep running)
    private DateTime _dockToggledAt = DateTime.MinValue;   // so one click can't collapse and expand at once
    private readonly ToolStripMenuItem _smAdd = new("&Add RDP connection ...");
    private readonly ToolStripMenuItem _smAdHoc = new("Connect to a &computer ...");
    private readonly Dictionary<string, string> _adHocPasswords = new();   // one-off connections: memory only
    private readonly ToolStripMenuItem _smEdit = new("&Edit connection ...");
    private readonly ToolStripMenuItem _smRemove = new("Remo&ve connection ...");
    private readonly ToolStripMenuItem _miAdd = new("Add RDP connection ...");
    private readonly ToolStripMenuItem _miEdit = new("Edit connection ...");
    private readonly ToolStripMenuItem _miRemove = new("Remove connection ...");
    private readonly ToolStripMenuItem _miConnect = new("Connect") { Font = new Font("Segoe UI", 9.75F, FontStyle.Bold) };
    private readonly ToolStripMenuItem _miStart = new("Start");
    private readonly ToolStripMenuItem _miStop = new("Stop ...");
    private readonly ToolStripMenuItem _miReboot = new("Reboot ...");

    // the same actions on the toolbar and in the Server menu
    private readonly ToolStripButton _btnConnect = new("Connect")
    { ToolTipText = "Connect to the selected server (or double-click it): SSH for Linux, Remote Desktop for Windows" };
    private readonly ToolStripButton _btnStart = new("Start") { ToolTipText = "Start the selected server" };
    private readonly ToolStripButton _btnStop = new("Stop") { ToolTipText = "Stop the selected server (asks first)" };
    private readonly ToolStripButton _btnReboot = new("Reboot") { ToolTipText = "Reboot the selected server (asks first)" };
    private readonly ToolStripMenuItem _smConnect = new("&Connect") { ShortcutKeyDisplayString = "Enter" };
    private readonly ToolStripMenuItem _smConnectSsh = new("Connect with SS&H");
    private readonly ToolStripMenuItem _smConnectRdp = new("Connect with Remote &Desktop");
    private readonly ToolStripMenuItem _miConnectSsh = new("Connect with SSH");
    private readonly ToolStripMenuItem _miConnectRdp = new("Connect with Remote Desktop");
    private readonly ToolStripMenuItem _smStart = new("&Start");
    private readonly ToolStripMenuItem _smStop = new("S&top ...");
    private readonly ToolStripMenuItem _smReboot = new("&Reboot ...");
    private readonly ToolStripMenuItem _smCopyId = new("Copy instance &ID");
    private readonly ToolStripMenuItem _smCopyIp = new("Copy IP &address");
    private readonly ThemedListView _list = new();
    private readonly ContextMenuStrip _listMenu = new();

    // right side
    private readonly SplitContainer _split = new();
    private readonly ThemedTabControl _tabs = new();
    private readonly Label _hint = new();
    private readonly ContextMenuStrip _tabMenu = new();

    // bottom
    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _status = new();

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };

    // "it's working" indicators
    private readonly ToolStripProgressBar _progress = new()
    { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Width = 130, Visible = false };
    private readonly ToolStripButton _btnCancel = new("Cancel sign-in") { Visible = false };
    private readonly System.Windows.Forms.Timer _busyTimer = new() { Interval = 1000 };
    private CancellationTokenSource _cts;
    private DateTime _busyStart;
    private string _busyMessage = "";
    private const string DefaultHint = "Double-click a server on the left to connect.";
    // ---- server list columns: Name is always first; the rest can be dragged into any order or hidden
    private sealed record ListColumn(string Key, string Title, int Width);
    private static readonly ListColumn[] AllColumns =
    {
        new("Name", "Name", 200), new("Type", "Type", 95), new("Os", "OS", 150), new("Region", "Region", 90),
        new("Ip", "IP address", 110), new("Id", "Instance ID", 150), new("Status", "Status", 110),
        new("User", "Sign in as", 170), new("Group", "Group", 150),
        new("Description", "Description", 220),
    };
    private static readonly string[] AwsColumnKeys = { "Name", "Type", "Os", "Region", "Ip", "Id", "Status" };
    private static readonly string[] SavedColumnKeys = { "Name", "Group", "Description", "Ip", "User", "Status" };

    // ---- the list's two tabs: AWS servers / saved RDP connections
    private bool _savedView;
    private readonly ToolStrip _viewBar = new() { GripStyle = ToolStripGripStyle.Hidden, Dock = DockStyle.Top, Padding = new Padding(4, 2, 4, 2) };
    private readonly ToolStripButton _tabAws = new("AWS servers") { ToolTipText = "EC2 servers reached through AWS Systems Manager (Ctrl+1)" };
    private readonly ToolStripButton _tabSaved = new("Saved RDP") { ToolTipText = "Your saved Remote Desktop connections (Ctrl+2)" };
    private readonly ToolStripMenuItem _miViewAws = new("&AWS servers") { ShortcutKeys = Keys.Control | Keys.D1 };
    private readonly ToolStripMenuItem _miViewSaved = new("Saved &RDP connections") { ShortcutKeys = Keys.Control | Keys.D2 };
    private readonly ToolStripMenuItem _miKeepSignedIn = new("&Keep me signed in");

    // "Keep me signed in" on the toolbar, next to the sign-in status
    private readonly ThemedCheckBox _chkKeep = new() { Text = "Keep me signed in", AutoSize = true };
    private ToolStripControlHost _keepHost;
    private readonly ToolTip _tips = new();
    private bool _syncingKeep; // set while the code (not the user) changes the checkbox
    private List<string> _columns = new();               // keys shown, in display order
    private readonly ContextMenuStrip _columnMenu = new();
    private bool _rebuildingColumns;

    public MainForm()
    {
        Text = AppInfo.Name;
        ClientSize = new Size(1400, 820);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        if (AppIcon.Icon != null) Icon = AppIcon.Icon;
        Theme.Configure(_settings);   // accent colour and fonts, before anything is built
        Shells.StartWslLookup();      // WSL distributions are listed in the background
        BuildUi();
        _miDark.Checked = _settings.EffectiveDark;
        _chkKeep.Checked = _settings.KeepSignedIn;
        WireEvents();

        SelectProfile(_settings.Profile ?? ""); // empty on first run: the first profile found is used (LoadProfilesAsync)
        _chkWin.Checked = _settings.WindowsOnly;
        _chkOffline.Checked = _settings.HideOffline;
        ApplyTheme();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
        TaskbarIcon.Apply(this);
        PseudoConsole.OwnerWindow = Handle;   // terminals' hidden console windows belong here, so WAM sign-in appears
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _tabKeys?.Install();   // back in Switchboard: make sure the tab-key hook is the newest again
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        TaskbarIcon.Apply(this);   // icon sizes follow the display scaling
    }

    private void ApplyTheme()
    {
        Theme.Configure(_settings);
        Theme.Apply(this, _settings.EffectiveDark, _listMenu, _tabMenu);
        // server list text size (Settings > Appearance), in the app font
        float listSize = _settings.ListFontSize is >= 8f and <= 18f ? _settings.ListFontSize : 10;
        if (_list.Font.Size != listSize || _list.Font.FontFamily.Name != Theme.UiFont)
            _list.Font = Theme.CreateFont(Theme.UiFont, listSize);
        _hint.ForeColor = Theme.Current.Muted;
        RenderActionIcons();
        foreach (var s in _sessions) s.Term?.ApplyTheme();
        _chkKeep.BackColor = Theme.Current.Raised; // sits on the toolbar, not the window background
        _chkKeep.ForeColor = Theme.Current.Text;
        _chkKeep.Invalidate();
        _list.Invalidate();
        _tabs.Invalidate();
    }

    private string Profile => _lastProfile;

    // ---------------------------------------------------------------- UI

    private void BuildUi()
    {
        // toolbar
        _tool.GripStyle = ToolStripGripStyle.Hidden;
        foreach (var b in new[] { _btnList, _btnRefresh, _btnCancel, _btnSettings, _btnAuth })
            b.DisplayStyle = ToolStripItemDisplayStyle.Text;
        foreach (var b in new ToolStripItem[] { _btnList, _btnRefresh, _btnSettings, _btnAdd, _btnShell })
        {
            b.Font = Icons.Font;
            b.Padding = new Padding(4, 1, 4, 1);
            b.Margin = new Padding(2, 1, 0, 2);
        }
        _btnList.Margin = new Padding(4, 1, 0, 2);
        _btnSettings.Margin = new Padding(0, 1, 6, 2); // a little breathing room from the window edge
        _btnShell.DisplayStyle = ToolStripItemDisplayStyle.Text;
        foreach (var b in new[] { _btnConnect, _btnStart, _btnStop, _btnReboot })
        {
            b.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            b.ImageScaling = ToolStripItemImageScaling.None; // icons are drawn at the right size for the display
            b.Padding = new Padding(3, 1, 4, 1);
            b.Enabled = false;                               // until a server is selected
        }
        foreach (var b in new[] { _btnSignIn, _btnSignOut })   // icon and word, like the server buttons
        {
            b.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            b.ImageScaling = ToolStripItemImageScaling.None;
            b.Padding = new Padding(3, 1, 4, 1);
        }
        _cboProfile.ToolTipText = "The AWS profile (from your computer's AWS settings) used to list servers and connect";
        _tool.Items.AddRange(new ToolStripItem[]
        {
            _btnList, _btnRefresh, _btnAdd, _btnShell,
            new ToolStripSeparator { Margin = new Padding(6, 0, 6, 0) },
            _btnConnect, _btnStart, _btnStop, _btnReboot,
            new ToolStripSeparator { Margin = new Padding(14, 0, 14, 0) },  // clear gap between server controls and the profile
            new ToolStripLabel("AWS profile"), _cboProfile,
            _btnSignIn, _btnSignOut, _btnAuth, KeepHost(), _btnCancel,
            _btnSettings,   // right-aligned
        });

        // menu bar
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(new ToolStripMenuItem("&Settings...", null, async (_, _) => await OpenSettingsAsync(SettingsForm.Page.Profile))
        { ShortcutKeys = Keys.Control | Keys.Oemcomma, ShortcutKeyDisplayString = "Ctrl+," });
        file.DropDownItems.Add(_miNewShell);
        file.DropDownItems.Add(_miShellMenu);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(ImportMenu());
        file.DropDownItems.Add(new ToolStripMenuItem("Open &log folder", null, (_, _) => OpenLogs()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (_, _) => Close()));

        var view = new ToolStripMenuItem("&View");
        view.DropDownItems.Add(_miViewAws);
        view.DropDownItems.Add(_miViewSaved);
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(_miDark);
        view.DropDownItems.Add(_miList);
        view.DropDownItems.Add(new ToolStripSeparator());
        var miNextTab = new ToolStripMenuItem("&Next tab", null, (_, _) => SwitchTab(+1)) { ShortcutKeyDisplayString = "Ctrl+Tab" };
        var miPrevTab = new ToolStripMenuItem("&Previous tab", null, (_, _) => SwitchTab(-1)) { ShortcutKeyDisplayString = "Ctrl+Shift+Tab" };
        var miCloseTab = new ToolStripMenuItem("&Close tab ...", null, (_, _) => CloseCurrentTab()) { ShortcutKeyDisplayString = "Ctrl+Shift+W" };
        view.DropDownItems.AddRange(new ToolStripItem[] { miNextTab, miPrevTab, miCloseTab, _miDock });
        view.DropDownOpening += (_, _) =>
        {
            miNextTab.Enabled = miPrevTab.Enabled = _tabs.TabCount > 1;
            miCloseTab.Enabled = _tabs.TabCount > 0;
            _miDock.Enabled = _dockTabs.TabCount > 0;
            _miDock.Checked = _dockTabs.TabCount > 0 && !_dockSplit.Panel2Collapsed && !_dockMinimized;
        };
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(new ToolStripMenuItem("&Refresh server list", null, async (_, _) => await RefreshAsync())
        { ShortcutKeys = Keys.F5 });

        var aws = new ToolStripMenuItem("&AWS");
        aws.DropDownItems.Add(new ToolStripMenuItem("&Sign in...", null, async (_, _) => await SignInAsync()));
        aws.DropDownItems.Add(new ToolStripMenuItem("Sign &out", null, async (_, _) => await SignOutNowAsync()));
        aws.DropDownItems.Add(new ToolStripMenuItem("&Add profile ...", null, (_, _) => AddProfile()));
        aws.DropDownItems.Add(new ToolStripMenuItem("&Remove profile ...", null, async (_, _) => await RemoveCurrentProfileAsync()));
        aws.DropDownItems.Add(_miKeepSignedIn);
        aws.DropDownOpening += (_, _) => _miKeepSignedIn.Checked = _settings.KeepSignedIn;
        aws.DropDownItems.Add(new ToolStripMenuItem("Set &up Microsoft sign-in...", null, (_, _) => SignInSetup.Run(Profile, this)));
        aws.DropDownItems.Add(new ToolStripSeparator());
        aws.DropDownItems.Add(new ToolStripMenuItem("&Profile settings...", null, async (_, _) => await OpenSettingsAsync(SettingsForm.Page.Profile)));
        aws.DropDownItems.Add(new ToolStripMenuItem("&RDP credentials...", null, async (_, _) => await OpenSettingsAsync(SettingsForm.Page.Credentials)));

        var server = new ToolStripMenuItem("&Server");
        server.DropDownItems.AddRange(new ToolStripItem[]
        {
            _smConnect, _smConnectSsh, _smConnectRdp,
            new ToolStripSeparator(),
            _smStart, _smStop, _smReboot,
            new ToolStripSeparator(),
            _smCopyId, _smCopyIp,
            new ToolStripSeparator(),
            _smAdHoc, _smAdd, _smEdit, _smRemove,
            ImportMenu(),
        });
        server.DropDownOpening += (_, _) => UpdateActionStates();

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(new ToolStripMenuItem("&About " + AppInfo.ShortName, null,
            async (_, _) => await OpenSettingsAsync(SettingsForm.Page.About)));

        _menu.Items.AddRange(new ToolStripItem[] { file, view, server, aws, help });
        MainMenuStrip = _menu;

        // status bar
        _status.Spring = true;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _statusStrip.Items.Add(_progress);
        _statusStrip.Items.Add(_status);

        // left: search + list
        var searchPanel = new Panel { Dock = DockStyle.Top, Height = 34 };
        _txtSearch.PlaceholderText = "Search name, IP, instance ID, tag ...";
        searchPanel.Padding = new Padding(6, 6, 6, 0);
        _txtSearch.Dock = DockStyle.Fill;
        foreach (var c in new[] { _chkWin, _chkOffline })
        {
            c.Dock = DockStyle.Right;
            c.Padding = new Padding(10, 0, 0, 0);
        }
        searchPanel.Controls.Add(_chkOffline); // docked first, so it sits furthest right
        searchPanel.Controls.Add(_chkWin);
        searchPanel.Controls.Add(_txtSearch);
        _txtSearch.BringToFront(); // docked last, so it takes whatever width the checkbox leaves
        _chkWin.Visible = !_settings.SavedView;
        if (_settings.SavedView) _txtSearch.PlaceholderText = "Search name, address, user ...";

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.GridLines = false; // row lines are drawn by ThemedListView
        _list.ShowItemToolTips = true;
        _list.AllowColumnReorder = true; // drag headers; Name is kept first (see ColumnReordered)
        _list.Font = new Font("Segoe UI", 10F);
        _savedView = _settings.SavedView;
        if (!_settings.DescriptionColumnAdded)
        {
            if (_settings.SavedColumns.Count > 0 && !_settings.SavedColumns.Contains("Description"))
            {
                int at = _settings.SavedColumns.IndexOf("Group");
                _settings.SavedColumns.Insert(at >= 0 ? at + 1 : _settings.SavedColumns.Count, "Description");
            }
            _settings.DescriptionColumnAdded = true;
        }
        _columns = ValidColumns(_savedView ? _settings.SavedColumns : _settings.Columns, ViewColumnKeys);
        RebuildColumns();
        _tabAws.Checked = !_savedView;
        _tabSaved.Checked = _savedView;
        foreach (var t in new[] { _tabAws, _tabSaved })
        {
            t.DisplayStyle = ToolStripItemDisplayStyle.Text;
            t.Padding = new Padding(8, 2, 8, 2);
            t.Margin = new Padding(0, 1, 4, 1);
            t.Click += (_, _) => SetView(t == _tabSaved);
        }
        _viewBar.Items.AddRange(new ToolStripItem[] { _tabAws, _tabSaved });
        _miViewAws.Checked = !_savedView;
        _miViewSaved.Checked = _savedView;

        // the context menu, the toolbar and the Server menu all do the same things
        EventHandler connect = async (_, _) => { var i = Selected(); if (i != null) await ConnectAsync(i); };
        EventHandler start = async (_, _) => { var i = Selected(); if (i != null) await InstanceActionAsync(i, InstanceActions.Action.Start); };
        EventHandler stop = async (_, _) => { var i = Selected(); if (i != null) await InstanceActionAsync(i, InstanceActions.Action.Stop); };
        EventHandler reboot = async (_, _) => { var i = Selected(); if (i != null) await InstanceActionAsync(i, InstanceActions.Action.Reboot); };
        foreach (var item in new ToolStripItem[] { _miConnect, _btnConnect, _smConnect }) item.Click += connect;
        foreach (var item in new ToolStripItem[] { _miConnectSsh, _smConnectSsh })
            item.Click += async (_, _) => { var i = Selected(); if (i != null) await ConnectAsync(i, Protocol.Ssh); };
        foreach (var item in new ToolStripItem[] { _miConnectRdp, _smConnectRdp })
            item.Click += async (_, _) => { var i = Selected(); if (i != null) await ConnectAsync(i, Protocol.Rdp); };
        foreach (var item in new ToolStripItem[] { _miStart, _btnStart, _smStart }) item.Click += start;
        foreach (var item in new ToolStripItem[] { _miStop, _btnStop, _smStop }) item.Click += stop;
        foreach (var item in new ToolStripItem[] { _miReboot, _btnReboot, _smReboot }) item.Click += reboot;
        _smCopyId.Click += (_, _) => CopyToClipboard(Selected()?.Id);
        foreach (var item in new ToolStripItem[] { _btnAdd, _smAdd, _miAdd }) item.Click += (_, _) => AddOrEditConnection(null);
        foreach (var item in new ToolStripItem[] { _smEdit, _miEdit }) item.Click += (_, _) => { if (Selected() is { IsSaved: true } i) AddOrEditConnection(i.Saved); };
        foreach (var item in new ToolStripItem[] { _smRemove, _miRemove }) item.Click += (_, _) => { if (Selected() is { IsSaved: true } i) RemoveConnection(i.Saved); };
        _smCopyIp.Click += (_, _) => CopyToClipboard(Selected()?.Ip);
        _list.SelectedIndexChanged += (_, _) => UpdateActionStates();
        _listMenu.Items.AddRange(new ToolStripItem[]
        {
            _miConnect, _miConnectSsh, _miConnectRdp,
            new ToolStripSeparator(),
            _miStart, _miStop, _miReboot,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Copy instance ID", null, (_, _) => CopyToClipboard(Selected()?.Id)),
            new ToolStripMenuItem("Copy IP address", null, (_, _) => CopyToClipboard(Selected()?.Ip)),
            new ToolStripSeparator(),
            _miAdd, _miEdit, _miRemove,
        });
        _listMenu.Opening += (_, e) =>
        {
            if (_list.PointToClient(Cursor.Position).Y < HeaderHeight())
            {
                e.Cancel = true;
                ShowColumnMenu(Cursor.Position);
                return;
            }
            UpdateActionStates();
            if (Selected() == null)
            {
                // nothing selected: only "Add RDP connection" makes sense
                foreach (ToolStripItem item in _listMenu.Items) item.Visible = item == _miAdd;
                return;
            }
            foreach (ToolStripItem item in _listMenu.Items) item.Visible = true;
        };
        _list.ContextMenuStrip = _listMenu;
        _list.CellColor = (item, column) =>
            ColumnKey(column) == "Status" && item.Tag is Instance inst ? StatusColor(inst) : null;

        _split.Panel1.Controls.Add(searchPanel);
        _split.Panel1.Controls.Add(_viewBar);   // added after the search row, so it docks above it
        _split.Panel1.Controls.Add(_list);
        _list.BringToFront();

        // right: tabs + hint
        _tabs.Dock = DockStyle.Fill;
        _tabs.Visible = false;
        _hint.Dock = DockStyle.Fill;
        _hint.TextAlign = ContentAlignment.MiddleCenter;
        _hint.ForeColor = SystemColors.GrayText;
        _hint.Font = new Font("Segoe UI", 11F);
        _hint.Text = DefaultHint;
        _dockSplit.Panel1.Controls.Add(_tabs);
        _dockSplit.Panel1.Controls.Add(_hint);
        // the docked-terminal panel: a header (title + Move to tab / Hide / Close) above its own tab strip
        _dockBar.Items.Add(_dockTitle);
        foreach (var b in new[] { _btnCloseDocked, _btnHideDock, _btnUndock })   // right-aligned: first added sits furthest right
        {
            b.Font = Icons.Font;
            b.DisplayStyle = ToolStripItemDisplayStyle.Text;
            b.Padding = new Padding(4, 0, 4, 0);
            _dockBar.Items.Add(b);
        }
        _dockSplit.Panel2.Controls.Add(_dockBar);
        _dockSplit.Panel2.Controls.Add(_dockTabs);
        _dockTabs.BringToFront();   // fills what the header leaves
        _split.Panel2.Controls.Add(_dockSplit);

        _tabMenu.Items.Add(_tabMenuDock);
        _tabMenu.Items.Add("Close tab", null, (_, _) => { if (_menuTarget != null) CloseSession(_menuTarget); });
        _tabMenu.Opening += (_, e) =>
        {
            // only terminals can dock (a remote desktop would lose its connection if moved)
            _tabMenuDock.Visible = _menuTarget?.Term != null;
            _tabMenuDock.Text = _menuTarget is { Docked: true } ? "Move back to a tab" : "Dock to bottom";
        };
        _tabMenuDock.Click += (_, _) =>
        {
            if (_menuTarget == null) return;
            if (_menuTarget.Docked) UndockSession(_menuTarget); else DockSession(_menuTarget);
        };

        _split.Dock = DockStyle.Fill;
        Controls.Add(_tool);
        Controls.Add(_menu);        // added after the toolbar so it docks above it
        Controls.Add(_statusStrip);
        Controls.Add(_split);
        _split.BringToFront();
    }

    private void WireEvents()
    {
        _btnList.Click += (_, _) => ToggleList();
        _btnRefresh.Click += async (_, _) => await RefreshAsync();
        _btnSignIn.Click += async (_, _) => await SignInAsync();
        _btnSignOut.Click += async (_, _) => await SignOutNowAsync();
        _btnSettings.Click += async (_, _) => await OpenSettingsAsync(SettingsForm.Page.Profile);
        _btnAuth.Click += async (_, _) => await OpenSettingsAsync(SettingsForm.Page.Profile);
        _miDark.Click += (_, _) => SetDarkMode(!_settings.EffectiveDark);
        _miList.Click += (_, _) => ToggleList();
        _miViewAws.Click += (_, _) => SetView(false);
        _miViewSaved.Click += (_, _) => SetView(true);
        _miKeepSignedIn.Click += async (_, _) => await SetKeepSignedInAsync(!_settings.KeepSignedIn);
        _chkKeep.CheckedChanged += async (_, _) => { if (!_syncingKeep) await SetKeepSignedInAsync(_chkKeep.Checked); };
        _cboProfile.SelectedIndexChanged += async (_, _) =>
        {
            string picked = _cboProfile.SelectedItem as string;
            if (picked == AddProfileItem)
            {
                SelectProfile(_lastProfile);  // back to where we were while the dialog is open
                AddProfile();
                return;
            }
            if (picked == null || picked == _lastProfile) return;
            _lastProfile = picked;
            if (!_busy) await CheckAuthAsync();
        };
        _btnCancel.Click += (_, _) => _cts?.Cancel();
        _busyTimer.Tick += (_, _) => UpdateBusyText();

        _txtSearch.TextChanged += (_, _) => RebuildList();
        _chkWin.CheckedChanged += (_, _) => RebuildList();
        _chkOffline.CheckedChanged += (_, _) => RebuildList();

        _list.DoubleClick += async (_, _) => { var i = Selected(); if (i != null) await ConnectAsync(i); };
        _list.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { var i = Selected(); if (i != null) await ConnectAsync(i); }
        };
        _list.ColumnReordered += (_, e) =>
        {
            // Name stays first: nothing may move it, and nothing may be dropped in front of it
            if (e.OldDisplayIndex == 0 || e.NewDisplayIndex == 0) { e.Cancel = true; return; }
            // let the drag finish, then adopt the new order
            BeginInvoke(() =>
            {
                var order = _list.Columns.Cast<ColumnHeader>().OrderBy(c => c.DisplayIndex).Select(c => (string)c.Tag).ToList();
                if (order.SequenceEqual(_columns)) return;
                CaptureColumnWidths();
                _columns = order;
                RebuildColumns();
                SaveColumnLayout();
            });
        };
        _list.ColumnWidthChanged += (_, e) =>
        {
            if (!_rebuildingColumns && e.ColumnIndex < _columns.Count - 1) CaptureColumnWidths(); // last one stretches to fit
        };
        _list.ColumnClick += (_, e) =>
        {
            string prop = ColumnKey(e.Column);
            if (_sortProp == prop) _sortDesc = !_sortDesc;
            else { _sortProp = prop; _sortDesc = false; }
            RebuildList();
        };

        _tabs.MouseUp += OnTabMouseUp;

        // docked terminals
        _dockTabs.MouseUp += OnTabMouseUp;
        _dockTabs.CloseRequested += page => { var d = _sessions.FirstOrDefault(x => x.Page == page); if (d != null) CloseSession(d); };
        _dockTabs.NewTabRequested += screen =>
        {
            FillShellMenu(_shellMenu.Items, _shellMenu, docked: true);
            _shellMenu.Show(screen);
        };
        _dockTabs.SelectedIndexChanged += (_, _) => _sessions.FirstOrDefault(x => x.Page == _dockTabs.SelectedTab)?.Term?.FocusTerminal();
        _btnUndock.Click += (_, _) => { var d = SelectedDocked(); if (d != null) UndockSession(d); };
        _btnHideDock.Click += (_, _) => ToggleDockFromClick(!_dockMinimized);
        // while collapsed, a click anywhere on the strip brings the terminals back. The strip only reacts to its
        // empty space: the arrow button and the title have their own handlers, and the same click reaching two
        // handlers is what made the panel expand and then collapse again.
        _dockTitle.Click += (_, _) => { if (_dockMinimized) ToggleDockFromClick(false); };
        _dockBar.MouseClick += (_, e) =>
        {
            if (_dockMinimized && _dockBar.GetItemAt(e.Location) == null) ToggleDockFromClick(false);
        };
        _btnCloseDocked.Click += (_, _) => CloseWithConfirm(SelectedDocked());
        _dockSplit.SplitterMoved += (_, _) =>
        {
            if (!_dockSplit.Panel2Collapsed && !_dockMinimized)
                _settings.DockHeight = _dockSplit.Height - _dockSplit.SplitterDistance - _dockSplit.SplitterWidth;
        };
        _dockSplit.MouseDoubleClick += (_, _) =>   // double-click the bar: back to a third of the height
        {
            if (_dockMinimized) { SetDockMinimized(false); return; }
            _settings.DockHeight = Math.Max(120, _dockSplit.Height / 3);
            ApplyDockHeight();
        };
        _miDock.Click += (_, _) => ToggleDock();
        // local shells: toolbar button (click = default, arrow = choose), File menu, Ctrl+T, and the tab strip's +
        _btnShell.ButtonClick += async (_, _) => await OpenShellAsync(Shells.Default(_settings.DefaultShell));
        _btnShell.DropDownOpening += (_, _) => FillShellMenu(_btnShell.DropDownItems, _btnShell.DropDown);
        _miNewShell.Click += async (_, _) => await OpenShellAsync(Shells.Default(_settings.DefaultShell));
        _miShellMenu.DropDownItems.Add("(loading)");   // makes it a submenu; filled when opened
        _miShellMenu.DropDownOpening += (_, _) => FillShellMenu(_miShellMenu.DropDownItems, _miShellMenu.DropDown);
        _tabs.NewTabRequested += screen =>
        {
            // the main tabs' +: a one-off connection, then the shells (the docked panel's + is shells only)
            FillShellMenu(_shellMenu.Items, _shellMenu);
            _shellMenu.Items.Insert(0, new ToolStripMenuItem("Remote Desktop to a computer ...", null,
                async (_, _) => await ConnectAdHocAsync()));
            _shellMenu.Items.Insert(1, new ToolStripSeparator());
            Theme.ApplyMenu(_shellMenu);
            _shellMenu.Show(screen);
        };
        _smAdHoc.Click += async (_, _) => await ConnectAdHocAsync();
        _tabs.CloseRequested += page =>
        {
            var s = _sessions.FirstOrDefault(x => x.Page == page);
            if (s != null) CloseSession(s);
        };
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            var sel = _sessions.FirstOrDefault(x => x.Page == _tabs.SelectedTab);
            sel?.Rdp?.SyncSize();
            sel?.Term?.FocusTerminal();
            if (sel?.Rdp != null) { try { sel.Rdp.Focus(); } catch { } }
            _tabKeys?.Install();
        };
        _timer.Tick += (_, _) => PollSessions();
        _timer.Start();
        _authTimer.Tick += async (_, _) => await AuthHeartbeatAsync();
        _authTimer.Start();
        WatchSessionState();
        // Ctrl+Alt+Left/Right, also inside remote desktops (see TabHotkeyHook)
        _tabKeys = new TabHotkeyHook(TabKeysApply, direction => BeginInvoke(() => SwitchTab(direction)));
        _tabKeys.Install();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try
        {
            _dockSplit.Panel1MinSize = 150;
            _dockSplit.Panel2MinSize = 80;
            _split.Panel1MinSize = 150;
            _split.Panel2MinSize = 200;
            _split.SplitterDistance = Math.Clamp(_settings.ListWidth, 150, Math.Max(150, _split.Width - 200));
        }
        catch { /* window too small, keep defaults */ }
    }

    /// <summary>
    /// The slow part of opening: profiles, sign-in check and the server list. Runs while the splash is showing,
    /// before this window appears. Never prompts; if sign-in is needed the caller starts it once the window is up.
    /// </summary>
    public async Task<StartupResult> PrepareAsync(Action<string> report)
    {
        report("Reading AWS profiles ...");
        await LoadProfilesAsync();
        ComposeList();          // saved connections show even before (or without) an AWS sign-in
        RebuildList();
        _ = ProbeSavedAsync();

        report($"Checking AWS sign-in for \"{Profile}\" ...");
        var (arn, authErr) = await IdentityAsync();
        SetAuth(arn, authErr);
        if (arn == null)
        {
            SetStatus("Not signed in");
            return StartupResult.NeedsSignIn;
        }

        report("Loading servers from " + string.Join(", ", _settings.ScanRegions) + " ...");
        bool needLogin = await LoadInventoryAsync();
        return needLogin ? StartupResult.NeedsSignIn : StartupResult.Ready;
    }

    private bool _closeReady;        // the async clean-up (ending sessions, signing out) has finished
    private bool _signedOutByUser;   // AWS > Sign out: no background renewal until the next sign-in

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (_closeReady) { base.OnFormClosing(e); return; }
        int remote = _sessions.Count(x => x.Kind != Protocol.Local), shells = _sessions.Count - remote;
        string open = remote > 0 && shells > 0 ? $"{remote} remote session(s) and {shells} shell(s) are open"
                    : remote > 0 ? $"{remote} remote session(s) are open"
                    : $"{shells} shell(s) are open";
        if (_sessions.Count > 0 && !Ask($"{open}. Close them and exit?"))
        {
            e.Cancel = true;
            return;
        }
        _settings.Profile = Profile;
        _settings.WindowsOnly = _chkWin.Checked;
        _settings.HideOffline = _chkOffline.Checked;
        SaveColumnLayout();
        _settings.ListWidth = _split.SplitterDistance; // kept even while the list is hidden
        _settings.Save();

        _timer.Stop();
        _authTimer.Stop();
        int awsSessions = _sessions.Count(x => x.Tunnel != null);
        bool signOut = _settings.SignOutOnClose;
        Hide();
        foreach (var s in _sessions.ToList()) CloseSession(s); // also asks AWS to end each tunnelled session

        if (awsSessions == 0 && !signOut)
        {
            base.OnFormClosing(e);
            return;
        }

        // Finish the clean-up before exiting (or the "end session" calls to AWS would be cut off), and show a small
        // card while it happens so a few extra seconds aren't a mystery.
        e.Cancel = true;
        var card = new SplashForm(Theme.Current.IsDark);
        card.Show();
        try
        {
            if (awsSessions > 0)
            {
                card.SetStatus($"Ending {awsSessions} AWS session{(awsSessions == 1 ? "" : "s")} ...");
                card.Refresh();
                // ending sessions needs the AWS keys, so this comes before signing out
                await Tunnel.WaitForCleanupAsync(TimeSpan.FromSeconds(5));
            }
            if (signOut)
            {
                card.SetStatus("Signing out of AWS ...");
                card.Refresh();
                Log.Write("Closing: " + await Aws.SignOutAsync(Profile));
            }
        }
        catch (Exception ex) { Log.Write("Clean-up on close failed: " + ex.Message); }
        finally { card.Close(); }

        _closeReady = true;
        Close();
    }

    /// <summary>AWS > Sign out: end open AWS sessions' access now (tabs stay open; their tunnels keep running).</summary>
    private async Task SignOutNowAsync()
    {
        if (_busy || _renewing) return;
        try
        {
            string what = await Aws.SignOutAsync(Profile);
            Log.Write("Sign out: " + what);
            SetAuth(null);
            _signedOutByUser = true;
            SetStatus($"Signed out of AWS ({Profile}). Open sessions keep running; sign in again to connect to AWS servers.");
        }
        catch (Exception ex)
        {
            Log.Write("Sign out failed: " + ex);
            MessageBox.Show(this, "Couldn't sign out: " + ex.Message, AppInfo.ShortName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ---------------------------------------------------------------- list

    private Instance Selected() =>
        _list.SelectedItems.Count > 0 ? (Instance)_list.SelectedItems[0].Tag : null;

    /// <summary>Passes the search box and the "Windows only" / "Hide offline" filters.</summary>
    private bool Matches(Instance i)
    {
        string q = _txtSearch.Text.Trim();
        return i.IsSaved == _savedView &&
               (_savedView || !_chkWin.Checked || i.Platform == "Windows") &&
               (!_chkOffline.Checked || i.Online || _actions.ContainsKey(i.Id)) && // keep servers we're starting in view
               (q.Length == 0 ||
                $"{i.Name} {i.Type} {i.Os} {i.Id} {i.Ip} {i.Region} {StatusOf(i)} {i.Tags} {i.Saved?.Group} {i.Saved?.Description}".Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void RebuildList()
    {
        IEnumerable<Instance> items = _all.Where(Matches);

        Func<Instance, string> key = _sortProp switch
        {
            "Type" => i => i.Type,
            "Os" => i => i.Os,
            "Region" => i => i.Region,
            "Ip" => i => IpKey(i.Ip),
            "Id" => i => i.Id,
            "Status" => i => StatusOf(i),
            "User" => i => CellText(i, "User"),
            "Group" => i => (i.Saved?.Group ?? "") + "\u0001" + i.Name.ToLowerInvariant(), // by group, then name
            "Description" => i => i.Saved?.Description ?? "",
            _ => i => i.Name.ToLowerInvariant(),
        };
        _list.SortColumn = _columns.IndexOf(_sortProp); // -1 (no arrow) if that column is hidden
        _list.SortDescending = _sortDesc;
        var ordered = (_sortDesc ? items.OrderByDescending(key) : items.OrderBy(key))
            .ThenBy(i => i.Name.ToLowerInvariant()).ToList();

        string selId = Selected()?.Id;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var i in ordered)
        {
            var it = new ListViewItem(_columns.Select(c => CellText(i, c)).ToArray())
            {
                Tag = i,
                ToolTipText = Tooltip(i),
            };
            _list.Items.Add(it);
            if (i.Id == selId) it.Selected = true;
        }
        _list.EndUpdate();
        _list.FillLastColumn();
        UpdateActionStates();
        int awsCount = _all.Count(i => !i.IsSaved), savedCount = _all.Count(i => i.IsSaved);
        _tabAws.Text = $"AWS servers ({awsCount})";
        _tabSaved.Text = $"Saved RDP ({savedCount})";
        var inView = _all.Where(i => i.IsSaved == _savedView).ToList();
        SetStatus(_savedView && savedCount == 0
            ? "No saved connections yet. Click + to add one."
            : $"{ordered.Count} shown of {inView.Count} {(_savedView ? "saved connection(s)" : "instance(s)")}, " +
              $"{inView.Count(i => i.Online)} {(_savedView ? "reachable" : "online")}");
    }

    private static string IpKey(string ip) =>
        string.Join(".", (ip ?? "").Split('.').Select(p => p.PadLeft(3, '0')));

    // ---------------------------------------------------------------- AWS actions

    private async Task LoadProfilesAsync()
    {
        string cur = _lastProfile;
        var list = await ListProfilesAsync();
        if (cur.Length == 0 && list.Count > 0) cur = list[0]; // first run: nothing picked yet
        _cboProfile.Items.Clear();
        foreach (var p in list) _cboProfile.Items.Add(p);
        SelectProfile(cur); // keeps a profile that isn't set up yet in the list, so it can still be signed in
    }

    private static Task<List<string>> ListProfilesAsync() => Task.Run(() => Aws.ListProfiles());

    private Task<(string Arn, string Error)> IdentityAsync()
    {
        // Read the UI values here, then do all the AWS work (credential files, SDK set-up) on a background thread.
        string profile = Profile, region = _settings.ScanRegions.FirstOrDefault() ?? "ca-central-1";
        return Task.Run(() => Aws.IdentityAsync(profile, region));
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        SetBusy(true, "Loading instances from " + string.Join(", ", _settings.ScanRegions) + " ...");
        bool needLogin = false;
        try
        {
            needLogin = await LoadInventoryAsync();
        }
        catch (Exception ex)
        {
            Log.Write("Refresh failed: " + ex);
            MessageBox.Show(this, ex.Message, "Refresh failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { SetBusy(false, null); }

        if (needLogin && Ask("Your AWS session looks expired or missing. Sign in now?"))
            await SignInAsync();
    }

    /// <summary>Load the server list into the window. True if AWS said the sign-in has expired.</summary>
    private async Task<bool> LoadInventoryAsync()
    {
        string profile = Profile;
        var regions = _settings.ScanRegions.ToList();
        var (items, errors) = await Task.Run(() => InventoryService.LoadAsync(profile, regions));
        _aws = items;
        ComposeList();
        RebuildList();
        _ = ProbeSavedAsync();
        if (errors.Count == 0) return false;

        string all = string.Join(" | ", errors);
        Log.Write("Refresh errors: " + all);
        _status.Text += "  |  " + all;
        if (!LoginPattern.IsMatch(all)) return false;
        SetAuth(null);
        return true;
    }

    public async Task SignInAsync()
    {
        if (_busy) return;
        if (_renewing) { SetStatus("Already renewing your sign-in in the background ..."); return; }
        string profile = Profile;
        if (profile.Length == 0)
        {
            // no profiles on this PC yet: setting one up is the first step
            AddProfile();
            return;
        }

        SetBusy(true, "Checking your AWS profile ...");
        var known = await ListProfilesAsync();
        if (!known.Contains(profile, StringComparer.OrdinalIgnoreCase))
        {
            SetBusy(false, null);
            if (Ask($"'{profile}' isn't set up for Microsoft sign-in yet. Set it up now?")) SignInSetup.Run(profile, this);
            return;
        }

        SetBusy(true, "Signing in: finish the Microsoft sign-in in the browser window that opened (check the taskbar if you can't see it) ...");
        _cts = new CancellationTokenSource();
        _btnCancel.Visible = true;
        bool cancelled = false;
        string arn = null;
        string failure = "";
        try
        {
            bool isSso = Aws.IsSsoProfile(profile);
            if (!isSso && _settings.KeepSignedIn && Aws.EnsureRememberMe(profile))
                Log.Write($"Turned on 'stay logged in' for {profile} so the sign-in can be renewed automatically");

            var r = isSso
                ? await Cli.RunAsync(Tools.AwsCli, new[] { "sso", "login", "--profile", profile }, _cts.Token)
                : await RunAzureLoginAsync(_cts.Token, profile, "--mode", "gui");
            Log.Write($"Sign-in finished with code {r.Code}\n{r.Out}\n{r.Err}");

            if (r.Code == -2) cancelled = true;
            var id = cancelled ? (Arn: (string)null, Error: "") : await IdentityAsync();
            arn = id.Arn;
            if (arn == null && !cancelled)
            {
                string tail = (r.Err + "\n" + r.Out).Trim();
                if (tail.Length > 800) tail = tail[^800..];
                failure = $"Sign-in did not complete.\n\nSign-in helper said:\n{tail}\n\nAWS said:\n{id.Error}";
            }
        }
        catch (Exception ex) { failure = ex.Message; Log.Write("Sign-in error: " + ex); }
        finally
        {
            _btnCancel.Visible = false;
            _cts?.Dispose();
            _cts = null;
            SetBusy(false, null);
        }

        if (!cancelled) SetAuth(arn);
        if (arn != null) _renewFailedAt = null; // a fresh manual sign-in: allow automatic renewal again
        if (cancelled)
        {
            SetStatus("Sign-in cancelled");
            return;
        }
        if (arn == null)
        {
            SetStatus("Sign-in did not complete");
            MessageBox.Show(this, failure, "Sign in", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        await RefreshAsync();
        _status.Text = $"Signed in as {arn}  |  {_status.Text}";
    }

    // ---------------------------------------------------------------- connecting

    /// <summary>Linux servers default to SSH, Windows to Remote Desktop; a saved connection uses its own.</summary>
    private static Protocol DefaultProtocol(Instance i) =>
        i.IsSaved ? i.Saved.Kind : i.Platform == "Windows" ? Protocol.Rdp : Protocol.Ssh;

    private async Task ConnectAsync(Instance inst, Protocol? want = null)
    {
        var protocol = want ?? DefaultProtocol(inst);
        string openingKey = $"{inst.Id}|{protocol}";
        var existing = _sessions.FirstOrDefault(s => s.Inst.Id == inst.Id && s.Kind == protocol);
        if (existing != null) { _tabs.SelectedTab = existing.Page; return; }
        if (!_opening.Add(openingKey)) return;

        try
        {
            if (!inst.Online && !inst.IsSaved)
            {
                if (_actions.ContainsKey(inst.Id))
                {
                    SetStatus($"{inst.Name} isn't ready yet: {_actions[inst.Id]}");
                    return;
                }
                if (inst.IsEc2 && inst.State == "stopped")
                {
                    if (Ask($"{inst.Name} is stopped. Start it and connect as soon as it's ready?"))
                    {
                        _opening.Remove(openingKey);
                        await InstanceActionAsync(inst, InstanceActions.Action.Start, confirm: false, connectWhenReady: true);
                    }
                    return;
                }
                string why = inst.State switch
                {
                    "pending" => "is still starting. Try again in a minute.",
                    "stopping" or "shutting-down" => "is shutting down.",
                    "running" when !inst.Managed => "isn't managed by Systems Manager (no SSM agent registered), so Switchboard can't reach it.",
                    _ => "is not Online in Systems Manager, so it can't be reached right now.",
                };
                MessageBox.Show(this, $"{inst.Name} {why}", "Cannot connect", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (protocol == Protocol.Ssh)
            {
                await OpenSshAsync(inst);
                return;
            }

            string clsid = RdpHost.FindClsid();
            if (clsid == null)
            {
                MessageBox.Show(this, "The Windows Remote Desktop control (mstscax.dll) isn't registered on this PC.",
                    "Cannot connect", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (!inst.IsSaved && !await EnsureSignedInAsync($"connecting to {inst.Name}"))
            {
                SetStatus($"Not connected to {inst.Name}: you're not signed in to AWS");
                return;
            }

            Tunnel tunnel = null;
            for (int attempt = 1; tunnel == null && !inst.IsSaved; attempt++)
            {
                SetStatus($"Opening tunnel to {inst.Name} ...");
                try { tunnel = await Tunnel.StartAsync(inst, Profile); }
                catch (TunnelException ex)
                {
                    Log.Write($"Tunnel to {inst.Id} failed: {ex.Message}");
                    if (LoginPattern.IsMatch(ex.Message) && attempt == 1)
                    {
                        // the sign-in ran out without us noticing: sign in, then try once more
                        SetAuth(null);
                        if (await EnsureSignedInAsync($"connecting to {inst.Name}", knownExpired: true)) continue;
                        SetStatus($"Not connected to {inst.Name}: you're not signed in to AWS");
                        return;
                    }
                    SetStatus("Tunnel failed");
                    MessageBox.Show(this, ex.Message, "Could not open the tunnel", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            var page = new TabPage(inst.Name) { BackColor = Theme.Current.Back };
            var host = new RdpHost(clsid) { Dock = DockStyle.Fill };
            host.Enter += (_, _) => _tabKeys?.Install();   // stay ahead of the RDP control's own keyboard hook
            host.FocusReleased += direction => SwitchTab(direction < 0 ? -1 : +1);   // Ctrl+Alt+Left / Right
            page.Controls.Add(host);
            _tabs.TabPages.Add(page);
            _tabs.Visible = true;
            _hint.Visible = false;
            _tabs.SelectedTab = page;

            var session = new Session { Inst = inst, Tunnel = tunnel, Rdp = host, Page = page };
            _sessions.Add(session);

            try
            {
                await StartRdpAsync(session);
                SetStatus($"Connecting to {inst.Name} ...");
            }
            catch (Exception ex)
            {
                Log.Write($"RDP start for {inst.Id} failed: {ex}");
                CloseSession(session);
                MessageBox.Show(this, ex.Message, "Could not start Remote Desktop", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally { _opening.Remove(openingKey); }
    }

    // ---------------------------------------------------------------- tab keys

    /// <summary>
    /// Keys while the main window itself has focus (list, toolbar ...). Inside a terminal the page catches them
    /// (OnAppShortcut); inside a remote desktop only Ctrl+Alt+Left/Right get through (RdpHost.FocusReleased).
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.Tab:
            case Keys.Control | Keys.Alt | Keys.Right:
                if (_tabs.TabCount > 0) { SwitchTab(+1); return true; }
                break;
            case Keys.Control | Keys.Shift | Keys.Tab:
            case Keys.Control | Keys.Alt | Keys.Left:
                if (_tabs.TabCount > 0) { SwitchTab(-1); return true; }
                break;
            case Keys.Control | Keys.Shift | Keys.W:
                if (_tabs.TabCount > 0) { CloseCurrentTab(); return true; }
                break;
            case Keys.Control | Keys.Shift | Keys.T:
                _ = OpenShellAsync(Shells.Default(_settings.DefaultShell));
                return true;
            case Keys.Control | Keys.Oemtilde:   // Ctrl+`
                ToggleDock();
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private TabHotkeyHook _tabKeys;

    /// <summary>
    /// Whether the tab-key hook should take Ctrl+Alt+arrow now: Switchboard's main window is the active window, there
    /// are tabs, and the keyboard isn't in a terminal (terminals handle these keys themselves, and a docked one moves
    /// between the docked terminals). Called from the hook, on the UI thread.
    /// </summary>
    private bool TabKeysApply()
    {
        if (IsDisposed || !IsHandleCreated || GetForegroundWindow() != Handle || _tabs.TabCount == 0) return false;
        Control c = ActiveControl;
        while (c is ContainerControl cc && cc.ActiveControl != null && cc.ActiveControl != c) c = cc.ActiveControl;
        for (var p = c; p != null; p = p.Parent)
            if (p is TerminalHost) return false;
        return true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    /// <summary>A tab key pressed inside a terminal tab.</summary>
    private void OnAppShortcut(string key, Session from)
    {
        bool docked = from is { Docked: true };
        switch (key)
        {
            case "next": SwitchTabIn(docked ? _dockTabs : _tabs, +1); break;
            case "prev": SwitchTabIn(docked ? _dockTabs : _tabs, -1); break;
            case "close": CloseWithConfirm(from ?? _sessions.FirstOrDefault(x => x.Page == _tabs.SelectedTab)); break;
            case "newshell": _ = OpenShellAsync(Shells.Default(_settings.DefaultShell), docked); break;
            case "dock": ToggleDock(); break;
        }
    }

    /// <summary>Move to the next (+1) or previous (-1) tab, wrapping around at the ends, and give it the keyboard.</summary>
    private void SwitchTab(int direction) => SwitchTabIn(_tabs, direction);

    /// <summary>Next / previous tab within one tab strip (the main tabs, or the docked terminals).</summary>
    private void SwitchTabIn(TabControl strip, int direction)
    {
        int count = strip.TabCount;
        if (count == 0) return;
        int next = ((strip.SelectedIndex + direction) % count + count) % count;
        if (next != strip.SelectedIndex) strip.SelectedIndex = next;   // SelectedIndexChanged focuses the new tab
        else
        {
            var only = _sessions.FirstOrDefault(x => x.Page == strip.SelectedTab);   // one tab: just give it focus back
            only?.Term?.FocusTerminal();
            if (only?.Rdp != null) { try { only.Rdp.Focus(); } catch { } }
        }
    }

    /// <summary>Close the tab in front, after asking (keyboard and menu; × and middle-click close straight away).</summary>
    private void CloseCurrentTab() => CloseWithConfirm(_sessions.FirstOrDefault(x => x.Page == _tabs.SelectedTab));

    /// <summary>Close a tab or docked terminal after asking.</summary>
    private void CloseWithConfirm(Session s)
    {
        if (s == null) return;
        string question = s.Kind == Protocol.Local
            ? $"Close {s.Inst.Name}?\n\nAnything still running in it will stop."
            : $"Disconnect from {s.Inst.Name}?\n\nThe server keeps running; only this tab closes.";
        if (!Ask(question))
        {
            // back to the tab's content, so the keys go where they were going
            s.Term?.FocusTerminal();
            if (s.Rdp != null) { try { s.Rdp.Focus(); } catch { } }
            return;
        }
        bool wasDocked = s.Docked;
        CloseSession(s);
        // keep going from the tab that's now in front
        var next = _sessions.FirstOrDefault(x => x.Page == (wasDocked && _dockTabs.TabCount > 0 ? _dockTabs : _tabs).SelectedTab);
        next?.Term?.FocusTerminal();
        if (next?.Rdp != null) { try { next.Rdp.Focus(); } catch { } }
    }

    // ---------------------------------------------------------------- docked terminals

    private Session SelectedDocked() => _sessions.FirstOrDefault(x => x.Docked && x.Page == _dockTabs.SelectedTab);

    /// <summary>Move a terminal from its tab into the docked panel; it carries on running.</summary>
    private void DockSession(Session s)
    {
        if (s?.Term == null || s.Docked) return;
        _tabs.TabPages.Remove(s.Page);
        s.Docked = true;
        _dockTabs.TabPages.Add(s.Page);
        _dockTabs.SelectedTab = s.Page;
        UpdateMainTabsVisibility();
        ShowDock(true);
        s.Term.FocusTerminal();
    }

    /// <summary>Move a docked terminal back to a tab of its own.</summary>
    private void UndockSession(Session s)
    {
        if (s == null || !s.Docked) return;
        _dockTabs.TabPages.Remove(s.Page);
        s.Docked = false;
        _tabs.TabPages.Add(s.Page);
        _tabs.SelectedTab = s.Page;
        UpdateMainTabsVisibility();
        if (_dockTabs.TabCount == 0) ShowDock(false); else UpdateDockTitle();
        s.Term?.FocusTerminal();
    }

    private void UpdateMainTabsVisibility()
    {
        bool any = _tabs.TabCount > 0;
        _tabs.Visible = any;
        _hint.Visible = !any;
    }

    /// <summary>Show or hide the docked panel (hidden terminals keep running). Never shown empty.</summary>
    private void ShowDock(bool show)
    {
        if (_dockTabs.TabCount == 0) show = false;
        UpdateDockTitle();
        if (!show)
        {
            // nothing left to dock: the panel goes away completely (and starts expanded next time)
            if (_dockMinimized) SetDockMinimized(false, apply: false);
            _dockSplit.Panel2Collapsed = true;
            return;
        }
        if (_dockSplit.Panel2Collapsed)
        {
            _dockSplit.Panel2Collapsed = false;
            if (!_dockMinimized) ApplyDockHeight();
        }
        if (_dockMinimized) SetDockMinimized(false);   // something new was docked: show it
        else SelectedDocked()?.Term?.FocusTerminal();
    }

    /// <summary>
    /// Collapse the panel to its header strip (title, count and an up arrow) or expand it back. The terminals keep
    /// running while collapsed, and the bar can't be dragged.
    /// </summary>
    private void SetDockMinimized(bool minimized, bool apply = true)
    {
        _dockMinimized = minimized;
        _btnHideDock.Text = minimized ? Icons.ShowUp : Icons.HideDown;
        _btnHideDock.ToolTipText = minimized
            ? "Show the docked terminals (Ctrl+`)"
            : "Collapse the docked terminals to this strip (Ctrl+`). They keep running.";
        UpdateDockTitle();
        if (!apply) return;

        _dockTabs.Visible = !minimized;
        _dockSplit.IsSplitterFixed = minimized;
        try
        {
            if (minimized)
            {
                int strip = _dockBar.PreferredSize.Height + 2;
                _dockSplit.Panel2MinSize = strip;
                _dockSplit.SplitterDistance = Math.Max(_dockSplit.Panel1MinSize, _dockSplit.Height - _dockSplit.SplitterWidth - strip);
            }
            else
            {
                _dockSplit.Panel2MinSize = 80;
                ApplyDockHeight();
                SelectedDocked()?.Term?.FocusTerminal();
            }
        }
        catch { /* window too small right now */ }
    }

    /// <summary>A click on the dock's header: act once, ignoring a second handler for the same click.</summary>
    private void ToggleDockFromClick(bool minimize)
    {
        if ((DateTime.Now - _dockToggledAt).TotalMilliseconds < 400) return;
        _dockToggledAt = DateTime.Now;
        SetDockMinimized(minimize);
    }

    /// <summary>"Docked terminals", plus how many while collapsed (so it's clear what the strip holds).</summary>
    private void UpdateDockTitle() =>
        _dockTitle.Text = _dockMinimized && _dockTabs.TabCount > 0 ? $"Docked terminals ({_dockTabs.TabCount})" : "Docked terminals";

    /// <summary>View > Docked terminals, Ctrl+`.</summary>
    private void ToggleDock()
    {
        if (_dockTabs.TabCount == 0)
        {
            SetStatus("No docked terminals yet: right-click a terminal tab and choose Dock to bottom.");
            return;
        }
        if (_dockSplit.Panel2Collapsed) ShowDock(true);
        else SetDockMinimized(!_dockMinimized);
    }

    /// <summary>Put the bar where the remembered panel height says, within sensible limits.</summary>
    private void ApplyDockHeight()
    {
        try
        {
            int total = _dockSplit.Height - _dockSplit.SplitterWidth;
            int h = Math.Clamp(_settings.DockHeight, 80, Math.Max(80, total - 150));
            _dockSplit.SplitterDistance = Math.Max(_dockSplit.Panel1MinSize, total - h);
        }
        catch { /* window too small right now */ }
    }

    // ---------------------------------------------------------------- local shells

    /// <summary>One menu entry per shell on this PC (the default marked), in a toolbar, File or + menu.</summary>
    private void FillShellMenu(ToolStripItemCollection items, ToolStrip owner, bool docked = false)
    {
        items.Clear();
        var def = Shells.Default(_settings.DefaultShell);
        foreach (var shell in Shells.Available())
        {
            var item = new ToolStripMenuItem(shell.Id == def.Id ? shell.Name + "  (default, Ctrl+T)" : shell.Name);
            var chosen = shell;
            item.Click += async (_, _) => await OpenShellAsync(chosen, docked);
            items.Add(item);
        }
        Theme.ApplyMenu(owner);
    }

    /// <summary>Environment for a shell: AWS_PROFILE set to the profile in use, and the bundled tools on PATH.</summary>
    private Dictionary<string, string> ShellEnvironment()
    {
        var env = new Dictionary<string, string>();
        if (Profile.Length > 0) env["AWS_PROFILE"] = Profile;
        var toolDirs = new[] { "aws-cli", "session-manager-plugin", "node" }
            .Select(d => Path.Combine(Tools.Dir, d)).Where(Directory.Exists).ToList();
        if (toolDirs.Count > 0)
            env["PATH"] = string.Join(";", toolDirs) + ";" + (Environment.GetEnvironmentVariable("PATH") ?? "");
        return env;
    }

    /// <summary>
    /// The shell's command line. PowerShell gets the terminal colour scheme's own colours (prompt, file lists, syntax)
    /// when a scheme is chosen and "Also colour PowerShell" is on: passed inline, so execution policy doesn't apply,
    /// and run after your profile, which keeps its own prompt.
    /// </summary>
    private string ShellCommandLine(ShellInfo shell)
    {
        bool powershell = shell.Id is "pwsh" or "powershell";
        if (!powershell || !_settings.StylePowerShell || Theme.TerminalScheme == null) return shell.CommandLine;
        return shell.CommandLine + " -NoExit -EncodedCommand " + TerminalSchemes.EncodedStyling(Theme.TerminalScheme);
    }

    /// <summary>Open a local shell in a new tab, starting in the user folder.</summary>
    private async Task OpenShellAsync(ShellInfo shell, bool docked = false)
    {
        docked |= _settings.DockNewTerminals;   // Settings > Terminal & SSH > Open new terminals docked
        // "PowerShell", then "PowerShell 2", "PowerShell 3" ... for more of the same kind
        var used = new HashSet<string>(_sessions.Where(x => x.Kind == Protocol.Local).Select(x => x.Inst.Name));
        string name = shell.Name;
        for (int n = 2; used.Contains(name); n++) name = $"{shell.Name} {n}";

        var inst = new Instance { Name = name, Id = "shell:" + Guid.NewGuid().ToString("N"), Type = shell.Id };
        var page = new TabPage(ThemedTabControl.IconSpace + name) { BackColor = Theme.Current.Back, Tag = ThemedTabControl.ShellTag };
        var term = new TerminalHost { Dock = DockStyle.Fill, EndedAction = "restart" };
        term.AppShortcut += key => OnAppShortcut(key, _sessions.FirstOrDefault(x => x.Term == term));
        page.Controls.Add(term);
        var session = new Session { Inst = inst, Kind = Protocol.Local, Term = term, Page = page, Docked = docked };
        _sessions.Add(session);
        if (docked)
        {
            _dockTabs.TabPages.Add(page);
            _dockTabs.SelectedTab = page;
            ShowDock(true);
        }
        else
        {
            _tabs.TabPages.Add(page);
            _tabs.Visible = true;
            _hint.Visible = false;
            _tabs.SelectedTab = page;
        }
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        term.SessionEnded += _ => page.Text = ThemedTabControl.IconSpace + name + " (ended)";
        term.ReconnectRequested += () =>
        {
            page.Text = ThemedTabControl.IconSpace + name;
            term.Reset();
            term.Run(ShellCommandLine(shell), home, ShellEnvironment());
        };

        try
        {
            await term.StartAsync();
        }
        catch (Exception ex)
        {
            Log.Write("Terminal failed to load: " + ex);
            CloseSession(session);
            string msg = ex is Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException
                ? "The Microsoft Edge WebView2 Runtime isn't installed, and the terminal needs it.\n\n" +
                  "It's normally part of Windows 10/11; it can be installed from Microsoft's WebView2 download page."
                : ex.Message;
            MessageBox.Show(this, msg, "Could not open the terminal", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        term.Run(ShellCommandLine(shell), home, ShellEnvironment());
        term.FocusTerminal();
        SetStatus($"Opened {name}" + (Profile.Length > 0 ? $" (AWS_PROFILE={Profile})" : ""));
    }

    // ---------------------------------------------------------------- SSH

    /// <summary>Open an SSH terminal tab: tunnel to port 22 for AWS servers (direct for saved ones), then ssh.exe.</summary>
    private async Task OpenSshAsync(Instance inst)
    {
        if (Tools.Ssh == null)
        {
            MessageBox.Show(this, "The OpenSSH client isn't installed on this PC.\n\n" +
                "Add it in Windows Settings > Apps > Optional features > OpenSSH Client, then try again.",
                "Cannot connect", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!inst.IsSaved && !await EnsureSignedInAsync($"connecting to {inst.Name}"))
        {
            SetStatus($"Not connected to {inst.Name}: you're not signed in to AWS");
            return;
        }

        var page = new TabPage(inst.Name + " · SSH") { BackColor = Theme.Current.Back };
        var term = new TerminalHost { Dock = DockStyle.Fill };
        term.AppShortcut += key => OnAppShortcut(key, _sessions.FirstOrDefault(x => x.Term == term));
        page.Controls.Add(term);
        var session = new Session { Inst = inst, Kind = Protocol.Ssh, Term = term, Page = page, Docked = _settings.DockNewTerminals };
        _sessions.Add(session);
        if (session.Docked)
        {
            _dockTabs.TabPages.Add(page);
            _dockTabs.SelectedTab = page;
            ShowDock(true);
        }
        else
        {
            _tabs.TabPages.Add(page);
            _tabs.Visible = true;
            _hint.Visible = false;
            _tabs.SelectedTab = page;
        }
        term.SessionEnded += _ =>
        {
            page.Text = inst.Name + " · SSH (ended)";
            session.Tunnel?.Dispose();   // nothing is using it any more
            session.Tunnel = null;
        };
        term.ReconnectRequested += async () => await RunSshAsync(session, reconnect: true);

        try
        {
            await term.StartAsync();
        }
        catch (Exception ex)
        {
            Log.Write("Terminal failed to load: " + ex);
            CloseSession(session);
            string msg = ex is Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException
                ? "The Microsoft Edge WebView2 Runtime isn't installed, and the SSH terminal needs it.\n\n" +
                  "It's normally part of Windows 10/11; it can be installed from Microsoft's WebView2 download page."
                : ex.Message;
            MessageBox.Show(this, msg, "Could not open the terminal", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        await RunSshAsync(session, reconnect: false);
    }

    /// <summary>(Re)start ssh in an SSH tab, opening a fresh tunnel first for AWS servers.</summary>
    private async Task RunSshAsync(Session s, bool reconnect)
    {
        var inst = s.Inst;
        var term = s.Term;
        if (term == null || term.IsDisposed) return;
        if (reconnect)
        {
            term.Reset();
            s.Page.Text = inst.Name + " · SSH";
        }

        if (!inst.IsSaved)
        {
            s.Tunnel?.Dispose();
            s.Tunnel = null;
            if (reconnect && !await EnsureSignedInAsync($"reconnecting to {inst.Name}"))
            {
                term.Fail("Not signed in to AWS.");
                return;
            }

            term.Notice($"Opening a Session Manager tunnel to {inst.Name} ({inst.Id}, port 22) ...");
            SetStatus($"Opening SSH tunnel to {inst.Name} ...");
            for (int attempt = 1; s.Tunnel == null; attempt++)
            {
                try { s.Tunnel = await Tunnel.StartAsync(inst, Profile, 22); }
                catch (TunnelException ex)
                {
                    Log.Write($"SSH tunnel to {inst.Id} failed: {ex.Message}");
                    if (LoginPattern.IsMatch(ex.Message) && attempt == 1)
                    {
                        SetAuth(null);
                        if (await EnsureSignedInAsync($"connecting to {inst.Name}", knownExpired: true)) continue;
                    }
                    term.Fail("Couldn't open the tunnel: " + ex.Message);
                    SetStatus("SSH tunnel failed");
                    return;
                }
            }
            if (term.IsDisposed) { s.Tunnel?.Dispose(); return; } // tab closed while the tunnel was opening
        }

        string cmd = SshCommand(inst, s.Tunnel);
        Log.Write("SSH: " + cmd);
        term.Notice(cmd.Replace(Tools.Ssh, "ssh").Replace("\"ssh\"", "ssh"));
        term.Run(cmd);
        term.FocusTerminal();
        SetStatus($"SSH to {inst.Name}");
    }

    /// <summary>The ssh.exe command line for a server: through the tunnel on localhost, or straight to a saved host.</summary>
    private string SshCommand(Instance inst, Tunnel tunnel)
    {
        var args = new List<string>();
        string user, host;
        int port;
        if (inst.IsSaved)
        {
            user = inst.Saved.User.Trim();
            host = inst.Saved.Host;
            port = inst.Saved.Port;
        }
        else
        {
            user = string.IsNullOrWhiteSpace(_settings.SshUser) ? GuessSshUser(inst) : _settings.SshUser.Trim();
            host = "127.0.0.1";
            port = tunnel.Port;
            // remember the server's host key by instance ID, not by the tunnel's random local port
            args.Add("-o"); args.Add("HostKeyAlias=" + inst.Id);
        }
        args.Add("-p"); args.Add(port.ToString());
        args.Add("-o"); args.Add("ServerAliveInterval=30");
        if (!string.IsNullOrWhiteSpace(_settings.SshKeyFile)) { args.Add("-i"); args.Add(_settings.SshKeyFile.Trim()); }
        args.Add(user.Length > 0 ? $"{user}@{host}" : host);
        return Quote(Tools.Ssh) + " " + string.Join(" ", args.Select(Quote));
    }

    /// <summary>The usual login for common Linux images on EC2.</summary>
    private static string GuessSshUser(Instance i)
    {
        string os = (i.Os + " " + i.OsFull).ToLowerInvariant();
        return os.Contains("ubuntu") ? "ubuntu"
            : os.Contains("debian") ? "admin"
            : os.Contains("centos") ? "centos"
            : os.Contains("rocky") ? "rocky"
            : os.Contains("fedora") ? "fedora"
            : os.Contains("bitnami") ? "bitnami"
            : "ec2-user"; // Amazon Linux, RHEL, SUSE, AlmaLinux, Oracle Linux ...
    }

    private static string Quote(string a) =>
        a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"";

    private async Task StartRdpAsync(Session s)
    {
        // the connection's own user if it has one, else the default from Settings > RDP credentials;
        // no saved password means Remote Desktop asks
        string user = s.Inst.Saved is { User.Length: > 0 } c ? c.User.Trim() : (_settings.Username ?? "").Trim();
        string password = s.Inst.Saved != null && _adHocPasswords.TryGetValue(s.Inst.Saved.Id, out var oneOff)
            ? oneOff                                  // typed for a one-off connection, never stored
            : CredentialStore.Read(user) ?? "";
        bool direct = s.Inst.IsSaved;
        string server = direct ? s.Inst.Saved.Host : "localhost";
        int port = direct ? s.Inst.Saved.Port : s.Tunnel.Port;

        // The ActiveX control only exists once its tab is on screen; give it a moment.
        Exception last = null;
        for (int attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                s.Rdp.CreateControl();
                s.Rdp.Start(server, port, user, password, direct);
                return;
            }
            catch (Exception ex) when (ex is AxHost.InvalidActiveXStateException || ex is InvalidOperationException)
            {
                last = ex;
                await Task.Delay(100);
            }
        }
        throw new InvalidOperationException("The Remote Desktop control did not start. " + last?.Message);
    }

    private void PollSessions()
    {
        foreach (var s in _sessions.ToList())
        {
            if (s.Rdp == null) continue; // SSH tabs report their own end
            int state;
            try { state = s.Rdp.ConnectionState(); }
            catch { continue; }

            if (state != 0)
            {
                s.SawActivity = true;
                if (state == 1 && !s.WasConnected)
                {
                    s.WasConnected = true;
                    SetStatus($"Connected to {s.Inst.Name}");
                    s.Rdp.SyncSize(); // in case the window changed size while connecting
                }
            }
            else if (s.WasConnected)
            {
                SetStatus($"{s.Inst.Name} disconnected");
                CloseSession(s);
            }
            else if (!s.Flagged && (s.SawActivity || (DateTime.Now - s.Started).TotalSeconds > 45))
            {
                s.Flagged = true;
                s.Page.Text = s.Inst.Name + " (not connected)";
            }
        }
    }

    private void CloseSession(Session s)
    {
        _sessions.Remove(s);
        try { s.Rdp?.SafeDisconnect(); } catch { }
        try { s.Term?.Dispose(); } catch { }  // ends ssh
        if (s.Inst?.Saved != null) _adHocPasswords.Remove(s.Inst.Saved.Id);
        try { (s.Docked ? _dockTabs : _tabs).TabPages.Remove(s.Page); } catch { }
        try { s.Page.Dispose(); } catch { }
        s.Tunnel?.Dispose(); // saved connections have no tunnel
        UpdateMainTabsVisibility();
        if (_dockTabs.TabCount == 0) ShowDock(false); else UpdateDockTitle();
    }

    private void OnTabMouseUp(object sender, MouseEventArgs e)
    {
        var strip = sender as TabControl ?? _tabs;
        for (int i = 0; i < strip.TabCount; i++)
        {
            if (!strip.GetTabRect(i).Contains(e.Location)) continue;
            var page = strip.TabPages[i];
            var session = _sessions.FirstOrDefault(x => x.Page == page);
            if (session == null) return;

            if (e.Button == MouseButtons.Middle) CloseSession(session);
            else if (e.Button == MouseButtons.Right)
            {
                _menuTarget = session;
                _tabMenu.Show(strip, e.Location);
            }
            return;
        }
    }

    // ---------------------------------------------------------------- helpers

    private void SetBusy(bool busy, string message)
    {
        _busy = busy;
        _btnRefresh.Enabled = _btnSignIn.Enabled = _cboProfile.Enabled = !busy;
        UpdateSignOutButton();
        _progress.Visible = busy;
        if (busy)
        {
            _busyStart = DateTime.Now;
            _busyMessage = message ?? "Working ...";
            _busyTimer.Start();
            UpdateBusyText();
        }
        else
        {
            _busyTimer.Stop();
            _hint.Text = DefaultHint;
            if (message != null) SetStatus(message);
        }
    }

    private void UpdateBusyText()
    {
        if (!_busy) return;
        int secs = (int)(DateTime.Now - _busyStart).TotalSeconds;
        SetStatus($"{_busyMessage} ({secs}s)");
        if (_sessions.Count == 0) _hint.Text = $"{_busyMessage}\n\n{secs}s elapsed";
    }

    private void SetStatus(string text) => _status.Text = text;

    // ---------------------------------------------------------------- settings, theme, sign-in status

    private async Task OpenSettingsAsync(SettingsForm.Page page)
    {
        if (OwnedForms.OfType<SettingsForm>().FirstOrDefault() is { } open) { open.Activate(); return; }

        bool keepBefore = _settings.KeepSignedIn;
        using var f = new SettingsForm(_settings, Profile, _cboProfile.Items.Cast<object>().Select(o => o.ToString()).Where(o => o != AddProfileItem), ApplyAppearance, page,
            awsSessionsOpen: _sessions.Any(x => x.Tunnel != null));
        f.ShowDialog(this);
        if (_settings.KeepSignedIn != keepBefore) await SetKeepSignedInAsync(_settings.KeepSignedIn);

        bool profileChanged = f.SelectedProfile.Length > 0 &&
                              !string.Equals(f.SelectedProfile, Profile, StringComparison.OrdinalIgnoreCase);
        foreach (var gone in f.RemovedProfiles) ForgetProfile(gone);
        if (profileChanged || !_cboProfile.Items.Contains(f.SelectedProfile)) await LoadProfilesAsync();
        if (profileChanged) SelectProfile(f.SelectedProfile);
        _settings.Profile = Profile;
        _settings.Save();

        if (f.SignInRequested) await SignInAsync();
        else if (profileChanged || f.RegionsChanged)
        {
            if (profileChanged) await CheckAuthAsync();
            await RefreshAsync();
        }
    }

    /// <summary>Switch light/dark everywhere: this window, its menus and an open Settings window.</summary>
    /// <summary>View > Dark mode: an explicit choice of light or dark (leaves "Match Windows").</summary>
    private void SetDarkMode(bool dark)
    {
        _settings.ThemeMode = dark ? "Dark" : "Light";
        _settings.DarkMode = dark;
        ApplyAppearance();
    }

    /// <summary>Save the appearance settings and restyle this window, its menus, terminals and any open Settings window.</summary>
    private void ApplyAppearance()
    {
        _settings.DarkMode = _settings.EffectiveDark; // keeps older versions' setting in step
        _settings.Save();
        RestyleEverything();
    }

    private void RestyleEverything()
    {
        _miDark.Checked = _settings.EffectiveDark;
        ApplyTheme();
        foreach (var f in OwnedForms.OfType<SettingsForm>()) f.ApplyTheme();
    }

    /// <summary>Windows' colours or light/dark setting changed: follow them if Appearance says to.</summary>
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle))
            return;
        bool followsWindows = _settings.ThemeMode == "System" ||
                              string.Equals(_settings.AccentColor, "Windows", StringComparison.OrdinalIgnoreCase);
        if (followsWindows) OnUiThread(RestyleEverything);
    }

    private async Task CheckAuthAsync()
    {
        if (_authChecking) return;
        _authChecking = true;
        try
        {
            if (_authArn == null) ShowAuth("Status: Checking ...", null);
            var (arn, err) = await IdentityAsync();
            SetAuth(arn, err);
        }
        finally { _authChecking = false; }
    }

    /// <summary>
    /// Every 30 seconds: update the time left, and ask AWS "who am I?" every 5 minutes, or straight away
    /// once the recorded expiry time has passed. Quiet: it only changes the status, it never pops anything up.
    /// </summary>
    private async Task AuthHeartbeatAsync()
    {
        if (_lastAuthCheck == DateTime.MinValue || _busy || _renewing) return; // startup or a sign-in is in progress
        if (_paused) return;                                                     // locked or asleep: nobody to sign in
        if (!_settings.KeepSignedIn)
        {
            UpdateAuthDisplay(); // countdown from the local credentials file only; no AWS calls, no renewals
            return;
        }
        if (ShouldRenew())
        {
            await RenewAsync();
            return;
        }
        bool expiredByClock = _authArn != null && _authExpiry is { } exp && exp <= DateTimeOffset.Now;
        if (expiredByClock || DateTime.Now - _lastAuthCheck >= AuthCheckEvery)
        {
            bool wasSignedIn = _authArn != null;
            await CheckAuthAsync();
            if (wasSignedIn && _authArn == null && _authError == null)
                SetStatus("Your AWS sign-in has expired. Open sessions keep working; click Sign in before connecting to another server.");
        }
        else UpdateAuthDisplay();
    }

    /// <summary>aws-azure-login from the bundled tools folder if it's there, otherwise the installed one.</summary>
    /// <summary>
    /// Run aws-azure-login for a profile, asking Entra for the profile's own account (azure_default_username)
    /// via login_hint, so it doesn't silently use the account signed in to Windows. The log says whether it applied.
    /// </summary>
    private static async Task<(int Code, string Out, string Err)> RunAzureLoginAsync(CancellationToken ct, string profile,
        params string[] extraArgs)
    {
        string hint = Aws.AzureDefaultUsername(profile);
        Log.Write(hint.Length > 0
            ? $"Sign-in for {profile}: asking Entra for {hint} (login_hint)"
            : $"Sign-in for {profile}: no azure_default_username in ~/.aws/config, so no account hint is sent");

        // only ever Puppeteer's own Chromium: if it isn't there, say so rather than open any other browser
        string browserProblem = Tools.SignInBrowserProblem();
        if (browserProblem != null)
        {
            Log.Write("Sign-in not started: " + browserProblem.Replace("\n", " "));
            return (1, "", browserProblem);
        }

        var args = new List<string> { "--profile", profile };
        args.AddRange(extraArgs);
        var (file, list, env) = Tools.AzureLogin(hint, args.ToArray());
        var r = await Cli.RunAsync(file, list, ct, env);

        // the shim reports each time it adds the hint
        string output = (r.Out ?? "") + "\n" + (r.Err ?? "");
        bool applied = false;
        foreach (var line in output.Split('\n').Where(l => l.Contains("[switchboard]")))
        {
            Log.Write("aws-azure-login: " + line.Trim());
            applied |= line.Contains("login_hint=");
        }
        if (hint.Length > 0 && !applied && r.Code != -2)
            Log.Write("The login hint wasn't applied: aws-azure-login didn't open the Microsoft page the way the shim expects.");
        return r;
    }

    // ---------------------------------------------------------------- locked / asleep

    /// <summary>Follow Windows locking, unlocking, sleeping and waking (events are unhooked when the window closes).</summary>
    private void WatchSessionState()
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged; // "Match Windows" theme / Windows accent
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged; // monitors changed: re-send the taskbar icon
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // SystemEvents are static: leaving these attached would keep the window alive and call into it after closing
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        _tabKeys?.Dispose();
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        base.OnFormClosed(e);
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e) => OnUiThread(() =>
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                PauseBackgroundSignIn("Windows was locked");
                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                ResumeBackgroundSignIn("Windows was unlocked");
                break;
        }
    });

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e) => OnUiThread(() =>
    {
        if (e.Mode == PowerModes.Suspend) PauseBackgroundSignIn("the PC is going to sleep");
        else if (e.Mode == PowerModes.Resume) ResumeBackgroundSignIn("the PC woke up");
    });

    private void OnUiThread(Action a)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch (ObjectDisposedException) { } catch (InvalidOperationException) { }
    }

    /// <summary>
    /// Locked or asleep: stop all background sign-in work, including a renewal already running, so nothing signs
    /// in (or sends authenticator prompts) while nobody is there. Open RDP/SSH tabs are not affected.
    /// </summary>
    private void PauseBackgroundSignIn(string why)
    {
        if (_paused) return;
        _paused = true;
        Log.Write($"Background sign-in paused: {why}");
        try { _renewCts?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>Unlocked or awake: carry on, with the next sign-in check about 30-60 seconds later (network first).</summary>
    private void OnDisplaySettingsChanged(object sender, EventArgs e) => OnUiThread(() =>
    {
        TaskbarIcon.Apply(this);
        RedrawAfterWake();
    });

    /// <summary>
    /// After sleep, unlock or a display change, Windows doesn't always ask every part of the window to repaint
    /// (particularly next to the GPU-drawn remote desktop and terminals), so some controls stayed blank until
    /// hovered. Force a full redraw of every Switchboard window now and again a little later, once the display has
    /// settled, and put the themed title bars back.
    /// </summary>
    private void RedrawAfterWake()
    {
        RedrawAllWindows();
        foreach (int delay in new[] { 1000, 3000 })
        {
            var t = new System.Windows.Forms.Timer { Interval = delay };
            t.Tick += (_, _) => { t.Stop(); t.Dispose(); RedrawAllWindows(); };
            t.Start();
        }
    }

    private static void RedrawAllWindows()
    {
        foreach (Form f in Application.OpenForms)
        {
            if (f.IsDisposed || !f.IsHandleCreated || !f.Visible) continue;
            try
            {
                Theme.ApplyTitleBar(f);
                RedrawWindow(f.Handle, IntPtr.Zero, IntPtr.Zero,
                    RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW);
            }
            catch { /* a window closing as we go */ }
        }
    }

    private const uint RDW_INVALIDATE = 0x0001, RDW_ERASE = 0x0004, RDW_FRAME = 0x0400,
                       RDW_ALLCHILDREN = 0x0080, RDW_UPDATENOW = 0x0100;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr rcUpdate, IntPtr hrgnUpdate, uint flags);

    private void ResumeBackgroundSignIn(string why)
    {
        TaskbarIcon.Apply(this);   // unlocked / awake: the taskbar asks for icons again, and may have missed ours
        RedrawAfterWake();         // and parts of the window may not have been repainted
        if (!_paused) return;
        _paused = false;
        Log.Write($"Background sign-in resumed: {why}");
        if (_lastAuthCheck != DateTime.MinValue)
            _lastAuthCheck = DateTime.Now - AuthCheckEvery + TimeSpan.FromSeconds(30);
        UpdateAuthDisplay();
    }

    /// <summary>Renew when the keys are about to run out, or once we've found we're signed out.</summary>
    private bool ShouldRenew()
    {
        if (!_settings.KeepSignedIn || _renewing || _busy || _signedOutByUser || _paused) return false;
        if (_renewFailedAt is { } failed && DateTime.Now - failed < RenewRetryAfter) return false;
        if (Aws.IsSsoProfile(Profile)) return false; // SSO keys refresh themselves while the SSO session lasts

        bool expiringSoon = _authArn != null && _authExpiry is { } exp && exp - DateTimeOffset.Now <= RenewAhead;
        bool signedOut = _authArn == null && _authError == null;
        return expiringSoon || signedOut;
    }

    /// <summary>
    /// Renew the AWS keys in the background with aws-azure-login's saved Microsoft session: no window, no typing.
    /// If Microsoft wants a fresh sign-in (MFA, password change, policy), it fails quietly and the user clicks Sign in.
    /// </summary>
    /// <summary>
    /// Make sure we're signed in before doing something that needs AWS. Signed in: returns straight away.
    /// Signed out: tries the silent renewal, then the normal sign-in (no "sign in now?" question; the user's
    /// double-click already said what they want). True once signed in; false if they cancelled or it failed.
    /// </summary>
    private async Task<bool> EnsureSignedInAsync(string forWhat, bool knownExpired = false)
    {
        bool SignedIn() => _authArn != null &&
                           (_authExpiry is not { } exp || exp > DateTimeOffset.Now.AddSeconds(30));

        if (!knownExpired)
        {
            if (SignedIn()) return true;
            if (_lastAuthCheck == DateTime.MinValue) return true;     // not checked yet: just try
            if (_authArn == null && _authError != null) return true;  // couldn't reach AWS: signing in won't help
        }

        // a sign-in or renewal is already running (e.g. a second double-click): wait for it instead of starting another
        if (_renewing || _busy)
        {
            SetStatus($"Waiting for the AWS sign-in to finish before {forWhat} ...");
            while ((_renewing || _busy) && !IsDisposed) await Task.Delay(500);
            if (IsDisposed) return false;
            if (SignedIn()) return true;
        }

        SetStatus($"Signing in to AWS before {forWhat} ...");
        if (_settings.KeepSignedIn && !Aws.IsSsoProfile(Profile))
        {
            _renewFailedAt = null; // the user asked for this, so try now even if a background attempt failed recently
            await RenewAsync();
            if (_authArn != null) return true;
        }

        await SignInAsync();
        return _authArn != null;
    }

    private async Task RenewAsync()
    {
        _renewing = true;
        string profile = Profile;
        ShowAuth("Status: Renewing sign-in ...", Tone.Warn, "Renewing your AWS sign-in in the background.");
        Log.Write($"Renewing sign-in for {profile} in the background");

        (int Code, string Out, string Err) r;
        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
        {
            _renewCts = cts;
            try { r = await RunAzureLoginAsync(cts.Token, profile, "--no-prompt"); }
            finally { _renewCts = null; }
        }
        Log.Write($"Background renewal finished with code {r.Code}\n{r.Out}\n{r.Err}");

        if (_paused)
        {
            // Windows was locked (or went to sleep) while renewing: it was stopped on purpose, not a failure.
            _renewing = false;
            UpdateAuthDisplay();
            SetStatus("Stopped renewing the AWS sign-in because Windows was locked. It'll check again when you're back.");
            return;
        }

        var (arn, err) = await IdentityAsync();
        _renewing = false;
        SetAuth(arn, err);

        if (r.Code == 0 && arn != null)
        {
            _renewFailedAt = null;
            SetStatus($"AWS sign-in renewed automatically at {DateTime.Now:t}");
        }
        else
        {
            _renewFailedAt = DateTime.Now;
            SetStatus("Couldn't renew your AWS sign-in automatically (Microsoft probably wants you to sign in again). " +
                      "Click Sign in when you need to connect to another server.");
        }
    }

    /// <summary>Record the result of a sign-in check. arn null + an error that isn't about sign-in = couldn't reach AWS.</summary>
    private void SetAuth(string arn, string error = null)
    {
        _lastAuthCheck = DateTime.Now;
        _authArn = string.IsNullOrEmpty(arn) ? null : arn;
        if (_authArn != null) _signedOutByUser = false; // signed in again (Sign in, or connecting)
        _authError = _authArn == null && !string.IsNullOrEmpty(error) && !LoginPattern.IsMatch(error) ? error : null;
        _authExpiry = _authArn != null ? Aws.CredentialExpiry(Profile) : null;
        UpdateAuthDisplay();
    }

    /// <summary>Sign out is available while signed in (or when AWS couldn't be reached to check), and not mid-sign-in.</summary>
    private void UpdateSignOutButton() =>
        _btnSignOut.Enabled = (_authArn != null || _authError != null) && !_busy && !_renewing;

    private void UpdateAuthDisplay()
    {
        UpdateSignOutButton();
        string checkedAt = $"Last checked {_lastAuthCheck:t}. Click for profile settings." +
            (_settings.KeepSignedIn ? "" : "\n\"Keep me signed in\" is off: AWS is only checked when you connect or sign in.");

        if (_authArn == null)
        {
            if (_authError != null)
                ShowAuth("Status: Can't reach AWS", Tone.Warn, $"The sign-in check failed: {_authError}\n{checkedAt}");
            else
                ShowAuth("Status: Signed out", Tone.Bad, $"Not signed in to \"{Profile}\". Click Sign in.\n{checkedAt}");
            return;
        }

        // arn:aws:sts::123456789012:assumed-role/RoleName/person@company.com -> "person@company.com (RoleName)"
        var parts = _authArn.Split('/');
        string who = parts.Length >= 3 ? $"{parts[^1]} ({parts[^2]})" : parts[^1];
        string tip = $"Signed in as {who}\n{_authArn}";

        if (_authExpiry is not { } expiry)
        {
            ShowAuth("Status: Signed in", Tone.Good, $"{tip}\n{checkedAt}");
            return;
        }

        var left = expiry - DateTimeOffset.Now;
        tip += $"\nExpires at {expiry.LocalDateTime:t}\n{checkedAt}";
        if (left <= TimeSpan.Zero)
            ShowAuth("Status: Expired", Tone.Bad, tip);
        else if (left < TimeSpan.FromMinutes(10))
            ShowAuth($"Status: Signed in, {Math.Max(1, (int)left.TotalMinutes)} min left", Tone.Warn, tip);
        else
            ShowAuth($"Status: Signed in, {FormatLeft(left)} left", Tone.Good, tip);
    }

    private static string FormatLeft(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{(int)t.TotalMinutes} min";

    private void ShowAuth(string text, Tone? tone, string tooltip = null)
    {
        _btnAuth.Text = (tone switch { Tone.Good => "●  ", Tone.Warn => "▲  ", Tone.Bad => "○  ", _ => "" }) + text;
        _btnAuth.Tag = tone; // ThemedRenderer colours the text by tone
        if (tooltip != null) _btnAuth.ToolTipText = tooltip;
        _btnAuth.Invalidate();
    }

    // ---------------------------------------------------------------- start / stop / reboot

    /// <summary>Enable Connect / Start / Stop / Reboot (toolbar, Server menu, right-click menu) for the selected server.</summary>
    private void UpdateActionStates()
    {
        var i = Selected();
        bool busy = i != null && _actions.ContainsKey(i.Id);
        bool canConnect = i != null && (i.IsSaved || i.Online || (i.IsEc2 && !busy && i.State == "stopped"));
        bool canStart = i != null && i.IsEc2 && !busy && i.State == "stopped";
        bool canStopOrReboot = i != null && i.IsEc2 && !busy && i.State == "running";

        foreach (var x in new ToolStripItem[] { _miConnect, _btnConnect, _smConnect }) x.Enabled = canConnect;
        // AWS servers can use either; a saved connection has its own protocol
        foreach (var x in new ToolStripItem[] { _miConnectSsh, _smConnectSsh, _miConnectRdp, _smConnectRdp })
            x.Enabled = canConnect && i is { IsSaved: false } && i.Online;
        foreach (var x in new ToolStripItem[] { _miStart, _btnStart, _smStart }) x.Enabled = canStart;
        foreach (var x in new ToolStripItem[] { _miStop, _btnStop, _smStop, _miReboot, _btnReboot, _smReboot }) x.Enabled = canStopOrReboot;
        _smCopyId.Enabled = i is { IsSaved: false };
        _smCopyIp.Enabled = i != null;
        foreach (var x in new ToolStripItem[] { _smEdit, _smRemove, _miEdit, _miRemove }) x.Enabled = i is { IsSaved: true };

        _btnConnect.ToolTipText = i is { Online: false, State: "stopped" }
            ? $"{i.Name} is stopped: start it and connect when it's ready"
            : "Connect to the selected server (or double-click it)";
    }

    /// <summary>Coloured icons for the server actions, redrawn for the current theme and display scaling.</summary>
    private void RenderActionIcons()
    {
        var p = Theme.Current;
        int px = (int)Math.Round(16 * (DeviceDpi > 0 ? DeviceDpi : 96) / 96f);
        foreach (var (items, glyph, color) in new (ToolStripItem[] Items, string Glyph, Color Color)[]
                 {
                     (new ToolStripItem[] { _btnConnect, _smConnect, _miConnect }, Icons.Connect, p.AccentFore),
                     (new ToolStripItem[] { _btnStart, _smStart, _miStart }, Icons.Start, p.Good),
                     (new ToolStripItem[] { _btnStop, _smStop, _miStop }, Icons.Stop, p.Bad),
                     (new ToolStripItem[] { _btnReboot, _smReboot, _miReboot }, Icons.Reboot, p.Warn),
                     (new ToolStripItem[] { _btnSignIn }, Icons.SignIn, p.AccentFore),
                     (new ToolStripItem[] { _btnSignOut }, Icons.SignOut, p.Muted),
                 })
        {
            foreach (var item in items)
            {
                var old = item.Image;
                item.Image = Icons.Render(glyph, color, px);
                old?.Dispose();
            }
        }
    }

    // ---------------------------------------------------------------- server list columns

    /// <summary>The saved column list, cleaned up: known keys only, no repeats, Name first.</summary>
    private string[] ViewColumnKeys => _savedView ? SavedColumnKeys : AwsColumnKeys;

    /// <summary>Saved settings keep separate widths per tab ("saved.Name" vs "Name").</summary>
    private string WidthKey(string key) => _savedView ? "saved." + key : key;

    private string ColumnTitle(ListColumn def) => _savedView && def.Key == "Ip" ? "Address" : def.Title;

    private static List<string> ValidColumns(IEnumerable<string> saved, IEnumerable<string> allowed)
    {
        var known = allowed.ToList();
        var list = (saved ?? Enumerable.Empty<string>()).Where(known.Contains).Distinct().ToList();
        if (list.Count == 0) list = known;                 // first run, or nothing usable saved
        list.Remove("Name");
        list.Insert(0, "Name");
        return list;
    }

    private string ColumnKey(int index) =>
        index >= 0 && index < _list.Columns.Count ? (string)_list.Columns[index].Tag : "";

    private string CellText(Instance i, string key) => key switch
    {
        "Type" => i.Type,
        "Os" => i.Os,
        "Region" => i.Region,
        "Ip" => i.Ip,
        "Id" => i.IsSaved ? "" : i.Id,
        "Status" => StatusOf(i),
        "Group" => i.Saved?.Group ?? "",
        // one line in the list (the full text, line breaks included, is in the tooltip)
        "Description" => (i.Saved?.Description ?? "").Replace("\r\n", " ").Replace('\n', ' '),
        "User" => i.Saved is { User.Length: > 0 } c ? c.User
                : i.Saved is { IsSsh: true } ? "your Windows user name"
                : (_settings.Username ?? "").Length > 0 ? _settings.Username + " (default)" : "asked when connecting",
        _ => i.Name,
    };

    /// <summary>Recreate the columns in _columns order (each column's index then matches its position) and refill.</summary>
    private void RebuildColumns()
    {
        _rebuildingColumns = true;
        _list.BeginUpdate();
        _list.Items.Clear();
        _list.Columns.Clear();
        foreach (var key in _columns)
        {
            var def = AllColumns.First(c => c.Key == key);
            int width = _settings.ColumnWidths.TryGetValue(WidthKey(key), out var w) && w >= 40 ? w : def.Width;
            _list.Columns.Add(new ColumnHeader { Text = ColumnTitle(def), Width = width, Tag = key });
        }
        _list.EndUpdate();
        _rebuildingColumns = false;
        RebuildList();
    }

    private void CaptureColumnWidths()
    {
        for (int i = 0; i < _list.Columns.Count - 1; i++) // the last column stretches to fit, so its width isn't a choice
            _settings.ColumnWidths[WidthKey((string)_list.Columns[i].Tag)] = _list.Columns[i].Width;
    }

    private void SaveColumnLayout()
    {
        CaptureColumnWidths();
        if (_savedView) _settings.SavedColumns = _columns.ToList();
        else _settings.Columns = _columns.ToList();
        _settings.SavedView = _savedView;
        _settings.Save();
    }

    private int HeaderHeight() =>
        _list.TopItem != null ? _list.TopItem.Bounds.Top : _list.Font.Height + 12;

    /// <summary>Right-click on the headers: tick the columns to show; drag headers to reorder.</summary>
    private void ShowColumnMenu(Point screen)
    {
        _columnMenu.Items.Clear();
        foreach (var def in AllColumns.Where(c => ViewColumnKeys.Contains(c.Key)))
        {
            var item = new ToolStripMenuItem(ColumnTitle(def))
            {
                Checked = _columns.Contains(def.Key),
                Enabled = def.Key != "Name",               // Name is always shown
                Tag = def.Key,
            };
            item.Click += (_, _) => ToggleColumn((string)item.Tag);
            _columnMenu.Items.Add(item);
        }
        _columnMenu.Items.Add(new ToolStripSeparator());
        _columnMenu.Items.Add(new ToolStripMenuItem("Reset columns", null, (_, _) =>
        {
            foreach (var k in ViewColumnKeys) _settings.ColumnWidths.Remove(WidthKey(k));
            _columns = ValidColumns(null, ViewColumnKeys);
            RebuildColumns();
            SaveColumnLayout();
        }));
        _columnMenu.Items.Add(new ToolStripLabel("Drag a header to move it") { Enabled = false });
        Theme.ApplyMenu(_columnMenu);
        _columnMenu.Show(screen);
    }

    private void ToggleColumn(string key)
    {
        if (key == "Name") return;
        CaptureColumnWidths();
        if (_columns.Contains(key))
        {
            if (_columns.Count <= 2) return; // keep Name plus at least one other
            _columns.Remove(key);
        }
        else _columns.Add(key);              // newly shown columns go on the end; drag to move them
        RebuildColumns();
        SaveColumnLayout();
    }

    /// <summary>Switch the list between the AWS servers tab and the saved RDP connections tab.</summary>
    private void SetView(bool saved)
    {
        _tabAws.Checked = !saved;
        _tabSaved.Checked = saved;
        _miViewAws.Checked = !saved;
        _miViewSaved.Checked = saved;
        if (saved == _savedView) return;

        SaveColumnLayout();                  // the old tab's columns
        _savedView = saved;
        _chkWin.Visible = !saved;            // "Windows only" means nothing for saved connections
        _txtSearch.PlaceholderText = saved ? "Search name, address, user ..." : "Search name, IP, instance ID, tag ...";
        _columns = ValidColumns(saved ? _settings.SavedColumns : _settings.Columns, ViewColumnKeys);
        RebuildColumns();
        SaveColumnLayout();                  // remembers which tab was open
    }

    /// <summary>
    /// Select a profile in the toolbar dropdown, adding it if it isn't listed (e.g. one being set up right now).
    /// "Add profile ..." always stays the last entry.
    /// </summary>
    private void SelectProfile(string name)
    {
        name = (name ?? "").Trim();
        _cboProfile.Items.Remove(AddProfileItem);
        if (name.Length > 0 && !_cboProfile.Items.Contains(name)) _cboProfile.Items.Add(name);
        _cboProfile.Items.Add(AddProfileItem);
        _lastProfile = name;
        if (name.Length > 0) _cboProfile.SelectedItem = name;
    }

    /// <summary>AWS > Remove profile: remove the profile currently in use (not while its AWS tabs are open).</summary>
    private async Task RemoveCurrentProfileAsync()
    {
        string name = Profile;
        if (name.Length == 0) return;
        if (_sessions.Any(x => x.Tunnel != null))
        {
            MessageBox.Show(this, $"\"{name}\" is in use by open AWS tabs. Close them first, so their sessions can end cleanly.",
                "Remove profile", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!ProfileSetup.Remove(this, name)) return;
        ForgetProfile(name);
        await LoadProfilesAsync();
        SetStatus($"Removed the AWS profile {name}");
        if (Profile.Length > 0) await CheckAuthAsync();
    }

    /// <summary>A removed profile leaves the list; if it was the one in use, switch to another (or none).</summary>
    private void ForgetProfile(string name)
    {
        _cboProfile.Items.Remove(name);
        if (!string.Equals(_lastProfile, name, StringComparison.OrdinalIgnoreCase)) return;
        var next = _cboProfile.Items.Cast<object>().Select(o => o.ToString()).FirstOrDefault(o => o != AddProfileItem) ?? "";
        _aws = new List<Instance>(); // the old profile's servers
        ComposeList();
        RebuildList();
        SetAuth(null);
        SelectProfile(next);
        _settings.Profile = next;
        _settings.Save();
    }

    /// <summary>Add a new profile: the dialog, then its setup window; the new profile is selected.</summary>
    private void AddProfile()
    {
        string name = ProfileSetup.Add(this);
        if (name == null) return;
        SelectProfile(name);
        SetAuth(null);
        SetStatus($"Setting up {name} in the window that opened. When it's done, click Sign in.");
    }

    /// <summary>The "Keep me signed in to AWS" setting (startup asks before signing in by itself).</summary>
    public bool KeepSignedIn => _settings.KeepSignedIn;

    /// <summary>Started signed out with "Keep me signed in" off: say how to sign in, without opening anything.</summary>
    public void ShowSignedOutNotice() =>
        SetStatus("Not signed in to AWS. Click Sign in when you need the AWS list, or just connect to an AWS server " +
                  "and you'll be signed in then. Saved connections work without AWS.");

    /// <summary>Turn the background sign-in heartbeat and renewal on or off.</summary>
    /// <summary>The toolbar's "Keep me signed in" checkbox, hosted as a toolbar item.</summary>
    private ToolStripControlHost KeepHost()
    {
        if (_keepHost == null)
        {
            _chkKeep.Padding = new Padding(0, 0, LogicalToDeviceUnits(100), 0);   // 100 px wider than its text needs
            _keepHost = new ToolStripControlHost(_chkKeep) { Margin = new Padding(8, 3, 4, 2), AutoSize = true };
        }
        _tips.SetToolTip(_chkKeep,
            "On: check the AWS sign-in every few minutes and renew it in the background.\n" +
            "Off: nothing in the background; you're signed in when you connect or change a server.");
        return _keepHost;
    }

    private async Task SetKeepSignedInAsync(bool on)
    {
        _settings.KeepSignedIn = on;
        _settings.Save();
        _miKeepSignedIn.Checked = on;
        _syncingKeep = true;
        _chkKeep.Checked = on;
        _syncingKeep = false;
        SetStatus(on ? "Keeping you signed in to AWS: checked every few minutes and renewed in the background"
                     : "No longer keeping you signed in: AWS is only checked when you connect or sign in");
        if (on && !_busy && !_renewing) await CheckAuthAsync();
        else UpdateAuthDisplay();
    }

    // ---------------------------------------------------------------- saved RDP connections

    /// <summary>The list = AWS servers + saved connections (with their last reachability result).</summary>
    private void ComposeList()
    {
        _all = _aws.Concat(_saved.Select(c =>
            ConnectionStore.ToInstance(c, _reachable.TryGetValue(c.Id, out var r) ? r : null))).ToList();
    }

    /// <summary>Check every saved connection's RDP port at once and update its Status as answers come in.</summary>
    private async Task ProbeSavedAsync(IEnumerable<SavedConnection> which = null)
    {
        var list = (which ?? _saved).ToList();
        await Task.WhenAll(list.Select(async c =>
        {
            bool ok = await ConnectionStore.IsReachableAsync(c.Host, c.Port);
            if (IsDisposed || !_saved.Contains(c)) return;
            _reachable[c.Id] = ok;
            var fresh = ConnectionStore.ToInstance(c, ok);
            int idx = _all.FindIndex(x => x.Id == fresh.Id);
            if (idx >= 0) _all[idx] = fresh;
            UpdateRow(fresh);
        }));
    }

    /// <summary>
    /// Connect to any computer now (RDP or SSH), without adding it to Saved connections unless asked to.
    /// A password typed for a one-off connection stays in memory for that tab only.
    /// </summary>
    private async Task ConnectAdHocAsync()
    {
        using var f = new ConnectionForm(null, _settings.Username, adHoc: true);
        if (f.ShowDialog(this) != DialogResult.OK || f.Result == null) return;
        var c = f.Result;

        if (f.SaveRequested)
        {
            _saved.Add(c);
            ConnectionStore.Save(_saved);
            if (f.Password.Length > 0)
            {
                try { CredentialStore.Save(f.EffectiveUser, f.Password); }
                catch (Exception ex) { Log.Write("Could not save password: " + ex.Message); }
            }
            ComposeList();
            RebuildList();
            _ = ProbeSavedAsync(new[] { c });
        }
        else if (f.Password.Length > 0)
        {
            _adHocPasswords[c.Id] = f.Password;
        }

        await ConnectAsync(ConnectionStore.ToInstance(c, null));
    }

    private void AddOrEditConnection(SavedConnection editing)
    {
        using var f = new ConnectionForm(editing, _settings.Username);
        if (f.ShowDialog(this) != DialogResult.OK || f.Result == null) return;

        var c = f.Result;
        int idx = _saved.FindIndex(x => x.Id == c.Id);
        if (idx >= 0) _saved[idx] = c; else _saved.Add(c);
        ConnectionStore.Save(_saved);

        if (f.Password.Length > 0)
        {
            try { CredentialStore.Save(f.EffectiveUser, f.Password); }
            catch (Exception ex)
            {
                Log.Write("Could not save password: " + ex.Message);
                MessageBox.Show(this, "The connection was saved, but its password couldn't be: " + ex.Message,
                    "Saved connection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        _reachable.Remove(c.Id);
        ComposeList();
        if (!_savedView) SetView(true); else RebuildList();
        SelectById("saved:" + c.Id);
        SetStatus($"{(editing == null ? "Added" : "Updated")} {(c.Name.Length > 0 ? c.Name : c.Host)}");
        _ = ProbeSavedAsync(new[] { c });
    }

    private void RemoveConnection(SavedConnection c)
    {
        string name = c.Name.Length > 0 ? c.Name : c.Host;
        if (!Ask($"Remove the saved connection {name} ({c.Address})?\n\nAny saved password stays in Settings > RDP credentials.")) return;
        _saved.RemoveAll(x => x.Id == c.Id);
        _reachable.Remove(c.Id);
        ConnectionStore.Save(_saved);
        ComposeList();
        RebuildList();
        SetStatus($"Removed {name}");
    }

    // ---------------------------------------------------------------- import from other tools

    /// <summary>"Import connections" submenu (a new copy each time, since menu items can only live in one menu).</summary>
    private ToolStripMenuItem ImportMenu()
    {
        var menu = new ToolStripMenuItem("&Import connections");
        menu.DropDownItems.Add(new ToolStripMenuItem("From &RDCMan file (.rdg) ...", null, (_, _) =>
            ImportFromFile("Remote Desktop Connection Manager files (*.rdg)|*.rdg|All files (*.*)|*.*", false,
                files => ConnectionImport.FromRdcMan(files[0]))));
        menu.DropDownItems.Add(new ToolStripMenuItem("From Remote &Desktop files (.rdp) ...", null, (_, _) =>
            ImportFromFile("Remote Desktop files (*.rdp)|*.rdp|All files (*.*)|*.*", true,
                files => ConnectionImport.FromRdpFiles(files))));
        menu.DropDownItems.Add(new ToolStripMenuItem("From &mRemoteNG (confCons.xml) ...", null, (_, _) =>
            ImportFromFile("mRemoteNG connection files (*.xml)|*.xml|All files (*.*)|*.*", false,
                files => ConnectionImport.FromMRemoteNg(files[0]),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mRemoteNG"))));
        menu.DropDownItems.Add(new ToolStripMenuItem("From &PuTTY saved sessions", null, (_, _) =>
            ApplyImport(ConnectionImport.FromPutty(), "PuTTY")));
        return menu;
    }

    private void ImportFromFile(string filter, bool many, Func<string[], ConnectionImport.Result> read, string folder = null)
    {
        using var dlg = new OpenFileDialog { Filter = filter, Multiselect = many, Title = "Import connections" };
        if (folder != null && Directory.Exists(folder)) dlg.InitialDirectory = folder;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        ConnectionImport.Result result;
        try { result = read(dlg.FileNames); }
        catch (Exception ex)
        {
            Log.Write("Import failed: " + ex);
            MessageBox.Show(this, "Couldn't read that file:\n\n" + ex.Message, "Import connections",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        ApplyImport(result, many ? $"{dlg.FileNames.Length} file(s)" : Path.GetFileName(dlg.FileName));
    }

    /// <summary>Show what was found, skip what's already saved (same protocol, host and port), and add the rest.</summary>
    private void ApplyImport(ConnectionImport.Result result, string source)
    {
        static string Key(SavedConnection c) => $"{c.Protocol}|{c.Host.ToLowerInvariant()}|{c.Port}";
        var existing = new HashSet<string>(_saved.Select(Key));
        var fresh = new List<SavedConnection>();
        foreach (var c in result.Found)
            if (existing.Add(Key(c))) fresh.Add(c); // also drops repeats within the file

        string warnings = result.Warnings.Count > 0 ? "\n\n" + string.Join("\n", result.Warnings) : "";
        int dupes = result.Found.Count - fresh.Count;
        if (fresh.Count == 0)
        {
            MessageBox.Show(this,
                (result.Found.Count == 0 ? $"Nothing to import from {source}."
                                         : $"All {result.Found.Count} connection(s) in {source} are already saved.") + warnings,
                "Import connections", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        int ssh = fresh.Count(c => c.IsSsh);
        string summary = $"Found {result.Found.Count} connection(s) in {source}" +
                         (dupes > 0 ? $"; {dupes} already saved and will be skipped" : "") + ".\n\n" +
                         $"Import {fresh.Count} ({fresh.Count - ssh} Remote Desktop, {ssh} SSH)?\n\n" +
                         "Passwords aren't imported; add them in Settings > RDP credentials if you want them saved." + warnings;
        if (MessageBox.Show(this, summary, "Import connections", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _saved.AddRange(fresh);
        ConnectionStore.Save(_saved);
        Log.Write($"Imported {fresh.Count} connection(s) from {source}");

        ComposeList();
        if (!_savedView) SetView(true);
        // imports usually bring groups: make sure the Group column is showing
        if (fresh.Any(c => c.Group.Length > 0) && !_columns.Contains("Group"))
        {
            CaptureColumnWidths();
            _columns.Insert(1, "Group");
            RebuildColumns();
            SaveColumnLayout();
        }
        else RebuildList();
        SetStatus($"Imported {fresh.Count} connection(s) from {source}");
        _ = ProbeSavedAsync(fresh);
    }

    private void SelectById(string id)
    {
        foreach (ListViewItem item in _list.Items)
        {
            if (item.Tag is not Instance i || i.Id != id) continue;
            item.Selected = item.Focused = true;
            item.EnsureVisible();
            return;
        }
    }

    private string StatusOf(Instance i) => _actions.TryGetValue(i.Id, out var a) ? a : i.StatusText;

    private Color? StatusColor(Instance i)
    {
        var p = Theme.Current;
        if (_actions.ContainsKey(i.Id)) return p.Warn;
        if (i.Online) return p.Good;
        return i.State switch
        {
            "pending" or "stopping" or "shutting-down" => p.Warn,
            "running" when i.Managed => p.Bad, // should be reachable but the agent isn't answering
            _ => null,                          // stopped / not managed: the row is already greyed out
        };
    }

    private string Tooltip(Instance i) => i.IsSaved
        ? $"{i.Name}\nSaved {(i.Saved.IsSsh ? "SSH" : "RDP")} connection to {i.Saved.Address}\nStatus: {StatusOf(i)}\n" +
          $"Signs in as: {CellText(i, "User")}" +
          (string.IsNullOrWhiteSpace(i.Saved.Description) ? "" : "\n\n" + i.Saved.Description)
        : 
        $"{i.Name}\n{i.Type}  {i.OsFull}\nStatus: {StatusOf(i)}" +
        $"\nEC2 state: {(i.State.Length > 0 ? i.State : "n/a")}   Systems Manager: {(i.Managed ? i.Ping : "not managed")}" +
        (i.Tags.Length > 0 ? "\n" + i.Tags.Replace("; ", "\n") : "");

    private async Task InstanceActionAsync(Instance inst, InstanceActions.Action action,
        bool confirm = true, bool connectWhenReady = false)
    {
        if (!inst.IsEc2 || _actions.ContainsKey(inst.Id)) return;

        bool hasSession = _sessions.Any(x => x.Inst.Id == inst.Id);
        string sessionNote = hasSession ? "\n\nYour open remote desktop to it will disconnect." : "";
        if (confirm && action == InstanceActions.Action.Stop &&
            !Ask($"Stop {inst.Name}?\n\nIt keeps its disks and settings and can be started again later.{sessionNote}"))
            return;
        if (confirm && action == InstanceActions.Action.Reboot &&
            !Ask($"Reboot {inst.Name}?\n\nAnyone signed in to it will be disconnected.{sessionNote}"))
            return;

        string verb = action switch
        {
            InstanceActions.Action.Start => "Starting",
            InstanceActions.Action.Stop => "Stopping",
            _ => "Rebooting",
        };
        if (!await EnsureSignedInAsync($"{verb.ToLowerInvariant()} {inst.Name}"))
        {
            SetStatus($"{inst.Name} wasn't changed: you're not signed in to AWS");
            return;
        }
        _actions[inst.Id] = verb + " ...";
        UpdateRow(inst);
        Log.Write($"{verb} {inst.Name} ({inst.Id}, {inst.Region})");

        try
        {
            await InstanceActions.RunAsync(Profile, inst, action);
        }
        catch (Exception ex)
        {
            _actions.Remove(inst.Id);
            UpdateRow(inst);
            string msg = Aws.Describe(ex);
            Log.Write($"{verb} {inst.Name} failed: {msg}");
            if (LoginPattern.IsMatch(msg)) SetAuth(null);
            if (msg.Contains("UnauthorizedOperation", StringComparison.OrdinalIgnoreCase))
                msg = $"Your AWS role isn't allowed to do this (it needs ec2:{action}Instances).\n\n{msg}";
            MessageBox.Show(this, $"Couldn't {action.ToString().ToLowerInvariant()} {inst.Name}.\n\n{msg}",
                verb, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        SetStatus($"{verb} {inst.Name} ..." + (connectWhenReady ? " It'll open in a tab when it's ready." : ""));
        await WatchAsync(inst, action, connectWhenReady);
    }

    /// <summary>Re-check the instance every 5 seconds until the action has finished, updating its row.</summary>
    private async Task WatchAsync(Instance inst, InstanceActions.Action action, bool connectWhenReady)
    {
        var started = DateTime.Now;
        var giveUp = started.AddMinutes(action == InstanceActions.Action.Stop ? 6 : 10);
        Instance latest = inst;

        while (DateTime.Now < giveUp && !IsDisposed)
        {
            await Task.Delay(5000);
            if (IsDisposed) return;
            try
            {
                latest = await InstanceActions.RefreshAsync(Profile, inst) ?? latest;
            }
            catch (Exception ex)
            {
                Log.Write($"Checking {inst.Name}: {Aws.Describe(ex)}");
                continue;
            }
            ReplaceInstance(latest);

            bool done = action switch
            {
                InstanceActions.Action.Stop => latest.State == "stopped",
                // a server without the SSM agent never comes "Online"; running is as good as it gets
                InstanceActions.Action.Start => latest.State == "running" && (latest.Online || !latest.Managed),
                // the agent's status can lag a reboot, so give it a minute and a half before trusting "Online"
                _ => DateTime.Now - started > TimeSpan.FromSeconds(90) && latest.State == "running" && latest.Online,
            };
            if (!done) continue;

            _actions.Remove(inst.Id);
            UpdateRow(latest);
            string result = action switch
            {
                InstanceActions.Action.Stop => "stopped",
                InstanceActions.Action.Start => latest.Online ? "started and online" : "started (no SSM agent, so it can't be reached from here)",
                _ => "rebooted and back online",
            };
            SetStatus($"{latest.Name} {result}");
            if (connectWhenReady && latest.Online) await ConnectAsync(latest);
            return;
        }

        _actions.Remove(inst.Id);
        UpdateRow(latest);
        SetStatus($"{inst.Name} is taking longer than expected. Refresh the list later to see where it got to.");
    }

    /// <summary>Swap in fresh details for an instance, keeping the list in place (no scroll jump).</summary>
    private void ReplaceInstance(Instance fresh)
    {
        int idx = _all.FindIndex(x => x.Id == fresh.Id);
        if (idx >= 0) _all[idx] = fresh;
        int awsIdx = _aws.FindIndex(x => x.Id == fresh.Id);
        if (awsIdx >= 0) _aws[awsIdx] = fresh;
        UpdateRow(fresh);
    }

    private void UpdateRow(Instance inst)
    {
        foreach (ListViewItem item in _list.Items)
        {
            if (item.Tag is not Instance shown || shown.Id != inst.Id) continue;
            item.Tag = inst;
            for (int c = 0; c < _columns.Count && c < item.SubItems.Count; c++)
                item.SubItems[c].Text = CellText(inst, _columns[c]);
            item.ToolTipText = Tooltip(inst);
            _list.Invalidate(item.Bounds);
            UpdateActionStates();
            return;
        }
        if (Matches(inst)) RebuildList(); // wasn't shown but should be now (e.g. "Hide offline" and it came online)
    }

    /// <summary>Hide or show the server list. The remote desktop resizes to use the space.</summary>
    private void ToggleList()
    {
        _split.Panel1Collapsed = !_split.Panel1Collapsed;
        _btnList.Checked = !_split.Panel1Collapsed; // pressed = list shown
        _miList.Checked = !_split.Panel1Collapsed;
        if (!_split.Panel1Collapsed) _list.Focus();
    }

    private bool Ask(string text) =>
        MessageBox.Show(this, text, AppInfo.ShortName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    private static void CopyToClipboard(string text)
    {
        if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
    }

    private static void OpenLogs()
    {
        try
        {
            Directory.CreateDirectory(Log.Dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Log.Dir}\"") { UseShellExecute = true });
        }
        catch { }
    }
}
