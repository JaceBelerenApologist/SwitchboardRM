namespace SwitchboardRM;

/// <summary>The common shape of the small volume dialogs: a column of controls, then the action and Cancel buttons.</summary>
internal abstract class VolumeDialog : Form
{
    protected readonly FlowLayoutPanel Body = new() { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(20, 16, 20, 8) };
    protected readonly Button Ok = new() { Tag = "primary", AutoSize = true, MinimumSize = new Size(110, 32), Margin = new Padding(0, 0, 8, 0) };
    private readonly FlowLayoutPanel _buttons = new() { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0, 14, 0, 8) };
    protected const int Width1 = 440;

    protected VolumeDialog(string title, string okText)
    {
        Text = title;
        if (AppIcon.Icon != null) Icon = AppIcon.Icon;
        Font = new Font("Segoe UI", 9.5F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Ok.Text = okText;
        Ok.Click += (_, _) => { if (Accept()) { DialogResult = DialogResult.OK; Close(); } };
        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(90, 32), DialogResult = DialogResult.Cancel, Margin = new Padding(0) };
        CancelButton = cancel;
        _buttons.Controls.AddRange(new Control[] { Ok, cancel });
        Controls.Add(Body);
    }

    /// <summary>Check the input; true closes the dialog with OK.</summary>
    protected virtual bool Accept() => true;

    protected static Label Caption(string text, int top = 10) => new() { Text = text, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, top, 0, 3) };
    protected static Label Hint(string text) => new() { Text = text, AutoSize = true, UseMnemonic = false, Tag = "muted", MaximumSize = new Size(Width1, 0), Margin = new Padding(0, 4, 0, 0) };
    protected static Label Problem() => new() { AutoSize = true, UseMnemonic = false, Tag = "accent", MaximumSize = new Size(Width1, 0), Margin = new Padding(0, 6, 0, 0), Visible = false };

    /// <summary>Called at the end of each dialog's constructor: the buttons go last, then the theme.</summary>
    protected void Finish()
    {
        Body.Controls.Add(_buttons);
        AcceptButton = Ok;
        Theme.Apply(this, Theme.Current.IsDark);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }
}

/// <summary>A volume's or snapshot's Name and other tags.</summary>
internal sealed class TagsForm : VolumeDialog
{
    private readonly TextBox _name = new() { Width = Width1 };
    private readonly TextBox _tags = new() { Width = Width1, Height = 120, Multiline = true, ScrollBars = ScrollBars.Vertical, AcceptsReturn = true };
    public string NameValue => _name.Text.Trim();
    public List<KeyValuePair<string, string>> TagsValue => VolumeAdmin.ParseTags(_tags.Text);

    public TagsForm(string what, string name, IEnumerable<KeyValuePair<string, string>> tags) : base($"Name and tags: {what}", "Save")
    {
        _name.Text = name ?? "";
        _tags.Text = VolumeAdmin.FormatTags(tags ?? Enumerable.Empty<KeyValuePair<string, string>>());
        Body.Controls.AddRange(new Control[]
        {
            Caption("Name", 0), _name,
            Caption("Other tags, one per line: Key=Value"), _tags,
            Hint("Tags removed here are removed in AWS too. AWS's own tags (aws:...) aren't shown and aren't changed."),
        });
        Finish();
    }
}

/// <summary>Confirm something that can't be undone by typing a word; optionally with a tick box (e.g. a final snapshot).</summary>
internal sealed class TypedConfirmForm : VolumeDialog
{
    private readonly TextBox _typed = new() { Width = 200 };
    private readonly ThemedCheckBox _option = new() { AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
    private readonly string _word;
    public bool OptionChecked => _option.Checked;

    public TypedConfirmForm(string title, string message, IEnumerable<string> items, string word, string option = null, bool optionDefault = true)
        : base(title, title)
    {
        _word = word;
        var list = items.ToList();
        var listing = new TextBox
        {
            Width = Width1, Height = Math.Min(160, 22 + 18 * list.Count), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            Text = string.Join("\r\n", list), BorderStyle = BorderStyle.None,
        };
        Body.Controls.Add(new Label { Text = message, AutoSize = true, UseMnemonic = false, MaximumSize = new Size(Width1, 0), Margin = new Padding(0, 0, 0, 8) });
        Body.Controls.Add(listing);
        if (option != null)
        {
            _option.Text = option;
            _option.Checked = optionDefault;
            Body.Controls.Add(_option);
        }
        Body.Controls.Add(Caption($"Type {word} to confirm"));
        Body.Controls.Add(_typed);
        Ok.Enabled = false;
        _typed.TextChanged += (_, _) => Ok.Enabled = string.Equals(_typed.Text.Trim(), _word, StringComparison.OrdinalIgnoreCase);
        Finish();
        Shown += (_, _) => _typed.Focus();
    }
}

/// <summary>Change a volume's size, type, IOPS and throughput (within EBS's limits; disks only grow).</summary>
internal sealed class ModifyVolumeForm : VolumeDialog
{
    private readonly VolumeRow _volume;
    private readonly Dictionary<string, double> _prices;
    private readonly NumericUpDown _size = new() { Width = 100, Maximum = 65536, ThousandsSeparator = true };
    private readonly ComboBox _type = new() { Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _iops = new() { Width = 100, Maximum = 256000, Increment = 500, ThousandsSeparator = true };
    private readonly NumericUpDown _throughput = new() { Width = 100, Maximum = 1000, Increment = 25 };
    private readonly Label _cost = new() { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 10, 0, 0) };
    private readonly Label _problem = Problem();

    public int SizeGb => (int)_size.Value;
    public string NewType => _type.SelectedItem as string ?? _volume.Type;
    public int Iops => (int)_iops.Value;
    public int Throughput => (int)_throughput.Value;

    public ModifyVolumeForm(VolumeRow volume, Dictionary<string, double> prices) : base($"Modify {volume.Label}", "Modify")
    {
        _volume = volume;
        _prices = prices;
        _type.Items.AddRange(VolumeRules.Types);
        _size.Minimum = Math.Max(1, volume.SizeGb);   // disks only grow
        _size.Value = Math.Max(_size.Minimum, volume.SizeGb);
        _type.SelectedItem = VolumeRules.Types.Contains(volume.Type) ? volume.Type : "gp3";
        _iops.Value = Math.Clamp(volume.Iops > 0 ? volume.Iops : 3000, 0, 256000);
        _throughput.Value = Math.Clamp(volume.Throughput > 0 ? volume.Throughput : 125, 0, 1000);
        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0) };
        void Add(string caption, Control c) { grid.Controls.Add(new Label { Text = caption, AutoSize = true, Margin = new Padding(0, 6, 12, 4) }); grid.Controls.Add(c); }
        Add("Size (GiB)", _size);
        Add("Type", _type);
        Add("IOPS", _iops);
        Add("Throughput (MB/s)", _throughput);
        Body.Controls.AddRange(new Control[]
        {
            new Label { Text = $"Now: {volume.SizeGb} GiB {volume.Type}" + (volume.Iops > 0 ? $", {volume.Iops:N0} IOPS" : "") +
                              (volume.Throughput > 0 ? $", {volume.Throughput} MB/s" : ""), AutoSize = true, Tag = "muted", Margin = new Padding(0, 0, 0, 8) },
            grid, _cost, _problem,
            Hint("Changes apply while the volume is in use, and AWS allows one change per volume every 6 hours. A bigger disk " +
                 "needs its partition extended inside the server (the Server window's Disks page can do that for Windows)."),
        });
        foreach (var c in new Control[] { _size, _iops, _throughput }) c.TextChanged += (_, _) => Recalc();
        _size.ValueChanged += (_, _) => Recalc();
        _iops.ValueChanged += (_, _) => Recalc();
        _throughput.ValueChanged += (_, _) => Recalc();
        _type.SelectedIndexChanged += (_, _) => Recalc();
        Recalc();
        Finish();
    }

    private void Recalc()
    {
        _iops.Enabled = VolumeRules.HasIops(NewType);
        _throughput.Enabled = VolumeRules.HasThroughput(NewType);
        string problem = VolumeRules.Check(NewType, SizeGb, Iops, Throughput);
        _problem.Text = problem ?? "";
        _problem.Visible = problem != null;
        bool changed = SizeGb != _volume.SizeGb || NewType != _volume.Type || (VolumeRules.HasIops(NewType) && Iops != _volume.Iops) ||
                       (VolumeRules.HasThroughput(NewType) && Throughput != _volume.Throughput);
        Ok.Enabled = problem == null && changed;
        double before = _volume.MonthlyCost, after = VolumeAdmin.MonthlyCost(_prices, NewType, SizeGb, Iops, Throughput);
        _cost.Text = after > 0 ? $"About ${after:N2}/month" + (before > 0 ? $" (now ${before:N2})" : "") : "";
    }
}

/// <summary>Pick one of a list (a server to attach to, a region to copy to ...).</summary>
internal sealed class ChoiceForm : VolumeDialog
{
    private readonly ComboBox _choices = new() { Width = Width1, DropDownStyle = ComboBoxStyle.DropDownList };
    public object Choice => _choices.SelectedItem;

    public ChoiceForm(string title, string prompt, IEnumerable<object> choices, string okText, string note = null) : base(title, okText)
    {
        foreach (var c in choices) _choices.Items.Add(c);
        if (_choices.Items.Count > 0) _choices.SelectedIndex = 0;
        Ok.Enabled = _choices.Items.Count > 0;
        Body.Controls.Add(Caption(prompt, 0));
        Body.Controls.Add(_choices);
        if (note != null) Body.Controls.Add(Hint(note));
        Finish();
    }
}
