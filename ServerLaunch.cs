using Amazon.EC2;
using Amazon.EC2.Model;
// Only EC2's namespace is imported: Systems Manager's and IAM's have types with the same names (ResourceType ...),
// so their clients are written out in full and their models go through the Ssm / Iam aliases.
using Ssm = Amazon.SimpleSystemsManagement.Model;
using Iam = Amazon.IdentityManagement.Model;
// WinForms projects import System.Drawing everywhere, which also has an Image: this one is the AWS image (AMI)
using Ec2Image = Amazon.EC2.Model.Image;

namespace SwitchboardRM;

/// <summary>An image to launch from: AWS's current build of an OS, or one of the account's own AMIs.</summary>
internal sealed class ImageChoice
{
    public string Id = "", Name = "", Architecture = "", RootDevice = "", Platform = "";
    public bool Mine;
    public int RootSizeGb;
    public DateTime Created;
    public bool IsWindows => Platform.Contains("Windows", StringComparison.OrdinalIgnoreCase);
    /// <summary>The operating system name AWS's price list uses for this image.</summary>
    public string PriceOs => IsWindows ? "Windows" : "Linux";
    public override string ToString() => Mine
        ? $"My image: {Name}" + (Created > DateTime.MinValue ? $"  ({Created:yyyy-MM-dd})" : "")
        : $"AWS: {Name}";
}

internal sealed class TypeChoice
{
    public string Name = "";
    public int VCpus, MaxInterfaces = 1, MaxIpsPerInterface = 1;
    public double MemoryGiB;
    public double Hourly = double.NaN;   // on-demand list price (USD), NaN = not known
    public override string ToString() => $"{Name}   ({VCpus} vCPU, {MemoryGiB:0.##} GiB)" +
        (double.IsNaN(Hourly) ? "" : $"   ${Hourly:0.0###}/h, ~${Hourly * 730:#,0}/month");
}

internal sealed class NamedChoice
{
    public string Id = "", Name = "", Detail = "", VpcId = "", Cidr = "", Zone = "";
    public bool Flag;      // instance profile: has Session Manager permissions; VPC: the default one
    public bool HasIpv6;   // subnet: has an IPv6 range
    public override string ToString() =>
        (Name.Length > 0 && Name != Id ? $"{Name}  ({Id})" : Id) + (Detail.Length > 0 ? "   " + Detail : "");
}

internal sealed class DiskSpec
{
    public int SizeGb;
    public string Type = "gp3";
}

/// <summary>One network interface: index 0 is the primary.</summary>
internal sealed class InterfaceSpec
{
    public string SubnetId = "", PrivateIp = "", Description = "";
    public List<string> SecurityGroupIds = new(), SecondaryIps = new();
    public int SecondaryCount, Ipv6Count;
    public bool PublicIp, SourceDestCheck = true;
}

/// <summary>What to launch (also what "New server like this one" reads from a server).</summary>
internal sealed class LaunchSpec
{
    public string Region = "", ImageId = "", RootDevice = "", InstanceType = "", Name = "";
    public List<KeyValuePair<string, string>> Tags = new();
    public DiskSpec RootDisk = new();
    public List<DiskSpec> DataDisks = new();
    public bool Encrypted = true;
    public List<InterfaceSpec> Interfaces = new();
    public string VpcId = "", Tenancy = "default", PlacementGroup = "", HostnameType = "ip-name";
    public bool DnsA, DnsAaaa;
    public string InstanceProfile = "", KeyName = "", ShutdownBehavior = "stop", UserData = "";
    public bool TerminationProtection, StopProtection, DetailedMonitoring, RequireTokens = true;
    public int HopLimit = 2;
    public bool IsWindows;
}

/// <summary>AWS calls for the New server wizard. All are read-only except LaunchAsync.</summary>
internal static class ServerLaunch
{
    // AWS's public parameters for the current image of each OS: short, always current, per region
    private const string WindowsPath = "/aws/service/ami-windows-latest";
    private const string AmazonLinuxPath = "/aws/service/ami-amazon-linux-latest";
    private static readonly string[] UbuntuParameters =
    {
        "/aws/service/canonical/ubuntu/server/24.04/stable/current/amd64/hvm/ebs-gp3/ami-id",
        "/aws/service/canonical/ubuntu/server/24.04/stable/current/arm64/hvm/ebs-gp3/ami-id",
        "/aws/service/canonical/ubuntu/server/22.04/stable/current/amd64/hvm/ebs-gp2/ami-id",
    };

    // the newer AWS SDK gives null for empty lists and some numbers: read them safely
    private static List<T> L<T>(List<T> list) => list ?? new List<T>();
    private static int N(object v) => v == null ? 0 : Convert.ToInt32(v);
    private static long N64(object v) => v == null ? 0 : Convert.ToInt64(v);
    private static bool B(object v) => v is bool b && b;
    private static string Tag(List<Tag> tags, string key) => L(tags).FirstOrDefault(t => t.Key == key)?.Value ?? "";

    private static AmazonEC2Client Ec2(string profile, string region) => new(Aws.Credentials(profile), Aws.Region(region));

    /// <summary>The curated current AWS images (Windows Server, Amazon Linux, Ubuntu) and the account's own AMIs.</summary>
    public static async Task<List<ImageChoice>> ImagesAsync(string profile, string region, CancellationToken ct)
    {
        var creds = Aws.Credentials(profile);
        using var ssm = new Amazon.SimpleSystemsManagement.AmazonSimpleSystemsManagementClient(creds, Aws.Region(region));
        using var ec2 = new AmazonEC2Client(creds, Aws.Region(region));

        // name -> AMI ID from the public parameters
        var picks = new Dictionary<string, string>();
        foreach (var path in new[] { WindowsPath, AmazonLinuxPath })
        {
            string token = null;
            do
            {
                var page = await ssm.GetParametersByPathAsync(new Ssm.GetParametersByPathRequest { Path = path, NextToken = token, MaxResults = 10 }, ct);
                foreach (var p in L(page.Parameters))
                {
                    string name = p.Name[(p.Name.LastIndexOf('/') + 1)..];
                    if (WantedAwsImage(path, name)) picks[Friendly(path, name)] = p.Value;
                }
                token = page.NextToken;
            } while (!string.IsNullOrEmpty(token));
        }
        try
        {
            var ubuntu = await ssm.GetParametersAsync(new Ssm.GetParametersRequest { Names = UbuntuParameters.ToList() }, ct);
            foreach (var p in L(ubuntu.Parameters))
            {
                // "/aws/service/canonical/ubuntu/server/24.04/stable/current/amd64/..." split on '/': [6] version, [9] architecture
                var parts = p.Name.Split('/');
                if (parts.Length > 9) picks[$"Ubuntu {parts[6]} ({(parts[9] == "arm64" ? "ARM" : "x86")})"] = p.Value;
            }
        }
        catch (Exception ex) { Log.Write("New server: Ubuntu images unavailable: " + ex.Message); }

        var result = new List<ImageChoice>();
        if (picks.Count > 0)
        {
            var byId = picks.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);
            foreach (var chunk in byId.Keys.Chunk(100))
            {
                var images = await ec2.DescribeImagesAsync(new DescribeImagesRequest { ImageIds = chunk.ToList() }, ct);
                foreach (var img in L(images.Images)) result.Add(ToChoice(img, byId[img.ImageId], mine: false));
            }
        }
        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        // the account's own images, newest first (e.g. a new terminal server build)
        var own = await ec2.DescribeImagesAsync(new DescribeImagesRequest { Owners = new List<string> { "self" } }, ct);
        result.AddRange(L(own.Images).Select(img => ToChoice(img, string.IsNullOrEmpty(img.Name) ? img.ImageId : img.Name, mine: true))
                                     .OrderByDescending(c => c.Created).Take(100));
        return result;
    }

    private static bool WantedAwsImage(string path, string name)
    {
        if (path == WindowsPath)
            return name.StartsWith("Windows_Server-", StringComparison.Ordinal) && name.Contains("-English-") &&
                   (name.EndsWith("-Full-Base") || name.EndsWith("-Core-Base")) && !name.Contains("SQL") && !name.Contains("ECS");
        // Amazon Linux 2023 (x86 and ARM) and Amazon Linux 2
        return name is "al2023-ami-kernel-default-x86_64" or "al2023-ami-kernel-default-arm64" or "amzn2-ami-hvm-x86_64-gp2";
    }

    private static string Friendly(string path, string name)
    {
        if (path == WindowsPath)
        {
            // Windows_Server-2022-English-Full-Base -> Windows Server 2022 (Desktop Experience)
            var p = name.Split('-');
            string version = p.Length > 1 ? p[1] : "";
            if (p.Length > 2 && p[2] == "R2") version += " R2";
            return $"Windows Server {version} ({(name.EndsWith("-Core-Base") ? "Core" : "Desktop Experience")})";
        }
        return name switch
        {
            "al2023-ami-kernel-default-x86_64" => "Amazon Linux 2023 (x86)",
            "al2023-ami-kernel-default-arm64" => "Amazon Linux 2023 (ARM)",
            "amzn2-ami-hvm-x86_64-gp2" => "Amazon Linux 2 (x86)",
            _ => name,
        };
    }

    private static ImageChoice ToChoice(Ec2Image img, string name, bool mine)
    {
        var root = L(img.BlockDeviceMappings).FirstOrDefault(b => b.DeviceName == img.RootDeviceName);
        DateTime.TryParse(Convert.ToString(img.CreationDate, System.Globalization.CultureInfo.InvariantCulture), out var created);
        return new ImageChoice
        {
            Id = img.ImageId, Name = name, Mine = mine, Created = created,
            Architecture = img.Architecture?.Value ?? "x86_64",
            Platform = img.PlatformDetails ?? "",
            RootDevice = img.RootDeviceName ?? "",
            RootSizeGb = N(root?.Ebs?.VolumeSize),
        };
    }

    /// <summary>One image by ID (a server's original image when cloning), or null if it's gone or not visible.</summary>
    public static async Task<ImageChoice> ImageAsync(string profile, string region, string imageId, CancellationToken ct)
    {
        try
        {
            using var ec2 = Ec2(profile, region);
            var img = L((await ec2.DescribeImagesAsync(new DescribeImagesRequest { ImageIds = new List<string> { imageId } }, ct)).Images).FirstOrDefault();
            return img == null ? null : ToChoice(img, string.IsNullOrEmpty(img.Name) ? img.ImageId : img.Name, mine: false);
        }
        catch { return null; }
    }

    /// <summary>Current-generation instance types offered in the region that run this architecture, smallest first per family.</summary>
    public static async Task<List<TypeChoice>> InstanceTypesAsync(string profile, string region, string architecture, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        var result = new List<TypeChoice>();
        string token = null;
        do
        {
            var page = await ec2.DescribeInstanceTypesAsync(new DescribeInstanceTypesRequest
            {
                Filters = new List<Filter>
                {
                    new("processor-info.supported-architecture", new List<string> { architecture }),
                    new("current-generation", new List<string> { "true" }),
                },
                MaxResults = 100,
                NextToken = token,
            }, ct);
            foreach (var t in L(page.InstanceTypes))
                result.Add(new TypeChoice
                {
                    Name = t.InstanceType?.Value ?? "",
                    VCpus = N(t.VCpuInfo?.DefaultVCpus),
                    MemoryGiB = N64(t.MemoryInfo?.SizeInMiB) / 1024.0,
                    MaxInterfaces = Math.Max(1, N(t.NetworkInfo?.MaximumNetworkInterfaces)),
                    MaxIpsPerInterface = Math.Max(1, N(t.NetworkInfo?.Ipv4AddressesPerInterface)),
                });
            token = page.NextToken;
        } while (!string.IsNullOrEmpty(token));
        return result.Where(t => t.Name.Length > 0)
                     .OrderBy(t => t.Name.Split('.')[0], StringComparer.Ordinal).ThenBy(t => t.VCpus).ThenBy(t => t.MemoryGiB).ToList();
    }

    /// <summary>VPCs, subnets and security groups in the region.</summary>
    public static async Task<(List<NamedChoice> Vpcs, List<NamedChoice> Subnets, List<NamedChoice> Groups)> NetworkAsync(
        string profile, string region, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        var vpcs = L((await ec2.DescribeVpcsAsync(new DescribeVpcsRequest(), ct)).Vpcs)
            .Select(v => new NamedChoice { Id = v.VpcId, Name = Tag(v.Tags, "Name"), Detail = v.CidrBlock + (B(v.IsDefault) ? "  default" : ""), VpcId = v.VpcId, Cidr = v.CidrBlock ?? "", Flag = B(v.IsDefault) })
            .OrderBy(v => v.Name.Length == 0).ThenBy(v => v.Name).ToList();

        var subnets = new List<NamedChoice>();
        string token = null;
        do
        {
            var page = await ec2.DescribeSubnetsAsync(new DescribeSubnetsRequest { NextToken = token }, ct);
            subnets.AddRange(L(page.Subnets).Select(s => new NamedChoice
            {
                Id = s.SubnetId, Name = Tag(s.Tags, "Name"), VpcId = s.VpcId, Cidr = s.CidrBlock ?? "", Zone = s.AvailabilityZone ?? "",
                HasIpv6 = L(s.Ipv6CidrBlockAssociationSet).Any(a => a.Ipv6CidrBlockState?.State?.Value == "associated"),
                Detail = $"{s.AvailabilityZone}, {s.CidrBlock}, {N(s.AvailableIpAddressCount)} free",
            }));
            token = page.NextToken;
        } while (!string.IsNullOrEmpty(token));

        var groups = new List<NamedChoice>();
        token = null;
        do
        {
            var page = await ec2.DescribeSecurityGroupsAsync(new DescribeSecurityGroupsRequest { NextToken = token }, ct);
            groups.AddRange(L(page.SecurityGroups).Select(g => new NamedChoice
            {
                Id = g.GroupId, Name = g.GroupName, VpcId = g.VpcId, Detail = g.Description ?? "",
            }));
            token = page.NextToken;
        } while (!string.IsNullOrEmpty(token));

        return (vpcs, subnets.OrderBy(s => s.Name.Length == 0).ThenBy(s => s.Name).ThenBy(s => s.Detail).ToList(),
                groups.OrderBy(g => g.Name).ToList());
    }

    /// <summary>Key pairs in the region.</summary>
    public static async Task<List<string>> KeyPairsAsync(string profile, string region, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        return L((await ec2.DescribeKeyPairsAsync(new DescribeKeyPairsRequest(), ct)).KeyPairs)
            .Select(k => k.KeyName).Where(k => !string.IsNullOrEmpty(k)).OrderBy(k => k).ToList();
    }

    /// <summary>
    /// Instance profiles (IAM is global). Flag = its role has AWS's AmazonSSMManagedInstanceCore policy attached, which
    /// Session Manager (and so Switchboard) needs; profiles granting it another way show without the flag.
    /// </summary>
    public static async Task<List<NamedChoice>> InstanceProfilesAsync(string profile, CancellationToken ct)
    {
        using var iam = new Amazon.IdentityManagement.AmazonIdentityManagementServiceClient(Aws.Credentials(profile), Amazon.RegionEndpoint.USEast1);
        var result = new List<NamedChoice>();
        string marker = null;
        do
        {
            var page = await iam.ListInstanceProfilesAsync(new Iam.ListInstanceProfilesRequest { Marker = marker }, ct);
            foreach (var ip in L(page.InstanceProfiles))
            {
                var role = L(ip.Roles).FirstOrDefault()?.RoleName ?? "";
                bool ssm = false;
                if (role.Length > 0)
                {
                    try
                    {
                        var attached = await iam.ListAttachedRolePoliciesAsync(new Iam.ListAttachedRolePoliciesRequest { RoleName = role }, ct);
                        ssm = L(attached.AttachedPolicies).Any(p => p.PolicyName == "AmazonSSMManagedInstanceCore");
                    }
                    catch { /* can't tell: shown without the mark */ }
                }
                result.Add(new NamedChoice
                {
                    Id = ip.InstanceProfileName, Name = ip.InstanceProfileName, Flag = ssm,
                    Detail = ssm ? "Session Manager: yes" : "",
                });
            }
            marker = B(page.IsTruncated) ? page.Marker : null;
        } while (!string.IsNullOrEmpty(marker));
        return result.OrderByDescending(p => p.Flag).ThenBy(p => p.Name).ToList();
    }

    /// <summary>Placement groups in the region (by name).</summary>
    public static async Task<List<string>> PlacementGroupsAsync(string profile, string region, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        return L((await ec2.DescribePlacementGroupsAsync(new DescribePlacementGroupsRequest(), ct)).PlacementGroups)
            .Select(g => g.GroupName).Where(g => !string.IsNullOrEmpty(g)).OrderBy(g => g).ToList();
    }

    /// <summary>The private addresses in use in a subnet, each with what uses it: for picking a free one.</summary>
    public static async Task<List<string>> UsedAddressesAsync(string profile, string region, string subnetId, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        var lines = new List<(uint Sort, string Text)>();
        string token = null;
        do
        {
            var page = await ec2.DescribeNetworkInterfacesAsync(new DescribeNetworkInterfacesRequest
            {
                Filters = new List<Filter> { new("subnet-id", new List<string> { subnetId }) },
                NextToken = token,
            }, ct);
            foreach (var eni in L(page.NetworkInterfaces))
            {
                string owner = eni.Attachment?.InstanceId is { Length: > 0 } id ? id : (eni.Description ?? "");
                if (string.IsNullOrEmpty(owner)) owner = eni.InterfaceType?.Value ?? "network interface";
                foreach (var ip in L(eni.PrivateIpAddresses))
                    if (TryParseIpv4(ip.PrivateIpAddress, out uint n))
                        lines.Add((n, $"{ip.PrivateIpAddress,-16} {owner}{(B(ip.Primary) ? "" : "  (secondary)")}"));
            }
            token = page.NextToken;
        } while (!string.IsNullOrEmpty(token));
        return lines.OrderBy(l => l.Sort).Select(l => l.Text).ToList();
    }

    /// <summary>
    /// Null if the address can be given to a new server in this subnet; otherwise why not: outside the subnet's range,
    /// one of the five addresses AWS reserves in every subnet (the first four and the last), or already in use.
    /// </summary>
    public static async Task<string> PrivateIpProblemAsync(string profile, string region, string subnetCidr, string ip, CancellationToken ct)
    {
        if (!TryParseIpv4(ip, out uint address)) return $"{ip} isn't an IPv4 address.";
        if (!TryParseCidr(subnetCidr, out uint network, out int bits)) return null;   // can't tell: AWS will check
        uint mask = bits == 0 ? 0 : uint.MaxValue << (32 - bits);
        uint first = network & mask, last = first | ~mask;
        if ((address & mask) != first) return $"{ip} isn't in the subnet's range ({subnetCidr}).";
        if (address - first < 4 || address == last) return $"{ip} is one of the addresses AWS reserves in every subnet (the first four and the last).";

        using var ec2 = Ec2(profile, region);
        var used = await ec2.DescribeNetworkInterfacesAsync(new DescribeNetworkInterfacesRequest
        {
            Filters = new List<Filter> { new("addresses.private-ip-address", new List<string> { ip }) },
        }, ct);
        var taken = L(used.NetworkInterfaces).FirstOrDefault();
        return taken == null ? null : $"{ip} is already in use ({taken.Attachment?.InstanceId ?? taken.NetworkInterfaceId}).";
    }

    private static bool TryParseIpv4(string text, out uint value)
    {
        value = 0;
        if (!System.Net.IPAddress.TryParse(text ?? "", out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        value = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        return text.Count(c => c == '.') == 3;   // "10.1" parses too, as 10.0.0.1: not what anyone means
    }

    private static bool TryParseCidr(string cidr, out uint network, out int bits)
    {
        network = 0;
        bits = 0;
        var parts = (cidr ?? "").Split('/');
        return parts.Length == 2 && TryParseIpv4(parts[0], out network) && int.TryParse(parts[1], out bits) && bits is >= 0 and <= 32;
    }

    // ---------------------------------------------------------------- prices (AWS Pricing API, on-demand list prices, USD)

    private static readonly string PriceCacheDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.DataName, "prices");

    /// <summary>On-demand hourly price per instance type for an OS ("Windows" includes the licence) in a region.</summary>
    public static Task<Dictionary<string, double>> InstancePricesAsync(string profile, string region, string os, CancellationToken ct) =>
        CachedPricesAsync($"instances-{region}-{os}", profile, ct, new()
        {
            ["regionCode"] = region, ["operatingSystem"] = os, ["tenancy"] = "Shared", ["preInstalledSw"] = "NA",
            ["capacitystatus"] = "Used", ["licenseModel"] = "No License required", ["productFamily"] = "Compute Instance",
        }, "instanceType");

    /// <summary>Price per GB-month per EBS volume type (gp3, gp2, io1 ...) in a region (storage only, not IOPS or throughput).</summary>
    public static Task<Dictionary<string, double>> DiskPricesAsync(string profile, string region, CancellationToken ct) =>
        CachedPricesAsync($"disks-{region}", profile, ct, new()
        {
            ["regionCode"] = region, ["productFamily"] = "Storage",
        }, "volumeApiName");

    /// <summary>Prices from AWS's price list, keyed by one product attribute; kept on disk for three days (the list is large).</summary>
    private static async Task<Dictionary<string, double>> CachedPricesAsync(string cacheName, string profile, CancellationToken ct,
        Dictionary<string, string> filters, string keyAttribute)
    {
        string file = Path.Combine(PriceCacheDir, cacheName + ".json");
        try
        {
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(3))
                return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(await File.ReadAllTextAsync(file, ct)) ?? new();
        }
        catch { /* fetch it again */ }

        var prices = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        // the Pricing API only runs in a few regions; us-east-1 serves every region's prices
        using var pricing = new Amazon.Pricing.AmazonPricingClient(Aws.Credentials(profile), Amazon.RegionEndpoint.USEast1);
        string token = null;
        do
        {
            var page = await pricing.GetProductsAsync(new Amazon.Pricing.Model.GetProductsRequest
            {
                ServiceCode = "AmazonEC2",
                FormatVersion = "aws_v1",
                MaxResults = 100,
                NextToken = token,
                Filters = filters.Select(f => new Amazon.Pricing.Model.Filter
                {
                    Type = Amazon.Pricing.FilterType.TERM_MATCH, Field = f.Key, Value = f.Value,
                }).ToList(),
            }, ct);
            foreach (var item in L(page.PriceList))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(item);
                var root = doc.RootElement;
                if (!root.TryGetProperty("product", out var product) || !product.TryGetProperty("attributes", out var attrs) ||
                    !attrs.TryGetProperty(keyAttribute, out var keyEl)) continue;
                string key = keyEl.GetString() ?? "";
                double? price = OnDemandPrice(root);
                if (key.Length > 0 && price is > 0 && (!prices.TryGetValue(key, out var old) || price < old)) prices[key] = price.Value;
            }
            token = page.NextToken;
        } while (!string.IsNullOrEmpty(token));

        try
        {
            Directory.CreateDirectory(PriceCacheDir);
            await File.WriteAllTextAsync(file, System.Text.Json.JsonSerializer.Serialize(prices), ct);
        }
        catch { /* only a cache */ }
        return prices;
    }

    /// <summary>The USD on-demand price in a price-list item (terms.OnDemand.*.priceDimensions.*.pricePerUnit.USD).</summary>
    private static double? OnDemandPrice(System.Text.Json.JsonElement item)
    {
        if (!item.TryGetProperty("terms", out var terms) || !terms.TryGetProperty("OnDemand", out var onDemand)) return null;
        foreach (var term in onDemand.EnumerateObject())
        {
            if (!term.Value.TryGetProperty("priceDimensions", out var dims)) continue;
            foreach (var dim in dims.EnumerateObject())
                if (dim.Value.TryGetProperty("pricePerUnit", out var unit) && unit.TryGetProperty("USD", out var usd) &&
                    double.TryParse(usd.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                    return v;
        }
        return null;
    }

    // ---------------------------------------------------------------- clone and launch

    /// <summary>
    /// A server's settings for "New server like this one": image, type, disks, network interfaces (subnets, security
    /// groups, address counts, but never its addresses), placement, hostname, access, protections, monitoring, metadata.
    /// </summary>
    public static async Task<LaunchSpec> ReadForCloneAsync(string profile, string region, string instanceId, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        var res = await ec2.DescribeInstancesAsync(new DescribeInstancesRequest { InstanceIds = new List<string> { instanceId } }, ct);
        var i = L(res.Reservations).SelectMany(r => L(r.Instances)).FirstOrDefault()
                ?? throw new InvalidOperationException($"{instanceId} wasn't found in {region}.");

        var spec = new LaunchSpec
        {
            Region = region, ImageId = i.ImageId ?? "", InstanceType = i.InstanceType?.Value ?? "", RootDevice = i.RootDeviceName ?? "",
            VpcId = i.VpcId ?? "", KeyName = i.KeyName ?? "",
            Tenancy = i.Placement?.Tenancy?.Value ?? "default", PlacementGroup = i.Placement?.GroupName ?? "",
            HostnameType = i.PrivateDnsNameOptions?.HostnameType?.Value ?? "ip-name",
            DnsA = B(i.PrivateDnsNameOptions?.EnableResourceNameDnsARecord),
            DnsAaaa = B(i.PrivateDnsNameOptions?.EnableResourceNameDnsAAAARecord),
            DetailedMonitoring = i.Monitoring?.State?.Value == "enabled",
            RequireTokens = i.MetadataOptions?.HttpTokens?.Value != "optional",
            HopLimit = Math.Max(1, N(i.MetadataOptions?.HttpPutResponseHopLimit)),
            IsWindows = (i.PlatformDetails ?? "").Contains("Windows", StringComparison.OrdinalIgnoreCase),
            Tags = L(i.Tags).Where(t => t.Key != "Name" && !t.Key.StartsWith("aws:", StringComparison.Ordinal))
                            .Select(t => new KeyValuePair<string, string>(t.Key, t.Value)).ToList(),
        };
        string arn = i.IamInstanceProfile?.Arn ?? "";
        if (arn.Contains("instance-profile/")) spec.InstanceProfile = arn[(arn.LastIndexOf('/') + 1)..];

        foreach (var eni in L(i.NetworkInterfaces).OrderBy(n => N(n.Attachment?.DeviceIndex)))
            spec.Interfaces.Add(new InterfaceSpec
            {
                SubnetId = eni.SubnetId ?? "", Description = eni.Description ?? "",
                SecurityGroupIds = L(eni.Groups).Select(g => g.GroupId).ToList(),
                SecondaryCount = Math.Max(0, L(eni.PrivateIpAddresses).Count - 1),
                Ipv6Count = L(eni.Ipv6Addresses).Count,
                SourceDestCheck = eni.SourceDestCheck == null || B(eni.SourceDestCheck),
                PublicIp = !string.IsNullOrEmpty(eni.Association?.PublicIp) && N(eni.Attachment?.DeviceIndex) == 0,
            });

        // disks: the system disk first, then the rest in device order
        var mappings = L(i.BlockDeviceMappings).Where(b => !string.IsNullOrEmpty(b.Ebs?.VolumeId)).ToList();
        if (mappings.Count > 0)
        {
            try
            {
                var volumes = L((await ec2.DescribeVolumesAsync(new DescribeVolumesRequest { VolumeIds = mappings.Select(m => m.Ebs.VolumeId).ToList() }, ct)).Volumes)
                    .ToDictionary(v => v.VolumeId);
                foreach (var m in mappings.OrderBy(m => m.DeviceName != i.RootDeviceName).ThenBy(m => m.DeviceName))
                {
                    if (!volumes.TryGetValue(m.Ebs.VolumeId, out var v)) continue;
                    var disk = new DiskSpec { SizeGb = N(v.Size), Type = v.VolumeType?.Value ?? "gp3" };
                    if (m.DeviceName == i.RootDeviceName) { spec.RootDisk = disk; spec.Encrypted = B(v.Encrypted); }
                    else spec.DataDisks.Add(disk);
                }
            }
            catch { /* sizes then come from the image */ }
        }

        // protections (each its own call; a missing permission just leaves the default)
        async Task<Amazon.EC2.Model.InstanceAttribute> Attr(string name)
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
        spec.TerminationProtection = B((await Attr("disableApiTermination"))?.DisableApiTermination);
        spec.StopProtection = B((await Attr("disableApiStop"))?.DisableApiStop);
        spec.ShutdownBehavior = (await Attr("instanceInitiatedShutdownBehavior"))?.InstanceInitiatedShutdownBehavior is { Length: > 0 } sb ? sb : "stop";
        return spec;
    }

    /// <summary>
    /// Launch the server. Returns its instance ID, and a warning if something after the launch itself didn't apply
    /// (turning off source/destination checks is a separate call after launching).
    /// </summary>
    public static async Task<(string Id, string Warning)> LaunchAsync(string profile, LaunchSpec spec, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, spec.Region);
        var tags = new List<Tag> { new("Name", spec.Name) };
        tags.AddRange(spec.Tags.Where(t => t.Key != "Name").Select(t => new Tag(t.Key, t.Value)));

        var interfaces = new List<InstanceNetworkInterfaceSpecification>();
        for (int n = 0; n < spec.Interfaces.Count; n++)
        {
            var it = spec.Interfaces[n];
            var eni = new InstanceNetworkInterfaceSpecification
            {
                DeviceIndex = n, SubnetId = it.SubnetId, Groups = it.SecurityGroupIds, DeleteOnTermination = true,
            };
            if (it.Description.Length > 0) eni.Description = it.Description;
            // a public address can only be asked for when the server has a single network interface
            if (spec.Interfaces.Count == 1) eni.AssociatePublicIpAddress = it.PublicIp;
            if (it.PrivateIp.Length > 0 && it.SecondaryIps.Count > 0)
            {
                var addresses = new List<PrivateIpAddressSpecification> { new() { PrivateIpAddress = it.PrivateIp, Primary = true } };
                addresses.AddRange(it.SecondaryIps.Select(ip => new PrivateIpAddressSpecification { PrivateIpAddress = ip, Primary = false }));
                eni.PrivateIpAddresses = addresses;
            }
            else
            {
                if (it.PrivateIp.Length > 0) eni.PrivateIpAddress = it.PrivateIp;
                if (it.SecondaryCount > 0) eni.SecondaryPrivateIpAddressCount = it.SecondaryCount;
            }
            if (it.Ipv6Count > 0) eni.Ipv6AddressCount = it.Ipv6Count;
            interfaces.Add(eni);
        }

        var disks = new List<BlockDeviceMapping>();
        if (spec.RootDevice.Length > 0 && spec.RootDisk.SizeGb > 0) disks.Add(Mapping(spec.RootDevice, spec.RootDisk, spec.Encrypted));
        for (int d = 0; d < spec.DataDisks.Count; d++)
        {
            // AWS's recommended device names: xvdf, xvdg ... on Windows, /dev/sdf, /dev/sdg ... on Linux
            char letter = (char)('f' + d);
            disks.Add(Mapping(spec.IsWindows ? $"xvd{letter}" : $"/dev/sd{letter}", spec.DataDisks[d], spec.Encrypted));
        }

        var request = new RunInstancesRequest
        {
            ImageId = spec.ImageId,
            InstanceType = Amazon.EC2.InstanceType.FindValue(spec.InstanceType),
            MinCount = 1,
            MaxCount = 1,
            NetworkInterfaces = interfaces,
            TagSpecifications = new List<TagSpecification>
            {
                new() { ResourceType = Amazon.EC2.ResourceType.Instance, Tags = tags },
                new() { ResourceType = Amazon.EC2.ResourceType.Volume, Tags = tags },
            },
            MetadataOptions = new InstanceMetadataOptionsRequest
            {
                HttpTokens = spec.RequireTokens ? Amazon.EC2.HttpTokensState.Required : Amazon.EC2.HttpTokensState.Optional,
                HttpEndpoint = Amazon.EC2.InstanceMetadataEndpointState.Enabled,
                HttpPutResponseHopLimit = spec.HopLimit,
            },
            PrivateDnsNameOptions = new PrivateDnsNameOptionsRequest
            {
                HostnameType = Amazon.EC2.HostnameType.FindValue(spec.HostnameType),
                EnableResourceNameDnsARecord = spec.DnsA,
                EnableResourceNameDnsAAAARecord = spec.DnsAaaa,
            },
            Monitoring = spec.DetailedMonitoring,   // the .NET SDK takes a plain true/false here
            DisableApiTermination = spec.TerminationProtection,
            DisableApiStop = spec.StopProtection,
            InstanceInitiatedShutdownBehavior = Amazon.EC2.ShutdownBehavior.FindValue(spec.ShutdownBehavior),
        };
        if (disks.Count > 0) request.BlockDeviceMappings = disks;
        if (spec.Tenancy != "default" || spec.PlacementGroup.Length > 0)
        {
            request.Placement = new Placement { Tenancy = Amazon.EC2.Tenancy.FindValue(spec.Tenancy) };
            if (spec.PlacementGroup.Length > 0) request.Placement.GroupName = spec.PlacementGroup;
        }
        if (spec.InstanceProfile.Length > 0) request.IamInstanceProfile = new IamInstanceProfileSpecification { Name = spec.InstanceProfile };
        if (spec.KeyName.Length > 0) request.KeyName = spec.KeyName;
        if (spec.UserData.Length > 0) request.UserData = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(spec.UserData));

        var res = await ec2.RunInstancesAsync(request, ct);
        var launched = L(res.Reservation?.Instances).FirstOrDefault();
        string id = launched?.InstanceId ?? "";
        Log.Write($"New server: launched {spec.Name} ({id}) in {spec.Region}: {spec.InstanceType}, image {spec.ImageId}, " +
                  $"{spec.Interfaces.Count} interface(s), {1 + spec.DataDisks.Count} disk(s)");

        // source/destination check is set on each interface after launch
        var warnings = new List<string>();
        for (int n = 0; n < spec.Interfaces.Count; n++)
        {
            if (spec.Interfaces[n].SourceDestCheck) continue;
            var eniId = L(launched?.NetworkInterfaces).FirstOrDefault(e => N(e.Attachment?.DeviceIndex) == n)?.NetworkInterfaceId;
            try
            {
                if (string.IsNullOrEmpty(eniId)) throw new InvalidOperationException("its network interface wasn't reported");
                await ec2.ModifyNetworkInterfaceAttributeAsync(new ModifyNetworkInterfaceAttributeRequest { NetworkInterfaceId = eniId, SourceDestCheck = false }, ct);
            }
            catch (Exception ex) { warnings.Add($"source/destination check is still on for interface {n + 1} ({Aws.Describe(ex)})"); }
        }
        return (id, string.Join("; ", warnings));
    }

    private static BlockDeviceMapping Mapping(string device, DiskSpec disk, bool encrypted)
    {
        var ebs = new EbsBlockDevice { VolumeSize = disk.SizeGb, VolumeType = Amazon.EC2.VolumeType.FindValue(disk.Type), DeleteOnTermination = true };
        if (encrypted) ebs.Encrypted = true;   // otherwise the account's default applies
        return new BlockDeviceMapping { DeviceName = device, Ebs = ebs };
    }
}
