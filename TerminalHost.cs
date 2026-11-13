using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SwitchboardRM;

/// <summary>
/// An SSH terminal in a tab: xterm.js (the terminal VS Code uses) in a WebView2 control, connected to ssh.exe
/// running in a Windows pseudo-console. Keys, passwords and host-key prompts are handled by ssh itself.
/// </summary>
internal sealed class TerminalHost : UserControl
{
    /// <summary>The program ended (e.g. the user typed "exit" or the connection dropped). Raised on the UI thread.</summary>
    public event Action<int> SessionEnded;
    /// <summary>The user pressed Enter after the session ended. Raised on the UI thread.</summary>
    public event Action ReconnectRequested;
    /// <summary>The user pressed Esc after the session ended: close the tab. Raised on the UI thread.</summary>
    public event Action CloseRequested;
    /// <summary>One of Switchboard's tab keys inside the terminal: "next", "prev", "close" or "newshell". UI thread.</summary>
    public event Action<string> AppShortcut;

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private PseudoConsole _pty;
    private string _workingDirectory;
    private IDictionary<string, string> _environment;
    /// <summary>What Enter does after the session ends, in the end-of-session message: "reconnect" or "restart".</summary>
    public string EndedAction = "reconnect";
    private string _pendingCommand;
    private int _cols = 120, _rows = 30;
    private bool _ready;
    private string _fontsSent;   // the fonts-folder family already given to this page
    private bool _ended;
    private readonly StringBuilder _buffer = new();   // output that arrived before the page was ready
    private readonly object _gate = new();

    private static Task<CoreWebView2Environment> _env;

    public TerminalHost()
    {
        Controls.Add(_web);
        _web.DefaultBackgroundColor = Theme.TerminalScheme is { } scheme   // no white flash while it loads
            ? System.Drawing.ColorTranslator.FromHtml(scheme.Background)
            : Theme.Current.IsDark ? Theme.Current.Back : Theme.Current.Surface;
    }

    /// <summary>Load the terminal page. Call Run() to start a command (it waits for the page if needed).</summary>
    public async Task StartAsync()
    {
        // one browser environment for every terminal tab, with its data kept in %LOCALAPPDATA%
        _env ??= CoreWebView2Environment.CreateAsync(null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.DataName, "WebView2"));
        await _web.EnsureCoreWebView2Async(await _env);

        var s = _web.CoreWebView2.Settings;
        s.AreDefaultContextMenusEnabled = false;
        s.AreDevToolsEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = false; // no F5 reload, Ctrl+P print, zoom ... (copy/paste still work)
        s.IsStatusBarEnabled = false;
        s.IsZoomControlEnabled = false;
        s.IsBuiltInErrorPageEnabled = false;

        _web.CoreWebView2.WebMessageReceived += OnMessage;
        _web.CoreWebView2.NavigateToString(BuildPage());
    }

    /// <summary>Run a command in the terminal now, or as soon as the page is ready.</summary>
    /// <summary>Run a command in a given folder with extra environment variables (used for local shells).</summary>
    public void Run(string commandLine, string workingDirectory, IDictionary<string, string> environment)
    {
        _workingDirectory = workingDirectory;
        _environment = environment;
        Run(commandLine);
    }

    public void Run(string commandLine)
    {
        _pty?.Dispose();
        _pty = null;
        _ended = false;
        if (!_ready) { _pendingCommand = commandLine; return; }
        RunCommand(commandLine);
    }

    /// <summary>Clear the screen and start again in the same tab (after the session ended).</summary>
    public void Reset()
    {
        _pty?.Dispose();
        _pty = null;
        _ended = false;
        Post("\x1bc");
    }

    /// <summary>Show an error line in red, and treat the session as ended (Enter retries).</summary>
    public void Fail(string text)
    {
        _ended = true;
        Post($"\x1b[31m{text}\x1b[0m\r\n\x1b[90m[Press Enter to try again, or Esc to close the tab.]\x1b[0m\r\n");
    }

    public void ApplyTheme()
    {
        if (_web.CoreWebView2 == null) return;
        SendFolderFonts();   // a fonts-folder font chosen as the terminal font: give the page its files first
        try
        {
            _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                t = "theme",
                theme = ThemeObject(),
                font = new { family = FontStack(), size = Theme.TerminalFontSize },
            }));
        }
        catch { }
    }

    public void FocusTerminal()
    {
        _web.Focus();
        try { _web.CoreWebView2?.PostWebMessageAsJson("{\"t\":\"focus\"}"); } catch { }
    }

    /// <summary>Write a line to the terminal from the app (e.g. "Opening tunnel ..."), dimmed.</summary>
    public void Notice(string text) => Post($"\x1b[90m{text}\x1b[0m\r\n");

    /// <summary>
    /// Restored output from the last session: shown dimmed, then a divider, above whatever runs next (text sent before
    /// the page is ready is queued and delivered first).
    /// </summary>
    public void Replay(string text, DateTime savedAt)
    {
        if (string.IsNullOrEmpty(text)) return;
        string body = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        Post($"\x1b[2m{body}\x1b[0m\r\n\x1b[90m—— restored from last session, {savedAt:yyyy-MM-dd HH:mm} ——\x1b[0m\r\n");
    }

    /// <summary>The terminal's scrollback as plain text (last 5,000 lines), or "" if it isn't available.</summary>
    public async Task<string> ScrollbackAsync()
    {
        try
        {
            if (!_ready || _web.CoreWebView2 == null) return "";
            string json = await _web.CoreWebView2.ExecuteScriptAsync("window.sbScrollback ? window.sbScrollback() : ''");
            return JsonSerializer.Deserialize<string>(json) ?? "";
        }
        catch (Exception ex)
        {
            Log.Write("Couldn't read a terminal's scrollback: " + ex.Message);
            return "";
        }
    }

    // ---------------------------------------------------------------- page <-> app

    private void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var m = doc.RootElement;
            switch (m.GetProperty("t").GetString())
            {
                case "ready":
                    _cols = m.GetProperty("cols").GetInt32();
                    _rows = m.GetProperty("rows").GetInt32();
                    _ready = true;
                    lock (_gate)
                    {
                        if (_buffer.Length > 0) { _web.CoreWebView2.PostWebMessageAsString(_buffer.ToString()); _buffer.Clear(); }
                    }
                    SendFolderFonts();
                    if (_pendingCommand != null) { var c = _pendingCommand; _pendingCommand = null; RunCommand(c); }
                    break;

                case "size":
                    _cols = m.GetProperty("cols").GetInt32();
                    _rows = m.GetProperty("rows").GetInt32();
                    _pty?.Resize(_cols, _rows);
                    break;

                case "in":
                    string d = m.GetProperty("d").GetString();
                    if (_ended)
                    {
                        // Esc on its own (arrow and function keys send longer sequences that also start with Esc)
                        if (d == "\x1b") CloseRequested?.Invoke();
                        else if (d.Contains('\r')) ReconnectRequested?.Invoke();
                        return;
                    }
                    _pty?.Write(d);
                    break;

                case "key":
                    AppShortcut?.Invoke(m.GetProperty("k").GetString());
                    break;

                case "copy":
                    string text = m.GetProperty("d").GetString();
                    if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text.Replace("\r\n", "\n").Replace("\n", "\r\n"));
                    break;

                case "paste":
                    if (Clipboard.ContainsText() && !_ended) _pty?.Write(Clipboard.GetText().Replace("\r\n", "\r"));
                    break;
            }
        }
        catch (Exception ex) { Log.Write("Terminal message: " + ex.Message); }
    }

    private void RunCommand(string commandLine)
    {
        try
        {
            PseudoConsole pty = null;
            pty = PseudoConsole.Start(commandLine, _cols, _rows,
                onOutput: Post,
                onExit: code => BeginInvokeSafe(() =>
                {
                    if (!ReferenceEquals(_pty, pty)) return; // an earlier session ending after a reconnect
                    _ended = true;
                    Post($"\r\n\x1b[90m[Session ended{(code != 0 ? $" (exit code {code})" : "")}. " +
                         $"Press Enter to {EndedAction}, or Esc to close the tab.]\x1b[0m\r\n");
                    SessionEnded?.Invoke(code);
                }),
                workingDirectory: _workingDirectory,
                environment: _environment);
            _pty = pty;
        }
        catch (Exception ex)
        {
            Log.Write("Terminal start failed: " + ex);
            _ended = true;
            Post($"\x1b[31mCouldn't start: {ex.Message}\x1b[0m\r\n");
        }
    }

    /// <summary>Send text to the terminal from any thread.</summary>
    private void Post(string text)
    {
        BeginInvokeSafe(() =>
        {
            if (!_ready || _web.CoreWebView2 == null)
            {
                lock (_gate) _buffer.Append(text);
                return;
            }
            try { _web.CoreWebView2.PostWebMessageAsString(text); } catch { }
        });
    }

    private void BeginInvokeSafe(Action a)
    {
        if (IsDisposed || Disposing) return;
        if (!IsHandleCreated || !InvokeRequired) { a(); return; }
        try { BeginInvoke(a); } catch (ObjectDisposedException) { } catch (InvalidOperationException) { }
    }

    private string BuildPage()
    {
        string page = Resource("terminal.html");
        return page
            .Replace("/*XTERM_CSS*/", Resource("xterm.css"))
            .Replace("/*XTERM_JS*/", Resource("xterm.js"))
            .Replace("/*FIT_JS*/", Resource("addon-fit.js"))
            .Replace("/*THEME*/", JsonSerializer.Serialize(ThemeObject()))
            .Replace("/*FONT_FAMILY*/", JsonSerializer.Serialize(FontStack()))
            .Replace("/*FONT_SIZE*/", Theme.TerminalFontSize.ToString());
    }

    /// <summary>
    /// If the terminal font comes from the fonts folder, hand its files to the page: the browser can't see fonts
    /// loaded only for this app. Sent once per family per terminal.
    /// </summary>
    private void SendFolderFonts()
    {
        string family = Theme.TerminalFont;
        if (!_ready || _web.CoreWebView2 == null || family == _fontsSent) return;
        var files = FontLoader.FilesOf(family);
        if (files.Count == 0) return;
        try
        {
            var payload = files.Select(f => new
            {
                family = f.Family,
                weight = f.Weight,
                italic = f.Italic,
                data = Convert.ToBase64String(File.ReadAllBytes(f.Path)),
            }).ToList();
            _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { t = "fonts", fonts = payload }));
            _fontsSent = family;
        }
        catch (Exception ex) { Log.Write($"Couldn't give the terminal the font {family}: {ex.Message}"); }
    }

    /// <summary>The chosen terminal font, with sensible monospace fonts behind it in case it's missing.</summary>
    private static string FontStack()
    {
        string chosen = Theme.TerminalFont.Replace("'", "");
        return $"'{chosen}', 'Cascadia Mono', 'Cascadia Code', Consolas, 'Courier New', monospace";
    }

    private static string Resource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("SwitchboardRM.terminal." + name);
        return s == null ? "" : new StreamReader(s).ReadToEnd();
    }

    /// <summary>Terminal colours for the current look (Windows Terminal's "Campbell" / "One Half Light"), purple cursor.</summary>
    private static Dictionary<string, string> ThemeObject()
    {
        // a chosen colour scheme (Settings > Appearance > Terminal colours) replaces the app-matched colours
        if (Theme.TerminalScheme is { } scheme)
        {
            var st = new Dictionary<string, string>
            {
                ["background"] = scheme.Background,
                ["foreground"] = scheme.Foreground,
                ["cursor"] = scheme.Cursor,
                ["cursorAccent"] = scheme.Background,
                ["selectionBackground"] = scheme.Selection,
            };
            for (int i = 0; i < 16; i++) st[TerminalScheme.AnsiNames[i]] = scheme.Ansi[i];
            return st;
        }

        var p = Theme.Current;
        static string Hex(System.Drawing.Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        var t = new Dictionary<string, string>
        {
            ["background"] = Hex(p.IsDark ? p.Back : p.Surface),
            ["foreground"] = p.IsDark ? "#E7E7EC" : "#383A42",
            ["cursor"] = Hex(p.Accent),
            ["cursorAccent"] = "#FFFFFF",
            ["selectionBackground"] = ColorMath.Hex(p.Accent) + (p.IsDark ? "66" : "40"), // the accent, see-through
        };
        string[] dark = { "#0C0C0C", "#C50F1F", "#13A10E", "#C19C00", "#0037DA", "#881798", "#3A96DD", "#CCCCCC",
                          "#767676", "#E74856", "#16C60C", "#F9F1A5", "#3B78FF", "#B4009E", "#61D6D6", "#F2F2F2" };
        string[] light = { "#383A42", "#E45649", "#50A14F", "#C18301", "#0184BC", "#A626A4", "#0997B3", "#FAFAFA",
                           "#4F525D", "#DF6C75", "#98C379", "#E4C07A", "#61AFEF", "#C577DD", "#56B5C1", "#FFFFFF" };
        string[] names = { "black", "red", "green", "yellow", "blue", "magenta", "cyan", "white",
                           "brightBlack", "brightRed", "brightGreen", "brightYellow", "brightBlue", "brightMagenta", "brightCyan", "brightWhite" };
        var set = p.IsDark ? dark : light;
        for (int i = 0; i < names.Length; i++) t[names[i]] = set[i];
        return t;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pty?.Dispose();
            _pty = null;
            _web.Dispose();
        }
        base.Dispose(disposing);
    }
}
