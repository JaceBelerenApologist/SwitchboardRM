using System.Reflection;

namespace SwitchboardRM;

/// <summary>Name, version and other details, read from the project file (Version, Company, Copyright, ...).</summary>
internal static class AppInfo
{
    public const string Name = "Switchboard Remote Manager";
    public const string ShortName = "Switchboard RM";
    /// <summary>Folder and key name for the app's data (settings, logs, saved passwords). Don't change lightly.</summary>
    public const string DataName = "SwitchboardRM";
    /// <summary>What versions before 2.0 (AWS RDP Launcher) used. Only read, to move data across.</summary>
    public const string OldDataName = "AwsRdpLauncher";

    private static Assembly Asm => Assembly.GetExecutingAssembly();

    /// <summary>e.g. "1.0.0" (from &lt;Version&gt; in the .csproj).</summary>
    public static string Version
    {
        get
        {
            string v = Asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                       ?? Asm.GetName().Version?.ToString(3) ?? "0.0.0";
            int plus = v.IndexOf('+'); // drop any "+commit" build suffix
            return plus > 0 ? v[..plus] : v;
        }
    }

    public static string Copyright => Asm.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? "";

    /// <summary>CHANGELOG.md, built into the .exe.</summary>
    public static string Changelog
    {
        get
        {
            try
            {
                using var s = Asm.GetManifestResourceStream("SwitchboardRM.CHANGELOG.md");
                return s == null ? "No changelog was included in this build." : new StreamReader(s).ReadToEnd();
            }
            catch (Exception ex) { return "Couldn't read the changelog: " + ex.Message; }
        }
    }

    /// <summary>Version details for a support request or bug report.</summary>
    public static string Details()
    {
        var lines = new List<string>
        {
            $"{Name} {Version}",
            $"Windows {Environment.OSVersion.Version}  ({(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})",
            $".NET {Environment.Version}",
        };
        lines.AddRange(Tools.Describe().Select(t => $"{t.Name}: {t.Detail}"));
        return string.Join(Environment.NewLine, lines);
    }
}
