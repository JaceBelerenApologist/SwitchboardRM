using System.Drawing;
using System.Windows.Forms;

namespace SwitchboardRM;

/// <summary>An active port forward: localhost:LocalPort reaches Host:RemotePort through a server's Session Manager.</summary>
internal sealed class PortForward
{
    public Instance Server;
    public Tunnel Tunnel;
    public string Host = "";      // "" = the server itself
    public int RemotePort;
    public DateTime Started = DateTime.Now;

    public int LocalPort => Tunnel?.Port ?? 0;
    public bool Alive => Tunnel?.Proc is { HasExited: false };
    public string Target => (Host.Length > 0 ? Host : Server.Name) + ":" + RemotePort;
    public string LocalAddress => $"localhost:{LocalPort}";
    public bool LooksLikeWeb => RemotePort is 80 or 443 or 8080 or 8443 or 3000 or 5000 or 9090 or 9443;
}

/// <summary>Port forwarding: what to forward through a server, to which local port.</summary>
internal sealed class PortForwardForm : Form
{
    private static readonly (string Name, int Port)[] Presets =
    {
        ("Custom", 0), ("SQL Server", 1433), ("HTTPS", 443), ("HTTP", 80), ("Remote Desktop", 3389), ("SSH", 22),
        ("MySQL / MariaDB", 3306), ("PostgreSQL", 5432), ("WinRM (HTTP)", 5985), ("WinRM (HTTPS)", 5986), ("Oracle", 1521), ("Redis", 6379),
    };

    private readonly RadioButton _toServer = new() { AutoSize = true, Checked = true };
    private readonly RadioButton _toHost = new() { Text = "A host reached through it (an RDS database, an internal site ...):", AutoSize = true };
    private readonly TextBox _host = new() { Width = 360, PlaceholderText = "Host name or IP address", Enabled = false, Margin = new Padding(20, 2, 0, 0) };
    private readonly ComboBox _preset = new() { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _remotePort = new() { Minimum = 1, Maximum = 65535, Value = 1433, Width = 90 };
    private readonly NumericUpDown _localPort = new() { Minimum = 0, Maximum = 65535, Value = 0, Width = 90 };
    private readonly Label _error = new() { AutoSize = true, UseMnemonic = false, Tag = "accent", MaximumSize = new Size(380, 0), Margin = new Padding(0, 8, 0, 0) };

    public string Host => _toHost.Checked ? _host.Text.Trim() : "";
    public int RemotePort => (int)_remotePort.Value;
    public int LocalPort => (int)_localPort.Value;

    public PortForwardForm(string serverName)
    {
        Text = $"Port forwarding through {serverName}";
        if (AppIcon.Icon != null) Icon = AppIcon.Icon;
        Font = new Font("Segoe UI", 9.5F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _toServer.Text = $"{serverName} itself";
        foreach (var p in Presets) _preset.Items.Add(p.Port > 0 ? $"{p.Name} ({p.Port})" : p.Name);
        _preset.SelectedIndex = 1;
        _preset.SelectedIndexChanged += (_, _) => { if (Presets[_preset.SelectedIndex].Port > 0) _remotePort.Value = Presets[_preset.SelectedIndex].Port; };
        _remotePort.ValueChanged += (_, _) =>
        {
            int match = Array.FindIndex(Presets, p => p.Port == (int)_remotePort.Value);
            if (_preset.SelectedIndex != (match < 0 ? 0 : match)) _preset.SelectedIndex = match < 0 ? 0 : match;
        };
        _toHost.CheckedChanged += (_, _) => { _host.Enabled = _toHost.Checked; if (_toHost.Checked) _host.Focus(); };

        var body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(20, 16, 20, 16) };
        body.Controls.Add(Heading("Forward to", first: true));
        body.Controls.Add(_toServer);
        body.Controls.Add(_toHost);
        body.Controls.Add(_host);
        body.Controls.Add(Heading("Port"));
        body.Controls.Add(Row(_preset, _remotePort));
        body.Controls.Add(Heading("Local port on this PC"));
        body.Controls.Add(Row(_localPort, new Label { Text = "0 = any free port", AutoSize = true, Tag = "muted", Margin = new Padding(8, 6, 0, 0) }));
        body.Controls.Add(new Label
        {
            Text = "The forward opens as localhost:<local port> on this PC and lasts until you stop it or close Switchboard. " +
                   "A host behind the server needs the server's security groups and network to reach it.",
            AutoSize = true, UseMnemonic = false, Tag = "muted", MaximumSize = new Size(380, 0), Margin = new Padding(0, 10, 0, 0),
        });
        body.Controls.Add(_error);

        var start = new Button { Text = "Start forwarding", Tag = "primary", AutoSize = true, MinimumSize = new Size(130, 32), Margin = new Padding(0, 0, 8, 0) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(90, 32), DialogResult = DialogResult.Cancel, Margin = new Padding(0) };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0, 14, 0, 0) };
        buttons.Controls.AddRange(new Control[] { start, cancel });
        body.Controls.Add(buttons);
        Controls.Add(body);
        AcceptButton = start;
        CancelButton = cancel;
        start.Click += (_, _) =>
        {
            if (_toHost.Checked && Host.Length == 0) { _error.Text = "Enter the host to forward to."; return; }
            if (Host.Any(char.IsWhiteSpace)) { _error.Text = "The host can't contain spaces."; return; }
            DialogResult = DialogResult.OK;
            Close();
        };
        Theme.Apply(this, Theme.Current.IsDark);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }

    private static Label Heading(string text, bool first = false) =>
        new() { Text = text, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, first ? 0 : 12, 0, 4) };

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        row.Controls.AddRange(controls);
        return row;
    }
}

/// <summary>The Port forwards panel: active forwards, with Copy address, Open in browser and Stop.</summary>
internal sealed class ForwardsForm : Form
{
    private readonly List<PortForward> _forwards;
    private readonly Action<PortForward> _stop;
    private readonly ListView _list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false };
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = 3000 };

    public ForwardsForm(List<PortForward> forwards, Action<PortForward> stop)
    {
        _forwards = forwards;
        _stop = stop;
        Text = "Port forwards";
        if (AppIcon.Icon != null) Icon = AppIcon.Icon;
        Font = new Font("Segoe UI", 9.5F);
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(LogicalToDeviceUnits(700), LogicalToDeviceUnits(260));
        foreach (var (title, width) in new[] { ("Local address", 140), ("Forwards to", 230), ("Through", 150), ("Since", 70), ("State", 80) })
            _list.Columns.Add(title, width);

        var copy = new Button { Text = "Copy address", AutoSize = true, MinimumSize = new Size(110, 30), Margin = new Padding(0, 0, 8, 0) };
        var open = new Button { Text = "Open in browser", AutoSize = true, MinimumSize = new Size(120, 30), Margin = new Padding(0, 0, 8, 0) };
        var stopButton = new Button { Text = "Stop", AutoSize = true, MinimumSize = new Size(80, 30), Margin = new Padding(0) };
        copy.Click += (_, _) => { if (Selected() is { } f) { try { Clipboard.SetText(f.LocalAddress); } catch { } } };
        open.Click += (_, _) =>
        {
            if (Selected() is not { } f) return;
            string scheme = f.RemotePort is 443 or 8443 or 9443 ? "https" : "http";
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"{scheme}://localhost:{f.LocalPort}/") { UseShellExecute = true }); }
            catch (Exception ex) { Log.Write("Couldn't open the browser: " + ex.Message); }
        };
        stopButton.Click += (_, _) => { if (Selected() is { } f) { _stop(f); Fill(); } };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(10, 8, 10, 8) };
        buttons.Controls.AddRange(new Control[] { copy, open, stopButton });
        Controls.Add(_list);
        Controls.Add(buttons);
        _list.SelectedIndexChanged += (_, _) => open.Enabled = Selected()?.LooksLikeWeb == true;
        _refresh.Tick += (_, _) => Fill();
        Theme.Apply(this, Theme.Current.IsDark);
        Fill();
        _refresh.Start();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _refresh.Stop();
        _refresh.Dispose();
        base.OnFormClosed(e);
    }

    private PortForward Selected() => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as PortForward : null;

    public void Fill()
    {
        var selected = Selected();
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var f in _forwards)
        {
            var item = new ListViewItem(new[] { f.LocalAddress, f.Target, f.Server.Name, f.Started.ToString("HH:mm"), f.Alive ? "active" : "ended" }) { Tag = f };
            _list.Items.Add(item);
            if (f == selected) item.Selected = true;
        }
        if (_list.SelectedItems.Count == 0 && _list.Items.Count > 0) _list.Items[0].Selected = true;
        _list.EndUpdate();
    }
}
