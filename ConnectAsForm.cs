using System.Drawing;
using System.Windows.Forms;

namespace SwitchboardRM;

/// <summary>
/// Connect as: another user for one connection, in a tab of its own. Remote Desktop also takes a password (optional:
/// left empty, the password saved for that user is used, or Remote Desktop asks) and can save it to Windows Credential
/// Manager. SSH only takes the login name; its password or key is entered in the terminal as usual.
/// </summary>
internal sealed class ConnectAsForm : Form
{
    private readonly ComboBox _user = new() { Width = 340, DropDownStyle = ComboBoxStyle.DropDown, AutoCompleteMode = AutoCompleteMode.SuggestAppend, AutoCompleteSource = AutoCompleteSource.ListItems };
    private readonly TextBox _password = new() { Width = 340, UseSystemPasswordChar = true };
    private readonly ThemedCheckBox _save = new() { Text = "Save the password in Windows Credential Manager", AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
    private readonly Label _error = new() { AutoSize = true, UseMnemonic = false, Tag = "accent", MaximumSize = new Size(340, 0), Margin = new Padding(0, 8, 0, 0) };
    private readonly bool _rdp;

    public string User => _user.Text.Trim();
    public string Password => _rdp ? _password.Text : "";
    public bool SavePassword => _rdp && _save.Checked;

    public ConnectAsForm(string serverName, bool remoteDesktop, IEnumerable<string> recentUsers)
    {
        _rdp = remoteDesktop;
        Text = $"Connect to {serverName} as";
        if (AppIcon.Icon != null) Icon = AppIcon.Icon;
        Font = new Font("Segoe UI", 9.5F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _user.Items.AddRange(recentUsers.Where(u => !string.IsNullOrWhiteSpace(u)).Cast<object>().ToArray());
        _password.PlaceholderText = "Optional";
        Theme.AddPasswordReveal(_password);

        var body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(20, 16, 20, 16) };
        body.Controls.Add(Heading("User", first: true));
        body.Controls.Add(_user);
        body.Controls.Add(Muted(remoteDesktop ? "DOMAIN\\user or user@domain" : "The login name on the server"));
        if (remoteDesktop)
        {
            body.Controls.Add(Heading("Password"));
            body.Controls.Add(_password);
            body.Controls.Add(Muted("Left empty: the password saved for this user is used, or Remote Desktop asks. " +
                                    "Not saved, it's kept only for this tab."));
            body.Controls.Add(_save);
        }
        else
        {
            body.Controls.Add(Muted("The password or key is asked for in the terminal, as usual."));
        }
        body.Controls.Add(_error);

        var connect = new Button { Text = "Connect", Tag = "primary", AutoSize = true, MinimumSize = new Size(100, 32), Margin = new Padding(0, 0, 8, 0) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(90, 32), DialogResult = DialogResult.Cancel, Margin = new Padding(0) };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0, 14, 0, 0) };
        buttons.Controls.AddRange(new Control[] { connect, cancel });
        body.Controls.Add(buttons);
        Controls.Add(body);
        AcceptButton = connect;
        CancelButton = cancel;

        connect.Click += (_, _) =>
        {
            if (User.Length == 0) { _error.Text = "Enter the user to connect as."; return; }
            if (SavePassword && _password.Text.Length == 0) { _error.Text = "Enter the password to save, or untick saving it."; return; }
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

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _user.Focus();
    }

    private static Label Heading(string text, bool first = false) =>
        new() { Text = text, AutoSize = true, UseMnemonic = false, Margin = new Padding(0, first ? 0 : 10, 0, 3) };

    private static Label Muted(string text) =>
        new() { Text = text, AutoSize = true, UseMnemonic = false, Tag = "muted", MaximumSize = new Size(340, 0), Margin = new Padding(0, 3, 0, 0) };
}
