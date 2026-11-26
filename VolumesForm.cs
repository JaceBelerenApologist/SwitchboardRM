namespace SwitchboardRM;

/// <summary>
/// AWS > Volumes and snapshots: every EBS volume and snapshot in the scanned regions. Volumes: filter, new volumes,
/// modify, attach, detach, snapshot, name and tags, delete (unattached only). Snapshots: new volume from one, copy to
/// another region, name and tags, delete (not while an AMI needs it). Cleanup: unattached volumes (newest first, for
/// disks made by mistake) and snapshots whose volume is gone, with what they cost.
/// </summary>
internal sealed class VolumesForm : Form
{
    private const int PageWidth = 900;

    private readonly string _profile;
    private readonly List<string> _regions;
    private readonly AppSettings _settings;
    private readonly CancellationTokenSource _cts = new();
    private List<VolumeRow> _volumes = new();
    private List<SnapshotRow> _snapshots = new();
    private bool _busy;

    private readonly FlowLayoutPanel _nav = new() { Dock = DockStyle.Left, Width = 190, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12, 14, 8, 8) };
    private readonly Panel _host = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(20, 14, 20, 14) };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(20, 8, 20, 0), UseMnemonic = false };
    private readonly List<(Button Button, Func<Control> Build)> _pages = new();
    private readonly Dictionary<int, Control> _built = new();

    // volumes
    private readonly TextBox _volSearch = new() { Width = 260, PlaceholderText = "Search name, ID, server, tag ..." };
    private readonly ComboBox _volFilter = new() { Width = 230, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(8, 0, 0, 0) };
    private readonly ListView _volList = MakeList(PageWidth, 380);
    private readonly Label _volTotals = new() { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 6, 0, 0) };

    // snapshots
    private readonly TextBox _snapSearch = new() { Width = 260, PlaceholderText = "Search name, ID, volume, description ..." };
    private readonly ComboBox _snapFilter = new() { Width = 230, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(8, 0, 0, 0) };
    private readonly ListView _snapList = MakeList(PageWidth, 380);
    private readonly Label _snapTotals = new() { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 6, 0, 0) };

    // cleanup
    private readonly ListView _cleanVolumes = MakeList(PageWidth, 200);
    private readonly ListView _cleanSnaps = MakeList(PageWidth, 200);
    private readonly Label _cleanVolTotals = new() { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 6, 0, 0) };
    private readonly Label _cleanSnapTotals = new() { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 6, 0, 0) };

    public VolumesForm(string profile, IEnumerable<string> regions, AppSettings settings)
    {
        _profile = profile;
        _regions = regions.Distinct().ToList();
        _settings = settings;
        Text = "Volumes and snapshots (" + string.Join(", ", _regions) + ")";
        if (AppIcon.Icon != null) Icon = AppIcon.Icon;
        Font = new Font("Segoe UI", 9.5F);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        ClientSize = new Size(LogicalToDeviceUnits(1160), LogicalToDeviceUnits(720));
        MinimumSize = new Size(LogicalToDeviceUnits(900), LogicalToDeviceUnits(520));

        foreach (var (title, width) in new[] { ("Name", 170), ("Size", 70), ("Type", 50), ("IOPS", 60), ("MB/s", 50), ("State", 72), ("Attached to", 170), ("Zone", 100), ("Encrypted", 70), ("Created", 120), ("$/month", 70), ("Volume ID", 170) })
            _volList.Columns.Add(title, width);
        foreach (var (title, width) in new[] { ("Name", 190), ("Snapshot ID", 175), ("Size", 60), ("Started", 120), ("Age", 60), ("State", 80), ("Source volume", 190), ("Used by", 160), ("Encrypted", 70), ("Description", 220), ("Region", 100), ("$/month (max)", 90) })
            _snapList.Columns.Add(title, width);
        foreach (var (title, width) in new[] { ("Name", 200), ("Size", 70), ("Type", 50), ("Created", 120), ("Created by", 200), ("Zone", 100), ("$/month", 70), ("Volume ID", 170) })
            _cleanVolumes.Columns.Add(title, width);
        foreach (var (title, width) in new[] { ("Name", 200), ("Snapshot ID", 175), ("Size", 60), ("Started", 120), ("Description", 260), ("Region", 100), ("$/month (max)", 90) })
            _cleanSnaps.Columns.Add(title, width);

        _volFilter.Items.AddRange(new object[] { "All volumes", "Unattached", "Attached", "gp2 (cheaper as gp3)", "Unencrypted", "No snapshot in 30 days" });
        _volFilter.SelectedIndex = 0;
        _snapFilter.Items.AddRange(new object[] { "All snapshots", "Not used by an image", "Source volume deleted", "Older than 30 days", "Older than 90 days", "Older than a year" });
        _snapFilter.SelectedIndex = 0;
        _volSearch.TextChanged += (_, _) => FillVolumes();
        _volFilter.SelectedIndexChanged += (_, _) => FillVolumes();
        _snapSearch.TextChanged += (_, _) => FillSnapshots();
        _snapFilter.SelectedIndexChanged += (_, _) => FillSnapshots();
        _volList.DoubleClick += async (_, _) => await ModifyAsync();
        _snapList.DoubleClick += async (_, _) => await VolumeFromSnapshotAsync();

        AddPage("Volumes", BuildVolumes);
        AddPage("Snapshots", BuildSnapshots);
        AddPage("Cleanup", BuildCleanup);

        var close = new Button { Text = "Close", AutoSize = true, MinimumSize = new Size(96, 32), DialogResult = DialogResult.Cancel };
        CancelButton = close;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 52, Padding = new Padding(16, 8, 16, 8) };
        buttons.Controls.Add(close);
        Controls.Add(_host);
        Controls.Add(_nav);
        Controls.Add(_status);
        Controls.Add(buttons);
        Theme.Apply(this, Theme.Current.IsDark);
        ShowPage(0);
        Shown += async (_, _) => await LoadAsync();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
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
        _host.SuspendLayout();
        _host.Controls.Clear();
        _host.Controls.Add(page);
        Theme.Apply(this, Theme.Current.IsDark);   // the theme only reaches controls in the window: this page now is
        for (int i = 0; i < _pages.Count; i++)
        {
            var b = _pages[i].Button;
            b.BackColor = i == index ? Theme.Current.Hover : Theme.Current.Back;
            b.ForeColor = Theme.Current.Text;
            b.Font = Theme.CreateFont(Theme.UiFont, Font.Size, i == index ? FontStyle.Bold : FontStyle.Regular);
        }
        _host.ResumeLayout();
        _host.AutoScrollPosition = new Point(0, 0);
    }

    private Control BuildVolumes()
    {
        var refresh = Btn("Refresh", 90, async () => await LoadAsync());
        return Page(
            Heading("Volumes", first: true),
            Row(_volSearch, _volFilter, Spacer(12), refresh),
            _volList, _volTotals,
            Row(Btn("New volumes ...", 130, async () => await NewVolumesAsync(), primary: true),
                Btn("Modify ...", 90, async () => await ModifyAsync()),
                Btn("Attach ...", 90, async () => await AttachAsync(SelectedVolumes().FirstOrDefault())),
                Btn("Detach ...", 90, async () => await DetachAsync()),
                Btn("Snapshot now", 110, async () => await SnapshotAsync()),
                Btn("Name and tags ...", 130, async () => await VolumeTagsAsync()),
                Btn("Delete ...", 90, async () => await DeleteVolumesAsync(SelectedVolumes()))),
            Note("Costs are estimates from AWS's list prices (USD): storage, plus gp3 IOPS and throughput above the free 3,000 / " +
                 "125 and io1/io2 provisioned IOPS. Double-click a volume to modify it. Delete works on unattached volumes only."));
    }

    private Control BuildSnapshots()
    {
        var refresh = Btn("Refresh", 90, async () => await LoadAsync());
        return Page(
            Heading("Snapshots", first: true),
            Row(_snapSearch, _snapFilter, Spacer(12), refresh),
            _snapList, _snapTotals,
            Row(Btn("New volume from snapshot ...", 200, async () => await VolumeFromSnapshotAsync(), primary: true),
                Btn("Copy to another region ...", 180, async () => await CopySnapshotAsync()),
                Btn("Name and tags ...", 130, async () => await SnapshotTagsAsync()),
                Btn("Delete ...", 90, async () => await DeleteSnapshotsAsync(SelectedSnapshots()))),
            Note("Snapshots are incremental, so each costs at most its size at the snapshot price, usually much less. A snapshot " +
                 "an image (AMI) needs can't be deleted until the image is deregistered (Server window > Images and snapshots)."));
    }

    private Control BuildCleanup()
    {
        return Page(
            Heading("Unattached volumes", first: true),
            Note("Disks attached to no server: still charged. Newest first, so a disk created by mistake is at the top."),
            _cleanVolumes, _cleanVolTotals,
            Row(Btn("Delete ...", 90, async () => await DeleteVolumesAsync(Selected<VolumeRow>(_cleanVolumes))),
                Btn("Attach ...", 90, async () => await AttachAsync(Selected<VolumeRow>(_cleanVolumes).FirstOrDefault()))),
            Heading("Snapshots whose volume is gone"),
            Note("Their source volume no longer exists and no image needs them: often left over after a disk was deleted."),
            _cleanSnaps, _cleanSnapTotals,
            Row(Btn("Delete ...", 90, async () => await DeleteSnapshotsAsync(Selected<SnapshotRow>(_cleanSnaps)))));
    }

    // ------------------------------------------------------------------ loading and filling

    private async Task LoadAsync()
    {
        if (_busy) return;
        _busy = true;
        UseWaitCursor = true;
        try
        {
            Status("Reading volumes from " + string.Join(", ", _regions) + " ...");
            _volumes = await VolumeAdmin.VolumesAsync(_profile, _regions, _cts.Token);
            Status("Reading snapshots ...");
            _snapshots = await VolumeAdmin.SnapshotsAsync(_profile, _regions, _volumes, _cts.Token);
            FillVolumes();
            FillSnapshots();
            FillCleanup();
            Status($"{_volumes.Count} volumes and {_snapshots.Count} snapshots, read {DateTime.Now:HH:mm}.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status("Couldn't read the volumes: " + Aws.Describe(ex), accent: true); }
        finally { _busy = false; UseWaitCursor = false; }
    }

    private static string Date(DateTime t) => t == DateTime.MinValue ? "" : t.ToString("yyyy-MM-dd HH:mm");
    private static string Money(double v) => v > 0 ? $"${v:N2}" : "";

    private string Attached(VolumeRow v) =>
        !v.Attached ? "" : (v.InstanceName.Length > 0 ? v.InstanceName : v.InstanceId) + (v.Device.Length > 0 ? $" ({v.Device})" : "");

    private DateTime LastSnapshot(VolumeRow v) =>
        _snapshots.Where(s => s.VolumeId == v.Id).Select(s => s.Started).DefaultIfEmpty(DateTime.MinValue).Max();

    private void FillVolumes()
    {
        string q = _volSearch.Text.Trim();
        var shown = _volumes.Where(v => _volFilter.SelectedIndex switch
        {
            1 => !v.Attached,
            2 => v.Attached,
            3 => v.Type == "gp2",
            4 => !v.Encrypted,
            5 => LastSnapshot(v) < DateTime.Now.AddDays(-30),
            _ => true,
        }).Where(v => q.Length == 0 || new[] { v.Name, v.Id, v.InstanceName, v.InstanceId, v.Zone, v.Type }
                          .Concat(v.Tags.Select(t => $"{t.Key}={t.Value}")).Any(x => x.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        Fill(_volList, shown, v => new[]
        {
            v.Name, $"{v.SizeGb:N0} GiB", v.Type, v.Iops > 0 ? $"{v.Iops:N0}" : "", v.Throughput > 0 ? $"{v.Throughput}" : "",
            v.State, Attached(v), v.Zone, v.Encrypted ? "yes" : "no", Date(v.Created), Money(v.MonthlyCost), v.Id,
        });
        double total = shown.Sum(v => v.MonthlyCost), unattached = shown.Where(v => !v.Attached).Sum(v => v.MonthlyCost);
        _volTotals.Text = $"{shown.Count} volumes, {shown.Sum(v => v.SizeGb):N0} GiB" + (total > 0 ? $", about ${total:N2}/month" : "") +
                          (unattached > 0 ? $"  ·  unattached: {shown.Count(v => !v.Attached)}, about ${unattached:N2}/month" : "");
    }

    private void FillSnapshots()
    {
        string q = _snapSearch.Text.Trim();
        var shown = _snapshots.Where(s => _snapFilter.SelectedIndex switch
        {
            1 => s.UsedBy.Count == 0,
            2 => !s.SourceExists,
            3 => s.Started < DateTime.Now.AddDays(-30),
            4 => s.Started < DateTime.Now.AddDays(-90),
            5 => s.Started < DateTime.Now.AddYears(-1),
            _ => true,
        }).Where(s => q.Length == 0 || new[] { s.Name, s.Id, s.VolumeId, s.SourceName, s.Description }
                          .Any(x => x.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
        Fill(_snapList, shown, s => new[]
        {
            s.Name, s.Id, $"{s.SizeGb:N0} GiB", Date(s.Started), s.Started == DateTime.MinValue ? "" : $"{(DateTime.Now - s.Started).Days} d",
            s.State == "completed" ? "completed" : $"{s.State} {s.Progress}", SourceText(s), string.Join(", ", s.UsedBy),
            s.Encrypted ? "yes" : "no", s.Description, s.Region, Money(s.MonthlyCostMax),
        });
        _snapTotals.Text = $"{shown.Count} snapshots, {shown.Sum(s => s.SizeGb):N0} GiB of source volumes";
    }

    private static string SourceText(SnapshotRow s) =>
        s.SourceExists ? (s.SourceName.Length > 0 ? $"{s.SourceName} ({s.VolumeId})" : s.VolumeId)
                       : s.VolumeId is "" or "vol-ffffffff" ? "(none: copied or from an image)" : $"{s.VolumeId} (deleted)";

    private void FillCleanup()
    {
        var unattached = _volumes.Where(v => !v.Attached && v.State == "available").OrderByDescending(v => v.Created).ToList();
        Fill(_cleanVolumes, unattached, v => new[]
        {
            v.Name, $"{v.SizeGb:N0} GiB", v.Type, Date(v.Created), v.CreatedBy, v.Zone, Money(v.MonthlyCost), v.Id,
        });
        double vc = unattached.Sum(v => v.MonthlyCost);
        _cleanVolTotals.Text = unattached.Count == 0 ? "No unattached volumes." :
            $"{unattached.Count} unattached, {unattached.Sum(v => v.SizeGb):N0} GiB" + (vc > 0 ? $": about ${vc:N2}/month" : "");

        var orphans = _snapshots.Where(s => !s.SourceExists && s.UsedBy.Count == 0 && s.VolumeId is not ("" or "vol-ffffffff"))
                                .OrderBy(s => s.Started).ToList();
        Fill(_cleanSnaps, orphans, s => new[] { s.Name, s.Id, $"{s.SizeGb:N0} GiB", Date(s.Started), s.Description, s.Region, Money(s.MonthlyCostMax) });
        double sc = orphans.Sum(s => s.MonthlyCostMax);
        _cleanSnapTotals.Text = orphans.Count == 0 ? "None." :
            $"{orphans.Count} snapshots" + (sc > 0 ? $": at most about ${sc:N2}/month (usually much less)" : "");
    }

    /// <summary>Fill a list, keeping the selection and the sort.</summary>
    private static void Fill<T>(ListView list, IEnumerable<T> rows, Func<T, string[]> cells) where T : class
    {
        var selected = list.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).ToHashSet();
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var row in rows)
        {
            var item = new ListViewItem(cells(row)) { Tag = row };
            list.Items.Add(item);
            if (selected.Any(s => ReferenceEquals(s, row) || SameId(s, row))) item.Selected = true;
        }
        list.EndUpdate();
    }

    private static bool SameId(object a, object b) =>
        (a, b) switch
        {
            (VolumeRow x, VolumeRow y) => x.Id == y.Id,
            (SnapshotRow x, SnapshotRow y) => x.Id == y.Id,
            _ => false,
        };

    private List<VolumeRow> SelectedVolumes() => Selected<VolumeRow>(_volList);
    private List<SnapshotRow> SelectedSnapshots() => Selected<SnapshotRow>(_snapList);
    private static List<T> Selected<T>(ListView list) where T : class => list.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag as T).Where(x => x != null).ToList();

    // ------------------------------------------------------------------ volume actions

    private string PickRegion(string purpose)
    {
        if (_regions.Count == 1) return _regions[0];
        using var f = new ChoiceForm(purpose, "Region", _regions.Cast<object>(), "Next");
        return f.ShowDialog(this) == DialogResult.OK ? f.Choice as string : null;
    }

    private async Task NewVolumesAsync()
    {
        if (_busy) return;
        string region = PickRegion("New volumes");
        if (region == null) return;
        using var f = AddDisksForm.Standalone(_profile, region, _settings);
        f.ShowDialog(this);
        if (f.CreatedVolumes.Count > 0) await LoadAsync();
    }

    private async Task ModifyAsync()
    {
        if (_busy) return;
        if (SelectedVolumes() is not { Count: 1 } one) { Status("Select one volume to modify.", accent: true); return; }
        var v = one[0];
        var prices = await VolumeAdmin.PricesAsync(_profile, v.Region, _cts.Token);
        using var f = new ModifyVolumeForm(v, prices);
        if (f.ShowDialog(this) != DialogResult.OK) return;
        await Run($"Modifying {v.Label} ...", async () =>
        {
            await VolumeAdmin.ModifyAsync(_profile, v.Region, v.Id, f.SizeGb, f.NewType, f.Iops, f.Throughput, _cts.Token);
            return $"{v.Label}: the change has started. AWS applies it in the background (minutes to hours for large disks); the volume stays usable.";
        });
    }

    private async Task AttachAsync(VolumeRow v)
    {
        if (_busy) return;
        if (v == null) { Status("Select a volume to attach.", accent: true); return; }
        if (v.Attached || v.State != "available") { Status($"{v.Label} is already attached (or not available).", accent: true); return; }
        List<InstanceChoice> servers;
        try { servers = await VolumeAdmin.InstancesInZoneAsync(_profile, v.Region, v.Zone, _cts.Token); }
        catch (Exception ex) { Status("Couldn't list the servers: " + Aws.Describe(ex), accent: true); return; }
        if (servers.Count == 0) { Status($"No servers in {v.Zone} to attach {v.Label} to (a volume attaches only within its zone).", accent: true); return; }
        using var f = new ChoiceForm($"Attach {v.Label}", $"Server in {v.Zone}", servers.Cast<object>(), "Attach",
            "Attached as the server's next free device. Bring it online in the operating system afterwards.");
        if (f.ShowDialog(this) != DialogResult.OK || f.Choice is not InstanceChoice server) return;
        await Run($"Attaching {v.Label} to {server} ...", async () =>
        {
            string device = VolumeAdmin.NextDevice(server.Windows, new HashSet<string>(server.Devices, StringComparer.OrdinalIgnoreCase));
            await VolumeAdmin.AttachAsync(_profile, v.Region, v.Id, server.Id, device, _cts.Token);
            return $"{v.Label} attached to {server} as {device}.";
        });
    }

    private async Task DetachAsync()
    {
        if (_busy) return;
        var chosen = SelectedVolumes().Where(v => v.Attached).ToList();
        if (chosen.Count == 0) { Status("Select attached volumes to detach.", accent: true); return; }
        if (MessageBox.Show(this, "Detach these volumes?\n\n" + string.Join("\n", chosen.Select(v => $"{v.Label}  from  {Attached(v)}")) +
                                  "\n\nTake them offline in the operating system first if they're in use. The volumes are kept (and still charged). " +
                                  "A server's system disk can't be detached while the server runs.",
                "Detach volumes", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
        await Run("Detaching ...", async () =>
        {
            var problems = new List<string>();
            foreach (var v in chosen)
            {
                try { await VolumeAdmin.DetachAsync(_profile, v.Region, v.Id, _cts.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { problems.Add($"{v.Label}: {Aws.Describe(ex)}"); }
            }
            return Summary(chosen.Count - problems.Count, "detached", problems);
        });
    }

    private async Task SnapshotAsync()
    {
        if (_busy) return;
        var chosen = SelectedVolumes();
        if (chosen.Count == 0) { Status("Select volumes to snapshot.", accent: true); return; }
        if (MessageBox.Show(this, "Snapshot these volumes now?\n\n" + string.Join("\n", chosen.Select(v => v.Label)) +
                                  "\n\nEach snapshot is named after its volume and today's date. The volumes stay in use.",
                "Snapshot now", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        await Run("Starting snapshots ...", async () =>
        {
            var problems = new List<string>();
            foreach (var v in chosen)
            {
                try { await VolumeAdmin.SnapshotAsync(_profile, v.Region, v, $"Switchboard: {v.Label}", wait: false, null, _cts.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { problems.Add($"{v.Label}: {Aws.Describe(ex)}"); }
            }
            return Summary(chosen.Count - problems.Count, "snapshot(s) started (they finish in the background)", problems);
        });
    }

    private async Task VolumeTagsAsync()
    {
        if (_busy) return;
        if (SelectedVolumes() is not { Count: 1 } one) { Status("Select one volume.", accent: true); return; }
        var v = one[0];
        using var f = new TagsForm(v.Label, v.Name, v.Tags);
        if (f.ShowDialog(this) != DialogResult.OK) return;
        await Run("Saving tags ...", async () =>
        {
            await VolumeAdmin.SetTagsAsync(_profile, v.Region, v.Id, f.NameValue, f.TagsValue, v.Tags, _cts.Token);
            return $"Tags of {v.Id} saved.";
        });
    }

    private async Task DeleteVolumesAsync(List<VolumeRow> chosen)
    {
        if (_busy) return;
        if (chosen.Count == 0) { Status("Select volumes to delete.", accent: true); return; }
        var attached = chosen.Where(v => v.Attached || v.State != "available").ToList();
        if (attached.Count > 0)
        {
            Status("Only unattached volumes can be deleted: detach these first: " + string.Join(", ", attached.Select(v => v.Label)), accent: true);
            return;
        }
        using var f = new TypedConfirmForm("Delete volumes", $"Delete {chosen.Count} volume(s) permanently? Their data is gone unless snapshotted.",
            chosen.Select(v => $"{v.Label}   {v.SizeGb:N0} GiB {v.Type}   {v.Zone}" + (v.MonthlyCost > 0 ? $"   ${v.MonthlyCost:N2}/month" : "")),
            "delete", "Take a final snapshot of each first (kept, and cheaper than the volume)");
        if (f.ShowDialog(this) != DialogResult.OK) return;
        bool snapshotFirst = f.OptionChecked;
        await Run("Deleting ...", async () =>
        {
            var problems = new List<string>();
            int done = 0;
            foreach (var v in chosen)
            {
                try
                {
                    if (snapshotFirst)
                    {
                        Status($"Final snapshot of {v.Label} ...");
                        await VolumeAdmin.SnapshotAsync(_profile, v.Region, v, $"Switchboard: final snapshot of {v.Label} before deleting it",
                            wait: true, new Progress<string>(m => Status(m)), _cts.Token);
                    }
                    Status($"Deleting {v.Label} ...");
                    await VolumeAdmin.DeleteVolumeAsync(_profile, v.Region, v.Id, _cts.Token);
                    done++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { problems.Add($"{v.Label}: {Aws.Describe(ex)}"); }
            }
            return Summary(done, "deleted" + (snapshotFirst ? " (each after a final snapshot)" : ""), problems);
        });
    }

    // ------------------------------------------------------------------ snapshot actions

    private async Task VolumeFromSnapshotAsync()
    {
        if (_busy) return;
        if (SelectedSnapshots() is not { Count: 1 } one) { Status("Select one snapshot.", accent: true); return; }
        var s = one[0];
        if (s.State != "completed") { Status($"{s.Label} isn't complete yet ({s.Progress}).", accent: true); return; }
        using var f = AddDisksForm.Standalone(_profile, s.Region, _settings, s);
        f.ShowDialog(this);
        if (f.CreatedVolumes.Count > 0) await LoadAsync();
    }

    private sealed record RegionPick(string Code, string Place)
    {
        public override string ToString() => Place.Length > 0 ? $"{Code}  ({Place})" : Code;
    }

    private async Task CopySnapshotAsync()
    {
        if (_busy) return;
        if (SelectedSnapshots() is not { Count: 1 } one) { Status("Select one snapshot to copy.", accent: true); return; }
        var s = one[0];
        if (s.State != "completed") { Status($"{s.Label} isn't complete yet.", accent: true); return; }
        var regions = Aws.AllRegions().Where(r => r.Code != s.Region).Select(r => (object)new RegionPick(r.Code, r.Place)).ToList();
        using var f = new ChoiceForm($"Copy {s.Label}", "To region", regions, "Copy",
            s.Encrypted ? "The copy is encrypted with that region's default EBS key." : null);
        if (f.ShowDialog(this) != DialogResult.OK || f.Choice is not RegionPick to) return;
        await Run($"Copying {s.Label} to {to.Code} ...", async () =>
        {
            string id = await VolumeAdmin.CopySnapshotAsync(_profile, s, to.Code, _cts.Token);
            return $"Copying to {to.Code} as {id}: it finishes in the background." +
                   (_regions.Contains(to.Code) ? "" : " (That region isn't in your scanned regions, so it won't show here.)");
        });
    }

    private async Task SnapshotTagsAsync()
    {
        if (_busy) return;
        if (SelectedSnapshots() is not { Count: 1 } one) { Status("Select one snapshot.", accent: true); return; }
        var s = one[0];
        using var f = new TagsForm(s.Label, s.Name, s.Tags);
        if (f.ShowDialog(this) != DialogResult.OK) return;
        await Run("Saving tags ...", async () =>
        {
            await VolumeAdmin.SetTagsAsync(_profile, s.Region, s.Id, f.NameValue, f.TagsValue, s.Tags, _cts.Token);
            return $"Tags of {s.Id} saved.";
        });
    }

    private async Task DeleteSnapshotsAsync(List<SnapshotRow> chosen)
    {
        if (_busy) return;
        if (chosen.Count == 0) { Status("Select snapshots to delete.", accent: true); return; }
        var needed = chosen.Where(s => s.UsedBy.Count > 0).ToList();
        if (needed.Count > 0)
        {
            MessageBox.Show(this, "These snapshots are used by images (AMIs) and can't be deleted until the images are deregistered:\n\n" +
                                  string.Join("\n", needed.Select(s => $"{s.Label}  →  {string.Join(", ", s.UsedBy)}")),
                "Delete snapshots", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var f = new TypedConfirmForm("Delete snapshots", $"Delete {chosen.Count} snapshot(s) permanently?",
            chosen.Select(s => $"{s.Label}   {s.SizeGb:N0} GiB   {Date(s.Started)}   {SourceText(s)}"), "delete");
        if (f.ShowDialog(this) != DialogResult.OK) return;
        await Run("Deleting ...", async () =>
        {
            var problems = new List<string>();
            foreach (var s in chosen)
            {
                try { await VolumeAdmin.DeleteSnapshotAsync(_profile, s.Region, s.Id, _cts.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException) { problems.Add($"{s.Label}: {Aws.Describe(ex)}"); }
            }
            return Summary(chosen.Count - problems.Count, "deleted", problems);
        });
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Run an action (busy, wait cursor), show its result, then read everything again.</summary>
    private async Task Run(string doing, Func<Task<string>> action)
    {
        if (_busy) return;
        _busy = true;
        UseWaitCursor = true;
        string result = null;
        bool problem = false;
        try
        {
            Status(doing);
            result = await action();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { result = Aws.Describe(ex); problem = true; }
        finally { _busy = false; UseWaitCursor = false; }
        await LoadAsync();
        if (result != null) Status(result, accent: problem || result.Contains("didn't work"));
    }

    private static string Summary(int done, string what, List<string> problems) =>
        $"{done} {what}." + (problems.Count > 0 ? $" {problems.Count} didn't work: " + string.Join("; ", problems) : "");

    private void Status(string text, bool accent = false)
    {
        _status.ForeColor = accent ? Theme.Current.AccentFore : Theme.Current.Muted;
        _status.Text = text;
    }

    private static ListView MakeList(int width, int height)
    {
        var list = new ListView { Width = width, Height = height, View = View.Details, FullRowSelect = true, MultiSelect = true, HideSelection = false };
        var sorter = new ColumnSorter();
        list.ColumnClick += (_, e) =>
        {
            // attached on the first click only: with no column chosen it would call every row equal, and Windows' sort
            // could then shuffle them (Cleanup is deliberately newest first)
            list.ListViewItemSorter ??= sorter;
            sorter.Toggle(e.Column);
            list.Sort();
        };
        return list;
    }

    /// <summary>Sort by the clicked column (again: reversed); numbers, sizes, money and dates by value.</summary>
    private sealed class ColumnSorter : System.Collections.IComparer
    {
        private int _column = -1;
        private bool _descending;

        public void Toggle(int column)
        {
            _descending = column == _column && !_descending;
            _column = column;
        }

        public int Compare(object x, object y)
        {
            if (_column < 0 || x is not ListViewItem a || y is not ListViewItem b) return 0;
            string sa = _column < a.SubItems.Count ? a.SubItems[_column].Text : "", sb = _column < b.SubItems.Count ? b.SubItems[_column].Text : "";
            int result = TryNumber(sa, out double na) && TryNumber(sb, out double nb) ? na.CompareTo(nb)
                       : DateTime.TryParse(sa, out var da) && DateTime.TryParse(sb, out var db) ? da.CompareTo(db)
                       : string.Compare(sa, sb, StringComparison.OrdinalIgnoreCase);
            return _descending ? -result : result;
        }

        private static bool TryNumber(string s, out double value)
        {
            string t = s.Replace("$", "").Replace(",", "").Replace("GiB", "").Replace(" d", "").Trim();
            return double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
        }
    }

    private Button Btn(string text, int width, Func<Task> click, bool primary = false)
    {
        var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(width, 30), Margin = new Padding(0, 8, 8, 0) };
        if (primary) b.Tag = "primary";
        b.Click += async (_, _) => await click();
        return b;
    }

    private static Control Spacer(int width) => new Panel { Width = width, Height = 1, Margin = new Padding(0) };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
        row.Controls.AddRange(controls);
        return row;
    }

    private static FlowLayoutPanel Page(params Control[] controls)
    {
        var page = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        page.Controls.AddRange(controls);
        return page;
    }

    private static Label Heading(string text, bool first = false) =>
        new() { Text = text, AutoSize = true, UseMnemonic = false, Font = new Font("Segoe UI Semibold", 10.5F), Margin = new Padding(0, first ? 0 : 16, 0, 6) };

    private static Label Note(string text) =>
        new() { Text = text, AutoSize = true, UseMnemonic = false, Tag = "muted", MaximumSize = new Size(PageWidth, 0), Margin = new Padding(0, 4, 0, 6) };

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cts.Cancel();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cts.Dispose();
        base.Dispose(disposing);
    }
}
