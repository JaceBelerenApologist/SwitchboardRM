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

    /// <summary>True once something was changed (name, tags ...), so the server list should refresh.</summary>
    public bool Changed { get; private set; }

    public ServerForm(string profile, string region, string instanceId, string name)
    {
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
        _log.Font = _overview.Font = new Font("Consolas", 9.5F);
        _log.WordWrap = false;
        _log.ScrollBars = ScrollBars.Both;

        AddPage("Overview", BuildOverview);
        AddPage("Screen and log", BuildScreenAndLog);
        AddPage("Name and tags", BuildTags);
        AddPage("Protection", BuildProtection);

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

    private void ShowPage(int index)
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
            b.Font = new Font(Font, i == index ? FontStyle.Bold : FontStyle.Regular);
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
        return Page(
            Heading("Protection", first: true), _termination, _stopProtection,
            Heading("When shut down from inside the operating system"), _shutdown,
            Heading("Monitoring"), _monitoring,
            save);
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
