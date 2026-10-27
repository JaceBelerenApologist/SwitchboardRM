using System.Diagnostics;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace SwitchboardRM;

/// <summary>Adding a new AWS profile: ask for a name and sign-in type, then open that type's setup window.</summary>
internal static class ProfileSetup
{
    /// <summary>Show the Add profile dialog and start the setup. Returns the new profile's name, or null if cancelled.</summary>
    public static string Add(IWin32Window owner)
    {
        using var f = new AddProfileForm(Aws.ListProfiles());
        if (f.ShowDialog(owner) != DialogResult.OK) return null;

        if (f.Sso) RunSsoSetup(f.ProfileName, owner);
        else SignInSetup.Run(f.ProfileName, owner);
        Log.Write($"Adding AWS profile {f.ProfileName} ({(f.Sso ? "SSO" : "Microsoft sign-in")})");
        return f.ProfileName;
    }

    /// <summary>Confirm, then delete the profile from this PC's AWS files. True if it was removed.</summary>
    public static bool Remove(IWin32Window owner, string profile)
    {
        if (string.IsNullOrWhiteSpace(profile)) return false;
        var answer = MessageBox.Show(owner,
            $"Remove the AWS profile \"{profile}\" from this PC?\n\n" +
            "This deletes its settings from ~/.aws/config and its keys from ~/.aws/credentials (backups of both are " +
            "saved as *.switchboard-backup). Other tools on this PC that use it, such as the AWS CLI, lose it too.\n\n" +
            "Nothing in AWS itself changes, and you can add the profile again later.",
            "Remove profile", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return false;

        try
        {
            int sections = Aws.RemoveProfile(profile);
            Log.Write($"Removed AWS profile {profile} ({sections} section(s))");
            if (sections == 0)
                MessageBox.Show(owner, $"\"{profile}\" wasn't in your AWS files, so there was nothing to remove.",
                    "Remove profile", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("Remove profile failed: " + ex);
            MessageBox.Show(owner, "Couldn't remove the profile: " + ex.Message, "Remove profile",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    /// <summary>"aws configure sso" for the profile, in a console window (it asks its questions there).</summary>
    private static void RunSsoSetup(string profile, IWin32Window owner)
    {
        try
        {
            string aws = Tools.AwsCli;
            string exe = aws.Contains('\\') ? $"\"{aws}\"" : aws;
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/k echo Setting up AWS IAM Identity Center (SSO) for the profile \"{profile}\". " +
                $"When it's done, close this window and click Sign in in Switchboard. && echo. && " +
                $"{exe} configure sso --profile {profile}")
            { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "Couldn't start the AWS CLI's SSO setup: " + ex.Message, "Add profile",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

/// <summary>Name and sign-in type for a new AWS profile.</summary>
internal sealed class AddProfileForm : Form
{
    public string ProfileName => _txtName.Text.Trim();
    public bool Sso => _cboType.SelectedIndex == 1;

    private readonly TextBox _txtName = new() { Width = 340, PlaceholderText = "e.g. Production, Dev" };
    private readonly ComboBox _cboType = new() { Width = 340, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _lblError = new() { AutoSize = true, Tag = "accent", Margin = new Padding(0, 8, 0, 0) };
    private readonly Label _lblNote = new()
    {
        AutoSize = true, MaximumSize = new Size(340, 0), Tag = "muted", Margin = new Padding(0, 8, 0, 0),
    };
    private readonly HashSet<string> _existing;

    public AddProfileForm(IEnumerable<string> existing)
    {
        _existing = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        Text = "Add AWS profile";
        if (AppIcon.Icon != null) Icon = AppIcon.Icon;
        Font = new Font("Segoe UI", 9.5F);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        _cboType.Items.AddRange(new object[] { "Microsoft sign-in (aws-azure-login)", "AWS IAM Identity Center (SSO)" });
        _cboType.SelectedIndex = 0;
        _cboType.SelectedIndexChanged += (_, _) => UpdateNote();
        UpdateNote();

        var ok = new Button { Text = "Add and set up", Tag = "primary", AutoSize = true, MinimumSize = new Size(120, 32), Margin = new Padding(0, 0, 8, 0) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, MinimumSize = new Size(96, 32), Margin = new Padding(0), DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => TryAdd();
        AcceptButton = ok;
        CancelButton = cancel;

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        buttons.Controls.AddRange(new Control[] { ok, cancel });

        var body = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Padding = new Padding(22, 18, 22, 18),
        };
        body.Controls.AddRange(new Control[]
        {
            new Label { Text = "Profile name", AutoSize = true, Margin = new Padding(0, 0, 0, 3) }, _txtName,
            new Label { Text = "Sign-in type", AutoSize = true, Margin = new Padding(0, 10, 0, 3) }, _cboType,
            _lblNote, _lblError, buttons,
        });
        Controls.Add(body);
        Theme.Apply(this, Theme.Current.IsDark);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.ApplyTitleBar(this);
    }

    private void UpdateNote() => _lblNote.Text = Sso
        ? "Opens the AWS CLI's SSO setup in a console window. Have your SSO start URL and region ready."
        : "Opens a setup window for aws-azure-login with the answers to type shown. Have your Azure tenant ID " +
          "and app ID URI ready (the same ones as your existing profile).";

    private void TryAdd()
    {
        string name = ProfileName;
        if (name.Length == 0) { _lblError.Text = "Type a name for the profile."; return; }
        if (!Regex.IsMatch(name, @"^[A-Za-z0-9._-]+$"))
        {
            _lblError.Text = "Use letters, numbers, dots, dashes and underscores only (no spaces).";
            return;
        }
        if (_existing.Contains(name))
        {
            _lblError.Text = $"There's already a profile called {name}. Pick it from the list instead.";
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
