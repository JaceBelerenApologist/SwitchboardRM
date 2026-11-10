using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Win32;

namespace SwitchboardRM;

/// <summary>A newer release on GitHub.</summary>
internal sealed record UpdateInfo(Version Version, string Tag, string Notes, string PageUrl, string SetupUrl, long SetupSize, string SetupSha256);

/// <summary>
/// Update checks against the project's GitHub releases. Checking is one anonymous request to GitHub's API (nothing
/// about the user or PC is sent). Installing downloads the release's setup .exe, checks it (size, SHA-256 when GitHub
/// gives one, an intact Authenticode signature by the same publisher as this copy) and runs it silently in the same
/// scope this copy was installed in; the setup closes Switchboard, updates it and starts it again. Copies that weren't
/// installed with the setup (zip) only get the release page.
/// </summary>
internal static class Updater
{
    private static readonly HttpClient Http = CreateClient();
    private const string AppId = "{3E4F5DA2-D33A-402B-8839-516C3670C9B2}";   // installer\SwitchboardRM.iss AppId

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SwitchboardRM/" + AppInfo.Version);   // GitHub requires one
        return http;
    }

    public static Version Current => ParseVersion(AppInfo.Version) ?? new Version(0, 0, 0);

    private static Version ParseVersion(string text)
    {
        text = (text ?? "").Trim().TrimStart('v', 'V');
        int dash = text.IndexOfAny(new[] { '-', '+' });
        if (dash > 0) text = text[..dash];
        return Version.TryParse(text, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }

    /// <summary>
    /// An administrator turned update checks off: DisableUpdateCheck = 1 (DWORD) under
    /// HKLM or HKCU\SOFTWARE\Policies\SwitchboardRM (e.g. from Intune, when IT deploys the updates).
    /// </summary>
    public static bool DisabledByPolicy
    {
        get
        {
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                try
                {
                    using var key = hive.OpenSubKey(@"SOFTWARE\Policies\" + AppInfo.DataName);
                    if (key?.GetValue("DisableUpdateCheck") is int v && v != 0) return true;
                }
                catch { }
            }
            return false;
        }
    }

    /// <summary>The latest release if it's newer than this copy, otherwise null.</summary>
    public static async Task<UpdateInfo> CheckAsync(CancellationToken ct)
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{AppInfo.Repo}/releases/latest", ct);
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>GitHub's "latest release" reply: the release if it's newer than this copy, otherwise null.</summary>
    internal static UpdateInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        var version = ParseVersion(tag);
        if (version == null || version <= Current) return null;

        string setupUrl = "", sha = "";
        long size = 0;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                if (!name.StartsWith("SwitchboardRM-Setup-", StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                setupUrl = a.GetProperty("browser_download_url").GetString() ?? "";
                size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                // newer GitHub releases give each file's checksum as "sha256:<hex>"
                if (a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String &&
                    d.GetString() is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                    sha = digest[7..];
                break;
            }
        }
        return new UpdateInfo(version, tag,
            root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
            root.TryGetProperty("html_url", out var url) ? url.GetString() ?? "" : $"https://github.com/{AppInfo.Repo}/releases",
            setupUrl, size, sha);
    }

    /// <summary>
    /// Whether this copy was installed by the setup, and for all users (Program Files) or just this user: from the
    /// setup's uninstall entry, if its install folder is the one this copy runs from.
    /// </summary>
    public static (bool Installed, bool AllUsers) InstallState()
    {
        string here = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\');
        foreach (var (hive, allUsers) in new[] { (RegistryHive.LocalMachine, true), (RegistryHive.CurrentUser, false) })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = root.OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{AppId}_is1");
                if (key?.GetValue("InstallLocation") is string location &&
                    string.Equals(Path.GetFullPath(location).TrimEnd('\\'), here, StringComparison.OrdinalIgnoreCase))
                    return (true, allUsers);
            }
            catch { }
        }
        return (false, false);
    }

    /// <summary>Download the setup to a temporary file and check it. Returns its path; throws with the reason if a check fails.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<int> percent, CancellationToken ct)
    {
        string path = Path.Combine(Path.GetTempPath(), $"SwitchboardRM-Setup-{update.Version}.exe");
        using (var response = await Http.GetAsync(update.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? update.SetupSize;
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(path);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read, last = -1;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                int p = total > 0 ? (int)(done * 100 / total) : 0;
                if (p != last) { percent?.Report(p); last = p; }
            }
        }

        try
        {
            long length = new FileInfo(path).Length;
            if (update.SetupSize > 0 && length != update.SetupSize)
                throw new InvalidOperationException($"the download is {length:N0} bytes instead of {update.SetupSize:N0}");
            if (update.SetupSha256.Length > 0)
            {
                await using var file = File.OpenRead(path);
                string actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
                if (!actual.Equals(update.SetupSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("the download's checksum doesn't match the release");
            }
            string problem = SignatureProblem(path);
            if (problem != null) throw new InvalidOperationException(problem);
            return path;
        }
        catch
        {
            try { File.Delete(path); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Null if the setup's signature is intact and from the same publisher as this running copy; otherwise why not.
    /// "Same publisher" compares the certificates' subject and issuer, so a renewed certificate still matches. The CA
    /// not being trusted on this PC is accepted (expected outside the organisation); a missing, broken or tampered
    /// signature isn't.
    /// </summary>
    public static string SignatureProblem(string setup)
    {
        X509Certificate2 mine;
        try { mine = new X509Certificate2(X509Certificate.CreateFromSignedFile(Environment.ProcessPath!)); }
        catch { return "this copy of Switchboard isn't signed, so there's nothing to check the update's signature against"; }

        X509Certificate2 theirs;
        try { theirs = new X509Certificate2(X509Certificate.CreateFromSignedFile(setup)); }
        catch { return "the downloaded setup isn't signed"; }

        uint result = VerifyFile(setup);
        bool intact = result is 0 or CERT_E_UNTRUSTEDROOT or CERT_E_CHAINING;
        if (!intact) return $"the downloaded setup's signature isn't valid (0x{result:X8})";

        if (!string.Equals(mine.Subject, theirs.Subject, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(mine.Issuer, theirs.Issuer, StringComparison.OrdinalIgnoreCase))
            return $"the downloaded setup is signed by \"{theirs.GetNameInfo(X509NameType.SimpleName, false)}\", not by this copy's publisher";
        return null;
    }

    /// <summary>Run the setup silently in this copy's scope. It closes Switchboard, updates it and starts it again.</summary>
    public static void RunSetup(string setup, bool allUsers)
    {
        Log.Write($"Updating: running {Path.GetFileName(setup)} ({(allUsers ? "all users" : "this user")})");
        Process.Start(new ProcessStartInfo(setup,
            $"/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS {(allUsers ? "/ALLUSERS" : "/CURRENTUSER")} /SBUPDATE=1")
        { UseShellExecute = true });
    }

    // ------------------------------------------------------------------ WinVerifyTrust

    private const uint CERT_E_UNTRUSTEDROOT = 0x800B0109, CERT_E_CHAINING = 0x800B010A;
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile, pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData, pSIPClientData;
        public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData, pwszURLReference;
        public uint dwProvFlags, dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern uint WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WINTRUST_DATA data);

    /// <summary>WinVerifyTrust's verdict on the file's Authenticode signature (0 = signed, intact and trusted).</summary>
    private static uint VerifyFile(string path)
    {
        var file = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(file, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,            // WTD_UI_NONE
                fdwRevocationChecks = 0,   // WTD_REVOKE_NONE (internal CAs often can't be reached for revocation from outside)
                dwUnionChoice = 1,         // WTD_CHOICE_FILE
                pFile = pFile,
                dwStateAction = 0,         // WTD_STATEACTION_IGNORE
            };
            return WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data);
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }
}
