using System.Drawing;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows.Forms;

namespace SwitchboardRM;

/// <summary>How to connect: Remote Desktop, or an SSH terminal.</summary>
internal enum Protocol { Rdp, Ssh, Local }   // Local = a shell on this PC (Command Prompt, PowerShell, ...)

/// <summary>A plain Remote Desktop or SSH connection (no AWS), saved by the user.</summary>
internal sealed class SavedConnection
{
    /// <summary>"RDP" or "SSH". Files from before SSH support have no value here and mean RDP.</summary>
    public string Protocol { get; set; } = "RDP";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 3389;
    /// <summary>Empty = sign in as the default RDP user from Settings.</summary>
    public string User { get; set; } = "";
    /// <summary>Optional folder-like grouping, e.g. "Datacenter / SQL" (imports bring theirs along).</summary>
    public string Group { get; set; } = "";

    public bool IsSsh => string.Equals(Protocol, "SSH", StringComparison.OrdinalIgnoreCase);
    public int DefaultPort => IsSsh ? 22 : 3389;
    public SwitchboardRM.Protocol Kind => IsSsh ? SwitchboardRM.Protocol.Ssh : SwitchboardRM.Protocol.Rdp;

    /// <summary>"server" or "server:3390" (port only when it isn't the protocol's usual one).</summary>
    public string Address => Port == DefaultPort ? Host : $"{Host}:{Port}";
}

/// <summary>Saved connections live in their own file: %APPDATA%\SwitchboardRM\connections.json.</summary>
internal static class ConnectionStore
{
    public static string FilePath => Path.Combine(AppSettings.Folder, "connections.json");

    public static List<SavedConnection> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<SavedConnection>();
            return JsonSerializer.Deserialize<List<SavedConnection>>(File.ReadAllText(FilePath))?
                       .Where(c => !string.IsNullOrWhiteSpace(c.Host)).ToList()
                   ?? new List<SavedConnection>();
        }
        catch (Exception ex)
        {
            Log.Write("Could not read saved connections: " + ex.Message);
            return new List<SavedConnection>();
        }
    }

    public static void Save(List<SavedConnection> list)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Write("Could not save connections: " + ex.Message); }
    }

    /// <summary>A list row for a saved connection.</summary>
    public static Instance ToInstance(SavedConnection c, bool? reachable) => new()
    {
        Name = string.IsNullOrWhiteSpace(c.Name) ? c.Host : c.Name,
        Id = "saved:" + c.Id,
        Type = c.IsSsh ? "Saved SSH" : "Saved RDP",
        Region = "Saved",
        Ip = c.Address,
        Platform = c.IsSsh ? "Linux" : "Windows",
        Ping = reachable switch { true => "Online", false => "Unreachable", _ => "" },
        Saved = c,
    };

    /// <summary>Does anything answer on the RDP port? (A quick TCP connect; doesn't sign in.)</summary>
    public static async Task<bool> IsReachableAsync(string host, int port, int timeoutMs = 2500)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            await client.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Split "server:3390" into host and port. IPv6 addresses (several colons) are left alone.</summary>
    public static (string Host, int Port) ParseAddress(string text, int defaultPort = 3389)
    {
        text = (text ?? "").Trim();
        int colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon && int.TryParse(text[(colon + 1)..], out int port) && port is > 0 and < 65536)
            return (text[..colon].Trim(), port);
        return (text.Trim('[', ']'), defaultPort);
    }
}

/// <summary>Add or edit a saved connection.</summary>
internal sealed class ConnectionForm : Form
{
    public SavedConnection Result { get; private set; }
    /// <summary>A password typed in the dialog (to save in Credential Manager), or "".</summary>
    public string Password => _txtPass.Text;
    /// <summary>The user the connection signs in as (its own, or the default).</summary>
    public string EffectiveUser => _txtUser.Text.Trim().Length > 0 ? _txtUser.Text.Trim() : _defaultUser;

    private readonly ComboBox _cboProtocol = new() { Width = 340, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _lblPass = new() { Text = "Password", AutoSize = true, Margin = new Padding(0, 8, 0, 3) };
    private readonly Label _lblPassNote = new()
    {
        AutoSize = true, MaximumSize = new Size(340, 0), Tag = "muted", Margin = new Padding(0, 6, 0, 0),
        Text = "A password typed here is saved in Windows Credential Manager for that user, " +
               "the same as Settings > RDP credentials.",
    };
    private readonly Label _lblSshNote = new()
    {
        AutoSize = true, MaximumSize = new Size(340, 0), Tag = "muted", Margin = new Padding(0, 6, 0, 0),
        Text = "SSH uses your keys (~/.ssh, the SSH agent, or the key file in Settings > SSH). " +
               "If the server wants a password, ssh asks for it in the terminal.",
    };
    private readonly TextBox _txtName = new() { Width = 340 };
    private readonly TextBox _txtGroup = new() { Width = 340, PlaceholderText = "Optional, e.g. Datacenter / SQL" };
    private readonly TextBox _txtAddress = new() { Width = 340 };
    private readonly TextBox _txtUser = new() { Width = 340 };
    private readonly TextBox _txtPass = new() { Width = 340, UseSystemPasswordChar = true };
    private readonly Label _lblError = new() { AutoSize = true, Tag = "accent", Margin = new Padding(0, 8, 0, 0) };
    private readonly string _defaultUser;
    private readonly SavedConnection _editing;

    public ConnectionForm(SavedConnection editing, string defaultUser)
    {
        _editing = editing;
        _defaultUser = (defaultUser ?? "").Trim();

        Text = editing == null ? "Add RDP connection" : "Edit RDP connection";
        if (AppIcon.Icon != null) Icon = AppIcon.Icon;
        Font = new Font("Segoe UI", 9.5F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _cboProtocol.Items.AddRange(new object[] { "Remote Desktop (RDP)", "SSH terminal" });
        _cboProtocol.SelectedIndex = editing is { IsSsh: true } ? 1 : 0;
        _cboProtocol.SelectedIndexChanged += (_, _) => UpdateProtocol();
        _txtAddress.PlaceholderText = "server.domain.local, 10.0.0.5 or host:3390";
        _txtName.PlaceholderText = "Optional; the computer name is used if blank";
        _txtUser.PlaceholderText = _defaultUser.Length > 0 ? $"Default: {_defaultUser}" : "Optional; Remote Desktop asks if blank";
        _txtUser.TextChanged += (_, _) => UpdatePasswordHint();

        if (editing != null)
        {
            _txtName.Text = editing.Name;
            _txtAddress.Text = editing.Address;
            _txtUser.Text = editing.User;
            _txtGroup.Text = editing.Group;
        }
        UpdatePasswordHint();
        UpdateProtocol();

        var save = new Button { Text = "Save", Tag = "primary", AutoSize = true, MinimumSize = new Size(96, 32), Margin = new Padding(0, 0, 8, 0) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(96, 32), Margin = new Padding(0), DialogResult = DialogResult.Cancel };
        save.Click += (_, _) => TrySave();
        AcceptButton = save;
        CancelButton = cancel;

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        buttons.Controls.AddRange(new Control[] { save, cancel });

        var body = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true,
            Padding = new Padding(22, 18, 22, 18),
        };
        body.Controls.AddRange(new Control[]
        {
            Field("Connect with"), _cboProtocol,
            Field("Computer"), _txtAddress,
            Field("Name"), _txtName,
            Field("Group"), _txtGroup,
            Field("User name"), _txtUser,
            _lblPass, _txtPass,
            _lblPassNote,
            _lblSshNote,
            _lblError,
            buttons,
        });
        Controls.Add(body);

        Theme.Apply(this, Theme.Current.IsDark);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }

    private static Label Field(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 8, 0, 3) };

    private void UpdatePasswordHint()
    {
        _txtPass.PlaceholderText = EffectiveUser.Length == 0 ? "Remote Desktop will ask"
            : CredentialStore.Exists(EffectiveUser) ? $"Saved for {EffectiveUser}; leave blank to keep it"
            : "Optional; leave blank to be asked";
    }

    private bool Ssh => _cboProtocol.SelectedIndex == 1;

    /// <summary>SSH doesn't take a password from here (ssh asks in the terminal), so swap the password field for a note.</summary>
    private void UpdateProtocol()
    {
        _lblPass.Visible = _txtPass.Visible = _lblPassNote.Visible = !Ssh;
        _lblSshNote.Visible = Ssh;
        _txtAddress.PlaceholderText = Ssh ? "server.domain.local, 10.0.0.5 or host:2222" : "server.domain.local, 10.0.0.5 or host:3390";
        _txtUser.PlaceholderText = Ssh
            ? "Optional; your Windows user name if blank"
            : _defaultUser.Length > 0 ? $"Default: {_defaultUser}" : "Optional; Remote Desktop asks if blank";
        if (Ssh) _txtPass.Text = "";
    }

    private void TrySave()
    {
        var (host, port) = ConnectionStore.ParseAddress(_txtAddress.Text, Ssh ? 22 : 3389);
        if (host.Length == 0)
        {
            _lblError.Text = "Type the computer's name or IP address.";
            _txtAddress.Focus();
            return;
        }
        if (_txtPass.Text.Length > 0 && EffectiveUser.Length == 0)
        {
            _lblError.Text = "Type a user name for the password to belong to.";
            _txtUser.Focus();
            return;
        }

        Result = new SavedConnection
        {
            Protocol = Ssh ? "SSH" : "RDP",
            Id = _editing?.Id ?? Guid.NewGuid().ToString("N"),
            Name = _txtName.Text.Trim(),
            Host = host,
            Port = port,
            User = _txtUser.Text.Trim(),
            Group = _txtGroup.Text.Trim(),
        };
        DialogResult = DialogResult.OK;
        Close();
    }
}
