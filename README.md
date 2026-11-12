<p align="center">
  <img src="Assets/app.png" alt="Switchboard Remote Manager" width="112">
</p>

<h1 align="center">Switchboard Remote Manager</h1>

<p align="center">
  Remote Desktop and SSH in tabs, to your AWS servers through Systems Manager and to any other machine directly.
  <br>
  A single self-contained Windows app: no AWS console, no open inbound ports, no VPN.
</p>

---

Switchboard RM lists your EC2 instances across the AWS regions you choose and opens each one in a tab, over a
hidden [AWS Systems Manager Session Manager](https://docs.aws.amazon.com/systems-manager/latest/userguide/session-manager.html)
tunnel. Windows servers open in Remote Desktop, Linux servers in an embedded SSH terminal. It also keeps your
ordinary RDP and SSH connections (or imports them from RDCMan, mRemoteNG, `.rdp` files and PuTTY) in the same place.

<!-- Screenshots: add a couple here, e.g. ![Main window](docs/main-window.png) -->

## Features

**Connecting**
- Remote Desktop and SSH sessions in tabs, side by side.
- AWS servers are reached through Session Manager port forwarding (3389 for RDP, 22 for SSH), so they need the SSM
  agent but no public IP, inbound security-group rules or bastion host.
- The remote desktop resizes with the window (dynamic resolution on Windows Server 2012 R2 and newer).
- Embedded SSH terminal: Windows' own OpenSSH client in a pseudo-console, shown with xterm.js. Your `~/.ssh` keys,
  the Windows SSH agent, `~/.ssh/config` and `known_hosts` work as usual. Host keys for AWS servers are remembered by
  instance ID, so the tunnel's changing local port never triggers host-key warnings.
- Linux servers default to SSH and Windows servers to RDP; Server > Connect with SSH / Remote Desktop overrides it.

- Terminals can be docked in a resizable panel under the tabs, next to a remote desktop.
- Windows sign-in windows (WAM) appear for commands like `Connect-MgGraph` in the terminals, using Microsoft's ConPTY
  bundled in `tools\conpty`.
- New EC2 servers from the app (AWS > New server, or "New server like this one" from an existing server): a wizard
  for the image (current AWS Windows/Linux or your own), instance type with its on-demand price, disks, full network
  control (specific private IPs, secondary IPs, IPv6, several network interfaces, tenancy, placement group, hostname),
  instance profile (Session Manager checked), protections, metadata and user data, with a costed review before launch.
- Minimize or close to the system tray (Settings > Appearance), keeping sessions and sign-in renewal running.
- Updates itself: a daily check of GitHub releases, and a verified, silent update for installed copies. Turn it off
  in Settings > About, or for everyone with `DisableUpdateCheck = 1` (DWORD) under
  `HKLM\SOFTWARE\Policies\SwitchboardRM`.
- Claude in the shell menus: Claude Code (Anthropic's command-line agent) in a terminal tab, with an installer if
  it's missing, and claude.ai in a tab, signed in with your own account.
- Local shells in tabs too: Command Prompt, Windows PowerShell, PowerShell 7, Git Bash and WSL (when installed),
  with `AWS_PROFILE` and the bundled AWS tools already set up.

**AWS servers**
- Lists every EC2 instance (except terminated ones) and anything else Systems Manager manages, across chosen regions.
- Live status: Online, Agent offline, Starting, Stopping, Stopped, Running without SSM.
- Start, stop and reboot instances from the toolbar, the Server menu or the right-click menu, with confirmation
  and live status tracking. Double-clicking a stopped server offers to start it and connects when it's ready.
- Filters: search, Windows only, Hide offline. Movable and hideable columns.

**Saved connections**
- Plain RDP or SSH connections to any machine, with optional groups, kept on their own tab.
- Reachability check on each refresh.
- Import from RDCMan (`.rdg`), Remote Desktop files (`.rdp`), mRemoteNG (`confCons.xml`) and PuTTY saved sessions.

**Sign-in**
- Microsoft Entra ID sign-in built in: a sign-in window on Microsoft's WebView2 engine (kept up to date by Windows),
  which signs in with the profile's account and gets temporary AWS keys through STS. Or AWS IAM Identity Center (SSO)
  profiles.
- Profiles set up and edited in the app (Add / Edit profile): tenant, AWS app, account, region, session length and
  default role, with Check sign-in to list the account's AWS roles.
- Sign-in status with time remaining, optional background renewal ("Keep me signed in"), paused while Windows is
  locked or asleep.
- Asks Entra for each profile's own account (`login_hint`), so a separate admin account isn't swapped for the
  account signed in to Windows.
- Signs you in automatically when you connect while signed out; optionally signs out of AWS when the app closes,
  after ending all open sessions.
- Add and remove AWS profiles from the app.

**Everything else**
- RDP passwords saved in Windows Credential Manager (never in files).
- Customisable look: light, dark or match Windows; any accent colour (presets, the Windows accent or a custom
  colour); app font, server list text size, and terminal font and size.
- Terminal colour schemes (Dracula, Nord, Solarized, One Half, Gruvbox, Tokyo Night, Campbell), which also colour
  PowerShell's prompt, file lists and syntax to match, without touching your profile.
- Extra fonts without installing them: put `.ttf` / `.otf` files in a `fonts` folder next to `SwitchboardRM.exe`
  (in the project: `fonts\`, copied on publish). They're offered in Settings > Appearance for the app and terminals.
- Splash screen, settings window, About page with bundled tool versions.
- Ships as one self-contained `.exe` plus a `tools\` folder: nothing to install on the target PC.

## Requirements

**To run the published app**
- Windows 10 or 11, x64.
- The Microsoft Edge WebView2 Runtime, for Microsoft sign-in and the terminals (included with Windows 10/11).
- The OpenSSH Client, for SSH only (Windows Settings > Apps > Optional features), unless bundled in `tools\openssh\`.
- The Session Manager plugin and the AWS CLI: included in the release download, or installed yourself (see
  [Installation](#installation)).

**To build**
- The [.NET 10 SDK](https://dotnet.microsoft.com/download).
- Visual Studio 2022+ or VS Code, optional.

**On the AWS side**
- Instances with the SSM agent installed and registered (online in Fleet Manager).
- For SSH to AWS servers: an SSH server on the instance and a key or account to log in with.
- IAM permissions for the signed-in role:

| Permission | Used for |
|---|---|
| `ec2:DescribeInstances` | the server list |
| `ssm:DescribeInstanceInformation` | Systems Manager status |
| `ssm:StartSession` (on the instances and the `AWS-StartPortForwardingSession` document) | connecting |
| `ssm:TerminateSession` | ending sessions cleanly when a tab or the app closes |
| `ec2:StartInstances`, `ec2:StopInstances`, `ec2:RebootInstances` | optional: start / stop / reboot |

## Installation

There are two ways to get Switchboard running: download a release, which includes everything, or build it yourself
and install the helper tools it uses.

### Option A: download a release (helper tools included)

1. Download `SwitchboardRM-<version>.zip` from [Releases](../../releases) and extract it anywhere, for example
   `C:\Tools\SwitchboardRM`. Keep the `tools` folder next to `SwitchboardRM.exe`.
2. Run `SwitchboardRM.exe`. Releases are code-signed with an organisational certificate; on PCs that don't trust it,
   Windows SmartScreen may ask you to confirm the first time (**More info > Run anyway**).
3. Set up an AWS profile and sign in (see [First-time sign-in setup](#first-time-sign-in-setup)).

The release bundles the Session Manager plugin, the AWS CLI and Microsoft ConPTY in `tools\`, so nothing else needs
installing. What's included, with versions and licences, is listed in `tools\THIRD-PARTY-NOTICES.txt`.

### Option B: build it and install the helper tools yourself

**1. Build Switchboard**

```powershell
git clone <this repository>
cd SwitchboardRM
.\publish.cmd          # needs the .NET 10 SDK; the app lands in .\output
```

**2. Install the helper tools.** Switchboard finds installed copies on its own (on `PATH` or in their default folders).

| Tool | Needed for | How to install |
|---|---|---|
| AWS Session Manager plugin | every AWS connection | Install it with the Windows installer from AWS: [Install the Session Manager plugin](https://docs.aws.amazon.com/systems-manager/latest/userguide/install-plugin-windows.html). Check with `session-manager-plugin --version`. |
| AWS CLI v2 | IAM Identity Center (SSO) profiles, and `aws` in Switchboard's shells | Install with the Windows installer: [Install the AWS CLI](https://docs.aws.amazon.com/cli/latest/userguide/getting-started-install.html). |
| OpenSSH Client | SSH sessions only | Usually already present. If not: Windows Settings > System > Optional features > OpenSSH Client, or `Add-WindowsCapability -Online -Name OpenSSH.Client~~~~0.0.1.0` in an admin PowerShell. |
| WebView2 Runtime | Microsoft sign-in and the terminals | Included with Windows 10/11. If missing, install the Evergreen runtime from Microsoft's WebView2 page. |

**3. Run** `output\SwitchboardRM.exe` and continue with the first-time sign-in setup below.

For development, open the folder in Visual Studio or VS Code and run it (`dotnet run`).

### First-time sign-in setup

Existing profiles in `~/.aws` are picked up automatically. To add one, use **Add profile** (bottom of the profile
list on the toolbar, Settings > AWS profile, or the AWS menu); **Edit profile** changes an existing one. Everything is
set up in the app:

- **Microsoft sign-in (Entra ID)**:

  | Field | What to enter |
  |---|---|
  | Tenant ID | your organisation's Microsoft Entra tenant ID (filled in if another profile on the PC already has one) |
  | App ID URI | the AWS app's identifier in Entra (Enterprise applications > the AWS app > Single sign-on) |
  | Account | the account to sign in with, so a separate admin account isn't swapped for the one signed in to Windows |
  | AWS region | where your servers are |
  | Session length | how long a sign-in lasts, up to the maximum your AWS role allows |
  | Default AWS role | only needed if your account has several roles: **Check sign-in** signs in and lists them |
  | Stay signed in | on, so Switchboard can renew the sign-in in the background |

- **AWS IAM Identity Center (SSO)**: start URL, SSO region, account ID, role (permission set) and AWS region.

The settings are saved in `~/.aws/config` under the standard names (for Microsoft sign-in, the same ones
aws-azure-login uses), so other AWS tools can use the profiles too. Then click **Sign in**.

## Usage notes

- **Profile list** (toolbar): the AWS profile to use. "Add profile ..." at the bottom sets up a new one.
- **Sign in** gets AWS access through the Microsoft sign-in page; **Sign out** removes it again (open tabs keep
  running). The status next to them shows the time left.
- **Keep me signed in** (toolbar checkbox): checks the sign-in every few minutes and renews it in the background.
  Off: nothing runs in the background, and you're signed in when you connect.
- **Double-click** a server to connect. Close tabs with ×, middle-click or right-click.
- **Ctrl+T** (or the terminal button, or the + after the last tab) opens a local shell; Settings > Terminal & SSH
  sets which one.
- **Ctrl+Alt+Right / Ctrl+Alt+Left** move to the next / previous tab from anywhere, even inside a Remote Desktop
  session; **Ctrl+Tab / Ctrl+Shift+Tab** do the same outside Remote Desktop. **Ctrl+Shift+W** closes the current
  tab (after asking), and **Ctrl+Shift+T** opens a shell from inside a terminal.
- **Dock to bottom** (right-click a shell or SSH tab) puts a terminal in a panel under the tabs, beside whatever
  tab is in front; drag the bar to resize it. **Ctrl+`** shows or hides the panel.
- **Ctrl+1 / Ctrl+2** switch between the AWS servers and Saved tabs; **Ctrl+L** hides the server list; **F5**
  refreshes; **Ctrl+,** opens Settings.
- In SSH tabs: select text (or Ctrl+Shift+C) to copy, Ctrl+V or right-click to paste, Enter to reconnect after a
  session ends.

## Data and security

| What | Where |
|---|---|
| Settings | `%APPDATA%\SwitchboardRM\settings.json` |
| Saved connections | `%APPDATA%\SwitchboardRM\connections.json` |
| RDP passwords | Windows Credential Manager, `SwitchboardRM:<user>` (encrypted with your Windows sign-in) |
| AWS profiles and temporary keys | `~/.aws/config` and `~/.aws/credentials`, as used by the AWS CLI |
| Logs | `%LOCALAPPDATA%\SwitchboardRM\logs` (File > Open log folder) |

- No passwords, keys or account details are stored in the repository or in the app's own files.
- AWS sessions go through Session Manager: access is controlled by IAM, and nothing needs to be exposed to the
  internet. SSH through the tunnel is end-to-end encrypted, so Session Manager logging records that a session
  happened but not its contents.
- Direct (saved) RDP connections verify the server certificate and warn if it can't be verified, like `mstsc`.
- Removing a profile backs up `~/.aws/config` and `~/.aws/credentials` first (`*.switchboard-backup`).

## Installer

Each release has `SwitchboardRM-Setup-<version>.exe` as well as the zip. Run it and choose **Install for me only**
(no admin rights; installs to `%LocalAppData%\Programs\Switchboard Remote Manager`) or **Install for all users**
(asks for admin; installs to `Program Files`). A newer setup upgrades an installed version in place; uninstalling
(Apps & features) keeps your settings in `%APPDATA%\SwitchboardRM`.

For deployment tools such as Intune (as a Win32 app):

```
SwitchboardRM-Setup-x.y.z.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /ALLUSERS      (or /CURRENTUSER)
"C:\Program Files\Switchboard Remote Manager\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

The installer is defined in `installer\SwitchboardRM.iss` (Inno Setup 6). To build one locally from the current
`output\` (unsigned), run `publish.cmd`, then `build-installer.ps1`.

## Automated builds

`.github/workflows/build.yml` builds Switchboard on GitHub's Windows machines. The version number decides releases:

- **Push to `main`:** a full build with the helper tools bundled (checked before zipping). If `<Version>` in the
  `.csproj` hasn't been released yet, it publishes the
  GitHub Release `vX.Y.Z` (creating the tag) with the installer, the zip and that version's CHANGELOG notes. An existing release is
  never replaced: bump the version to publish a new one. A build that doesn't publish a release is kept with its run
  for 7 days instead (Actions tab > the run > Artifacts); one that does keeps no separate copy, since the release
  holds the same files.
- **Pull request:** compile only.
- **Run by hand** (Actions tab > Build > Run workflow): a full build kept with the run; tick **Publish the release**
  to also release the current version, if it isn't released yet.

**Code signing:** builds of `main` run in the repository's `release` environment (Settings > Environments, limited to
`main`), whose secrets `SIGNING_PFX_BASE64` (the PFX, base64) and `SIGNING_PFX_PASSWORD` hold the code-signing
certificate. `SwitchboardRM.exe`, the installer and its uninstaller are signed (SHA-256, timestamped); a build of `main` without those
secrets fails rather than publish unsigned. Other builds aren't signed. To renew, export the new certificate's PFX
and replace the two secrets.

So the normal release routine is: bump the version and changelog, then run `git-release.ps1`, which commits and
pushes (and refuses a version that's already released).

## Building a release with bundled tools

For maintainers who publish the ready-to-run download (Option A).

1. On the build PC, install the helper tools as in [Option B](#option-b-build-it-and-install-the-helper-tools-yourself).
2. Collect them, with their licences, into `tools\`:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\collect-tools.ps1
   ```

   | File or folder | Contents |
   |---|---|
   | `tools\THIRD-PARTY-NOTICES.txt` | every bundled tool with its version, licence and source, and where its licence files are |
   | `tools\session-manager-plugin\` | the plugin with its `LICENSE` and `NOTICE` |
   | `tools\aws-cli\` | AWS CLI v2 with its licence files |
   | `tools\conpty\` | Microsoft ConPTY (`conpty.dll`, `OpenConsole.exe`) from NuGet, with its licence |
   | `tools\openssh\` | only if you put an OpenSSH build there by hand; the script leaves it as is |

   Where a tool's installation has no licence file, the script writes a `LICENSE-NOTE.txt` naming the licence and
   where to read it.
3. Run `.\publish.cmd`. `output\` then holds `SwitchboardRM.exe` and `tools\`.
4. Commit, tag and push with `git-release.ps1` (it reads the version from the `.csproj`, uses that version's
   [CHANGELOG.md](CHANGELOG.md) notes, and prints the link for the release):

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\git-release.ps1     # -DryRun to preview, -Version X.Y.Z to choose
   ```
5. Zip `output\` as `SwitchboardRM-<version>.zip` and attach it to that GitHub Release. Don't commit `tools\` or
   `output\` to the repository.

### Keeping the bundled tools up to date

People who download a release run the tool versions you collected, so refresh them regularly.

`update-tools.ps1` does the checking and updating for you, then refreshes `tools\`:

```powershell
powershell -ExecutionPolicy Bypass -File .\update-tools.ps1              # check, update, refresh tools\
powershell -ExecutionPolicy Bypass -File .\update-tools.ps1 -CheckOnly   # only report what's out of date
```

It updates the Session Manager plugin and the AWS CLI with winget, then runs `collect-tools.ps1` (which also fetches
the current Microsoft ConPTY).
Publish afterwards and release it as a new PATCH version. Settings > About in the app lists the bundled versions.

To do it by hand instead: install the newer Session Manager plugin and AWS CLI installers, then re-run
`collect-tools.ps1`.

### Licences of the bundled tools

All of the bundled tools are open source under permissive licences that allow redistribution, provided their licence
files go with them: the Session Manager plugin and AWS CLI (Apache 2.0) and Microsoft ConPTY (MIT).
`collect-tools.ps1` puts the licence files in each tool's folder and writes the notices.

## How it works

| Part | Files |
|---|---|
| Main window, list, tabs, connect flow | `MainForm.cs` |
| AWS SDK calls, inventory, Session Manager tunnel, profiles | `AwsServices.cs` |
| Remote Desktop (ActiveX MsRdpClient) host | `RdpHost.cs` |
| SSH terminal: WebView2 + xterm.js, ConPTY | `TerminalHost.cs`, `PseudoConsole.cs`, `Assets\terminal\` |
| Saved connections and import | `Connections.cs`, `ConnectionImport.cs` |
| Microsoft sign-in (WebView2 sign-in window, SAML, STS) | `EntraSignIn.cs` |
| Profiles: add, edit, remove (Microsoft and SSO forms) | `ProfileSetup.cs`, `ProfileForms.cs` |
| Bundled tools | `Tools.cs` |
| Settings, theme, splash | `SettingsForm.cs`, `Theme.cs`, `SplashForm.cs` |
| Credential Manager | `CredentialStore.cs` |

The app starts sessions with the AWS SDK for .NET and hands the stream to Amazon's `session-manager-plugin`, which
forwards a local port to 3389 or 22 on the instance. Remote Desktop connects to that port with the built-in RDP
ActiveX control; SSH runs `ssh.exe` against it inside a Windows pseudo-console.

## Versioning

Versions follow MAJOR.MINOR.PATCH: PATCH for fixes, MINOR for new features or behaviour changes, MAJOR for changes
that break existing setups. The version is set in `SwitchboardRM.csproj`, and every release has an entry in
[CHANGELOG.md](CHANGELOG.md), which the app shows under Help > About > What's new.

## Third-party components

| Component | Licence | Use |
|---|---|---|
| [AWS SDK for .NET](https://github.com/aws/aws-sdk-net) | Apache 2.0 | AWS calls (NuGet) |
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) | see package | Microsoft sign-in window and terminals (NuGet) |
| [xterm.js](https://github.com/xtermjs/xterm.js) 5.5.0 + fit add-on 0.10.0 | MIT | SSH terminal (in `Assets\terminal\`) |
| [Microsoft ConPTY](https://github.com/microsoft/terminal) | MIT | terminals, so Windows sign-in windows (WAM) appear (bundled) |
| [Session Manager plugin](https://github.com/aws/session-manager-plugin) | Apache 2.0 | tunnels (bundled) |
| [AWS CLI v2](https://github.com/aws/aws-cli) | Apache 2.0 | SSO sign-in (bundled) |

The bundled tools' licence files and notices are in `tools\` (see
[Licences of the bundled tools](#licences-of-the-bundled-tools)).

## License

Released under the [MIT License](LICENSE). Copyright © 2026 Nick Morrissey.
