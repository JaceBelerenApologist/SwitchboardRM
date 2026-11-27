using System.Text.Json;
using System.Text.RegularExpressions;

namespace SwitchboardRM;

internal sealed class Instance
{
    public string Name = "";
    public string Type = "";   // EC2 instance type, e.g. t3.large
    public string Id = "";
    public string Region = "";
    public string Ip = "";
    public string Platform = "";
    public string Os = "";       // short name, e.g. "Windows Server 2025" or "Ubuntu 22.04"
    public string OsFull = "";   // what Systems Manager reports, e.g. "Microsoft Windows Server 2025 Datacenter"
    public string Ping = "";       // Systems Manager agent: "Online", "ConnectionLost", ... ("" = not managed)
    public string State = "";      // EC2: "running", "stopped", "pending", "stopping", ... ("" = not an EC2 instance)
    public string Tags = "";
    public bool Managed;           // known to Systems Manager (has the agent registered)
    public SavedConnection Saved;  // set for a saved RDP connection (not an AWS machine)

    public bool IsSaved => Saved != null;

    /// <summary>Reachable for RDP right now: the Systems Manager agent is online.</summary>
    public bool Online => Ping == "Online";

    /// <summary>An EC2 instance (not an on-premises "mi-" machine), so it can be started, stopped and rebooted.</summary>
    public bool IsEc2 => State.Length > 0 && Id.StartsWith("i-", StringComparison.OrdinalIgnoreCase);

    /// <summary>What the Status column shows.</summary>
    public string StatusText => IsSaved
        ? Ping switch { "Online" => "Reachable", "" => "Checking ...", _ => "Unreachable" }
        : State switch
    {
        "running" => !Managed ? "Running, no SSM" : Online ? "Online" : "Agent offline",
        "stopped" => "Stopped",
        "stopping" => "Stopping",
        "pending" => "Starting",
        "shutting-down" => "Shutting down",
        "" => Online ? "Online" : string.IsNullOrEmpty(Ping) ? "Unknown" : "Offline",
        _ => char.ToUpperInvariant(State[0]) + State[1..],
    };
}

/// <summary>Turns what Systems Manager reports into a short OS name for the list.</summary>
internal static class OsNames
{
    public static string Friendly(string name, string version, string platform)
    {
        name = (name ?? "").Trim();
        version = (version ?? "").Trim();

        if (platform == "Windows" || name.Contains("Windows", StringComparison.OrdinalIgnoreCase))
        {
            // "Microsoft Windows Server 2022 Datacenter" -> "Windows Server 2022"
            var m = Regex.Match(name, @"Windows Server \d{4}( R2)?", RegexOptions.IgnoreCase);
            if (m.Success) return m.Value;
            // "Microsoft Windows 11 Pro" -> "Windows 11"
            m = Regex.Match(name, @"Windows \d+", RegexOptions.IgnoreCase);
            if (m.Success) return m.Value;
            // name without a year: work it out from the build number in "10.0.20348"
            string year = ServerYear(version);
            return year != null ? "Windows Server " + year : "Windows";
        }

        if (name.Length == 0) return platform ?? "";

        string shortName =
            name.StartsWith("Red Hat", StringComparison.OrdinalIgnoreCase) ? "RHEL" :
            name.StartsWith("SUSE", StringComparison.OrdinalIgnoreCase) || name.StartsWith("SLES", StringComparison.OrdinalIgnoreCase) ? "SLES" :
            name.StartsWith("CentOS", StringComparison.OrdinalIgnoreCase) ? "CentOS" :
            name.StartsWith("Debian", StringComparison.OrdinalIgnoreCase) ? "Debian" :
            name.StartsWith("Oracle", StringComparison.OrdinalIgnoreCase) ? "Oracle Linux" :
            name;

        // Ubuntu is known by year.month ("22.04"); everything else by its major version ("9", "2023")
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        string v = parts.Length == 0 ? "" :
            shortName.StartsWith("Ubuntu", StringComparison.OrdinalIgnoreCase) && parts.Length > 1 ? $"{parts[0]}.{parts[1]}" :
            parts[0];

        return v.Length > 0 && !shortName.Contains(v) ? $"{shortName} {v}" : shortName;
    }

    private static string ServerYear(string version)
    {
        var parts = version.Split('.');
        if (parts.Length < 3 || !int.TryParse(parts[2], out int build)) return null;
        return build switch
        {
            >= 26100 => "2025",
            >= 20348 => "2022",
            >= 17763 => "2019",
            >= 14393 => "2016",
            >= 9600 => "2012 R2",
            >= 9200 => "2012",
            _ => null,
        };
    }
}

internal sealed class AppSettings
{
    public string Profile { get; set; } = "";   // empty until the first profile is picked or added
    public string Username { get; set; } = "";
    public List<string> ScanRegions { get; set; } = new() { "ca-central-1", "ca-west-1" };
    /// <summary>The KMS key last used for new disks, per region ("" = AWS managed); offered first next time.</summary>
    public Dictionary<string, string> LastKmsKeys { get; set; } = new();
    public bool WindowsOnly { get; set; } = true;
    public bool DarkMode { get; set; }   // from before ThemeMode; still kept in step for older versions

    // ---- appearance (Settings > Appearance)
    /// <summary>"Light", "Dark" or "System" (follow Windows). Empty = settings from before 2.6: use DarkMode.</summary>
    public string ThemeMode { get; set; } = "";
    /// <summary>"" = the default purple, "#RRGGBB" = a chosen colour, "Windows" = follow the Windows accent colour.</summary>
    public string AccentColor { get; set; } = "";
    /// <summary>Font family for the app's text. Empty = Segoe UI.</summary>
    public string UiFont { get; set; } = "";
    public float ListFontSize { get; set; } = 10;
    /// <summary>The server list's font family ("" = the same as the app font).</summary>
    public string ListFont { get; set; } = "";
    /// <summary>Font family for SSH terminals. Empty = Cascadia Mono.</summary>
    public string TerminalFont { get; set; } = "";
    public int TerminalFontSize { get; set; } = 14;
    /// <summary>Terminal colour scheme by name ("Dracula", "Nord" ...). Empty = match the app's light/dark look.</summary>
    public string TerminalScheme { get; set; } = "";
    /// <summary>Start PowerShell tabs with the scheme's own colours for the prompt, file lists and syntax.</summary>
    public bool StylePowerShell { get; set; } = true;
    /// <summary>Shell for Ctrl+T and the toolbar's terminal button: "cmd", "powershell", "pwsh", ... Empty = automatic.</summary>
    public string DefaultShell { get; set; } = "";
    /// <summary>Folder Claude Code starts in; its subfolders are offered as projects (Claude Code in). Empty = the user folder.</summary>
    public string ProjectsFolder { get; set; } = "";

    /// <summary>Whether to show the dark look right now (ThemeMode, or Windows' setting for "System").</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool EffectiveDark => ThemeMode switch
    {
        "Dark" => true,
        "Light" => false,
        "System" => Theme.WindowsIsDark(),
        _ => DarkMode,
    };
    public int ListWidth { get; set; } = 760;
    /// <summary>The main window's normal (not maximized) position and size: left, top, width, height. Empty = default.</summary>
    public int[] WindowBounds { get; set; } = Array.Empty<int>();
    public bool WindowMaximized { get; set; }
    /// <summary>Height of the docked-terminal panel under the tabs, in pixels.</summary>
    public int DockHeight { get; set; } = 260;
    /// <summary>Open new shells and SSH sessions in the docked panel instead of a tab.</summary>
    public bool DockNewTerminals { get; set; }
    /// <summary>Reopen the tabs that were open when Switchboard last closed.</summary>
    public bool RestoreTabs { get; set; } = true;
    /// <summary>Also keep terminals' output (written to this PC until the next close) and show it again.</summary>
    public bool RestoreScrollback { get; set; }
    /// <summary>Check GitHub for a newer release (at most daily). An administrator can turn this off by policy.</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>Users recently typed in Connect as (newest first), offered in its user list.</summary>
    public List<string> RecentConnectAs { get; set; } = new();
    /// <summary>Server window > Run command: the last scripts run (newest first).</summary>
    public List<string> RecentCommands { get; set; } = new();
    /// <summary>Only one copy of Switchboard per Windows session: starting it again brings the open window forward.</summary>
    public bool SingleInstance { get; set; } = true;
    /// <summary>Minimizing hides the window to the system tray instead of the taskbar.</summary>
    public bool MinimizeToTray { get; set; }
    /// <summary>The window's close button hides it to the system tray; Exit (tray menu or File) really closes.</summary>
    public bool CloseToTray { get; set; }
    /// <summary>The user has chosen what the close button does ("Don't ask me again", or the option in Settings).</summary>
    public bool CloseChoiceMade { get; set; }
    /// <summary>The "still running in the tray" notification has been shown once.</summary>
    public bool TrayHintShown { get; set; }
    /// <summary>A release the user chose to skip ("Skip this version"), e.g. "3.2.1".</summary>
    public string SkippedVersion { get; set; } = "";
    public DateTime LastUpdateCheck { get; set; } = DateTime.MinValue;
    /// <summary>New server: the choices last used per region, offered again next time.</summary>
    public Dictionary<string, LaunchDefault> LaunchDefaults { get; set; } = new();
    /// <summary>Check the AWS sign-in in the background and renew it before it runs out.</summary>
    public bool KeepSignedIn { get; set; } = true;
    /// <summary>Remove the profile's AWS keys when the app closes.</summary>
    public bool SignOutOnClose { get; set; } = true;
    /// <summary>The list tab last shown: false = AWS servers, true = saved RDP connections.</summary>
    public bool SavedView { get; set; }
    /// <summary>Columns for the saved-connections tab (the AWS tab uses Columns).</summary>
    public List<string> SavedColumns { get; set; } = new();
    /// <summary>The Description column (2.17) has been added to an existing saved-tab layout once.</summary>
    public bool DescriptionColumnAdded { get; set; }
    /// <summary>SSH user for AWS servers. Empty = a guess from the OS (ec2-user, ubuntu, admin, ...).</summary>
    public string SshUser { get; set; } = "";
    /// <summary>Optional private key file for SSH. Empty = ssh's defaults (~/.ssh keys and the SSH agent).</summary>
    public string SshKeyFile { get; set; } = "";
    public bool HideOffline { get; set; }
    /// <summary>Server list columns shown, in order (Name is always first). Empty = the default set.</summary>
    public List<string> Columns { get; set; } = new();
    public Dictionary<string, int> ColumnWidths { get; set; } = new();

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.DataName);
    private static string FilePath => Path.Combine(Dir, "settings.json");
    /// <summary>The folder settings.json lives in.</summary>
    public static string Folder => Dir;

    /// <summary>
    /// Just the dark-mode setting, read with a plain text search so the splash can appear before
    /// the JSON library has loaded. Load() still reads the whole file properly afterwards.
    /// </summary>
    public static bool PeekDarkMode() => PeekAppearance().Dark;

    /// <summary>
    /// Light/dark and the accent colour, read with a plain text search so the splash can use them before the JSON
    /// library has loaded. Load() still reads the whole file properly afterwards.
    /// </summary>
    public static (bool Dark, string Accent) PeekAppearance()
    {
        try
        {
            if (!File.Exists(FilePath)) return (false, "");
            string compact = string.Concat(File.ReadAllText(FilePath).Where(c => !char.IsWhiteSpace(c)));
            static string Str(string json, string key)
            {
                string marker = "\"" + key + "\":\"";
                int i = json.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (i < 0) return "";
                int start = i + marker.Length, end = json.IndexOf('"', start);
                return end > start ? json[start..end] : "";
            }
            string mode = Str(compact, "ThemeMode");
            bool dark = mode.Equals("Dark", StringComparison.OrdinalIgnoreCase) ? true
                : mode.Equals("Light", StringComparison.OrdinalIgnoreCase) ? false
                : mode.Equals("System", StringComparison.OrdinalIgnoreCase) ? Theme.WindowsIsDark()
                : compact.Contains("\"DarkMode\":true", StringComparison.OrdinalIgnoreCase);
            return (dark, Str(compact, "AccentColor"));
        }
        catch { return (false, ""); }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) { Log.Write("Could not read settings: " + ex.Message); }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Write("Could not save settings: " + ex.Message); }
    }
}

internal static class Log
{
    public static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.DataName, "logs");
    private static readonly object Gate = new();

    /// <summary>app.log is rotated past this size: it becomes app.1.log (replacing the previous one) and a new one starts.</summary>
    private const long MaxBytes = 10L * 1024 * 1024;
    private const int CheckEvery = 200;   // writes between size checks (the first write always checks)
    private static int _writesSinceCheck = CheckEvery;

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                string path = Path.Combine(Dir, "app.log");
                if (++_writesSinceCheck >= CheckEvery)
                {
                    _writesSinceCheck = 0;
                    Rotate(path);
                }
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash the app */ }
    }

    /// <summary>
    /// Keep the log from growing without limit: past MaxBytes, app.log becomes app.1.log (the one before it is
    /// deleted) and the next line starts a fresh app.log. So at most about 2 x MaxBytes is kept.
    /// </summary>
    private static void Rotate(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxBytes) return;
            string previous = Path.Combine(Dir, "app.1.log");
            File.Move(path, previous, overwrite: true);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} Log rotated: the earlier log is app.1.log{Environment.NewLine}");
        }
        catch { /* e.g. the file is open elsewhere: try again at the next check */ }
    }

    public static void Cleanup()
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            foreach (var f in Directory.GetFiles(Dir, "tunnel-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-7)) File.Delete(f);
        }
        catch { }
    }
}

/// <summary>New server: what was used last time in a region (each may be empty).</summary>
internal sealed class LaunchDefault
{
    public string SubnetId { get; set; } = "";
    public List<string> SecurityGroupIds { get; set; } = new();
    public string InstanceProfile { get; set; } = "";
    public string InstanceType { get; set; } = "";
    public string KeyName { get; set; } = "";
    public string DiskType { get; set; } = "";
}
