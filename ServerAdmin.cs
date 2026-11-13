using Amazon.EC2;
using Amazon.EC2.Model;

namespace SwitchboardRM;

/// <summary>Everything the Server window shows about one EC2 instance.</summary>
internal sealed class ServerDetails
{
    public string Id = "", Name = "", State = "", Type = "", Zone = "", ImageId = "", ImageName = "", Platform = "";
    public string PrivateIps = "", PublicIp = "", InstanceProfile = "", KeyName = "", Launched = "";
    public List<string> SecurityGroups = new();
    public List<string> Disks = new();
    public string SystemStatus = "", InstanceStatus = "";
    public List<string> Events = new();
    public List<KeyValuePair<string, string>> Tags = new();   // without Name and aws: tags
    public bool TerminationProtection, StopProtection, DetailedMonitoring;
    public string ShutdownBehavior = "stop";
}

/// <summary>
/// AWS calls for the Server window (AWS server > Manage server). Reads: details, status checks, scheduled events,
/// console screenshot, system log. Writes (each confirmed by the window first): name and tags, protections,
/// shutdown behaviour, detailed monitoring.
/// </summary>
internal static class ServerAdmin
{
    private static List<T> L<T>(List<T> list) => list ?? new List<T>();
    private static int N(object v) => v == null ? 0 : Convert.ToInt32(v);
    private static bool B(object v) => v is bool b && b;

    private static AmazonEC2Client Ec2(string profile, string region) => new(Aws.Credentials(profile), Aws.Region(region));

    public static async Task<ServerDetails> DetailsAsync(string profile, string region, string instanceId, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        var res = await ec2.DescribeInstancesAsync(new DescribeInstancesRequest { InstanceIds = new List<string> { instanceId } }, ct);
        var i = L(res.Reservations).SelectMany(r => L(r.Instances)).FirstOrDefault()
                ?? throw new InvalidOperationException($"{instanceId} wasn't found in {region}.");

        var d = new ServerDetails
        {
            Id = i.InstanceId, State = i.State?.Name?.Value ?? "", Type = i.InstanceType?.Value ?? "",
            Zone = i.Placement?.AvailabilityZone ?? "", ImageId = i.ImageId ?? "", Platform = i.PlatformDetails ?? "",
            PublicIp = i.PublicIpAddress ?? "", KeyName = i.KeyName ?? "",
            Launched = string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:yyyy-MM-dd HH:mm}", i.LaunchTime),
            DetailedMonitoring = i.Monitoring?.State?.Value == "enabled",
            Name = L(i.Tags).FirstOrDefault(t => t.Key == "Name")?.Value ?? "",
            Tags = L(i.Tags).Where(t => t.Key != "Name" && !t.Key.StartsWith("aws:", StringComparison.Ordinal))
                            .OrderBy(t => t.Key).Select(t => new KeyValuePair<string, string>(t.Key, t.Value)).ToList(),
            SecurityGroups = L(i.SecurityGroups).Select(g => $"{g.GroupName} ({g.GroupId})").ToList(),
            PrivateIps = string.Join(", ", L(i.NetworkInterfaces).OrderBy(n => N(n.Attachment?.DeviceIndex))
                .SelectMany(n => L(n.PrivateIpAddresses).Select(a => a.PrivateIpAddress + (B(a.Primary) ? "" : " (secondary)")))),
        };
        string arn = i.IamInstanceProfile?.Arn ?? "";
        d.InstanceProfile = arn.Contains("instance-profile/") ? arn[(arn.LastIndexOf('/') + 1)..] : "";

        // the rest is extra detail: a missing permission or a hiccup leaves that part out rather than failing the window
        try
        {
            var image = L((await ec2.DescribeImagesAsync(new DescribeImagesRequest { ImageIds = new List<string> { d.ImageId } }, ct)).Images).FirstOrDefault();
            d.ImageName = image?.Name ?? "";
        }
        catch { }

        try
        {
            var volumes = L((await ec2.DescribeVolumesAsync(new DescribeVolumesRequest
            {
                Filters = new List<Filter> { new("attachment.instance-id", new List<string> { instanceId }) },
            }, ct)).Volumes);
            d.Disks = volumes
                .Select(v => (Device: L(v.Attachments).FirstOrDefault(a => a.InstanceId == instanceId)?.Device ?? "", Volume: v))
                .OrderBy(x => x.Device != i.RootDeviceName).ThenBy(x => x.Device)
                .Select(x => $"{x.Device}  {N(x.Volume.Size)} GiB {x.Volume.VolumeType?.Value}" +
                             (N(x.Volume.Iops) > 0 ? $", {N(x.Volume.Iops)} IOPS" : "") +
                             (B(x.Volume.Encrypted) ? ", encrypted" : "") +
                             (x.Device == i.RootDeviceName ? "  (system disk)" : "") + $"  {x.Volume.VolumeId}")
                .ToList();
        }
        catch { }

        try
        {
            var status = L((await ec2.DescribeInstanceStatusAsync(new DescribeInstanceStatusRequest
            {
                InstanceIds = new List<string> { instanceId }, IncludeAllInstances = true,
            }, ct)).InstanceStatuses).FirstOrDefault();
            d.SystemStatus = status?.SystemStatus?.Status?.Value ?? "";
            // the .NET SDK names this one Status: a class can't have a property with its own name (InstanceStatus)
            d.InstanceStatus = status?.Status?.Status?.Value ?? "";
            d.Events = L(status?.Events)
                .Where(e => !(e.Description ?? "").StartsWith("[Completed]", StringComparison.OrdinalIgnoreCase) &&
                            !(e.Description ?? "").StartsWith("[Canceled]", StringComparison.OrdinalIgnoreCase))
                .Select(e => $"{e.Code?.Value}: {e.Description}" +
                             string.Format(System.Globalization.CultureInfo.CurrentCulture, " (from {0:yyyy-MM-dd HH:mm})", e.NotBefore))
                .ToList();
        }
        catch { }

        async Task<InstanceAttribute> Attr(string name)
        {
            try
            {
                return (await ec2.DescribeInstanceAttributeAsync(new DescribeInstanceAttributeRequest
                {
                    InstanceId = instanceId, Attribute = Amazon.EC2.InstanceAttributeName.FindValue(name),
                }, ct)).InstanceAttribute;
            }
            catch { return null; }
        }
        d.TerminationProtection = B((await Attr("disableApiTermination"))?.DisableApiTermination);
        d.StopProtection = B((await Attr("disableApiStop"))?.DisableApiStop);
        d.ShutdownBehavior = (await Attr("instanceInitiatedShutdownBehavior"))?.InstanceInitiatedShutdownBehavior is { Length: > 0 } sb ? sb : "stop";
        return d;
    }

    /// <summary>A screenshot of the server's console (JPEG), even when it can't be reached over the network.</summary>
    public static async Task<byte[]> ScreenshotAsync(string profile, string region, string instanceId, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        var res = await ec2.GetConsoleScreenshotAsync(new GetConsoleScreenshotRequest { InstanceId = instanceId, WakeUp = true }, ct);
        return string.IsNullOrEmpty(res.ImageData) ? Array.Empty<byte>() : Convert.FromBase64String(res.ImageData);
    }

    /// <summary>The server's system (console) log: the latest output where the instance type supports it, else AWS's last capture.</summary>
    public static async Task<string> SystemLogAsync(string profile, string region, string instanceId, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        GetConsoleOutputResponse res;
        try { res = await ec2.GetConsoleOutputAsync(new GetConsoleOutputRequest { InstanceId = instanceId, Latest = true }, ct); }
        catch (AmazonEC2Exception) { res = await ec2.GetConsoleOutputAsync(new GetConsoleOutputRequest { InstanceId = instanceId }, ct); }
        if (string.IsNullOrEmpty(res.Output)) return "";
        try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(res.Output)); }
        catch (FormatException) { return res.Output; }   // already plain text
    }

    /// <summary>Set the name and tags: changed and new tags written, removed ones deleted (aws: tags are never touched).</summary>
    public static async Task SaveTagsAsync(string profile, string region, string instanceId, string name,
        List<KeyValuePair<string, string>> before, List<KeyValuePair<string, string>> after, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        var write = new List<Tag> { new("Name", name) };
        write.AddRange(after.Where(t => !before.Any(b => b.Key == t.Key && b.Value == t.Value)).Select(t => new Tag(t.Key, t.Value)));
        await ec2.CreateTagsAsync(new CreateTagsRequest { Resources = new List<string> { instanceId }, Tags = write }, ct);

        var removed = before.Where(b => after.All(a => a.Key != b.Key)).Select(b => new Tag { Key = b.Key }).ToList();
        if (removed.Count > 0)
            await ec2.DeleteTagsAsync(new DeleteTagsRequest { Resources = new List<string> { instanceId }, Tags = removed }, ct);
        Log.Write($"Server: tags saved on {instanceId} (name {name}, {write.Count - 1} set, {removed.Count} removed)");
    }

    /// <summary>Protections, shutdown behaviour and monitoring: only what changed is sent (one setting per AWS call).</summary>
    public static async Task SaveProtectionAsync(string profile, string region, ServerDetails before,
        bool termination, bool stop, string shutdown, bool monitoring, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        string id = before.Id;
        if (termination != before.TerminationProtection)
            await ec2.ModifyInstanceAttributeAsync(new ModifyInstanceAttributeRequest { InstanceId = id, DisableApiTermination = termination }, ct);
        if (stop != before.StopProtection)
            await ec2.ModifyInstanceAttributeAsync(new ModifyInstanceAttributeRequest { InstanceId = id, DisableApiStop = stop }, ct);
        if (shutdown != before.ShutdownBehavior)
            await ec2.ModifyInstanceAttributeAsync(new ModifyInstanceAttributeRequest { InstanceId = id, InstanceInitiatedShutdownBehavior = shutdown }, ct);
        if (monitoring != before.DetailedMonitoring)
        {
            if (monitoring) await ec2.MonitorInstancesAsync(new MonitorInstancesRequest { InstanceIds = new List<string> { id } }, ct);
            else await ec2.UnmonitorInstancesAsync(new UnmonitorInstancesRequest { InstanceIds = new List<string> { id } }, ct);
        }
        Log.Write($"Server: protection settings saved on {id}");
    }
}
