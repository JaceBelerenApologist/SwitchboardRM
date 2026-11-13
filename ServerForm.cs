using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace SwitchboardRM;

/// <summary>
/// The Server window (AWS server > Manage server): pages on the left, the chosen page on the right.
///   Overview: details, disks, status checks, scheduled events
///   Screen and log: console screenshot and system log (work even when the server can't be reached)
///   Name and tags: rename, edit tags
///   Protection: termination/stop protection, OS shutdown behaviour, detailed monitoring
/// More pages (instance type, disks, network, images, Run Command ...) join in later versions.
/// </summary>
internal sealed class ServerForm : Form
{
    private const int PageWidth = 620;

    private readonly string _profile, _region, _instanceId;
    private readonly AppSettings _settings;
    private readonly List<CommandTarget> _peers;
    private readonly CancellationTokenSource _cts = new();
    private ServerDetails _details;

    private readonly FlowLayoutPanel _nav = new() { Dock = DockStyle.Left, Width = 190, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12, 14, 8, 8) };
    private readonly Panel _host = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(20, 14, 20, 14) };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(20, 8, 20, 0), UseMnemonic = false };
    private readonly List<(Button Button, Func<Control> Build)> _pages = new();
    private readonly Dictionary<int, Control> _built = new();
    private int _current = -1;

    // overview
    private readonly TextBox _overview = Readout(PageWidth, 470);
    // screen and log
    private readonly PictureBox _screen = new() { Width = PageWidth, Height = 360, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle };
    private readonly TextBox _log = Readout(PageWidth, 300);
    // name and tags
    private readonly TextBox _name = new() { Width = 360 };
    private readonly TextBox _tags = new() { Width = PageWidth, Height = 200, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
    // protection
    private readonly ThemedCheckBox _termination = new() { Text = "Termination protection (can't be terminated until this is turned off)", AutoSize = true };
    private readonly ThemedCheckBox _stopProtection = new() { Text = "Stop protection (can't be stopped until this is turned off)", AutoSize = true };
    private readonly ComboBox _shutdown = new() { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ThemedCheckBox _monitoring = new() { Text = "Detailed monitoring (1-minute CloudWatch metrics, charged)", AutoSize = true };
    // instance type
    private readonly Label _currentType = new() { AutoSize = true, UseMnemonic = false, MaximumSize = new Size(PageWidth, 0) };
    private readonly ComboBox _newType = new() { Width = 460, DropDownStyle = ComboBoxStyle.DropDown, DropDownWidth = 600, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
    private readonly Label _typeDiff = new() { AutoSize = true, UseMnemonic = false, Tag = "muted", Margin = new Padding(0, 4, 0, 0) };
    private List<TypeChoice> _types;
    private string _typesFor = "";
    // disks
    private readonly ListView _disks = new() { Width = PageWidth, Height = 150, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    private readonly NumericUpDown _growTo = new() { Minimum = 1, Maximum = 65536, Width = 90 };
    private readonly ComboBox _growType = new() { Width = 130, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ThemedCheckBox _extend = new() { Text = "Then extend the partition and file system inside the OS (through Systems Manager)", AutoSize = true, Checked = true, Margin = new Padding(0, 6, 0, 0) };
    private readonly NumericUpDown _addSize = new() { Minimum = 1, Maximum = 16384, Value = 100, Width = 90 };
    private readonly ComboBox _addType = new() { Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ThemedCheckBox _addEncrypted = new() { Text = "Encrypted", AutoSize = true, Checked = true, Margin = new Padding(12, 6, 0, 0) };
    private readonly ThemedCheckBox _addFormat = new() { Text = "Then initialize and format it in Windows (GPT, NTFS, next free drive letter)", AutoSize = true, Checked = true, Margin = new Padding(0, 6, 0, 0) };
    private readonly TextBox _diskOutput = Readout(PageWidth, 90);
    private bool _busy;
    // network
    private readonly ComboBox _eni = new() { Width = PageWidth, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckedListBox _sgs = new() { Width = PageWidth, Height = 120, CheckOnClick = true, IntegralHeight = false };
    private readonly ThemedCheckBox _sourceDest = new() { Text = "Source/destination check (turn off only for NAT, firewall or routing servers)", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    private readonly ListBox _secondary = new() { Width = 300, Height = 90, IntegralHeight = false };
    private readonly TextBox _newIp = new() { Width = 180, PlaceholderText = "Automatic" };
    private readonly ComboBox _iamProfile = new() { Width = 460, DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = 560 };
    private readonly Label _iamWarning = new() { AutoSize = true, UseMnemonic = false, Tag = "accent", MaximumSize = new Size(PageWidth, 0), Margin = new Padding(0, 4, 0, 0) };
    private List<NamedChoice> _vpcGroups;
    private bool _profilesLoaded;
    // images and snapshots
    private readonly TextBox _imageName = new() { Width = 460 };
    private readonly TextBox _imageDescription = new() { Width = 460, PlaceholderText = "Optional" };
    private readonly ThemedCheckBox _imageReboot = new() { Text = "Reboot the server first, for a consistent image (recommended; a short outage)", AutoSize = true, Checked = true, Margin = new Padding(0, 6, 0, 0) };
    private readonly ListView _images = new() { Width = PageWidth, Height = 130, View = View.Details, FullRowSelect = true, MultiSelect = false };
    private readonly ListView _snapshots = new() { Width = PageWidth, Height = 150, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    private readonly TextBox _backupOutput = Readout(PageWidth, 60);
    // run command
    private readonly ComboBox _recent = new() { Width = PageWidth, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _script = new() { Width = PageWidth, Height = 150, Multiline = true, ScrollBars = ScrollBars.Both, AcceptsReturn = true, AcceptsTab = true, WordWrap = false };
    private readonly CheckedListBox _alsoOn = new() { Width = PageWidth, Height = 100, CheckOnClick = true, IntegralHeight = false };
    private readonly TextBox _runOutput = Readout(PageWidth, 220);
    private readonly Label _scriptKind = new() { AutoSize = true, UseMnemonic = false, Tag = "muted", Margin = new Padding(0, 0, 0, 4) };
    // patches and agent
    private readonly TextBox _patchInfo = Readout(PageWidth, 230);
    private readonly ThemedCheckBox _rebootIfNeeded = new() { Text = "Reboot if the patches need it", AutoSize = true, Margin = new Padding(12, 6, 0, 0) };
    private readonly TextBox _appFilter = new() { Width = 260, PlaceholderText = "Filter" };
    private readonly TextBox _apps = Readout(PageWidth, 180);
    private List<string> _appList;
    private bool _patchesLoaded;

    /// <summary>True once something was changed (name, tags ...), so the server list should refresh.</summary>
    public bool Changed { get; private set; }
    /// <summary>The server was terminated from this window (Protection page).</summary>
    public bool Terminated { get; private set; }

    /// <param name="peers">Other online servers in the same region (Run command's "Also run on").</param>
    public ServerForm(string profile, string region, string instanceId, string name, AppSettings settings, IEnumerable<CommandTarget> peers)
    {
        _settings = settings;
        _peers = peers?.Where(p => p.Id != instanceId).ToList() ?? new List<CommandTarget>();
        _profile = profile;
        _region = region;
        _instanceId = instanceId;
        Text = $"Server: {name} ({instanceId})";
        if (AppIcon.Icon != null) Icon = AppIcon.Icon;
        Font = new Font("Segoe UI", 9.5F);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        ClientSize = new Size(LogicalToDeviceUnits(880), LogicalToDeviceUnits(640));
        MinimumSize = new Size(LogicalToDeviceUnits(760), LogicalToDeviceUnits(480));

        _shutdown.Items.AddRange(new object[] { "Stop", "Terminate" });
        _growType.Items.AddRange(new object[] { "(keep its type)", "gp3", "gp2", "io1", "io2", "st1", "sc1" });
        _growType.SelectedIndex = 0;
        _addType.Items.AddRange(new object[] { "gp3", "gp2", "io1", "io2", "st1", "sc1" });
        _addType.SelectedIndex = 0;
        _disks.Columns.Add("Device", 90);
        _disks.Columns.Add("Size", 70);
        _disks.Columns.Add("Type", 60);
        _disks.Columns.Add("IOPS", 60);
        _disks.Columns.Add("MB/s", 60);
        _disks.Columns.Add("Encrypted", 76);
        _disks.Columns.Add("Volume", 180);
        _disks.SelectedIndexChanged += (_, _) => DiskSelected();
        _newType.SelectedIndexChanged += (_, _) => UpdateTypeDiff();
        _newType.TextChanged += (_, _) => UpdateTypeDiff();
        _diskOutput.Font = _backupOutput.Font = _runOutput.Font = _patchInfo.Font = _apps.Font = new Font("Consolas", 9F);
        _script.Font = new Font("Consolas", 9.5F);
        _runOutput.WordWrap = false;
        _runOutput.ScrollBars = ScrollBars.Both;
        _recent.SelectedIndexChanged += (_, _) => { if (_recent.SelectedIndex > 0) _script.Text = _settings.RecentCommands[_recent.SelectedIndex - 1].Replace("\n", "\r\n"); };
        _appFilter.TextChanged += (_, _) => ShowApps();
        _eni.SelectedIndexChanged += (_, _) => ShowInterface();
        _iamProfile.SelectedIndexChanged += (_, _) => UpdateIamWarning();
        foreach (var (title, width) in new[] { ("Name", 250), ("Image", 170), ("Created", 120), ("State", 80) }) _images.Columns.Add(title, width);
        foreach (var (title, width) in new[] { ("Disk", 80), ("Snapshot", 190), ("Size", 60), ("Taken", 120), ("State", 80), ("Description", 220) }) _snapshots.Columns.Add(title, width);
        _log.Font = _overview.Font = new Font("Consolas", 9.5F);
        _log.WordWrap = false;
        _log.ScrollBars = ScrollBars.Both;

        AddPage("Overview", BuildOverview);
        AddPage("Screen and log", BuildScreenAndLog);
        AddPage("Name and tags", BuildTags);
        AddPage("Protection", BuildProtection);
        AddPage("Instance type", BuildInstanceType);
        AddPage("Disks", BuildDisks);
        AddPage("Network", BuildNetwork);
        AddPage("Images and snapshots", BuildBackups);
        AddPage("Run command", BuildRunCommand);
        AddPage("Patches and agent", BuildPatches);

        var close = new Button { Text = "Close", AutoSize = true, MinimumSize = new Size(96, 32), DialogResult = DialogResult.Cancel };
        CancelButton = close;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 52, Padding = new Padding(16, 8, 16, 8) };
        buttons.Controls.Add(close);
        Controls.Add(_host);
        Controls.Add(_nav);
        Controls.Add(_status);
        Controls.Add(buttons);
        Theme.Apply(this, Theme.Current.IsDark);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ShowPage(0);
        await LoadDetailsAsync();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cts.Cancel();
        base.OnFormClosed(e);
    }

    // ------------------------------------------------------------------ pages

    private void AddPage(string title, Func<Control> build)
    {
        int index = _pages.Count;
        var button = new Button
        {
            Text = title, Width = 168, Height = 34, TextAlign = ContentAlignment.MiddleLeft, FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 0, 0, 4), Padding = new Padding(8, 0, 0, 0),
        };
        button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) => ShowPage(index);
        _nav.Controls.Add(button);
        _pages.Add((button, build));
    }

    private async void ShowPage(int index)
    {
        ShowPageNow(index);
        if (index == 4) await LoadTypesAsync();
        if (index == 6) await LoadNetworkAsync();
        if (index == 7) await LoadBackupsAsync();
        if (index == 8) FillRunTargets();
        if (index == 9) await LoadPatchesAsync();
    }

    private void ShowPageNow(int index)
    {
        if (!_built.TryGetValue(index, out var page)) _built[index] = page = _pages[index].Build();
        _current = index;
        _host.SuspendLayout();
        _host.Controls.Clear();
        _host.Controls.Add(page);
        // the theme colours only the controls in the window, and only this page is: theme it as it goes in
        Theme.Apply(this, Theme.Current.IsDark);
        for (int i = 0; i < _pages.Count; i++)
        {
            var b = _pages[i].Button;
            b.BackColor = i == index ? Theme.Current.Hover : Theme.Current.Back;
            b.ForeColor = Theme.Current.Text;
            // the app font (Settings > Appearance), set after the theme: a font built from the window's own base font
            // (Segoe UI) would undo the theme's choice for these buttons only
            b.Font = Theme.CreateFont(Theme.UiFont, Font.Size, i == index ? FontStyle.Bold : FontStyle.Regular);
        }
        _host.ResumeLayout();
        _host.AutoScrollPosition = new Point(0, 0);
    }

    private Control BuildOverview()
    {
        var refresh = new Button { Text = "Refresh", AutoSize = true, MinimumSize = new Size(96, 30), Margin = new Padding(0, 10, 0, 0) };
        refresh.Click += async (_, _) => await LoadDetailsAsync();
        return Page(Heading("Overview", first: true), _overview, refresh);
    }

    private Control BuildScreenAndLog()
    {
        var shot = new Button { Text = "Take a screenshot", AutoSize = true, MinimumSize = new Size(140, 30), Margin = new Padding(0, 6, 0, 0) };
        shot.Click += async (_, _) => await ScreenshotAsync();
        var log = new Button { Text = "Get the system log", AutoSize = true, MinimumSize = new Size(140, 30), Margin = new Padding(0, 6, 0, 0) };
        log.Click += async (_, _) => await SystemLogAsync();
        return Page(
            Heading("Console screenshot", first: true),
            Note("What the server's screen shows right now: stuck at boot, a blue screen, installing updates ... AWS captures it " +
                 "even when Remote Desktop and Session Manager can't reach the server."),
            _screen, shot,
            Heading("System log"),
            Note("The server's boot and console output. Newer instance types give the latest output; older ones, AWS's last capture."),
            _log, log);
    }

    private Control BuildTags()
    {
        var save = new Button { Text = "Save name and tags", Tag = "primary", AutoSize = true, MinimumSize = new Size(150, 32), Margin = new Padding(0, 12, 0, 0) };
        save.Click += async (_, _) => await SaveTagsAsync();
        return Page(
            Heading("Name", first: true), _name,
            Heading("Tags (one per line: Key=Value)"), _tags,
            Note("Tags AWS manages itself (aws:...) aren't shown and aren't changed. Removing a line removes that tag."),
            save);
    }

    private Control BuildProtection()
    {
        var save = new Button { Text = "Save", Tag = "primary", AutoSize = true, MinimumSize = new Size(110, 32), Margin = new Padding(0, 14, 0, 0) };
        save.Click += async (_, _) => await SaveProtectionAsync();
        var terminate = new Button { Text = "Terminate this server ...", AutoSize = true, MinimumSize = new Size(180, 32), Margin = new Padding(0, 8, 0, 0) };
        terminate.Click += (_, _) =>
        {
            using var f = new TerminateForm(_profile, _region, _instanceId, _details?.Name ?? _instanceId);
            if (f.ShowDialog(this) == DialogResult.OK && f.Terminated) { Terminated = true; Close(); }
        };
        return Page(
            Heading("Protection", first: true), _termination, _stopProtection,
            Heading("When shut down from inside the operating system"), _shutdown,
            Heading("Monitoring"), _monitoring,
            save,
            Heading("Terminate"),
            Note("Deletes the server permanently (you'll be asked to type its name). Termination protection has to be off."),
            terminate);
    }

    private Control BuildInstanceType()
    {
        var change = new Button { Text = "Change type ...", Tag = "primary", AutoSize = true, MinimumSize = new Size(140, 32), Margin = new Padding(0, 14, 0, 0) };
        change.Click += async (_, _) => await ChangeTypeAsync();
        return Page(
            Heading("Current", first: true), _currentType,
            Heading("New instance type"), _newType, _typeDiff,
            Note("Types that run on this server's processor architecture. Prices: on-demand list price in USD, for the server's " +
                 "operating system. Type to search, e.g. \"m6i\"."),
            Note("Changing the type stops the server, changes it and starts it again (if it was running): a few minutes' outage. " +
                 "Stop protection has to be off. Temporary instance-store disks, if any, lose their contents; EBS disks don't."),
            change);
    }

    private Control BuildDisks()
    {
        var grow = new Button { Text = "Grow disk ...", Tag = "primary", AutoSize = true, MinimumSize = new Size(120, 30), Margin = new Padding(12, 0, 0, 0) };
        grow.Click += async (_, _) => await GrowDiskAsync();
        var add = new Button { Text = "Add disk ...", AutoSize = true, MinimumSize = new Size(120, 30), Margin = new Padding(12, 0, 0, 0) };
        add.Click += async (_, _) => await AddDiskAsync();
        var detach = new Button { Text = "Detach the selected disk ...", AutoSize = true, MinimumSize = new Size(190, 30), Margin = new Padding(0, 8, 0, 0) };
        detach.Click += async (_, _) => await DetachDiskAsync();
        return Page(
            Heading("Disks", first: true), _disks, detach,
            Heading("Grow the selected disk"),
            Row(new Label { Text = "New size", AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, _growTo,
                new Label { Text = "GiB", AutoSize = true, Margin = new Padding(4, 6, 12, 0) }, _growType, grow),
            _extend,
            Note("The disk grows while the server runs. Disks can only grow, and AWS allows one change per disk every 6 hours."),
            Heading("Add a disk"),
            Row(_addSize, new Label { Text = "GiB", AutoSize = true, Margin = new Padding(4, 6, 12, 0) }, _addType, _addEncrypted, add),
            _addFormat,
            Heading("Result"), _diskOutput);
    }

    private Control BuildNetwork()
    {
        var saveEni = new Button { Text = "Save security groups and check", Tag = "primary", AutoSize = true, MinimumSize = new Size(210, 30), Margin = new Padding(0, 8, 0, 0) };
        saveEni.Click += async (_, _) => await SaveInterfaceAsync();
        var addIp = new Button { Text = "Add", AutoSize = true, MinimumSize = new Size(70, 28), Margin = new Padding(8, 0, 0, 0) };
        addIp.Click += async (_, _) => await AddSecondaryIpAsync();
        var removeIp = new Button { Text = "Remove the selected address", AutoSize = true, MinimumSize = new Size(190, 28), Margin = new Padding(0, 6, 0, 0) };
        removeIp.Click += async (_, _) => await RemoveSecondaryIpAsync();
        var setProfile = new Button { Text = "Change instance profile", Tag = "primary", AutoSize = true, MinimumSize = new Size(170, 30), Margin = new Padding(0, 8, 0, 0) };
        setProfile.Click += async (_, _) => await SetInstanceProfileAsync();
        return Page(
            Heading("Network interface", first: true), _eni,
            Heading("Security groups"), _sgs, _sourceDest, saveEni,
            Heading("Secondary private IPv4 addresses"), _secondary, removeIp,
            Row(new Label { Text = "Add", AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, _newIp, addIp),
            Note("Leave the address empty for the next free one. On Windows, a secondary address also has to be added in the " +
                 "network adapter's settings (Windows doesn't pick it up automatically); Linux distributions on AWS usually do."),
            Heading("Instance profile (IAM role)"), _iamProfile, _iamWarning, setProfile,
            Note("Switchboard reaches servers through Session Manager, which needs a role with AWS's AmazonSSMManagedInstanceCore " +
                 "policy: attaching one is how to fix a server Switchboard can't connect to. It takes effect within a few minutes."));
    }

    private Control BuildBackups()
    {
        var create = new Button { Text = "Create image ...", Tag = "primary", AutoSize = true, MinimumSize = new Size(140, 30), Margin = new Padding(0, 8, 0, 0) };
        create.Click += async (_, _) => await CreateImageAsync();
        var refresh = new Button { Text = "Refresh", AutoSize = true, MinimumSize = new Size(90, 28), Margin = new Padding(0, 6, 0, 0) };
        refresh.Click += async (_, _) => await LoadBackupsAsync(force: true);
        var snap = new Button { Text = "Snapshot all disks now ...", AutoSize = true, MinimumSize = new Size(180, 30), Margin = new Padding(0, 8, 8, 0) };
        snap.Click += async (_, _) => await SnapshotAllAsync();
        var restore = new Button { Text = "Attach a copy of the selected snapshot ...", AutoSize = true, MinimumSize = new Size(270, 30), Margin = new Padding(0, 8, 0, 0) };
        restore.Click += async (_, _) => await RestoreSnapshotAsync();
        return Page(
            Heading("Create an image (AMI)", first: true),
            Row(new Label { Text = "Name", AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, _imageName),
            Row(new Label { Text = "Description", AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, _imageDescription),
            _imageReboot, create,
            Heading("Your images"), _images, refresh,
            Note("These are the images New server offers under My images. A new image appears here as \"pending\" and becomes " +
                 "\"available\" in a few minutes (longer for big disks)."),
            Heading("This server's snapshots"), _snapshots,
            Row(snap, restore),
            Note("Attaching a copy creates a new disk from the snapshot and attaches it to this server, to copy files back. The " +
                 "server's own disks aren't touched. On Windows, the copy is brought online with a drive letter."),
            Heading("Result"), _backupOutput);
    }

    private Control BuildRunCommand()
    {
        var run = new Button { Text = "Run ...", Tag = "primary", AutoSize = true, MinimumSize = new Size(110, 32), Margin = new Padding(0, 10, 0, 0) };
        run.Click += async (_, _) => await RunCommandAsync();
        return Page(
            Heading("Recent commands", first: true), _recent,
            Heading("Script"), _scriptKind, _script,
            Heading("Also run on"), _alsoOn,
            Note("Other servers in this region with the same platform that Systems Manager reports online."),
            run,
            Heading("Output"), _runOutput);
    }

    private Control BuildPatches()
    {
        var refresh = new Button { Text = "Refresh", AutoSize = true, MinimumSize = new Size(90, 30), Margin = new Padding(0, 8, 8, 0) };
        refresh.Click += async (_, _) => await LoadPatchesAsync(force: true);
        var scan = new Button { Text = "Scan for patches now", AutoSize = true, MinimumSize = new Size(160, 30), Margin = new Padding(0, 8, 8, 0) };
        scan.Click += async (_, _) => await PatchAsync(install: false);
        var install = new Button { Text = "Install patches now ...", AutoSize = true, MinimumSize = new Size(170, 30), Margin = new Padding(0, 8, 0, 0) };
        install.Click += async (_, _) => await PatchAsync(install: true);
        var loadApps = new Button { Text = "Load installed applications", AutoSize = true, MinimumSize = new Size(190, 30), Margin = new Padding(0, 0, 12, 0) };
        loadApps.Click += async (_, _) => await LoadAppsAsync();
        return Page(
            Heading("Systems Manager agent and patches", first: true), _patchInfo,
            Row(refresh, scan, install, _rebootIfNeeded),
            Note("Uses AWS's patch baseline for the server (AWS-RunPatchBaseline). Scanning only reports; installing changes the server."),
            Heading("Installed applications"),
            Row(loadApps, _appFilter), _apps,
            Note("From Systems Manager inventory: empty if inventory collection isn't turned on for this server."));
    }

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        row.Controls.AddRange(controls);
        return row;
    }

    // ------------------------------------------------------------------ actions

    private async Task LoadDetailsAsync()
    {
        Status("Reading the server from AWS ...");
        try
        {
            _details = await ServerAdmin.DetailsAsync(_profile, _region, _instanceId, _cts.Token);
            _overview.Text = OverviewText(_details);
            _name.Text = _details.Name;
            _tags.Text = string.Join(Environment.NewLine, _details.Tags.Select(t => $"{t.Key}={t.Value}"));
            _termination.Checked = _details.TerminationProtection;
            _stopProtection.Checked = _details.StopProtection;
            _shutdown.SelectedIndex = _details.ShutdownBehavior == "terminate" ? 1 : 0;
            _monitoring.Checked = _details.DetailedMonitoring;
            FillDisks();
            UpdateTypeDiff();
            FillInterfaces();
            if (_current == 4) await LoadTypesAsync();
            Status("");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status("Couldn't read the server: " + Aws.Describe(ex), accent: true); }
    }

    private static string OverviewText(ServerDetails d)
    {
        var sb = new StringBuilder();
        void Line(string label, string value) { if (!string.IsNullOrEmpty(value)) sb.Append($"{label,-20}{value}\r\n"); }
        Line("Name", d.Name);
        Line("Instance ID", d.Id);
        Line("State", d.State);
        Line("Instance type", d.Type);
        Line("Zone", d.Zone);
        Line("Platform", d.Platform);
        Line("Image", d.ImageName.Length > 0 ? $"{d.ImageName}  ({d.ImageId})" : d.ImageId);
        Line("Launched", d.Launched);
        Line("Private IPv4", d.PrivateIps);
        Line("Public IPv4", d.PublicIp);
        Line("Instance profile", d.InstanceProfile.Length > 0 ? d.InstanceProfile : "none");
        Line("Key pair", d.KeyName.Length > 0 ? d.KeyName : "none");
        for (int g = 0; g < d.SecurityGroups.Count; g++) Line(g == 0 ? "Security groups" : "", d.SecurityGroups[g]);
        sb.Append("\r\n");
        for (int k = 0; k < d.Disks.Count; k++) Line(k == 0 ? "Disks" : "", d.Disks[k]);
        sb.Append("\r\n");
        Line("System status", StatusWord(d.SystemStatus));
        Line("Instance status", StatusWord(d.InstanceStatus));
        if (d.Events.Count == 0) Line("Scheduled events", "none");
        for (int k = 0; k < d.Events.Count; k++) Line(k == 0 ? "Scheduled events" : "", d.Events[k]);
        sb.Append("\r\n");
        Line("Protection", (d.TerminationProtection ? "termination " : "") + (d.StopProtection ? "stop" : "") is { Length: > 0 } p ? p.Trim() : "none");
        Line("OS shutdown", d.ShutdownBehavior == "terminate" ? "terminates the server" : "stops the server");
        Line("Monitoring", d.DetailedMonitoring ? "detailed (1 minute)" : "basic (5 minutes)");
        return sb.ToString();
    }

    private static string StatusWord(string status) => status switch
    {
        "ok" => "passed",
        "impaired" => "FAILED (impaired)",
        "initializing" => "checking (the server is starting)",
        "insufficient-data" => "not enough data yet",
        "not-applicable" => "not applicable (the server isn't running)",
        "" => "unknown",
        _ => status,
    };

    private async Task ScreenshotAsync()
    {
        Status("Asking AWS for a screenshot ...");
        try
        {
            var bytes = await ServerAdmin.ScreenshotAsync(_profile, _region, _instanceId, _cts.Token);
            if (bytes.Length == 0) { Status("AWS returned no screenshot (the server may be stopped).", accent: true); return; }
            // copied into a bitmap of its own: an image read from a stream needs that stream for its whole life
            using var ms = new MemoryStream(bytes);
            using var loaded = Image.FromStream(ms, true, true);
            var old = _screen.Image;
            _screen.Image = new Bitmap(loaded);
            old?.Dispose();
            Status($"Screenshot taken at {DateTime.Now:HH:mm:ss}.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status("No screenshot: " + Aws.Describe(ex), accent: true); }
    }

    private async Task SystemLogAsync()
    {
        Status("Asking AWS for the system log ...");
        try
        {
            string text = await ServerAdmin.SystemLogAsync(_profile, _region, _instanceId, _cts.Token);
            _log.Text = text.Length == 0 ? "AWS has no console output for this server yet." : text.Replace("\r\n", "\n").Replace("\n", "\r\n");
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
            Status($"System log read at {DateTime.Now:HH:mm:ss}.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status("No system log: " + Aws.Describe(ex), accent: true); }
    }

    private async Task SaveTagsAsync()
    {
        if (_details == null) return;
        string name = _name.Text.Trim();
        if (name.Length == 0) { Status("Enter a name.", accent: true); return; }
        var tags = new List<KeyValuePair<string, string>>();
        foreach (var raw in _tags.Lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) { Status($"Tag line \"{line}\" isn't Key=Value.", accent: true); return; }
            string key = line[..eq].Trim();
            if (key.Equals("Name", StringComparison.OrdinalIgnoreCase) || key.StartsWith("aws:", StringComparison.OrdinalIgnoreCase)) continue;
            tags.RemoveAll(t => t.Key == key);
            tags.Add(new(key, line[(eq + 1)..].Trim()));
        }
        Status("Saving ...");
        try
        {
            await ServerAdmin.SaveTagsAsync(_profile, _region, _instanceId, name, _details.Tags, tags, _cts.Token);
            Changed = true;
            Text = $"Server: {name} ({_instanceId})";
            await LoadDetailsAsync();
            Status("Name and tags saved.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status("Not saved: " + Aws.Describe(ex), accent: true); }
    }

    private async Task SaveProtectionAsync()
    {
        if (_details == null) return;
        string shutdown = _shutdown.SelectedIndex == 1 ? "terminate" : "stop";
        if (shutdown == "terminate" && _details.ShutdownBehavior != "terminate" &&
            MessageBox.Show(this, "With this, shutting the server down from inside Windows or Linux terminates it: the server and " +
                                  "its disks (unless set to be kept) are deleted, not just stopped. Save this?", "Protection",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        Status("Saving ...");
        try
        {
            await ServerAdmin.SaveProtectionAsync(_profile, _region, _details, _termination.Checked, _stopProtection.Checked,
                shutdown, _monitoring.Checked, _cts.Token);
            await LoadDetailsAsync();
            Status("Saved.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status("Not saved: " + Aws.Describe(ex), accent: true); }
    }

    // ------------------------------------------------------------------ instance type

    private string Os => _details?.IsWindows == true ? "Windows" : "Linux";

    private async Task LoadTypesAsync()
    {
        if (_details == null) return;
        string key = _details.Architecture + "|" + Os;
        if (_typesFor == key && _types != null) return;
        Status("Loading instance types and prices ...");
        try
        {
            var types = await ServerLaunch.InstanceTypesAsync(_profile, _region, _details.Architecture, _cts.Token);
            Dictionary<string, double> prices = null;
            try { prices = await ServerLaunch.InstancePricesAsync(_profile, _region, Os, _cts.Token); }
            catch (Exception ex) { Log.Write("Server: prices unavailable: " + Aws.Describe(ex)); }
            foreach (var t in types) t.Hourly = prices != null && prices.TryGetValue(t.Name, out var p) ? p : double.NaN;
            _types = types;
            _typesFor = key;
            _newType.BeginUpdate();
            _newType.Items.Clear();
            _newType.Items.AddRange(types.Cast<object>().ToArray());
            _newType.EndUpdate();
            NewServerForm.Select(_newType, types.FirstOrDefault(t => t.Name == _details.Type));
            UpdateTypeDiff();
            Status(prices == null ? "Prices aren't available (the AWS Pricing API needs pricing:GetProducts)." : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status("Couldn't list instance types: " + Aws.Describe(ex), accent: true); }
    }

    private TypeChoice TypeByName(string name) => _types?.FirstOrDefault(t => t.Name == name);

    private void UpdateTypeDiff()
    {
        if (_details == null) return;
        var current = TypeByName(_details.Type);
        _currentType.Text = current?.ToString() ?? _details.Type;
        var wanted = TypeByName(((_newType.Text ?? "").Trim().Split(' ')[0]));
        if (wanted == null || wanted.Name == _details.Type) { _typeDiff.Text = ""; return; }
        if (current == null || double.IsNaN(current.Hourly) || double.IsNaN(wanted.Hourly)) { _typeDiff.Text = ""; return; }
        double monthly = (wanted.Hourly - current.Hourly) * 730;
        _typeDiff.Text = monthly >= 0 ? $"~${monthly:N2}/month more than now" : $"~${-monthly:N2}/month less than now";
    }

    private async Task ChangeTypeAsync()
    {
        if (_busy || _details == null) return;
        string wanted = (_newType.Text ?? "").Trim().Split(' ')[0];
        if (wanted.Length == 0 || wanted == _details.Type) { Status("Choose a different instance type.", accent: true); return; }
        if (_types != null && TypeByName(wanted) == null) { Status($"{wanted} isn't a type that runs on this server's architecture here.", accent: true); return; }
        bool running = _details.State is "running" or "pending";
        if (running && _details.StopProtection)
        {
            Status("Stop protection is on: turn it off (Protection page) to change the type.", accent: true);
            return;
        }
        string question = running
            ? $"Change {_details.Name} from {_details.Type} to {wanted}?\n\nThe server is stopped, changed and started again: anyone " +
              "connected is disconnected for a few minutes."
            : $"Change {_details.Name} from {_details.Type} to {wanted}? It stays stopped.";
        if (MessageBox.Show(this, question, "Change instance type", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.OK) return;

        _busy = true;
        UseWaitCursor = true;
        try
        {
            await ServerAdmin.ChangeTypeAsync(_profile, _region, _instanceId, wanted, new Progress<string>(m => Status(m)), _cts.Token);
            Changed = true;
            await LoadDetailsAsync();
            Status($"The type is now {wanted}{(running ? ", and the server is running again" : "")}.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            await LoadDetailsAsync();
            Status($"The type change didn't finish: {Aws.Describe(ex)}. The server is now {_details?.State}.", accent: true);
        }
        finally { _busy = false; UseWaitCursor = false; }
    }

    // ------------------------------------------------------------------ disks

    private DiskInfo SelectedDisk => _disks.SelectedItems.Count > 0 ? _disks.SelectedItems[0].Tag as DiskInfo : null;

    private void FillDisks()
    {
        if (_details == null) return;
        string selected = SelectedDisk?.VolumeId;
        _disks.BeginUpdate();
        _disks.Items.Clear();
        foreach (var v in _details.Volumes)
        {
            var item = new ListViewItem(new[]
            {
                v.Device + (v.IsRoot ? " (system)" : ""), $"{v.SizeGb} GiB", v.Type, v.Iops > 0 ? v.Iops.ToString() : "",
                v.Throughput > 0 ? v.Throughput.ToString() : "", v.Encrypted ? "yes" : "no", v.VolumeId,
            }) { Tag = v };
            _disks.Items.Add(item);
            if (v.VolumeId == selected || (selected == null && _disks.Items.Count == 1)) item.Selected = true;
        }
        _disks.EndUpdate();
        _addFormat.Visible = _details.IsWindows;
        DiskSelected();
    }

    private void DiskSelected()
    {
        if (SelectedDisk is not { } d) return;
        _growTo.Minimum = 1;
        _growTo.Value = Math.Min(_growTo.Maximum, d.SizeGb + Math.Max(10, d.SizeGb / 4));
        _growTo.Minimum = d.SizeGb + 1;
    }

    private void Output(string text) =>
        _diskOutput.Text = (text ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");

    private async Task GrowDiskAsync()
    {
        if (_busy || _details == null) return;
        if (SelectedDisk is not { } d) { Status("Select a disk first.", accent: true); return; }
        int size = (int)_growTo.Value;
        string type = _growType.SelectedIndex > 0 ? _growType.SelectedItem as string : "";
        bool extend = _extend.Checked;
        bool canExtend = _details.State == "running";
        string question = $"Grow {d.Device} ({d.VolumeId}) from {d.SizeGb} GiB to {size} GiB" + (type.Length > 0 ? $" and change it to {type}" : "") + "?" +
                          (extend && canExtend ? "\n\nThen its partition and file system are extended inside the operating system." :
                           extend ? "\n\nThe server isn't running, so extend the partition once it is." : "") +
                          "\n\nDisks can't be made smaller again.";
        if (MessageBox.Show(this, question, "Grow disk", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;

        _busy = true;
        UseWaitCursor = true;
        try
        {
            await ServerAdmin.GrowDiskAsync(_profile, _region, d.VolumeId, size, type, new Progress<string>(m => Status(m)), _cts.Token);
            Output($"{d.Device} is now {size} GiB in AWS.");
            if (extend && canExtend)
            {
                Status("Extending the partition inside the operating system ...");
                try
                {
                    string result = await ServerAdmin.RunScriptAsync(_profile, _region, _instanceId, _details.IsWindows,
                        ServerAdmin.ExtendScript(_details.IsWindows, d.VolumeId, d.Device), _cts.Token);
                    Output($"{d.Device} is now {size} GiB in AWS.\n{result}");
                }
                catch (Exception ex)
                {
                    Output($"{d.Device} is now {size} GiB in AWS, but the partition wasn't extended: {Aws.Describe(ex)}\n" +
                           "Extend it in Disk Management (Windows) or with growpart and xfs_growfs / resize2fs (Linux).");
                }
            }
            await LoadDetailsAsync();
            Status("Done.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status("The disk wasn't grown: " + Aws.Describe(ex), accent: true); }
        finally { _busy = false; UseWaitCursor = false; }
    }

    private async Task AddDiskAsync()
    {
        if (_busy || _details == null) return;
        int size = (int)_addSize.Value;
        string type = _addType.SelectedItem as string ?? "gp3";
        bool format = _details.IsWindows && _addFormat.Checked && _details.State == "running";
        if (MessageBox.Show(this, $"Add a {size} GiB {type} disk to {_details.Name}?" + (format ? " It's then initialized and formatted in Windows." : ""),
                "Add disk", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

        _busy = true;
        UseWaitCursor = true;
        try
        {
            string volumeId = await ServerAdmin.AddDiskAsync(_profile, _region, _details, size, type, _addEncrypted.Checked,
                new Progress<string>(m => Status(m)), _cts.Token);
            Output($"{volumeId} ({size} GiB {type}) attached.");
            if (format)
            {
                Status("Initializing and formatting the disk in Windows ...");
                try
                {
                    string result = await ServerAdmin.RunScriptAsync(_profile, _region, _instanceId, true, ServerAdmin.FormatNewDiskScript(volumeId), _cts.Token);
                    Output($"{volumeId} ({size} GiB {type}) attached.\n{result}");
                }
                catch (Exception ex) { Output($"{volumeId} attached, but it wasn't formatted: {Aws.Describe(ex)}\nSet it up in Disk Management."); }
            }
            Changed = true;
            await LoadDetailsAsync();
            Status("Done.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status("The disk wasn't added: " + Aws.Describe(ex), accent: true); }
        finally { _busy = false; UseWaitCursor = false; }
    }

    private async Task DetachDiskAsync()
    {
        if (_busy || _details == null) return;
        if (SelectedDisk is not { } d) { Status("Select a disk first.", accent: true); return; }
        if (d.IsRoot) { Status("The system disk can't be detached while it's the server's system disk.", accent: true); return; }
        if (MessageBox.Show(this, $"Detach {d.Device} ({d.SizeGb} GiB, {d.VolumeId}) from {_details.Name}?\n\nTake it offline in the " +
                                  "operating system first if it's in use. The disk itself is kept (and still charged), so it can be attached again.",
                "Detach disk", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        _busy = true;
        try
        {
            await ServerAdmin.DetachDiskAsync(_profile, _region, d.VolumeId, _cts.Token);
            Output($"{d.VolumeId} detached (kept in AWS).");
            await Task.Delay(TimeSpan.FromSeconds(3));
            await LoadDetailsAsync();
            Status("Done.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status("The disk wasn't detached: " + Aws.Describe(ex), accent: true); }
        finally { _busy = false; }
    }

    // ------------------------------------------------------------------ network

    private InterfaceInfo SelectedInterface => _eni.SelectedItem as InterfaceInfo;

    private void FillInterfaces()
    {
        if (_details == null) return;
        string selected = SelectedInterface?.Id;
        _eni.Items.Clear();
        _eni.Items.AddRange(_details.Interfaces.Cast<object>().ToArray());
        NewServerForm.Select(_eni, _details.Interfaces.FirstOrDefault(n => n.Id == selected) ?? _details.Interfaces.FirstOrDefault());
        ShowInterface();
        if (_profilesLoaded) SelectCurrentProfile();
    }

    private void ShowInterface()
    {
        if (SelectedInterface is not { } n) return;
        _sgs.Items.Clear();
        foreach (var g in _vpcGroups ?? new List<NamedChoice>()) _sgs.Items.Add(g, n.GroupIds.Contains(g.Id));
        // groups on the interface that the list doesn't have (e.g. still loading): keep them visible and ticked
        foreach (var id in n.GroupIds.Where(id => (_vpcGroups ?? new List<NamedChoice>()).All(g => g.Id != id)))
            _sgs.Items.Add(new NamedChoice { Id = id, Name = id }, true);
        _sourceDest.Checked = n.SourceDestCheck;
        _secondary.Items.Clear();
        _secondary.Items.AddRange(n.SecondaryIps.Cast<object>().ToArray());
    }

    private async Task LoadNetworkAsync()
    {
        if (_details == null) return;
        try
        {
            if (_vpcGroups == null)
            {
                Status("Loading security groups ...");
                var (_, _, groups) = await ServerLaunch.NetworkAsync(_profile, _region, _cts.Token);
                _vpcGroups = groups.Where(g => g.VpcId == _details.VpcId).ToList();
                ShowInterface();
            }
            if (!_profilesLoaded)
            {
                Status("Loading instance profiles ...");
                var profiles = await ServerLaunch.InstanceProfilesAsync(_profile, _cts.Token);
                _iamProfile.Items.Clear();
                _iamProfile.Items.Add("(none)");
                _iamProfile.Items.AddRange(profiles.Cast<object>().ToArray());
                _profilesLoaded = true;
                SelectCurrentProfile();
            }
            Status("");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status("Couldn't load the network details: " + Aws.Describe(ex), accent: true); }
    }

    private void SelectCurrentProfile()
    {
        var match = _iamProfile.Items.OfType<NamedChoice>().FirstOrDefault(p => p.Id == _details?.InstanceProfile);
        NewServerForm.Select(_iamProfile, (object)match ?? "(none)");
        UpdateIamWarning();
    }

    private void UpdateIamWarning() =>
        _iamWarning.Text = _iamProfile.SelectedItem is NamedChoice { Flag: false }
            ? "This profile's role doesn't have AWS's AmazonSSMManagedInstanceCore policy attached: unless it grants Session Manager access another way, Switchboard can't connect."
            : _iamProfile.SelectedItem is string ? "Without an instance profile, Switchboard can't connect to the server through Session Manager." : "";

    private async Task SaveInterfaceAsync()
    {
        if (_busy || SelectedInterface is not { } n) return;
        var groups = _sgs.CheckedItems.Cast<NamedChoice>().Select(g => g.Id).ToList();
        if (groups.Count == 0) { Status("Tick at least one security group.", accent: true); return; }
        _busy = true;
        Status("Saving ...");
        try
        {
            await ServerAdmin.SaveInterfaceAsync(_profile, _region, n, groups, _sourceDest.Checked, _cts.Token);
            await LoadDetailsAsync();
            Status("Saved.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status("Not saved: " + Aws.Describe(ex), accent: true); }
        finally { _busy = false; }
    }

    private async Task AddSecondaryIpAsync()
    {
        if (_busy || SelectedInterface is not { } n) return;
        string ip = _newIp.Text.Trim();
        _busy = true;
        try
        {
            if (ip.Length > 0)
            {
                Status($"Checking {ip} ...");
                string cidr = await ServerAdmin.SubnetCidrAsync(_profile, _region, n.SubnetId, _cts.Token);
                string problem = await ServerLaunch.PrivateIpProblemAsync(_profile, _region, cidr, ip, _cts.Token);
                if (problem != null) { Status(problem, accent: true); return; }
            }
            Status("Adding the address ...");
            string added = await ServerAdmin.AddSecondaryIpAsync(_profile, _region, n.Id, ip, _cts.Token);
            _newIp.Text = "";
            await LoadDetailsAsync();
            Status($"{added} added." + (_details?.IsWindows == true ? " Add it in Windows' network adapter settings too." : ""));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status("Not added: " + Aws.Describe(ex), accent: true); }
        finally { _busy = false; }
    }

    private async Task RemoveSecondaryIpAsync()
    {
        if (_busy || SelectedInterface is not { } n) return;
        if (_secondary.SelectedItem is not string ip) { Status("Select an address first.", accent: true); return; }
        if (MessageBox.Show(this, $"Remove {ip} from {n.Id}? Anything using that address loses it.", "Remove address",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        _busy = true;
        try
        {
            await ServerAdmin.RemoveSecondaryIpAsync(_profile, _region, n.Id, ip, _cts.Token);
            await LoadDetailsAsync();
            Status($"{ip} removed.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status("Not removed: " + Aws.Describe(ex), accent: true); }
        finally { _busy = false; }
    }

    private async Task SetInstanceProfileAsync()
    {
        if (_busy || _details == null || !_profilesLoaded) return;
        string wanted = (_iamProfile.SelectedItem as NamedChoice)?.Id ?? "";
        if (wanted == _details.InstanceProfile) { Status("That's already the server's instance profile.", accent: true); return; }
        string question = wanted.Length == 0
            ? $"Remove the instance profile from {_details.Name}? Switchboard (and anything else using the role) loses its access to the server."
            : $"Set {_details.Name}'s instance profile to {wanted}?";
        if (MessageBox.Show(this, question, "Instance profile", MessageBoxButtons.OKCancel, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        _busy = true;
        try
        {
            await ServerAdmin.SetInstanceProfileAsync(_profile, _region, _instanceId, wanted, _cts.Token);
            await Task.Delay(TimeSpan.FromSeconds(2));
            await LoadDetailsAsync();
            Status(wanted.Length == 0 ? "Instance profile removed." : $"Instance profile set to {wanted}; it takes effect on the server within a few minutes.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status("Not changed: " + Aws.Describe(ex), accent: true); }
        finally { _busy = false; }
    }

    // ------------------------------------------------------------------ images and snapshots

    private bool _backupsLoaded;

    private async Task LoadBackupsAsync(bool force = false)
    {
        if (_details == null || (_backupsLoaded && !force)) return;
        if (_imageName.Text.Length == 0) _imageName.Text = $"{_details.Name} {DateTime.Now:yyyy-MM-dd HHmm}";
        Status("Loading images and snapshots ...");
        try
        {
            var images = await ServerAdmin.MyImagesAsync(_profile, _region, _cts.Token);
            var snapshots = await ServerAdmin.SnapshotsAsync(_profile, _region, _details, _cts.Token);
            _images.BeginUpdate();
            _images.Items.Clear();
            foreach (var img in images)
                _images.Items.Add(new ListViewItem(new[] { img.Name, img.Id, img.Created > DateTime.MinValue ? img.Created.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "", img.State }) { Tag = img });
            _images.EndUpdate();
            _snapshots.BeginUpdate();
            _snapshots.Items.Clear();
            foreach (var sn in snapshots)
                _snapshots.Items.Add(new ListViewItem(new[] { sn.Name, sn.Id, $"{sn.SizeGb} GiB", sn.Created > DateTime.MinValue ? sn.Created.ToString("yyyy-MM-dd HH:mm") : "", sn.State, sn.Detail }) { Tag = sn });
            _snapshots.EndUpdate();
            _backupsLoaded = true;
            Status("");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status("Couldn't load images and snapshots: " + Aws.Describe(ex), accent: true); }
    }

    private async Task CreateImageAsync()
    {
        if (_busy || _details == null) return;
        string name = _imageName.Text.Trim();
        if (name.Length < 3) { Status("Give the image a name (at least 3 characters).", accent: true); return; }
        bool reboot = _imageReboot.Checked;
        if (MessageBox.Show(this, $"Create an image of {_details.Name} named \"{name}\"?" +
                                  (reboot ? "\n\nThe server is rebooted first, so anyone connected is disconnected briefly." :
                                            "\n\nWithout a reboot, the image is taken from the running server and may miss data still in memory."),
                "Create image", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _busy = true;
        Status("Starting the image ...");
        try
        {
            string id = await ServerAdmin.CreateImageAsync(_profile, _region, _details, name, _imageDescription.Text.Trim(), reboot, _cts.Token);
            _backupOutput.Text = $"Image {id} ({name}) is being created. It shows as pending below until AWS finishes it.";
            _imageName.Text = "";
            _backupsLoaded = false;
            await LoadBackupsAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status("The image wasn't started: " + Aws.Describe(ex), accent: true); }
        finally { _busy = false; }
    }

    private async Task SnapshotAllAsync()
    {
        if (_busy || _details == null) return;
        if (MessageBox.Show(this, $"Snapshot all of {_details.Name}'s disks now ({_details.Volumes.Count})? The server keeps running; the " +
                                  "snapshots are taken together, at the same moment.", "Snapshot", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _busy = true;
        Status("Starting the snapshots ...");
        try
        {
            var ids = await ServerAdmin.SnapshotAllAsync(_profile, _region, _details, $"{_details.Name} {DateTime.Now:yyyy-MM-dd HH:mm} (Switchboard)", _cts.Token);
            _backupOutput.Text = $"Snapshots started: {string.Join(", ", ids)}. They show as pending until AWS finishes them.";
            _backupsLoaded = false;
            await LoadBackupsAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status("The snapshots weren't started: " + Aws.Describe(ex), accent: true); }
        finally { _busy = false; }
    }

    private async Task RestoreSnapshotAsync()
    {
        if (_busy || _details == null) return;
        if ((_snapshots.SelectedItems.Count > 0 ? _snapshots.SelectedItems[0].Tag : null) is not BackupItem snap) { Status("Select a snapshot first.", accent: true); return; }
        if (!snap.State.StartsWith("completed", StringComparison.OrdinalIgnoreCase)) { Status("That snapshot isn't finished yet.", accent: true); return; }
        if (MessageBox.Show(this, $"Create a new disk from {snap.Id} ({snap.Name}, taken {snap.Created:yyyy-MM-dd HH:mm}) and attach it to " +
                                  $"{_details.Name}? The server's own disks aren't changed. The new disk is charged until you detach and delete it.",
                "Attach a copy", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        _busy = true;
        UseWaitCursor = true;
        try
        {
            string volumeId = await ServerAdmin.DiskFromSnapshotAsync(_profile, _region, _details, snap, new Progress<string>(m => Status(m)), _cts.Token);
            _backupOutput.Text = $"{volumeId} (a copy of {snap.Id}) attached.";
            if (_details.IsWindows && _details.State == "running")
            {
                Status("Bringing the copy online in Windows ...");
                try
                {
                    string result = await ServerAdmin.RunScriptAsync(_profile, _region, _instanceId, true, ServerAdmin.BringOnlineScript(volumeId), _cts.Token);
                    _backupOutput.Text = $"{volumeId} (a copy of {snap.Id}) attached.\r\n{result}";
                }
                catch (Exception ex) { _backupOutput.Text = $"{volumeId} attached, but not brought online: {Aws.Describe(ex)}. Bring it online in Disk Management."; }
            }
            Changed = true;
            await LoadDetailsAsync();
            Status("Done. When you've finished with the copy, detach it on the Disks page (and delete it in the AWS console).");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status("The copy wasn't attached: " + Aws.Describe(ex), accent: true); }
        finally { _busy = false; UseWaitCursor = false; }
    }

    // ------------------------------------------------------------------ run command

    private void FillRunTargets()
    {
        if (_details == null) return;
        _scriptKind.Text = _details.IsWindows ? "PowerShell (runs as SYSTEM through Systems Manager)" : "Shell script (runs as root through Systems Manager)";
        if (_alsoOn.Items.Count == 0)
            foreach (var p in _peers.Where(p => p.IsWindows == _details.IsWindows).OrderBy(p => p.Name)) _alsoOn.Items.Add(p, false);
        _recent.Items.Clear();
        _recent.Items.Add(_settings.RecentCommands.Count == 0 ? "(none yet)" : "(choose a recent command)");
        foreach (var c in _settings.RecentCommands) _recent.Items.Add(c.Replace("\r", "").Replace("\n", "  ⏎  "));
        _recent.SelectedIndex = 0;
    }

    private async Task RunCommandAsync()
    {
        if (_busy || _details == null) return;
        string script = _script.Text.Trim();
        if (script.Length == 0) { Status("Type a command or script to run.", accent: true); return; }
        var targets = new List<CommandTarget> { new(_details.Id, _details.Name, _details.IsWindows) };
        targets.AddRange(_alsoOn.CheckedItems.Cast<CommandTarget>());
        string where = targets.Count == 1 ? _details.Name : $"{targets.Count} servers ({string.Join(", ", targets.Select(t => t.Name))})";
        if (MessageBox.Show(this, $"Run this on {where}?\n\n{(script.Length > 400 ? script[..400] + " ..." : script)}", "Run command",
                MessageBoxButtons.OKCancel, targets.Count > 1 ? MessageBoxIcon.Warning : MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;

        string stored = script.Replace("\r\n", "\n");
        _settings.RecentCommands.RemoveAll(c => c == stored);
        _settings.RecentCommands.Insert(0, stored);
        if (_settings.RecentCommands.Count > 10) _settings.RecentCommands.RemoveRange(10, _settings.RecentCommands.Count - 10);
        _settings.Save();

        _busy = true;
        UseWaitCursor = true;
        _runOutput.Text = "";
        Status($"Running on {targets.Count} server(s) ...");
        try
        {
            var results = await ServerAdmin.RunOnManyAsync(_profile, _region, targets, script, new Progress<string>(m => Status(m)), _cts.Token);
            var sb = new StringBuilder();
            foreach (var r in results)
            {
                string verdict = r.Status == "Success" ? "succeeded" : r.Status.ToLowerInvariant();
                sb.Append($"===== {r.Name} ({r.Id}): {verdict} =====\r\n");
                sb.Append((r.Output.Length > 0 ? r.Output : "(no output)").Replace("\r\n", "\n").Replace("\n", "\r\n")).Append("\r\n\r\n");
            }
            _runOutput.Text = sb.ToString();
            int ok = results.Count(r => r.Status == "Success");
            Status(ok == results.Count ? $"Finished on {ok} server(s)." : $"{ok} of {results.Count} succeeded.", accent: ok != results.Count);
            FillRunTargets();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status("Not run: " + Aws.Describe(ex), accent: true); }
        finally { _busy = false; UseWaitCursor = false; }
    }

    // ------------------------------------------------------------------ patches and agent

    private async Task LoadPatchesAsync(bool force = false)
    {
        if (_details == null || (_patchesLoaded && !force)) return;
        Status("Reading the agent and patch state ...");
        try
        {
            var p = await ServerAdmin.PatchInfoAsync(_profile, _region, _instanceId, _cts.Token);
            var sb = new StringBuilder();
            void Line(string label, string value) => sb.Append($"{label,-22}{value}\r\n");
            if (p.Ping.Length == 0) sb.Append("Systems Manager doesn't know this server: its agent isn't registered (or the instance profile lacks permission).\r\n");
            else
            {
                Line("Agent", p.Ping == "Online" ? "online" : p.Ping == "ConnectionLost" ? "CONNECTION LOST" : p.Ping);
                Line("Agent version", p.AgentVersion + (p.AgentLatest == false ? "  (an update is available)" : p.AgentLatest == true ? "  (latest)" : ""));
                Line("Last check-in", p.LastPing);
                Line("Operating system", p.Platform + (p.ComputerName.Length > 0 ? $"  ({p.ComputerName})" : ""));
            }
            sb.Append("\r\n");
            if (!p.HasPatchState) sb.Append("No patch information yet: scan for patches to get it.\r\n");
            else
            {
                Line("Patches missing", p.Missing.ToString());
                Line("Failed", p.Failed.ToString());
                Line("Waiting for a reboot", p.PendingReboot.ToString());
                Line("Installed", p.Installed.ToString() + (p.Rejected > 0 ? $"  ({p.Rejected} installed but rejected by the baseline)" : ""));
                Line("Last operation", p.LastOperation);
                if (p.Baseline.Length > 0) Line("Patch baseline", p.Baseline);
                if (p.Problems.Count > 0)
                {
                    sb.Append("\r\nMissing and failed:\r\n");
                    foreach (var line in p.Problems) sb.Append("  ").Append(line).Append("\r\n");
                }
            }
            _patchInfo.Text = sb.ToString();
            _patchesLoaded = true;
            Status("");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status("Couldn't read the patch state: " + Aws.Describe(ex), accent: true); }
    }

    private async Task PatchAsync(bool install)
    {
        if (_busy || _details == null) return;
        bool reboot = install && _rebootIfNeeded.Checked;
        if (install && MessageBox.Show(this, $"Install the missing patches on {_details.Name} now, using its patch baseline?" +
                (reboot ? "\n\nThe server reboots if the patches need it: anyone connected is disconnected." :
                          "\n\nThe server won't be rebooted; patches that need a reboot finish installing at its next restart."),
                "Install patches", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        _busy = true;
        UseWaitCursor = true;
        Status(install ? "Installing patches (this can take a while) ..." : "Scanning for patches ...");
        try
        {
            await ServerAdmin.PatchBaselineAsync(_profile, _region, _instanceId, install, reboot, _cts.Token);
            await LoadPatchesAsync(force: true);
            Status(install ? "Patch installation finished." : "Scan finished.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status((install ? "Installing patches" : "The scan") + " didn't finish: " + Aws.Describe(ex), accent: true); }
        finally { _busy = false; UseWaitCursor = false; }
    }

    private async Task LoadAppsAsync()
    {
        Status("Reading installed applications ...");
        try
        {
            _appList = await ServerAdmin.ApplicationsAsync(_profile, _region, _instanceId, _cts.Token);
            ShowApps();
            Status(_appList.Count == 0 ? "No inventory for this server (Systems Manager inventory isn't collecting it)." : $"{_appList.Count} applications.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status("Couldn't read the applications: " + Aws.Describe(ex), accent: true); }
    }

    private void ShowApps()
    {
        if (_appList == null) return;
        string filter = _appFilter.Text.Trim();
        var shown = filter.Length == 0 ? _appList : _appList.Where(a => a.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        _apps.Text = string.Join("\r\n", shown);
    }

    // ------------------------------------------------------------------ helpers

    private void Status(string text, bool accent = false)
    {
        _status.ForeColor = accent ? Theme.Current.AccentFore : Theme.Current.Muted;
        _status.Text = text;
    }

    private static TextBox Readout(int width, int height) => new()
    {
        Width = width, Height = height, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
    };

    private static FlowLayoutPanel Page(params Control[] controls)
    {
        var page = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        page.Controls.AddRange(controls);
        return page;
    }

    private static Label Heading(string text, bool first = false) =>
        new() { Text = text, AutoSize = true, UseMnemonic = false, Font = new Font("Segoe UI Semibold", 10.5F), Margin = new Padding(0, first ? 0 : 16, 0, 6) };

    private static Label Note(string text) =>
        new() { Text = text, AutoSize = true, UseMnemonic = false, Tag = "muted", MaximumSize = new Size(PageWidth, 0), Margin = new Padding(0, 0, 0, 6) };

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _cts.Dispose(); _screen.Image?.Dispose(); }
        base.Dispose(disposing);
    }
}
