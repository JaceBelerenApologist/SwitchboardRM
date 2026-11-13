using System.Drawing;
using System.Windows.Forms;

namespace SwitchboardRM;

/// <summary>
/// Terminate a server: says what's deleted and what's kept, refuses while termination protection is on, and only
/// enables Terminate once the server's name is typed exactly.
/// </summary>
internal sealed class TerminateForm : Form
{
    private readonly string _profile, _region, _instanceId, _name;
    private readonly Label _info = new() { AutoSize = true, UseMnemonic = false, MaximumSize = new Size(440, 0), Margin = new Padding(0, 0, 0, 10) };
    private readonly TextBox _confirm = new() { Width = 300, Enabled = false };
    private readonly Button _terminate = new() { Text = "Terminate", AutoSize = true, MinimumSize = new Size(110, 32), Enabled = false, Margin = new Padding(0, 0, 8, 0) };
    private readonly Label _status = new() { AutoSize = true, UseMnemonic = false, Tag = "accent", MaximumSize = new Size(440, 0), Margin = new Padding(0, 8, 0, 0) };

    public bool Terminated { get; private set; }

    public TerminateForm(string profile, string region, string instanceId, string name)
    {
        _profile = profile;
        _region = region;
        _instanceId = instanceId;
        _name = name;
        Text = $"Terminate {name}";
        if (AppIcon.Icon != null) Icon = AppIcon.Icon;
        Font = new Font("Segoe UI", 9.5F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _info.Text = "Reading the server ...";
        var body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(20, 16, 20, 16) };
        body.Controls.Add(new Label
        {
            Text = $"Terminating {name} ({instanceId}) deletes the server permanently. It can't be undone.",
            AutoSize = true, UseMnemonic = false, MaximumSize = new Size(440, 0), Font = new Font("Segoe UI Semibold", 10F), Margin = new Padding(0, 0, 0, 10),
        });
        body.Controls.Add(_info);
        body.Controls.Add(new Label { Text = $"Type the server's name ({name}) to confirm:", AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 4, 0, 4) });
        body.Controls.Add(_confirm);
        body.Controls.Add(_status);
        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(90, 32), DialogResult = DialogResult.Cancel, Margin = new Padding(0) };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0, 14, 0, 0) };
        buttons.Controls.AddRange(new Control[] { _terminate, cancel });
        body.Controls.Add(buttons);
        Controls.Add(body);
        CancelButton = cancel;

        _confirm.TextChanged += (_, _) => _terminate.Enabled = _confirm.Text.Trim() == _name;
        _terminate.Click += async (_, _) => await TerminateAsync();
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
        try
        {
            var (isProtected, deleted, kept) = await ServerAdmin.TerminationInfoAsync(_profile, _region, _instanceId, CancellationToken.None);
            var lines = new List<string>();
            if (deleted.Count > 0) lines.Add("Deleted with it: " + string.Join(", ", deleted) + ".");
            if (kept.Count > 0) lines.Add("Kept (and still charged): " + string.Join(", ", kept) + ".");
            lines.Add("Its images and snapshots are kept.");
            if (isProtected)
            {
                lines.Add("");
                lines.Add("Termination protection is on, so it can't be terminated. Turn it off on the Server window's Protection page first.");
            }
            _info.Text = string.Join(Environment.NewLine, lines);
            _confirm.Enabled = !isProtected;
            if (!isProtected) _confirm.Focus();
        }
        catch (Exception ex) { _info.Text = "Couldn't read the server: " + Aws.Describe(ex); }
    }

    private async Task TerminateAsync()
    {
        if (_confirm.Text.Trim() != _name) return;
        _terminate.Enabled = _confirm.Enabled = false;
        _status.Text = "Terminating ...";
        try
        {
            await ServerAdmin.TerminateAsync(_profile, _region, _instanceId, CancellationToken.None);
            Terminated = true;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _status.Text = "Not terminated: " + Aws.Describe(ex);
            _confirm.Enabled = true;
            _terminate.Enabled = _confirm.Text.Trim() == _name;
        }
    }
}
