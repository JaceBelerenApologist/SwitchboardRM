using System.Drawing;

namespace SwitchboardRM;

/// <summary>
/// Each shell's own program icon (cmd.exe, powershell.exe, pwsh.exe, Git Bash, wsl.exe, claude.exe), read from the
/// executable as Windows shows it, for shell tabs and the shell menus. Cached per program. Null when there's no icon
/// to read (e.g. Claude Code installed with npm, which is a .cmd): the tab keeps the generic terminal glyph.
/// </summary>
internal static class ShellIcons
{
    private static readonly Dictionary<string, Image> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static Image For(ShellInfo shell, int size = 16)
    {
        string source = IconSource(shell);
        if (source == null) return null;
        string key = source + "|" + size;
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;
        }
        Image image = null;
        try
        {
            // the icon at the size wanted (crisp); otherwise the default icon, scaled
            using var icon = Icon.ExtractIcon(source, 0, size) ?? Icon.ExtractAssociatedIcon(source);
            if (icon != null)
            {
                using var bitmap = icon.ToBitmap();
                image = bitmap.Width == size && bitmap.Height == size ? new Bitmap(bitmap) : new Bitmap(bitmap, size, size);
            }
        }
        catch (Exception ex) { Log.Write($"No icon for {shell.Name} ({source}): {ex.Message}"); }
        lock (Cache) Cache[key] = image;
        return image;
    }

    /// <summary>The program whose icon stands for this shell, or null.</summary>
    private static string IconSource(ShellInfo shell)
    {
        if (shell.Id == "claude-code")
        {
            // the native install's claude.exe; an npm install runs claude.cmd through cmd.exe and has no icon of its own
            string claude = Programs(shell.CommandLine).FirstOrDefault(p => p.EndsWith("claude.exe", StringComparison.OrdinalIgnoreCase));
            return claude != null && File.Exists(claude) ? claude : null;
        }
        string exe = Programs(shell.CommandLine).FirstOrDefault();
        if (exe == null) return null;
        if (Path.GetFileName(exe).Equals("bash.exe", StringComparison.OrdinalIgnoreCase))
        {
            // Git Bash: bash.exe (in Git\bin or Git\usr\bin) has a generic icon; git-bash.exe in Git's folder has the real one
            string dir = Path.GetDirectoryName(exe) ?? "";
            foreach (var candidate in new[] { Path.Combine(dir, "..", "git-bash.exe"), Path.Combine(dir, "..", "..", "git-bash.exe") })
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>The quoted (or first, unquoted) program paths in a command line, in order.</summary>
    private static IEnumerable<string> Programs(string commandLine)
    {
        string text = (commandLine ?? "").Trim();
        int i = 0;
        bool first = true;
        while (i < text.Length)
        {
            if (text[i] == '"')
            {
                int end = text.IndexOf('"', i + 1);
                if (end < 0) yield break;
                yield return text.Substring(i + 1, end - i - 1);
                i = end + 1;
            }
            else if (first)
            {
                int end = text.IndexOf(' ', i);
                yield return end < 0 ? text[i..] : text[i..end];
                i = end < 0 ? text.Length : end;
            }
            else i++;
            first = false;
        }
    }
}
