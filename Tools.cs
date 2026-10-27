using System.Text.Json;

namespace SwitchboardRM;

/// <summary>
/// Helper programs shipped in the "tools" folder next to the .exe (see collect-tools.ps1).
/// Bundled copies are used first; if they're missing, the installed versions on PATH are used as before.
///
///   tools\node\node.exe                                      Node.js runtime (runs aws-azure-login)
///   tools\aws-azure-login\                                   the npm package with its node_modules
///   tools\chromium\chrome.exe                                optional; otherwise Microsoft Edge shows the sign-in page
///   tools\session-manager-plugin\session-manager-plugin.exe  carries the RDP tunnel
///   tools\aws-cli\aws.exe                                    AWS CLI v2, only for "aws sso login"
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

    public static string BundledNode => Existing(Path.Combine(Dir, "node", "node.exe"));
    public static string BundledPlugin => Existing(Path.Combine(Dir, "session-manager-plugin", "session-manager-plugin.exe"));
    public static string BundledAwsCli => Existing(Path.Combine(Dir, "aws-cli", "aws.exe"));

    /// <summary>
    /// The OpenSSH client: tools\openssh\ssh.exe if bundled, otherwise the one built into Windows 10/11
    /// (Settings > Apps > Optional features > OpenSSH Client). Null if neither is there.
    /// </summary>
    public static string Ssh =>
        Existing(Path.Combine(Dir, "openssh", "ssh.exe")) ??
        Existing(Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe"));

    /// <summary>The AWS CLI to run: the bundled one, or "aws" from PATH.</summary>
    public static string AwsCli => BundledAwsCli ?? "aws";

    private static readonly Lazy<string> _azureLoginScript =
        new(() => FindPackageBin(Path.Combine(Dir, "aws-azure-login"), "aws-azure-login"));
    public static string BundledAzureLogin => _azureLoginScript.Value;

    public static bool HasBundledAzureLogin => BundledNode != null && BundledAzureLogin != null;

    /// <summary>How to run aws-azure-login with these arguments: the bundled copy through node.exe, or the installed one.</summary>
    /// <param name="loginHint">
    /// The account Entra should sign in (the profile's azure_default_username), added to the sign-in page as
    /// login_hint by login-hint-shim.js. Empty = no hint, aws-azure-login runs unchanged.
    /// </param>
    public static (string File, List<string> Args, Dictionary<string, string> Env) AzureLogin(string loginHint, params string[] args)
    {
        string shim = string.IsNullOrWhiteSpace(loginHint) ? null : LoginHintShim();
        if (HasBundledAzureLogin)
        {
            var a = new List<string>();
            if (shim != null) { a.Add("--require"); a.Add(shim); }
            a.Add(BundledAzureLogin);
            a.AddRange(args);
            var env = BrowserEnv() ?? new Dictionary<string, string>();
            if (shim != null) env["SWITCHBOARD_LOGIN_HINT"] = loginHint.Trim();
            return (BundledNode, a, env);
        }

        var c = new List<string> { "/c", "aws-azure-login" };
        c.AddRange(args);
        Dictionary<string, string> installedEnv = null;
        if (shim != null)
        {
            // an installed aws-azure-login is started by npm's wrapper, so the shim goes in through NODE_OPTIONS
            // (forward slashes: NODE_OPTIONS treats backslashes as escapes)
            installedEnv = new Dictionary<string, string>
            {
                ["NODE_OPTIONS"] = $"--require \"{shim.Replace('\\', '/')}\"",
                ["SWITCHBOARD_LOGIN_HINT"] = loginHint.Trim(),
            };
        }
        return ("cmd.exe", c, installedEnv);
    }

    /// <summary>Write login-hint-shim.js (built into the .exe) to %LOCALAPPDATA%\SwitchboardRM and return its path.</summary>
    private static string LoginHintShim()
    {
        try
        {
            using var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("SwitchboardRM.login-hint-shim.js");
            if (s == null) return null;
            string text = new StreamReader(s).ReadToEnd();
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.DataName);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "login-hint-shim.js");
            if (!File.Exists(path) || File.ReadAllText(path) != text) File.WriteAllText(path, text);
            return path;
        }
        catch (Exception ex)
        {
            Log.Write("Couldn't write the login-hint shim: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Which browser the bundled aws-azure-login drives. Puppeteer's own Chromium if it came along,
    /// otherwise a bundled tools\chromium, otherwise Microsoft Edge (installed on every Windows 10/11 PC).
    /// </summary>
    private static Dictionary<string, string> BrowserEnv()
    {
        string pkg = Path.Combine(Dir, "aws-azure-login");
        if (Directory.Exists(Path.Combine(pkg, "node_modules", "puppeteer", ".local-chromium"))) return null; // Puppeteer finds it itself

        string browser = Existing(Path.Combine(Dir, "chromium", "chrome.exe")) ?? FindEdge();
        return browser == null ? null : new Dictionary<string, string> { ["BROWSER_CHROME_BIN"] = browser };
    }

    private static string FindEdge()
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                 })
        {
            if (string.IsNullOrEmpty(root)) continue;
            string edge = Existing(Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"));
            if (edge != null) return edge;
        }
        return null;
    }

    /// <summary>The script an npm package runs as a command, from the "bin" entry of its package.json.</summary>
    private static string FindPackageBin(string packageDir, string command)
    {
        try
        {
            string manifest = Path.Combine(packageDir, "package.json");
            if (!File.Exists(manifest)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            if (!doc.RootElement.TryGetProperty("bin", out var bin)) return null;

            string rel = bin.ValueKind == JsonValueKind.String ? bin.GetString()
                : bin.ValueKind == JsonValueKind.Object && bin.TryGetProperty(command, out var b) ? b.GetString()
                : null;
            return rel == null ? null : Existing(Path.GetFullPath(Path.Combine(packageDir, rel)));
        }
        catch (Exception ex)
        {
            Log.Write($"Could not read {packageDir}\\package.json: {ex.Message}");
            return null;
        }
    }

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

        string aalVersion = "bundled";
        try
        {
            string manifest = Path.Combine(Dir, "aws-azure-login", "package.json");
            if (File.Exists(manifest))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                if (doc.RootElement.TryGetProperty("version", out var v)) aalVersion = "bundled, " + v.GetString();
            }
        }
        catch { }

        const string installed = "not bundled, using the installed copy";
        return new List<(string, string)>
        {
            ("Node.js", BundledNode != null ? FileVer(BundledNode) : installed),
            ("aws-azure-login", BundledAzureLogin != null ? aalVersion : installed),
            ("Session Manager plugin", BundledPlugin != null ? FileVer(BundledPlugin) : installed),
            ("AWS CLI", BundledAwsCli != null ? FileVer(BundledAwsCli) : installed),
            ("OpenSSH client", Ssh == null ? "not found (install Windows' optional OpenSSH Client)"
                : Ssh.StartsWith(Dir, StringComparison.OrdinalIgnoreCase) ? FileVer(Ssh) : "built into Windows"),
        };
    }

    private static string Existing(string path) => File.Exists(path) ? path : null;
}
