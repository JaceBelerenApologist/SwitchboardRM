using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;

namespace SwitchboardRM;

/// <summary>
/// AWS > New server, or "New server like this one" (pre-filled from a server). Four screens with Back / Next:
///   1. Server: region, image, instance type (with on-demand prices), name, tags, system disk, extra data disks
///   2. Network: VPC, subnet, tenancy, placement group, hostname, and the network interfaces (addresses, security groups ...)
///   3. Access and protection: instance profile, key pair, protections, shutdown behaviour, monitoring, metadata, user data
///   4. Review: everything, with an estimated cost; Launch
/// Choices are remembered per region. Nothing is created until Launch on the review screen.
/// </summary>
internal sealed class NewServerForm : Form
{
    private const int ColumnWidth = 400;
    private const double HoursPerMonth = 730;   // AWS's standard month

    private readonly string _profile;
    private readonly AppSettings _settings;
    private readonly LaunchSpec _clone;
    private readonly string _cloneName;
    private readonly Func<string, string, bool> _nameInUse;
    private readonly string _createdBy;
    private readonly CancellationTokenSource _cts = new();
    private int _loadVersion;
    private readonly Dictionary<string, List<TypeChoice>> _typeCache = new();
    private Dictionary<string, double> _instancePrices, _diskPrices;
    private string _pricesFor = "";
    private string _priceProblem = "";
    private List<NamedChoice> _subnets = new(), _groups = new();
    private bool _profilesLoaded;

    // ---- screens and navigation
    private readonly Panel _host = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(20, 12, 20, 12) };
    private readonly FlowLayoutPanel[] _pages = new FlowLayoutPanel[4];
    private readonly Label _steps = new() { Dock = DockStyle.Top, Height = 36, Padding = new Padding(20, 12, 20, 0), UseMnemonic = false };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(20, 6, 20, 0), UseMnemonic = false };
    private readonly Button _back = new() { Text = "< Back", AutoSize = true, MinimumSize = new Size(96, 32), Margin = new Padding(0, 0, 8, 0) };
    private readonly Button _next = new() { Text = "Next >", Tag = "primary", AutoSize = true, MinimumSize = new Size(120, 32), Margin = new Padding(0, 0, 8, 0) };
    private int _page;
    private static readonly string[] StepNames = { "1  Server", "2  Network", "3  Access and protection", "4  Review" };

    // ---- 1. server
    private readonly ComboBox _region = new() { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _image = new() { Width = ColumnWidth, DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = 560 };
    private readonly ComboBox _type = new() { Width = ColumnWidth, DropDownStyle = ComboBoxStyle.DropDown, DropDownWidth = 560, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
    private readonly TextBox _name = new() { Width = ColumnWidth };
    private readonly TextBox _tags = new() { Width = ColumnWidth, Height = 64, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
    private readonly ThemedCheckBox _tagCreatedBy = new() { AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
    private readonly NumericUpDown _rootGb = new() { Minimum = 8, Maximum = 16384, Value = 30, Width = 90 };
    private readonly ComboBox _rootType = DiskTypeBox();
    private readonly ThemedCheckBox _encrypted = new() { Text = "Encrypt the disks", AutoSize = true, Checked = true, Margin = new Padding(0, 8, 0, 0) };
    private readonly FlowLayoutPanel _dataDisks = new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
    private readonly Label _typeNote, _nameWarning, _diskCost;

    // ---- 2. network
    private readonly Label _vpcLabel;
    private readonly ComboBox _vpc = new() { Width = ColumnWidth, DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = 520 };
    private readonly ComboBox _subnet = new() { Width = ColumnWidth, DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = 560 };
    private readonly ComboBox _tenancy = new() { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _placementGroup = new() { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _hostname = new() { Width = ColumnWidth, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ThemedCheckBox _dnsA = new() { Text = "DNS A record for the instance-ID name", AutoSize = true };
    private readonly ThemedCheckBox _dnsAaaa = new() { Text = "DNS AAAA record for the instance-ID name (IPv6)", AutoSize = true };
    private readonly FlowLayoutPanel _interfaces = new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
    private readonly Button _addInterface = new() { Text = "Add network interface", AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
    private readonly Label _interfaceNote;

    // ---- 3. access and protection
    private readonly ComboBox _instanceProfile = new() { Width = ColumnWidth, DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = 520 };
    private readonly ComboBox _keyPair = new() { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ThemedCheckBox _termination = new() { Text = "Termination protection (can't be terminated until this is turned off)", AutoSize = true };
    private readonly ThemedCheckBox _stopProtection = new() { Text = "Stop protection (can't be stopped until this is turned off)", AutoSize = true };
    private readonly ComboBox _shutdown = new() { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ThemedCheckBox _monitoring = new() { Text = "Detailed monitoring (1-minute CloudWatch metrics, charged)", AutoSize = true };
    private readonly ThemedCheckBox _tokens = new() { Text = "Require tokens for instance metadata (IMDSv2, recommended)", AutoSize = true, Checked = true };
    private readonly NumericUpDown _hopLimit = new() { Minimum = 1, Maximum = 64, Value = 2, Width = 70 };
    private readonly TextBox _userData = new() { Width = ColumnWidth * 2, Height = 110, Multiline = true, ScrollBars = ScrollBars.Both, AcceptsReturn = true, AcceptsTab = true, WordWrap = false };
    private readonly Label _profileWarning;

    // ---- 4. review
    private readonly TextBox _review = new() { Width = ColumnWidth * 2 + 24, Height = 430, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, BorderStyle = BorderStyle.None };

    public string LaunchedId { get; private set; } = "";
    public string LaunchedName { get; private set; } = "";
    public string LaunchedRegion { get; private set; } = "";
    public string LaunchWarning { get; private set; } = "";

    public NewServerForm(string profile, AppSettings settings, IEnumerable<string> regions, Func<string, string, bool> nameInUse,
        LaunchSpec clone = null, string cloneName = "")
    {
        _profile = profile;
        _settings = settings;
        _clone = clone;
        _cloneName = cloneName ?? "";
        _nameInUse = nameInUse;
        _createdBy = Aws.AzureDefaultUsername(profile) is { Length: > 0 } u ? u : Environment.UserName;

        Text = clone == null ? "New server" : $"New server like {cloneName}";
        if (AppIcon.Icon != null) Icon = AppIcon.Icon;
        Font = new Font("Segoe UI", 9.5F);
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(LogicalToDeviceUnits(920), LogicalToDeviceUnits(700));
        MinimumSize = new Size(LogicalToDeviceUnits(760), LogicalToDeviceUnits(520));

        foreach (var r in regions.Concat(clone != null ? new[] { clone.Region } : Array.Empty<string>()).Where(r => !string.IsNullOrEmpty(r)).Distinct())
            _region.Items.Add(r);
        if (_region.Items.Count == 0) _region.Items.Add("us-east-1");
        _tagCreatedBy.Text = $"Add a CreatedBy tag ({_createdBy})";
        _tenancy.Items.AddRange(new object[] { "Shared (default)", "Dedicated instance" });
        _tenancy.SelectedIndex = 0;
        _hostname.Items.AddRange(new object[] { "IP name (e.g. ip-10-0-1-25)", "Instance-ID name (e.g. i-0123456789abcdef0)" });
        _hostname.SelectedIndex = 0;
        _shutdown.Items.AddRange(new object[] { "Stop", "Terminate" });
        _shutdown.SelectedIndex = 0;

        _typeNote = Note("");
        _nameWarning = Note("", accent: true);
        _diskCost = Note("");
        _vpcLabel = FieldLabel("VPC", first: true);
        _interfaceNote = Note("", width: ColumnWidth * 2);
        _profileWarning = Note("", accent: true);

        BuildServerPage();
        BuildNetworkPage();
        BuildAccessPage();
        _pages[3] = Page(FieldLabel("Review", first: true), _review,
            Note("Prices are AWS's public on-demand list prices in USD, for shared tenancy. Savings Plans, Reserved Instances " +
                 "and negotiated discounts aren't included, nor are data transfer, snapshots or disk IOPS/throughput charges.",
                 width: ColumnWidth * 2));
        _review.Font = new Font("Consolas", 9.5F);

        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(96, 32), DialogResult = DialogResult.Cancel, Margin = new Padding(0) };
        CancelButton = cancel;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 52, Padding = new Padding(16, 8, 16, 8) };
        buttons.Controls.AddRange(new Control[] { cancel, _next, _back });
        Controls.Add(_host);
        Controls.Add(_status);
        Controls.Add(buttons);
        Controls.Add(_steps);

        _back.Click += (_, _) => ShowPage(_page - 1);
        _next.Click += async (_, _) => await NextAsync();
        _region.SelectedIndexChanged += async (_, _) => await LoadRegionAsync();
        _image.SelectedIndexChanged += async (_, _) => await ImageChangedAsync();
        _type.SelectedIndexChanged += (_, _) => UpdateInterfaceLimits();
        _type.TextChanged += (_, _) => UpdateInterfaceLimits();
        _vpc.SelectedIndexChanged += (_, _) => FillVpcLists();
        _subnet.SelectedIndexChanged += (_, _) => SubnetChanged();
        _instanceProfile.SelectedIndexChanged += (_, _) => UpdateProfileWarning();
        _name.TextChanged += (_, _) => UpdateNameWarning();
        _rootGb.ValueChanged += (_, _) => UpdateDiskCost();
        _rootType.SelectedIndexChanged += (_, _) => UpdateDiskCost();
        _addInterface.Click += (_, _) => AddInterface(null);

        AddInterface(null);   // the primary interface
        ApplyCloneNonNetwork();
        Theme.Apply(this, Theme.Current.IsDark);
        ShowPage(0);
        _region.SelectedItem = clone?.Region ?? _region.Items[0];
        if (_region.SelectedIndex < 0) _region.SelectedIndex = 0;
    }

    // ================================================================== screens

    private void BuildServerPage()
    {
        var left = Column();
        left.Controls.AddRange(new Control[]
        {
            FieldLabel("Region", first: true), _region,
            FieldLabel("Image"), _image,
            Note("AWS's current Windows Server and Linux images, then your own images (newest first)."),
            FieldLabel("Instance type"), _type, _typeNote,
            FieldLabel("Name"), _name, _nameWarning,
            FieldLabel("Tags (optional, one per line: Key=Value)"), _tags,
            _tagCreatedBy,
        });
        var addDisk = new Button { Text = "Add data disk", AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        addDisk.Click += (_, _) => AddDataDisk(new DiskSpec { SizeGb = 100, Type = DiskTypeOf(_rootType) });
        var right = Column();
        right.Controls.AddRange(new Control[]
        {
            FieldLabel("System disk", first: true), Row(_rootGb, Unit("GiB"), _rootType),
            FieldLabel("Data disks"), _dataDisks, addDisk,
            _encrypted,
            Note("Uses the account's default encryption key. Unticked, the account's own default encryption setting applies."),
            _diskCost,
        });
        _pages[0] = Page(Columns(left, right));
    }

    private void BuildNetworkPage()
    {
        var usedLink = new LinkLabel { Text = "Show addresses already in use in this subnet ...", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
        usedLink.LinkClicked += async (_, _) => await ShowUsedAddressesAsync();
        var left = Column();
        left.Controls.AddRange(new Control[]
        {
            _vpcLabel, _vpc,
            FieldLabel("Subnet"), _subnet, usedLink,
            FieldLabel("Tenancy"), _tenancy,
            Note("Dedicated runs on hardware used only by your account (charged more; prices shown are for shared)."),
            FieldLabel("Placement group"), _placementGroup,
        });
        var right = Column();
        right.Controls.AddRange(new Control[]
        {
            FieldLabel("Hostname", first: true), _hostname, _dnsA, _dnsAaaa,
            Note("The name AWS gives the server inside the VPC, and which DNS records it creates for the instance-ID name."),
        });
        _pages[1] = Page(Columns(left, right), FieldLabel("Network interfaces"), _interfaces, _addInterface, _interfaceNote);
    }

    private void BuildAccessPage()
    {
        var left = Column();
        left.Controls.AddRange(new Control[]
        {
            FieldLabel("Instance profile (IAM role)", first: true), _instanceProfile, _profileWarning,
            FieldLabel("Key pair (optional)"), _keyPair,
            Note("Only needed to decrypt a Windows administrator password or for SSH without Session Manager."),
            FieldLabel("When shut down from inside the operating system"), _shutdown,
        });
        var right = Column();
        right.Controls.AddRange(new Control[]
        {
            FieldLabel("Protection", first: true), _termination, _stopProtection,
            FieldLabel("Monitoring and metadata"), _monitoring, _tokens,
            Row(new Label { Text = "Metadata hop limit", AutoSize = true, Margin = new Padding(0, 6, 8, 0) }, _hopLimit),
            Note("2 lets containers on the server reach the metadata service; 1 is stricter."),
        });
        _pages[2] = Page(Columns(left, right), FieldLabel("User data (optional, runs at first boot)"), _userData,
            Note("Windows: wrap a PowerShell script in <powershell> ... </powershell>. Linux: start with #!/bin/bash or #cloud-config.",
                 width: ColumnWidth * 2));
    }

    private void ShowPage(int page)
    {
        _page = Math.Clamp(page, 0, _pages.Length - 1);
        _host.SuspendLayout();
        _host.Controls.Clear();
        _host.Controls.Add(_pages[_page]);
        // the theme colours the controls that are in the window, and only the current screen is: theme it as it goes in
        Theme.Apply(this, Theme.Current.IsDark);
        _host.ResumeLayout();
        _host.AutoScrollPosition = new Point(0, 0);
        _back.Enabled = _page > 0;
        _next.Text = _page == _pages.Length - 1 ? "Launch" : "Next >";
        _steps.Text = string.Join("      ", StepNames.Select((s, i) => i == _page ? "[ " + s + " ]" : s));
        if (_page == 3) _review.Text = ReviewText();
        Status("");
    }

    /// <summary>Next: check this screen first (the network screen's addresses with AWS), or Launch on the review.</summary>
    private async Task NextAsync()
    {
        string problem = _page switch
        {
            0 => CheckServerPage(),
            1 => await CheckNetworkPageAsync(),
            _ => null,
        };
        if (problem != null) { Status(problem, accent: true); return; }
        if (_page == _pages.Length - 1) await LaunchAsync();
        else ShowPage(_page + 1);
    }

    // ================================================================== loading

    private string SelectedRegion => _region.SelectedItem as string ?? "";
    private LaunchDefault Remembered() => _settings.LaunchDefaults.TryGetValue(SelectedRegion, out var d) ? d : null;
    private bool Cloning => _clone != null && SelectedRegion == _clone.Region;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        await LoadProfilesAsync();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _cts.Cancel();
        base.OnFormClosed(e);
    }

    private async Task LoadProfilesAsync()
    {
        if (_profilesLoaded) return;
        try
        {
            var profiles = await ServerLaunch.InstanceProfilesAsync(_profile, _cts.Token);
            _instanceProfile.Items.Clear();
            _instanceProfile.Items.Add("(none)");
            _instanceProfile.Items.AddRange(profiles.Cast<object>().ToArray());
            _profilesLoaded = true;
            string want = _clone?.InstanceProfile is { Length: > 0 } c ? c : Remembered()?.InstanceProfile ?? "";
            object pick = (object)profiles.FirstOrDefault(p => p.Id == want) ?? profiles.FirstOrDefault(p => p.Flag);
            Select(_instanceProfile, pick ?? "(none)");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _instanceProfile.Items.Clear();
            _instanceProfile.Items.Add("(none)");
            _instanceProfile.SelectedIndex = 0;
            _profileWarning.Text = "Couldn't list instance profiles (" + Aws.Describe(ex) + "). Without one, Switchboard can't connect to the server.";
        }
        UpdateProfileWarning();
    }

    private async Task LoadRegionAsync()
    {
        string region = SelectedRegion;
        if (region.Length == 0) return;
        int version = ++_loadVersion;
        Busy(true, $"Loading images, network and key pairs for {region} ...");
        try
        {
            var imagesTask = ServerLaunch.ImagesAsync(_profile, region, _cts.Token);
            var networkTask = ServerLaunch.NetworkAsync(_profile, region, _cts.Token);
            var keysTask = ServerLaunch.KeyPairsAsync(_profile, region, _cts.Token);
            var groupsTask = ServerLaunch.PlacementGroupsAsync(_profile, region, _cts.Token);
            await Task.WhenAll(imagesTask, networkTask, keysTask, groupsTask);
            var images = imagesTask.Result;
            if (Cloning && _clone.ImageId.Length > 0 && images.All(i => i.Id != _clone.ImageId))
            {
                var original = await ServerLaunch.ImageAsync(_profile, region, _clone.ImageId, _cts.Token);
                if (original != null)
                {
                    original.Name = $"Same image as {_cloneName}: {original.Name}";
                    images.Insert(0, original);
                }
            }
            if (version != _loadVersion) return;
            var remembered = Remembered();

            _image.Items.Clear();
            _image.Items.AddRange(images.Cast<object>().ToArray());
            string wantImage = Cloning ? _clone.ImageId : "";
            Select(_image, images.FirstOrDefault(i => i.Id == wantImage) ?? images.FirstOrDefault(i => !i.Mine && i.IsWindows) ?? images.FirstOrDefault());

            var (vpcs, subnets, groups) = networkTask.Result;
            _subnets = subnets;
            _groups = groups;
            _vpc.Items.Clear();
            _vpc.Items.AddRange(vpcs.Cast<object>().ToArray());
            _vpcLabel.Visible = _vpc.Visible = vpcs.Count > 1;
            string wantSubnet = Cloning && _clone.Interfaces.Count > 0 ? _clone.Interfaces[0].SubnetId : remembered?.SubnetId ?? "";
            string wantVpc = subnets.FirstOrDefault(s => s.Id == wantSubnet)?.VpcId ?? (Cloning ? _clone.VpcId : "");
            Select(_vpc, vpcs.FirstOrDefault(v => v.Id == wantVpc) ?? vpcs.FirstOrDefault(v => v.Flag) ?? vpcs.FirstOrDefault());
            FillVpcLists();
            Select(_subnet, _subnet.Items.Cast<NamedChoice>().FirstOrDefault(s => s.Id == wantSubnet));

            _placementGroup.Items.Clear();
            _placementGroup.Items.Add("(none)");
            _placementGroup.Items.AddRange(groupsTask.Result.Cast<object>().ToArray());
            Select(_placementGroup, Cloning && groupsTask.Result.Contains(_clone.PlacementGroup) ? _clone.PlacementGroup : "(none)");

            _keyPair.Items.Clear();
            _keyPair.Items.Add("(none)");
            _keyPair.Items.AddRange(keysTask.Result.Cast<object>().ToArray());
            string wantKey = Cloning ? _clone.KeyName : remembered?.KeyName ?? "";
            Select(_keyPair, keysTask.Result.Contains(wantKey) ? wantKey : "(none)");

            ApplyCloneInterfaces(remembered);
            Busy(false, images.Count == 0 ? "No images were found in this region." : "");
            await ImageChangedAsync();
            UpdateNameWarning();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version == _loadVersion) Busy(false, "Couldn't load from AWS: " + Aws.Describe(ex), accent: true);
        }
    }

    /// <summary>A new image: the instance types that can run it (with prices for its OS) and its minimum disk size.</summary>
    private async Task ImageChangedAsync()
    {
        if (_image.SelectedItem is not ImageChoice image) return;
        if (image.RootSizeGb > 0)
        {
            _rootGb.Minimum = image.RootSizeGb;
            int wantDisk = Cloning && _clone.RootDisk.SizeGb > 0 ? _clone.RootDisk.SizeGb : Math.Max(image.RootSizeGb, (int)_rootGb.Value);
            _rootGb.Value = Math.Max(image.RootSizeGb, Math.Min(wantDisk, (int)_rootGb.Maximum));
        }

        string key = SelectedRegion + "|" + image.Architecture;
        string current = TypeName(_type.Text);
        if (!_typeCache.TryGetValue(key, out var types))
        {
            _typeNote.Text = "Loading instance types ...";
            try { types = await ServerLaunch.InstanceTypesAsync(_profile, SelectedRegion, image.Architecture, _cts.Token); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _typeNote.Text = "Couldn't list instance types: " + Aws.Describe(ex); return; }
            _typeCache[key] = types;
        }
        if (_image.SelectedItem != image) return;

        await LoadPricesAsync(image);
        foreach (var t in types)
            t.Hourly = _instancePrices != null && _instancePrices.TryGetValue(t.Name, out var p) ? p : double.NaN;

        _type.BeginUpdate();
        _type.Items.Clear();
        _type.Items.AddRange(types.Cast<object>().ToArray());
        _type.EndUpdate();
        string want = current.Length > 0 && types.Any(t => t.Name == current) ? current
            : Cloning && types.Any(t => t.Name == _clone.InstanceType) ? _clone.InstanceType
            : Remembered()?.InstanceType is { Length: > 0 } r && types.Any(t => t.Name == r) ? r
            : types.Any(t => t.Name == "t3.medium") ? "t3.medium" : types.FirstOrDefault()?.Name ?? "";
        Select(_type, types.FirstOrDefault(t => t.Name == want));
        _typeNote.Text = $"{types.Count} types fit this image ({image.Architecture}). Type to search, e.g. \"m6i\". " +
                         (_priceProblem.Length > 0 ? "Prices unavailable: " + _priceProblem
                             : $"Prices: on-demand list price, USD{(image.IsWindows ? ", Windows licence included" : "")}.");
        UpdateDiskCost();
        UpdateInterfaceLimits();
    }

    /// <summary>Prices for this region and OS (instance types) and region (disks); a failure just means no prices.</summary>
    private async Task LoadPricesAsync(ImageChoice image)
    {
        string wanted = SelectedRegion + "|" + image.PriceOs;
        if (_pricesFor == wanted) return;
        _typeNote.Text = "Loading prices ...";
        try
        {
            var instances = ServerLaunch.InstancePricesAsync(_profile, SelectedRegion, image.PriceOs, _cts.Token);
            var disks = ServerLaunch.DiskPricesAsync(_profile, SelectedRegion, _cts.Token);
            await Task.WhenAll(instances, disks);
            _instancePrices = instances.Result;
            _diskPrices = disks.Result;
            _priceProblem = _instancePrices.Count == 0 ? "AWS returned no prices for this region" : "";
            _pricesFor = wanted;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _instancePrices = _diskPrices = null;
            _priceProblem = Aws.Describe(ex);
            _pricesFor = wanted;
            Log.Write("New server: prices unavailable: " + _priceProblem);
        }
    }

    // ================================================================== disks

    private void AddDataDisk(DiskSpec disk)
    {
        var size = new NumericUpDown { Minimum = 1, Maximum = 16384, Value = Math.Clamp(disk.SizeGb, 1, 16384), Width = 90 };
        var type = DiskTypeBox();
        Select(type, disk.Type);
        var remove = new Button { Text = "Remove", AutoSize = true, Margin = new Padding(8, 0, 0, 0) };
        var row = Row(size, Unit("GiB"), type, remove);
        row.Tag = new DiskRow(size, type);
        remove.Click += (_, _) => { _dataDisks.Controls.Remove(row); row.Dispose(); UpdateDiskCost(); };
        size.ValueChanged += (_, _) => UpdateDiskCost();
        type.SelectedIndexChanged += (_, _) => UpdateDiskCost();
        _dataDisks.Controls.Add(row);
        Theme.Apply(this, Theme.Current.IsDark);
        UpdateDiskCost();
    }

    private sealed record DiskRow(NumericUpDown Size, ComboBox Type);

    private List<DiskSpec> DataDisks() => _dataDisks.Controls.OfType<FlowLayoutPanel>()
        .Select(r => r.Tag as DiskRow).Where(d => d != null)
        .Select(d => new DiskSpec { SizeGb = (int)d.Size.Value, Type = DiskTypeOf(d.Type) }).ToList();

    private double? DiskMonthly(DiskSpec d) =>
        _diskPrices != null && _diskPrices.TryGetValue(d.Type, out var perGb) ? perGb * d.SizeGb : null;

    private void UpdateDiskCost()
    {
        var disks = new List<DiskSpec> { new() { SizeGb = (int)_rootGb.Value, Type = DiskTypeOf(_rootType) } };
        disks.AddRange(DataDisks());
        var costs = disks.Select(DiskMonthly).ToList();
        _diskCost.Text = costs.All(c => c.HasValue)
            ? $"Disks: {disks.Sum(d => d.SizeGb):N0} GiB, ~${costs.Sum(c => c.Value):N2}/month (storage only)."
            : "";
    }

    // ================================================================== network

    private string SelectedVpc => (_vpc.SelectedItem as NamedChoice)?.Id ?? "";
    private List<NamedChoice> VpcSubnets() => _subnets.Where(s => SelectedVpc.Length == 0 || s.VpcId == SelectedVpc).ToList();
    private List<NamedChoice> VpcGroups() => _groups.Where(g => SelectedVpc.Length == 0 || g.VpcId == SelectedVpc).ToList();

    private void FillVpcLists()
    {
        var subnet = (_subnet.SelectedItem as NamedChoice)?.Id;
        _subnet.Items.Clear();
        _subnet.Items.AddRange(VpcSubnets().Cast<object>().ToArray());
        Select(_subnet, _subnet.Items.Cast<NamedChoice>().FirstOrDefault(s => s.Id == subnet));
        foreach (var editor in Editors()) editor.SetChoices(VpcSubnets(), VpcGroups(), Zone);
        SubnetChanged();
    }

    private string Zone => (_subnet.SelectedItem as NamedChoice)?.Zone ?? "";

    private void SubnetChanged()
    {
        foreach (var editor in Editors()) editor.SetZone(Zone, _subnet.SelectedItem as NamedChoice);
    }

    private List<InterfaceEditor> Editors() => _interfaces.Controls.OfType<InterfaceEditor>().ToList();

    private void AddInterface(InterfaceSpec from)
    {
        var editor = new InterfaceEditor(Editors().Count);
        editor.SetChoices(VpcSubnets(), VpcGroups(), Zone);
        editor.SetZone(Zone, _subnet.SelectedItem as NamedChoice);
        if (from != null) editor.Load(from);
        editor.RemoveRequested += () => { _interfaces.Controls.Remove(editor); editor.Dispose(); Renumber(); UpdateInterfaceLimits(); };
        _interfaces.Controls.Add(editor);
        Theme.Apply(this, Theme.Current.IsDark);
        UpdateInterfaceLimits();
    }

    private void Renumber()
    {
        int n = 0;
        foreach (var e in Editors()) e.SetIndex(n++);
    }

    private TypeChoice SelectedType => _type.Items.Cast<TypeChoice>().FirstOrDefault(t => t.Name == TypeName(_type.Text));

    private void UpdateInterfaceLimits()
    {
        var type = SelectedType;
        int count = Editors().Count;
        int max = type?.MaxInterfaces ?? 1;
        _addInterface.Enabled = count < max;
        _interfaceNote.Text = type == null ? "" :
            $"{type.Name} allows up to {max} network interface(s) and {type.MaxIpsPerInterface} IPv4 address(es) per interface." +
            (count > 1 ? " A public IPv4 address can only be given at launch to a server with a single interface." : "");
        foreach (var e in Editors()) e.SetPublicIpAllowed(count == 1);
    }

    private async Task ShowUsedAddressesAsync()
    {
        if (_subnet.SelectedItem is not NamedChoice subnet) { Status("Choose a subnet first.", accent: true); return; }
        Busy(true, $"Reading the addresses in use in {subnet.Name} ...");
        List<string> used;
        try { used = await ServerLaunch.UsedAddressesAsync(_profile, SelectedRegion, subnet.Id, _cts.Token); }
        catch (Exception ex) { Busy(false, "Couldn't read the addresses: " + Aws.Describe(ex), accent: true); return; }
        Busy(false, "");
        using var f = new Form
        {
            Text = $"Addresses in use: {subnet.Name} ({subnet.Cidr})", Icon = AppIcon.Icon, StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(LogicalToDeviceUnits(560), LogicalToDeviceUnits(420)), ShowInTaskbar = false, MinimizeBox = false,
        };
        f.Controls.Add(new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
            Font = new Font("Consolas", 9.5F),
            Text = used.Count == 0 ? "No addresses are in use in this subnet." :
                $"{used.Count} address(es) in use, in order (AWS also reserves the first four and the last):\r\n\r\n" + string.Join("\r\n", used),
        });
        Theme.Apply(f, Theme.Current.IsDark);
        f.ShowDialog(this);
    }

    // ================================================================== pre-filling

    /// <summary>Clone values that don't depend on what the region offers (or remembered defaults when not cloning).</summary>
    private void ApplyCloneNonNetwork()
    {
        var c = _clone;
        if (c == null)
        {
            if (_settings.LaunchDefaults.Values.FirstOrDefault()?.DiskType is { Length: > 0 } t) Select(_rootType, t);
            return;
        }
        _tags.Text = string.Join(Environment.NewLine, c.Tags.Select(t => $"{t.Key}={t.Value}"));
        Select(_rootType, c.RootDisk.Type);
        foreach (var d in c.DataDisks) AddDataDisk(d);
        _encrypted.Checked = c.Encrypted;
        _tenancy.SelectedIndex = c.Tenancy == "dedicated" ? 1 : 0;
        _hostname.SelectedIndex = c.HostnameType == "resource-name" ? 1 : 0;
        _dnsA.Checked = c.DnsA;
        _dnsAaaa.Checked = c.DnsAaaa;
        _termination.Checked = c.TerminationProtection;
        _stopProtection.Checked = c.StopProtection;
        _shutdown.SelectedIndex = c.ShutdownBehavior == "terminate" ? 1 : 0;
        _monitoring.Checked = c.DetailedMonitoring;
        _tokens.Checked = c.RequireTokens;
        _hopLimit.Value = Math.Clamp(c.HopLimit, 1, 64);
    }

    /// <summary>The network interfaces: copied from the server being cloned (never its addresses), or the region's remembered choices.</summary>
    private void ApplyCloneInterfaces(LaunchDefault remembered)
    {
        foreach (var e in Editors().Skip(1).ToList()) { _interfaces.Controls.Remove(e); e.Dispose(); }
        var primary = Editors().First();
        if (Cloning && _clone.Interfaces.Count > 0)
        {
            primary.Load(_clone.Interfaces[0]);
            foreach (var extra in _clone.Interfaces.Skip(1)) AddInterface(extra);
        }
        else if (remembered != null)
        {
            primary.Load(new InterfaceSpec { SecurityGroupIds = remembered.SecurityGroupIds });
        }
        UpdateInterfaceLimits();
    }

    // ================================================================== checks

    private void UpdateNameWarning()
    {
        string name = _name.Text.Trim();
        _nameWarning.Text = name.Length > 0 && _nameInUse(SelectedRegion, name) ? $"A server named {name} already exists in {SelectedRegion}." : "";
    }

    private void UpdateProfileWarning()
    {
        if (_instanceProfile.SelectedItem is NamedChoice p)
            _profileWarning.Text = p.Flag ? "" : "This profile's role doesn't have AWS's AmazonSSMManagedInstanceCore policy attached. " +
                                                 "Unless it grants Session Manager access another way, Switchboard won't be able to connect.";
        else if (_profilesLoaded)
            _profileWarning.Text = "Without an instance profile with Session Manager access, Switchboard can't connect to the server.";
    }

    private string CheckServerPage()
    {
        if (_image.SelectedItem is not ImageChoice) return "Choose an image.";
        string type = TypeName(_type.Text);
        if (type.Length == 0) return "Choose an instance type.";
        if (_type.Items.Count > 0 && SelectedType == null) return $"{type} isn't an instance type that can run this image here.";
        if (_name.Text.Trim().Length == 0) return "Enter a name for the server.";
        ParseTags(out var tagProblem);
        return tagProblem;
    }

    private async Task<string> CheckNetworkPageAsync()
    {
        if (_subnet.SelectedItem is not NamedChoice primarySubnet) return "Choose a subnet.";
        var editors = Editors();
        var addresses = new List<(int Index, string Ip, NamedChoice Subnet)>();
        for (int n = 0; n < editors.Count; n++)
        {
            var spec = editors[n].Read(out string problem);
            if (problem != null) return $"Network interface {n + 1}: {problem}";
            var subnet = n == 0 ? primarySubnet : _subnets.FirstOrDefault(s => s.Id == spec.SubnetId);
            if (subnet == null) return $"Network interface {n + 1}: choose a subnet.";
            if (spec.SecurityGroupIds.Count == 0) return $"Network interface {n + 1}: tick at least one security group.";
            int ips = 1 + spec.SecondaryCount + spec.SecondaryIps.Count;
            if (SelectedType is { } t && ips > t.MaxIpsPerInterface)
                return $"Network interface {n + 1}: {t.Name} allows {t.MaxIpsPerInterface} IPv4 address(es) per interface, not {ips}.";
            if (spec.Ipv6Count > 0 && !subnet.HasIpv6) return $"Network interface {n + 1}: the subnet has no IPv6 range.";
            if (spec.PrivateIp.Length > 0) addresses.Add((n, spec.PrivateIp, subnet));
            foreach (var ip in spec.SecondaryIps) addresses.Add((n, ip, subnet));
        }
        var duplicate = addresses.GroupBy(a => a.Ip).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null) return $"{duplicate.Key} is entered more than once.";
        if (addresses.Count == 0) return null;

        Busy(true, "Checking the addresses with AWS ...");
        try
        {
            foreach (var a in addresses)
            {
                string problem = await ServerLaunch.PrivateIpProblemAsync(_profile, SelectedRegion, a.Subnet.Cidr, a.Ip, _cts.Token);
                if (problem != null) { Busy(false, ""); return $"Network interface {a.Index + 1}: {problem}"; }
            }
        }
        catch (Exception ex) { Busy(false, ""); return "Couldn't check the addresses with AWS: " + Aws.Describe(ex); }
        Busy(false, "");
        return null;
    }

    private List<KeyValuePair<string, string>> ParseTags(out string problem)
    {
        problem = null;
        var tags = new List<KeyValuePair<string, string>>();
        foreach (var raw in _tags.Lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) { problem = $"Tag line \"{line}\" isn't Key=Value."; return tags; }
            string key = line[..eq].Trim();
            if (key.Equals("Name", StringComparison.OrdinalIgnoreCase)) continue;
            tags.Add(new(key, line[(eq + 1)..].Trim()));
        }
        if (_tagCreatedBy.Checked && tags.All(t => t.Key != "CreatedBy")) tags.Add(new("CreatedBy", _createdBy));
        return tags;
    }

    // ================================================================== review and launch

    private LaunchSpec BuildSpec()
    {
        var image = (ImageChoice)_image.SelectedItem;
        var primarySubnet = _subnet.SelectedItem as NamedChoice;
        var spec = new LaunchSpec
        {
            Region = SelectedRegion, ImageId = image.Id, RootDevice = image.RootDevice, IsWindows = image.IsWindows,
            InstanceType = TypeName(_type.Text), Name = _name.Text.Trim(), Tags = ParseTags(out _),
            RootDisk = new DiskSpec { SizeGb = (int)_rootGb.Value, Type = DiskTypeOf(_rootType) },
            DataDisks = DataDisks(), Encrypted = _encrypted.Checked,
            VpcId = primarySubnet?.VpcId ?? "",
            Tenancy = _tenancy.SelectedIndex == 1 ? "dedicated" : "default",
            PlacementGroup = _placementGroup.SelectedItem as string is { } pg && pg != "(none)" ? pg : "",
            HostnameType = _hostname.SelectedIndex == 1 ? "resource-name" : "ip-name",
            DnsA = _dnsA.Checked, DnsAaaa = _dnsAaaa.Checked,
            InstanceProfile = (_instanceProfile.SelectedItem as NamedChoice)?.Id ?? "",
            KeyName = _keyPair.SelectedItem as string is { } k && k != "(none)" ? k : "",
            TerminationProtection = _termination.Checked, StopProtection = _stopProtection.Checked,
            ShutdownBehavior = _shutdown.SelectedIndex == 1 ? "terminate" : "stop",
            DetailedMonitoring = _monitoring.Checked, RequireTokens = _tokens.Checked, HopLimit = (int)_hopLimit.Value,
            UserData = _userData.Text.Trim(),
        };
        var editors = Editors();
        for (int n = 0; n < editors.Count; n++)
        {
            var it = editors[n].Read(out _);
            if (n == 0) it.SubnetId = primarySubnet?.Id ?? "";
            spec.Interfaces.Add(it);
        }
        return spec;
    }

    private string ReviewText()
    {
        if (_image.SelectedItem is not ImageChoice image) return "";
        var spec = BuildSpec();
        var type = SelectedType;
        var sb = new StringBuilder();
        void Line(string label, string value) => sb.Append($"{label,-22}{value}\r\n");
        void Heading(string text) => sb.Append("\r\n" + text + "\r\n");

        sb.Append("SERVER\r\n");
        Line("Name", spec.Name);
        Line("Region", spec.Region);
        Line("Image", $"{image.Name}  ({image.Id})");
        Line("Instance type", type?.ToString() ?? spec.InstanceType);
        Line("System disk", $"{spec.RootDisk.SizeGb} GiB {spec.RootDisk.Type}");
        for (int d = 0; d < spec.DataDisks.Count; d++) Line($"Data disk {d + 1}", $"{spec.DataDisks[d].SizeGb} GiB {spec.DataDisks[d].Type}");
        Line("Encrypted", spec.Encrypted ? "yes" : "account default");
        if (spec.Tags.Count > 0) Line("Tags", string.Join(", ", spec.Tags.Select(t => $"{t.Key}={t.Value}")));

        Heading("NETWORK");
        Line("Subnet", (_subnet.SelectedItem as NamedChoice)?.ToString() ?? "");
        Line("Tenancy", spec.Tenancy == "dedicated" ? "dedicated instance" : "shared");
        if (spec.PlacementGroup.Length > 0) Line("Placement group", spec.PlacementGroup);
        Line("Hostname", (spec.HostnameType == "resource-name" ? "instance-ID name" : "IP name") +
                         (spec.DnsA ? ", A record" : "") + (spec.DnsAaaa ? ", AAAA record" : ""));
        for (int n = 0; n < spec.Interfaces.Count; n++)
        {
            var it = spec.Interfaces[n];
            string subnet = n == 0 ? "" : $"subnet {_subnets.FirstOrDefault(s => s.Id == it.SubnetId)?.Name ?? it.SubnetId}, ";
            string secondary = it.SecondaryIps.Count > 0 ? $", secondary {string.Join(", ", it.SecondaryIps)}"
                             : it.SecondaryCount > 0 ? $", {it.SecondaryCount} secondary" : "";
            Line($"Interface {n + 1}", $"{subnet}IPv4 {(it.PrivateIp.Length > 0 ? it.PrivateIp : "automatic")}{secondary}" +
                                      (it.Ipv6Count > 0 ? $", {it.Ipv6Count} IPv6" : "") + (it.PublicIp ? ", public IPv4" : ""));
            Line("", "security groups " + string.Join(", ", it.SecurityGroupIds.Select(id => _groups.FirstOrDefault(g => g.Id == id)?.Name ?? id)) +
                     (it.SourceDestCheck ? "" : "; source/destination check off") + (it.Description.Length > 0 ? $"; \"{it.Description}\"" : ""));
        }

        Heading("ACCESS AND PROTECTION");
        var profile = _instanceProfile.SelectedItem as NamedChoice;
        Line("Instance profile", profile == null ? "none: Switchboard won't be able to connect"
            : profile.Id + (profile.Flag ? "" : "  (no Session Manager policy found)"));
        Line("Key pair", spec.KeyName.Length > 0 ? spec.KeyName : "none");
        var protections = new List<string>();
        if (spec.TerminationProtection) protections.Add("termination");
        if (spec.StopProtection) protections.Add("stop");
        Line("Protection", protections.Count > 0 ? string.Join(", ", protections) : "none");
        Line("OS shutdown", spec.ShutdownBehavior == "terminate" ? "terminates the server" : "stops the server");
        Line("Monitoring", spec.DetailedMonitoring ? "detailed (1 minute)" : "basic (5 minutes)");
        Line("Metadata", (spec.RequireTokens ? "tokens required (IMDSv2)" : "tokens optional") + $", hop limit {spec.HopLimit}");
        Line("User data", spec.UserData.Length > 0 ? $"{spec.UserData.Split('\n').Length} line(s)" : "none");

        Heading("ESTIMATED COST (on-demand list price, USD)");
        double? hourly = type != null && !double.IsNaN(type.Hourly) ? type.Hourly : null;
        var diskCosts = new[] { spec.RootDisk }.Concat(spec.DataDisks).Select(DiskMonthly).ToList();
        double? disks = diskCosts.All(c => c.HasValue) ? diskCosts.Sum(c => c.Value) : null;
        Line("Instance", hourly.HasValue ? $"${hourly.Value:0.0###}/hour, ~${hourly.Value * HoursPerMonth:N2}/month" : "price unavailable");
        Line("Disks", disks.HasValue ? $"~${disks.Value:N2}/month" : "price unavailable");
        if (hourly.HasValue && disks.HasValue) Line("Total", $"~${hourly.Value * HoursPerMonth + disks.Value:N2}/month (730 hours)");
        if (spec.Tenancy == "dedicated") sb.Append("Dedicated tenancy costs more than the shared price shown.\r\n");
        if (_nameWarning.Text.Length > 0) sb.Append("\r\n" + _nameWarning.Text + "\r\n");
        sb.Append("\r\nLaunch starts the server straight away (and its charges).");
        return sb.ToString();
    }

    private async Task LaunchAsync()
    {
        var spec = BuildSpec();
        Busy(true, $"Launching {spec.Name} ...");
        _back.Enabled = false;
        try
        {
            var (id, warning) = await ServerLaunch.LaunchAsync(_profile, spec, _cts.Token);
            LaunchedId = id;
            LaunchWarning = warning;
            LaunchedName = spec.Name;
            LaunchedRegion = spec.Region;
            _settings.LaunchDefaults[spec.Region] = new LaunchDefault
            {
                SubnetId = spec.Interfaces[0].SubnetId, SecurityGroupIds = spec.Interfaces[0].SecurityGroupIds,
                InstanceProfile = spec.InstanceProfile, InstanceType = spec.InstanceType, KeyName = spec.KeyName,
                DiskType = spec.RootDisk.Type,
            };
            _settings.Save();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Write("New server: launch failed: " + ex.Message);
            Busy(false, "AWS didn't launch the server: " + Aws.Describe(ex), accent: true);
            _back.Enabled = true;
        }
    }

    // ================================================================== small helpers

    private void Busy(bool busy, string text, bool accent = false)
    {
        _next.Enabled = !busy;
        _region.Enabled = !busy;
        UseWaitCursor = busy;
        Status(text, accent);
    }

    private void Status(string text, bool accent = false)
    {
        _status.ForeColor = accent ? Theme.Current.AccentFore : Theme.Current.Muted;
        _status.Text = text;
    }

    private static string TypeName(string text) => (text ?? "").Trim().Split(' ')[0];

    private static ComboBox DiskTypeBox()
    {
        var box = new ComboBox { Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
        box.Items.AddRange(new object[] { "gp3", "gp2", "io1", "io2", "st1", "sc1" });
        box.SelectedIndex = 0;
        return box;
    }

    private static string DiskTypeOf(ComboBox box) => box.SelectedItem as string ?? "gp3";

    internal static void Select(ComboBox box, object item)
    {
        if (item == null) { if (box.Items.Count > 0 && box.SelectedIndex < 0) box.SelectedIndex = 0; return; }
        int i = box.Items.IndexOf(item);
        if (i >= 0) { box.SelectedIndex = i; return; }
        if (item is string s)
            for (int j = 0; j < box.Items.Count; j++)
                if (box.Items[j]?.ToString() == s) { box.SelectedIndex = j; return; }
    }

    private static FlowLayoutPanel Page(params Control[] controls)
    {
        var page = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        page.Controls.AddRange(controls);
        return page;
    }

    private static FlowLayoutPanel Column() =>
        new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 24, 0) };

    private static FlowLayoutPanel Columns(params Control[] columns)
    {
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        row.Controls.AddRange(columns);
        return row;
    }

    internal static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        row.Controls.AddRange(controls);
        return row;
    }

    private static Label Unit(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(4, 6, 12, 0) };

    internal static Label FieldLabel(string text, bool first = false) =>
        new() { Text = text, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, first ? 0 : 10, 0, 3) };

    internal static Label Note(string text, bool accent = false, int width = ColumnWidth) =>
        new() { Text = text, AutoSize = true, UseMnemonic = false, MaximumSize = new Size(width, 0), Tag = accent ? "accent" : "muted", Margin = new Padding(0, 3, 0, 0) };

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cts.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// One network interface on the New server's network screen: subnet (extra interfaces only; the primary uses the
/// screen's subnet), security groups, primary private IPv4 (automatic or specific), secondary IPv4s (a number or
/// addresses), IPv6 count, public IPv4 (primary, single interface only), source/destination check, description.
/// </summary>
internal sealed class InterfaceEditor : FlowLayoutPanel
{
    private readonly Label _title = new() { AutoSize = true, UseMnemonic = false, Font = new Font("Segoe UI Semibold", 9.5F), Margin = new Padding(0, 6, 0, 2) };
    private readonly Label _subnetLabel;
    private readonly ComboBox _subnet = new() { Width = 400, DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = 560 };
    private readonly CheckedListBox _groups = new() { Width = 400, Height = 96, CheckOnClick = true, IntegralHeight = false };
    private readonly TextBox _privateIp = new() { Width = 180, PlaceholderText = "Automatic" };
    private readonly TextBox _secondary = new() { Width = 400, PlaceholderText = "None (a number, or addresses separated by commas)" };
    private readonly NumericUpDown _ipv6 = new() { Minimum = 0, Maximum = 50, Width = 70 };
    private readonly TextBox _description = new() { Width = 400, PlaceholderText = "Optional" };
    private readonly ThemedCheckBox _publicIp = new() { Text = "Public IPv4 address", AutoSize = true };
    private readonly ThemedCheckBox _sourceDest = new() { Text = "Source/destination check (turn off only for NAT, firewall or routing servers)", AutoSize = true, Checked = true };
    private readonly Button _remove = new() { Text = "Remove this interface", AutoSize = true, Margin = new Padding(0, 6, 0, 0) };
    private readonly Label _ipv6Note = NewServerForm.Note("");
    private List<NamedChoice> _subnets = new();
    private int _index;

    public event Action RemoveRequested;

    public InterfaceEditor(int index)
    {
        FlowDirection = FlowDirection.TopDown;
        WrapContents = false;
        AutoSize = true;
        Padding = new Padding(10, 4, 10, 10);
        Margin = new Padding(0, 6, 0, 0);
        BorderStyle = BorderStyle.FixedSingle;
        _subnetLabel = NewServerForm.FieldLabel("Subnet (same zone as the primary)");
        _remove.Click += (_, _) => RemoveRequested?.Invoke();
        _subnet.SelectedIndexChanged += (_, _) => UpdateIpv6(_subnet.SelectedItem as NamedChoice);

        var left = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 24, 0) };
        left.Controls.AddRange(new Control[]
        {
            _subnetLabel, _subnet,
            NewServerForm.FieldLabel("Primary private IPv4"), _privateIp,
            NewServerForm.FieldLabel("Secondary private IPv4"), _secondary,
            NewServerForm.FieldLabel("IPv6 addresses"), _ipv6, _ipv6Note,
        });
        var right = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        right.Controls.AddRange(new Control[]
        {
            NewServerForm.FieldLabel("Security groups"), _groups,
            NewServerForm.FieldLabel("Description"), _description,
            _publicIp, _sourceDest,
        });
        Controls.Add(_title);
        Controls.Add(NewServerForm.Row(left, right));
        Controls.Add(_remove);
        SetIndex(index);
    }

    public void SetIndex(int index)
    {
        _index = index;
        _title.Text = index == 0 ? "Network interface 1 (primary)" : $"Network interface {index + 1}";
        _subnetLabel.Visible = _subnet.Visible = index > 0;
        _remove.Visible = index > 0;
        _publicIp.Visible = index == 0;
    }

    public void SetPublicIpAllowed(bool allowed)
    {
        _publicIp.Enabled = allowed;
        if (!allowed) _publicIp.Checked = false;
    }

    /// <summary>The subnets and security groups of the chosen VPC (extra interfaces must be in the primary's zone).</summary>
    public void SetChoices(List<NamedChoice> subnets, List<NamedChoice> groups, string zone)
    {
        _subnets = subnets;
        var ticked = _groups.CheckedItems.Cast<NamedChoice>().Select(g => g.Id).ToHashSet();
        _groups.Items.Clear();
        foreach (var g in groups) _groups.Items.Add(g, ticked.Contains(g.Id));
        FillSubnets(zone);
    }

    private void FillSubnets(string zone)
    {
        var current = (_subnet.SelectedItem as NamedChoice)?.Id;
        _subnet.Items.Clear();
        _subnet.Items.AddRange(_subnets.Where(s => zone.Length == 0 || s.Zone == zone).Cast<object>().ToArray());
        NewServerForm.Select(_subnet, _subnet.Items.Cast<NamedChoice>().FirstOrDefault(s => s.Id == current));
    }

    /// <summary>The primary subnet changed: extra interfaces follow its zone; IPv6 depends on this interface's subnet.</summary>
    public void SetZone(string zone, NamedChoice primarySubnet)
    {
        if (_index > 0) FillSubnets(zone);
        UpdateIpv6(_index == 0 ? primarySubnet : _subnet.SelectedItem as NamedChoice);
    }

    private void UpdateIpv6(NamedChoice subnet)
    {
        bool v6 = subnet?.HasIpv6 == true;
        _ipv6.Enabled = v6;
        if (!v6) _ipv6.Value = 0;
        _ipv6Note.Text = subnet == null || v6 ? "" : "The subnet has no IPv6 range.";
    }

    public void Load(InterfaceSpec spec)
    {
        NewServerForm.Select(_subnet, _subnet.Items.Cast<NamedChoice>().FirstOrDefault(s => s.Id == spec.SubnetId));
        for (int i = 0; i < _groups.Items.Count; i++)
            _groups.SetItemChecked(i, spec.SecurityGroupIds.Contains(((NamedChoice)_groups.Items[i]).Id));
        _privateIp.Text = spec.PrivateIp;
        _secondary.Text = spec.SecondaryIps.Count > 0 ? string.Join(", ", spec.SecondaryIps)
            : spec.SecondaryCount > 0 ? spec.SecondaryCount.ToString(CultureInfo.InvariantCulture) : "";
        if (_ipv6.Enabled) _ipv6.Value = Math.Clamp(spec.Ipv6Count, 0, 50);
        _description.Text = spec.Description;
        _publicIp.Checked = spec.PublicIp;
        _sourceDest.Checked = spec.SourceDestCheck;
    }

    /// <summary>What's entered; problem says what's wrong with the address fields, if anything.</summary>
    public InterfaceSpec Read(out string problem)
    {
        problem = null;
        var spec = new InterfaceSpec
        {
            SubnetId = (_subnet.SelectedItem as NamedChoice)?.Id ?? "",
            SecurityGroupIds = _groups.CheckedItems.Cast<NamedChoice>().Select(g => g.Id).ToList(),
            PrivateIp = _privateIp.Text.Trim(),
            Ipv6Count = (int)_ipv6.Value,
            Description = _description.Text.Trim(),
            PublicIp = _publicIp.Visible && _publicIp.Enabled && _publicIp.Checked,
            SourceDestCheck = _sourceDest.Checked,
        };
        string secondary = _secondary.Text.Trim();
        if (secondary.Length > 0)
        {
            if (int.TryParse(secondary, NumberStyles.None, CultureInfo.InvariantCulture, out int count)) spec.SecondaryCount = count;
            else
            {
                spec.SecondaryIps = secondary.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
                if (spec.PrivateIp.Length == 0) problem = "to give specific secondary addresses, give the primary address too.";
            }
        }
        if (_index > 0 && spec.SubnetId.Length == 0) problem ??= "choose a subnet.";
        return spec;
    }
}
