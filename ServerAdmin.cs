using Amazon.EC2;
using Amazon.EC2.Model;
using Ssm = Amazon.SimpleSystemsManagement.Model;

namespace SwitchboardRM;

/// <summary>One of a server's EBS disks.</summary>
internal sealed class DiskInfo
{
    public string VolumeId = "", Device = "", Type = "";
    public int SizeGb, Iops, Throughput;
    public bool Encrypted, IsRoot;
    public string Describe() =>
        $"{Device}  {SizeGb} GiB {Type}" + (Iops > 0 ? $", {Iops} IOPS" : "") + (Throughput > 0 ? $", {Throughput} MB/s" : "") +
        (Encrypted ? ", encrypted" : "") + (IsRoot ? "  (system disk)" : "") + $"  {VolumeId}";
}

/// <summary>One of a server's network interfaces.</summary>
internal sealed class InterfaceInfo
{
    public string Id = "", SubnetId = "", Description = "", PrimaryIp = "";
    public int DeviceIndex;
    public List<string> SecondaryIps = new(), GroupIds = new();
    public bool SourceDestCheck = true;
    public override string ToString() => $"{DeviceIndex + 1}: {Id}  {PrimaryIp}  ({SubnetId})" + (Description.Length > 0 ? $"  {Description}" : "");
}

/// <summary>An image (AMI) or snapshot, for the Images and snapshots page.</summary>
internal sealed class BackupItem
{
    public string Id = "", Name = "", State = "", Detail = "", VolumeId = "";
    public DateTime Created;
    public int SizeGb;
}

/// <summary>A server to run a command on (Run command page).</summary>
internal sealed record CommandTarget(string Id, string Name, bool IsWindows)
{
    public override string ToString() => $"{Name}  ({Id})";
}

/// <summary>One server's result from Run command.</summary>
internal sealed record CommandResult(string Id, string Name, string Status, string Output);

/// <summary>The Systems Manager agent and patch state of a server (Patches and agent page).</summary>
internal sealed class PatchInfo
{
    public string Ping = "", AgentVersion = "", Platform = "", LastPing = "", ComputerName = "";
    public bool? AgentLatest;
    public bool HasPatchState;
    public int Installed, Missing, Failed, PendingReboot, Rejected;
    public string LastOperation = "", Baseline = "";
    public List<string> Problems = new();   // missing and failed patches
}

/// <summary>Everything the Server window shows about one EC2 instance.</summary>
internal sealed class ServerDetails
{
    public string Architecture = "x86_64", RootDevice = "", VpcId = "";
    public List<InterfaceInfo> Interfaces = new();
    public List<DiskInfo> Volumes = new();
    public bool IsWindows => Platform.Contains("Windows", StringComparison.OrdinalIgnoreCase);
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
            Architecture = i.Architecture?.Value ?? "x86_64", RootDevice = i.RootDeviceName ?? "", VpcId = i.VpcId ?? "",
            Interfaces = L(i.NetworkInterfaces).OrderBy(n => N(n.Attachment?.DeviceIndex)).Select(n => new InterfaceInfo
            {
                Id = n.NetworkInterfaceId, SubnetId = n.SubnetId ?? "", Description = n.Description ?? "",
                DeviceIndex = N(n.Attachment?.DeviceIndex),
                PrimaryIp = L(n.PrivateIpAddresses).FirstOrDefault(a => B(a.Primary))?.PrivateIpAddress ?? n.PrivateIpAddress ?? "",
                SecondaryIps = L(n.PrivateIpAddresses).Where(a => !B(a.Primary)).Select(a => a.PrivateIpAddress).ToList(),
                GroupIds = L(n.Groups).Select(g => g.GroupId).ToList(),
                SourceDestCheck = n.SourceDestCheck == null || B(n.SourceDestCheck),
            }).ToList(),
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
            d.Volumes = volumes
                .Select(v =>
                {
                    string device = L(v.Attachments).FirstOrDefault(a => a.InstanceId == instanceId)?.Device ?? "";
                    return new DiskInfo
                    {
                        VolumeId = v.VolumeId, Device = device, Type = v.VolumeType?.Value ?? "", SizeGb = N(v.Size),
                        Iops = N(v.Iops), Throughput = N(v.Throughput), Encrypted = B(v.Encrypted), IsRoot = device == i.RootDeviceName,
                    };
                })
                .OrderBy(x => !x.IsRoot).ThenBy(x => x.Device).ToList();
            d.Disks = d.Volumes.Select(v => v.Describe()).ToList();
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

    // ================================================================== instance type

    private static async Task<string> StateAsync(AmazonEC2Client ec2, string instanceId, CancellationToken ct)
    {
        var res = await ec2.DescribeInstancesAsync(new DescribeInstancesRequest { InstanceIds = new List<string> { instanceId } }, ct);
        return L(res.Reservations).SelectMany(r => L(r.Instances)).FirstOrDefault()?.State?.Name?.Value ?? "";
    }

    private static async Task WaitForStateAsync(AmazonEC2Client ec2, string instanceId, string wanted, TimeSpan limit, CancellationToken ct)
    {
        var until = DateTime.UtcNow + limit;
        while (true)
        {
            string state = await StateAsync(ec2, instanceId, ct);
            if (state == wanted) return;
            if (DateTime.UtcNow > until) throw new TimeoutException($"the server is still {state} after {limit.TotalMinutes:0} minutes");
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    /// <summary>
    /// Change the instance type: stop (if running) and wait, change, then start again if it was running.
    /// progress gets a line per step.
    /// </summary>
    public static async Task ChangeTypeAsync(string profile, string region, string instanceId, string newType,
        IProgress<string> progress, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        string state = await StateAsync(ec2, instanceId, ct);
        bool wasRunning = state is "running" or "pending";
        if (state is not ("running" or "pending" or "stopped" or "stopping"))
            throw new InvalidOperationException($"the server is {state}; its type can only be changed when it's running or stopped");

        if (state != "stopped")
        {
            if (state != "stopping")
            {
                progress?.Report("Stopping the server ...");
                await ec2.StopInstancesAsync(new StopInstancesRequest { InstanceIds = new List<string> { instanceId } }, ct);
            }
            progress?.Report("Waiting for the server to stop ...");
            await WaitForStateAsync(ec2, instanceId, "stopped", TimeSpan.FromMinutes(15), ct);
        }

        progress?.Report($"Changing the type to {newType} ...");
        await ec2.ModifyInstanceAttributeAsync(new ModifyInstanceAttributeRequest { InstanceId = instanceId, InstanceType = newType }, ct);
        Log.Write($"Server: {instanceId} type changed to {newType}");

        if (wasRunning)
        {
            progress?.Report("Starting the server again ...");
            await ec2.StartInstancesAsync(new StartInstancesRequest { InstanceIds = new List<string> { instanceId } }, ct);
            await WaitForStateAsync(ec2, instanceId, "running", TimeSpan.FromMinutes(10), ct);
        }
    }

    // ================================================================== disks

    /// <summary>
    /// Grow a disk (and optionally change its type) while the server runs; returns once AWS has the new size ready
    /// (the volume is "optimizing" or done), which is when the operating system can use it.
    /// </summary>
    public static async Task GrowDiskAsync(string profile, string region, string volumeId, int newSizeGb, string newType,
        IProgress<string> progress, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        var request = new ModifyVolumeRequest { VolumeId = volumeId, Size = newSizeGb };
        if (!string.IsNullOrEmpty(newType)) request.VolumeType = Amazon.EC2.VolumeType.FindValue(newType);
        progress?.Report($"Asking AWS to grow {volumeId} to {newSizeGb} GiB ...");
        await ec2.ModifyVolumeAsync(request, ct);
        Log.Write($"Server: {volumeId} modified to {newSizeGb} GiB {newType}");

        var until = DateTime.UtcNow + TimeSpan.FromMinutes(15);
        while (true)
        {
            var mods = await ec2.DescribeVolumesModificationsAsync(new DescribeVolumesModificationsRequest { VolumeIds = new List<string> { volumeId } }, ct);
            var mod = L(mods.VolumesModifications).OrderByDescending(m => m.StartTime).FirstOrDefault();
            string state = mod?.ModificationState?.Value ?? "";
            if (state is "optimizing" or "completed") return;
            if (state == "failed") throw new InvalidOperationException("AWS couldn't modify the disk: " + (mod?.StatusMessage ?? "no reason given"));
            if (DateTime.UtcNow > until) throw new TimeoutException("the disk modification is still in progress after 15 minutes");
            progress?.Report($"Waiting for AWS to apply the new size ({(state.Length > 0 ? state : "starting")}) ...");
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
    }

    /// <summary>Create a disk in the server's zone and attach it at the next free device name. Returns the volume ID.</summary>
    public static async Task<string> AddDiskAsync(string profile, string region, ServerDetails server, int sizeGb, string type, bool encrypted,
        IProgress<string> progress, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        string device = NextDevice(server);

        progress?.Report($"Creating a {sizeGb} GiB {type} disk in {server.Zone} ...");
        var create = new CreateVolumeRequest
        {
            AvailabilityZone = server.Zone, Size = sizeGb, VolumeType = Amazon.EC2.VolumeType.FindValue(type),
            TagSpecifications = new List<TagSpecification>
            {
                new() { ResourceType = Amazon.EC2.ResourceType.Volume, Tags = new List<Tag> { new("Name", $"{server.Name} {device}") } },
            },
        };
        if (encrypted) create.Encrypted = true;
        string volumeId = (await ec2.CreateVolumeAsync(create, ct)).Volume.VolumeId;

        var until = DateTime.UtcNow + TimeSpan.FromMinutes(5);
        while (true)
        {
            var v = L((await ec2.DescribeVolumesAsync(new DescribeVolumesRequest { VolumeIds = new List<string> { volumeId } }, ct)).Volumes).FirstOrDefault();
            if (v?.State?.Value == "available") break;
            if (DateTime.UtcNow > until) throw new TimeoutException($"{volumeId} was created but isn't available after 5 minutes");
            progress?.Report("Waiting for the new disk to be ready ...");
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }

        progress?.Report($"Attaching {volumeId} as {device} ...");
        await ec2.AttachVolumeAsync(new AttachVolumeRequest { InstanceId = server.Id, VolumeId = volumeId, Device = device }, ct);
        Log.Write($"Server: {volumeId} ({sizeGb} GiB {type}) attached to {server.Id} as {device}");
        return volumeId;
    }

    /// <summary>Detach a (non-system) disk. The disk itself is kept and can be attached again.</summary>
    public static async Task DetachDiskAsync(string profile, string region, string volumeId, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        await ec2.DetachVolumeAsync(new DetachVolumeRequest { VolumeId = volumeId }, ct);
        Log.Write($"Server: {volumeId} detached");
    }

    // ================================================================== inside the operating system (Systems Manager)

    /// <summary>
    /// Run a script on the server through Systems Manager Run Command and wait for it. Returns its output; throws with
    /// the output if it fails. Needs the server running and managed by Systems Manager.
    /// </summary>
    public static async Task<string> RunScriptAsync(string profile, string region, string instanceId, bool windows, string script,
        CancellationToken ct, int timeoutSeconds = 600)
    {
        using var ssm = new Amazon.SimpleSystemsManagement.AmazonSimpleSystemsManagementClient(Aws.Credentials(profile), Aws.Region(region));
        var sent = await ssm.SendCommandAsync(new Amazon.SimpleSystemsManagement.Model.SendCommandRequest
        {
            DocumentName = windows ? "AWS-RunPowerShellScript" : "AWS-RunShellScript",
            InstanceIds = new List<string> { instanceId },
            Parameters = new Dictionary<string, List<string>> { ["commands"] = new List<string> { script } },
            TimeoutSeconds = timeoutSeconds,
            Comment = "Switchboard Remote Manager",
        }, ct);
        string commandId = sent.Command.CommandId;

        var until = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds + 60);
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            Amazon.SimpleSystemsManagement.Model.GetCommandInvocationResponse inv;
            try
            {
                inv = await ssm.GetCommandInvocationAsync(new Amazon.SimpleSystemsManagement.Model.GetCommandInvocationRequest
                {
                    CommandId = commandId, InstanceId = instanceId,
                }, ct);
            }
            catch (Amazon.SimpleSystemsManagement.Model.InvocationDoesNotExistException) { continue; }   // not registered yet

            string status = inv.Status?.Value ?? "";
            string output = ((inv.StandardOutputContent ?? "") + (string.IsNullOrWhiteSpace(inv.StandardErrorContent) ? "" : "\n" + inv.StandardErrorContent)).Trim();
            if (status == "Success") return output;
            if (status is "Failed" or "Cancelled" or "TimedOut" or "Cancelling")
                throw new InvalidOperationException($"the script {status.ToLowerInvariant()} on the server" + (output.Length > 0 ? ": " + output : ""));
            if (DateTime.UtcNow > until) throw new TimeoutException("the server didn't finish the script in time");
        }
    }

    /// <summary>The script that extends a grown disk's last partition and file system, found by its EBS volume ID.</summary>
    public static string ExtendScript(bool windows, string volumeId, string device)
    {
        string id = volumeId.Replace("-", "");   // NVMe disks report the volume ID without its dash as their serial number
        if (windows)
            return string.Join("\n", new[]
            {
                "$ErrorActionPreference = 'Stop'",
                "Update-HostStorageCache",
                $"$id = '{id}'",
                "$disk = Get-Disk | Where-Object { ($_.SerialNumber -replace '[^0-9a-zA-Z]', '') -like \"*$id*\" } | Select-Object -First 1",
                "if (-not $disk) { Write-Output 'Switchboard: this disk wasn''t found by its volume ID (older, non-NVMe instance type?). Extend it in Disk Management.'; exit 2 }",
                "$part = Get-Partition -DiskNumber $disk.Number | Sort-Object Offset | Select-Object -Last 1",
                // a partition without a letter reports [char]0, which PowerShell doesn't reliably treat as "no letter"
                "if ([string]$part.DriveLetter -notmatch '^[A-Za-z]$') { Write-Output \"Switchboard: the last partition on disk $($disk.Number) isn't a drive (e.g. a recovery partition), so it was left alone. Extend the drive in Disk Management.\"; exit 2 }",
                "$max = (Get-PartitionSupportedSize -DiskNumber $disk.Number -PartitionNumber $part.PartitionNumber).SizeMax",
                "if ($max -gt $part.Size + 1MB) {",
                "  Resize-Partition -DiskNumber $disk.Number -PartitionNumber $part.PartitionNumber -Size $max",
                "  Write-Output (\"Switchboard: extended {0}: to {1:N1} GB\" -f $part.DriveLetter, ($max / 1GB))",
                "} else { Write-Output (\"Switchboard: {0}: already uses the whole disk ({1:N1} GB)\" -f $part.DriveLetter, ($part.Size / 1GB)) }",
            });
        string xvd = device.Replace("/dev/sd", "/dev/xvd");
        return string.Join("\n", new[]
        {
            "set -e",
            $"dev=$(readlink -f /dev/disk/by-id/nvme-Amazon_Elastic_Block_Store_{id} 2>/dev/null || true)",
            $"[ -b \"$dev\" ] || dev={device}",
            $"[ -b \"$dev\" ] || dev={xvd}",
            "[ -b \"$dev\" ] || { echo \"Switchboard: this disk wasn't found on the server\"; exit 2; }",
            "part=$(lsblk -lnpo NAME,TYPE \"$dev\" | awk '$2==\"part\"{p=$1} END{print p}')",
            "target=$dev",
            "if [ -n \"$part\" ]; then num=$(echo \"$part\" | grep -o '[0-9]*$'); growpart \"$dev\" \"$num\" || true; target=$part; fi",
            "mnt=$(findmnt -nro TARGET -S \"$target\" | head -1)",
            "fs=$(findmnt -nro FSTYPE -S \"$target\" | head -1)",
            "[ -n \"$mnt\" ] || { echo \"Switchboard: $target isn't mounted; the partition was grown, the file system wasn't\"; exit 0; }",
            "case \"$fs\" in xfs) xfs_growfs \"$mnt\" >/dev/null ;; ext2|ext3|ext4) resize2fs \"$target\" >/dev/null ;; *) echo \"Switchboard: $fs file systems aren't extended automatically\"; exit 2 ;; esac",
            "echo \"Switchboard: extended $mnt ($fs):\"; df -h \"$mnt\" | tail -1",
        });
    }

    /// <summary>Windows: bring a newly attached disk online, initialize it (GPT) and format it (NTFS, next free letter).</summary>
    public static string FormatNewDiskScript(string volumeId)
    {
        string id = volumeId.Replace("-", "");
        return string.Join("\n", new[]
        {
            "$ErrorActionPreference = 'Stop'",
            $"$id = '{id}'",
            "for ($i = 0; $i -lt 30; $i++) { Update-HostStorageCache; $disk = Get-Disk | Where-Object { ($_.SerialNumber -replace '[^0-9a-zA-Z]', '') -like \"*$id*\" } | Select-Object -First 1; if ($disk) { break }; Start-Sleep -Seconds 2 }",
            "if (-not $disk) { Write-Output 'Switchboard: the new disk didn''t appear in Windows (older, non-NVMe instance type?). Set it up in Disk Management.'; exit 2 }",
            "if ($disk.PartitionStyle -ne 'RAW') { Write-Output 'Switchboard: the disk already has partitions, so it was left as it is.'; exit 0 }",
            "if ($disk.IsOffline) { Set-Disk -Number $disk.Number -IsOffline $false }",
            "if ($disk.IsReadOnly) { Set-Disk -Number $disk.Number -IsReadOnly $false }",
            "Initialize-Disk -Number $disk.Number -PartitionStyle GPT",
            "$vol = New-Partition -DiskNumber $disk.Number -UseMaximumSize -AssignDriveLetter | Format-Volume -FileSystem NTFS -Confirm:$false",
            "Write-Output (\"Switchboard: formatted as {0}: ({1:N1} GB, NTFS)\" -f $vol.DriveLetter, ($vol.Size / 1GB))",
        });
    }

    // ================================================================== network

    /// <summary>A network interface's security groups and source/destination check (only what changed is sent).</summary>
    public static async Task SaveInterfaceAsync(string profile, string region, InterfaceInfo before, List<string> groups, bool sourceDest, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        if (!groups.OrderBy(g => g).SequenceEqual(before.GroupIds.OrderBy(g => g)))
            await ec2.ModifyNetworkInterfaceAttributeAsync(new ModifyNetworkInterfaceAttributeRequest { NetworkInterfaceId = before.Id, Groups = groups }, ct);
        if (sourceDest != before.SourceDestCheck)
            await ec2.ModifyNetworkInterfaceAttributeAsync(new ModifyNetworkInterfaceAttributeRequest { NetworkInterfaceId = before.Id, SourceDestCheck = sourceDest }, ct);
        Log.Write($"Server: interface {before.Id} saved ({groups.Count} security group(s), source/dest check {(sourceDest ? "on" : "off")})");
    }

    /// <summary>Add a secondary private IP to an interface: a specific address, or the next free one (address empty). Returns the address.</summary>
    public static async Task<string> AddSecondaryIpAsync(string profile, string region, string interfaceId, string address, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        var request = new AssignPrivateIpAddressesRequest { NetworkInterfaceId = interfaceId };
        if (address.Length > 0) request.PrivateIpAddresses = new List<string> { address };
        else request.SecondaryPrivateIpAddressCount = 1;
        var res = await ec2.AssignPrivateIpAddressesAsync(request, ct);
        string assigned = L(res.AssignedPrivateIpAddresses).FirstOrDefault()?.PrivateIpAddress ?? address;
        Log.Write($"Server: {assigned} added to {interfaceId}");
        return assigned;
    }

    public static async Task RemoveSecondaryIpAsync(string profile, string region, string interfaceId, string address, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        await ec2.UnassignPrivateIpAddressesAsync(new UnassignPrivateIpAddressesRequest
        {
            NetworkInterfaceId = interfaceId, PrivateIpAddresses = new List<string> { address },
        }, ct);
        Log.Write($"Server: {address} removed from {interfaceId}");
    }

    /// <summary>The CIDR range of a subnet (to check a typed address), or "" if it can't be read.</summary>
    public static async Task<string> SubnetCidrAsync(string profile, string region, string subnetId, CancellationToken ct)
    {
        try
        {
            using var ec2 = Ec2(profile, region);
            return L((await ec2.DescribeSubnetsAsync(new DescribeSubnetsRequest { SubnetIds = new List<string> { subnetId } }, ct)).Subnets)
                .FirstOrDefault()?.CidrBlock ?? "";
        }
        catch { return ""; }
    }

    /// <summary>Attach, replace or remove (name empty) the server's instance profile.</summary>
    public static async Task SetInstanceProfileAsync(string profile, string region, string instanceId, string profileName, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        var current = L((await ec2.DescribeIamInstanceProfileAssociationsAsync(new DescribeIamInstanceProfileAssociationsRequest
        {
            Filters = new List<Filter> { new("instance-id", new List<string> { instanceId }) },
        }, ct)).IamInstanceProfileAssociations)
            .FirstOrDefault(a => a.State?.Value is "associated" or "associating");

        if (profileName.Length == 0)
        {
            if (current != null)
                await ec2.DisassociateIamInstanceProfileAsync(new DisassociateIamInstanceProfileRequest { AssociationId = current.AssociationId }, ct);
        }
        else if (current == null)
            await ec2.AssociateIamInstanceProfileAsync(new AssociateIamInstanceProfileRequest
            {
                InstanceId = instanceId, IamInstanceProfile = new IamInstanceProfileSpecification { Name = profileName },
            }, ct);
        else
            await ec2.ReplaceIamInstanceProfileAssociationAsync(new ReplaceIamInstanceProfileAssociationRequest
            {
                AssociationId = current.AssociationId, IamInstanceProfile = new IamInstanceProfileSpecification { Name = profileName },
            }, ct);
        Log.Write($"Server: {instanceId} instance profile set to {(profileName.Length > 0 ? profileName : "none")}");
    }

    // ================================================================== images and snapshots

    /// <summary>Create an image (AMI) of the server. Returns its ID; it becomes "available" in AWS's own time.</summary>
    public static async Task<string> CreateImageAsync(string profile, string region, ServerDetails server, string name, string description,
        bool reboot, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        var tags = new List<Tag> { new("Name", name), new("SourceInstance", server.Id) };
        var res = await ec2.CreateImageAsync(new CreateImageRequest
        {
            InstanceId = server.Id, Name = name, Description = description, NoReboot = !reboot,
            TagSpecifications = new List<TagSpecification>
            {
                new() { ResourceType = Amazon.EC2.ResourceType.Image, Tags = tags },
                new() { ResourceType = Amazon.EC2.ResourceType.Snapshot, Tags = tags },
            },
        }, ct);
        Log.Write($"Server: image {res.ImageId} ({name}) started from {server.Id}, reboot {(reboot ? "yes" : "no")}");
        return res.ImageId;
    }

    /// <summary>The account's own images, newest first.</summary>
    public static async Task<List<BackupItem>> MyImagesAsync(string profile, string region, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        return L((await ec2.DescribeImagesAsync(new DescribeImagesRequest { Owners = new List<string> { "self" } }, ct)).Images)
            .Select(img =>
            {
                DateTime.TryParse(Convert.ToString(img.CreationDate, System.Globalization.CultureInfo.InvariantCulture), out var created);
                return new BackupItem { Id = img.ImageId, Name = img.Name ?? "", State = img.State?.Value ?? "", Created = created, Detail = img.Description ?? "" };
            })
            .OrderByDescending(x => x.Created).Take(100).ToList();
    }

    /// <summary>Snapshot all the server's disks together (crash-consistent across disks). Returns the snapshot IDs.</summary>
    public static async Task<List<string>> SnapshotAllAsync(string profile, string region, ServerDetails server, string description, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        var res = await ec2.CreateSnapshotsAsync(new CreateSnapshotsRequest
        {
            InstanceSpecification = new InstanceSpecification { InstanceId = server.Id, ExcludeBootVolume = false },
            Description = description,
            CopyTagsFromSource = Amazon.EC2.CopyTagsFromSource.Volume,
        }, ct);
        var ids = L(res.Snapshots).Select(x => x.SnapshotId).ToList();
        Log.Write($"Server: snapshots of {server.Id}: {string.Join(", ", ids)}");
        return ids;
    }

    /// <summary>Snapshots of the server's current disks, newest first.</summary>
    public static async Task<List<BackupItem>> SnapshotsAsync(string profile, string region, ServerDetails server, CancellationToken ct)
    {
        if (server.Volumes.Count == 0) return new List<BackupItem>();
        using var ec2 = Ec2(profile, region);
        var devices = server.Volumes.ToDictionary(v => v.VolumeId, v => v.Device);
        var result = new List<BackupItem>();
        string token = null;
        do
        {
            var page = await ec2.DescribeSnapshotsAsync(new DescribeSnapshotsRequest
            {
                OwnerIds = new List<string> { "self" },
                Filters = new List<Filter> { new("volume-id", server.Volumes.Select(v => v.VolumeId).ToList()) },
                NextToken = token,
            }, ct);
            result.AddRange(L(page.Snapshots).Select(x => new BackupItem
            {
                Id = x.SnapshotId, VolumeId = x.VolumeId ?? "", SizeGb = N(x.VolumeSize),
                State = (x.State?.Value ?? "") + (x.State?.Value == "pending" && !string.IsNullOrEmpty(x.Progress) ? " " + x.Progress : ""),
                Detail = x.Description ?? "",
                Name = devices.TryGetValue(x.VolumeId ?? "", out var dev) ? dev : x.VolumeId ?? "",
                Created = x.StartTime is DateTime t ? t.ToLocalTime() : DateTime.MinValue,
            }));
            token = page.NextToken;
        } while (!string.IsNullOrEmpty(token));
        return result.OrderByDescending(x => x.Created).ToList();
    }

    /// <summary>
    /// Create a disk from a snapshot in the server's zone and attach it at the next free device name (to recover files).
    /// Returns the new volume's ID.
    /// </summary>
    public static async Task<string> DiskFromSnapshotAsync(string profile, string region, ServerDetails server, BackupItem snapshot,
        IProgress<string> progress, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        string device = NextDevice(server);
        progress?.Report($"Creating a disk from {snapshot.Id} in {server.Zone} ...");
        string volumeId = (await ec2.CreateVolumeAsync(new CreateVolumeRequest
        {
            AvailabilityZone = server.Zone, SnapshotId = snapshot.Id, VolumeType = Amazon.EC2.VolumeType.Gp3,
            TagSpecifications = new List<TagSpecification>
            {
                new() { ResourceType = Amazon.EC2.ResourceType.Volume, Tags = new List<Tag> { new("Name", $"{server.Name} restore of {snapshot.Id}") } },
            },
        }, ct)).Volume.VolumeId;
        await WaitAvailableAsync(ec2, volumeId, progress, ct);
        progress?.Report($"Attaching {volumeId} as {device} ...");
        await ec2.AttachVolumeAsync(new AttachVolumeRequest { InstanceId = server.Id, VolumeId = volumeId, Device = device }, ct);
        Log.Write($"Server: {volumeId} created from {snapshot.Id} and attached to {server.Id} as {device}");
        return volumeId;
    }

    private static string NextDevice(ServerDetails server)
    {
        var used = server.Volumes.Select(v => v.Device).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (char c = 'f'; c <= 'p'; c++)
        {
            string name = server.IsWindows ? $"xvd{c}" : $"/dev/sd{c}";
            string alt = server.IsWindows ? $"/dev/sd{c}" : $"/dev/xvd{c}";
            if (!used.Contains(name) && !used.Contains(alt)) return name;
        }
        throw new InvalidOperationException("no free device name is left (xvdf to xvdp are all in use)");
    }

    private static async Task WaitAvailableAsync(AmazonEC2Client ec2, string volumeId, IProgress<string> progress, CancellationToken ct)
    {
        var until = DateTime.UtcNow + TimeSpan.FromMinutes(10);
        while (true)
        {
            var v = L((await ec2.DescribeVolumesAsync(new DescribeVolumesRequest { VolumeIds = new List<string> { volumeId } }, ct)).Volumes).FirstOrDefault();
            if (v?.State?.Value == "available") return;
            if (DateTime.UtcNow > until) throw new TimeoutException($"{volumeId} was created but isn't available after 10 minutes");
            progress?.Report("Waiting for the new disk to be ready ...");
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }

    /// <summary>Windows: bring a disk restored from a snapshot online (Windows keeps a copy of an existing disk offline).</summary>
    public static string BringOnlineScript(string volumeId)
    {
        string id = volumeId.Replace("-", "");
        return string.Join("\n", new[]
        {
            "$ErrorActionPreference = 'Stop'",
            $"$id = '{id}'",
            "for ($i = 0; $i -lt 30; $i++) { Update-HostStorageCache; $disk = Get-Disk | Where-Object { ($_.SerialNumber -replace '[^0-9a-zA-Z]', '') -like \"*$id*\" } | Select-Object -First 1; if ($disk) { break }; Start-Sleep -Seconds 2 }",
            "if (-not $disk) { Write-Output 'Switchboard: the restored disk didn''t appear in Windows. Bring it online in Disk Management.'; exit 2 }",
            "if ($disk.IsOffline) { Set-Disk -Number $disk.Number -IsOffline $false }",
            "if ($disk.IsReadOnly) { Set-Disk -Number $disk.Number -IsReadOnly $false }",
            "$letters = (Get-Partition -DiskNumber $disk.Number | Where-Object { [string]$_.DriveLetter -match '^[A-Za-z]$' } | ForEach-Object { \"$($_.DriveLetter):\" }) -join ', '",
            "if (-not $letters) { Get-Partition -DiskNumber $disk.Number | Where-Object { $_.Type -eq 'Basic' -and [string]$_.DriveLetter -notmatch '^[A-Za-z]$' } | ForEach-Object { $_ | Add-PartitionAccessPath -AssignDriveLetter -ErrorAction SilentlyContinue }; $letters = (Get-Partition -DiskNumber $disk.Number | Where-Object { [string]$_.DriveLetter -match '^[A-Za-z]$' } | ForEach-Object { \"$($_.DriveLetter):\" }) -join ', ' }",
            "Write-Output \"Switchboard: the restored disk is online as $letters\"",
        });
    }

    // ================================================================== run command

    /// <summary>
    /// Run a script on several servers of the same platform at once (one Systems Manager command), and collect each
    /// server's status and output as it finishes. progress gets "n of m finished".
    /// </summary>
    public static async Task<List<CommandResult>> RunOnManyAsync(string profile, string region, List<CommandTarget> targets, string script,
        IProgress<string> progress, CancellationToken ct, int timeoutSeconds = 600)
    {
        using var ssm = new Amazon.SimpleSystemsManagement.AmazonSimpleSystemsManagementClient(Aws.Credentials(profile), Aws.Region(region));
        bool windows = targets[0].IsWindows;
        var sent = await ssm.SendCommandAsync(new Ssm.SendCommandRequest
        {
            DocumentName = windows ? "AWS-RunPowerShellScript" : "AWS-RunShellScript",
            InstanceIds = targets.Select(t => t.Id).ToList(),
            Parameters = new Dictionary<string, List<string>> { ["commands"] = new List<string> { script } },
            TimeoutSeconds = timeoutSeconds,
            Comment = "Switchboard Remote Manager: Run command",
        }, ct);
        string commandId = sent.Command.CommandId;
        Log.Write($"Run command {commandId} sent to {targets.Count} server(s): {string.Join(", ", targets.Select(t => t.Id))}");

        var results = new Dictionary<string, CommandResult>();
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(timeoutSeconds + 90);
        while (results.Count < targets.Count)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            foreach (var t in targets.Where(t => !results.ContainsKey(t.Id)))
            {
                Ssm.GetCommandInvocationResponse inv;
                try { inv = await ssm.GetCommandInvocationAsync(new Ssm.GetCommandInvocationRequest { CommandId = commandId, InstanceId = t.Id }, ct); }
                catch (Ssm.InvocationDoesNotExistException) { continue; }
                string status = inv.Status?.Value ?? "";
                if (status is "Pending" or "InProgress" or "Delayed" or "Cancelling") continue;
                string output = ((inv.StandardOutputContent ?? "") +
                                 (string.IsNullOrWhiteSpace(inv.StandardErrorContent) ? "" : "\n" + inv.StandardErrorContent)).Trim();
                results[t.Id] = new CommandResult(t.Id, t.Name, status, output);
                progress?.Report($"{results.Count} of {targets.Count} finished ...");
            }
            if (DateTime.UtcNow > until)
            {
                foreach (var t in targets.Where(t => !results.ContainsKey(t.Id)))
                    results[t.Id] = new CommandResult(t.Id, t.Name, "No result", "The server didn't report back in time.");
                break;
            }
        }
        return targets.Select(t => results[t.Id]).ToList();
    }

    // ================================================================== patches, agent, inventory

    public static async Task<PatchInfo> PatchInfoAsync(string profile, string region, string instanceId, CancellationToken ct)
    {
        using var ssm = new Amazon.SimpleSystemsManagement.AmazonSimpleSystemsManagementClient(Aws.Credentials(profile), Aws.Region(region));
        var info = new PatchInfo();

        var agent = L((await ssm.DescribeInstanceInformationAsync(new Ssm.DescribeInstanceInformationRequest
        {
            Filters = new List<Ssm.InstanceInformationStringFilter> { new() { Key = "InstanceIds", Values = new List<string> { instanceId } } },
        }, ct)).InstanceInformationList).FirstOrDefault();
        if (agent != null)
        {
            info.Ping = agent.PingStatus?.Value ?? "";
            info.AgentVersion = agent.AgentVersion ?? "";
            info.AgentLatest = agent.IsLatestVersion;
            info.Platform = $"{agent.PlatformName} {agent.PlatformVersion}".Trim();
            info.ComputerName = agent.ComputerName ?? "";
            info.LastPing = string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:yyyy-MM-dd HH:mm}", agent.LastPingDateTime);
        }

        try
        {
            var state = L((await ssm.DescribeInstancePatchStatesAsync(new Ssm.DescribeInstancePatchStatesRequest
            {
                InstanceIds = new List<string> { instanceId },
            }, ct)).InstancePatchStates).FirstOrDefault();
            if (state != null)
            {
                info.HasPatchState = true;
                info.Installed = N(state.InstalledCount);
                info.Missing = N(state.MissingCount);
                info.Failed = N(state.FailedCount);
                info.PendingReboot = N(state.InstalledPendingRebootCount);
                info.Rejected = N(state.InstalledRejectedCount);
                info.Baseline = state.BaselineId ?? "";
                info.LastOperation = $"{state.Operation?.Value}" +
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, " at {0:yyyy-MM-dd HH:mm}", state.OperationEndTime);
            }

            string token = null;
            do
            {
                var page = await ssm.DescribeInstancePatchesAsync(new Ssm.DescribeInstancePatchesRequest
                {
                    InstanceId = instanceId, NextToken = token, MaxResults = 50,
                    Filters = new List<Ssm.PatchOrchestratorFilter> { new() { Key = "State", Values = new List<string> { "Missing", "Failed" } } },
                }, ct);
                info.Problems.AddRange(L(page.Patches).Select(x =>
                    $"{x.State?.Value,-8} {(string.IsNullOrEmpty(x.KBId) ? "" : x.KBId + "  ")}{x.Title}" +
                    (string.IsNullOrEmpty(x.Classification) ? "" : $"  [{x.Classification}{(string.IsNullOrEmpty(x.Severity) ? "" : ", " + x.Severity)}]")));
                token = page.NextToken;
            } while (!string.IsNullOrEmpty(token) && info.Problems.Count < 500);
        }
        catch (Exception ex) { Log.Write("Server: patch state unavailable: " + ex.Message); }
        return info;
    }

    /// <summary>Installed applications from Systems Manager inventory ("" entries if inventory isn't collected).</summary>
    public static async Task<List<string>> ApplicationsAsync(string profile, string region, string instanceId, CancellationToken ct)
    {
        using var ssm = new Amazon.SimpleSystemsManagement.AmazonSimpleSystemsManagementClient(Aws.Credentials(profile), Aws.Region(region));
        var apps = new List<string>();
        string token = null;
        do
        {
            var page = await ssm.ListInventoryEntriesAsync(new Ssm.ListInventoryEntriesRequest
            {
                InstanceId = instanceId, TypeName = "AWS:Application", MaxResults = 50, NextToken = token,
            }, ct);
            foreach (var e in L(page.Entries))
            {
                e.TryGetValue("Name", out var name);
                e.TryGetValue("Version", out var version);
                e.TryGetValue("Publisher", out var publisher);
                if (!string.IsNullOrEmpty(name))
                    apps.Add($"{name}  {version}" + (string.IsNullOrEmpty(publisher) ? "" : $"  ({publisher})"));
            }
            token = page.NextToken;
        } while (!string.IsNullOrEmpty(token) && apps.Count < 5000);
        return apps.OrderBy(a => a, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// AWS's patch baseline document: Scan (report only) or Install, with a reboot choice for Install. Waits for it and
    /// returns its output.
    /// </summary>
    public static async Task<string> PatchBaselineAsync(string profile, string region, string instanceId, bool install, bool rebootIfNeeded,
        CancellationToken ct)
    {
        using var ssm = new Amazon.SimpleSystemsManagement.AmazonSimpleSystemsManagementClient(Aws.Credentials(profile), Aws.Region(region));
        var parameters = new Dictionary<string, List<string>> { ["Operation"] = new List<string> { install ? "Install" : "Scan" } };
        if (install) parameters["RebootOption"] = new List<string> { rebootIfNeeded ? "RebootIfNeeded" : "NoReboot" };
        var sent = await ssm.SendCommandAsync(new Ssm.SendCommandRequest
        {
            DocumentName = "AWS-RunPatchBaseline", InstanceIds = new List<string> { instanceId }, Parameters = parameters,
            TimeoutSeconds = 7200, Comment = "Switchboard Remote Manager: " + (install ? "install patches" : "scan for patches"),
        }, ct);
        Log.Write($"Server: patch baseline {(install ? "install" : "scan")} on {instanceId} ({sent.Command.CommandId})");

        var until = DateTime.UtcNow + TimeSpan.FromHours(2.2);
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            Ssm.GetCommandInvocationResponse inv;
            try { inv = await ssm.GetCommandInvocationAsync(new Ssm.GetCommandInvocationRequest { CommandId = sent.Command.CommandId, InstanceId = instanceId }, ct); }
            catch (Ssm.InvocationDoesNotExistException) { continue; }
            string status = inv.Status?.Value ?? "";
            if (status is "Pending" or "InProgress" or "Delayed" or "Cancelling")
            {
                if (DateTime.UtcNow > until) throw new TimeoutException("the patch operation is still running after two hours");
                continue;
            }
            string output = ((inv.StandardOutputContent ?? "") + (string.IsNullOrWhiteSpace(inv.StandardErrorContent) ? "" : "\n" + inv.StandardErrorContent)).Trim();
            if (status != "Success") throw new InvalidOperationException($"the patch operation ended {status.ToLowerInvariant()}" + (output.Length > 0 ? ":\n" + output : ""));
            return output;
        }
    }

    // ================================================================== terminate

    /// <summary>What terminating would do: whether termination protection is on, and which disks are deleted or kept.</summary>
    public static async Task<(bool Protected, List<string> Deleted, List<string> Kept)> TerminationInfoAsync(string profile, string region,
        string instanceId, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        var res = await ec2.DescribeInstancesAsync(new DescribeInstancesRequest { InstanceIds = new List<string> { instanceId } }, ct);
        var i = L(res.Reservations).SelectMany(r => L(r.Instances)).FirstOrDefault()
                ?? throw new InvalidOperationException($"{instanceId} wasn't found in {region}.");
        var deleted = new List<string>();
        var kept = new List<string>();
        foreach (var m in L(i.BlockDeviceMappings).Where(m => m.Ebs != null))
            (B(m.Ebs.DeleteOnTermination) ? deleted : kept).Add($"{m.DeviceName} ({m.Ebs.VolumeId})");
        bool isProtected = false;
        try
        {
            isProtected = B((await ec2.DescribeInstanceAttributeAsync(new DescribeInstanceAttributeRequest
            {
                InstanceId = instanceId, Attribute = Amazon.EC2.InstanceAttributeName.FindValue("disableApiTermination"),
            }, ct)).InstanceAttribute?.DisableApiTermination);
        }
        catch { }
        return (isProtected, deleted, kept);
    }

    public static async Task TerminateAsync(string profile, string region, string instanceId, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        await ec2.TerminateInstancesAsync(new TerminateInstancesRequest { InstanceIds = new List<string> { instanceId } }, ct);
        Log.Write($"Server: {instanceId} terminated");
    }
}
