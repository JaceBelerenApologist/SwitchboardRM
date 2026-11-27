using System.Text.Json;

namespace SwitchboardRM;

/// <summary>One tab to reopen next time (Settings > Appearance > Tabs at start-up).</summary>
internal sealed class TabState
{
    public string Kind { get; set; } = "";        // "shell", "claude-web", "rdp", "ssh", "vnc"
    public string ShellId { get; set; } = "";     // shell: Shells id, or "claude-code"
    public string InstanceId { get; set; } = "";  // AWS server
    public string Region { get; set; } = "";
    public string SavedId { get; set; } = "";     // saved connection
    public string Name { get; set; } = "";        // for placeholders and messages
    public string AsUser { get; set; }            // Connect as (the password is never kept)
    public bool Docked { get; set; }
    public bool PoppedOut { get; set; }           // in a window of its own (Docked: where it goes back to)
    public int[] WindowBounds { get; set; }       // popped out: x, y, width, height when not maximized
    public bool WindowMaximized { get; set; }
    public bool WindowFullScreen { get; set; }
    public bool WindowOnTop { get; set; }
    public string ScrollbackFile { get; set; } = "";   // file name in the scrollback folder, if output was kept
    public bool IsRemote => Kind is "rdp" or "ssh" or "vnc";
    public bool IsAws => IsRemote && SavedId.Length == 0;
}

/// <summary>The tabs open when Switchboard last closed.</summary>
internal sealed class SavedTabs
{
    public DateTime SavedAt { get; set; } = DateTime.Now;
    public int Selected { get; set; } = -1;   // index in Tabs of the tab that was selected
    public List<TabState> Tabs { get; set; } = new();
}

/// <summary>
/// Saving and loading the tabs: tabs.json in the settings folder; terminal output (only when that option is on) in
/// %LOCALAPPDATA%\SwitchboardRM\scrollback, which isn't part of a roaming profile.
/// </summary>
internal static class SessionRestore
{
    private static string StateFile => Path.Combine(AppSettings.Folder, "tabs.json");
    private static readonly string ScrollbackDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.DataName, "scrollback");

    public static SavedTabs Load()
    {
        try
        {
            if (!File.Exists(StateFile)) return null;
            return JsonSerializer.Deserialize<SavedTabs>(File.ReadAllText(StateFile));
        }
        catch (Exception ex) { Log.Write("Couldn't read the saved tabs: " + ex.Message); return null; }
    }

    /// <summary>Save the tabs; scrollback maps a tab's index to its output (kept only for those). Old output is removed.</summary>
    public static void Save(SavedTabs tabs, Dictionary<int, string> scrollback)
    {
        try
        {
            ClearScrollback();
            if (scrollback != null && scrollback.Count > 0)
            {
                Directory.CreateDirectory(ScrollbackDir);
                foreach (var (index, text) in scrollback)
                {
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    string file = $"tab{index}.txt";
                    File.WriteAllText(Path.Combine(ScrollbackDir, file), text);
                    tabs.Tabs[index].ScrollbackFile = file;
                }
            }
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllText(StateFile, JsonSerializer.Serialize(tabs, new JsonSerializerOptions { WriteIndented = true }));
            Log.Write($"Saved {tabs.Tabs.Count} tab(s) to reopen next time" + (scrollback?.Count > 0 ? $" ({scrollback.Count} with their output)" : ""));
        }
        catch (Exception ex) { Log.Write("Couldn't save the tabs: " + ex.Message); }
    }

    /// <summary>Nothing to reopen (the option is off): remove any saved tabs and output.</summary>
    public static void Forget()
    {
        try { if (File.Exists(StateFile)) File.Delete(StateFile); } catch { }
        ClearScrollback();
    }

    public static string ReadScrollback(TabState tab)
    {
        if (string.IsNullOrEmpty(tab.ScrollbackFile)) return "";
        try
        {
            string path = Path.Combine(ScrollbackDir, Path.GetFileName(tab.ScrollbackFile));
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch { return ""; }
    }

    private static void ClearScrollback()
    {
        try
        {
            if (Directory.Exists(ScrollbackDir))
                foreach (var f in Directory.GetFiles(ScrollbackDir, "tab*.txt")) File.Delete(f);
        }
        catch { }
    }
}
