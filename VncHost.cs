using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SwitchboardRM;

/// <summary>
/// noVNC speaks VNC over WebSockets, so each VNC tab gets a small relay: a WebSocket server on this PC's loopback only,
/// accepting only connections that carry its random token, passing bytes to and from the VNC server (directly, or the
/// local end of a Session Manager tunnel).
/// </summary>
internal sealed class VncRelay : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly string _host;
    private readonly int _targetPort;

    public int Port { get; }
    public string Token { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public VncRelay(string host, int port)
    {
        _host = host;
        _targetPort = port;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                string request = await ReadHeadersAsync(stream);
                var lines = request.Split("\r\n");
                string path = lines.Length > 0 && lines[0].Split(' ') is { Length: >= 2 } first ? first[1] : "";
                string Header(string name) => lines.Skip(1).Select(l => l.Split(':', 2))
                    .Where(p => p.Length == 2 && p[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    .Select(p => p[1].Trim()).FirstOrDefault() ?? "";
                string key = Header("Sec-WebSocket-Key");
                if (path.Trim('/') != Token || key.Length == 0)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n"));
                    return;
                }
                string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                string protocol = Header("Sec-WebSocket-Protocol").Split(',').Select(p => p.Trim()).FirstOrDefault(p => p == "binary");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                    $"Sec-WebSocket-Accept: {accept}\r\n" + (protocol != null ? $"Sec-WebSocket-Protocol: {protocol}\r\n" : "") + "\r\n"));

                using var socket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
                {
                    IsServer = true, SubProtocol = protocol, KeepAliveInterval = TimeSpan.FromSeconds(30),
                });
                using var target = new TcpClient { NoDelay = true };
                try { await target.ConnectAsync(_host, _targetPort, _cts.Token); }
                catch (Exception ex)
                {
                    Log.Write($"VNC: couldn't reach {_host}:{_targetPort}: {ex.Message}");
                    await socket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "Couldn't reach the VNC server", CancellationToken.None);
                    return;
                }
                var tcp = target.GetStream();
                var toServer = Task.Run(async () =>
                {
                    var buffer = new byte[64 * 1024];
                    while (true)
                    {
                        var r = await socket.ReceiveAsync(buffer.AsMemory(), _cts.Token);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        await tcp.WriteAsync(buffer.AsMemory(0, r.Count), _cts.Token);
                    }
                });
                var toBrowser = Task.Run(async () =>
                {
                    var buffer = new byte[64 * 1024];
                    int n;
                    while ((n = await tcp.ReadAsync(buffer, _cts.Token)) > 0)
                        await socket.SendAsync(buffer.AsMemory(0, n), WebSocketMessageType.Binary, true, _cts.Token);
                });
                await Task.WhenAny(toServer, toBrowser);
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Log.Write("VNC relay: " + ex.Message); }
            catch (OperationCanceledException) { }
        }
    }

    /// <summary>The HTTP upgrade request, up to the blank line (8 KB at most).</summary>
    private static async Task<string> ReadHeadersAsync(NetworkStream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 8192 && await stream.ReadAsync(one) == 1)
        {
            bytes.Add(one[0]);
            int c = bytes.Count;
            if (c >= 4 && bytes[c - 4] == '\r' && bytes[c - 3] == '\n' && bytes[c - 2] == '\r' && bytes[c - 1] == '\n') break;
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
    }
}

/// <summary>
/// A VNC tab: noVNC in WebView2, its files served from the .exe (embedded resources) at http://switchboard.vnc/, so
/// nothing is fetched from the network. Talks to the page with web messages.
/// </summary>
internal sealed class VncHost : UserControl
{
    private const string Origin = "http://switchboard.vnc/";
    private static Task<CoreWebView2Environment> _env;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private VncRelay _relay;
    private bool _ready;

    /// <summary>The server wants a password (and a user name if true).</summary>
    public event Action<bool> CredentialsRequired;
    public event Action Connected;
    /// <summary>Clean = the session ended normally (not lost).</summary>
    public event Action<bool> Disconnected;
    public event Action<string> SecurityFailure;
    public event Action ReconnectRequested;

    public VncHost()
    {
        _web.DefaultBackgroundColor = Theme.Current.Back;
        Controls.Add(_web);
    }

    /// <summary>Connect (again) to host:port, through a fresh relay.</summary>
    public async Task StartAsync(string host, int port)
    {
        _relay?.Dispose();
        _relay = new VncRelay(host, port);
        if (!_ready)
        {
            _env ??= CoreWebView2Environment.CreateAsync(null,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.DataName, "Vnc"));
            await _web.EnsureCoreWebView2Async(await _env);
            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.AddWebResourceRequestedFilter(Origin + "*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += ServeEmbedded;
            core.WebMessageReceived += OnMessage;
            _ready = true;
        }
        var p = Theme.Current;
        string Hex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";
        _web.CoreWebView2.Navigate($"{Origin}vnc.html?port={_relay.Port}&token={_relay.Token}" +
                                   $"&bg={Uri.EscapeDataString(Hex(p.Back))}&fg={Uri.EscapeDataString(Hex(p.Text))}" +
                                   $"&accent={Uri.EscapeDataString(Hex(p.Accent))}&accentfg={Uri.EscapeDataString(Hex(p.AccentText))}");
    }

    /// <summary>The page and noVNC's files, from the .exe: resources named SwitchboardRM.vnc\&lt;path&gt; (see the .csproj).</summary>
    private void ServeEmbedded(object sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            string path = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
            string name = "SwitchboardRM.vnc\\" + Uri.UnescapeDataString(path).Replace('/', '\\');
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream == null)
            {
                e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                return;
            }
            string type = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".html" => "text/html; charset=utf-8",
                ".js" => "text/javascript; charset=utf-8",
                ".css" => "text/css",
                _ => "application/octet-stream",
            };
            e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {type}");
        }
        catch (Exception ex) { Log.Write("VNC page: " + ex.Message); }
    }

    private void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var m = doc.RootElement;
            switch (m.GetProperty("t").GetString())
            {
                case "credentials": CredentialsRequired?.Invoke(m.TryGetProperty("user", out var u) && u.GetBoolean()); break;
                case "connected": Connected?.Invoke(); break;
                case "disconnected": Disconnected?.Invoke(m.TryGetProperty("clean", out var c) && c.GetBoolean()); break;
                case "securityfailure": SecurityFailure?.Invoke(m.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : ""); break;
                case "reconnect": ReconnectRequested?.Invoke(); break;
                case "clipboard":
                    string text = m.GetProperty("text").GetString() ?? "";
                    if (text.Length > 0) { try { Clipboard.SetText(text); } catch { } }   // copied on the remote machine: here too
                    break;
            }
        }
        catch (Exception ex) { Log.Write("VNC message: " + ex.Message); }
    }

    private void Post(object message)
    {
        if (_ready) _web.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    public void SendCredentials(string user, string password) => Post(new { t = "credentials", user = user ?? "", pass = password ?? "" });
    public void SendCtrlAltDel() => Post(new { t = "cad" });
    /// <summary>Put text on the remote machine's clipboard (then paste it there).</summary>
    public void SendClipboard(string text) => Post(new { t = "paste", text });
    public void FocusScreen() { try { _web.Focus(); Post(new { t = "focus" }); } catch { } }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { Post(new { t = "close" }); } catch { }
            _relay?.Dispose();
            _web.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>The VNC sign-in: a password, plus a user name when the server asks for one; optionally saved.</summary>
internal sealed class VncLoginForm : Form
{
    private readonly TextBox _user = new() { Width = 320 };
    private readonly TextBox _password = new() { Width = 320, UseSystemPasswordChar = true };
    private readonly ThemedCheckBox _save = new() { Text = "Save the password in Windows Credential Manager", AutoSize = true, Margin = new Padding(0, 10, 0, 0) };

    public string User => _user.Text.Trim();
    public string Password => _password.Text;
    public bool SavePassword => _save.Checked;

    public VncLoginForm(string serverName, bool needUser, string user, string problem)
    {
        Text = $"Sign in to {serverName} (VNC)";
        if (AppIcon.Icon != null) Icon = AppIcon.Icon;
        Font = new Font("Segoe UI", 9.5F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _user.Text = user ?? "";

        var body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(20, 16, 20, 16) };
        if (!string.IsNullOrEmpty(problem))
            body.Controls.Add(new Label { Text = problem, AutoSize = true, UseMnemonic = false, Tag = "accent", MaximumSize = new Size(320, 0), Margin = new Padding(0, 0, 0, 10) });
        if (needUser)
        {
            body.Controls.Add(new Label { Text = "User name", AutoSize = true, Margin = new Padding(0, 0, 0, 3) });
            body.Controls.Add(_user);
        }
        body.Controls.Add(new Label { Text = "Password", AutoSize = true, Margin = new Padding(0, needUser ? 10 : 0, 0, 3) });
        body.Controls.Add(_password);
        body.Controls.Add(_save);
        var ok = new Button { Text = "Sign in", Tag = "primary", AutoSize = true, MinimumSize = new Size(100, 32), Margin = new Padding(0, 0, 8, 0), DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(90, 32), DialogResult = DialogResult.Cancel, Margin = new Padding(0) };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0, 14, 0, 0) };
        buttons.Controls.AddRange(new Control[] { ok, cancel });
        body.Controls.Add(buttons);
        Controls.Add(body);
        AcceptButton = ok;
        CancelButton = cancel;
        Theme.AddPasswordReveal(_password);
        Theme.Apply(this, Theme.Current.IsDark);
        Shown += (_, _) => (needUser && _user.Text.Length == 0 ? _user : _password).Focus();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }
}
