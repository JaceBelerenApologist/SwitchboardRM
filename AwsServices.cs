using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Amazon;
using Amazon.EC2;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Amazon.SecurityToken;
using Amazon.SimpleSystemsManagement;
using Ec2 = Amazon.EC2.Model;
using Ssm = Amazon.SimpleSystemsManagement.Model;
using Sts = Amazon.SecurityToken.Model;

namespace SwitchboardRM;

/// <summary>Profiles, credentials and error text, read the same way the AWS CLI reads them (~/.aws/config and credentials).</summary>
internal static class Aws
{
    /// <summary>~/.aws/config (or AWS_CONFIG_FILE).</summary>
    public static string ConfigPath =>
        Environment.GetEnvironmentVariable("AWS_CONFIG_FILE") is { Length: > 0 } p ? p
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aws", "config");

    /// <summary>~/.aws/credentials (or AWS_SHARED_CREDENTIALS_FILE).</summary>
    public static string CredentialsPath =>
        Environment.GetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE") is { Length: > 0 } p ? p
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aws", "credentials");

    /// <summary>
    /// Every profile named in ~/.aws/config or ~/.aws/credentials. Read from the files directly: the SDK only lists
    /// profiles it can get keys from right now, so a signed-out aws-azure-login profile (settings in config, no keys
    /// yet) would otherwise look as if it didn't exist.
    /// </summary>
    public static List<string> ListProfiles()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Read(string path, bool isConfig)
        {
            try
            {
                if (!File.Exists(path)) return;
                foreach (var raw in File.ReadLines(path))
                {
                    string line = raw.Trim();
                    if (!line.StartsWith('[') || !line.EndsWith(']')) continue;
                    string n = line[1..^1].Trim();
                    if (isConfig)
                    {
                        // config uses [profile name] and [default]; skip [sso-session ...], [services ...] etc.
                        if (n.StartsWith("profile ", StringComparison.OrdinalIgnoreCase)) n = n[8..].Trim();
                        else if (!n.Equals("default", StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    if (n.Length > 0) names.Add(n);
                }
            }
            catch (Exception ex) { Log.Write($"Could not read {path}: {ex.Message}"); }
        }

        Read(ConfigPath, true);
        Read(CredentialsPath, false);
        try { foreach (var p in new CredentialProfileStoreChain().ListProfiles()) names.Add(p.Name); }
        catch (Exception ex) { Log.Write("Could not list AWS profiles: " + ex.Message); }

        return names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>True when the profile signs in through IAM Identity Center (aws sso login) rather than aws-azure-login.</summary>
    public static bool IsSsoProfile(string profile)
    {
        try
        {
            return new CredentialProfileStoreChain().TryGetProfile(Name(profile), out var p) &&
                   (!string.IsNullOrEmpty(p.Options.SsoSession) || !string.IsNullOrEmpty(p.Options.SsoStartUrl));
        }
        catch { return false; }
    }

    /// <summary>Fresh credentials for the profile. Read every time so a new sign-in is picked up straight away.</summary>
    public static AWSCredentials Credentials(string profile)
    {
        if (new CredentialProfileStoreChain().TryGetAWSCredentials(Name(profile), out var creds)) return creds;
        throw new AmazonClientException($"Profile '{Name(profile)}' was not found or has no credentials. Sign in first.");
    }

    /// <summary>
    /// When the profile's temporary keys run out, if the sign-in tool wrote that down
    /// (aws-azure-login writes "aws_expiration" into ~/.aws/credentials). Null if unknown.
    /// </summary>
    public static DateTimeOffset? CredentialExpiry(string profile)
    {
        try
        {
            string path = Environment.GetEnvironmentVariable("AWS_SHARED_CREDENTIALS_FILE");
            if (string.IsNullOrWhiteSpace(path))
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aws", "credentials");
            if (!File.Exists(path)) return null;

            string section = Name(profile);
            bool inSection = false;
            foreach (var raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    inSection = string.Equals(line[1..^1].Trim(), section, StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inSection) continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line[..eq].Trim().ToLowerInvariant();
                if (key is "aws_expiration" or "expiration" or "aws_session_expiration" &&
                    DateTimeOffset.TryParse(line[(eq + 1)..].Trim(), out var when))
                    return when;
            }
        }
        catch (Exception ex) { Log.Write("Could not read credential expiry: " + ex.Message); }
        return null;
    }

    /// <summary>
    /// Turn on aws-azure-login's "stay logged in" for this profile (azure_default_remember_me = true in ~/.aws/config),
    /// so it keeps the Microsoft session and can renew AWS keys later without a window. Returns true if it changed anything.
    /// </summary>
    public static bool EnsureRememberMe(string profile)
    {
        try
        {
            string path = Environment.GetEnvironmentVariable("AWS_CONFIG_FILE");
            if (string.IsNullOrWhiteSpace(path))
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aws", "config");
            if (!File.Exists(path)) return false;

            string name = Name(profile);
            string header = name == "default" ? "[default]" : $"[profile {name}]";
            var lines = File.ReadAllLines(path).ToList();
            int start = lines.FindIndex(l => string.Equals(l.Trim(), header, StringComparison.OrdinalIgnoreCase));
            if (start < 0) return false;

            int end = start + 1;
            while (end < lines.Count && !lines[end].TrimStart().StartsWith('[')) end++;

            for (int i = start + 1; i < end; i++)
            {
                int eq = lines[i].IndexOf('=');
                if (eq <= 0 || !string.Equals(lines[i][..eq].Trim(), "azure_default_remember_me", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.Equals(lines[i][(eq + 1)..].Trim(), "true", StringComparison.OrdinalIgnoreCase)) return false;
                lines[i] = "azure_default_remember_me = true";
                File.WriteAllLines(path, lines);
                return true;
            }

            lines.Insert(start + 1, "azure_default_remember_me = true");
            File.WriteAllLines(path, lines);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Could not turn on stay-logged-in: " + ex.Message);
            return false;
        }
    }

    /// <summary>One AWS region for the Settings list, e.g. ("ca-central-1", "Canada", "Central").</summary>
    public sealed record RegionInfo(string Code, string Group, string Place);

    // Display order of the geography groups; the names are the start of AWS's own region names.
    private static readonly (string Prefix, string Group)[] RegionGroups =
    {
        ("US ", "United States"), ("Canada", "Canada"), ("Mexico", "Mexico"), ("South America", "South America"),
        ("Europe", "Europe"), ("Israel", "Middle East"), ("Middle East", "Middle East"), ("Africa", "Africa"),
        ("Asia Pacific", "Asia Pacific"),
    };

    /// <summary>
    /// Every standard (commercial) AWS region the SDK knows about, grouped by geography. Works offline.
    /// China, GovCloud and the isolated regions are left out: they need separate accounts.
    /// </summary>
    public static List<RegionInfo> AllRegions()
    {
        var list = new List<RegionInfo>();
        foreach (var r in RegionEndpoint.EnumerableAllRegions)
        {
            string code = r.SystemName ?? "";
            if (code.Length == 0 || code == "aws-global" || code.StartsWith("cn-") || code.StartsWith("us-gov-") ||
                code.StartsWith("us-iso") || code.StartsWith("eu-iso") || code.StartsWith("eusc-"))
                continue;

            string name = string.IsNullOrWhiteSpace(r.DisplayName) ? code : r.DisplayName.Trim();
            if (name.Contains("GovCloud", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("ISO", StringComparison.Ordinal)) continue;

            string group = "Other", place = name;
            foreach (var (prefix, g) in RegionGroups)
            {
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                group = g;
                // "Canada West (Calgary)" -> "West (Calgary)"; "Israel (Tel Aviv)" stays as it is
                string rest = prefix == "Israel" ? name : name[prefix.Length..].Trim();
                place = rest.Length > 0 ? rest : name;
                break;
            }
            list.Add(new RegionInfo(code, group, place));
        }

        var order = RegionGroups.Select(g => g.Group).Distinct().Append("Other").ToList();
        return list
            .GroupBy(r => r.Code).Select(g => g.First())
            .OrderBy(r => order.IndexOf(r.Group))
            .ThenBy(r => r.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The profile's azure_default_username in ~/.aws/config (the account aws-azure-login signs in), or "".</summary>
    public static string AzureDefaultUsername(string profile) => ConfigValue(profile, "azure_default_username");

    /// <summary>
    /// The Microsoft sign-in settings (tenant ID, app ID URI) of an existing aws-azure-login profile in this user's
    /// ~/.aws/config, other than <paramref name="except"/>. Used to show the answers when adding another profile,
    /// so nothing organisation-specific has to be built into the app. Empty strings if there's no such profile.
    /// </summary>
    public static (string TenantId, string AppIdUri, string FromProfile) ExistingAzureSettings(string except)
    {
        foreach (var p in ListProfiles())
        {
            if (string.Equals(p, except, StringComparison.OrdinalIgnoreCase)) continue;
            string tenant = ConfigValue(p, "azure_tenant_id");
            if (tenant.Length > 0) return (tenant, ConfigValue(p, "azure_app_id_uri"), p);
        }
        return ("", "", "");
    }

    /// <summary>One value from a profile's section of ~/.aws/config, or "".</summary>
    public static string ConfigValue(string profile, string key)
    {
        try
        {
            if (!File.Exists(ConfigPath)) return "";
            string name = Name(profile);
            string header = name.Equals("default", StringComparison.OrdinalIgnoreCase) ? "default" : "profile " + name;
            bool inSection = false;
            foreach (var raw in File.ReadLines(ConfigPath))
            {
                string line = raw.Trim();
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    inSection = System.Text.RegularExpressions.Regex.Replace(line[1..^1].Trim(), @"\s+", " ")
                        .Equals(header, StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inSection) continue;
                int eq = line.IndexOf('=');
                if (eq > 0 && line[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    return line[(eq + 1)..].Trim();
            }
        }
        catch (Exception ex) { Log.Write($"Could not read {key}: " + ex.Message); }
        return "";
    }

    /// <summary>
    /// Delete a profile from this PC: its [profile X] section in ~/.aws/config and its [X] section in
    /// ~/.aws/credentials. Each file is backed up first (config.switchboard-backup, credentials.switchboard-backup).
    /// Nothing in AWS itself changes. Returns how many sections were removed.
    /// </summary>
    public static int RemoveProfile(string profile)
    {
        string name = Name(profile);
        string configHeader = name.Equals("default", StringComparison.OrdinalIgnoreCase) ? "default" : "profile " + name;
        return RemoveSection(ConfigPath, configHeader) + RemoveSection(CredentialsPath, name);
    }

    private static int RemoveSection(string path, string header)
    {
        if (!File.Exists(path)) return 0;
        var kept = new List<string>();
        bool skipping = false;
        int removed = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                string inner = System.Text.RegularExpressions.Regex.Replace(line[1..^1].Trim(), @"\s+", " ");
                skipping = inner.Equals(header, StringComparison.OrdinalIgnoreCase);
                if (skipping) { removed++; continue; }
            }
            if (!skipping) kept.Add(raw);
        }
        if (removed == 0) return 0;
        File.Copy(path, path + ".switchboard-backup", overwrite: true);
        File.WriteAllLines(path, kept);
        return removed;
    }

    /// <summary>
    /// Sign the profile out of AWS: an SSO profile runs "aws sso logout"; any other profile has its temporary keys
    /// (and their expiry) removed from ~/.aws/credentials. The profile's settings in ~/.aws/config are kept.
    /// Returns a short description of what was done, for the log.
    /// </summary>
    public static async Task<string> SignOutAsync(string profile)
    {
        string name = Name(profile);
        if (IsSsoProfile(name))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var r = await Cli.RunAsync(Tools.AwsCli, new[] { "sso", "logout", "--profile", name }, cts.Token);
            return r.Code == 0 ? $"signed {name} out of SSO" : $"aws sso logout failed ({r.Code}): {r.Err.Trim()}";
        }

        string path = CredentialsPath;
        if (!File.Exists(path)) return "no credentials file";

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "aws_access_key_id", "aws_secret_access_key", "aws_session_token", "aws_security_token",
            "aws_expiration", "aws_session_expiration", "expiration",
        };
        // Only the keys (and their expiry) are removed. The [profile] heading stays, so the profile is still
        // recognised and the next sign-in writes fresh keys into the same place.
        var kept = new List<string>();
        bool inSection = false;
        int removed = 0;
        foreach (var raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
                inSection = string.Equals(line[1..^1].Trim(), name, StringComparison.OrdinalIgnoreCase);
            else if (inSection && line.IndexOf('=') is > 0 and var eq && keys.Contains(line[..eq].Trim()))
            {
                removed++;
                continue;
            }
            kept.Add(raw);
        }

        if (removed == 0) return $"{name} had no keys to remove";
        File.WriteAllLines(path, kept);
        return $"removed {name}'s AWS keys from {path}";
    }

    public static RegionEndpoint Region(string region) => RegionEndpoint.GetBySystemName(region);

    public static async Task<(string Arn, string Error)> IdentityAsync(string profile, string region)
    {
        try
        {
            using var sts = new AmazonSecurityTokenServiceClient(Credentials(profile), Region(region));
            var r = await sts.GetCallerIdentityAsync(new Sts.GetCallerIdentityRequest());
            return (r.Arn, "");
        }
        catch (Exception ex) { return (null, Describe(ex)); }
    }

    /// <summary>Error text that keeps AWS's error code (e.g. "ExpiredToken: ...") so expired sign-ins are recognised.</summary>
    public static string Describe(Exception ex)
    {
        if (ex is AggregateException { InnerException: not null } ag) ex = ag.InnerException;
        return ex is AmazonServiceException s && !string.IsNullOrEmpty(s.ErrorCode) ? $"{s.ErrorCode}: {s.Message}" : ex.Message;
    }

    private static string Name(string profile) => string.IsNullOrWhiteSpace(profile) ? "default" : profile.Trim();
}

/// <summary>Finds EC2 instances that Systems Manager (Fleet Manager) manages.</summary>
internal static class InventoryService
{
    public static async Task<(List<Instance> Items, List<string> Errors)> LoadAsync(string profile, IEnumerable<string> regions)
    {
        var results = await Task.WhenAll(regions.Select(r => LoadRegionAsync(profile, r)));
        var items = new List<Instance>();
        var errors = new List<string>();
        foreach (var r in results)
        {
            items.AddRange(r.Items);
            if (r.Error != null) errors.Add(r.Error);
        }
        return (items, errors);
    }

    private static async Task<(List<Instance> Items, string Error)> LoadRegionAsync(string profile, string region)
    {
        try
        {
            var creds = Aws.Credentials(profile);
            using var ssm = new AmazonSimpleSystemsManagementClient(creds, Aws.Region(region));
            using var ec2 = new AmazonEC2Client(creds, Aws.Region(region));

            var managedTask = ListManagedAsync(ssm);
            var ec2Task = ListEc2Async(ec2);
            await Task.WhenAll(managedTask, ec2Task);
            var ec2Map = ec2Task.Result;

            var list = new List<Instance>();
            var managed = managedTask.Result.Where(m => !string.IsNullOrEmpty(m.InstanceId))
                .GroupBy(m => m.InstanceId).ToDictionary(g => g.Key, g => g.First());

            // EC2 instances, managed or not, so stopped ones show up and can be started
            foreach (var e in ec2Map.Values)
            {
                if (e.State?.Name?.Value is "terminated") continue;
                managed.TryGetValue(e.InstanceId, out var m);
                list.Add(ToInstance(region, m, e));
            }
            // machines Systems Manager manages that aren't EC2 instances here (e.g. on-premises "mi-" servers)
            foreach (var m in managed.Values.Where(m => !ec2Map.ContainsKey(m.InstanceId)))
                list.Add(ToInstance(region, m, null));
            return (list, null);
        }
        catch (Exception ex)
        {
            Log.Write($"Load failed in {region}: {ex}");
            return (new List<Instance>(), $"{region}: {Short(Aws.Describe(ex))}");
        }
    }

    /// <summary>One list row from what Systems Manager (m) and/or EC2 (e) know about a machine.</summary>
    internal static Instance ToInstance(string region, Ssm.InstanceInformation m, Ec2.Instance e)
    {
        string id = m?.InstanceId ?? e?.InstanceId ?? "";
        var tags = (e?.Tags ?? new List<Ec2.Tag>())
            .Select(t => new KeyValuePair<string, string>(t.Key ?? "", t.Value ?? "")).ToList();

        string name = tags.FirstOrDefault(t => t.Key == "Name").Value;
        if (string.IsNullOrEmpty(name)) name = m?.ComputerName;
        if (string.IsNullOrEmpty(name)) name = id;

        // OS: Systems Manager knows the exact version; EC2 only knows "Windows" / "Linux/UNIX" / "Red Hat ..."
        string platform = m?.PlatformType?.Value ??
                          (string.Equals(e?.Platform?.Value, "windows", StringComparison.OrdinalIgnoreCase) ? "Windows" : "Linux");
        string osName = m?.PlatformName ?? e?.PlatformDetails ?? "";
        string osVersion = m?.PlatformVersion ?? "";

        return new Instance
        {
            Name = name,
            Id = id,
            Type = e?.InstanceType?.Value ?? "",
            Region = region,
            Ip = m?.IPAddress ?? e?.PrivateIpAddress ?? "",
            Platform = platform,
            Os = OsNames.Friendly(osName, osVersion, platform),
            OsFull = string.Join(" ", new[] { osName, osVersion }.Where(x => !string.IsNullOrEmpty(x))),
            Ping = m?.PingStatus?.Value ?? "",
            Managed = m != null,
            State = e?.State?.Name?.Value ?? "",
            Tags = string.Join("; ", tags.Select(t => $"{t.Key}={t.Value}")),
        };
    }

    private static async Task<List<Ssm.InstanceInformation>> ListManagedAsync(AmazonSimpleSystemsManagementClient ssm)
    {
        var all = new List<Ssm.InstanceInformation>();
        string token = null;
        do
        {
            var r = await ssm.DescribeInstanceInformationAsync(new Ssm.DescribeInstanceInformationRequest { NextToken = token });
            all.AddRange(r.InstanceInformationList ?? new List<Ssm.InstanceInformation>());
            token = r.NextToken;
        } while (!string.IsNullOrEmpty(token));
        return all;
    }

    private static async Task<Dictionary<string, Ec2.Instance>> ListEc2Async(AmazonEC2Client ec2)
    {
        var map = new Dictionary<string, Ec2.Instance>();
        string token = null;
        do
        {
            var r = await ec2.DescribeInstancesAsync(new Ec2.DescribeInstancesRequest { NextToken = token });
            foreach (var res in r.Reservations ?? new List<Ec2.Reservation>())
                foreach (var i in res.Instances ?? new List<Ec2.Instance>())
                    map[i.InstanceId] = i;
            token = r.NextToken;
        } while (!string.IsNullOrEmpty(token));
        return map;
    }

    private static string Short(string s)
    {
        s = (s ?? "").Trim().Replace("\r", " ").Replace("\n", " ");
        return s.Length > 220 ? s[..220] + "..." : s;
    }
}

/// <summary>Start, stop, reboot and re-check one EC2 instance.</summary>
internal static class InstanceActions
{
    public enum Action { Start, Stop, Reboot }

    public static Task RunAsync(string profile, Instance inst, Action action) => Task.Run(async () =>
    {
        using var ec2 = new AmazonEC2Client(Aws.Credentials(profile), Aws.Region(inst.Region));
        var ids = new List<string> { inst.Id };
        switch (action)
        {
            case Action.Start: await ec2.StartInstancesAsync(new Ec2.StartInstancesRequest { InstanceIds = ids }); break;
            case Action.Stop: await ec2.StopInstancesAsync(new Ec2.StopInstancesRequest { InstanceIds = ids }); break;
            case Action.Reboot: await ec2.RebootInstancesAsync(new Ec2.RebootInstancesRequest { InstanceIds = ids }); break;
        }
    });

    /// <summary>Fresh EC2 state and Systems Manager status for one instance (null if it no longer exists).</summary>
    public static Task<Instance> RefreshAsync(string profile, Instance inst) => Task.Run(async () =>
    {
        var creds = Aws.Credentials(profile);
        using var ec2 = new AmazonEC2Client(creds, Aws.Region(inst.Region));
        using var ssm = new AmazonSimpleSystemsManagementClient(creds, Aws.Region(inst.Region));

        var ec2Task = ec2.DescribeInstancesAsync(new Ec2.DescribeInstancesRequest { InstanceIds = new List<string> { inst.Id } });
        var ssmTask = ssm.DescribeInstanceInformationAsync(new Ssm.DescribeInstanceInformationRequest
        {
            Filters = new List<Ssm.InstanceInformationStringFilter>
            {
                new() { Key = "InstanceIds", Values = new List<string> { inst.Id } },
            },
        });
        await Task.WhenAll(ec2Task, ssmTask);

        var e = (ec2Task.Result.Reservations ?? new List<Ec2.Reservation>())
            .SelectMany(r => r.Instances ?? new List<Ec2.Instance>()).FirstOrDefault();
        var m = (ssmTask.Result.InstanceInformationList ?? new List<Ssm.InstanceInformation>()).FirstOrDefault();
        return e == null && m == null ? null : InventoryService.ToInstance(inst.Region, m, e);
    });
}

internal sealed class TunnelException : Exception
{
    public TunnelException(string message) : base(message) { }
}

/// <summary>
/// A hidden Session Manager port-forward to port 3389 of one instance.
/// The SDK starts the session; Amazon's session-manager-plugin carries the traffic (the SDK has no data channel).
/// </summary>
internal sealed class Tunnel : IDisposable
{
    private const string DocumentName = "AWS-StartPortForwardingSession";

    public int Port { get; private set; }
    public Process Proc { get; private set; }

    private string _sessionId;
    private string _profile;
    private string _region;
    private readonly StringBuilder _output = new();
    private readonly object _gate = new();
    private StreamWriter _log;

    private Tunnel() { }

    /// <param name="remotePort">Port on the instance: 3389 for Remote Desktop, 22 for SSH.</param>
    public static async Task<Tunnel> StartAsync(Instance inst, string profile, int remotePort = 3389)
    {
        string plugin = Tools.BundledPlugin ?? FindPlugin() ?? throw new TunnelException(
            "The AWS Session Manager plugin wasn't found: it's not in Switchboard's tools folder and isn't installed.");

        var t = new Tunnel { Port = FreePort(), _profile = profile, _region = inst.Region };
        Directory.CreateDirectory(Log.Dir);
        string logPath = Path.Combine(Log.Dir, $"tunnel-{inst.Id}-{t.Port}.log");
        t._log = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        { AutoFlush = true };

        var parameters = new Dictionary<string, List<string>>
        {
            ["portNumber"] = new() { remotePort.ToString() },
            ["localPortNumber"] = new() { t.Port.ToString() },
        };

        Ssm.StartSessionResponse session;
        try
        {
            using var ssm = new AmazonSimpleSystemsManagementClient(Aws.Credentials(profile), Aws.Region(inst.Region));
            session = await ssm.StartSessionAsync(new Ssm.StartSessionRequest
            {
                Target = inst.Id,
                DocumentName = DocumentName,
                Parameters = parameters,
            });
        }
        catch (Exception ex)
        {
            t.Dispose();
            throw new TunnelException(Aws.Describe(ex));
        }
        t._sessionId = session.SessionId;
        t.Append($"Session {session.SessionId} started");

        // Same arguments the AWS CLI hands to the plugin.
        string responseJson = JsonSerializer.Serialize(new
        {
            session.SessionId,
            session.TokenValue,
            session.StreamUrl,
        });
        string requestJson = JsonSerializer.Serialize(new
        {
            Target = inst.Id,
            DocumentName,
            Parameters = parameters,
        });

        var psi = new ProcessStartInfo(plugin)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[]
        {
            responseJson, inst.Region, "StartSession", profile ?? "", requestJson,
            $"https://ssm.{inst.Region}.amazonaws.com",
        }) psi.ArgumentList.Add(a);

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => t.Append(e.Data);
        p.ErrorDataReceived += (_, e) => t.Append(e.Data);

        try { p.Start(); ProcessJob.Add(p); }   // ends with Switchboard, even if it crashes
        catch (Exception ex)
        {
            t.Dispose();
            throw new TunnelException("Could not start the Session Manager plugin: " + ex.Message);
        }
        t.Proc = p;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            if (p.HasExited) break;
            if (IsListening(t.Port)) return t;
            await Task.Delay(250);
        }

        string text = t.Captured();
        t.Dispose();
        throw new TunnelException(string.IsNullOrWhiteSpace(text) ? "The tunnel did not start in time." : text);
    }

    /// <summary>session-manager-plugin.exe on PATH, or in its default install folder.</summary>
    private static string FindPlugin()
    {
        const string exe = "session-manager-plugin.exe";
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Amazon", "SessionManagerPlugin", "bin"));
        foreach (var d in dirs)
        {
            try
            {
                string f = Path.Combine(d.Trim('"'), exe);
                if (File.Exists(f)) return f;
            }
            catch { /* bad PATH entry */ }
        }
        return null;
    }

    private void Append(string line)
    {
        if (line == null) return;
        lock (_gate)
        {
            _output.AppendLine(line);
            try { _log?.WriteLine(line); } catch { }
        }
    }

    private string Captured()
    {
        lock (_gate)
        {
            string s = _output.ToString().Trim();
            return s.Length > 1500 ? s[^1500..] : s;
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static bool IsListening(int port) =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);

    private static readonly List<Task> _cleanup = new();

    private static void Track(Task t)
    {
        lock (_cleanup) { _cleanup.RemoveAll(x => x.IsCompleted); _cleanup.Add(t); }
    }

    /// <summary>Wait (up to the timeout) for "end session" calls still on their way to AWS. Used before signing out.</summary>
    public static async Task WaitForCleanupAsync(TimeSpan timeout)
    {
        Task[] pending;
        lock (_cleanup) pending = _cleanup.Where(x => !x.IsCompleted).ToArray();
        if (pending.Length > 0) await Task.WhenAny(Task.WhenAll(pending), Task.Delay(timeout));
    }

    public void Dispose()
    {
        // The plugin is started directly (not through aws.exe), so it has no child processes: a plain Kill is enough.
        // Kill(true) would walk every process on the PC looking for children, which is slow and throws
        // "Access is denied" for each system process it can't inspect.
        var proc = Proc;
        Proc = null;
        if (proc != null)
        {
            try { proc.Kill(); } catch { /* already exited */ }
            proc.Dispose();
        }

        // Close the session on AWS's side too, rather than leaving it to time out.
        string id = _sessionId;
        _sessionId = null;
        if (id != null)
        {
            string profile = _profile, region = _region;
            Track(Task.Run(async () =>
            {
                try
                {
                    using var ssm = new AmazonSimpleSystemsManagementClient(Aws.Credentials(profile), Aws.Region(region));
                    await ssm.TerminateSessionAsync(new Ssm.TerminateSessionRequest { SessionId = id });
                }
                catch (Exception ex) { Log.Write($"Could not end session {id}: {Aws.Describe(ex)}"); }
            }));
        }

        lock (_gate)
        {
            try { _log?.Dispose(); } catch { }
            _log = null;
        }
    }
}
