using System.Drawing;
using System.Text;
using System.Windows.Forms;

namespace SwitchboardRM;

/// <summary>
/// AWS > New server (blank), or "New server like this one" (pre-filled from a server). One screen, two columns:
/// what the server is (region, image, instance type, disk, name, tags) and where it goes (network, security groups,
/// instance profile, key pair). Choices are remembered per region. Nothing is created until the review is confirmed.
/// </summary>
internal sealed class NewServerForm : Form
{
    private const int ColumnWidth = 400;

    private readonly string _profile;
    private readonly AppSettings _settings;
    private readonly CloneSource _clone;
    private readonly string _cloneName;
    private readonly Func<string, string, bool> _nameInUse;   // (region, name) -> a server already has that name
    private readonly string _createdBy;
    private readonly CancellationTokenSource _cts = new();
    private int _loadVersion;                                  // ignores results of a region load that's been superseded
    private readonly Dictionary<string, List<TypeChoice>> _typeCache = new();

    private List<NamedChoice> _subnets = new(), _groups = new();
    private bool _profilesLoaded;

    private readonly ComboBox _region = new() { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox _image = new() { Width = ColumnWidth, DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = 560 };
    private readonly ComboBox _type = new() { Width = ColumnWidth, DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
    private readonly NumericUpDown _diskGb = new() { Minimum = 8, Maximum = 16384, Value = 30, Width = 90 };
    private readonly ComboBox _diskType = new() { Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _name = new() { Width = ColumnWidth };
    private readonly TextBox _tags = new() { Width = ColumnWidth, Height = 70, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
    private readonly ComboBox _vpc = new() { Width = ColumnWidth, DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = 520 };
    private readonly ComboBox _subnet = new() { Width = ColumnWidth, DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = 560 };
    private readonly CheckedListBox _sgs = new() { Width = ColumnWidth, Height = 110, CheckOnClick = true, IntegralHeight = false };
    private readonly ThemedCheckBox _publicIp = new() { Text = "Give it a public IP address (Session Manager doesn't need one)", AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
    private readonly ComboBox _instanceProfile = new() { Width = ColumnWidth, DropDownStyle = ComboBoxStyle.DropDownList, DropDownWidth = 520 };
    private readonly ComboBox _keyPair = new() { Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ThemedCheckBox _tagCreatedBy = new() { AutoSize = true, Margin = new Padding(0, 4, 0, 0) };
    private readonly Label _vpcLabel, _nameWarning, _profileWarning, _typeNote, _status;
    private readonly Button _launch;

    public string LaunchedId { get; private set; } = "";
    public string LaunchedName { get; private set; } = "";
    public string LaunchedRegion { get; private set; } = "";

    public NewServerForm(string profile, AppSettings settings, IEnumerable<string> regions, Func<string, string, bool> nameInUse,
        CloneSource clone = null, string cloneName = "")
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
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        foreach (var r in regions.Concat(clone != null ? new[] { clone.Region } : Array.Empty<string>()).Where(r => !string.IsNullOrEmpty(r)).Distinct())
            _region.Items.Add(r);
        if (_region.Items.Count == 0) _region.Items.Add("us-east-1");
        _diskType.Items.AddRange(new object[] { "gp3", "gp2", "io1", "io2", "st1", "sc1" });
        _diskType.SelectedItem = "gp3";
        _tagCreatedBy.Text = $"Add a CreatedBy tag ({_createdBy})";

        _vpcLabel = FieldLabel("VPC");
        _nameWarning = Note("");
        _nameWarning.Tag = "accent";
        _profileWarning = Note("");
        _profileWarning.Tag = "accent";
        _typeNote = Note("");
        _status = Note("");

        // ---- left: what the server is
        var left = Column();
        left.Controls.AddRange(new Control[]
        {
            FieldLabel("Region", first: true), _region,
            FieldLabel("Image"), _image,
            Note("AWS's current Windows Server and Linux images, then your own images (newest first)."),
            FieldLabel("Instance type"), _type, _typeNote,
            FieldLabel("System disk"), Row(_diskGb, new Label { Text = "GiB", AutoSize = true, Margin = new Padding(4, 6, 12, 0) }, _diskType),
            FieldLabel("Name"), _name, _nameWarning,
            FieldLabel("Tags (optional, one per line: Key=Value)"), _tags,
            _tagCreatedBy,
        });

        // ---- right: where it goes and how it's reached
        var right = Column();
        right.Controls.AddRange(new Control[]
        {
            _vpcLabel, _vpc,
            FieldLabel("Subnet", first: false), _subnet,
            FieldLabel("Security groups"), _sgs,
            _publicIp,
            FieldLabel("Instance profile (IAM role)"), _instanceProfile, _profileWarning,
            FieldLabel("Key pair (optional)"), _keyPair,
            Note("Only needed to decrypt a Windows administrator password or for SSH without Session Manager."),
        });

        var columns = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        columns.Controls.Add(left);
        columns.Controls.Add(right);

        _launch = new Button { Text = "Review and launch ...", Tag = "primary", AutoSize = true, MinimumSize = new Size(170, 32), Margin = new Padding(0, 0, 8, 0) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(96, 32), DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
        buttons.Controls.AddRange(new Control[] { _launch, cancel });
        CancelButton = cancel;

        var body = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(20, 16, 20, 16),
        };
        _status.MaximumSize = new Size(ColumnWidth * 2 + 24, 0);
        body.Controls.AddRange(new Control[] { columns, _status, buttons });
        Controls.Add(body);

        _region.SelectedIndexChanged += async (_, _) => await LoadRegionAsync();
        _image.SelectedIndexChanged += async (_, _) => await ImageChangedAsync();
        _vpc.SelectedIndexChanged += (_, _) => FillVpcLists();
        _instanceProfile.SelectedIndexChanged += (_, _) => UpdateProfileWarning();
        _name.TextChanged += (_, _) => UpdateNameWarning();
        _launch.Click += async (_, _) => await ReviewAndLaunchAsync();

        Theme.Apply(this, Theme.Current.IsDark);
        _region.SelectedItem = clone?.Region ?? _region.Items[0];
        if (_region.SelectedIndex < 0) _region.SelectedIndex = 0;
    }

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

    // ------------------------------------------------------------------ loading

    private string SelectedRegion => _region.SelectedItem as string ?? "";

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
            Select(_instanceProfile, profiles.FirstOrDefault(p => p.Id == want) ?? (object)profiles.FirstOrDefault(p => p.Flag) ?? "(none)");
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

    private LaunchDefault Remembered() => _settings.LaunchDefaults.TryGetValue(SelectedRegion, out var d) ? d : null;

    private async Task LoadRegionAsync()
    {
        string region = SelectedRegion;
        if (region.Length == 0) return;
        int version = ++_loadVersion;
        SetBusy(true, $"Loading images, network and key pairs for {region} ...");
        try
        {
            var imagesTask = ServerLaunch.ImagesAsync(_profile, region, _cts.Token);
            var networkTask = ServerLaunch.NetworkAsync(_profile, region, _cts.Token);
            var keysTask = ServerLaunch.KeyPairsAsync(_profile, region, _cts.Token);
            await Task.WhenAll(imagesTask, networkTask, keysTask);
            var images = imagesTask.Result;
            if (_clone != null && region == _clone.Region && _clone.ImageId.Length > 0 && images.All(i => i.Id != _clone.ImageId))
            {
                // the original server's image (an older AWS build, or one of yours) at the top
                var original = await ServerLaunch.ImageAsync(_profile, region, _clone.ImageId, _cts.Token);
                if (original != null)
                {
                    original.Name = $"Same image as {_cloneName}: {original.Name}";
                    images.Insert(0, original);
                }
            }
            if (version != _loadVersion) return;   // another region was picked meanwhile

            var remembered = Remembered();
            bool cloning = _clone != null && region == _clone.Region;

            _image.Items.Clear();
            _image.Items.AddRange(images.Cast<object>().ToArray());
            string wantImage = cloning ? _clone.ImageId : "";
            Select(_image, images.FirstOrDefault(i => i.Id == wantImage) ?? images.FirstOrDefault(i => !i.Mine && i.IsWindows) ?? images.FirstOrDefault());

            var (vpcs, subnets, groups) = networkTask.Result;
            _subnets = subnets;
            _groups = groups;
            _vpc.Items.Clear();
            _vpc.Items.AddRange(vpcs.Cast<object>().ToArray());
            _vpcLabel.Visible = _vpc.Visible = vpcs.Count > 1;   // one VPC: nothing to choose
            string wantSubnet = cloning ? _clone.SubnetId : remembered?.SubnetId ?? "";
            string wantVpc = subnets.FirstOrDefault(s => s.Id == wantSubnet)?.VpcId ?? (cloning ? _clone.VpcId : "");
            Select(_vpc, vpcs.FirstOrDefault(v => v.Id == wantVpc) ?? vpcs.FirstOrDefault(v => v.Detail.EndsWith("default")) ?? vpcs.FirstOrDefault());
            FillVpcLists();
            Select(_subnet, _subnet.Items.Cast<NamedChoice>().FirstOrDefault(s => s.Id == wantSubnet));
            var wantGroups = cloning ? _clone.SecurityGroupIds : remembered?.SecurityGroupIds ?? new List<string>();
            for (int i = 0; i < _sgs.Items.Count; i++)
                _sgs.SetItemChecked(i, wantGroups.Contains(((NamedChoice)_sgs.Items[i]).Id));
            _publicIp.Checked = cloning && _clone.PublicIp;

            _keyPair.Items.Clear();
            _keyPair.Items.Add("(none)");
            _keyPair.Items.AddRange(keysTask.Result.Cast<object>().ToArray());
            string wantKey = cloning ? _clone.KeyName : remembered?.KeyName ?? "";
            Select(_keyPair, keysTask.Result.Contains(wantKey) ? wantKey : "(none)");

            string wantDiskType = cloning && _clone.DiskType.Length > 0 ? _clone.DiskType : remembered?.DiskType is { Length: > 0 } dt ? dt : "gp3";
            Select(_diskType, wantDiskType);
            if (cloning)
            {
                _tags.Text = string.Join(Environment.NewLine, _clone.Tags.Select(t => $"{t.Key}={t.Value}"));
            }
            SetBusy(false, images.Count == 0 ? "No images were found in this region." : "");
            await ImageChangedAsync();
            UpdateNameWarning();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version == _loadVersion) SetBusy(false, "Couldn't load from AWS: " + Aws.Describe(ex));
        }
    }

    /// <summary>A new image: instance types that can run it, and its minimum disk size.</summary>
    private async Task ImageChangedAsync()
    {
        if (_image.SelectedItem is not ImageChoice image) return;
        if (image.RootSizeGb > 0)
        {
            _diskGb.Minimum = image.RootSizeGb;
            int want = _clone != null && SelectedRegion == _clone.Region && _clone.DiskGb > 0 ? _clone.DiskGb : Math.Max(image.RootSizeGb, (int)_diskGb.Value);
            _diskGb.Value = Math.Max(image.RootSizeGb, Math.Min(want, (int)_diskGb.Maximum));
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
        if (_image.SelectedItem != image) return;   // changed again meanwhile

        _type.BeginUpdate();
        _type.Items.Clear();
        _type.Items.AddRange(types.Cast<object>().ToArray());
        _type.EndUpdate();
        string want = current.Length > 0 && types.Any(t => t.Name == current) ? current
            : _clone != null && SelectedRegion == _clone.Region && types.Any(t => t.Name == _clone.InstanceType) ? _clone.InstanceType
            : Remembered()?.InstanceType is { Length: > 0 } r && types.Any(t => t.Name == r) ? r
            : types.Any(t => t.Name == "t3.medium") ? "t3.medium" : types.FirstOrDefault()?.Name ?? "";
        Select(_type, types.FirstOrDefault(t => t.Name == want));
        _typeNote.Text = $"{types.Count} types fit this image ({image.Architecture}). Type to search, e.g. \"m6i\".";
    }

    private void FillVpcLists()
    {
        string vpc = (_vpc.SelectedItem as NamedChoice)?.Id ?? "";
        var checkedIds = _sgs.CheckedItems.Cast<NamedChoice>().Select(g => g.Id).ToHashSet();
        var subnet = (_subnet.SelectedItem as NamedChoice)?.Id;

        _subnet.Items.Clear();
        _subnet.Items.AddRange(_subnets.Where(s => vpc.Length == 0 || s.VpcId == vpc).Cast<object>().ToArray());
        Select(_subnet, _subnet.Items.Cast<NamedChoice>().FirstOrDefault(s => s.Id == subnet));

        _sgs.Items.Clear();
        foreach (var g in _groups.Where(g => vpc.Length == 0 || g.VpcId == vpc))
            _sgs.Items.Add(g, checkedIds.Contains(g.Id));
    }

    // ------------------------------------------------------------------ checks

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

    private static string TypeName(string text) => (text ?? "").Trim().Split(' ')[0];

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
            if (key.Equals("Name", StringComparison.OrdinalIgnoreCase)) continue;   // the Name field sets it
            tags.Add(new(key, line[(eq + 1)..].Trim()));
        }
        if (_tagCreatedBy.Checked && tags.All(t => t.Key != "CreatedBy")) tags.Add(new("CreatedBy", _createdBy));
        return tags;
    }

    // ------------------------------------------------------------------ launch

    private async Task ReviewAndLaunchAsync()
    {
        string name = _name.Text.Trim();
        string type = TypeName(_type.Text);
        var image = _image.SelectedItem as ImageChoice;
        var subnet = _subnet.SelectedItem as NamedChoice;
        var groups = _sgs.CheckedItems.Cast<NamedChoice>().ToList();
        string problem =
            name.Length == 0 ? "Enter a name for the server." :
            image == null ? "Choose an image." :
            type.Length == 0 ? "Choose an instance type." :
            _type.Items.Count > 0 && _type.Items.Cast<TypeChoice>().All(t => t.Name != type) ? $"{type} isn't an instance type that can run this image here." :
            subnet == null ? "Choose a subnet." :
            groups.Count == 0 ? "Tick at least one security group." : null;
        var tags = ParseTags(out var tagProblem);
        problem ??= tagProblem;
        if (problem != null) { _status.Tag = "accent"; Theme.Apply(this, Theme.Current.IsDark); _status.Text = problem; return; }

        var profile = _instanceProfile.SelectedItem as NamedChoice;
        string key = _keyPair.SelectedItem as string is { } k && k != "(none)" ? k : "";
        var spec = new LaunchSpec
        {
            Region = SelectedRegion, ImageId = image.Id, RootDevice = image.RootDevice, InstanceType = type, SubnetId = subnet.Id,
            SecurityGroupIds = groups.Select(g => g.Id).ToList(), PublicIp = _publicIp.Checked,
            InstanceProfile = profile?.Id ?? "", KeyName = key, DiskGb = (int)_diskGb.Value,
            DiskType = _diskType.SelectedItem as string ?? "gp3", Name = name, Tags = tags,
        };

        var summary = new StringBuilder();
        summary.AppendLine($"Name:            {name}");
        summary.AppendLine($"Region:          {spec.Region}");
        summary.AppendLine($"Image:           {image.Name}  ({image.Id})");
        summary.AppendLine($"Instance type:   {(_type.SelectedItem as TypeChoice)?.ToString() ?? type}");
        summary.AppendLine($"System disk:     {spec.DiskGb} GiB {spec.DiskType}");
        summary.AppendLine($"Subnet:          {subnet}");
        summary.AppendLine($"Security groups: {string.Join(", ", groups.Select(g => g.Name))}");
        summary.AppendLine($"Public IP:       {(spec.PublicIp ? "yes" : "no")}");
        summary.AppendLine($"Instance profile:{(profile != null ? " " + profile.Id + (profile.Flag ? "" : "  (no Session Manager policy found)") : " none: Switchboard won't be able to connect")}");
        summary.AppendLine($"Key pair:        {(key.Length > 0 ? key : "none")}");
        if (tags.Count > 0) summary.AppendLine($"Tags:            {string.Join(", ", tags.Select(t => $"{t.Key}={t.Value}"))}");
        if (_nameWarning.Text.Length > 0) summary.AppendLine().AppendLine(_nameWarning.Text);
        summary.AppendLine().Append("Launch this server now? It starts running (and costing) straight away.");

        if (MessageBox.Show(this, summary.ToString(), "Review the new server", MessageBoxButtons.OKCancel, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) != DialogResult.OK) return;

        SetBusy(true, $"Launching {name} ...");
        try
        {
            LaunchedId = await ServerLaunch.LaunchAsync(_profile, spec, _cts.Token);
            LaunchedName = name;
            LaunchedRegion = spec.Region;
            _settings.LaunchDefaults[spec.Region] = new LaunchDefault
            {
                SubnetId = spec.SubnetId, SecurityGroupIds = spec.SecurityGroupIds, InstanceProfile = spec.InstanceProfile,
                InstanceType = spec.InstanceType, KeyName = spec.KeyName, DiskType = spec.DiskType,
            };
            _settings.Save();
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Write("New server: launch failed: " + ex.Message);
            SetBusy(false, "AWS didn't launch the server: " + Aws.Describe(ex));
        }
    }

    // ------------------------------------------------------------------ small helpers

    private void SetBusy(bool busy, string text)
    {
        _launch.Enabled = !busy;
        _region.Enabled = !busy;
        UseWaitCursor = busy;
        _status.Tag = "muted";
        _status.Text = text;
        Theme.Apply(this, Theme.Current.IsDark);
    }

    private static void Select(ComboBox box, object item)
    {
        if (item == null) { if (box.Items.Count > 0 && box.SelectedIndex < 0) box.SelectedIndex = 0; return; }
        int i = box.Items.IndexOf(item);
        if (i >= 0) box.SelectedIndex = i;
        else if (item is string s)
        {
            for (int j = 0; j < box.Items.Count; j++)
                if (box.Items[j]?.ToString() == s) { box.SelectedIndex = j; return; }
        }
    }

    private static FlowLayoutPanel Column() =>
        new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 24, 0) };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        row.Controls.AddRange(controls);
        return row;
    }

    private static Label FieldLabel(string text, bool first = false) =>
        new() { Text = text, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, first ? 0 : 10, 0, 3) };

    private static Label Note(string text) =>
        new() { Text = text, AutoSize = true, UseMnemonic = false, MaximumSize = new Size(ColumnWidth, 0), Tag = "muted", Margin = new Padding(0, 3, 0, 0) };

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cts.Dispose();
        base.Dispose(disposing);
    }
}
