using System.Diagnostics;
using System.Text;

namespace SwitchboardRM;

/// <summary>
/// Runs command-line helpers (such as "aws sso login" for SSO profiles) without a console window.
/// Everything else talks to AWS through the SDK (see AwsServices.cs).
/// </summary>
internal static class Cli
{
    public static async Task<(int Code, string Out, string Err)> RunAsync(string file, IEnumerable<string> args,
        CancellationToken ct = default, IDictionary<string, string> env = null)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        using var p = new Process { StartInfo = psi };
        try { p.Start(); ProcessJob.Add(p); }   // ends with Switchboard, even if it crashes
        catch (Exception ex) { return (-1, "", ex.Message); }

        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            // Only reached when a sign-in is cancelled. The tree kill is needed here because
            // a helper can start programs of its own (the AWS CLI opens a browser).
            try { p.Kill(true); } catch { }
            return (-2, "", "Cancelled");
        }
        return (p.ExitCode, await outTask, await errTask);
    }
}
