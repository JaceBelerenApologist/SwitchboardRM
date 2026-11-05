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
            var env = BrowserEnv(Path.Combine(Dir, "aws-azure-login")) ?? new Dictionary<string, string>();
            if (shim != null) env["SWITCHBOARD_LOGIN_HINT"] = loginHint.Trim();
            return (BundledNode, a, env);
        }

        var c = new List<string> { "/c", "aws-azure-login" };
        c.AddRange(args);
        // an installed aws-azure-login lives in npm's global folder: same check on its Chromium
        string installedPackage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                               "npm", "node_modules", "aws-azure-login");
        var installedEnv = BrowserEnv(installedPackage);
        if (shim != null)
        {
            // an installed aws-azure-login is started by npm's wrapper, so the shim goes in through NODE_OPTIONS
            // (forward slashes: NODE_OPTIONS treats backslashes as escapes)
            installedEnv ??= new Dictionary<string, string>();
            installedEnv["NODE_OPTIONS"] = $"--require \"{shim.Replace('\\', '/')}\"";
            installedEnv["SWITCHBOARD_LOGIN_HINT"] = loginHint.Trim();
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
    /// <summary>
    /// The browser for sign-in. aws-azure-login launches Puppeteer without a browser path, so Puppeteer uses its own
    /// Chromium (in node_modules\puppeteer\.local-chromium) unless PUPPETEER_EXECUTABLE_PATH says otherwise. If that
    /// Chromium is missing or incomplete (no chrome.exe: an interrupted download, or security software removed it),
    /// point Puppeteer at tools\chromium or Microsoft Edge instead. Null = leave it to Puppeteer.
    /// </summary>
    private static Dictionary<string, string> BrowserEnv(string packageDir)
    {
        if (packageDir == null || !Directory.Exists(packageDir)) return null;   // can't tell: leave it to Puppeteer

        string local = Path.Combine(packageDir, "node_modules", "puppeteer", ".local-chromium");
        bool complete = false;
        try { complete = Directory.Exists(local) && Directory.EnumerateFiles(local, "chrome.exe", SearchOption.AllDirectories).Any(); }
        catch { }
        if (complete) return null;   // Puppeteer's own Chromium is there

        string browser = Existing(Path.Combine(Dir, "chromium", "chrome.exe")) ?? FindEdge();
        Log.Write(browser == null
            ? "Puppeteer's Chromium is missing or incomplete, and no other browser was found for sign-in"
            : $"Puppeteer's Chromium is missing or incomplete; signing in with {browser}");
        return browser == null ? null : new Dictionary<string, string> { ["PUPPETEER_EXECUTABLE_PATH"] = browser };
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

/// <summary>
/// update-tools.ps1 and collect-tools.ps1, run from inside Switchboard (Help > Update helper tools). They live
/// beside the tools folder: next to the .exe, or the project folder while developing. Copies are built into the
/// .exe, so a deleted script is written back; an existing one (perhaps edited) is left as it is.
/// </summary>
internal static class ToolScripts
{
    /// <summary>The Id of the tab that runs them (it gets the plain environment, not the bundled tools on PATH).</summary>
    public const string ShellId = "tool-update";
    private static readonly string[] Names = { "update-tools.ps1", "collect-tools.ps1" };

    /// <summary>The folder the scripts (and tools\) belong in.</summary>
    public static string Folder => Path.GetDirectoryName(Tools.Dir);

    /// <summary>Path of update-tools.ps1, after writing back any missing script. Null (with the reason) if it can't be.</summary>
    public static string Ensure(out string problem)
    {
        problem = null;
        string folder = Folder;
        try
        {
            foreach (var name in Names)
            {
                string path = Path.Combine(folder, name);
                if (File.Exists(path)) continue;
                using var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("SwitchboardRM.scripts." + name);
                if (s == null) { problem = $"{name} is missing, and this build of Switchboard has no copy of it."; return null; }
                using var file = File.Create(path);
                s.CopyTo(file);
                Log.Write($"Wrote {name} back to {folder} (it was missing)");
            }
            return Path.Combine(folder, "update-tools.ps1");
        }
        catch (UnauthorizedAccessException)
        {
            problem = $"Switchboard can't write to {folder} (for example, it's under Program Files). Move Switchboard to a " +
                      "folder you can write to, or run it as administrator, to update its tools.";
            return null;
        }
        catch (Exception ex)
        {
            problem = "Couldn't prepare the update scripts: " + ex.Message;
            return null;
        }
    }

    /// <summary>PowerShell 7 if installed (the scripts work in both), else Windows PowerShell.</summary>
    public static string PowerShell() =>
        (Shells.Find("pwsh") ?? Shells.Find("powershell"))?.CommandLine.Split(" -NoLogo")[0].Trim().Trim('"')   // the bare path
        ?? Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
}
