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
    public ClaudeWebHost Web;    // the claude.ai tab (a page, not a terminal)
    public VncHost Vnc;          // VNC tabs (noVNC through a local relay)
    public bool VncTriedSaved;   // the saved VNC password was already offered this time
    public string VncProblem = "";
    public bool Docked;          // in the docked-terminal panel under the tabs, not a tab of its own
    public TabPage Page;
    public DateTime Started = DateTime.Now;
    public bool SawActivity;
    public bool WasConnected;
    public bool Flagged;
    // Remote Desktop: how it last disconnected, and reconnecting after a lost connection
    public int DisconnectReason = -1, ExtendedReason;
    public bool Reconnecting;
    public Panel Overlay;
    public TaskCompletionSource<bool> RetryNow;
    /// <summary>Connect as: this tab's own user (and typed password, kept in memory only); null = the usual credentials.</summary>
    public string AsUser;
    public string AsPassword = "";
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
    private readonly ToolStripMenuItem _tabMenuReconnect = new("&Reconnect");
    private readonly ToolStripMenuItem _tabMenuCad = new("Send Ctrl+Alt+&Del");
    private readonly ToolStripMenuItem _tabMenuPaste = new("Send clipboard text");
    private readonly ContextMenuStrip _placeholderMenu = new();   // restored tabs waiting for a click
    private TabPage _placeholderTarget;
    private readonly ToolStripMenuItem _tabMenuLeft = new("Move left");
    private readonly ToolStripMenuItem _tabMenuRight = new("Move right");
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
    private readonly ToolStripMenuItem _miConnectVnc = new("Connect with VNC");
    private readonly ToolStripMenuItem _miConnectAs = new("Connect &as ...");
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

    // updates: a notice under the toolbar when GitHub has a newer release
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 60_000 };   // first check a minute after start
    private readonly Panel _updateBanner = new() { Dock = DockStyle.Top, Height = 34, Visible = false };
    private readonly Label _updateText = new() { AutoSize = true, Margin = new Padding(12, 9, 14, 0) };
    private readonly LinkLabel _updateNotes = new() { Text = "What's new", AutoSize = true, Margin = new Padding(0, 9, 14, 0) };
    private readonly LinkLabel _updateNow = new() { Text = "Update now", AutoSize = true, Margin = new Padding(0, 9, 14, 0) };
    private readonly LinkLabel _updateLater = new() { Text = "Later", AutoSize = true, Margin = new Padding(0, 9, 14, 0) };
    private readonly LinkLabel _updateSkip = new() { Text = "Skip this version", AutoSize = true, Margin = new Padding(0, 9, 14, 0) };
    private UpdateInfo _update;
    private bool _updating;   // closing to install an update: no questions, and stay signed in for the restart

    // system tray (Settings > Appearance > Window)
    private readonly NotifyIcon _tray = new() { Text = AppInfo.Name };
    private bool _exitRequested;   // Exit from the tray or File menu: really close, even with "close to tray" on
    private FormWindowState _restoreState = FormWindowState.Normal;
    private readonly ToolStripMenuItem _miClone = new("New server &like this one ...");
    private readonly ToolStripMenuItem _miManage = new("&Manage server ...");
    private readonly ToolStripMenuItem _miForward = new("Port &forwarding ...");
    private readonly ToolStripMenuItem _miTerminate = new("&Terminate ...");
    private readonly List<PortForward> _forwards = new();
    private ForwardsForm _forwardsForm;

    // restoring tabs (Settings > Terminal & SSH)
    private const int ReconnectAtStart = 5;   // remote tabs reconnected at start-up; any more open as "Reconnect" placeholders
    private readonly Dictionary<TabPage, TabState> _placeholders = new();
    private readonly List<TabState> _pendingRestore = new();   // AWS tabs waiting for sign-in and the server list
    private DateTime _restoredFrom = DateTime.Now;
    private bool _closeConfirmed, _tabsSaved;

    // the layout (window, columns, list width, dock height) is saved a moment after it changes, not only on exit:
    // a Windows shutdown or anything else that ends Switchboard abruptly would otherwise lose it
    private readonly System.Windows.Forms.Timer _layoutSave = new() { Interval = 1000 };

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
    private HoverTip _hoverTip;
    private bool _syncingKeep; // set while the code (not the user) changes the checkbox
    private List<string> _columns = new();               // keys shown, in display order
    private readonly ContextMenuStrip _columnMenu = new();
    private bool _rebuildingColumns;

    public MainForm()
    {
        Text = AppInfo.Name;
        ClientSize = new Size(1400, 820);
        StartPosition = FormStartPosition.CenterScreen;
        ApplySavedWindowBounds();
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
        // server list font and size (Settings > Appearance): its own font, or the app font. Set after the theme, which
        // would otherwise give the list the app font again.
        float listSize = _settings.ListFontSize is >= 8f and <= 18f ? _settings.ListFontSize : 10;
        string listFamily = string.IsNullOrWhiteSpace(_settings.ListFont) ? Theme.UiFont : _settings.ListFont.Trim();
        if (_list.Font.Size != listSize || !_list.Font.FontFamily.Name.Equals(listFamily, StringComparison.OrdinalIgnoreCase))
            _list.Font = Theme.CreateFont(listFamily, listSize);
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
        file.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (_, _) => ExitApp()));

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
        aws.DropDownItems.Add(new ToolStripMenuItem("&New server ...", null, async (_, _) => await OpenNewServerAsync(null)));
        aws.DropDownItems.Add(new ToolStripSeparator());
        aws.DropDownItems.Add(new ToolStripMenuItem("&Sign in...", null, async (_, _) => await SignInAsync()));
        aws.DropDownItems.Add(new ToolStripMenuItem("Sign &out", null, async (_, _) => await SignOutNowAsync()));
        aws.DropDownItems.Add(new ToolStripMenuItem("&Edit profile ...", null, (_, _) => ProfileSetup.Edit(this, Profile)));
        aws.DropDownItems.Add(new ToolStripMenuItem("&Add profile ...", null, (_, _) => AddProfile()));
        aws.DropDownItems.Add(new ToolStripMenuItem("&Remove profile ...", null, async (_, _) => await RemoveCurrentProfileAsync()));
        aws.DropDownItems.Add(_miKeepSignedIn);
        aws.DropDownOpening += (_, _) => _miKeepSignedIn.Checked = _settings.KeepSignedIn;
        aws.DropDownItems.Add(new ToolStripSeparator());
        aws.DropDownItems.Add(new ToolStripMenuItem("&Profile settings...", null, async (_, _) => await OpenSettingsAsync(SettingsForm.Page.Profile)));
        aws.DropDownItems.Add(new ToolStripMenuItem("&RDP credentials...", null, async (_, _) => await OpenSettingsAsync(SettingsForm.Page.Credentials)));

        var smManage = new ToolStripMenuItem("&Manage server ...", null,
            async (_, _) => { if (Selected() is { IsEc2: true, IsSaved: false } target) await ManageServerAsync(target); });
        var smForwards = new ToolStripMenuItem("Port forwar&ds ...", null, (_, _) => ShowForwards());
        var server = new ToolStripMenuItem("&Server");
        server.DropDownItems.AddRange(new ToolStripItem[]
        {
            _smConnect, _smConnectSsh, _smConnectRdp,
            new ToolStripSeparator(),
            _smStart, _smStop, _smReboot,
            new ToolStripSeparator(),
            _smCopyId, _smCopyIp,
            new ToolStripSeparator(),
            smManage, smForwards,
            new ToolStripSeparator(),
            _smAdHoc, _smAdd, _smEdit, _smRemove,
            ImportMenu(),
        });
        server.DropDownOpening += (_, _) =>
        {
            UpdateActionStates();
            smManage.Enabled = Selected() is { IsEc2: true, IsSaved: false };
        };

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(new ToolStripMenuItem("Check for &updates ...", null, async (_, _) => await CheckForUpdatesAsync(manual: true)));
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
        _list.MultiSelect = true;   // Start / Stop / Reboot act on every selected AWS server; everything else on the first
        _list.HideSelection = false;
        _list.GridLines = false; // row lines are drawn by ThemedListView
        // the server tooltips: a formatted card, mouse-transparent so moving down the list keeps working
        _hoverTip = new HoverTip(_list, item => item.Tag is Instance hovered ? TipFor(hovered) : null);
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
        EventHandler start = async (_, _) => await RunActionAsync(InstanceActions.Action.Start);
        EventHandler stop = async (_, _) => await RunActionAsync(InstanceActions.Action.Stop);
        EventHandler reboot = async (_, _) => await RunActionAsync(InstanceActions.Action.Reboot);
        foreach (var item in new ToolStripItem[] { _miConnect, _btnConnect, _smConnect }) item.Click += connect;
        foreach (var item in new ToolStripItem[] { _miConnectSsh, _smConnectSsh })
            item.Click += async (_, _) => { var i = Selected(); if (i != null) await ConnectAsync(i, Protocol.Ssh); };
        foreach (var item in new ToolStripItem[] { _miConnectRdp, _smConnectRdp })
            item.Click += async (_, _) => { var i = Selected(); if (i != null) await ConnectAsync(i, Protocol.Rdp); };
        _miConnectVnc.Click += async (_, _) => { var i = Selected(); if (i != null) await ConnectAsync(i, Protocol.Vnc); };
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
            _miConnect, _miConnectSsh, _miConnectRdp, _miConnectVnc, _miConnectAs,
            new ToolStripSeparator(),
            _miStart, _miStop, _miReboot,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Copy instance ID", null, (_, _) => CopyToClipboard(Selected()?.Id)),
            new ToolStripMenuItem("Copy IP address", null, (_, _) => CopyToClipboard(Selected()?.Ip)),
            new ToolStripSeparator(),
            _miManage, _miForward, _miClone,
            new ToolStripSeparator(),
            _miAdd, _miEdit, _miRemove,
            new ToolStripSeparator(),
            _miTerminate,
        });
        _miClone.Click += async (_, _) => { if (Selected() is { IsEc2: true, IsSaved: false } src) await OpenNewServerAsync(src); };
        _miConnectAs.Click += async (_, _) => { if (Selected() is { } target) await ConnectAsAsync(target); };
        _miManage.Click += async (_, _) => { if (Selected() is { IsEc2: true, IsSaved: false } target) await ManageServerAsync(target); };
        _miForward.Click += async (_, _) => { if (Selected() is { IsSaved: false } target) await StartPortForwardAsync(target); };
        _miTerminate.Click += async (_, _) => { if (Selected() is { IsEc2: true, IsSaved: false } target) await TerminateServerAsync(target); };
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
            _miClone.Visible = _miManage.Visible = _miTerminate.Visible = Selected() is { IsEc2: true, IsSaved: false };   // EC2 servers only
            _miForward.Visible = Selected() is { IsSaved: false, Managed: true };   // through Session Manager
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

        _tabMenu.Items.Add(_tabMenuReconnect);
        _tabMenu.Items.Add(_tabMenuCad);
        _tabMenu.Items.Add(_tabMenuPaste);
        _tabMenu.Items.Add(new ToolStripSeparator());
        _tabMenu.Items.Add(_tabMenuLeft);
        _tabMenu.Items.Add(_tabMenuRight);
        _tabMenu.Items.Add(new ToolStripSeparator());
        _tabMenu.Items.Add(_tabMenuDock);
        _tabMenu.Items.Add("Close tab", null, (_, _) => { if (_menuTarget != null) CloseSession(_menuTarget); });
        _tabMenu.Opening += (_, e) =>
        {
            // only terminals can dock (a remote desktop would lose its connection if moved)
            _tabMenuDock.Visible = _menuTarget?.Term != null;
            _tabMenuDock.Text = _menuTarget is { Docked: true } ? "Move back to a tab" : "Dock to bottom";
            _tabMenuReconnect.Visible = _menuTarget is { Kind: Protocol.Rdp or Protocol.Ssh or Protocol.Vnc };
            _tabMenuCad.Visible = _menuTarget?.Rdp != null || _menuTarget?.Vnc != null;
            _tabMenuPaste.Visible = _menuTarget?.Vnc != null;
            int rdpState = 0;
            try { rdpState = _menuTarget?.Rdp?.ConnectionState() ?? 0; } catch { }
            _tabMenuCad.Enabled = rdpState == 1 || _menuTarget is { Vnc: not null, WasConnected: true };   // only into a connected session
            _tabMenuPaste.Enabled = _menuTarget is { Vnc: not null, WasConnected: true };
            var (strip, index) = TabPosition(_menuTarget);
            _tabMenuLeft.Enabled = index > 0;
            _tabMenuRight.Enabled = strip != null && index >= 0 && index < strip.TabCount - 1;
        };
        _tabMenuReconnect.Click += async (_, _) => await ReconnectSessionAsync(_menuTarget);
        _tabMenuPaste.Click += (_, _) =>
        {
            // VNC has no shared clipboard with this PC: this puts the text on the remote clipboard, to paste there
            if (_menuTarget?.Vnc == null) return;
            string text = "";
            try { text = Clipboard.GetText(); } catch { }
            if (text.Length == 0) { SetStatus("There's no text on the clipboard to send."); return; }
            _menuTarget.Vnc.SendClipboard(text);
            SetStatus($"Sent {text.Length:N0} characters to {_menuTarget.Inst.Name}'s clipboard: paste them there.");
        };
        _tabMenuCad.Click += (_, _) =>
        {
            var target = _menuTarget;
            if (target?.Vnc != null)
            {
                target.Vnc.SendCtrlAltDel();
                SetStatus($"Sent Ctrl+Alt+Del to {target.Inst.Name}.");
                return;
            }
            if (target?.Rdp == null) return;
            _tabs.SelectedTab = target.Page;   // the session needs to be the one on screen to take the keys
            try
            {
                target.Rdp.SendCtrlAltDel();
                SetStatus($"Sent Ctrl+Alt+Del to {target.Inst.Name} (Ctrl+Alt+End does the same from the keyboard).");
            }
            catch (Exception ex)
            {
                Log.Write("Ctrl+Alt+Del not sent: " + ex.Message);
                SetStatus($"Ctrl+Alt+Del couldn't be sent: {ex.Message}. Click into the session and press Ctrl+Alt+End instead.");
            }
        };
        _placeholderMenu.Items.Add("&Reconnect", null, async (_, _) => { if (_placeholderTarget != null) await ActivatePlaceholderAsync(_placeholderTarget); });
        _placeholderMenu.Items.Add("Close tab", null, (_, _) =>
        {
            if (_placeholderTarget == null || !_placeholders.Remove(_placeholderTarget)) return;
            _tabs.TabPages.Remove(_placeholderTarget);
            _placeholderTarget.Dispose();
            UpdateMainTabsVisibility();
        });
        _placeholderMenu.Opening += (_, _) => Theme.ApplyMenu(_placeholderMenu);
        _tabMenuLeft.Click += (_, _) => MoveTabBy(_menuTarget, -1);
        _tabMenuRight.Click += (_, _) => MoveTabBy(_menuTarget, +1);
        _tabs.TabsReordered += () => Log.Write("Tabs reordered");
        _tabMenuDock.Click += (_, _) =>
        {
            if (_menuTarget == null) return;
            if (_menuTarget.Docked) UndockSession(_menuTarget); else DockSession(_menuTarget);
        };

        _split.Dock = DockStyle.Fill;
        BuildUpdateBanner();
        BuildTray();
        SingleInstance.ActivateRequested += OnActivateRequested;
        Controls.Add(_updateBanner); // added before the toolbar, so it docks below it
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
            if (_rebuildingColumns || e.ColumnIndex >= _columns.Count - 1) return;   // the last one stretches to fit
            CaptureColumnWidths();
            LayoutChanged();
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
            Theme.ShowMenuOnScreen(_shellMenu, screen);
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
            LayoutChanged();
        };
        _split.SplitterMoved += (_, _) =>
        {
            if (!_split.Panel1Collapsed && Visible) _settings.ListWidth = _split.SplitterDistance;
            LayoutChanged();
        };
        ResizeEnd += (_, _) => LayoutChanged();       // after dragging the window's edge or moving it
        SizeChanged += (_, _) => { if (Visible && WindowState != FormWindowState.Minimized) LayoutChanged(); };   // maximize / restore
        _layoutSave.Tick += (_, _) => SaveLayoutNow();
        // the network came back (e.g. cable to Wi-Fi): tabs waiting to reconnect try again straight away
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, e) =>
        {
            if (!e.IsAvailable || IsDisposed) return;
            try { BeginInvoke(() => { foreach (var x in _sessions.Where(x => x.Reconnecting)) x.RetryNow?.TrySetResult(true); }); } catch { }
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
            FillShellMenu(_shellMenu.Items, _shellMenu, withConnect: true);
            Theme.ShowMenuOnScreen(_shellMenu, screen);
        };
        _smAdHoc.Click += async (_, _) => await ConnectAdHocAsync();
        _tabs.CloseRequested += page =>
        {
            if (_placeholders.Remove(page))
            {
                _tabs.TabPages.Remove(page);
                page.Dispose();
                UpdateMainTabsVisibility();
                return;
            }
            var s = _sessions.FirstOrDefault(x => x.Page == page);
            if (s != null) CloseSession(s);
        };
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            var sel = _sessions.FirstOrDefault(x => x.Page == _tabs.SelectedTab);
            sel?.Rdp?.SyncSize();
            sel?.Term?.FocusTerminal();
            sel?.Web?.FocusPage();
            sel?.Vnc?.FocusScreen();
            if (sel?.Rdp != null) { try { sel.Rdp.Focus(); } catch { } }
            _tabKeys?.Install();
        };
        _timer.Tick += (_, _) => PollSessions();
        _timer.Start();
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = 6 * 60 * 60 * 1000;   // after the first, look every few hours whether a day has passed
            await CheckForUpdatesAsync(manual: false);
        };
        _updateTimer.Start();
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
        bool shuttingDown = e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing;
        if (shuttingDown && !_closeConfirmed)
        {
            // Windows is signing out or shutting down: asking "close them and exit?" would only hold things up until
            // Windows ends the app, losing the layout and tabs. Save them and close.
            _closeConfirmed = true;
            Log.Write($"Closing for {e.CloseReason}: saving the layout and tabs without asking");
        }
        SaveLayoutNow();
        // the close button: to the tray, or (until the user has chosen) ask. Never for Exit, an update, sign-out or shutdown.
        if (!_exitRequested && !_updating && !_closeConfirmed && e.CloseReason == CloseReason.UserClosing)
        {
            bool toTray = _settings.CloseToTray;
            if (!toTray && !_settings.CloseChoiceMade)
            {
                var choice = AskCloseChoice();
                if (choice == CloseChoice.Cancel) { e.Cancel = true; return; }
                toTray = choice == CloseChoice.Tray;
            }
            if (toTray)
            {
                e.Cancel = true;
                HideToTray(closing: true);
                return;
            }
        }
        if (!_closeConfirmed)
        {
            int remote = _sessions.Count(x => x.Kind != Protocol.Local), shells = _sessions.Count(x => x.Kind == Protocol.Local && x.Web == null);
            string open = remote > 0 && shells > 0 ? $"{remote} remote session(s) and {shells} shell(s) are open"
                        : remote > 0 ? $"{remote} remote session(s) are open"
                        : $"{shells} shell(s) are open";
            if (remote + shells > 0 && !_updating && !Ask($"{open}. Close them and exit?" +
                    (_settings.RestoreTabs ? " (They reopen next time.)" : "")))
            {
                e.Cancel = true;
                return;
            }
            _closeConfirmed = true;
        }

        // remember the tabs for next time, before the sessions close; reading terminal output takes a moment, so then
        // the close pauses, saves, and starts again (without asking anything twice)
        if (!_tabsSaved)
        {
            _tabsSaved = true;
            if (!_settings.RestoreTabs) SessionRestore.Forget();
            else
            {
                var (tabs, terminals) = CaptureTabs();
                if (_settings.RestoreScrollback && terminals.Count > 0 && !shuttingDown)
                {
                    e.Cancel = true;
                    _exitRequested = true;
                    var output = new Dictionary<int, string>();
                    var deadline = DateTime.UtcNow.AddSeconds(3);   // in all: never hold up closing (or a Windows shutdown) for long
                    foreach (var (index, term) in terminals)
                    {
                        var left = deadline - DateTime.UtcNow;
                        if (left <= TimeSpan.Zero) break;
                        var read = term.ScrollbackAsync();
                        if (await Task.WhenAny(read, Task.Delay(left)) == read) output[index] = read.Result;
                    }
                    SessionRestore.Save(tabs, output);
                    Close();
                    return;
                }
                SessionRestore.Save(tabs, null);
            }
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
        bool signOut = _settings.SignOutOnClose && !_updating;   // an update restarts Switchboard: stay signed in
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

    /// <summary>All selected rows (several can be selected for Start / Stop / Reboot).</summary>
    private List<Instance> SelectedAll() => _list.SelectedItems.Cast<ListViewItem>().Select(x => (Instance)x.Tag).ToList();

    /// <summary>Start / Stop / Reboot: one server as before, or every selected AWS server after one confirmation that lists them.</summary>
    private async Task RunActionAsync(InstanceActions.Action action)
    {
        var targets = SelectedAll().Where(i => i.IsEc2).ToList();
        if (targets.Count <= 1)
        {
            if ((targets.FirstOrDefault() ?? Selected()) is { } one) await InstanceActionAsync(one, action);
            return;
        }
        string verb = action.ToString();
        string names = string.Join("\n", targets.Take(15).Select(t => "    " + t.Name)) + (targets.Count > 15 ? $"\n    ... and {targets.Count - 15} more" : "");
        string note = action == InstanceActions.Action.Start ? "" : "\n\nAnyone connected to them is disconnected.";
        if (!Ask($"{verb} these {targets.Count} servers?\n\n{names}{note}")) return;
        if (!await EnsureSignedInAsync($"{verb.ToLowerInvariant()} {targets.Count} servers")) { SetStatus("Not signed in to AWS."); return; }
        Log.Write($"{verb} {targets.Count} servers: {string.Join(", ", targets.Select(t => t.Id))}");
        await Task.WhenAll(targets.Select(t => InstanceActionAsync(t, action, confirm: false)));
    }

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
        if (_pendingRestore.Count > 0 && _aws.Count > 0) BeginInvoke(new Action(async () => await RestorePendingAsync()));
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
            if (Ask($"'{profile}' isn't set up for Microsoft sign-in yet. Set it up now?") && ProfileSetup.Edit(this, profile))
                await SignInAsync();   // set up: sign in straight away
            return;
        }

        SetBusy(true, Aws.IsSsoProfile(profile)
            ? "Signing in: finish the SSO sign-in in your browser ..."
            : "Signing in: finish the Microsoft sign-in in the sign-in window ...");
        _cts = new CancellationTokenSource();
        _btnCancel.Visible = true;
        bool cancelled = false;
        string arn = null;
        string failure = "";
        try
        {
            bool isSso = Aws.IsSsoProfile(profile);

            var r = isSso
                ? await Cli.RunAsync(Tools.AwsCli, new[] { "sso", "login", "--profile", profile }, _cts.Token)
                : await MicrosoftSignInAsync(_cts.Token, profile, interactive: true);
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

    private async Task ConnectAsync(Instance inst, Protocol? want = null, string asUser = null, string asPassword = "", string replay = null)
    {
        var protocol = want ?? DefaultProtocol(inst);
        string openingKey = $"{inst.Id}|{protocol}|{asUser}";
        // Connect as always opens its own tab (it's usually alongside a session as yourself)
        var existing = asUser != null ? null : _sessions.FirstOrDefault(s => s.Inst.Id == inst.Id && s.Kind == protocol && s.AsUser == null);
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
                await OpenSshAsync(inst, asUser, replay);
                return;
            }
            if (protocol == Protocol.Vnc)
            {
                await OpenVncAsync(inst);
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

            var page = new TabPage(Badged(inst, Protocol.Rdp, asUser == null ? inst.Name : $"{inst.Name} ({asUser})"))
                { BackColor = Theme.Current.Back, Tag = ThemedTabControl.BadgeTag(BadgeFor(inst, Protocol.Rdp)) };
            var host = new RdpHost(clsid) { Dock = DockStyle.Fill };
            host.Enter += (_, _) => _tabKeys?.Install();   // stay ahead of the RDP control's own keyboard hook
            host.FocusReleased += direction => SwitchTab(direction < 0 ? -1 : +1);   // Ctrl+Alt+Left / Right
            page.Controls.Add(host);
            _tabs.TabPages.Add(page);
            _tabs.Visible = true;
            _hint.Visible = false;
            _tabs.SelectedTab = page;

            var session = new Session { Inst = inst, Tunnel = tunnel, Rdp = host, Page = page, AsUser = asUser, AsPassword = asPassword ?? "" };
            host.Disconnected += (reason, extended) => { session.DisconnectReason = reason; session.ExtendedReason = extended; };
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

    /// <summary>
    /// Connect as: the server's usual protocol, with another user (and, for Remote Desktop, optionally a password),
    /// in a tab of its own.
    /// </summary>
    private async Task ConnectAsAsync(Instance inst)
    {
        var protocol = DefaultProtocol(inst);
        using var f = new ConnectAsForm(inst.Name, protocol == Protocol.Rdp, _settings.RecentConnectAs);
        if (f.ShowDialog(this) != DialogResult.OK) return;

        _settings.RecentConnectAs.RemoveAll(u => u.Equals(f.User, StringComparison.OrdinalIgnoreCase));
        _settings.RecentConnectAs.Insert(0, f.User);
        if (_settings.RecentConnectAs.Count > 10) _settings.RecentConnectAs.RemoveRange(10, _settings.RecentConnectAs.Count - 10);
        _settings.Save();
        if (f.SavePassword && f.Password.Length > 0)
        {
            try { CredentialStore.Save(f.User, f.Password); }
            catch (Exception ex) { Log.Write("Could not save password: " + ex.Message); SetStatus("The password couldn't be saved: " + ex.Message); }
        }
        await ConnectAsync(inst, protocol, f.User, f.SavePassword ? "" : f.Password);
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

    /// <summary>"Keep me signed in" on the toolbar: as wide as its text needs in its current font, plus room to spare.</summary>
    private void SizeKeepSignedIn()
    {
        if (_keepHost == null) return;
        var needed = _chkKeep.GetPreferredSize(Size.Empty);
        var size = new Size(needed.Width + LogicalToDeviceUnits(24), needed.Height);
        _chkKeep.AutoSize = false;
        _chkKeep.Size = size;
        _keepHost.Size = size;
    }

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
    private void SwitchTabIn(ThemedTabControl strip, int direction)
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
        if (s.Web != null) { CloseSession(s); return; }   // a web page: nothing running to lose
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
    /// <param name="withConnect">The main tabs' +: "Remote Desktop to a computer ..." above the shells.</param>
    /// <param name="refresh">Re-check WSL distributions too, and rebuild the menu if they changed while it's open.</param>
    private void FillShellMenu(ToolStripItemCollection items, ToolStrip owner, bool docked = false, bool withConnect = false,
        bool refresh = true)
    {
        items.Clear();
        // the menu bar's font (the app font): the toolbar terminal button's dropdown would otherwise inherit the
        // button's icon font, which has no letters, so Windows drew its text in a fallback font instead
        owner.Font = _menu.Font;
        if (withConnect)
        {
            items.Add(new ToolStripMenuItem("Remote Desktop to a computer ...", null, async (_, _) => await ConnectAdHocAsync()));
            items.Add(new ToolStripSeparator());
        }
        var def = Shells.Default(_settings.DefaultShell);
        foreach (var shell in Shells.Available())
        {
            var item = new ToolStripMenuItem(shell.Id == def.Id ? shell.Name + "  (default, Ctrl+T)" : shell.Name, ShellIcons.For(shell));
            var chosen = shell;
            item.Click += async (_, _) => await OpenShellAsync(chosen, docked);
            items.Add(item);
        }

        // Claude: Claude Code in a terminal (or its installer), and claude.ai in a tab
        items.Add(new ToolStripSeparator());
        var claudeCode = Shells.ClaudeCode();
        items.Add(claudeCode != null
            ? new ToolStripMenuItem("Claude Code", ShellIcons.For(claudeCode), async (_, _) => await OpenShellAsync(claudeCode, docked))
            : new ToolStripMenuItem("Install Claude Code ...", null, async (_, _) => await InstallClaudeCodeAsync(docked)));
        items.Add(new ToolStripMenuItem("Claude (claude.ai)", null, async (_, _) => await OpenClaudeWebAsync()));

        Theme.ApplyMenu(owner);
        if (refresh) RefreshShellMenu(items, owner, docked, withConnect);
    }

    /// <summary>A WSL distribution installed or removed since the last look: rebuild the menu if it's still open.</summary>
    private async void RefreshShellMenu(ToolStripItemCollection items, ToolStrip owner, bool docked, bool withConnect)
    {
        try
        {
            if (await Shells.RefreshWslAsync() && owner.Visible)
                FillShellMenu(items, owner, docked, withConnect, refresh: false);
        }
        catch (Exception ex) { Log.Write("Refreshing the shell menu failed: " + ex.Message); }
    }

    /// <summary>Environment for a shell: AWS_PROFILE set to the profile in use, and the bundled tools on PATH.</summary>
    private Dictionary<string, string> ShellEnvironment()
    {
        var env = new Dictionary<string, string>();
        if (Profile.Length > 0) env["AWS_PROFILE"] = Profile;
        var toolDirs = new[] { "aws-cli", "session-manager-plugin" }
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

    /// <summary>Claude Code isn't installed: offer Anthropic's installer, run in a tab so it can be watched.</summary>
    private async Task InstallClaudeCodeAsync(bool docked)
    {
        if (!Ask("Claude Code isn't installed on this PC.\n\nInstall it now with Anthropic's installer " +
                 "(irm https://claude.ai/install.ps1 | iex)? It runs in a tab here. When it's done, Claude Code is in this " +
                 "menu; it needs a Claude Pro, Max, Team or Enterprise plan, or an Anthropic Console account, to sign in.")) return;
        await OpenShellAsync(Shells.ClaudeCodeInstaller(), docked);
    }

    /// <summary>The claude.ai tab: switch to it if it's open, otherwise open it.</summary>
    private async Task OpenClaudeWebAsync()
    {
        var open = _sessions.FirstOrDefault(x => x.Web != null);
        if (open != null)
        {
            _tabs.SelectedTab = open.Page;
            open.Web.FocusPage();
            return;
        }

        var inst = new Instance { Name = "Claude", Id = "claude-web", Type = "claude-web" };
        var page = new TabPage("Claude") { BackColor = Theme.Current.Back };
        var web = new ClaudeWebHost { Dock = DockStyle.Fill };
        page.Controls.Add(web);
        var session = new Session { Inst = inst, Kind = Protocol.Local, Page = page, Web = web };
        _sessions.Add(session);
        _tabs.TabPages.Add(page);
        _tabs.Visible = true;
        _hint.Visible = false;
        _tabs.SelectedTab = page;
        try
        {
            await web.StartAsync();
            web.FocusPage();
            SetStatus("Opened claude.ai");
        }
        catch (Exception ex)
        {
            Log.Write("claude.ai tab failed to load: " + ex);
            CloseSession(session);
            MessageBox.Show(this, ex is Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException
                    ? "The Microsoft Edge WebView2 Runtime isn't installed, and the claude.ai tab needs it."
                    : "Couldn't open claude.ai: " + ex.Message,
                "Claude", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ---------------------------------------------------------------- system tray

    private void BuildTray()
    {
        _tray.Icon = AppIcon.Icon;
        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("&Open Switchboard", null, (_, _) => RestoreFromTray());
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("E&xit", null, (_, _) => ExitApp()));
        menu.Opening += (_, _) =>
        {
            // the app font, like the menu bar (this menu isn't part of the window, so it doesn't inherit it), with
            // "Open" the bold version of the same font; set on opening, so a font change in Settings applies at once
            menu.Font = _menu.Font;
            // through CreateFont: a fonts-folder font without a bold file falls back to its regular style, instead of
            // Windows substituting some other font that has a bold
            open.Font = Theme.CreateFont(Theme.UiFont, _menu.Font.Size, FontStyle.Bold);
            Theme.ApplyMenu(menu);
        };
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) RestoreFromTray(); };
        _tray.BalloonTipClicked += (_, _) => RestoreFromTray();

        // minimize to tray; remember how the window was, to bring it back the same way
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
            {
                if (_settings.MinimizeToTray && Visible) HideToTray(closing: false);
            }
            else _restoreState = WindowState;
        };
        UpdateTrayIcon();
    }

    /// <summary>The tray icon shows while either tray option is on (so it can always be found), or while the window is hidden.</summary>
    private void UpdateTrayIcon() => _tray.Visible = _settings.MinimizeToTray || _settings.CloseToTray || !Visible;

    private void HideToTray(bool closing)
    {
        Hide();
        _tray.Visible = true;
        if (_settings.TrayHintShown) return;
        _settings.TrayHintShown = true;
        _settings.Save();
        _tray.ShowBalloonTip(6000, $"{AppInfo.Name} is still running",
            (closing ? "Closing the window keeps it running here in the system tray" : "Minimized to the system tray") +
            ": click the icon to open it, or right-click > Exit to close it.", ToolTipIcon.Info);
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = _restoreState;
        ShowInTaskbar = true;
        Activate();
        UpdateTrayIcon();
    }

    private enum CloseChoice { Tray, Exit, Cancel }

    /// <summary>
    /// The first time the close button is used (and the user hasn't chosen in Settings): keep running in the tray, or
    /// exit? With "Don't ask me again" the answer becomes the Close to the system tray setting; without it, it's for
    /// this time only.
    /// </summary>
    private CloseChoice AskCloseChoice()
    {
        var tray = new TaskDialogButton("Keep running in the tray");
        var exit = new TaskDialogButton("Exit");
        var page = new TaskDialogPage
        {
            Caption = AppInfo.Name,
            Heading = "Keep Switchboard running in the system tray?",
            Text = "It can keep running in the tray when you close the window: sessions, tunnels and sign-in renewal carry on, " +
                   "and the tray icon opens it again. Or it can exit.",
            Icon = TaskDialogIcon.Information,
            Buttons = { tray, exit, TaskDialogButton.Cancel },
            DefaultButton = tray,
            Verification = new TaskDialogVerificationCheckBox("Don't ask me again"),
            Footnote = new TaskDialogFootnote("You can change this later in Settings > Appearance > Window."),
        };
        var result = TaskDialog.ShowDialog(this, page);
        var choice = result == tray ? CloseChoice.Tray : result == exit ? CloseChoice.Exit : CloseChoice.Cancel;
        if (choice == CloseChoice.Cancel) return choice;

        if (choice == CloseChoice.Tray) _settings.TrayHintShown = true;   // just explained: no notification needed
        if (page.Verification.Checked)
        {
            _settings.CloseToTray = choice == CloseChoice.Tray;
            _settings.CloseChoiceMade = true;
            Log.Write($"Close button set to {(choice == CloseChoice.Tray ? "keep running in the tray" : "exit")} (don't ask again)");
        }
        _settings.Save();
        UpdateTrayIcon();
        return choice;
    }

    /// <summary>Switchboard was started again while this copy runs (one copy only): come to the front, from the tray too.</summary>
    private void OnActivateRequested()
    {
        if (IsDisposed || !IsHandleCreated) return;   // still starting up: its window comes up by itself
        BeginInvoke(() =>
        {
            if (!Visible) RestoreFromTray();
            else if (WindowState == FormWindowState.Minimized) WindowState = _restoreState;
            Activate();
            BringToFront();
            Log.Write("Single copy: started again, brought this window to the front");
        });
    }

    /// <summary>Exit (tray menu or File): really close, with the usual questions about open sessions.</summary>
    private void ExitApp()
    {
        _exitRequested = true;
        if (!Visible) RestoreFromTray();   // so the "close open sessions?" question has a window to belong to
        Close();
        // if the question was answered "no", the next close button press follows the tray option again
        if (!IsDisposed) BeginInvoke(() => _exitRequested = false);
    }

    // ---------------------------------------------------------------- server window

    /// <summary>The Server window for an EC2 server (details, screenshot and log, tags, protection ...).</summary>
    private async Task ManageServerAsync(Instance inst)
    {
        if (string.IsNullOrEmpty(Profile)) { SetStatus("Choose an AWS profile and sign in first."); return; }
        if (!await EnsureSignedInAsync($"managing {inst.Name}")) { SetStatus("Not signed in to AWS."); return; }
        var peers = _aws.Where(x => x.Region == inst.Region && x.Online && x.Id != inst.Id)
            .Select(x => new CommandTarget(x.Id, x.Name, x.Platform.Contains("Windows", StringComparison.OrdinalIgnoreCase)));
        using var f = new ServerForm(Profile, inst.Region, inst.Id, inst.Name, _settings, peers);
        f.ShowDialog(this);
        if (f.Terminated) AfterTermination(inst);
        if (f.Changed || f.Terminated) await RefreshAsync();
    }

    // ---------------------------------------------------------------- remembering the layout

    /// <summary>The window where it was last time (if that's still on a screen), and maximized if it was.</summary>
    private void ApplySavedWindowBounds()
    {
        if (_settings.WindowBounds is { Length: 4 } b && b[2] >= 600 && b[3] >= 400)
        {
            var bounds = new Rectangle(b[0], b[1], b[2], b[3]);
            // at least a decent part of it on a screen that's connected now (a monitor may have gone)
            bool onScreen = Screen.AllScreens.Any(sc =>
            {
                var overlap = Rectangle.Intersect(sc.WorkingArea, bounds);
                return overlap.Width >= 200 && overlap.Height >= 150;
            });
            if (onScreen)
            {
                StartPosition = FormStartPosition.Manual;
                Bounds = bounds;
            }
        }
        if (_settings.WindowMaximized) WindowState = FormWindowState.Maximized;
    }

    /// <summary>Something in the layout changed: save it a moment later (once changes settle).</summary>
    private void LayoutChanged()
    {
        if (!IsHandleCreated || _closeReady) return;
        _layoutSave.Stop();
        _layoutSave.Start();
    }

    /// <summary>Save the window's position, size and state, the columns, the list width and the dock height.</summary>
    private void SaveLayoutNow()
    {
        _layoutSave.Stop();
        try
        {
            if (Visible)
            {
                // the normal (not maximized) bounds, so restoring from maximized goes back to the right size
                var normal = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                if (normal.Width >= 600 && normal.Height >= 400)
                    _settings.WindowBounds = new[] { normal.Left, normal.Top, normal.Width, normal.Height };
                _settings.WindowMaximized = WindowState == FormWindowState.Maximized ||
                                            (WindowState == FormWindowState.Minimized && _restoreState == FormWindowState.Maximized);
            }
            if (!_split.Panel1Collapsed && Visible) _settings.ListWidth = _split.SplitterDistance;
            CaptureColumnWidths();
            _settings.Save();
        }
        catch (Exception ex) { Log.Write("Couldn't save the layout: " + ex.Message); }
    }

    /// <summary>
    /// Tab menu > Reconnect. SSH reconnects in place (as Enter does when it has ended). Remote Desktop gets a fresh
    /// connection and tunnel with the same user (and a typed Connect as or one-off password, kept in memory), in the
    /// same position: for a session left stale after Windows was locked and its tunnel timed out.
    /// </summary>
    private async Task ReconnectSessionAsync(Session s)
    {
        if (s == null || !_sessions.Contains(s)) return;
        if (s.Reconnecting) { s.RetryNow?.TrySetResult(true); return; }
        Log.Write($"Reconnecting {s.Inst.Name} ({s.Kind}) from the tab menu");
        if (s.Kind == Protocol.Vnc) { await RestartVncAsync(s); return; }
        if (s.Kind == Protocol.Ssh)
        {
            await RunSshAsync(s, reconnect: true);
            return;
        }
        if (s.Kind != Protocol.Rdp) return;

        var inst = s.Inst;
        string asUser = s.AsUser, asPassword = s.AsPassword;
        string oneOff = inst.Saved != null && _adHocPasswords.TryGetValue(inst.Saved.Id, out var typed) ? typed : null;
        int index = _tabs.TabPages.IndexOf(s.Page);
        SetStatus($"Reconnecting to {inst.Name} ...");
        CloseSession(s);
        if (oneOff != null) _adHocPasswords[inst.Saved.Id] = oneOff;   // closing forgets it; this connection still needs it

        await ConnectAsync(inst, Protocol.Rdp, asUser, asPassword ?? "");
        var fresh = _sessions.LastOrDefault(x => x.Inst.Id == inst.Id && x.Kind == Protocol.Rdp && x.AsUser == asUser);
        if (fresh != null && index >= 0)
        {
            int now = _tabs.TabPages.IndexOf(fresh.Page);
            if (now >= 0) _tabs.MoveTab(now, Math.Min(index, _tabs.TabCount - 1));   // back where it was (no page is detached)
        }
    }

    /// <summary>Which strip a session's tab is on (main or docked), and its position there.</summary>
    private (ThemedTabControl Strip, int Index) TabPosition(Session s)
    {
        if (s?.Page == null) return (null, -1);
        var strip = s.Docked ? _dockTabs : _tabs;
        return (strip, strip.TabPages.IndexOf(s.Page));
    }

    private void MoveTabBy(Session s, int delta)
    {
        var (strip, index) = TabPosition(s);
        if (strip == null || index < 0) return;
        strip.MoveTab(index, Math.Clamp(index + delta, 0, strip.TabCount - 1));
    }

    /// <summary>A remote tab's badge: AWS (through Session Manager), or the protocol of a saved connection.</summary>
    private static string BadgeFor(Instance inst, Protocol protocol) =>
        !inst.IsSaved ? "AWS" : protocol == Protocol.Ssh ? "SSH" : protocol == Protocol.Vnc ? "VNC" : "RDP";

    /// <summary>
    /// " · SSH" / " · VNC" after the name, for AWS tabs only: their badge says AWS whatever the protocol, while a saved
    /// connection's badge already shows SSH or VNC. (Remote Desktop has no suffix either way.)
    /// </summary>
    private static string ProtocolSuffix(Instance inst, Protocol protocol) =>
        inst.IsSaved ? "" : protocol switch { Protocol.Ssh => " · SSH", Protocol.Vnc => " · VNC", _ => "" };

    /// <summary>A remote tab's text: room for its badge (drawn by the tab strip), then the title.</summary>
    private string Badged(Instance inst, Protocol protocol, string title) => _tabs.BadgeRoom(BadgeFor(inst, protocol)) + title;

    // ---------------------------------------------------------------- VNC

    /// <summary>A VNC tab: through a Session Manager tunnel to port 5900 for an AWS server, or straight to a saved address.</summary>
    private async Task OpenVncAsync(Instance inst)
    {
        Tunnel tunnel = null;
        if (!inst.IsSaved)
        {
            if (!await EnsureSignedInAsync($"connecting to {inst.Name}")) { SetStatus("Not signed in to AWS."); return; }
            SetStatus($"Opening a Session Manager tunnel to {inst.Name} (VNC, port 5900) ...");
            try { tunnel = await Tunnel.StartAsync(inst, Profile, 5900); }
            catch (Exception ex)
            {
                SetStatus("");
                MessageBox.Show(this, $"Couldn't open a tunnel to {inst.Name} for VNC: {(ex is TunnelException ? ex.Message : Aws.Describe(ex))}",
                    "VNC", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        var page = new TabPage(Badged(inst, Protocol.Vnc, inst.Name + ProtocolSuffix(inst, Protocol.Vnc)))
            { BackColor = Theme.Current.Back, Tag = ThemedTabControl.BadgeTag(BadgeFor(inst, Protocol.Vnc)) };
        var host = new VncHost { Dock = DockStyle.Fill };
        page.Controls.Add(host);
        var session = new Session { Inst = inst, Kind = Protocol.Vnc, Tunnel = tunnel, Vnc = host, Page = page };
        _sessions.Add(session);
        _tabs.TabPages.Add(page);
        _tabs.Visible = true;
        _hint.Visible = false;
        _tabs.SelectedTab = page;

        host.CredentialsRequired += needUser => BeginInvoke(() => VncSignIn(session, needUser));
        host.SecurityFailure += reason => { session.VncTriedSaved = true; session.VncProblem = reason.Length > 0 ? reason : "The password wasn't accepted."; };
        host.Connected += () => BeginInvoke(() =>
        {
            session.WasConnected = true;
            session.VncProblem = "";
            SetStatus($"Connected to {inst.Name} (VNC)");
        });
        host.ReconnectRequested += () => BeginInvoke(new Action(async () => await RestartVncAsync(session)));
        Log.Write($"VNC: connecting to {inst.Name} ({VncTarget(session)})");
        try { await host.StartAsync(VncTarget(session).Host, VncTarget(session).Port); }
        catch (Exception ex)
        {
            Log.Write("VNC tab failed to start: " + ex);
            CloseSession(session);
            MessageBox.Show(this, "Couldn't open the VNC tab: " + ex.Message, "VNC", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static (string Host, int Port) VncTarget(Session s) =>
        s.Inst.IsSaved ? (s.Inst.Saved.Host, s.Inst.Saved.Port) : ("127.0.0.1", s.Tunnel?.Port ?? 0);

    /// <summary>Reconnect a VNC tab: a fresh tunnel for an AWS server, then a fresh relay and connection.</summary>
    private async Task RestartVncAsync(Session s)
    {
        if (!_sessions.Contains(s) || s.Vnc == null) return;
        s.WasConnected = false;
        if (!s.Inst.IsSaved)
        {
            try { s.Tunnel?.Dispose(); } catch { }
            s.Tunnel = null;
            if (!await EnsureSignedInAsync($"reconnecting to {s.Inst.Name}")) { SetStatus("Not signed in to AWS."); return; }
            try { s.Tunnel = await Tunnel.StartAsync(s.Inst, Profile, 5900); }
            catch (Exception ex) { SetStatus($"Couldn't reopen the tunnel to {s.Inst.Name}: {ex.Message}"); return; }
        }
        await s.Vnc.StartAsync(VncTarget(s).Host, VncTarget(s).Port);
    }

    /// <summary>The VNC server wants a password: the saved one first (once), otherwise ask (and optionally save it).</summary>
    private void VncSignIn(Session s, bool needUser)
    {
        if (!_sessions.Contains(s) || s.Vnc == null) return;
        string key = "vnc:" + (s.Inst.IsSaved ? $"{s.Inst.Saved.Host}:{s.Inst.Saved.Port}" : s.Inst.Id);
        if (!s.VncTriedSaved && CredentialStore.Read(key) is { Length: > 0 } saved)
        {
            s.VncTriedSaved = true;
            int sep = saved.IndexOf('\u0001');
            s.Vnc.SendCredentials(sep >= 0 ? saved[..sep] : "", sep >= 0 ? saved[(sep + 1)..] : saved);
            return;
        }
        using var f = new VncLoginForm(s.Inst.Name, needUser, s.Inst.Saved?.User ?? "", s.VncProblem);
        if (f.ShowDialog(this) != DialogResult.OK) { SetStatus($"VNC sign-in to {s.Inst.Name} cancelled."); return; }
        if (f.SavePassword)
        {
            try { CredentialStore.Save(key, f.User + "\u0001" + f.Password); }
            catch (Exception ex) { Log.Write("Couldn't save the VNC password: " + ex.Message); }
        }
        s.Vnc.SendCredentials(f.User, f.Password);
    }

    // ---------------------------------------------------------------- restoring tabs

    /// <summary>The open tabs, in order (main tabs, then docked), and the terminals among them (by index in Tabs).</summary>
    private (SavedTabs Tabs, List<(int Index, TerminalHost Term)> Terminals) CaptureTabs()
    {
        var saved = new SavedTabs();
        var terminals = new List<(int, TerminalHost)>();
        foreach (var page in _tabs.TabPages.Cast<TabPage>().Concat(_dockTabs.TabPages.Cast<TabPage>()))
        {
            if (_placeholders.TryGetValue(page, out var waiting))
            {
                if (page == _tabs.SelectedTab) saved.Selected = saved.Tabs.Count;
                saved.Tabs.Add(waiting);
                continue;
            }
            var s = _sessions.FirstOrDefault(x => x.Page == page);
            if (s == null) continue;
            var tab = new TabState { Name = s.Inst.Name, AsUser = s.AsUser, Docked = s.Docked };
            if (s.Web != null) tab.Kind = "claude-web";
            else if (s.Kind == Protocol.Local)
            {
                if (s.Inst.Type == "claude-code-install") continue;   // the installer isn't something to reopen
                tab.Kind = "shell";
                tab.ShellId = s.Inst.Type;
            }
            else
            {
                tab.Kind = s.Kind == Protocol.Ssh ? "ssh" : s.Kind == Protocol.Vnc ? "vnc" : "rdp";
                if (s.Inst.IsSaved) tab.SavedId = s.Inst.Saved.Id;
                else { tab.InstanceId = s.Inst.Id; tab.Region = s.Inst.Region; }
            }
            if (page == _tabs.SelectedTab) saved.Selected = saved.Tabs.Count;
            if (s.Term != null) terminals.Add((saved.Tabs.Count, s.Term));
            saved.Tabs.Add(tab);
        }
        return (saved, terminals);
    }

    /// <summary>
    /// Start-up: reopen last time's tabs. Shells and Claude tabs reopen; remote tabs reconnect, the first few at once
    /// and the rest as Reconnect placeholders; AWS tabs wait until signed in with the server list loaded.
    /// </summary>
    public async Task RestoreTabsAsync()
    {
        if (!_settings.RestoreTabs) return;
        var saved = SessionRestore.Load();
        if (saved == null || saved.Tabs.Count == 0) return;
        _restoredFrom = saved.SavedAt;
        Log.Write($"Restoring {saved.Tabs.Count} tab(s) from {saved.SavedAt:yyyy-MM-dd HH:mm}");
        var selected = saved.Selected >= 0 && saved.Selected < saved.Tabs.Count ? saved.Tabs[saved.Selected] : null;
        TabPage selectPage = null;
        int reconnects = ReconnectAtStart;

        foreach (var tab in saved.Tabs)
        {
            try
            {
                TabPage page;
                if (tab.IsAws && _aws.Count == 0) { _pendingRestore.Add(tab); continue; }
                if (tab.IsRemote)
                {
                    if (reconnects > 0) { reconnects--; page = await RestoreRemoteAsync(tab); }
                    else page = AddPlaceholder(tab);
                }
                else page = await RestoreLocalAsync(tab);
                if (tab == selected && page != null) selectPage = page;
            }
            catch (Exception ex) { Log.Write($"Couldn't reopen {tab.Name}: {ex.Message}"); }
        }
        if (_placeholders.Count > 0) ApplyTheme();   // the placeholders' buttons
        if (selectPage != null && _tabs.TabPages.Contains(selectPage)) _tabs.SelectedTab = selectPage;
        if (_pendingRestore.Count > 0)
            SetStatus($"{_pendingRestore.Count} AWS tab(s) from last time reopen once you're signed in.");
    }

    /// <summary>AWS tabs that waited for sign-in: the server list has loaded now.</summary>
    private async Task RestorePendingAsync()
    {
        var waiting = _pendingRestore.ToList();
        _pendingRestore.Clear();
        int reconnects = ReconnectAtStart;
        foreach (var tab in waiting)
        {
            try
            {
                if (reconnects > 0) { reconnects--; await RestoreRemoteAsync(tab); }
                else AddPlaceholder(tab);
            }
            catch (Exception ex) { Log.Write($"Couldn't reopen {tab.Name}: {ex.Message}"); }
        }
        if (_placeholders.Count > 0) ApplyTheme();
    }

    private async Task<TabPage> RestoreLocalAsync(TabState tab)
    {
        if (tab.Kind == "claude-web")
        {
            await OpenClaudeWebAsync();
            return _sessions.FirstOrDefault(x => x.Web != null)?.Page;
        }
        ShellInfo shell;
        if (tab.ShellId == "claude-code")
        {
            // Claude Code: pick up its last conversation (claude --continue)
            var cc = Shells.ClaudeCode();
            shell = cc == null ? null : cc with { CommandLine = cc.CommandLine + " --continue" };
        }
        else shell = Shells.Find(tab.ShellId);
        if (shell == null) { Log.Write($"Restore: {tab.Name} wasn't reopened ({tab.ShellId} isn't available now)"); return null; }
        await OpenShellAsync(shell, tab.Docked, SessionRestore.ReadScrollback(tab));
        return _sessions.LastOrDefault(x => x.Kind == Protocol.Local && x.Web == null)?.Page;
    }

    /// <summary>The server a remote tab was connected to: a saved connection, or an AWS server in the loaded list.</summary>
    private Instance FindTarget(TabState tab)
    {
        if (tab.SavedId.Length > 0)
            return _saved.FirstOrDefault(c => c.Id == tab.SavedId) is { } c ? ConnectionStore.ToInstance(c, null) : null;
        return _aws.FirstOrDefault(i => i.Id == tab.InstanceId);
    }

    private async Task<TabPage> RestoreRemoteAsync(TabState tab)
    {
        var inst = FindTarget(tab);
        if (inst == null)
        {
            Log.Write($"Restore: {tab.Name} wasn't reopened (the server or connection no longer exists)");
            return null;
        }
        var protocol = tab.Kind == "ssh" ? Protocol.Ssh : tab.Kind == "vnc" ? Protocol.Vnc : Protocol.Rdp;
        await ConnectAsync(inst, protocol, tab.AsUser, "", tab.Kind == "ssh" ? SessionRestore.ReadScrollback(tab) : null);
        var s = _sessions.LastOrDefault(x => x.Inst.Id == inst.Id && x.Kind == protocol && x.AsUser == tab.AsUser);
        if (s != null && tab.Docked && s.Term != null && !s.Docked) DockSession(s);
        return s?.Page;
    }

    /// <summary>A remote tab that waits for a click: Reconnect opens it (as a new tab); × closes it.</summary>
    private TabPage AddPlaceholder(TabState tab)
    {
        // the protocol after the name only for AWS tabs, whose badge says AWS (a saved connection's badge shows it)
        string title = tab.Name + (tab.IsAws ? (tab.Kind == "ssh" ? " · SSH" : tab.Kind == "vnc" ? " · VNC" : "") : "") + (tab.AsUser != null ? $" ({tab.AsUser})" : "");
        string badge = tab.IsAws ? "AWS" : tab.Kind == "ssh" ? "SSH" : tab.Kind == "vnc" ? "VNC" : "RDP";
        var page = new TabPage(_tabs.BadgeRoom(badge) + title) { BackColor = Theme.Current.Back, Tag = ThemedTabControl.BadgeTag(badge) };
        var label = new Label
        {
            Text = $"{title} was open last time.", AutoSize = true, UseMnemonic = false, Anchor = AnchorStyles.None,
            ForeColor = Theme.Current.Muted, Tag = "muted", Margin = new Padding(0, 0, 0, 12),
        };
        var button = new Button { Text = "Reconnect", Tag = "primary", AutoSize = true, MinimumSize = new Size(140, 34), Anchor = AnchorStyles.None };
        button.Click += async (_, _) => await ActivatePlaceholderAsync(page);
        var center = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        center.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        center.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        center.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        center.Controls.Add(label, 0, 1);
        center.Controls.Add(button, 0, 2);
        page.Controls.Add(center);
        _placeholders[page] = tab;
        _tabs.TabPages.Add(page);
        _tabs.Visible = true;
        _hint.Visible = false;
        return page;
    }

    private async Task ActivatePlaceholderAsync(TabPage page)
    {
        if (!_placeholders.TryGetValue(page, out var tab)) return;
        if (tab.IsAws && FindTarget(tab) == null)
        {
            SetStatus(_aws.Count == 0 ? $"Sign in to AWS to reconnect {tab.Name}." : $"{tab.Name} isn't in the server list any more.");
            return;
        }
        _placeholders.Remove(page);
        _tabs.TabPages.Remove(page);
        page.Dispose();
        var opened = await RestoreRemoteAsync(tab);
        if (opened != null && _tabs.TabPages.Contains(opened)) _tabs.SelectedTab = opened;
        UpdateMainTabsVisibility();
    }

    // ---------------------------------------------------------------- port forwarding and terminate

    /// <summary>Port forwarding through a server's Session Manager: to a port on the server, or a host behind it.</summary>
    private async Task StartPortForwardAsync(Instance inst)
    {
        using var f = new PortForwardForm(inst.Name);
        if (f.ShowDialog(this) != DialogResult.OK) return;
        if (!await EnsureSignedInAsync($"forwarding a port through {inst.Name}")) { SetStatus("Not signed in to AWS."); return; }
        string target = (f.Host.Length > 0 ? f.Host : inst.Name) + ":" + f.RemotePort;
        SetStatus($"Opening a forward to {target} through {inst.Name} ...");
        try
        {
            var tunnel = await Tunnel.StartAsync(inst, Profile, f.RemotePort, f.Host, f.LocalPort);
            var forward = new PortForward { Server = inst, Tunnel = tunnel, Host = f.Host, RemotePort = f.RemotePort };
            _forwards.Add(forward);
            Log.Write($"Port forward: {forward.LocalAddress} -> {target} through {inst.Id}");
            SetStatus($"Forwarding {forward.LocalAddress} to {target} (the address is on the clipboard; Server > Port forwards lists it).");
            try { Clipboard.SetText(forward.LocalAddress); } catch { }
            ShowForwards();
        }
        catch (Exception ex)
        {
            SetStatus("");
            MessageBox.Show(this, $"The forward to {target} didn't start: {(ex is TunnelException ? ex.Message : Aws.Describe(ex))}",
                "Port forwarding", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowForwards()
    {
        if (_forwardsForm is { IsDisposed: false }) { _forwardsForm.Fill(); _forwardsForm.Activate(); return; }
        _forwardsForm = new ForwardsForm(_forwards, StopForward);
        _forwardsForm.Show(this);
    }

    private void StopForward(PortForward f)
    {
        try { f.Tunnel?.Dispose(); } catch { }
        _forwards.Remove(f);
        Log.Write($"Port forward stopped: {f.LocalAddress} -> {f.Target}");
        SetStatus($"Stopped forwarding {f.LocalAddress}.");
    }

    /// <summary>Terminate (type-the-name confirmation); its open tabs and forwards are closed afterwards.</summary>
    private async Task TerminateServerAsync(Instance inst)
    {
        if (!await EnsureSignedInAsync($"terminating {inst.Name}")) { SetStatus("Not signed in to AWS."); return; }
        using var f = new TerminateForm(Profile, inst.Region, inst.Id, inst.Name);
        if (f.ShowDialog(this) != DialogResult.OK || !f.Terminated) return;
        AfterTermination(inst);
        await RefreshAsync();
    }

    private void AfterTermination(Instance inst)
    {
        foreach (var s in _sessions.Where(x => x.Inst.Id == inst.Id).ToList()) CloseSession(s);
        foreach (var fw in _forwards.Where(x => x.Server.Id == inst.Id).ToList()) StopForward(fw);
        SetStatus($"{inst.Name} is being terminated.");
    }

    // ---------------------------------------------------------------- new server

    /// <summary>AWS > New server (from == null), or "New server like this one" from an EC2 server.</summary>
    private async Task OpenNewServerAsync(Instance from)
    {
        if (string.IsNullOrEmpty(Profile)) { SetStatus("Choose an AWS profile and sign in first."); return; }
        LaunchSpec clone = null;
        if (from != null)
        {
            SetStatus($"Reading {from.Name}'s settings ...");
            try { clone = await ServerLaunch.ReadForCloneAsync(Profile, from.Region, from.Id, CancellationToken.None); }
            catch (Exception ex)
            {
                SetStatus("");
                MessageBox.Show(this, $"Couldn't read {from.Name}'s settings: {Aws.Describe(ex)}", "New server",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            SetStatus("");
        }

        var regions = _settings.ScanRegions.Count > 0 ? _settings.ScanRegions : new List<string> { "us-east-1" };
        using var f = new NewServerForm(Profile, _settings, regions,
            (region, name) => _aws.Any(i => i.Region == region && i.Name.Equals(name, StringComparison.OrdinalIgnoreCase)),
            clone, from?.Name);
        if (f.ShowDialog(this) != DialogResult.OK || f.LaunchedId.Length == 0) return;

        SetStatus($"Launched {f.LaunchedName} ({f.LaunchedId}) in {f.LaunchedRegion}. It's in the list now; Connect works once " +
                  "Session Manager reports it online, usually within a few minutes.");
        if (f.LaunchWarning.Length > 0)
            MessageBox.Show(this, $"{f.LaunchedName} was launched, but {f.LaunchWarning}. You can change that in the AWS console " +
                                  "(the network interface's Actions > Change source/dest. check).", "New server",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        await RefreshAsync();
    }

    // ---------------------------------------------------------------- updates

    private void BuildUpdateBanner()
    {
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
        row.Controls.AddRange(new Control[] { _updateText, _updateNotes, _updateNow, _updateLater, _updateSkip });
        _updateBanner.Controls.Add(row);
        _updateNotes.LinkClicked += (_, _) => ShowReleaseNotes();
        _updateNow.LinkClicked += async (_, _) => await UpdateNowAsync();
        _updateLater.LinkClicked += (_, _) => _updateBanner.Visible = false;   // until next start (or the next check)
        _updateSkip.LinkClicked += (_, _) =>
        {
            if (_update != null) { _settings.SkippedVersion = _update.Version.ToString(); _settings.Save(); }
            _updateBanner.Visible = false;
        };
    }

    private void StyleUpdateBanner()
    {
        var p = Theme.Current;
        _updateBanner.BackColor = p.Hover;
        _updateText.ForeColor = p.Text;
        foreach (var link in new[] { _updateNotes, _updateNow, _updateLater, _updateSkip })
        {
            link.LinkColor = link.ActiveLinkColor = link.VisitedLinkColor = p.AccentFore;
            link.LinkBehavior = LinkBehavior.HoverUnderline;
            link.Font = _menu.Font;
        }
        _updateText.Font = _menu.Font;
    }

    /// <summary>
    /// Look for a newer release on GitHub. Automatic checks: at most once a day, if enabled and not turned off by
    /// policy, and quiet about failures and skipped versions. Manual ones (Help, Settings) always say what they found.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (Updater.DisabledByPolicy)
        {
            if (manual) MessageBox.Show(this, "Update checks are turned off by your administrator.", "Check for updates",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!manual && (!_settings.CheckForUpdates || DateTime.UtcNow - _settings.LastUpdateCheck < TimeSpan.FromHours(20))) return;

        try
        {
            if (manual) SetStatus("Checking for updates ...");
            var update = await Updater.CheckAsync(CancellationToken.None);
            _settings.LastUpdateCheck = DateTime.UtcNow;
            _settings.Save();
            if (update == null)
            {
                Log.Write($"Update check: {AppInfo.Version} is the latest");
                if (manual)
                {
                    SetStatus("");
                    MessageBox.Show(this, $"You have the latest version ({AppInfo.Version}).", "Check for updates",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                return;
            }
            Log.Write($"Update check: {update.Tag} is available");
            if (!manual && update.Version.ToString() == _settings.SkippedVersion) return;
            _update = update;
            _updateText.Text = $"Switchboard {update.Version} is available (you have {AppInfo.Version}).";
            StyleUpdateBanner();
            _updateBanner.Visible = true;
            if (manual) SetStatus($"Switchboard {update.Version} is available.");
        }
        catch (Exception ex)
        {
            Log.Write("Update check failed: " + ex.Message);
            if (manual)
            {
                SetStatus("");
                MessageBox.Show(this, "Couldn't check for updates: " + ex.Message, "Check for updates",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    private async void ShowReleaseNotes()
    {
        if (_update == null) return;
        string notes = _update.Notes.Length > 0 ? _update.Notes : "No notes were published with this release.";
        bool updateRequested;
        using (var f = new NotesForm($"What's new in Switchboard {_update.Version}", $"You have {AppInfo.Version}", notes,
                   _update.PageUrl, offerUpdate: true))
        {
            f.ShowDialog(this);
            updateRequested = f.UpdateRequested;
        }
        if (updateRequested) await UpdateNowAsync();
    }

    /// <summary>
    /// Update now: an installed copy downloads the setup, checks it, and runs it silently in the same scope (it closes
    /// Switchboard, updates it and starts it again). A copy not installed with the setup opens the release page.
    /// </summary>
    private async Task UpdateNowAsync()
    {
        var update = _update;
        if (update == null) return;
        var (installed, allUsers) = Updater.InstallState();
        if (!installed || update.SetupUrl.Length == 0)
        {
            OpenInBrowser(update.PageUrl);
            SetStatus("This copy wasn't installed with the setup, so it can't update itself: download the new version from the release page.");
            return;
        }

        int open = _sessions.Count(x => x.Web == null);
        if (!Ask(open > 0
                ? $"Updating to {update.Version} closes Switchboard and its {open} open tab(s), then starts it again. Update now?"
                : $"Update to {update.Version} now? Switchboard closes, updates and starts again.")) return;

        _updateNow.Enabled = false;
        string setup;
        try
        {
            setup = await Updater.DownloadAsync(update, new Progress<int>(p => SetStatus($"Downloading the update: {p}%")), CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Write("Update download/check failed: " + ex.Message);
            SetStatus("");
            _updateNow.Enabled = true;
            if (MessageBox.Show(this, $"The update wasn't installed: {ex.Message}.\n\nOpen the release page to download it yourself?",
                    "Update", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                OpenInBrowser(update.PageUrl);
            return;
        }

        if (allUsers) SetStatus("Installing the update: Windows asks for permission, since Switchboard is installed for all users.");
        try { Updater.RunSetup(setup, allUsers); }
        catch (Exception ex)
        {
            // e.g. the permission prompt was declined
            Log.Write("Update setup didn't start: " + ex.Message);
            SetStatus("The update didn't start: " + ex.Message);
            _updateNow.Enabled = true;
            return;
        }
        _updating = true;
        Close();
    }

    private static void OpenInBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("Couldn't open " + url + ": " + ex.Message); }
    }

    /// <summary>Open a local shell in a new tab, starting in the user folder.</summary>
    /// <param name="replay">Restored output from the last session, shown dimmed above the new shell.</param>
    private async Task OpenShellAsync(ShellInfo shell, bool docked = false, string replay = null)
    {
        docked |= _settings.DockNewTerminals;   // Settings > Terminal & SSH > Open new terminals docked
        // "PowerShell", then "PowerShell 2", "PowerShell 3" ... for more of the same kind
        var used = new HashSet<string>(_sessions.Where(x => x.Kind == Protocol.Local).Select(x => x.Inst.Name));
        string name = shell.Name;
        for (int n = 2; used.Contains(name); n++) name = $"{shell.Name} {n}";

        var inst = new Instance { Name = name, Id = "shell:" + Guid.NewGuid().ToString("N"), Type = shell.Id };
        var page = new TabPage(ThemedTabControl.IconSpace + name) { BackColor = Theme.Current.Back, Tag = ThemedTabControl.ShellTag };
        ThemedTabControl.SetTabIcon(page, ShellIcons.For(shell, _tabs.LogicalToDeviceUnits(16)));   // its program's own icon, at this screen's scale
        var term = new TerminalHost { Dock = DockStyle.Fill, EndedAction = "restart" };
        term.AppShortcut += key => OnAppShortcut(key, _sessions.FirstOrDefault(x => x.Term == term));
        // Esc on an ended terminal closes its tab (after this key event, since closing destroys the terminal)
        term.CloseRequested += () => BeginInvoke(() =>
        {
            var ended = _sessions.FirstOrDefault(x => x.Term == term);
            if (ended != null) CloseSession(ended);
        });
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
        if (!string.IsNullOrEmpty(replay)) term.Replay(replay, _restoredFrom);
        term.Run(ShellCommandLine(shell), home, ShellEnvironment());
        term.FocusTerminal();
        SetStatus($"Opened {name}" + (Profile.Length > 0 ? $" (AWS_PROFILE={Profile})" : ""));
    }

    // ---------------------------------------------------------------- SSH

    /// <summary>Open an SSH terminal tab: tunnel to port 22 for AWS servers (direct for saved ones), then ssh.exe.</summary>
    private async Task OpenSshAsync(Instance inst, string asUser = null, string replay = null)
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

        var page = new TabPage(Badged(inst, Protocol.Ssh, inst.Name + ProtocolSuffix(inst, Protocol.Ssh) + (asUser == null ? "" : $" ({asUser})")))
            { BackColor = Theme.Current.Back, Tag = ThemedTabControl.BadgeTag(BadgeFor(inst, Protocol.Ssh)) };
        var term = new TerminalHost { Dock = DockStyle.Fill };
        term.AppShortcut += key => OnAppShortcut(key, _sessions.FirstOrDefault(x => x.Term == term));
        // Esc on an ended terminal closes its tab (after this key event, since closing destroys the terminal)
        term.CloseRequested += () => BeginInvoke(() =>
        {
            var ended = _sessions.FirstOrDefault(x => x.Term == term);
            if (ended != null) CloseSession(ended);
        });
        page.Controls.Add(term);
        var session = new Session { Inst = inst, Kind = Protocol.Ssh, Term = term, Page = page, Docked = _settings.DockNewTerminals, AsUser = asUser };
        _sessions.Add(session);
        if (!string.IsNullOrEmpty(replay)) term.Replay(replay, _restoredFrom);
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
            page.Text = Badged(inst, Protocol.Ssh, inst.Name + ProtocolSuffix(inst, Protocol.Ssh) + " (ended)");
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
            s.Page.Text = Badged(inst, Protocol.Ssh, inst.Name + ProtocolSuffix(inst, Protocol.Ssh) + (s.AsUser == null ? "" : $" ({s.AsUser})"));
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

        string cmd = SshCommand(inst, s.Tunnel, s.AsUser);
        Log.Write("SSH: " + cmd);
        term.Notice(cmd.Replace(Tools.Ssh, "ssh").Replace("\"ssh\"", "ssh"));
        term.Run(cmd);
        term.FocusTerminal();
        SetStatus($"SSH to {inst.Name}");
    }

    /// <summary>The ssh.exe command line for a server: through the tunnel on localhost, or straight to a saved host.</summary>
    /// <param name="asUser">Connect as: this tab's own login (null = the usual user).</param>
    private string SshCommand(Instance inst, Tunnel tunnel, string asUser = null)
    {
        var args = new List<string>();
        string user, host;
        int port;
        if (asUser != null)
        {
            // Connect as: this tab's own login, to the same server
            user = asUser;
            host = inst.IsSaved ? inst.Saved.Host : "127.0.0.1";
            port = inst.IsSaved ? inst.Saved.Port : tunnel.Port;
            if (!inst.IsSaved) { args.Add("-o"); args.Add("HostKeyAlias=" + inst.Id); }
        }
        else if (inst.IsSaved)
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
        if (s.AsUser != null)
        {
            // Connect as: the user given for this tab; its typed password, else the one saved for that user, else RDP asks
            user = s.AsUser;
            password = s.AsPassword.Length > 0 ? s.AsPassword : CredentialStore.Read(user) ?? "";
        }
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
                if (state == 1 && s.Reconnecting) FinishReconnect(s);
            }
            else if (s.Reconnecting || s.Overlay is { Visible: true }) continue;   // being reconnected, or waiting for a click
            else if (s.WasConnected)
            {
                if (IsDeliberateEnd(s))
                {
                    SetStatus($"{s.Inst.Name} disconnected");
                    CloseSession(s);
                }
                else _ = AutoReconnectAsync(s);
            }
            else if (!s.Flagged && (s.SawActivity || (DateTime.Now - s.Started).TotalSeconds > 45))
            {
                s.Flagged = true;
                s.Page.Text = Badged(s.Inst, s.Kind, s.Inst.Name + " (not connected)");
            }
        }
    }

    // ---------------------------------------------------------------- reconnecting a lost Remote Desktop connection

    /// <summary>
    /// The session was ended on purpose (the tab closes): this side disconnected (1), the remote user disconnected or
    /// signed out (2), or the server ended it for an administrative reason, a logoff or another connection taking over
    /// (extended 1, 2, 5, 11, 12). Anything else (a network drop, a timeout, the tunnel dying, the server restarting)
    /// is a lost connection, which is reconnected.
    /// </summary>
    private static bool IsDeliberateEnd(Session s) =>
        s.DisconnectReason is 1 or 2 || s.ExtendedReason is 1 or 2 or 5 or 11 or 12;

    private string RdpTitle(Session s, string suffix = "") =>
        Badged(s.Inst, Protocol.Rdp, (s.AsUser == null ? s.Inst.Name : $"{s.Inst.Name} ({s.AsUser})") + suffix);

    /// <summary>
    /// A lost connection: reconnect in the same tab, behind an overlay, with a fresh tunnel for AWS servers. Retries every
    /// 5 s for half a minute, then every 15 s, for up to 10 minutes; at once when the network comes back; waits while
    /// there's no network. Windows keeps the session on the server, so it comes back as it was.
    /// </summary>
    private async Task AutoReconnectAsync(Session s)
    {
        if (s.Reconnecting) return;
        s.Reconnecting = true;
        Log.Write($"{s.Inst.Name}: connection lost (reason {s.DisconnectReason}, extended {s.ExtendedReason}); reconnecting");
        s.Page.Text = RdpTitle(s, " (reconnecting)");
        var until = DateTime.Now.AddMinutes(10);
        int attempt = 0;
        while (s.Reconnecting && _sessions.Contains(s) && DateTime.Now < until)
        {
            attempt++;
            if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
            {
                ShowOverlay(s, $"The connection to {s.Inst.Name} was lost. Waiting for the network to come back ...", waiting: true);
            }
            else
            {
                ShowOverlay(s, $"The connection to {s.Inst.Name} was lost. Reconnecting (attempt {attempt}) ...", waiting: true);
                try
                {
                    if (!s.Inst.IsSaved)
                    {
                        // a fresh Session Manager tunnel: the old one ended with the connection
                        try { s.Tunnel?.Dispose(); } catch { }
                        s.Tunnel = null;
                        s.Tunnel = await Tunnel.StartAsync(s.Inst, Profile);
                    }
                    s.DisconnectReason = -1;
                    s.ExtendedReason = 0;
                    await StartRdpAsync(s);
                    // give it up to 30 s; PollSessions finishes the reconnect as soon as it's connected
                    for (int i = 0; i < 60 && s.Reconnecting; i++)
                    {
                        await Task.Delay(500);
                        int state = 0;
                        try { state = s.Rdp.ConnectionState(); } catch { }
                        if (state == 1) { FinishReconnect(s); return; }
                        if (state == 0 && i >= 6) break;   // gave up connecting this time
                    }
                    if (!s.Reconnecting) return;
                    // ended on purpose while reconnecting (e.g. signed out elsewhere): stop
                    if (IsDeliberateEnd(s)) { s.Reconnecting = false; CloseSession(s); return; }
                }
                catch (Exception ex)
                {
                    Log.Write($"{s.Inst.Name}: reconnect attempt {attempt} failed: {ex.Message}");
                    if (ex is TunnelException && ex.Message.Contains("sign", StringComparison.OrdinalIgnoreCase))
                        ShowOverlay(s, $"The connection to {s.Inst.Name} was lost, and the AWS sign-in has expired. " +
                                       "It reconnects once you're signed in again.", waiting: true);
                }
            }
            // wait before the next try (or less, if the network comes back or Reconnect now is clicked)
            s.RetryNow = new TaskCompletionSource<bool>();
            await Task.WhenAny(s.RetryNow.Task, Task.Delay(attempt < 6 ? 5000 : 15000));
        }
        if (!s.Reconnecting || !_sessions.Contains(s)) return;
        s.Reconnecting = false;
        s.Page.Text = RdpTitle(s, " (disconnected)");
        ShowOverlay(s, $"Couldn't reconnect to {s.Inst.Name}.", waiting: false);
        SetStatus($"Couldn't reconnect to {s.Inst.Name}.");
    }

    private void FinishReconnect(Session s)
    {
        if (!s.Reconnecting) return;
        s.Reconnecting = false;
        s.RetryNow?.TrySetResult(true);
        if (s.Overlay != null) s.Overlay.Visible = false;
        s.Page.Text = RdpTitle(s);
        try { s.Rdp.SyncSize(); } catch { }
        Log.Write($"{s.Inst.Name}: reconnected");
        SetStatus($"Reconnected to {s.Inst.Name}");
    }

    /// <summary>The overlay over a Remote Desktop tab while it reconnects (or after it gave up): message, Reconnect now, Close tab.</summary>
    private void ShowOverlay(Session s, string message, bool waiting)
    {
        if (s.Overlay == null)
        {
            var panel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Current.Back };
            var label = new Label { AutoSize = true, UseMnemonic = false, ForeColor = Theme.Current.Text, Anchor = AnchorStyles.None, Margin = new Padding(0, 0, 0, 14), MaximumSize = new Size(LogicalToDeviceUnits(520), 0) };
            var retry = new Button { Text = "Reconnect now", Tag = "primary", AutoSize = true, MinimumSize = new Size(LogicalToDeviceUnits(130), LogicalToDeviceUnits(32)), Margin = new Padding(0, 0, 8, 0) };
            var close = new Button { Text = "Close tab", AutoSize = true, MinimumSize = new Size(LogicalToDeviceUnits(100), LogicalToDeviceUnits(32)) };
            retry.Click += (_, _) =>
            {
                if (s.Reconnecting) s.RetryNow?.TrySetResult(true);
                else _ = AutoReconnectAsync(s);   // it had given up: start again
            };
            close.Click += (_, _) => { s.Reconnecting = false; CloseSession(s); };
            var buttons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.None, WrapContents = false };
            buttons.Controls.AddRange(new Control[] { retry, close });
            var center = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            center.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            center.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            center.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            center.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            center.Controls.Add(label, 0, 1);
            center.Controls.Add(buttons, 0, 2);
            panel.Controls.Add(center);
            panel.Tag = label;
            s.Page.Controls.Add(panel);
            s.Overlay = panel;
            ApplyTheme();   // the overlay's buttons and colours, like the rest of the window
        }
        ((Label)s.Overlay.Tag).Text = message + (waiting ? "\n\nWindows keeps your session on the server, so you'll be back where you were." : "");
        s.Overlay.Visible = true;
        s.Overlay.BringToFront();
    }

    private void CloseSession(Session s)
    {
        _sessions.Remove(s);
        try { s.Rdp?.SafeDisconnect(); } catch { }
        try { s.Term?.Dispose(); } catch { }  // ends ssh
        try { s.Web?.Dispose(); } catch { }
        try { s.Vnc?.Dispose(); } catch { }
        if (s.Inst?.Saved != null) _adHocPasswords.Remove(s.Inst.Saved.Id);
        try { (s.Docked ? _dockTabs : _tabs).TabPages.Remove(s.Page); } catch { }
        try { s.Page.Dispose(); } catch { }
        s.Tunnel?.Dispose(); // saved connections have no tunnel
        UpdateMainTabsVisibility();
        if (_dockTabs.TabCount == 0) ShowDock(false); else UpdateDockTitle();
    }

    private void OnTabMouseUp(object sender, MouseEventArgs e)
    {
        var strip = sender as ThemedTabControl ?? _tabs;   // TabCount without the + slot
        for (int i = 0; i < strip.TabCount; i++)
        {
            if (!strip.GetTabRect(i).Contains(e.Location)) continue;
            var page = strip.TabPages[i];
            var session = _sessions.FirstOrDefault(x => x.Page == page);
            if (session == null)
            {
                if (e.Button == MouseButtons.Right && _placeholders.ContainsKey(page))
                {
                    _placeholderTarget = page;
                    Theme.ShowMenuOnScreen(_placeholderMenu, strip.PointToScreen(e.Location));
                }
                return;
            }

            if (e.Button == MouseButtons.Middle) CloseSession(session);
            else if (e.Button == MouseButtons.Right)
            {
                _menuTarget = session;
                Theme.ShowMenuOnScreen(_tabMenu, strip.PointToScreen(e.Location));
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
        UpdateTrayIcon();
        if (_settings.KeepSignedIn != keepBefore) await SetKeepSignedInAsync(_settings.KeepSignedIn);

        bool profileChanged = f.SelectedProfile.Length > 0 &&
                              !string.Equals(f.SelectedProfile, Profile, StringComparison.OrdinalIgnoreCase);
        foreach (var gone in f.RemovedProfiles) ForgetProfile(gone);
        if (profileChanged || !_cboProfile.Items.Contains(f.SelectedProfile)) await LoadProfilesAsync();
        if (profileChanged) SelectProfile(f.SelectedProfile);
        _settings.Profile = Profile;
        _settings.Save();

        if (f.UpdateCheckRequested) { await CheckForUpdatesAsync(manual: true); return; }
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

    /// <summary>
    /// Microsoft sign-in for a profile, with the built-in sign-in window (EntraSignIn). interactive = Sign in from the
    /// toolbar; otherwise a background renewal in a hidden window, which can't ask anything.
    /// </summary>
    private Task<(int Code, string Out, string Err)> MicrosoftSignInAsync(CancellationToken ct, string profile, bool interactive) =>
        EntraSignIn.RunAsync(this, profile, interactive, ct);

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
        foreach (var f in _forwards) { try { f.Tunnel?.Dispose(); } catch { } }
        _forwards.Clear();
        SingleInstance.ActivateRequested -= OnActivateRequested;
        _tray.Visible = false;
        _tray.Dispose();
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
    /// Renew the AWS keys in the background with the sign-in window's remembered Microsoft session: no window shown, no typing.
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
            try { r = await MicrosoftSignInAsync(cts.Token, profile, interactive: false); }
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
        foreach (var x in new ToolStripItem[] { _miConnectSsh, _smConnectSsh, _miConnectRdp, _smConnectRdp, _miConnectVnc })
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
        Theme.ShowMenuOnScreen(_columnMenu, screen);
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
            _keepHost = new ToolStripControlHost(_chkKeep) { Margin = new Padding(8, 3, 4, 2), AutoSize = false };
            // sized from its text in its current font, with room to spare, and again whenever the font (Settings >
            // Appearance) or text changes, so a wider font can't cut it short
            _chkKeep.FontChanged += (_, _) => SizeKeepSignedIn();
            _chkKeep.TextChanged += (_, _) => SizeKeepSignedIn();
            SizeKeepSignedIn();
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
        $"{i.Name}\nInstance ID: {i.Id}" + (string.IsNullOrEmpty(i.Region) ? "" : $"   Region: {i.Region}") +
        $"\n{i.Type}  {i.OsFull}\nStatus: {StatusOf(i)}" +
        $"\nEC2 state: {(i.State.Length > 0 ? i.State : "n/a")}   Systems Manager: {(i.Managed ? i.Ping : "not managed")}" +
        (i.Tags.Length > 0 ? "\n" + i.Tags.Replace("; ", "\n") : "");

    /// <summary>A server's tooltip card: name, a one-line summary, then details as label/value rows (status in colour).</summary>
    private TipContent TipFor(Instance i)
    {
        string status = StatusOf(i);
        var tip = new TipContent { Title = i.Name };
        if (i.IsSaved)
        {
            tip.Subtitle = $"Saved {(i.Saved.IsSsh ? "SSH" : "Remote Desktop")} connection";
            tip.Rows.Add(("Address", i.Saved.Address, null));
            tip.Rows.Add(("Status", status, StatusColor(status)));
            tip.Rows.Add(("Signs in as", CellText(i, "User"), null));
            tip.Note = i.Saved.Description?.Trim() ?? "";
            return tip;
        }
        tip.Subtitle = string.Join("  ·  ", new[] { i.Type, i.OsFull }.Where(x => !string.IsNullOrWhiteSpace(x)));
        tip.Rows.Add(("Status", status, StatusColor(status)));
        tip.Rows.Add(("Instance ID", i.Id, null));
        if (!string.IsNullOrEmpty(i.Region)) tip.Rows.Add(("Region", i.Region, null));
        tip.Rows.Add(("EC2 state", i.State.Length > 0 ? i.State : "n/a", StatusColor(i.State)));
        tip.Rows.Add(("Systems Manager", i.Managed ? i.Ping : "not managed", StatusColor(i.Managed ? i.Ping : "not managed")));
        foreach (var pair in i.Tags.Split("; ", StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0) tip.Tags.Add((pair[..eq], pair[(eq + 1)..]));
        }
        return tip;
    }

    /// <summary>Green for up, amber for changing, red for down; null (normal text) for anything else.</summary>
    private static Color? StatusColor(string text)
    {
        string t = (text ?? "").ToLowerInvariant();
        var p = Theme.Current;
        if (t.Contains("unreachable") || t.Contains("offline") || t.Contains("connectionlost") || t.Contains("connection lost") ||
            t.Contains("not managed") || t.Contains("terminated") || t.Contains("failed")) return p.Bad;
        if (t.Contains("starting") || t.Contains("stopping") || t.Contains("pending") || t.Contains("rebooting") ||
            t.Contains("shutting") || t.Contains("checking")) return p.Warn;
        if (t.Contains("online") || t.Contains("running") || t.Contains("reachable") || t.Contains("ready")) return p.Good;
        return null;
    }

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
