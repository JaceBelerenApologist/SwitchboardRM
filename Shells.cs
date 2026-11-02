using System.Diagnostics;
using System.Text;

namespace SwitchboardRM;

/// <summary>A local shell that can be opened in a tab.</summary>
internal sealed record ShellInfo(string Id, string Name, string CommandLine);

/// <summary>
/// The local shells on this PC: Command Prompt and Windows PowerShell always, plus PowerShell 7, Git Bash and WSL
/// distributions when installed. WSL is looked up in the background (it can be slow), so it appears a moment later.
/// </summary>
internal static class Shells
{
    private static List<ShellInfo> _wsl = new();
    private static readonly object _gate = new();
    private static Task _wslLookup;

    public static List<ShellInfo> Available()
    {
        var list = new List<ShellInfo>();
        string sys = Environment.SystemDirectory;

        string cmd = Environment.GetEnvironmentVariable("ComSpec");
        if (string.IsNullOrWhiteSpace(cmd) || !File.Exists(cmd)) cmd = Path.Combine(sys, "cmd.exe");
        list.Add(new ShellInfo("cmd", "Command Prompt", Quote(cmd)));

        string winPs = Path.Combine(sys, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(winPs)) list.Add(new ShellInfo("powershell", "Windows PowerShell", Quote(winPs) + " -NoLogo"));

        string pwsh = FindPwsh();
        if (pwsh != null) list.Add(new ShellInfo("pwsh", "PowerShell 7", Quote(pwsh) + " -NoLogo"));

        string gitBash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe");
        if (File.Exists(gitBash)) list.Add(new ShellInfo("git-bash", "Git Bash", Quote(gitBash) + " --login -i"));

        StartWslLookup();
        lock (_gate) list.AddRange(_wsl);
        return list;
    }

    /// <summary>The shell to open by default: the one chosen in Settings, else PowerShell 7, else Windows PowerShell.</summary>
    public static ShellInfo Default(string chosenId)
    {
        var all = Available();
        return all.FirstOrDefault(s => s.Id == chosenId)
               ?? all.FirstOrDefault(s => s.Id == "pwsh")
               ?? all.FirstOrDefault(s => s.Id == "powershell")
               ?? all[0];
    }

    public static ShellInfo Find(string id) => Available().FirstOrDefault(s => s.Id == id);

    private static string FindPwsh()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string f = Path.Combine(dir.Trim('"'), "pwsh.exe");
                if (File.Exists(f)) return f;
            }
            catch { }
        }
        string pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        return File.Exists(pf) ? pf : null;
    }

    /// <summary>List WSL distributions once, in the background ("wsl --list --quiet" prints UTF-16).</summary>
    public static void StartWslLookup()
    {
        if (_wslLookup != null) return;
        string wsl = Path.Combine(Environment.SystemDirectory, "wsl.exe");
        if (!File.Exists(wsl)) { _wslLookup = Task.CompletedTask; return; }

        _wslLookup = Task.Run(async () =>
        {
            try
            {
                var psi = new ProcessStartInfo(wsl, "--list --quiet")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.Unicode,
                };
                using var p = Process.Start(psi);
                var read = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return; }
                if (p.ExitCode != 0) return; // WSL not set up
                var distros = (await read).Split('\n')
                    .Select(l => l.Replace("\0", "").Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase))
                    .Distinct()
                    .Select(d => new ShellInfo("wsl:" + d, "WSL: " + d, Quote(wsl) + " -d " + Quote(d) + " --cd ~"))
                    .ToList();
                lock (_gate) _wsl = distros;
            }
            catch (Exception ex) { Log.Write("WSL lookup failed: " + ex.Message); }
        });
    }

    private static string Quote(string s) => s.Contains(' ') ? "\"" + s + "\"" : s;
}
