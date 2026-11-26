using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.KeyManagementService;
using Kms = Amazon.KeyManagementService.Model;

namespace SwitchboardRM;

/// <summary>An EBS volume, for the Volumes window and the Disks page.</summary>
internal sealed class VolumeRow
{
    public string Id = "", Name = "", Region = "", Zone = "", Type = "", State = "", KmsKeyId = "", SnapshotId = "";
    public string InstanceId = "", InstanceName = "", Device = "";
    public int SizeGb, Iops, Throughput;
    public bool Encrypted, DeleteOnTermination;
    public DateTime Created;
    public List<KeyValuePair<string, string>> Tags = new();   // without Name
    public double MonthlyCost;                                  // estimate, USD (0 = unknown)
    public bool Attached => InstanceId.Length > 0;
    public string CreatedBy => Tags.FirstOrDefault(t => t.Key.Equals("CreatedBy", StringComparison.OrdinalIgnoreCase)).Value ?? "";
    public string Label => Name.Length > 0 ? $"{Name} ({Id})" : Id;
}

/// <summary>An EBS snapshot owned by the account.</summary>
internal sealed class SnapshotRow
{
    public string Id = "", Name = "", Region = "", VolumeId = "", Description = "", State = "", Progress = "", KmsKeyId = "";
    public string SourceName = "";              // the source volume's name or server, when it still exists
    public int SizeGb;
    public DateTime Started;
    public bool Encrypted, SourceExists;
    public List<string> UsedBy = new();         // AMIs that need it ("name (ami-...)")
    public List<KeyValuePair<string, string>> Tags = new();   // without Name
    public double MonthlyCostMax;               // estimate: snapshots are incremental, so this is the most it can cost
    public string Label => Name.Length > 0 ? $"{Name} ({Id})" : Id;
}

/// <summary>An encryption key choice for new volumes ("" = AWS managed aws/ebs).</summary>
internal sealed class KeyChoice
{
    public string KeyId = "", Label = "", Alias = "", KeyGuid = "";
    public bool AwsManaged, IsAccountDefault;
    public override string ToString() => Label + (IsAccountDefault ? "   (account default)" : "");
}

/// <summary>A server a volume can be attached to (same availability zone).</summary>
internal sealed class InstanceChoice
{
    public string Id = "", Name = "", Zone = "", State = "";
    public bool Windows;
    public HashSet<string> Devices = new(StringComparer.OrdinalIgnoreCase);
    public override string ToString() => (Name.Length > 0 ? $"{Name} ({Id})" : Id) + (State == "running" ? "" : $"  [{State}]");
}

/// <summary>A disk to create: its type and performance, and its name part (the drive letter, by convention).</summary>
internal sealed class NewDisk
{
    public string Label = "", Type = "gp3", SnapshotId = "";
    public int SizeGb = 100, Iops = 3000, Throughput = 125;
}

/// <summary>EBS limits per volume type, checked before anything is sent to AWS.</summary>
internal static class VolumeRules
{
    public static readonly string[] Types = { "gp3", "gp2", "io2", "io1", "st1", "sc1" };

    public static bool HasIops(string type) => type is "gp3" or "io1" or "io2";
    public static bool HasThroughput(string type) => type == "gp3";
    public static int MinSize(string type) => type is "st1" or "sc1" ? 125 : type is "io1" or "io2" ? 4 : 1;
    public static int MaxSize(string type) => type == "io2" ? 65536 : 16384;
    public static (int Min, int Max) IopsRange(string type) => type switch
    {
        "gp3" => (3000, 16000), "io1" => (100, 64000), "io2" => (100, 256000), _ => (0, 0),
    };
    public static (int Min, int Max) ThroughputRange(string type) => type == "gp3" ? (125, 1000) : (0, 0);

    /// <summary>What's wrong with this combination, or null if AWS will accept it.</summary>
    public static string Check(string type, int sizeGb, int iops, int throughput)
    {
        if (sizeGb < MinSize(type) || sizeGb > MaxSize(type)) return $"{type} disks are {MinSize(type):N0} to {MaxSize(type):N0} GiB";
        if (HasIops(type))
        {
            var (min, max) = IopsRange(type);
            if (iops < min || iops > max) return $"{type} IOPS are {min:N0} to {max:N0}";
            // gp3's 3,000 IOPS baseline comes with any size; only IOPS above it need 500 per GiB
            int perGb = type switch { "gp3" => 500, "io1" => 50, _ => 1000 };
            bool limited = type != "gp3" || iops > 3000;
            if (limited && iops > sizeGb * perGb) return $"{type} allows at most {perGb} IOPS per GiB: {Math.Max(sizeGb * perGb, type == "gp3" ? 3000 : 0):N0} for {sizeGb} GiB";
        }
        if (HasThroughput(type))
        {
            var (min, max) = ThroughputRange(type);
            if (throughput < min || throughput > max) return $"gp3 throughput is {min} to {max} MB/s";
            if (throughput > iops / 4) return $"gp3 throughput can be at most IOPS ÷ 4: {iops / 4} MB/s at {iops:N0} IOPS";
        }
        return null;
    }
}

/// <summary>EBS volumes and snapshots across the scanned regions, and the actions on them.</summary>
internal static class VolumeAdmin
{
    private static List<T> L<T>(List<T> list) => list ?? new List<T>();
    private static int N(object v) => v == null ? 0 : Convert.ToInt32(v);
    private static bool B(object v) => v is bool b && b;
    private static AmazonEC2Client Ec2(string profile, string region) => new(Aws.Credentials(profile), Aws.Region(region));

    private static string NameOf(List<Tag> tags) => L(tags).FirstOrDefault(t => t.Key == "Name")?.Value ?? "";
    private static List<KeyValuePair<string, string>> OtherTags(List<Tag> tags) =>
        L(tags).Where(t => t.Key != "Name" && !t.Key.StartsWith("aws:", StringComparison.OrdinalIgnoreCase))
               .Select(t => new KeyValuePair<string, string>(t.Key, t.Value ?? "")).ToList();

    // ------------------------------------------------------------------ costs (estimates)

    /// <summary>
    /// A month of a volume, in USD: storage at the region's list price (AWS's price list, cached), plus gp3 IOPS and
    /// throughput above the free baseline and io1/io2 provisioned IOPS (us-east-1 rates scaled by the region's gp3 price).
    /// </summary>
    public static double MonthlyCost(Dictionary<string, double> prices, string type, int sizeGb, int iops, int throughput)
    {
        if (prices == null || !prices.TryGetValue(type, out double perGb)) return 0;
        double scale = prices.TryGetValue("gp3", out double gp3) && gp3 > 0 ? gp3 / 0.08 : 1;
        double cost = sizeGb * perGb;
        if (type == "gp3") cost += Math.Max(0, iops - 3000) * 0.005 * scale + Math.Max(0, throughput - 125) * 0.04 * scale;
        if (type is "io1" or "io2") cost += iops * 0.065 * scale;
        return cost;
    }

    /// <summary>Snapshot storage, per GB-month (us-east-1 $0.05, scaled by the region's gp3 price).</summary>
    public static double SnapshotPricePerGb(Dictionary<string, double> prices) =>
        0.05 * (prices != null && prices.TryGetValue("gp3", out double gp3) && gp3 > 0 ? gp3 / 0.08 : 1);

    public static async Task<Dictionary<string, double>> PricesAsync(string profile, string region, CancellationToken ct)
    {
        try { return await ServerLaunch.DiskPricesAsync(profile, region, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Write($"Volumes: no prices for {region}: {ex.Message}");
            return new Dictionary<string, double>();
        }
    }

    // ------------------------------------------------------------------ reading

    public static async Task<List<VolumeRow>> VolumesAsync(string profile, IEnumerable<string> regions, CancellationToken ct)
    {
        var rows = new List<VolumeRow>();
        foreach (string region in regions.Distinct())
        {
            using var ec2 = Ec2(profile, region);
            var volumes = new List<Volume>();
            string token = null;
            do
            {
                var page = await ec2.DescribeVolumesAsync(new DescribeVolumesRequest { NextToken = token, MaxResults = 500 }, ct);
                volumes.AddRange(L(page.Volumes));
                token = page.NextToken;
            } while (!string.IsNullOrEmpty(token));

            var names = await InstanceNamesAsync(ec2, volumes.SelectMany(v => L(v.Attachments)).Select(a => a.InstanceId), ct);
            var prices = await PricesAsync(profile, region, ct);
            foreach (var v in volumes)
            {
                var attachment = L(v.Attachments).FirstOrDefault();
                var row = new VolumeRow
                {
                    Id = v.VolumeId, Name = NameOf(v.Tags), Region = region, Zone = v.AvailabilityZone ?? "",
                    Type = v.VolumeType?.Value ?? "", State = v.State?.Value ?? "", KmsKeyId = v.KmsKeyId ?? "", SnapshotId = v.SnapshotId ?? "",
                    SizeGb = N(v.Size), Iops = N(v.Iops), Throughput = N(v.Throughput), Encrypted = B(v.Encrypted),
                    Created = v.CreateTime is DateTime t ? t.ToLocalTime() : DateTime.MinValue, Tags = OtherTags(v.Tags),
                };
                if (attachment != null)
                {
                    row.InstanceId = attachment.InstanceId ?? "";
                    row.InstanceName = names.TryGetValue(row.InstanceId, out var n) ? n : "";
                    row.Device = attachment.Device ?? "";
                    row.DeleteOnTermination = B(attachment.DeleteOnTermination);
                }
                row.MonthlyCost = MonthlyCost(prices, row.Type, row.SizeGb, row.Iops, row.Throughput);
                rows.Add(row);
            }
        }
        return rows;
    }

    /// <summary>Server names (Name tags) by instance ID.</summary>
    private static async Task<Dictionary<string, string>> InstanceNamesAsync(AmazonEC2Client ec2, IEnumerable<string> ids, CancellationToken ct)
    {
        var names = new Dictionary<string, string>();
        var list = ids.Where(i => !string.IsNullOrEmpty(i)).Distinct().ToList();
        for (int i = 0; i < list.Count; i += 100)
        {
            var page = await ec2.DescribeInstancesAsync(new DescribeInstancesRequest
            {
                Filters = new List<Filter> { new("instance-id", list.Skip(i).Take(100).ToList()) },
            }, ct);
            foreach (var instance in L(page.Reservations).SelectMany(r => L(r.Instances)))
                names[instance.InstanceId] = NameOf(instance.Tags);
        }
        return names;
    }

    /// <summary>The account's snapshots, with the AMIs that need each one, and whether its source volume still exists.</summary>
    public static async Task<List<SnapshotRow>> SnapshotsAsync(string profile, IEnumerable<string> regions,
        IReadOnlyCollection<VolumeRow> volumes, CancellationToken ct)
    {
        var rows = new List<SnapshotRow>();
        var byVolume = volumes.ToDictionary(v => v.Id, v => v);
        foreach (string region in regions.Distinct())
        {
            using var ec2 = Ec2(profile, region);
            var snapshots = new List<Snapshot>();
            string token = null;
            do
            {
                var page = await ec2.DescribeSnapshotsAsync(new DescribeSnapshotsRequest
                {
                    OwnerIds = new List<string> { "self" }, NextToken = token, MaxResults = 1000,
                }, ct);
                snapshots.AddRange(L(page.Snapshots));
                token = page.NextToken;
            } while (!string.IsNullOrEmpty(token));

            // AMIs and the snapshots behind them (those can't be deleted while the AMI exists)
            var usedBy = new Dictionary<string, List<string>>();
            var images = await ec2.DescribeImagesAsync(new DescribeImagesRequest { Owners = new List<string> { "self" } }, ct);
            foreach (Amazon.EC2.Model.Image image in L(images.Images))
                foreach (var mapping in L(image.BlockDeviceMappings))
                    if (mapping.Ebs?.SnapshotId is { Length: > 0 } sid)
                    {
                        if (!usedBy.TryGetValue(sid, out var list)) usedBy[sid] = list = new List<string>();
                        list.Add($"{image.Name} ({image.ImageId})");
                    }

            var prices = await PricesAsync(profile, region, ct);
            double perGb = SnapshotPricePerGb(prices);
            foreach (var s in snapshots)
            {
                var row = new SnapshotRow
                {
                    Id = s.SnapshotId, Name = NameOf(s.Tags), Region = region, VolumeId = s.VolumeId ?? "",
                    Description = s.Description ?? "", State = s.State?.Value ?? "", Progress = s.Progress ?? "", KmsKeyId = s.KmsKeyId ?? "",
                    SizeGb = N(s.VolumeSize), Encrypted = B(s.Encrypted), Tags = OtherTags(s.Tags),
                    Started = s.StartTime is DateTime t ? t.ToLocalTime() : DateTime.MinValue,
                    UsedBy = usedBy.TryGetValue(s.SnapshotId, out var u) ? u : new List<string>(),
                };
                // AWS fills a placeholder volume ID (vol-ffffffff) for snapshots copied or made from AMIs
                if (byVolume.TryGetValue(row.VolumeId, out var source))
                {
                    row.SourceExists = true;
                    row.SourceName = source.Name.Length > 0 ? source.Name : source.InstanceName;
                }
                row.MonthlyCostMax = row.SizeGb * perGb;
                rows.Add(row);
            }
        }
        return rows;
    }

    public static async Task<List<string>> ZonesAsync(string profile, string region, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        var zones = await ec2.DescribeAvailabilityZonesAsync(new DescribeAvailabilityZonesRequest(), ct);
        return L(zones.AvailabilityZones).Where(z => z.State?.Value == "available").Select(z => z.ZoneName).OrderBy(z => z).ToList();
    }

    /// <summary>Servers in a zone (running or stopped) that a volume could be attached to, with the devices they use.</summary>
    public static async Task<List<InstanceChoice>> InstancesInZoneAsync(string profile, string region, string zone, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        var result = new List<InstanceChoice>();
        string token = null;
        do
        {
            var page = await ec2.DescribeInstancesAsync(new DescribeInstancesRequest
            {
                NextToken = token,
                Filters = new List<Filter>
                {
                    new("availability-zone", new List<string> { zone }),
                    new("instance-state-name", new List<string> { "running", "stopped" }),
                },
            }, ct);
            foreach (var i in L(page.Reservations).SelectMany(r => L(r.Instances)))
            {
                var choice = new InstanceChoice
                {
                    Id = i.InstanceId, Name = NameOf(i.Tags), Zone = zone, State = i.State?.Name?.Value ?? "",
                    Windows = string.Equals(i.Platform?.Value, "windows", StringComparison.OrdinalIgnoreCase) ||
                              (i.PlatformDetails ?? "").Contains("Windows", StringComparison.OrdinalIgnoreCase),
                };
                foreach (var m in L(i.BlockDeviceMappings)) choice.Devices.Add(m.DeviceName ?? "");
                result.Add(choice);
            }
            token = page.NextToken;
        } while (!string.IsNullOrEmpty(token));
        return result.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Customer-managed KMS keys usable for EBS (enabled, symmetric, encrypt/decrypt) plus AWS managed, and the account's
    /// EBS default key for the region (marked). CouldList is false when the role can't list keys: a key ARN or alias can
    /// still be typed.
    /// </summary>
    public static async Task<(List<KeyChoice> Keys, bool CouldList)> KeysAsync(string profile, string region, CancellationToken ct)
    {
        var keys = new List<KeyChoice> { new() { KeyId = "", Label = "AWS managed (aws/ebs)", Alias = "alias/aws/ebs", AwsManaged = true } };
        string accountDefault = "";
        try
        {
            using var ec2 = Ec2(profile, region);
            accountDefault = (await ec2.GetEbsDefaultKmsKeyIdAsync(new GetEbsDefaultKmsKeyIdRequest(), ct)).KmsKeyId ?? "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Write("Volumes: no EBS default key: " + ex.Message); }

        bool listed = true;
        try
        {
            using var kms = new AmazonKeyManagementServiceClient(Aws.Credentials(profile), Aws.Region(region));
            var aliases = new List<Kms.AliasListEntry>();
            string marker = null;
            do
            {
                var page = await kms.ListAliasesAsync(new Kms.ListAliasesRequest { Marker = marker, Limit = 100 }, ct);
                aliases.AddRange(L(page.Aliases));
                marker = B(page.Truncated) ? page.NextMarker : null;
            } while (!string.IsNullOrEmpty(marker));

            foreach (var alias in aliases.Where(a => !string.IsNullOrEmpty(a.TargetKeyId) &&
                                                     !(a.AliasName ?? "").StartsWith("alias/aws/", StringComparison.OrdinalIgnoreCase))
                                         .OrderBy(a => a.AliasName, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var key = (await kms.DescribeKeyAsync(new Kms.DescribeKeyRequest { KeyId = alias.TargetKeyId }, ct)).KeyMetadata;
                    bool usable = key.KeyManager == KeyManagerType.CUSTOMER && key.KeyState == KeyState.Enabled &&
                                  key.KeyUsage == KeyUsageType.ENCRYPT_DECRYPT && (key.KeySpec == null || key.KeySpec == KeySpec.SYMMETRIC_DEFAULT);
                    if (!usable) continue;
                    string name = alias.AliasName.StartsWith("alias/", StringComparison.OrdinalIgnoreCase) ? alias.AliasName[6..] : alias.AliasName;
                    keys.Add(new KeyChoice { KeyId = key.Arn, Alias = alias.AliasName, KeyGuid = key.KeyId, Label = $"{name}  ({key.KeyId[..8]}...)" });
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { Log.Write($"Volumes: key {alias.AliasName}: {ex.Message}"); }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            listed = false;
            Log.Write("Volumes: couldn't list KMS keys: " + ex.Message);
        }

        // the account's EBS default: a key ARN, an alias ARN, or alias/aws/ebs
        if (accountDefault.Length > 0)
        {
            var match = keys.FirstOrDefault(k => !k.AwsManaged && (accountDefault.Contains(k.KeyGuid, StringComparison.OrdinalIgnoreCase) ||
                                                                  accountDefault.EndsWith(k.Alias, StringComparison.OrdinalIgnoreCase)))
                        ?? (accountDefault.EndsWith("alias/aws/ebs", StringComparison.OrdinalIgnoreCase) ? keys[0] : null);
            if (match != null) match.IsAccountDefault = true;
            else keys.Add(new KeyChoice { KeyId = accountDefault, Label = accountDefault, IsAccountDefault = true });
        }
        return (keys, listed);
    }

    // ------------------------------------------------------------------ changes

    /// <summary>The next free device name for a server (xvdf..xvdp on Windows, /dev/sdf..p on Linux); taken ones are added to used.</summary>
    public static string NextDevice(bool windows, HashSet<string> used)
    {
        for (char c = 'f'; c <= 'p'; c++)
        {
            string name = windows ? $"xvd{c}" : $"/dev/sd{c}";
            string alt = windows ? $"/dev/sd{c}" : $"/dev/xvd{c}";
            if (used.Contains(name) || used.Contains(alt) || used.Contains("/dev/" + name) || used.Contains(name.Replace("/dev/", ""))) continue;
            used.Add(name);
            return name;
        }
        throw new InvalidOperationException("no free device name is left (xvdf to xvdp are all in use)");
    }

    /// <summary>Create a volume (blank or from a snapshot) and wait until it's available. Returns its ID.</summary>
    public static async Task<string> CreateAsync(string profile, string region, string zone, NewDisk disk, string name, bool encrypt,
        string kmsKeyId, List<KeyValuePair<string, string>> tags, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        var allTags = new List<Tag>();
        if (!string.IsNullOrWhiteSpace(name)) allTags.Add(new Tag("Name", name.Trim()));
        foreach (var t in tags ?? new())
            if (t.Key.Length > 0 && t.Key != "Name" && !t.Key.StartsWith("aws:", StringComparison.OrdinalIgnoreCase) && allTags.All(x => x.Key != t.Key))
                allTags.Add(new Tag(t.Key, t.Value ?? ""));
        var request = new CreateVolumeRequest
        {
            AvailabilityZone = zone, Size = disk.SizeGb, VolumeType = VolumeType.FindValue(disk.Type),
        };
        if (allTags.Count > 0)
            request.TagSpecifications = new List<TagSpecification> { new() { ResourceType = Amazon.EC2.ResourceType.Volume, Tags = allTags } };
        if (!string.IsNullOrEmpty(disk.SnapshotId)) request.SnapshotId = disk.SnapshotId;
        if (VolumeRules.HasIops(disk.Type)) request.Iops = disk.Iops;
        if (VolumeRules.HasThroughput(disk.Type)) request.Throughput = disk.Throughput;
        if (encrypt)
        {
            request.Encrypted = true;
            if (!string.IsNullOrWhiteSpace(kmsKeyId)) request.KmsKeyId = kmsKeyId.Trim();
        }
        string id = (await ec2.CreateVolumeAsync(request, ct)).Volume.VolumeId;
        Log.Write($"Volumes: created {id} ({disk.SizeGb} GiB {disk.Type}) in {zone}" + (encrypt ? ", encrypted" : ""));

        var until = DateTime.UtcNow + TimeSpan.FromMinutes(10);
        while (true)
        {
            var v = L((await ec2.DescribeVolumesAsync(new DescribeVolumesRequest { VolumeIds = new List<string> { id } }, ct)).Volumes).FirstOrDefault();
            string state = v?.State?.Value ?? "";
            if (state == "available") return id;
            if (state == "error") throw new InvalidOperationException($"AWS couldn't create {id} (it's in the error state)" +
                                                                      (encrypt ? ": check that your role may use the encryption key" : ""));
            if (DateTime.UtcNow > until) throw new TimeoutException($"{id} was created but isn't available after 10 minutes");
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
    }

    public static async Task AttachAsync(string profile, string region, string volumeId, string instanceId, string device, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        await ec2.AttachVolumeAsync(new AttachVolumeRequest { VolumeId = volumeId, InstanceId = instanceId, Device = device }, ct);
        Log.Write($"Volumes: {volumeId} attached to {instanceId} as {device}");
    }

    public static async Task DetachAsync(string profile, string region, string volumeId, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        await ec2.DetachVolumeAsync(new DetachVolumeRequest { VolumeId = volumeId }, ct);
        Log.Write($"Volumes: {volumeId} detached");
    }

    public static async Task ModifyAsync(string profile, string region, string volumeId, int sizeGb, string type, int iops, int throughput, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        var request = new ModifyVolumeRequest { VolumeId = volumeId, Size = sizeGb, VolumeType = VolumeType.FindValue(type) };
        if (VolumeRules.HasIops(type)) request.Iops = iops;
        if (VolumeRules.HasThroughput(type)) request.Throughput = throughput;
        await ec2.ModifyVolumeAsync(request, ct);
        Log.Write($"Volumes: {volumeId} modified to {sizeGb} GiB {type}" + (VolumeRules.HasIops(type) ? $", {iops} IOPS" : "") +
                  (VolumeRules.HasThroughput(type) ? $", {throughput} MB/s" : ""));
    }

    public static async Task DeleteVolumeAsync(string profile, string region, string volumeId, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        await ec2.DeleteVolumeAsync(new DeleteVolumeRequest { VolumeId = volumeId }, ct);
        Log.Write($"Volumes: {volumeId} deleted");
    }

    /// <summary>Snapshot a volume (named after it, with the date). Waits for it to complete only if asked.</summary>
    public static async Task<string> SnapshotAsync(string profile, string region, VolumeRow volume, string description, bool wait,
        IProgress<string> progress, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        string name = (volume.Name.Length > 0 ? volume.Name : volume.Id) + " " + DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        var request = new CreateSnapshotRequest
        {
            VolumeId = volume.Id, Description = description,
            TagSpecifications = new List<TagSpecification> { new() { ResourceType = Amazon.EC2.ResourceType.Snapshot, Tags = new List<Tag> { new("Name", name) } } },
        };
        string id = (await ec2.CreateSnapshotAsync(request, ct)).Snapshot.SnapshotId;   // SDK v4: the snapshot is on .Snapshot
        Log.Write($"Volumes: snapshot {id} of {volume.Id} started");
        while (wait)
        {
            var s = L((await ec2.DescribeSnapshotsAsync(new DescribeSnapshotsRequest { SnapshotIds = new List<string> { id } }, ct)).Snapshots).FirstOrDefault();
            string state = s?.State?.Value ?? "";
            if (state == "completed") break;
            if (state == "error") throw new InvalidOperationException($"the snapshot {id} failed");
            progress?.Report($"Snapshot of {volume.Label}: {s?.Progress ?? "starting"} ...");
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        return id;
    }

    public static async Task DeleteSnapshotAsync(string profile, string region, string snapshotId, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        await ec2.DeleteSnapshotAsync(new DeleteSnapshotRequest { SnapshotId = snapshotId }, ct);
        Log.Write($"Volumes: snapshot {snapshotId} deleted");
    }

    /// <summary>Copy a snapshot to another region (encrypted copies use the destination's default key). Returns the new ID.</summary>
    public static async Task<string> CopySnapshotAsync(string profile, SnapshotRow snapshot, string toRegion, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, toRegion);
        var request = new CopySnapshotRequest
        {
            SourceRegion = snapshot.Region, SourceSnapshotId = snapshot.Id,
            Description = $"Copy of {snapshot.Id} from {snapshot.Region}" + (snapshot.Description.Length > 0 ? $": {snapshot.Description}" : ""),
            TagSpecifications = new List<TagSpecification>
            {
                new() { ResourceType = Amazon.EC2.ResourceType.Snapshot, Tags = new List<Tag> { new("Name", snapshot.Name.Length > 0 ? snapshot.Name : snapshot.Id) } },
            },
        };
        if (snapshot.Encrypted) request.Encrypted = true;
        string id = (await ec2.CopySnapshotAsync(request, ct)).SnapshotId;
        Log.Write($"Volumes: snapshot {snapshot.Id} ({snapshot.Region}) copying to {toRegion} as {id}");
        return id;
    }

    /// <summary>Set a resource's Name and tags: tags not in the new list are removed (aws: tags are left alone).</summary>
    public static async Task SetTagsAsync(string profile, string region, string resourceId, string name,
        List<KeyValuePair<string, string>> tags, List<KeyValuePair<string, string>> before, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        var set = new List<Tag> { new("Name", name ?? "") };
        set.AddRange(tags.Where(t => t.Key.Length > 0 && t.Key != "Name" && !t.Key.StartsWith("aws:", StringComparison.OrdinalIgnoreCase))
                         .Select(t => new Tag(t.Key, t.Value ?? "")));
        await ec2.CreateTagsAsync(new CreateTagsRequest { Resources = new List<string> { resourceId }, Tags = set }, ct);
        var removed = before.Where(b => tags.All(t => t.Key != b.Key)).Select(b => new Tag(b.Key)).ToList();
        if (removed.Count > 0) await ec2.DeleteTagsAsync(new DeleteTagsRequest { Resources = new List<string> { resourceId }, Tags = removed }, ct);
        Log.Write($"Volumes: tags of {resourceId} saved");
    }

    /// <summary>Key=Value lines (blank lines and lines without = are ignored).</summary>
    public static List<KeyValuePair<string, string>> ParseTags(string text)
    {
        var tags = new List<KeyValuePair<string, string>>();
        foreach (var line in (text ?? "").Replace("\r", "").Split('\n'))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim(), value = line[(eq + 1)..].Trim();
            if (key.Length > 0 && tags.All(t => t.Key != key)) tags.Add(new(key, value));
        }
        return tags;
    }

    public static string FormatTags(IEnumerable<KeyValuePair<string, string>> tags) =>
        string.Join("\r\n", tags.Select(t => $"{t.Key}={t.Value}"));
}
