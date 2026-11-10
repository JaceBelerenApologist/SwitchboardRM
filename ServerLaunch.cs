using Amazon.EC2;
using Amazon.EC2.Model;
using Amazon.IdentityManagement;
using Amazon.SimpleSystemsManagement;
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
    public override string ToString() => Mine
        ? $"My image: {Name}" + (Created > DateTime.MinValue ? $"  ({Created:yyyy-MM-dd})" : "")
        : $"AWS: {Name}";
}

internal sealed class TypeChoice
{
    public string Name = "";
    public int VCpus;
    public double MemoryGiB;
    public override string ToString() => $"{Name}   ({VCpus} vCPU, {MemoryGiB:0.##} GiB)";
}

internal sealed class NamedChoice
{
    public string Id = "", Name = "", Detail = "", VpcId = "";
    public bool Flag;   // subnet: default-ish; instance profile: has Session Manager permissions
    public override string ToString() =>
        (Name.Length > 0 && Name != Id ? $"{Name}  ({Id})" : Id) + (Detail.Length > 0 ? "   " + Detail : "");
}

/// <summary>What to launch.</summary>
internal sealed class LaunchSpec
{
    public string Region = "", ImageId = "", RootDevice = "", InstanceType = "", SubnetId = "", InstanceProfile = "", KeyName = "";
    public List<string> SecurityGroupIds = new();
    public bool PublicIp;
    public int DiskGb;
    public string DiskType = "gp3";
    public string Name = "";
    public List<KeyValuePair<string, string>> Tags = new();
}

/// <summary>A server's settings, to start the wizard from ("New server like this one").</summary>
internal sealed class CloneSource
{
    public string Region = "", ImageId = "", InstanceType = "", SubnetId = "", VpcId = "", InstanceProfile = "", KeyName = "";
    public List<string> SecurityGroupIds = new();
    public bool PublicIp;
    public int DiskGb;
    public string DiskType = "";
    public List<KeyValuePair<string, string>> Tags = new();
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
        using var ssm = new AmazonSimpleSystemsManagementClient(creds, Aws.Region(region));
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
            .Select(v => new NamedChoice { Id = v.VpcId, Name = Tag(v.Tags, "Name"), Detail = v.CidrBlock + (B(v.IsDefault) ? "  default" : ""), VpcId = v.VpcId })
            .OrderBy(v => v.Name.Length == 0).ThenBy(v => v.Name).ToList();

        var subnets = new List<NamedChoice>();
        string token = null;
        do
        {
            var page = await ec2.DescribeSubnetsAsync(new DescribeSubnetsRequest { NextToken = token }, ct);
            subnets.AddRange(L(page.Subnets).Select(s => new NamedChoice
            {
                Id = s.SubnetId, Name = Tag(s.Tags, "Name"), VpcId = s.VpcId,
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
        using var iam = new AmazonIdentityManagementServiceClient(Aws.Credentials(profile), Amazon.RegionEndpoint.USEast1);
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

    /// <summary>A server's settings for "New server like this one".</summary>
    public static async Task<CloneSource> ReadForCloneAsync(string profile, string region, string instanceId, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, region);
        var res = await ec2.DescribeInstancesAsync(new DescribeInstancesRequest { InstanceIds = new List<string> { instanceId } }, ct);
        var i = L(res.Reservations).SelectMany(r => L(r.Instances)).FirstOrDefault()
                ?? throw new InvalidOperationException($"{instanceId} wasn't found in {region}.");

        var src = new CloneSource
        {
            Region = region, ImageId = i.ImageId ?? "", InstanceType = i.InstanceType?.Value ?? "",
            SubnetId = i.SubnetId ?? "", VpcId = i.VpcId ?? "", KeyName = i.KeyName ?? "",
            SecurityGroupIds = L(i.SecurityGroups).Select(g => g.GroupId).ToList(),
            PublicIp = !string.IsNullOrEmpty(i.PublicIpAddress),
            Tags = L(i.Tags).Where(t => t.Key != "Name" && !t.Key.StartsWith("aws:", StringComparison.Ordinal))
                            .Select(t => new KeyValuePair<string, string>(t.Key, t.Value)).ToList(),
        };
        string arn = i.IamInstanceProfile?.Arn ?? "";
        if (arn.Contains("instance-profile/")) src.InstanceProfile = arn[(arn.LastIndexOf('/') + 1)..];

        var rootVolume = L(i.BlockDeviceMappings).FirstOrDefault(b => b.DeviceName == i.RootDeviceName)?.Ebs?.VolumeId;
        if (!string.IsNullOrEmpty(rootVolume))
        {
            try
            {
                var vol = L((await ec2.DescribeVolumesAsync(new DescribeVolumesRequest { VolumeIds = new List<string> { rootVolume } }, ct)).Volumes).FirstOrDefault();
                src.DiskGb = N(vol?.Size);
                src.DiskType = vol?.VolumeType?.Value ?? "";
            }
            catch { /* disk size then comes from the image */ }
        }
        return src;
    }

    /// <summary>Launch the server. Returns its instance ID.</summary>
    public static async Task<string> LaunchAsync(string profile, LaunchSpec spec, CancellationToken ct)
    {
        using var ec2 = Ec2(profile, spec.Region);
        var tags = new List<Tag> { new("Name", spec.Name) };
        tags.AddRange(spec.Tags.Where(t => t.Key != "Name").Select(t => new Tag(t.Key, t.Value)));

        var request = new RunInstancesRequest
        {
            ImageId = spec.ImageId,
            InstanceType = InstanceType.FindValue(spec.InstanceType),
            MinCount = 1,
            MaxCount = 1,
            NetworkInterfaces = new List<InstanceNetworkInterfaceSpecification>
            {
                new()
                {
                    DeviceIndex = 0,
                    SubnetId = spec.SubnetId,
                    Groups = spec.SecurityGroupIds,
                    AssociatePublicIpAddress = spec.PublicIp,
                    DeleteOnTermination = true,
                },
            },
            TagSpecifications = new List<TagSpecification>
            {
                new() { ResourceType = ResourceType.Instance, Tags = tags },
                new() { ResourceType = ResourceType.Volume, Tags = tags },
            },
            // instance metadata with tokens only (IMDSv2): AWS's recommended setting
            MetadataOptions = new InstanceMetadataOptionsRequest
            {
                HttpTokens = HttpTokensState.Required,
                HttpEndpoint = InstanceMetadataEndpointState.Enabled,
            },
        };
        if (spec.InstanceProfile.Length > 0) request.IamInstanceProfile = new IamInstanceProfileSpecification { Name = spec.InstanceProfile };
        if (spec.KeyName.Length > 0) request.KeyName = spec.KeyName;
        if (spec.RootDevice.Length > 0 && spec.DiskGb > 0)
        {
            request.BlockDeviceMappings = new List<BlockDeviceMapping>
            {
                new()
                {
                    DeviceName = spec.RootDevice,
                    Ebs = new EbsBlockDevice { VolumeSize = spec.DiskGb, VolumeType = VolumeType.FindValue(spec.DiskType), DeleteOnTermination = true },
                },
            };
        }

        var res = await ec2.RunInstancesAsync(request, ct);
        string id = L(res.Reservation?.Instances).FirstOrDefault()?.InstanceId ?? "";
        Log.Write($"New server: launched {spec.Name} ({id}) in {spec.Region}: {spec.InstanceType}, image {spec.ImageId}, subnet {spec.SubnetId}");
        return id;
    }
}
