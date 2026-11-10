using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace SwitchboardRM;

/// <summary>Shared look for the profile forms: a titled, auto-sized dialog of labelled fields.</summary>
internal abstract class ProfileFormBase : Form
{
    protected const int FieldWidth = 400;
    protected readonly FlowLayoutPanel Body = new()
    {
        FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(22, 18, 22, 18),
    };
    protected readonly Label Error = new() { AutoSize = true, Tag = "accent", MaximumSize = new Size(FieldWidth, 0), Margin = new Padding(0, 10, 0, 0) };

    protected static readonly string[] CommonRegions =
    {
        "us-east-1", "us-east-2", "us-west-1", "us-west-2", "ca-central-1", "ca-west-1", "eu-west-1", "eu-west-2",
        "eu-central-1", "eu-north-1", "ap-southeast-1", "ap-southeast-2", "ap-northeast-1", "ap-south-1", "sa-east-1",
    };

    protected ProfileFormBase(string title)
    {
        Text = title;
        if (AppIcon.Icon != null) Icon = AppIcon.Icon;
        Font = new Font("Segoe UI", 9.5F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Controls.Add(Body);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }

    protected static Label FieldLabel(string text, bool first = false) =>
        new() { Text = text, AutoSize = true, Margin = new Padding(0, first ? 0 : 10, 0, 3) };

    protected static Label Note(string text) =>
        new() { Text = text, AutoSize = true, MaximumSize = new Size(FieldWidth, 0), Tag = "muted", Margin = new Padding(0, 3, 0, 0) };

    protected static ComboBox RegionBox(string value)
    {
        var box = new ComboBox { Width = 200, DropDownStyle = ComboBoxStyle.DropDown };
        box.Items.AddRange(CommonRegions.Cast<object>().ToArray());
        box.Text = value;
        return box;
    }

    protected FlowLayoutPanel Buttons(Button ok, Button cancel)
    {
        AcceptButton = ok;
        CancelButton = cancel;
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        row.Controls.AddRange(new Control[] { ok, cancel });
        return row;
    }

    protected static Button OkButton(string text) =>
        new() { Text = text, Tag = "primary", AutoSize = true, MinimumSize = new Size(120, 32), Margin = new Padding(0, 0, 8, 0) };

    protected static Button CancelButtonFor() =>
        new() { Text = "Cancel", AutoSize = true, MinimumSize = new Size(96, 32), Margin = new Padding(0), DialogResult = DialogResult.Cancel };

    protected static bool ValidRegion(string region) => Regex.IsMatch(region, @"^[a-z]{2}(-gov)?-[a-z]+-\d$");
}

/// <summary>
/// A profile's Microsoft (Entra ID) sign-in settings, written to ~/.aws/config under the same names aws-azure-login
/// uses (azure_tenant_id, azure_app_id_uri, azure_default_username, azure_default_role_arn,
/// azure_default_duration_hours, azure_default_remember_me, region), so either sign-in can use the profile. Check
/// sign-in tries the settings in the built-in sign-in window and lists the account's AWS roles.
/// </summary>
internal sealed class MicrosoftProfileForm : ProfileFormBase
{
    private readonly string _profile;
    private readonly TextBox _tenant = new() { Width = FieldWidth, PlaceholderText = "e.g. 00000000-0000-0000-0000-000000000000" };
    private readonly TextBox _appIdUri = new() { Width = FieldWidth, PlaceholderText = "the AWS app's identifier in Entra" };
    private readonly TextBox _account = new() { Width = FieldWidth, PlaceholderText = "e.g. admin_you@example.com" };
    private readonly ComboBox _region;
    private readonly NumericUpDown _hours = new() { Minimum = 1, Maximum = 12, Width = 70 };
    private readonly ComboBox _role = new() { Width = FieldWidth - 130, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly Button _check = new() { Text = "Check sign-in", AutoSize = true, MinimumSize = new Size(120, 28), Margin = new Padding(8, 0, 0, 0) };
    private readonly ThemedCheckBox _stay = new() { Text = "Stay signed in (lets Switchboard renew the sign-in in the background)", AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
    private readonly Label _checkResult = new() { AutoSize = true, MaximumSize = new Size(FieldWidth, 0), Tag = "muted", Margin = new Padding(0, 4, 0, 0) };

    public MicrosoftProfileForm(string profile, bool isNew)
        : base(isNew ? $"Set up Microsoft sign-in for {profile}" : $"Microsoft sign-in for {profile}")
    {
        _profile = profile;

        // this profile's own settings; anything missing comes from another Microsoft profile on this PC (same tenant)
        string Own(string key) => Aws.ConfigValue(profile, key);
        var (otherTenant, otherApp, from) = Aws.ExistingAzureSettings(except: profile);
        string From(string key) => string.IsNullOrEmpty(from) ? "" : Aws.ConfigValue(from, key);
        string Pick(string key, string fallback) => Own(key) is { Length: > 0 } v ? v : fallback;

        _tenant.Text = Pick("azure_tenant_id", otherTenant);
        _appIdUri.Text = Pick("azure_app_id_uri", otherApp);
        _account.Text = Pick("azure_default_username", From("azure_default_username"));
        _region = RegionBox(Pick("region", From("region") is { Length: > 0 } r ? r : "us-east-1"));
        _hours.Value = decimal.TryParse(Pick("azure_default_duration_hours", From("azure_default_duration_hours")),
            NumberStyles.Number, CultureInfo.InvariantCulture, out var h) && h is >= 1 and <= 12 ? decimal.Truncate(h) : 1;
        _role.Text = Own("azure_default_role_arn");
        _stay.Checked = !Own("azure_default_remember_me").Equals("false", StringComparison.OrdinalIgnoreCase);

        var roleRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        roleRow.Controls.AddRange(new Control[] { _role, _check });
        _check.Click += async (_, _) => await CheckAsync();

        var save = OkButton(isNew ? "Save profile" : "Save");
        save.Click += (_, _) => Save();

        Body.Controls.AddRange(new Control[]
        {
            FieldLabel("Tenant ID", first: true), _tenant,
            Note("Your organisation's Microsoft Entra tenant: Entra admin center > Overview > Tenant ID." +
                 (from is { Length: > 0 } && Own("azure_tenant_id").Length == 0 ? $" Filled in from your profile {from}." : "")),
            FieldLabel("App ID URI"), _appIdUri,
            Note("The AWS app in Entra: Enterprise applications > your AWS app > Single sign-on > Identifier (Entity ID)."),
            FieldLabel("Account"), _account,
            Note("The Microsoft account to sign in with. Useful when it isn't the one you're signed in to Windows with."),
            FieldLabel("AWS region"), _region,
            FieldLabel("Session length (hours)"), _hours,
            Note("Up to the role's maximum session duration in AWS (1 hour unless your administrator raised it)."),
            FieldLabel("Default AWS role"), roleRow,
            Note("Only needed if your account has several AWS roles. Check sign-in signs in with these settings and " +
                 "lists your roles here; no AWS keys are saved by it."),
            _checkResult,
            _stay,
            Error,
            Buttons(save, CancelButtonFor()),
        });
        Theme.Apply(this, Theme.Current.IsDark);
    }

    /// <summary>Sign in with what's in the form (built-in sign-in window), and list the roles the account gets.</summary>
    private async Task CheckAsync()
    {
        Error.Text = "";
        if (!Required(out string tenant, out string app)) return;
        _check.Enabled = false;
        _checkResult.Text = "Signing in to check ...";
        try
        {
            var (saml, failure) = await EntraSignIn.GetSamlAsync(this, tenant, app, _account.Text.Trim(), _region.Text.Trim(),
                interactive: true, CancellationToken.None);
            if (saml == null)
            {
                _checkResult.Text = failure ?? "Check cancelled.";
                return;
            }
            var roles = EntraSignIn.Roles(saml).Select(r => r.Role).Distinct().OrderBy(r => r).ToList();
            string current = _role.Text;
            _role.Items.Clear();
            _role.Items.AddRange(roles.Cast<object>().ToArray());
            if (roles.Count == 1) _role.Text = roles[0];
            else if (roles.Contains(current)) _role.Text = current;
            _checkResult.Text = roles.Count switch
            {
                0 => "Signed in, but this account has no AWS roles in the app. Ask your administrator to assign one.",
                1 => "Signed in. Your account has one AWS role, so no default is needed (it's filled in anyway).",
                _ => $"Signed in. Your account has {roles.Count} AWS roles: pick the default from the list.",
            };
        }
        catch (Exception ex) { _checkResult.Text = "Check failed: " + ex.Message; }
        finally { _check.Enabled = true; }
    }

    private bool Required(out string tenant, out string app)
    {
        tenant = _tenant.Text.Trim();
        app = _appIdUri.Text.Trim();
        if (tenant.Length == 0) { Error.Text = "Enter the tenant ID."; return false; }
        if (app.Length == 0) { Error.Text = "Enter the app ID URI."; return false; }
        return true;
    }

    private void Save()
    {
        Error.Text = "";
        if (!Required(out string tenant, out string app)) return;
        string region = _region.Text.Trim();
        if (!ValidRegion(region)) { Error.Text = "Enter an AWS region such as us-east-1 or ca-central-1."; return; }
        string role = _role.Text.Trim();
        if (role.Length > 0 && !role.StartsWith("arn:aws", StringComparison.OrdinalIgnoreCase))
        {
            Error.Text = "The default role should be a role ARN (arn:aws:iam::...:role/...), or empty.";
            return;
        }
        try
        {
            Aws.SetConfigValues(_profile, new Dictionary<string, string>
            {
                ["azure_tenant_id"] = tenant,
                ["azure_app_id_uri"] = app,
                ["azure_default_username"] = _account.Text.Trim(),
                ["azure_default_role_arn"] = role,
                ["azure_default_duration_hours"] = ((int)_hours.Value).ToString(CultureInfo.InvariantCulture),
                ["azure_default_remember_me"] = _stay.Checked ? "true" : "false",
                ["region"] = region,
            });
            Log.Write($"Saved Microsoft sign-in settings for profile {_profile}");
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) { Error.Text = "Couldn't save to ~/.aws/config: " + ex.Message; }
    }
}

/// <summary>
/// A profile's AWS IAM Identity Center (SSO) settings, written to ~/.aws/config in the standard form the AWS CLI and
/// SDKs read (sso_start_url, sso_region, sso_account_id, sso_role_name, region). Signing in still uses "aws sso login".
/// </summary>
internal sealed class SsoProfileForm : ProfileFormBase
{
    private readonly string _profile;
    private readonly TextBox _startUrl = new() { Width = FieldWidth, PlaceholderText = "https://your-org.awsapps.com/start" };
    private readonly ComboBox _ssoRegion;
    private readonly TextBox _account = new() { Width = 200, PlaceholderText = "123456789012" };
    private readonly TextBox _roleName = new() { Width = FieldWidth, PlaceholderText = "e.g. AdministratorAccess" };
    private readonly ComboBox _region;

    public SsoProfileForm(string profile, bool isNew)
        : base(isNew ? $"Set up SSO for {profile}" : $"SSO settings for {profile}")
    {
        _profile = profile;
        string Own(string key) => Aws.ConfigValue(profile, key);
        // start URL and SSO region from another SSO profile on this PC, if this one has none yet
        string other = Aws.ListProfiles().FirstOrDefault(p => !p.Equals(profile, StringComparison.OrdinalIgnoreCase) &&
                                                              Aws.ConfigValue(p, "sso_start_url").Length > 0) ?? "";
        string Pick(string key) => Own(key) is { Length: > 0 } v ? v : other.Length > 0 ? Aws.ConfigValue(other, key) : "";

        _startUrl.Text = Pick("sso_start_url");
        _ssoRegion = RegionBox(Pick("sso_region") is { Length: > 0 } sr ? sr : "us-east-1");
        _account.Text = Own("sso_account_id");
        _roleName.Text = Own("sso_role_name");
        _region = RegionBox(Pick("region") is { Length: > 0 } r ? r : "us-east-1");

        var save = OkButton(isNew ? "Save profile" : "Save");
        save.Click += (_, _) => Save();

        Body.Controls.AddRange(new Control[]
        {
            FieldLabel("SSO start URL", first: true), _startUrl,
            Note("Your AWS access portal address, from IAM Identity Center > Settings" +
                 (other.Length > 0 && Own("sso_start_url").Length == 0 ? $" (filled in from your profile {other})." : ".")),
            FieldLabel("SSO region"), _ssoRegion,
            Note("The region IAM Identity Center is in (shown on the same settings page)."),
            FieldLabel("AWS account ID"), _account,
            FieldLabel("Role (permission set) name"), _roleName,
            Note("The account and permission set to sign in to: the access portal lists the ones you can use."),
            FieldLabel("AWS region"), _region,
            Note("Where your servers are. Sign-in uses \"aws sso login\" (the bundled AWS CLI)."),
            Error,
            Buttons(save, CancelButtonFor()),
        });
        Theme.Apply(this, Theme.Current.IsDark);
    }

    private void Save()
    {
        Error.Text = "";
        string url = _startUrl.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
        { Error.Text = "Enter the SSO start URL (https://...)."; return; }
        if (!ValidRegion(_ssoRegion.Text.Trim())) { Error.Text = "Enter the SSO region, such as us-east-1."; return; }
        if (!Regex.IsMatch(_account.Text.Trim(), @"^\d{12}$")) { Error.Text = "The AWS account ID is 12 digits."; return; }
        if (_roleName.Text.Trim().Length == 0) { Error.Text = "Enter the role (permission set) name."; return; }
        if (!ValidRegion(_region.Text.Trim())) { Error.Text = "Enter an AWS region such as us-east-1 or ca-central-1."; return; }
        try
        {
            Aws.SetConfigValues(_profile, new Dictionary<string, string>
            {
                ["sso_start_url"] = url,
                ["sso_region"] = _ssoRegion.Text.Trim(),
                ["sso_account_id"] = _account.Text.Trim(),
                ["sso_role_name"] = _roleName.Text.Trim(),
                ["region"] = _region.Text.Trim(),
            });
            Log.Write($"Saved SSO settings for profile {_profile}");
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) { Error.Text = "Couldn't save to ~/.aws/config: " + ex.Message; }
    }
}
