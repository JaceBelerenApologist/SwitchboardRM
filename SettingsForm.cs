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
    /// <summary>Profiles removed while Settings was open (the toolbar mustn't keep showing them).</summary>
    public List<string> RemovedProfiles { get; } = new();
    /// <summary>The regions to scan were changed.</summary>
    public bool RegionsChanged { get; private set; }

    private const int ContentWidth = 500;

    private readonly AppSettings _settings;
    private readonly Action<bool> _setDark;

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
    private readonly ComboBox _cboProfile = new() { Width = 280, DropDownStyle = ComboBoxStyle.DropDownList };
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
    private readonly ThemedCheckBox _chkDark = new() { Text = "Dark mode", AutoSize = true, Margin = new Padding(0, 8, 0, 4) };

    private readonly string _profileInUse;
    private readonly bool _awsSessionsOpen;

    public SettingsForm(AppSettings settings, string profile, IEnumerable<string> profiles, Action<bool> setDark, Page start,
        bool awsSessionsOpen = false)
    {
        _profileInUse = profile;
        _awsSessionsOpen = awsSessionsOpen;
        _settings = settings;
        _setDark = setDark;

        Text = "Settings";
        if (AppIcon.Icon != null) Icon = AppIcon.Icon;
        Font = new Font("Segoe UI", 9.5F);
        ClientSize = new Size(780, 600);
        MinimumSize = new Size(640, 480);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        KeyPreview = true;

        foreach (var p in profiles) _cboProfile.Items.Add(p);
        if (!string.IsNullOrWhiteSpace(profile) && !_cboProfile.Items.Contains(profile)) _cboProfile.Items.Add(profile);
        _cboProfile.SelectedItem = profile;
        BuildRegionList(settings.ScanRegions);
        _chkDark.Checked = settings.DarkMode;
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
        if (_chkDark.Checked != Theme.Current.IsDark) _chkDark.Checked = Theme.Current.IsDark;
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
                p.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        };
        header.Disposed += (_, _) => headerFont.Dispose();
        _nav.Items.AddRange(new object[] { "AWS profile", "RDP credentials", "SSH", "Appearance", "About" });
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
            sel ? p.Text : p.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }

    private static FlowLayoutPanel NewPage() => new()
    {
        Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false,
        AutoScroll = true, Visible = false,
    };

    private static Label Title(string text) => new()
    {
        Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 15F), Margin = new Padding(0, 0, 0, 6),
    };

    private static Label Heading(string text) => new()
    {
        Text = text, AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5F), Margin = new Padding(0, 18, 0, 3),
    };

    private static Label Muted(string text) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(ContentWidth, 0), Tag = "muted", Margin = new Padding(0, 0, 0, 8),
    };

    private static Label Accent(string text) => new()
    {
        Text = text, AutoSize = true, MaximumSize = new Size(ContentWidth, 0), Tag = "accent", Margin = new Padding(0, 4, 0, 8),
    };

    private static Label FieldLabel(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 8, 0, 3) };

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
    private static FlowLayoutPanel ProfileRow(ComboBox combo, params Button[] buttons)
    {
        var row = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0, 4, 0, 0),
        };
        combo.Margin = new Padding(0, 0, 8, 0);
        foreach (var b in buttons)
        {
            b.AutoSize = false;
            b.Padding = new Padding(8, 0, 8, 0);
            b.Margin = new Padding(0, 0, 6, 0);
        }
        row.Controls.Add(combo);
        row.Controls.AddRange(buttons);
        return row;
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

        var setup = Btn("Set up Microsoft sign-in...");
        setup.Click += (_, _) => SignInSetup.Run(SelectedProfile, this);

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
        _profileButtons = new[] { addProfile, removeProfile };

        page.Controls.AddRange(new Control[]
        {
            Title("AWS profile"),
            Muted("Switchboard uses an AWS profile saved on this computer (the same ones the AWS command line uses) " +
                  "to find your servers and open connections. The profile picked here is also the one shown in the main toolbar. " +
                  "Add profile sets up a new one without changing the others."),

            Heading("Profile"),
            ProfileRow(_cboProfile, addProfile, removeProfile),
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

            Heading("First-time setup"),
            Muted("Sets up the Microsoft sign-in helper (aws-azure-login) with your organisation's sign-in details. " +
                  (Tools.HasBundledAzureLogin
                      ? "The helper comes with Switchboard, so nothing needs installing. "
                      : "The helper isn't bundled with this copy of Switchboard, so this installs it (needs Node.js). ") +
                  "You only need this once per computer, or when adding a new profile."),
            ButtonRow(setup),

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

        const int boxWidth = ContentWidth / 2 - 6;
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
    private readonly TextBox _txtSshKey = new() { Width = 380 };

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

        string ssh = Tools.Ssh;
        page.Controls.AddRange(new Control[]
        {
            Title("SSH"),
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
        _chkDark.CheckedChanged += (_, _) =>
        {
            if (_chkDark.Checked != Theme.Current.IsDark) _setDark(_chkDark.Checked); // applies to every window
        };
        page.Controls.AddRange(new Control[]
        {
            Title("Appearance"),
            Muted("Both looks use the purple accent."),
            _chkDark,
            Muted("You can also switch it from View > Dark mode."),
        });
        return page;
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
            Muted("Remote Desktop and SSH sessions in tabs: to your AWS servers through Systems Manager (no console, " +
                  "no open ports), and to any other computer directly. Plus start, stop and reboot for EC2."),
            Muted(AppInfo.Copyright),

            Heading("Links"),
            Link("What's new in this version", ShowChangelog),
            Link("Open the log folder", () => OpenPath(Log.Dir)),
            Link("Open the settings folder", () => OpenPath(AppSettings.Folder)),
            Link("Open the bundled tools folder (includes their licences)", () =>
            {
                if (Directory.Exists(Tools.Dir)) OpenPath(Tools.Dir, create: false);
                else _lblAboutMsg.Text = "This copy has no bundled tools folder, so it uses the tools installed on this PC.";
            }),
            Link("AWS Session Manager documentation",
                () => OpenPath("https://docs.aws.amazon.com/systems-manager/latest/userguide/session-manager.html")),
            Link("aws-azure-login on GitHub", () => OpenPath("https://github.com/aws-azure-login/aws-azure-login")),

            Heading("Bundled tools"),
            tools,

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
        using var f = new Form
        {
            Text = "What's new", Icon = AppIcon.Icon, ShowInTaskbar = false, MinimizeBox = false, MaximizeBox = false,
            StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(620, 480), Font = Font,
            KeyPreview = true,
        };
        var text = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None, Text = AppInfo.Changelog.Replace("\r\n", "\n").Replace("\n", "\r\n"),
        };
        var holder = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 14, 8, 8) };
        holder.Controls.Add(text);
        f.Controls.Add(holder);
        f.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) f.Close(); };
        f.HandleCreated += (_, _) => Theme.ApplyTitleBar(f);
        Theme.Apply(f, Theme.Current.IsDark);
        text.BorderStyle = BorderStyle.None;
        text.BackColor = Theme.Current.Back;
        f.Shown += (_, _) => { text.SelectionStart = 0; text.SelectionLength = 0; };
        f.ShowDialog(this);
    }
}
