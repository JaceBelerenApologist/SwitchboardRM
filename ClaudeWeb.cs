using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SwitchboardRM;

/// <summary>
/// A tab with claude.ai in it: the regular Claude website, signed in with your own account, in Microsoft's WebView2
/// engine. Its sign-in is remembered in a private browser profile of its own (%LOCALAPPDATA%\SwitchboardRM\ClaudeWeb),
/// separate from the AWS sign-in window. Sign-in pop-ups (Google, Microsoft, Apple) open as pop-up windows so those
/// sign-ins can work; any other link opens in your normal browser.
/// </summary>
internal sealed class ClaudeWebHost : UserControl
{
    private const string Home = "https://claude.ai";
    private static Task<CoreWebView2Environment> _env;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };

    // pages that stay inside Switchboard: Claude itself, and the sign-in services it offers
    private static readonly string[] Kept =
    {
        "claude.ai", "anthropic.com", "accounts.google.com", "google.com", "gstatic.com",
        "login.microsoftonline.com", "login.live.com", "appleid.apple.com",
    };

    public ClaudeWebHost()
    {
        _web.DefaultBackgroundColor = Theme.Current.Back;
        Controls.Add(_web);
    }

    public async Task StartAsync()
    {
        _env ??= CoreWebView2Environment.CreateAsync(null,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.DataName, "ClaudeWeb"));
        await _web.EnsureCoreWebView2Async(await _env);

        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.NewWindowRequested += OnNewWindow;
        core.NavigationStarting += (_, e) =>
        {
            // a link to somewhere else entirely: the normal browser, not this tab
            if (e.IsUserInitiated && !IsKept(e.Uri))
            {
                e.Cancel = true;
                OpenExternally(e.Uri);
            }
        };
        core.Navigate(Home);
    }

    public void FocusPage()
    {
        try { _web.Focus(); } catch { }
    }

    private void OnNewWindow(object sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // sign-in pop-ups stay as pop-ups (they report back to the page that opened them); everything else goes out
        if (IsKept(e.Uri)) return;
        e.Handled = true;
        OpenExternally(e.Uri);
    }

    private static bool IsKept(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u)) return true;   // about:blank and the like
        if (u.Scheme != Uri.UriSchemeHttps) return u.Scheme is "about" or "data" or "blob";
        string host = u.Host.ToLowerInvariant();
        return Kept.Any(k => host == k || host.EndsWith("." + k));
    }

    private static void OpenExternally(string uri)
    {
        try
        {
            if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Write("Couldn't open a link from claude.ai: " + ex.Message); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _web.Dispose();
        base.Dispose(disposing);
    }
}
