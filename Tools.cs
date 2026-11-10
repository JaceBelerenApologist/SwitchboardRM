namespace SwitchboardRM;

/// <summary>
/// Helper programs shipped in the "tools" folder next to the .exe (see collect-tools.ps1).
/// Bundled copies are used first; if they're missing, the installed versions on PATH are used.
///
///   tools\session-manager-plugin\session-manager-plugin.exe  carries the Session Manager tunnels
///   tools\aws-cli\aws.exe                                    AWS CLI v2: "aws sso login" for SSO profiles, and
///                                                            aws commands in Switchboard's shells
///   tools\conpty\conpty.dll + OpenConsole.exe                Microsoft ConPTY for the terminals (see PseudoConsole)
///   tools\openssh\ssh.exe                                    optional; otherwise Windows' own OpenSSH client
///
/// Microsoft sign-in needs nothing from here: it's built in (EntraSignIn, on the WebView2 engine that comes with
/// Windows).
///
/// A published copy has tools\ next to the .exe. While developing, the project's own tools\ folder is used
/// where it is, so nothing gets copied into bin\ on every build.
/// </summary>
internal static class Tools
{
    private static readonly Lazy<string> _dir = new(FindDir);
    public static string Dir => _dir.Value;

    private static string FindDir()
    {
        string here = Path.Combine(AppContext.BaseDirectory, "tools");
        if (Directory.Exists(here)) return here;

        // Running from bin\Debug\net10.0-windows\ (or similar): look upwards for the project folder's tools\
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "tools");
            if (Directory.Exists(candidate) && dir.GetFiles("*.csproj").Length > 0) return candidate;
        }
        return here; // nothing bundled; installed copies are used instead
    }

    public static string BundledPlugin => Existing(Path.Combine(Dir, "session-manager-plugin", "session-manager-plugin.exe"));
    public static string BundledAwsCli => Existing(Path.Combine(Dir, "aws-cli", "aws.exe"));
    private static string BundledConpty => Existing(Path.Combine(Dir, "conpty", "conpty.dll"));

    /// <summary>
    /// The OpenSSH client: tools\openssh\ssh.exe if bundled, otherwise the one built into Windows 10/11
    /// (Settings > Apps > Optional features > OpenSSH Client). Null if neither is there.
    /// </summary>
    public static string Ssh =>
        Existing(Path.Combine(Dir, "openssh", "ssh.exe")) ??
        Existing(Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe"));

    /// <summary>The AWS CLI to run: the bundled one, or "aws" from PATH.</summary>
    public static string AwsCli => BundledAwsCli ?? "aws";

    /// <summary>Each helper and which copy is used, with its version where it can be read. For the About page.</summary>
    public static List<(string Name, string Detail)> Describe()
    {
        static string FileVer(string path)
        {
            try
            {
                var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                string s = !string.IsNullOrWhiteSpace(v.ProductVersion) ? v.ProductVersion : v.FileVersion;
                return string.IsNullOrWhiteSpace(s) ? "bundled" : "bundled, " + s.Trim();
            }
            catch { return "bundled"; }
        }

        string webView;
        try { webView = "built in (WebView2 " + Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString() + ")"; }
        catch { webView = "built in (needs the Microsoft Edge WebView2 Runtime, which isn't installed)"; }

        const string installed = "not bundled, using the installed copy";
        return new List<(string, string)>
        {
            ("Microsoft sign-in", webView),
            ("Session Manager plugin", BundledPlugin != null ? FileVer(BundledPlugin) : installed),
            ("AWS CLI", BundledAwsCli != null ? FileVer(BundledAwsCli) : installed),
            ("Microsoft ConPTY", BundledConpty != null ? FileVer(BundledConpty) : "not bundled, using Windows' built-in pseudo-console"),
            ("OpenSSH client", Ssh == null ? "not found (install Windows' optional OpenSSH Client)"
                : Ssh.StartsWith(Dir, StringComparison.OrdinalIgnoreCase) ? FileVer(Ssh) : "built into Windows"),
        };
    }

    private static string Existing(string path) => File.Exists(path) ? path : null;
}
