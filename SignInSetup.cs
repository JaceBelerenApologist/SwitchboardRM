using System.Diagnostics;
using System.Windows.Forms;

namespace SwitchboardRM;

/// <summary>
/// First-time setup for Microsoft sign-in: runs aws-azure-login's configuration in a PowerShell window, with the
/// answers to type shown on screen. Uses the copy in the tools folder; installs it through npm only if that's missing.
/// </summary>
internal static class SignInSetup
{
    // Only used when Switchboard was shipped without its tools folder.
    private const string InstallBlock = """
            if (-not (Get-Command aws-azure-login -ErrorAction SilentlyContinue)) {
                if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
                    Write-Host 'Node.js is not installed. Install the LTS version from https://nodejs.org,' -ForegroundColor Yellow
                    Write-Host 'then close this window and Switchboard, reopen Switchboard and click Set up again.' -ForegroundColor Yellow
                    return
                }
                Write-Host 'Installing aws-azure-login (a minute or two) ...'
                npm install -g aws-azure-login
            }
        """;

    public static void Run(string profile, IWin32Window owner)
    {
        profile = string.IsNullOrWhiteSpace(profile) ? "default" : profile.Trim();

        // Show the same tenant and app ID as an existing profile on this PC (read from ~/.aws/config now),
        // so no organisation's details are built into the app.
        var (tenant, appId, from) = Aws.ExistingAzureSettings(except: profile);
        string tenantLine = tenant.Length > 0
            ? $"  Azure Tenant ID:  {tenant}   (same as your profile {from})"
            : "  Azure Tenant ID:  your organisation's Microsoft Entra tenant ID (ask your administrator)";
        string appIdLine = appId.Length > 0
            ? $"  Azure App ID URI: {appId}"
            : "  Azure App ID URI: usually urn:amazon:webservices (ask your administrator if unsure)";
        const string script = """
            $name = '__PROFILE__'
            Write-Host "Setting up Microsoft sign-in for profile $name" -ForegroundColor Cyan
            __INSTALL__
            Write-Host ''
            Write-Host 'Answer the questions below:' -ForegroundColor Cyan
            Write-Host '__TENANT_LINE__'
            Write-Host '__APPID_LINE__'
            Write-Host '  Default Username: your admin sign-in email'
            Write-Host '  Default Role ARN: leave blank'
            Write-Host '  Session hours:    1'
            Write-Host '  Stay logged in:   true   (lets Switchboard renew your sign-in in the background)'
            Write-Host ''
            __AZLOGIN__ --configure --profile $name
            Write-Host ''
            Write-Host 'Done. Close this window, then click Sign in in Switchboard.' -ForegroundColor Green
            """;
        try
        {
            string file = Path.Combine(Path.GetTempPath(), "switchboard-signin-setup.ps1");
            File.WriteAllText(file, script
                .Replace("__PROFILE__", profile.Replace("'", "''"))
                .Replace("__TENANT_LINE__", tenantLine.Replace("'", "''"))
                .Replace("__APPID_LINE__", appIdLine.Replace("'", "''"))
                .Replace("__INSTALL__", Tools.HasBundledAzureLogin ? "" : InstallBlock)
                .Replace("__AZLOGIN__", Tools.HasBundledAzureLogin
                    ? $"& '{Tools.BundledNode.Replace("'", "''")}' '{Tools.BundledAzureLogin.Replace("'", "''")}'"
                    : "aws-azure-login"));
            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoExit -ExecutionPolicy Bypass -File \"{file}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, "Set up", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
