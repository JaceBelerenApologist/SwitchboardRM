using System.IO.Compression;
using System.Text;
using System.Xml;
using Amazon;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SwitchboardRM;

/// <summary>
/// Built-in Microsoft (Entra ID) sign-in to AWS, doing what aws-azure-login does without Node or Puppeteer's
/// Chromium. It uses the same profile settings in ~/.aws/config (azure_tenant_id, azure_app_id_uri,
/// azure_default_username, azure_default_role_arn, azure_default_duration_hours, region) and writes the same keys to
/// ~/.aws/credentials, so the rest of Switchboard can't tell which one signed in.
///
///   1. A SAML sign-in request for the AWS app in Entra, compressed and encoded into a login.microsoftonline.com address
///      (exactly as aws-azure-login builds it), with the account hint.
///   2. A sign-in window hosting WebView2: Microsoft's evergreen browser engine, kept up to date by Windows. It has its
///      own private profile (so a "stay signed in" session is remembered for background renewals) and doesn't use the
///      Windows account for single sign-on.
///   3. The window watches for Microsoft's hand-off to AWS (the SAMLResponse posted to signin.aws.amazon.com/saml)
///      from before it loads anything, so there's no race, and keeps the response instead of letting it through.
///   4. The AWS roles in the response: the only one, the profile's default, or the one you pick.
///   5. AWS STS AssumeRoleWithSAML turns it into temporary keys, written to the profile in ~/.aws/credentials.
/// </summary>
internal static class EntraSignIn
{
    private const string RoleAttribute = "https://aws.amazon.com/SAML/Attributes/Role";

    /// <summary>
    /// Sign the profile in. interactive = a visible window you sign in through; otherwise a hidden one that only
    /// succeeds if Microsoft remembers the session (background renewal). Returns the same shape as running
    /// aws-azure-login: Code 0 = signed in, -2 = cancelled, anything else = failed (Err says why).
    /// </summary>
    public static async Task<(int Code, string Out, string Err)> RunAsync(Form owner, string profile, bool interactive, CancellationToken ct)
    {
        string tenant = Aws.ConfigValue(profile, "azure_tenant_id");
        string appIdUri = Aws.ConfigValue(profile, "azure_app_id_uri");
        if (tenant.Length == 0 || appIdUri.Length == 0)
            return (1, "", $"The profile {profile} isn't set up for Microsoft sign-in: azure_tenant_id and azure_app_id_uri " +
                           "are needed in ~/.aws/config (AWS > Add profile sets them up).");
        string username = Aws.ConfigValue(profile, "azure_default_username");
        string defaultRole = Aws.ConfigValue(profile, "azure_default_role_arn");
        string region = Aws.ConfigValue(profile, "region");
        double hours = double.TryParse(Aws.ConfigValue(profile, "azure_default_duration_hours"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h) && h > 0 ? h : 1;

        // 1-3. the sign-in window, and Microsoft's hand-off to AWS
        Log.Write($"Built-in sign-in for {profile} ({(interactive ? "window" : "background")})");
        var (saml, failure) = await GetSamlAsync(owner, tenant, appIdUri, username, region, interactive, ct);
        if (saml == null) return failure == null ? (-2, "", "Sign-in was cancelled.") : (1, "", failure);

        // 4. which role
        List<(string Role, string Principal)> roles;
        try { roles = Roles(saml); }
        catch (Exception ex) { return (1, "", "Couldn't read the sign-in response: " + ex.Message); }
        if (roles.Count == 0) return (1, "", "The sign-in response has no AWS roles for this account.");

        (string Role, string Principal) role;
        int preferred = roles.FindIndex(r => r.Role.Equals(defaultRole, StringComparison.OrdinalIgnoreCase));
        if (roles.Count == 1) role = roles[0];
        else if (preferred >= 0) role = roles[preferred];
        else if (interactive)
        {
            using var picker = new RolePickerForm(roles.Select(r => r.Role).OrderBy(r => r).ToList(), defaultRole);
            if (picker.ShowDialog(owner) != DialogResult.OK || picker.Chosen == null) return (-2, "", "No role was chosen.");
            role = roles.First(r => r.Role == picker.Chosen);
        }
        else return (1, "", $"This account has {roles.Count} AWS roles and the profile has no azure_default_role_arn to choose one, " +
                            "so the background sign-in can't pick: sign in from the toolbar.");
        Log.Write($"Built-in sign-in: role {role.Role}, for {hours:0.##} hour(s)");

        // 5. temporary keys from AWS, into ~/.aws/credentials
        try
        {
            var endpoint = RegionEndpoint.GetBySystemName(region.Length > 0 ? region : "us-east-1");
            using var sts = new AmazonSecurityTokenServiceClient(new AnonymousAWSCredentials(), endpoint);
            var res = await sts.AssumeRoleWithSAMLAsync(new Amazon.SecurityToken.Model.AssumeRoleWithSAMLRequest
            {
                RoleArn = role.Role,
                PrincipalArn = role.Principal,
                SAMLAssertion = saml,
                DurationSeconds = (int)Math.Round(hours * 3600),
            }, ct);
            var c = res.Credentials;
            DateTime? expires = c.Expiration;
            Aws.SetCredentials(profile, new Dictionary<string, string>
            {
                ["aws_access_key_id"] = c.AccessKeyId,
                ["aws_secret_access_key"] = c.SecretAccessKey,
                ["aws_session_token"] = c.SessionToken,
                ["aws_expiration"] = expires?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ") ?? "",
            });
            Log.Write($"Built-in sign-in: keys for {profile} written, valid until {expires?.ToLocalTime():g}");
            return (0, $"Signed in as {role.Role}", "");
        }
        catch (OperationCanceledException) { return (-2, "", "Sign-in was cancelled."); }
        catch (Exception ex)
        {
            string why = Aws.Describe(ex);
            Log.Write("Built-in sign-in: AWS refused the role: " + why);
            return (1, "", "AWS didn't accept the sign-in: " + why);
        }
    }

    /// <summary>
    /// Steps 1-3 on their own: open the sign-in window for these settings and return Microsoft's SAML response for
    /// AWS (base64). Failure is null when it was cancelled. Used by sign-in, and by profile setup's Check sign-in,
    /// which only wants to know the settings work and which roles the account has.
    /// </summary>
    public static async Task<(string Saml, string Failure)> GetSamlAsync(Form owner, string tenant, string appIdUri,
        string username, string region, bool interactive, CancellationToken ct)
    {
        string acs = AssertionConsumerService(region ?? "");
        string url = LoginUrl(tenant, appIdUri, acs, username ?? "");
        Log.Write($"Built-in sign-in: tenant {tenant}, app {appIdUri}, " +
                  (string.IsNullOrEmpty(username) ? "no account hint" : $"account {username}") + $", AWS endpoint {acs}");

        using var window = new EntraSignInForm(url, interactive);
        using var reg = ct.Register(() => { try { window.BeginInvoke(() => window.Cancel()); } catch { } });
        if (interactive) window.ShowDialog(owner);
        else
        {
            window.Show();
            await window.Finished;
        }
        if (window.SamlResponse != null)
        {
            Log.Write("Built-in sign-in: Microsoft handed the sign-in to AWS");
            return (window.SamlResponse, null);
        }
        Log.Write(window.Failure != null ? "Built-in sign-in didn't finish: " + window.Failure : "Built-in sign-in cancelled");
        return (null, window.Failure);
    }

    /// <summary>Where Entra posts the response: AWS, AWS GovCloud or AWS China (as aws-azure-login decides it).</summary>
    private static string AssertionConsumerService(string region) =>
        region.StartsWith("us-gov", StringComparison.OrdinalIgnoreCase) ? "https://signin.amazonaws-us-gov.com/saml"
        : region.StartsWith("cn-", StringComparison.OrdinalIgnoreCase) ? "https://signin.amazonaws.cn/saml"
        : "https://signin.aws.amazon.com/saml";

    /// <summary>The Entra sign-in address with a SAML request for the AWS app: aws-azure-login's request, byte for byte in form.</summary>
    internal static string LoginUrl(string tenant, string appIdUri, string acs, string username)
    {
        string request =
            $"<samlp:AuthnRequest xmlns=\"urn:oasis:names:tc:SAML:2.0:metadata\" ID=\"id{Guid.NewGuid()}\" Version=\"2.0\" " +
            $"IssueInstant=\"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}\" IsPassive=\"false\" AssertionConsumerServiceURL=\"{acs}\" " +
            "xmlns:samlp=\"urn:oasis:names:tc:SAML:2.0:protocol\">" +
            $"<Issuer xmlns=\"urn:oasis:names:tc:SAML:2.0:assertion\">{System.Security.SecurityElement.Escape(appIdUri)}</Issuer>" +
            "<samlp:NameIDPolicy Format=\"urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress\"></samlp:NameIDPolicy>" +
            "</samlp:AuthnRequest>";

        // raw deflate (no zlib header), then base64: SAML's HTTP-Redirect encoding
        using var compressed = new MemoryStream();
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(request);
            deflate.Write(bytes, 0, bytes.Length);
        }
        string url = $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/saml2?SAMLRequest=" +
                     Uri.EscapeDataString(Convert.ToBase64String(compressed.ToArray()));
        if (username.Length > 0) url += "&login_hint=" + Uri.EscapeDataString(username);
        return url;
    }

    /// <summary>The AWS roles in a SAML response: "role ARN, provider ARN" pairs (in either order).</summary>
    internal static List<(string Role, string Principal)> Roles(string samlResponse)
    {
        var doc = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(new StringReader(Encoding.UTF8.GetString(Convert.FromBase64String(samlResponse))),
                   new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            doc.Load(reader);

        var roles = new List<(string, string)>();
        foreach (XmlNode value in doc.SelectNodes(
                     $"//*[local-name()='Attribute' and @Name='{RoleAttribute}']/*[local-name()='AttributeValue']"))
        {
            var parts = value.InnerText.Split(',').Select(p => p.Trim()).ToArray();
            if (parts.Length != 2) continue;
            roles.Add(parts[0].Contains(":role/") ? (parts[0], parts[1]) : (parts[1], parts[0]));
        }
        return roles;
    }
}

/// <summary>
/// The sign-in window: WebView2 with its own private profile, no Windows-account single sign-on, and a watch on the
/// AWS sign-in endpoints that's in place before the first page loads. Hidden (off-screen) for background renewals.
/// </summary>
internal sealed class EntraSignInForm : Form
{
    private static readonly string[] Endpoints =
        { "https://signin.aws.amazon.com/saml", "https://signin.amazonaws-us-gov.com/saml", "https://signin.amazonaws.cn/saml" };

    private readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    private readonly string _url;
    private readonly bool _interactive;
    private readonly TaskCompletionSource<bool> _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CoreWebView2Environment _env;

    /// <summary>The SAMLResponse Microsoft handed to AWS (base64), or null.</summary>
    public string SamlResponse { get; private set; }
    /// <summary>Why it didn't finish (null when it was simply closed or cancelled).</summary>
    public string Failure { get; private set; }
    /// <summary>Completes when the window closes.</summary>
    public Task Finished => _finished.Task;

    public EntraSignInForm(string url, bool interactive)
    {
        _url = url;
        _interactive = interactive;
        Text = "Sign in to AWS";
        Icon = AppIcon.Icon;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(460, 640);
        MinimumSize = new Size(380, 480);
        Controls.Add(_web);
        if (interactive)
        {
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = true;
        }
        else
        {
            // background renewal: never seen, never focused
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
        }
        Theme.Apply(this, Theme.Current.IsDark);
    }

    protected override bool ShowWithoutActivation => !_interactive;

    public void Cancel()
    {
        if (!IsDisposed) Close();
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.DataName, "SignIn");
            _env = await CoreWebView2Environment.CreateAsync(null, folder,
                new CoreWebView2EnvironmentOptions { AllowSingleSignOnUsingOSPrimaryAccount = false });
            await _web.EnsureCoreWebView2Async(_env);
            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;

            // the watch goes on before anything loads: Microsoft's hand-off can't get past it
            foreach (var endpoint in Endpoints) core.AddWebResourceRequestedFilter(endpoint + "*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnRequest;
            core.NewWindowRequested += (_, a) => { a.Handled = true; core.Navigate(a.Uri); };   // keep sign-in in this window
            core.DocumentTitleChanged += (_, _) => { if (_interactive) Text = "Sign in to AWS - " + core.DocumentTitle; };

            if (!_interactive)
            {
                // a background renewal only works if Microsoft remembers the session; if it needs you, stop
                var timer = new System.Windows.Forms.Timer { Interval = 45000 };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    Failure ??= "Microsoft needs you to sign in again (the remembered session didn't complete it on its own).";
                    Close();
                };
                timer.Start();
            }
            core.Navigate(_url);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Failure = "The Microsoft Edge WebView2 Runtime isn't installed, and the built-in sign-in needs it.";
            Close();
        }
        catch (Exception ex)
        {
            Failure = "The sign-in window couldn't start: " + ex.Message;
            Close();
        }
    }

    private void OnRequest(object sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            if (!e.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) || e.Request.Content == null) return;
            string body;
            using (var reader = new StreamReader(e.Request.Content, Encoding.UTF8)) body = reader.ReadToEnd();
            string saml = FormValue(body, "SAMLResponse");
            if (string.IsNullOrEmpty(saml)) return;

            SamlResponse = saml;
            // answer it ourselves, so AWS's console never opens in this window
            e.Response = _env.CreateWebResourceResponse(
                new MemoryStream(Encoding.UTF8.GetBytes(
                    "<!doctype html><body style=\"font:14px Segoe UI,sans-serif;color:#555;display:flex;align-items:center;" +
                    "justify-content:center;height:90vh\">Signed in. Finishing in Switchboard ...</body>")),
                200, "OK", "Content-Type: text/html; charset=utf-8");
            DialogResult = DialogResult.OK;
            BeginInvoke(Close);
        }
        catch (Exception ex) { Log.Write("Built-in sign-in: couldn't read the hand-off: " + ex.Message); }
    }

    /// <summary>One value from an application/x-www-form-urlencoded body.</summary>
    internal static string FormValue(string body, string name)
    {
        foreach (var pair in body.Split('&'))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            if (Uri.UnescapeDataString(pair[..eq].Replace('+', ' ')) == name)
                return Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }
        return null;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _finished.TrySetResult(SamlResponse != null);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _web.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Choose one of several AWS roles (only when the profile's default role isn't among them).</summary>
internal sealed class RolePickerForm : Form
{
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    public string Chosen => _list.SelectedItem as string;

    public RolePickerForm(List<string> roles, string preferred)
    {
        Text = "Choose an AWS role";
        Icon = AppIcon.Icon;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(560, 300);
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;

        var label = new Label
        {
            Dock = DockStyle.Top, AutoSize = false, Height = 44, Padding = new Padding(10, 10, 10, 4),
            Text = "Your account can use several AWS roles. Choose one for this sign-in " +
                   "(set azure_default_role_arn in the profile to skip this).",
        };
        _list.Items.AddRange(roles.Cast<object>().ToArray());
        _list.SelectedItem = roles.FirstOrDefault(r => r.Equals(preferred, StringComparison.OrdinalIgnoreCase)) ?? roles.FirstOrDefault();
        _list.DoubleClick += (_, _) => { if (Chosen != null) { DialogResult = DialogResult.OK; Close(); } };

        var ok = new Button { Text = "Sign in", Tag = "primary", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(8) };
        buttons.Controls.AddRange(new Control[] { ok, cancel });
        var middle = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 10, 0) };
        middle.Controls.Add(_list);

        Controls.Add(middle);
        Controls.Add(buttons);
        Controls.Add(label);
        AcceptButton = ok;
        CancelButton = cancel;
        Theme.Apply(this, Theme.Current.IsDark);
    }
}
