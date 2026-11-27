using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SwitchboardRM;

/// <summary>Settings window: a navigation sidebar on the left, one page at a time on the right.</summary>
internal sealed class SettingsForm : Form
{
    public enum Page { Profile, Credentials, Ssh, Appearance, About }

    /// <summary>The profile picked on the Profile page.</summary>
    public string SelectedProfile => (_cboProfile.SelectedItem as string ?? "").Trim();
    /// <summary>"Sign in now" was clicked: the main window signs in once this closes.</summary>
    public bool SignInRequested { get; private set; }
    /// <summary>About > Check for updates now: done by the main window once Settings closes.</summary>
    public bool UpdateCheckRequested { get; private set; }
    /// <summary>Profiles removed while Settings was open (the toolbar mustn't keep showing them).</summary>
    public List<string> RemovedProfiles { get; } = new();
    /// <summary>The regions to scan were changed.</summary>
    public bool RegionsChanged { get; private set; }

    // the width text wraps at, scaled to the screen's scaling (the text grows with it, so fixed pixels squeezed it);
    // static so the field initializers below can use it (the window opens on the primary screen's scaling)
    private static readonly int ContentWidth = AtScreenScale(560);

    private static int AtScreenScale(int pixelsAt100)
    {
        try
        {
            using var g = Graphics.FromHwnd(IntPtr.Zero);
            return (int)Math.Round(pixelsAt100 * g.DpiX / 96.0);
        }
        catch { return pixelsAt100; }
    }

    private readonly AppSettings _settings;
    private readonly Action _appearanceChanged; // saves and restyles every window

    private readonly Panel _sidebar = new() { Dock = DockStyle.Left, Width = 190, Tag = "sidebar", Padding = new Padding(0, 16, 0, 0) };
    private readonly ListBox _nav = new()
    {
        Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed,
        ItemHeight = 38, IntegralHeight = false,
    };
    private readonly Panel _content = new() { Dock = DockStyle.Fill, Padding = new Padding(28, 22, 16, 0) };
    private readonly Panel _footer = new() { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(16, 12, 16, 12) };
    private readonly List<Control> _pages = new();

    // profile page
    private readonly ComboBox _cboProfile = new() { Width = ContentWidth, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _lblProfileStatus = Muted("");
    private readonly FlowLayoutPanel _regionPanel = new()
    {
        FlowDirection = FlowDirection.LeftToRight, WrapContents = true, AutoSize = true,
        MaximumSize = new Size(ContentWidth, 0), MinimumSize = new Size(ContentWidth, 0), Margin = new Padding(0, 2, 0, 0),
    };
    private readonly List<(CheckBox Box, string Code)> _regionBoxes = new();
    private readonly Label _lblRegionCount = Muted("");
    private int _checkTicket;
    private readonly ThemedCheckBox _chkSignOut = new()
    { Text = "Sign out of AWS when Switchboard closes", AutoSize = true, Margin = new Padding(0, 10, 0, 2) };
    private readonly ThemedCheckBox _chkAutoRenew = new()
    { Text = "Keep me signed in to AWS", AutoSize = true, Margin = new Padding(0, 10, 0, 2) };

    // credentials page
    private readonly ThemedListView _lvCreds = new();
    private readonly TextBox _txtCredUser = new() { Width = 300 };
    private readonly TextBox _txtCredPass = new() { Width = 300, UseSystemPasswordChar = true };
    private readonly Label _lblDefault = Accent("");
    private readonly Label _lblCredMsg = Muted("");

    // appearance page
    // appearance page
    private readonly ComboBox _cboTheme = new() { Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _cboUiFont = new() { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _cboListFont = new() { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _cboListSize = new() { Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _cboTermFont = new() { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _cboTermSize = new() { Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _cboScheme = new() { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ThemedCheckBox _chkStylePs = new()
    { Text = "Also colour PowerShell to match (prompt, file lists, syntax)", AutoSize = true, Margin = new Padding(0, 10, 0, 2) };
    private readonly TerminalPreview _schemePreview = new() { Margin = new Padding(0, 8, 0, 4) };
    private readonly List<(AccentSwatch Swatch, string Value)> _swatches = new();
    private AccentSwatch _windowsSwatch, _customSwatch;
    private readonly Label _lblAccent = Muted("");
    private readonly ToolTip _appearanceTips = new();
    private bool _loadingAppearance;

    private readonly string _profileInUse;
    private readonly bool _awsSessionsOpen;

    public SettingsForm(AppSettings settings, string profile, IEnumerable<string> profiles, Action appearanceChanged, Page start,
        bool awsSessionsOpen = false)
    {
        _profileInUse = profile;
        _awsSessionsOpen = awsSessionsOpen;
        _settings = settings;
        _appearanceChanged = appearanceChanged;

        Text = "Settings";
        if (AppIcon.Icon != null) Icon = AppIcon.Icon;
        Font = new Font("Segoe UI", 9.5F);
        // big enough that every page's widest line fits without a sideways scroll bar, at any scaling
        ClientSize = new Size(LogicalToDeviceUnits(900), LogicalToDeviceUnits(680));
        MinimumSize = new Size(LogicalToDeviceUnits(760), LogicalToDeviceUnits(520));
        _sidebar.Width = LogicalToDeviceUnits(190);
        _content.Padding = new Padding(LogicalToDeviceUnits(28), LogicalToDeviceUnits(22), LogicalToDeviceUnits(16), 0);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        KeyPreview = true;

        foreach (var p in profiles) _cboProfile.Items.Add(p);
        if (!string.IsNullOrWhiteSpace(profile) && !_cboProfile.Items.Contains(profile)) _cboProfile.Items.Add(profile);
        _cboProfile.SelectedItem = profile;
        BuildRegionList(settings.ScanRegions);
        _chkAutoRenew.Checked = settings.KeepSignedIn;
        _chkSignOut.Checked = settings.SignOutOnClose;
        _chkSignOut.CheckedChanged += (_, _) => { _settings.SignOutOnClose = _chkSignOut.Checked; _settings.Save(); };
        _chkAutoRenew.CheckedChanged += (_, _) => { _settings.KeepSignedIn = _chkAutoRenew.Checked; _settings.Save(); };

        BuildChrome();
        _pages.Add(BuildProfilePage());
        _pages.Add(BuildCredentialsPage());
        _pages.Add(BuildSshPage());
        _pages.Add(BuildAppearancePage());
        _pages.Add(BuildAboutPage());
        foreach (var page in _pages) _content.Controls.Add(page);

        ApplyTheme();
        _nav.SelectedIndex = (int)start;
        LoadCredentials();
    }

    public void ApplyTheme()
    {
        Theme.Apply(this, Theme.Current.IsDark);
        _nav.Invalidate();
        _lvCreds.Invalidate();
        LoadAppearance(); // e.g. View > Dark mode was used while Settings was open
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await CheckProfileAsync();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        var regions = Regions();
        // compare as sets: the list may order the same regions differently from the saved setting
        if (regions.Count > 0 && !new HashSet<string>(regions, StringComparer.OrdinalIgnoreCase).SetEquals(_settings.ScanRegions))
        {
            _settings.ScanRegions = regions;
            RegionsChanged = true;
        }
        _settings.Save();
        base.OnFormClosing(e);
    }

    // ---------------------------------------------------------------- layout

    private void BuildChrome()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 64 };
        var headerFont = new Font("Segoe UI Semibold", 13F);
        header.Paint += (_, e) =>
        {
            var p = Theme.Current;
            e.Graphics.Clear(p.Raised);
            int x = 16;
            if (AppIcon.Logo is { } logo)
            {
                e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                e.Graphics.DrawImage(logo, new Rectangle(x, (header.Height - 34) / 2 - 4, 34, 34));
                x += 44;
            }
            TextRenderer.DrawText(e.Graphics, "Settings", headerFont, new Rectangle(x, 0, header.Width - x, header.Height - 8),
                p.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        };
        header.Disposed += (_, _) => headerFont.Dispose();
        _nav.Items.AddRange(new object[] { "AWS profile", "RDP credentials", "Terminal & SSH", "Appearance", "About" });
        _nav.DrawItem += DrawNavItem;
        _nav.SelectedIndexChanged += (_, _) => ShowPage(_nav.SelectedIndex);
        _sidebar.Controls.Add(_nav);
        _sidebar.Controls.Add(header);

        var close = Btn("Close", primary: true);
        close.Dock = DockStyle.Right;
        close.Click += (_, _) => Close();
        _footer.Controls.Add(close);

        Controls.Add(_content);
        Controls.Add(_footer);
        Controls.Add(_sidebar);
        _content.BringToFront(); // fills what the sidebar and footer leave
    }

    private void ShowPage(int index)
    {
        for (int i = 0; i < _pages.Count; i++) _pages[i].Visible = i == index;
    }

    private void DrawNavItem(object sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var p = Theme.Current;
        bool sel = (e.State & DrawItemState.Selected) != 0;
        using (var bg = new SolidBrush(sel ? p.Hover : p.Raised)) e.Graphics.FillRectangle(bg, e.Bounds);
        if (sel)
        {
            using var bar = new SolidBrush(p.Accent);
            e.Graphics.FillRectangle(bar, e.Bounds.Left + 6, e.Bounds.Top + 9, 3, e.Bounds.Height - 18);
        }
        TextRenderer.DrawText(e.Graphics, _nav.Items[e.Index].ToString(), _nav.Font,
            new Rectangle(e.Bounds.Left + 20, e.Bounds.Top, e.Bounds.Width - 20, e.Bounds.Height),
            sel ? p.Text : p.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    private static FlowLayoutPanel NewPage() => new()
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
        AutoScroll = true, Visible = false,
    };

    private static Label Title(string text) => new()
    {
        Text = text, UseMnemonic = false, AutoSize = true, Font = new Font("Segoe UI Semibold", 15F), Margin = new Padding(0, 0, 0, 6),
    };

    private static Label Heading(string text) => new()
    {
        Text = text, UseMnemonic = false, AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5F), Margin = new Padding(0, 18, 0, 3),
    };

    private static Label Muted(string text) => new()
    {
        Text = text, UseMnemonic = false, AutoSize = true, MaximumSize = new Size(ContentWidth, 0), Tag = "muted", Margin = new Padding(0, 0, 0, 8),
    };

    private static Label Accent(string text) => new()
    {
        Text = text, UseMnemonic = false, AutoSize = true, MaximumSize = new Size(ContentWidth, 0), Tag = "accent", Margin = new Padding(0, 4, 0, 8),
    };

    private static Label FieldLabel(string text) => new() { Text = text, UseMnemonic = false, AutoSize = true, Margin = new Padding(0, 8, 0, 3) };

    private static Button Btn(string text, bool primary = false) => new()
    {
        Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(96, 32),
        Padding = new Padding(10, 2, 10, 2), Margin = new Padding(0, 4, 8, 0), Tag = primary ? "primary" : null,
        Cursor = Cursors.Hand,
    };

    private Button[] _profileButtons = Array.Empty<Button>();

    /// <summary>
    /// The profile list with its buttons beside it. The buttons are made exactly as tall as the list box (whose
    /// height comes from its font and the display scaling) and share its top edge, so the row lines up.
    /// </summary>
    /// <summary>The profile list on its own line (the page's full width), with its buttons in a row underneath.</summary>
    private static FlowLayoutPanel ProfileRow(ComboBox combo, params Button[] buttons)
    {
        var block = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0),
        };
        combo.Margin = new Padding(0);
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = true,
            MaximumSize = new Size(ContentWidth, 0), Margin = new Padding(0, 8, 0, 0),
        };
        foreach (var b in buttons)
        {
            b.AutoSize = false;
            b.Padding = new Padding(8, 0, 8, 0);
            b.Margin = new Padding(0, 0, 6, 0);
        }
        row.Controls.AddRange(buttons);
        block.Controls.Add(combo);
        block.Controls.Add(row);
        return block;
    }

    /// <summary>Size the profile buttons to the list box's real height (known once the form's font is applied).</summary>
    private void SyncProfileButtons()
    {
        int h = _cboProfile.Height;
        foreach (var b in _profileButtons)
        {
            int w = TextRenderer.MeasureText(b.Text, b.Font).Width + b.Padding.Horizontal + 6;
            b.MinimumSize = Size.Empty;
            b.Size = new Size(Math.Max(w, 90), h);
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        SyncProfileButtons();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        SyncProfileButtons();
    }

    private ThemedCheckBox TrayCheckBox(string text, Func<bool> get, Action<bool> set)
    {
        var box = new ThemedCheckBox { Text = text, AutoSize = true, Checked = get(), Margin = new Padding(0, 8, 0, 2) };
        box.CheckedChanged += (_, _) => { set(box.Checked); _settings.Save(); };
        return box;
    }

    /// <summary>About: daily update checks on or off (shown greyed out when an administrator turned them off).</summary>
    private Control UpdatesCheckBox()
    {
        var box = new ThemedCheckBox
        {
            Text = "Check for updates automatically (once a day, from GitHub)", AutoSize = true, Margin = new Padding(0, 10, 0, 2),
            Checked = _settings.CheckForUpdates && !Updater.DisabledByPolicy, Enabled = !Updater.DisabledByPolicy,
        };
        box.CheckedChanged += (_, _) => { _settings.CheckForUpdates = box.Checked; _settings.Save(); };
        if (!Updater.DisabledByPolicy) return box;
        var panel = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        panel.Controls.Add(box);
        panel.Controls.Add(Muted("Turned off by your administrator."));
        return panel;
    }

    private static FlowLayoutPanel ButtonRow(params Control[] buttons)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0),
        };
        row.Controls.AddRange(buttons);
        return row;
    }

    // ---------------------------------------------------------------- AWS profile

    private Control BuildProfilePage()
    {
        var page = NewPage();

        var signIn = Btn("Sign in now", primary: true);
        signIn.Click += (_, _) => { SignInRequested = true; Close(); };

        var editProfile = Btn("Edit profile ...");
        editProfile.Click += async (_, _) =>
        {
            if (SelectedProfile.Length == 0) return;
            if (ProfileSetup.Edit(this, SelectedProfile)) await CheckProfileAsync();
        };

        _cboProfile.SelectedIndexChanged += async (_, _) => await CheckProfileAsync();
        var addProfile = Btn("Add profile ...");
        addProfile.Click += async (_, _) =>
        {
            string name = ProfileSetup.Add(this);
            if (name == null) return;
            if (!_cboProfile.Items.Contains(name)) _cboProfile.Items.Add(name);
            _cboProfile.SelectedItem = name; // becomes the toolbar's profile when Settings closes
            await CheckProfileAsync();
        };
        var removeProfile = Btn("Remove profile ...");
        removeProfile.Click += async (_, _) =>
        {
            string name = SelectedProfile;
            if (name.Length == 0) return;
            if (name.Equals(_profileInUse, StringComparison.OrdinalIgnoreCase) && _awsSessionsOpen)
            {
                MessageBox.Show(this, $"\"{name}\" is in use by open AWS tabs. Close them first, so their sessions can end cleanly.",
                    "Remove profile", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!ProfileSetup.Remove(this, name)) return;
            RemovedProfiles.Add(name);
            _cboProfile.Items.Remove(name);
            _cboProfile.SelectedIndex = _cboProfile.Items.Count > 0 ? 0 : -1;
            await CheckProfileAsync();
        };
        _profileButtons = new[] { editProfile, addProfile, removeProfile };

        page.Controls.AddRange(new Control[]
        {
            Title("AWS profile"),
            Muted("Switchboard uses an AWS profile saved on this computer (the same ones the AWS command line uses) " +
                  "to find your servers and open connections. The profile picked here is also the one shown in the main toolbar. " +
                  "Edit profile changes its sign-in details (for Microsoft sign-in: tenant, AWS app, account, region, " +
                  "session length and default role; for SSO: start URL, account and role); Add profile sets up a new " +
                  "one without changing the others. Both are saved in ~/.aws/config, where other AWS tools see them too."),

            Heading("Profile"),
            ProfileRow(_cboProfile, editProfile, addProfile, removeProfile),
            _lblProfileStatus,

            Heading("Sign in"),
            Muted("Opens the Microsoft sign-in page and gets temporary AWS access for this profile. " +
                  "Do this when the toolbar shows \"Not signed in\"; access usually lasts about an hour."),
            ButtonRow(signIn),
            _chkAutoRenew,
            Muted("On: Switchboard checks your AWS sign-in every few minutes and renews it in the background " +
                  "before it runs out (using your saved Microsoft session, with no window). If Microsoft wants you " +
                  "to sign in again, the status turns red. Off: nothing runs in the background; when you connect to " +
                  "or change a server while signed out, it signs you in at that moment. Also in the AWS menu."),
            _chkSignOut,
            Muted("Removes this profile's temporary AWS keys from ~/.aws/credentials when you close Switchboard (SSO " +
                  "profiles run \"aws sso logout\"), after ending your open sessions. Other tools using the same " +
                  "profile, such as the AWS CLI, are signed out too. AWS > Sign out does it at any time."),
            Muted("The refresh icon on the toolbar (or F5) only reloads the server list. It doesn't sign in again."),

            Heading("Regions"),
            Muted("Tick the AWS regions to look for servers in. Each extra region adds a little to how long the list " +
                  "takes to load, so only tick the ones you use. At least one must stay ticked."),
            ButtonRow(RegionButton("Clear all", false), RegionButton("Tick all", true)),
            _lblRegionCount,
            _regionPanel,
        });
        _lblProfileStatus.Margin = new Padding(0, 6, 0, 0);
        return page;
    }

    private async Task CheckProfileAsync()
    {
        string profile = SelectedProfile;
        int ticket = ++_checkTicket;
        if (profile.Length == 0)
        {
            _lblProfileStatus.Text = "Pick or type a profile name.";
            return;
        }

        _lblProfileStatus.Text = "Checking sign-in ...";
        bool known = await Task.Run(() => Aws.ListProfiles().Contains(profile, StringComparer.OrdinalIgnoreCase));
        string arn = known
            ? (await Aws.IdentityAsync(profile, Regions().FirstOrDefault() ?? "ca-central-1")).Arn
            : null;
        if (ticket != _checkTicket || IsDisposed) return; // a newer check started, or the window closed

        _lblProfileStatus.Text =
            !known ? $"\"{profile}\" isn't set up on this computer yet. Use \"Set up Microsoft sign-in\" below." :
            arn != null ? "Signed in as " + arn :
            "Not signed in. Use \"Sign in now\" below.";
    }

    /// <summary>The ticked regions, in list order.</summary>
    private List<string> Regions() => _regionBoxes.Where(r => r.Box.Checked).Select(r => r.Code).ToList();

    /// <summary>One heading per geography, two regions per row: "Central   ca-central-1".</summary>
    private void BuildRegionList(IEnumerable<string> selected)
    {
        var picked = new HashSet<string>(selected ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var regions = Aws.AllRegions();

        // keep anything already picked that this SDK version doesn't list (e.g. a brand-new region)
        foreach (var code in picked.Where(c => regions.All(r => !string.Equals(r.Code, c, StringComparison.OrdinalIgnoreCase))))
            regions.Add(new Aws.RegionInfo(code.ToLowerInvariant(), "Other", code.ToLowerInvariant()));

        int boxWidth = ContentWidth / 2 - 6;
        _regionPanel.SuspendLayout();
        foreach (var group in regions.GroupBy(r => r.Group))
        {
            var heading = new Label
            {
                Text = group.Key, AutoSize = true, Font = new Font("Segoe UI Semibold", 9.5F),
                Margin = new Padding(0, 10, 0, 2), Tag = "accent",
            };
            _regionPanel.Controls.Add(heading);
            _regionPanel.SetFlowBreak(heading, true);

            foreach (var r in group)
            {
                var box = new ThemedCheckBox
                {
                    Text = r.Place == r.Code ? r.Code : $"{r.Place}   {r.Code}",
                    Checked = picked.Contains(r.Code), AutoSize = false, AutoEllipsis = true,
                    Size = new Size(boxWidth, 24), Margin = new Padding(0, 0, 6, 0),
                };
                box.CheckedChanged += (_, _) => UpdateRegionCount();
                _regionBoxes.Add((box, r.Code));
                _regionPanel.Controls.Add(box);
            }
            // end the group's row so the next heading starts on its own line
            _regionPanel.SetFlowBreak(_regionPanel.Controls[_regionPanel.Controls.Count - 1], true);
        }
        _regionPanel.ResumeLayout();
        UpdateRegionCount();
    }

    private Button RegionButton(string text, bool tick)
    {
        var b = Btn(text);
        b.Click += (_, _) =>
        {
            foreach (var (box, _) in _regionBoxes) box.Checked = tick;
        };
        return b;
    }

    private void UpdateRegionCount()
    {
        var picked = Regions();
        _lblRegionCount.Text = picked.Count == 0
            ? "No regions ticked: the previous selection will be kept when you close Settings."
            : $"{picked.Count} ticked: {string.Join(", ", picked)}";
    }

    // ---------------------------------------------------------------- RDP credentials

    private Control BuildCredentialsPage()
    {
        var page = NewPage();

        _lvCreds.View = View.Details;
        _lvCreds.FullRowSelect = true;
        _lvCreds.MultiSelect = false;
        _lvCreds.HideSelection = false;
        _lvCreds.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _lvCreds.Size = new Size(ContentWidth, 150);
        _lvCreds.Margin = new Padding(0, 4, 0, 4);
        _lvCreds.Columns.Add("User", 250);
        _lvCreds.Columns.Add("Password", 120);
        _lvCreds.Columns.Add("", 100);
        _lvCreds.SelectedIndexChanged += (_, _) =>
        {
            if (_lvCreds.SelectedItems.Count == 0) return;
            _txtCredUser.Text = (string)_lvCreds.SelectedItems[0].Tag;
            _txtCredPass.Text = "";
        };

        _txtCredUser.PlaceholderText = @"DOMAIN\user or user";
        Theme.AddPasswordReveal(_txtCredPass);
        _txtCredUser.TextChanged += (_, _) =>
            _txtCredPass.PlaceholderText = CredentialStore.Exists(_txtCredUser.Text) ? "Leave blank to keep the saved password" : "";

        var save = Btn("Save", primary: true);
        save.Click += (_, _) => SaveCredential();
        var makeDefault = Btn("Use for connections");
        makeDefault.Click += (_, _) => SetDefault();
        var delete = Btn("Delete");
        delete.Click += (_, _) => DeleteCredential();

        page.Controls.AddRange(new Control[]
        {
            Title("RDP credentials"),
            Muted("Passwords are kept in Windows Credential Manager, encrypted with your Windows sign-in, and only work on this PC. " +
                  "Every connection signs in as the user marked \"Used for connections\". If that user has no saved password, " +
                  "Remote Desktop asks for one."),
            _lblDefault,
            _lvCreds,

            Heading("Add or change"),
            FieldLabel("User name"),
            _txtCredUser,
            FieldLabel("Password"),
            _txtCredPass,
            ButtonRow(save, makeDefault, delete),
            _lblCredMsg,
        });
        _lblCredMsg.Margin = new Padding(0, 10, 0, 0);
        return page;
    }

    private string DefaultUser => (_settings.Username ?? "").Trim();

    private void LoadCredentials(string select = null)
    {
        var saved = CredentialStore.ListUsers();
        var users = saved.ToList();
        if (DefaultUser.Length > 0 && !users.Contains(DefaultUser, StringComparer.OrdinalIgnoreCase))
            users.Insert(0, DefaultUser);

        _lvCreds.BeginUpdate();
        _lvCreds.Items.Clear();
        foreach (var u in users)
        {
            bool hasPassword = saved.Contains(u, StringComparer.OrdinalIgnoreCase);
            bool isDefault = string.Equals(u, DefaultUser, StringComparison.OrdinalIgnoreCase);
            var item = new ListViewItem(new[] { u, hasPassword ? "Saved" : "Ask each time", isDefault ? "Used for connections" : "" })
            { Tag = u };
            _lvCreds.Items.Add(item);
            if (select != null && string.Equals(u, select, StringComparison.OrdinalIgnoreCase)) item.Selected = true;
        }
        _lvCreds.EndUpdate();
        _lvCreds.FillLastColumn();

        _lblDefault.Text = DefaultUser.Length == 0
            ? "No user picked yet, so Remote Desktop will ask for a user name and password each time."
            : $"Connections sign in as {DefaultUser}.";
    }

    private void SaveCredential()
    {
        string user = _txtCredUser.Text.Trim();
        string password = _txtCredPass.Text;
        if (user.Length == 0) { CredMessage("Type a user name first."); return; }
        if (password.Length == 0 && !CredentialStore.Exists(user)) { CredMessage("Type the password to save."); return; }

        try
        {
            if (password.Length > 0) CredentialStore.Save(user, password);
        }
        catch (Exception ex)
        {
            Log.Write("Could not save password: " + ex.Message);
            CredMessage("Could not save: " + ex.Message);
            return;
        }
        _txtCredPass.Text = "";

        if (DefaultUser.Length == 0) { _settings.Username = user; _settings.Save(); } // first one saved becomes the default
        LoadCredentials(user);
        CredMessage($"Password for {user} saved.");
    }

    private void SetDefault()
    {
        string user = _txtCredUser.Text.Trim();
        if (user.Length == 0) { CredMessage("Pick a user in the list or type one."); return; }
        _settings.Username = user;
        _settings.Save();
        LoadCredentials(user);
        CredMessage(CredentialStore.Exists(user)
            ? $"Connections now sign in as {user}."
            : $"Connections now sign in as {user}. No password is saved, so Remote Desktop will ask for it.");
    }

    private void DeleteCredential()
    {
        string user = _txtCredUser.Text.Trim();
        if (user.Length == 0) { CredMessage("Pick a user in the list first."); return; }

        bool hasPassword = CredentialStore.Exists(user);
        bool isDefault = string.Equals(user, DefaultUser, StringComparison.OrdinalIgnoreCase);
        if (!hasPassword && !isDefault) { CredMessage($"Nothing is saved for {user}."); return; }

        string question = hasPassword ? $"Delete the saved password for {user}?" : $"Stop using {user} for connections?";
        if (MessageBox.Show(this, question, "RDP credentials", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        try
        {
            if (hasPassword) CredentialStore.Delete(user);
            else { _settings.Username = ""; _settings.Save(); }
        }
        catch (Exception ex)
        {
            Log.Write("Could not delete password: " + ex.Message);
            CredMessage("Could not delete: " + ex.Message);
            return;
        }
        _txtCredUser.Text = "";
        _txtCredPass.Text = "";
        LoadCredentials();
        CredMessage(hasPassword ? $"Saved password for {user} deleted." : $"{user} is no longer used for connections.");
    }

    private void CredMessage(string text) => _lblCredMsg.Text = text;

    // ---------------------------------------------------------------- SSH

    private readonly TextBox _txtSshUser = new() { Width = 300 };
    private readonly ComboBox _cboShell = new() { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ThemedCheckBox _chkDockNew = new()
    { Text = "Open new terminals docked", AutoSize = true, Margin = new Padding(0, 12, 0, 2) };
    private readonly TextBox _txtSshKey = new() { Width = 380 };
    private readonly TextBox _txtProjects = new() { Width = 380 };
    private readonly ThemedCheckBox _chkRestoreTabs = new()
    { Text = "Restore my tabs when Switchboard starts", AutoSize = true, Margin = new Padding(0, 8, 0, 2) };
    private readonly ThemedCheckBox _chkRestoreOutput = new()
    { Text = "Also restore terminal output (kept on this PC until Switchboard next closes)", AutoSize = true, Margin = new Padding(20, 4, 0, 2) };

    private Control BuildSshPage()
    {
        var page = NewPage();
        _txtSshUser.Text = _settings.SshUser ?? "";
        _txtSshUser.PlaceholderText = "Automatic: ec2-user, ubuntu, admin ... from the server's OS";
        _txtSshUser.TextChanged += (_, _) => { _settings.SshUser = _txtSshUser.Text.Trim(); _settings.Save(); };
        _txtSshKey.Text = _settings.SshKeyFile ?? "";
        _txtSshKey.PlaceholderText = "Optional; otherwise your ~/.ssh keys and the SSH agent";
        _txtSshKey.TextChanged += (_, _) => { _settings.SshKeyFile = _txtSshKey.Text.Trim(); _settings.Save(); };

        var browse = Btn("Browse ...");
        browse.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Private key for SSH",
                Filter = "Private keys (*.pem;id_*;*.key)|*.pem;id_*;*.key|All files (*.*)|*.*",
                InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
            };
            if (dlg.ShowDialog(this) == DialogResult.OK) _txtSshKey.Text = dlg.FileName;
        };
        var clear = Btn("Clear");
        clear.Click += (_, _) => _txtSshKey.Text = "";

        // default local shell
        var shells = Shells.Available();
        foreach (var sh in shells) _cboShell.Items.Add(sh.Name);
        var current = Shells.Default(_settings.DefaultShell);
        _cboShell.SelectedIndex = Math.Max(0, shells.FindIndex(x => x.Id == current.Id));
        _cboShell.SelectedIndexChanged += (_, _) =>
        {
            if (_cboShell.SelectedIndex < 0 || _cboShell.SelectedIndex >= shells.Count) return;
            _settings.DefaultShell = shells[_cboShell.SelectedIndex].Id;
            _settings.Save();
        };

        _chkDockNew.Checked = _settings.DockNewTerminals;
        _chkDockNew.CheckedChanged += (_, _) => { _settings.DockNewTerminals = _chkDockNew.Checked; _settings.Save(); };

        // Claude Code's projects folder
        _txtProjects.Text = _settings.ProjectsFolder ?? "";
        _txtProjects.PlaceholderText = "Not set: Claude Code starts in your user folder";
        _txtProjects.TextChanged += (_, _) => { _settings.ProjectsFolder = _txtProjects.Text.Trim(); _settings.Save(); };
        var browseProjects = Btn("Browse ...");
        browseProjects.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog
            {
                Description = "Folder that holds your projects",
                UseDescriptionForTitle = true,
                SelectedPath = Directory.Exists(_txtProjects.Text.Trim()) ? _txtProjects.Text.Trim()
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            if (dlg.ShowDialog(this) == DialogResult.OK) _txtProjects.Text = dlg.SelectedPath;
        };
        var clearProjects = Btn("Clear");
        clearProjects.Click += (_, _) => _txtProjects.Text = "";

        string ssh = Tools.Ssh;
        page.Controls.AddRange(new Control[]
        {
            Title("Terminal & SSH"),

            Heading("Local shells"),
            Muted("Shells on this PC open as tabs: the terminal button on the toolbar, File > New shell (Ctrl+T), or the + " +
                  "after the last tab. Each starts in your user folder with AWS_PROFILE set to the profile in use and " +
                  "the bundled AWS tools on its PATH. They run with Switchboard's own rights: for an administrator " +
                  "shell, run Switchboard as administrator."),
            FieldLabel("Default shell (Ctrl+T)"),
            _cboShell,
            _chkDockNew,
            Muted("New shells and SSH sessions open in the docked panel under the tabs instead of in a tab of their own. " +
                  "Move back to a tab still works on any of them."),
            Muted("Terminal font and text size, and restoring tabs at start-up, are under Appearance."),

            Heading("Claude Code projects folder"),
            Muted("The folder that holds your projects, one folder each. Claude Code starts there, and Claude Code in " +
                  "(next to Claude Code in the shell menus) lists its folders, so a session can start straight in a " +
                  "project, with that project's CLAUDE.md, memory and permissions. The tab is named after the project, " +
                  "and comes back in it next time. Leave blank to start in your user folder."),
            _txtProjects,
            ButtonRow(browseProjects, clearProjects),

            Heading("SSH"),
            Muted("Linux servers open in an SSH terminal tab. AWS servers are reached through the same Session Manager " +
                  "tunnel as Remote Desktop (to port 22), so they need an SSH server and your key, but no open " +
                  "inbound ports. Server > Connect with SSH / RDP picks the other one for any server."),
            Accent(ssh == null
                ? "The OpenSSH client isn't installed. Add it in Windows Settings > Apps > Optional features > OpenSSH Client."
                : "Using " + ssh),

            Heading("User name for AWS servers"),
            Muted("Leave blank to pick the usual one for each server's OS. Saved SSH connections have their own."),
            _txtSshUser,

            Heading("Private key"),
            Muted("A key file to use for every SSH connection, e.g. an EC2 key pair's .pem file. Leave blank to use " +
                  "your ~/.ssh keys and the Windows SSH agent, the same as typing ssh in a terminal."),
            _txtSshKey,
            ButtonRow(browse, clear),

            Heading("Host keys"),
            Muted("ssh asks before trusting a new server and remembers it in ~/.ssh/known_hosts. Servers reached " +
                  "through AWS are remembered by instance ID, so the tunnel's changing local port doesn't matter."),
        });
        return page;
    }

    // ---------------------------------------------------------------- appearance

    private Control BuildAppearancePage()
    {
        var page = NewPage();

        // theme
        _cboTheme.Items.AddRange(new object[] { "Light", "Dark", "Match Windows" });
        _cboTheme.SelectedIndexChanged += (_, _) =>
        {
            if (_loadingAppearance) return;
            _settings.ThemeMode = _cboTheme.SelectedIndex switch { 1 => "Dark", 2 => "System", _ => "Light" };
            _appearanceChanged();
        };

        // accent colour swatches: presets, the Windows accent colour, and a custom colour
        var swatchRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = true,
                                              MaximumSize = new Size(ContentWidth, 0), Margin = new Padding(0, 4, 0, 0) };
        for (int i = 0; i < Theme.AccentPresets.Length; i++)
        {
            var (name, color) = Theme.AccentPresets[i];
            string value = i == 0 ? "" : ColorMath.Hex(color);   // the default purple is stored as "" (default)
            var sw = new AccentSwatch { Swatch = color };
            _appearanceTips.SetToolTip(sw, i == 0 ? name + " (default)" : name);
            sw.Click += (_, _) => SetAccent(value);
            _swatches.Add((sw, value));
            swatchRow.Controls.Add(sw);
        }
        _windowsSwatch = new AccentSwatch { Swatch = Theme.WindowsAccent() ?? Color.Gray, Glyph = "W" };
        _appearanceTips.SetToolTip(_windowsSwatch, "Windows accent colour (follows Windows when it changes)");
        _windowsSwatch.Click += (_, _) => SetAccent("Windows");
        swatchRow.Controls.Add(_windowsSwatch);

        _customSwatch = new AccentSwatch { Swatch = Color.Empty, Glyph = "+" };
        _appearanceTips.SetToolTip(_customSwatch, "Pick any colour ...");
        _customSwatch.Click += (_, _) =>
        {
            using var dlg = new ColorDialog { FullOpen = true, AnyColor = true, Color = Theme.Accent };
            if (dlg.ShowDialog(this) == DialogResult.OK) SetAccent(ColorMath.Hex(dlg.Color));
        };
        swatchRow.Controls.Add(_customSwatch);

        // fonts: only ones installed on this PC are offered (plus the defaults, which have built-in fallbacks)
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var fonts = new System.Drawing.Text.InstalledFontCollection();
            foreach (var f in fonts.Families) installed.Add(f.Name);
        }
        catch { }
        FillFonts(_cboUiFont, Theme.DefaultUiFont, installed, new[]
        {
            "Segoe UI", "Segoe UI Variable Text", "Aptos", "Calibri", "Arial", "Verdana", "Tahoma", "Trebuchet MS",
            "Bahnschrift", "Inter", "Open Sans", "Roboto", "Noto Sans", "Source Sans Pro",
        });
        FillFonts(_cboTermFont, Theme.DefaultTerminalFont, installed, new[]
        {
            "Cascadia Mono", "Cascadia Code", "Consolas", "Lucida Console", "Courier New", "JetBrains Mono",
            "Fira Code", "Fira Mono", "Source Code Pro", "Hack", "IBM Plex Mono", "Iosevka", "Ubuntu Mono",
        });
        // the server list: the app font by default, or any text or monospace font (purely cosmetic)
        _cboListFont.Items.Add(new FontChoice("", true, Label: "Same as the app font (default)"));
        foreach (var f in new[]
                 {
                     "Segoe UI", "Segoe UI Variable Text", "Aptos", "Calibri", "Arial", "Verdana", "Tahoma", "Trebuchet MS",
                     "Bahnschrift", "Inter", "Open Sans", "Roboto", "Noto Sans", "Source Sans Pro",
                     "Cascadia Mono", "Cascadia Code", "Consolas", "Lucida Console", "Courier New", "JetBrains Mono",
                     "Fira Code", "Fira Mono", "Source Code Pro", "Hack", "IBM Plex Mono", "Iosevka", "Ubuntu Mono",
                 })
            if (installed.Contains(f)) _cboListFont.Items.Add(new FontChoice(f, false));
        foreach (var f in FontLoader.Families)
            if (!_cboListFont.Items.Cast<FontChoice>().Any(x => x.Family.Equals(f, StringComparison.OrdinalIgnoreCase)))
                _cboListFont.Items.Add(new FontChoice(f, false, FromFolder: true));
        _cboListFont.SelectedIndexChanged += (_, _) =>
        {
            if (_loadingAppearance || _cboListFont.SelectedItem is not FontChoice f) return;
            _settings.ListFont = f.IsDefault ? "" : f.Family;
            _appearanceChanged();
        };
        for (int pt = 9; pt <= 14; pt++) _cboListSize.Items.Add(pt);
        for (int pt = 10; pt <= 22; pt++) _cboTermSize.Items.Add(pt);

        _cboUiFont.SelectedIndexChanged += (_, _) =>
        {
            if (_loadingAppearance || _cboUiFont.SelectedItem is not FontChoice f) return;
            _settings.UiFont = f.IsDefault ? "" : f.Family;
            _appearanceChanged();
        };
        _cboListSize.SelectedIndexChanged += (_, _) =>
        {
            if (_loadingAppearance || _cboListSize.SelectedItem is not int pt) return;
            _settings.ListFontSize = pt;
            _appearanceChanged();
        };
        _cboTermFont.SelectedIndexChanged += (_, _) =>
        {
            if (_loadingAppearance || _cboTermFont.SelectedItem is not FontChoice f) return;
            _settings.TerminalFont = f.IsDefault ? "" : f.Family;
            _appearanceChanged();
        };
        _cboTermSize.SelectedIndexChanged += (_, _) =>
        {
            if (_loadingAppearance || _cboTermSize.SelectedItem is not int pt) return;
            _settings.TerminalFontSize = pt;
            _appearanceChanged();
        };

        // terminal colour schemes
        _cboScheme.Items.Add("Match app (default)");
        foreach (var sc in TerminalSchemes.All) _cboScheme.Items.Add(sc.Name);
        _cboScheme.SelectedIndexChanged += (_, _) =>
        {
            _schemePreview.Invalidate();
            if (_loadingAppearance) return;
            _settings.TerminalScheme = _cboScheme.SelectedIndex <= 0 ? "" : TerminalSchemes.All[_cboScheme.SelectedIndex - 1].Name;
            _appearanceChanged();
        };
        _chkStylePs.CheckedChanged += (_, _) =>
        {
            if (_loadingAppearance) return;
            _settings.StylePowerShell = _chkStylePs.Checked;
            _appearanceChanged();
        };

        var reset = Btn("Reset appearance");
        reset.Click += (_, _) =>
        {
            _settings.ThemeMode = "";
            _settings.DarkMode = false;
            _settings.AccentColor = "";
            _settings.UiFont = "";
            _settings.ListFont = "";
            _settings.ListFontSize = 10;
            _settings.TerminalFont = "";
            _settings.TerminalFontSize = 14;
            _settings.TerminalScheme = "";
            _settings.StylePowerShell = true;
            _appearanceChanged();
        };

        // tabs at start-up (they lived on Terminal & SSH before 3.24.0; the settings are the same)
        _chkRestoreTabs.Checked = _settings.RestoreTabs;
        _chkRestoreOutput.Checked = _settings.RestoreScrollback;
        _chkRestoreOutput.Enabled = _settings.RestoreTabs;
        _chkRestoreTabs.CheckedChanged += (_, _) =>
        {
            _settings.RestoreTabs = _chkRestoreTabs.Checked;
            _chkRestoreOutput.Enabled = _chkRestoreTabs.Checked;
            _settings.Save();
        };
        _chkRestoreOutput.CheckedChanged += (_, _) => { _settings.RestoreScrollback = _chkRestoreOutput.Checked; _settings.Save(); };

        page.Controls.AddRange(new Control[]
        {
            Title("Appearance"),
            Muted("Changes apply straight away, to every window."),

            Heading("Theme"),
            _cboTheme,
            Muted("Match Windows follows Windows' light or dark setting, even while Switchboard is open. " +
                  "View > Dark mode switches quickly between light and dark."),

            Heading("Accent colour"),
            swatchRow,
            _lblAccent,

            Heading("Fonts"),
            FieldLabel("App font"), _cboUiFont,
            FieldLabel("Server list font"), _cboListFont,
            FieldLabel("Server list text size"), _cboListSize,
            FieldLabel("Terminal font (SSH)"), _cboTermFont,
            FieldLabel("Terminal text size"), _cboTermSize,

            Heading("Window"),
            TrayCheckBox("Allow only one copy of Switchboard (starting it again brings this window to the front)",
                () => _settings.SingleInstance, v => _settings.SingleInstance = v),
            TrayCheckBox("Minimize to the system tray", () => _settings.MinimizeToTray, v => _settings.MinimizeToTray = v),
            TrayCheckBox("Close to the system tray (the close button keeps Switchboard running)", () => _settings.CloseToTray,
                v => { _settings.CloseToTray = v; _settings.CloseChoiceMade = true; }),   // chosen here: the close button doesn't ask
            Muted("Switchboard keeps running in the tray: sessions, tunnels and sign-in renewal carry on. Click its tray icon " +
                  "to open it again; Exit (in the tray icon's menu or the File menu) closes it."),

            Heading("Tabs at start-up"),
            _chkRestoreTabs,
            _chkRestoreOutput,
            Muted("Remote Desktop and SSH tabs reconnect (up to five at start-up; any more wait for a click), shells and " +
                  "Claude tabs reopen, and AWS tabs follow once you're signed in. Popped-out windows come back where they " +
                  "were and reconnect straight away. Passwords typed for Connect as aren't kept. Terminal output, when " +
                  "kept, shows dimmed above a fresh shell; what was running can't come back."),

            Heading("Terminal colours"),
            Muted("The colour scheme for SSH and shell tabs: background, text, and the 16 colours every console " +
                  "program uses. Changes open terminals straight away."),
            _cboScheme,
            _schemePreview,
            _chkStylePs,
            Muted("New PowerShell tabs start with the scheme's own colours for the prompt, dir listings, tables, errors " +
                  "and syntax, without editing your profile. A prompt from your profile is kept."),
            Muted("Lists the fonts installed on this PC, plus any in the fonts folder next to SwitchboardRM.exe " +
                  "(loaded when Switchboard starts; .ttf, .otf or .ttc)."),

            ButtonRow(reset),
        });
        LoadAppearance();
        return page;
    }

    /// <summary>A font in a font list: the family, shown as "Name (default)" for the default.</summary>
    private sealed record FontChoice(string Family, bool IsDefault, bool FromFolder = false, string Label = null)
    {
        public override string ToString() => Label ?? (IsDefault ? Family + " (default)" : FromFolder ? Family + "  (fonts folder)" : Family);
    }

    private static void FillFonts(ComboBox combo, string defaultFamily, HashSet<string> installed, string[] candidates)
    {
        combo.Items.Add(new FontChoice(defaultFamily, true));
        foreach (var f in candidates)
            if (!f.Equals(defaultFamily, StringComparison.OrdinalIgnoreCase) && installed.Contains(f))
                combo.Items.Add(new FontChoice(f, false));
        // everything in the fonts folder, whatever it is
        foreach (var f in FontLoader.Families)
            if (!combo.Items.Cast<FontChoice>().Any(x => x.Family.Equals(f, StringComparison.OrdinalIgnoreCase)))
                combo.Items.Add(new FontChoice(f, false, FromFolder: true));
    }

    private void SetAccent(string value)
    {
        _settings.AccentColor = value;
        _appearanceChanged();
    }

    /// <summary>Show the current appearance settings (without triggering changes).</summary>
    private void LoadAppearance()
    {
        if (_swatches.Count == 0) return; // page not built yet
        _loadingAppearance = true;
        try
        {
            _cboTheme.SelectedIndex = _settings.ThemeMode switch
            {
                "Dark" => 1, "System" => 2, "Light" => 0, _ => _settings.DarkMode ? 1 : 0,
            };

            // accent: which swatch is selected, and what it's called
            string accent = _settings.AccentColor ?? "";
            bool isWindows = accent.Equals("Windows", StringComparison.OrdinalIgnoreCase);
            var preset = _swatches.FirstOrDefault(x => string.Equals(x.Value, accent, StringComparison.OrdinalIgnoreCase));
            bool isCustom = !isWindows && preset.Swatch == null;
            foreach (var (sw, _) in _swatches) sw.SetSelected(sw == preset.Swatch);
            _windowsSwatch.Swatch = Theme.WindowsAccent() ?? Color.Gray;
            _windowsSwatch.SetSelected(isWindows);
            _customSwatch.Swatch = isCustom ? Theme.Accent : Color.Empty;
            _customSwatch.SetSelected(isCustom);
            _lblAccent.Text = isWindows ? "Windows accent colour (" + ColorMath.Hex(Theme.Accent) + ")"
                : isCustom ? "Custom colour " + ColorMath.Hex(Theme.Accent)
                : Theme.AccentPresets[_swatches.IndexOf(preset)].Name + (_swatches.IndexOf(preset) == 0 ? " (default)" : "");

            SelectFont(_cboUiFont, _settings.UiFont);
            SelectFont(_cboTermFont, _settings.TerminalFont);
            SelectFont(_cboListFont, _settings.ListFont);
            _cboListSize.SelectedItem = (int)Math.Round(_settings.ListFontSize is >= 9f and <= 14f ? _settings.ListFontSize : 10);
            _cboTermSize.SelectedItem = _settings.TerminalFontSize is >= 10 and <= 22 ? _settings.TerminalFontSize : 14;
            var scheme = TerminalSchemes.Find(_settings.TerminalScheme);
            _cboScheme.SelectedIndex = scheme == null ? 0 : Array.IndexOf(TerminalSchemes.All, scheme) + 1;
            _chkStylePs.Checked = _settings.StylePowerShell;
            _schemePreview.Invalidate();
        }
        finally { _loadingAppearance = false; }
    }

    private static void SelectFont(ComboBox combo, string family)
    {
        var items = combo.Items.Cast<FontChoice>().ToList();
        var match = string.IsNullOrWhiteSpace(family)
            ? items.FirstOrDefault(x => x.IsDefault)
            : items.FirstOrDefault(x => x.Family.Equals(family, StringComparison.OrdinalIgnoreCase));
        if (match == null && !string.IsNullOrWhiteSpace(family))
        {
            match = new FontChoice(family, false); // chosen earlier, no longer installed: still show it
            combo.Items.Add(match);
        }
        combo.SelectedItem = match ?? items.FirstOrDefault();
    }

    // ---------------------------------------------------------------- about

    private Control BuildAboutPage()
    {
        var page = NewPage();

        var logo = new PictureBox
        {
            Image = AppIcon.Logo, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(96, 96),
            Margin = new Padding(0, 0, 0, 10), Visible = AppIcon.Logo != null,
        };

        var copy = Btn("Copy version details");
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(AppInfo.Details()); _lblAboutMsg.Text = "Copied. Paste it into a support request or bug report."; }
            catch (Exception ex) { _lblAboutMsg.Text = "Couldn't copy: " + ex.Message; }
        };

        var tools = new Label
        {
            AutoSize = true, MaximumSize = new Size(ContentWidth, 0), Tag = "muted", Margin = new Padding(0, 0, 0, 4),
            Text = string.Join(Environment.NewLine, Tools.Describe().Select(t => $"{t.Name}:  {t.Detail}")),
        };

        page.Controls.AddRange(new Control[]
        {
            logo,
            Title(AppInfo.Name),
            Accent("Version " + AppInfo.Version),
            Muted("All your servers in one window. Remote Desktop, SSH, your shells and Claude sit side by side in tabs, " +
                  "whether a server lives in AWS or on your own network, and AWS servers connect securely without opening any ports."),
            Muted("Build new servers, resize them, back them up, patch them and run commands across several at once, all " +
                  "without opening the AWS console. And next time, your tabs and layout are right where you left them."),
            Muted(AppInfo.Copyright),

            Heading("Links"),
            Link("What's new in this version", ShowChangelog),
            UpdatesCheckBox(),
            Link("Check for updates now", () => { UpdateCheckRequested = true; Close(); }),
            Link("Open the log folder", () => OpenPath(Log.Dir)),
            Link("Open the settings folder", () => OpenPath(AppSettings.Folder)),
            Link("Open the bundled tools folder (includes their licences)", () =>
            {
                if (Directory.Exists(Tools.Dir)) OpenPath(Tools.Dir, create: false);
                else _lblAboutMsg.Text = "This copy has no bundled tools folder, so it uses the tools installed on this PC.";
            }),
            Link("AWS Session Manager documentation",
                () => OpenPath("https://docs.aws.amazon.com/systems-manager/latest/userguide/session-manager.html")),

            Heading("Bundled tools"),
            tools,
            Muted("Terminals: " + PseudoConsole.HostDescription + "."),

            ButtonRow(copy),
            _lblAboutMsg,
        });
        _lblAboutMsg.Margin = new Padding(0, 8, 0, 0);
        return page;
    }

    private readonly Label _lblAboutMsg = Muted("");

    private static LinkLabel Link(string text, Action onClick)
    {
        var link = new LinkLabel
        {
            Text = text, AutoSize = true, Margin = new Padding(0, 3, 0, 3),
            LinkBehavior = LinkBehavior.HoverUnderline, Cursor = Cursors.Hand,
        };
        link.LinkClicked += (_, _) => onClick();
        return link;
    }

    private void OpenPath(string target, bool create = true)
    {
        try
        {
            if (create && !target.StartsWith("http", StringComparison.OrdinalIgnoreCase)) Directory.CreateDirectory(target);
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) { _lblAboutMsg.Text = $"Couldn't open {target}: {ex.Message}"; }
    }

    /// <summary>The changelog in a small themed reading window.</summary>
    private void ShowChangelog()
    {
        using var f = new NotesForm("What's new", $"Switchboard {AppInfo.Version}: every version's changes, newest first",
            AppInfo.Changelog, $"https://github.com/{AppInfo.Repo}/releases");
        f.ShowDialog(this);
    }
}

/// <summary>A round colour swatch for choosing the accent. A ring marks the selected one.</summary>
internal sealed class AccentSwatch : Control
{
    public Color Swatch;   // Color.Empty = no colour yet (drawn as an outline)
    public string Glyph;   // optional letter on top, e.g. "W" for the Windows accent, "+" for custom
    private bool _selected, _hover;

    public AccentSwatch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Size = new Size(36, 36);
        Margin = new Padding(0, 0, 6, 6);
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    public void SetSelected(bool on) { _selected = on; Invalidate(); }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var p = Theme.Current;
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var outer = new Rectangle(1, 1, Width - 3, Height - 3);
        if (_selected || _hover || Focused)
        {
            using var ring = new Pen(_selected ? p.Text : p.Muted, _selected ? 2.2f : 1.4f);
            g.DrawEllipse(ring, outer);
        }

        var inner = Rectangle.Inflate(outer, -5, -5);
        if (Swatch.IsEmpty)
        {
            using var fill = new SolidBrush(p.Surface);
            using var edge = new Pen(p.Border, 1.4f);
            g.FillEllipse(fill, inner);
            g.DrawEllipse(edge, inner);
        }
        else
        {
            using var fill = new SolidBrush(Swatch);
            g.FillEllipse(fill, inner);
        }

        if (!string.IsNullOrEmpty(Glyph))
        {
            var color = Swatch.IsEmpty ? p.Text : ColorMath.TextOn(Swatch);
            using var font = Theme.CreateFont(Theme.UiFont, 9.5f, FontStyle.Bold);
            TextRenderer.DrawText(g, Glyph, font, inner, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}

/// <summary>A small sample terminal in the chosen colour scheme: prompt, command, dir listing, error and the 16 colours.</summary>
internal sealed class TerminalPreview : Control
{
    public TerminalPreview()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        Size = new Size(460, 150);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var s = Theme.TerminalScheme ?? TerminalSchemes.MatchApp();
        Color C(string hex) => ColorTranslator.FromHtml(hex);
        var g = e.Graphics;
        g.Clear(Theme.Current.Back);

        var box = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var bg = new SolidBrush(C(s.Background))) g.FillRectangle(bg, box);
        using (var edge = new Pen(Theme.Current.Border)) g.DrawRectangle(edge, box);

        using var font = Theme.CreateFont(Theme.TerminalFont, 9.5f);
        using var boldFont = Theme.CreateFont(Theme.TerminalFont, 9.5f, FontStyle.Bold);
        int lineH = font.Height + 2, x0 = 10, y = 8;
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

        void Line(params (string Text, string Color, bool Bold)[] parts)
        {
            int x = x0;
            foreach (var (text, color, bold) in parts)
            {
                var f = bold ? boldFont : font;
                TextRenderer.DrawText(g, text, f, new Point(x, y), C(color), flags);
                if (Theme.IsFauxBold(f)) TextRenderer.DrawText(g, text, f, new Point(x + 1, y), C(color), flags);   // no bold file: fake it
                x += TextRenderer.MeasureText(g, text, f, Size.Empty, flags).Width;
            }
            y += lineH;
        }

        Line(("PS ", s.Role("PromptLabel"), false), (@"C:\Users\you", s.Role("PromptPath"), false), (" > ", s.Role("PromptArrow"), false),
             ("Get-ChildItem", s.Role("Command"), false), (" -Path", s.Role("Parameter"), false), (" '.\\Projects'", s.Role("String"), false));
        Line(("Mode    LastWriteTime   Name", s.Role("TableHeader"), true));
        Line(("d----   2026-11-05      ", s.Foreground, false), ("Switchboard", s.Role("Directory"), true));
        Line(("-a---   2026-11-04      ", s.Foreground, false), ("deploy.ps1", s.Role("Executable"), false));
        Line(("Write-Error: ", s.Role("Error"), false), ("the server didn't answer", s.Role("Error"), false));

        // the 16 ANSI colours
        int sw = Math.Max(8, (Width - 2 * x0) / 16 - 2);
        for (int i = 0; i < 16; i++)
        {
            using var b = new SolidBrush(C(s.Ansi[i]));
            g.FillRectangle(b, x0 + i * (sw + 2), Height - sw - 10, sw, sw);
        }
    }
}
