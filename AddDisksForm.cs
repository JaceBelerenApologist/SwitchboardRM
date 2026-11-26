namespace SwitchboardRM;

/// <summary>
/// Create several EBS disks in one go: one row per disk (name part, size, type, IOPS, throughput), encryption with a
/// KMS key (the account's EBS default selected), tags (the server's offered to copy) and attaching. Used for a server
/// (Server window > Disks: its zone, attached to it), for standalone new volumes (Volumes window: pick a zone, and a
/// server to attach to if wanted), and for a volume from a snapshot (one disk, at least the snapshot's size).
/// Delete on termination stays off (AWS's default for disks attached after launch).
/// </summary>
internal sealed class AddDisksForm : Form
{
    private const int MaxDisks = 10;

    private readonly string _profile, _region;
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _serverName;                 // the server (Server window), or null
    private readonly InstanceChoice _server;              // ... its ID, platform and devices
    private readonly List<KeyValuePair<string, string>> _serverTags;
    private readonly SnapshotRow _snapshot;               // creating from a snapshot, or null
    private Dictionary<string, double> _prices = new();

    private readonly TableLayoutPanel _grid = new() { AutoSize = true, Margin = new Padding(0), ColumnCount = 8 };
    private readonly List<DiskRow> _rows = new();
    private readonly Button _addRow = new() { Text = "+ Add another", AutoSize = true, MinimumSize = new Size(120, 28), Margin = new Padding(0, 6, 0, 0) };
    private readonly Label _total = new() { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 8, 0, 0) };
    private readonly ComboBox _zone = new() { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _attachTo = new() { Width = 360, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ThemedCheckBox _attach = new() { AutoSize = true, Checked = true, Margin = new Padding(0, 10, 0, 0) };
    private readonly ThemedCheckBox _encrypt = new() { Text = "Encrypt", AutoSize = true, Checked = true, Margin = new Padding(0, 4, 12, 0) };
    private readonly ComboBox _key = new() { Width = 380, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly Label _keyNote = new() { AutoSize = true, UseMnemonic = false, Tag = "muted", MaximumSize = new Size(640, 0), Margin = new Padding(0, 4, 0, 0) };
    private readonly ThemedCheckBox _copyTags = new() { AutoSize = true, Checked = true, Margin = new Padding(0, 4, 0, 0) };
    private readonly TextBox _tags = new() { Width = 640, Height = 70, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
    private readonly Label _status = new() { AutoSize = true, UseMnemonic = false, MaximumSize = new Size(640, 0), Margin = new Padding(0, 10, 0, 0) };
    private readonly Button _create = new() { Tag = "primary", AutoSize = true, MinimumSize = new Size(140, 32), Margin = new Padding(0, 0, 8, 0) };
    private readonly Button _cancel = new() { Text = "Cancel", AutoSize = true, MinimumSize = new Size(90, 32), Margin = new Padding(0) };
    private bool _running, _done;

    /// <summary>The volumes created (attached or not): callers check this, not DialogResult.</summary>
    public List<string> CreatedVolumes { get; } = new();

    /// <summary>For a server: its zone, its tags offered to copy, attached to it.</summary>
    public static AddDisksForm ForServer(string profile, string region, AppSettings settings, ServerDetails server) =>
        new(profile, region, settings, server.Zone,
            new InstanceChoice
            {
                Id = server.Id, Name = server.Name, Zone = server.Zone, State = server.State, Windows = server.IsWindows,
                Devices = new HashSet<string>(server.Volumes.Select(v => v.Device), StringComparer.OrdinalIgnoreCase),
            }, server.Tags, null);

    /// <summary>Standalone (Volumes window): a zone to choose, and optionally a server there to attach to.</summary>
    public static AddDisksForm Standalone(string profile, string region, AppSettings settings, SnapshotRow fromSnapshot = null) =>
        new(profile, region, settings, null, null, null, fromSnapshot);

    private AddDisksForm(string profile, string region, AppSettings settings, string zone, InstanceChoice server,
        List<KeyValuePair<string, string>> serverTags, SnapshotRow snapshot)
    {
        _profile = profile;
        _region = region;
        _settings = settings;
        _server = server;
        _serverName = server?.Name;
        _serverTags = serverTags ?? new();
        _snapshot = snapshot;

        Text = snapshot != null ? $"New volume from {snapshot.Label}" : server != null ? $"Add disks to {Display(server)}" : $"New volumes ({region})";
        if (AppIcon.Icon != null) Icon = AppIcon.Icon;
        Font = new Font("Segoe UI", 9.5F);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        AutoScroll = true;
        ClientSize = new Size(LogicalToDeviceUnits(760), LogicalToDeviceUnits(640));
        MinimumSize = new Size(LogicalToDeviceUnits(700), LogicalToDeviceUnits(480));

        var body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(20, 16, 20, 16) };

        // where
        if (server != null)
        {
            body.Controls.Add(Caption($"In {zone}, the zone of {Display(server)}" + (server.Windows ? " (Windows)" : ""), 0));
        }
        else
        {
            body.Controls.Add(Caption("Availability zone", 0));
            body.Controls.Add(_zone);
            _zone.SelectedIndexChanged += async (_, _) => await LoadServersInZoneAsync();
        }

        // the disks
        body.Controls.Add(Heading(snapshot != null ? "The volume" : "Disks"));
        foreach (var (title, width) in new[] { (server != null ? "Drive / name" : "Name", 150), ("Size (GiB)", 90), ("Type", 80), ("IOPS", 90), ("MB/s", 76), ("$/month", 80), ("", 34), ("", 200) })
            _grid.Controls.Add(new Label { Text = title, AutoSize = true, Tag = "muted", Margin = new Padding(0, 0, 8, 2), MinimumSize = new Size(width, 0) });
        body.Controls.Add(_grid);
        if (snapshot == null) body.Controls.Add(_addRow);
        _addRow.Click += (_, _) => { AddRow(); Recalc(); };
        body.Controls.Add(_total);
        body.Controls.Add(Hint(server != null
            ? "Names follow your convention: \"Server - Drive\", e.g. \"" + NameFor("E") + "\". The drive letter is only used for the name: " +
              "the disk is attached as-is, to be brought online and formatted in the operating system."
            : "Each volume is named after what you type here."));

        // encryption
        body.Controls.Add(Heading("Encryption"));
        body.Controls.Add(Row(_encrypt, _key));
        body.Controls.Add(_keyNote);
        _encrypt.CheckedChanged += (_, _) => { _key.Enabled = _encrypt.Checked; Recalc(); };
        _key.Items.Add("Loading keys ...");
        _key.SelectedIndex = 0;
        _key.Enabled = false;
        if (snapshot is { Encrypted: true })
        {
            _encrypt.Checked = true;
            _encrypt.Enabled = false;   // a volume from an encrypted snapshot is always encrypted
        }

        // tags
        body.Controls.Add(Heading("Tags"));
        var copyable = _serverTags.Where(t => t.Key != "Name" && !t.Key.StartsWith("aws:", StringComparison.OrdinalIgnoreCase)).ToList();
        if (server != null && copyable.Count > 0)
        {
            _copyTags.Text = $"Copy the server's tags ({string.Join(", ", copyable.Select(t => t.Key))})";
            body.Controls.Add(_copyTags);
        }
        else _copyTags.Checked = false;
        body.Controls.Add(Caption("Extra tags, one per line: Key=Value"));
        body.Controls.Add(_tags);
        body.Controls.Add(Hint("Delete on termination stays off: the disks are kept if the server is ever terminated."));

        // attach
        if (server != null)
        {
            _attach.Text = $"Attach to {Display(server)} once created";
            body.Controls.Add(_attach);
        }
        else
        {
            body.Controls.Add(Heading("Attach"));
            body.Controls.Add(_attachTo);
            _attachTo.Items.Add("(don't attach)");
            _attachTo.SelectedIndex = 0;
        }

        body.Controls.Add(_status);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0, 14, 0, 0) };
        buttons.Controls.AddRange(new Control[] { _create, _cancel });
        body.Controls.Add(buttons);
        Controls.Add(body);

        _create.Click += async (_, _) => await CreateAsync();
        _cancel.Click += (_, _) => { if (_running) _cts.Cancel(); else Close(); };
        CancelButton = _cancel;

        AddRow();
        if (snapshot != null)
        {
            var row = _rows[0];
            row.Gib.Minimum = Math.Max(1, snapshot.SizeGb);
            row.Gib.Value = Math.Max(row.Gib.Minimum, snapshot.SizeGb);
            row.Part.Text = snapshot.Name;
        }
        Recalc();
        Theme.Apply(this, Theme.Current.IsDark);
        Shown += async (_, _) => await LoadAsync();
    }

    // ------------------------------------------------------------------ rows

    private sealed class DiskRow
    {
        public TextBox Part;                       // the drive letter (or a name)
        public NumericUpDown Gib, Iops, Mbps;
        public ComboBox Kind;
        public Label Cost, Status;
        public Button Remove;
        public string VolumeId;
        public bool Done;
    }

    private void AddRow()
    {
        if (_rows.Count >= MaxDisks) return;
        var previous = _rows.LastOrDefault();
        var row = new DiskRow
        {
            Part = new TextBox { Width = 150, Margin = new Padding(0, 2, 8, 2), PlaceholderText = _server != null ? "e.g. E" : "Name" },
            Gib = new NumericUpDown { Width = 90, Minimum = 1, Maximum = 65536, Value = previous != null ? previous.Gib.Value : 100, ThousandsSeparator = true, Margin = new Padding(0, 2, 8, 2) },
            Kind = new ComboBox { Width = 80, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 2, 8, 2) },
            Iops = new NumericUpDown { Width = 90, Minimum = 0, Maximum = 256000, Increment = 500, Value = 3000, ThousandsSeparator = true, Margin = new Padding(0, 2, 8, 2) },
            Mbps = new NumericUpDown { Width = 76, Minimum = 0, Maximum = 1000, Increment = 25, Value = 125, Margin = new Padding(0, 2, 8, 2) },
            Cost = new Label { AutoSize = true, MinimumSize = new Size(80, 0), Margin = new Padding(0, 6, 8, 2) },
            Remove = new Button { Text = "×", Width = 30, Height = 26, Margin = new Padding(0, 2, 8, 2) },
            Status = new Label { AutoSize = true, UseMnemonic = false, MaximumSize = new Size(240, 0), Margin = new Padding(0, 6, 0, 2) },
        };
        row.Kind.Items.AddRange(VolumeRules.Types);
        row.Kind.SelectedItem = previous?.Kind.SelectedItem ?? "gp3";
        if (previous != null) { row.Iops.Value = previous.Iops.Value; row.Mbps.Value = previous.Mbps.Value; }
        row.Part.TextChanged += (_, _) => Recalc();
        row.Gib.ValueChanged += (_, _) => Recalc();
        row.Iops.ValueChanged += (_, _) => Recalc();
        row.Mbps.ValueChanged += (_, _) => Recalc();
        row.Kind.SelectedIndexChanged += (_, _) =>
        {
            string type = row.Kind.SelectedItem as string ?? "gp3";
            // each type's usual starting values
            if (VolumeRules.HasIops(type)) row.Iops.Value = Math.Clamp(row.Iops.Value < VolumeRules.IopsRange(type).Min ? 3000 : row.Iops.Value, 0, 256000);
            if (type == "gp3" && row.Mbps.Value < 125) row.Mbps.Value = 125;
            if (row.Gib.Value < VolumeRules.MinSize(type)) row.Gib.Value = VolumeRules.MinSize(type);
            Recalc();
        };
        row.Remove.Click += (_, _) => RemoveRow(row);
        _rows.Add(row);
        _grid.Controls.AddRange(new Control[] { row.Part, row.Gib, row.Kind, row.Iops, row.Mbps, row.Cost, row.Remove, row.Status });
        if (IsHandleCreated) Theme.Apply(this, Theme.Current.IsDark);
        row.Part.Focus();
    }

    private void RemoveRow(DiskRow row)
    {
        if (_running || _rows.Count <= 1) return;
        _rows.Remove(row);
        foreach (Control c in new Control[] { row.Part, row.Gib, row.Kind, row.Iops, row.Mbps, row.Cost, row.Remove, row.Status })
        {
            _grid.Controls.Remove(c);
            c.Dispose();
        }
        Recalc();
    }

    private NewDisk DiskOf(DiskRow row) => new()
    {
        Label = row.Part.Text.Trim(), Type = row.Kind.SelectedItem as string ?? "gp3", SizeGb = (int)row.Gib.Value,
        Iops = (int)row.Iops.Value, Throughput = (int)row.Mbps.Value, SnapshotId = _snapshot?.Id ?? "",
    };

    /// <summary>"Server - Drive" for a server's disks (your convention), otherwise what was typed.</summary>
    private string NameFor(string label)
    {
        string part = (label ?? "").Trim().TrimEnd(':');
        if (_serverName == null) return part;
        if (part.Length == 1 && char.IsLetter(part[0])) part = part.ToUpperInvariant();
        return part.Length > 0 ? $"{_serverName} - {part}" : _serverName;
    }

    /// <summary>Check every row (limits, names), show costs and the total; enable Create when everything's right.</summary>
    private void Recalc()
    {
        if (_done) return;
        double total = 0;
        bool ok = _rows.Count > 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _rows)
        {
            var d = DiskOf(row);
            row.Iops.Enabled = !_running && VolumeRules.HasIops(d.Type);
            row.Mbps.Enabled = !_running && VolumeRules.HasThroughput(d.Type);
            double cost = VolumeAdmin.MonthlyCost(_prices, d.Type, d.SizeGb, d.Iops, d.Throughput);
            row.Cost.Text = cost > 0 ? $"${cost:N2}" : "";
            total += cost;
            if (row.Done) continue;
            string problem = VolumeRules.Check(d.Type, d.SizeGb, d.Iops, d.Throughput);
            if (problem == null && _server != null && d.Label.Length == 0) problem = "Type the drive letter (or a name)";
            if (problem == null && !names.Add(NameFor(d.Label))) problem = "Same name as another disk";
            row.Status.Text = problem ?? (d.Label.Length > 0 || _server == null ? NameFor(d.Label) : "");
            row.Status.ForeColor = problem != null ? Theme.Current.AccentFore : Theme.Current.Muted;
            if (problem != null) ok = false;
        }
        int count = _rows.Count(r => !r.Done);
        _total.Text = (_rows.Count > 1 ? $"{_rows.Count} disks, {_rows.Sum(r => (int)r.Gib.Value):N0} GiB" : "") +
                      (total > 0 ? $"{(_rows.Count > 1 ? ":  " : "")}about ${total:N2}/month (list price, USD)" : "");
        _addRow.Enabled = !_running && _rows.Count < MaxDisks;
        if (_server == null && _zone.SelectedItem == null) ok = false;
        _create.Text = _snapshot != null ? "Create the volume" : count == 1 ? "Create 1 disk" : $"Create {count} disks";
        _create.Enabled = ok && !_running && count > 0;
        foreach (var row in _rows) row.Remove.Enabled = !_running && _rows.Count > 1;
    }

    // ------------------------------------------------------------------ loading (prices, keys, zones)

    private async Task LoadAsync()
    {
        try
        {
            var pricesTask = VolumeAdmin.PricesAsync(_profile, _region, _cts.Token);
            var keysTask = VolumeAdmin.KeysAsync(_profile, _region, _cts.Token);
            if (_server == null)
            {
                foreach (var z in await VolumeAdmin.ZonesAsync(_profile, _region, _cts.Token)) _zone.Items.Add(z);
                if (_zone.Items.Count > 0) _zone.SelectedIndex = 0;
            }
            _prices = await pricesTask;
            var (keys, couldList) = await keysTask;
            _key.Items.Clear();
            foreach (var k in keys) _key.Items.Add(k);
            // the key used last time in this region, else the account's EBS default, else AWS managed
            string last = _settings.LastKmsKeys.TryGetValue(_region, out var l) ? l : null;
            var pick = last != null ? keys.FirstOrDefault(k => k.KeyId == last) : null;
            if (pick == null && !string.IsNullOrEmpty(last))
            {
                pick = new KeyChoice { KeyId = last, Label = last };   // pasted last time: offered again
                _key.Items.Add(pick);
            }
            _key.SelectedItem = pick ?? keys.FirstOrDefault(k => k.IsAccountDefault) ?? keys[0];
            _key.Enabled = _encrypt.Checked;
            _keyNote.Text = couldList
                ? "Your customer-managed keys, and AWS managed. The key selected is the one you used last in this region, or the account's EBS default."
                : "Your role can't list KMS keys, so only AWS managed is listed: paste a key ARN, key ID or alias (alias/...) to use another key.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = "Couldn't load everything: " + Aws.Describe(ex); }
        Recalc();
    }

    private async Task LoadServersInZoneAsync()
    {
        if (_zone.SelectedItem is not string zone) return;
        _attachTo.Items.Clear();
        _attachTo.Items.Add("(don't attach)");
        _attachTo.SelectedIndex = 0;
        try
        {
            foreach (var i in await VolumeAdmin.InstancesInZoneAsync(_profile, _region, zone, _cts.Token)) _attachTo.Items.Add(i);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = "Couldn't list the servers in that zone: " + Aws.Describe(ex); }
        Recalc();
    }

    // ------------------------------------------------------------------ creating

    /// <summary>The key to use: a listed choice (empty = AWS managed), or a pasted ARN / key ID / alias.</summary>
    private string KeyId() => _key.SelectedItem is KeyChoice k ? k.KeyId : _key.Text.Trim();

    private async Task CreateAsync()
    {
        if (_running || _done) return;
        string zone = _server != null ? _server.Zone : _zone.SelectedItem as string;
        var attachTo = _server ?? (_attachTo.SelectedItem as InstanceChoice);
        if (_server != null && !_attach.Checked) attachTo = null;
        bool encrypt = _encrypt.Checked;
        string keyId = encrypt ? KeyId() : "";
        var tags = new List<KeyValuePair<string, string>>();
        if (_copyTags.Checked) tags.AddRange(_serverTags.Where(t => t.Key != "Name" && !t.Key.StartsWith("aws:", StringComparison.OrdinalIgnoreCase)));
        foreach (var t in VolumeAdmin.ParseTags(_tags.Text))
        {
            tags.RemoveAll(x => x.Key == t.Key);   // a typed tag wins over a copied one
            tags.Add(t);
        }
        if (encrypt) { _settings.LastKmsKeys[_region] = keyId; _settings.Save(); }

        _running = true;
        _cancel.Text = "Stop";
        foreach (var c in new Control[] { _zone, _attachTo, _attach, _encrypt, _key, _copyTags, _tags }) c.Enabled = false;
        foreach (var row in _rows) foreach (var c in new Control[] { row.Part, row.Gib, row.Kind, row.Iops, row.Mbps }) c.Enabled = false;
        Recalc();
        var used = attachTo != null ? new HashSet<string>(attachTo.Devices, StringComparer.OrdinalIgnoreCase) : null;
        int failed = 0;
        foreach (var row in _rows.Where(r => !r.Done))
        {
            var disk = DiskOf(row);
            string name = NameFor(disk.Label);
            try
            {
                SetRow(row, "Creating ...", false);
                row.VolumeId = await VolumeAdmin.CreateAsync(_profile, _region, zone, disk, name, encrypt, keyId, tags, _cts.Token);
                CreatedVolumes.Add(row.VolumeId);
                if (attachTo != null)
                {
                    string device = VolumeAdmin.NextDevice(attachTo.Windows, used);
                    SetRow(row, $"Attaching as {device} ...", false);
                    await VolumeAdmin.AttachAsync(_profile, _region, row.VolumeId, attachTo.Id, device, _cts.Token);
                    SetRow(row, $"Ready: {row.VolumeId}, attached as {device}", false, good: true);
                }
                else SetRow(row, $"Ready: {row.VolumeId}", false, good: true);
                row.Done = true;
            }
            catch (OperationCanceledException)
            {
                SetRow(row, row.VolumeId != null ? $"Stopped: {row.VolumeId} was created, not attached" : "Not created (stopped)", true);
                break;
            }
            catch (Exception ex)
            {
                failed++;
                SetRow(row, (row.VolumeId != null ? $"{row.VolumeId} created, but: " : "") + Aws.Describe(ex), true);
                Log.Write($"Add disks: {name}: {ex}");
            }
        }
        _running = false;
        _done = true;
        _cancel.Text = "Close";
        _create.Visible = false;
        CancelButton = _cancel;
        foreach (var row in _rows) row.Remove.Visible = false;
        _addRow.Visible = false;
        int made = CreatedVolumes.Count;
        _status.ForeColor = failed > 0 ? Theme.Current.AccentFore : Theme.Current.Text;
        _status.Text = made == 0 ? "No disks were created." :
            $"{made} disk{(made == 1 ? "" : "s")} created" + (attachTo != null ? $" and attached to {Display(attachTo)}" : "") +
            (failed > 0 ? $"; {failed} didn't work (see above)." : ".") +
            (attachTo != null ? " Bring them online and format them in the operating system." : "");
    }

    private void SetRow(DiskRow row, string text, bool problem, bool good = false)
    {
        row.Status.Text = text;
        row.Status.ForeColor = problem ? Theme.Current.AccentFore : good ? Theme.Current.Good : Theme.Current.Text;
    }

    // ------------------------------------------------------------------ helpers

    private static string Display(InstanceChoice i) => i.Name.Length > 0 ? i.Name : i.Id;
    private static Label Caption(string text, int top = 10) => new() { Text = text, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, top, 0, 3) };
    private static Label Heading(string text) => new() { Text = text, AutoSize = true, UseMnemonic = false, Font = new Font("Segoe UI Semibold", 10.5F), Margin = new Padding(0, 16, 0, 6) };
    private static Label Hint(string text) => new() { Text = text, AutoSize = true, UseMnemonic = false, Tag = "muted", MaximumSize = new Size(680, 0), Margin = new Padding(0, 4, 0, 0) };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        row.Controls.AddRange(controls);
        return row;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_running) { e.Cancel = true; _cts.Cancel(); return; }   // finish the disk in progress, then close
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cts.Dispose();
        base.Dispose(disposing);
    }
}
