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
    public bool WindowsOnly { get; set; } = true;
    public bool DarkMode { get; set; }
    public int ListWidth { get; set; } = 760;
    /// <summary>Check the AWS sign-in in the background and renew it before it runs out.</summary>
    public bool KeepSignedIn { get; set; } = true;
    /// <summary>Remove the profile's AWS keys when the app closes.</summary>
    public bool SignOutOnClose { get; set; } = true;
    /// <summary>The list tab last shown: false = AWS servers, true = saved RDP connections.</summary>
    public bool SavedView { get; set; }
    /// <summary>Columns for the saved-connections tab (the AWS tab uses Columns).</summary>
    public List<string> SavedColumns { get; set; } = new();
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
    public static bool PeekDarkMode()
    {
        try
        {
            if (!File.Exists(FilePath)) return false;
            string compact = string.Concat(File.ReadAllText(FilePath).Where(c => !char.IsWhiteSpace(c)));
            return compact.Contains("\"DarkMode\":true", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
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

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(Path.Combine(Dir, "app.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch { /* logging must never crash the app */ }
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
