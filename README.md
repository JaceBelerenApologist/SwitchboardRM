<p align="center">
  <img src="Assets/app.png" alt="Switchboard Remote Manager" width="112">
</p>

<h1 align="center">Switchboard Remote Manager</h1>

<p align="center">
  All your servers in one window: Remote Desktop, SSH, local shells and Claude, side by side in tabs.
  <br>
  To AWS servers through Systems Manager (no open ports, no VPN) and to any other machine directly.
</p>

---

Switchboard lists the EC2 servers in the AWS regions you choose, plus your own saved Remote Desktop and SSH
connections, and opens each one in a tab. AWS servers are reached through hidden
[Session Manager](https://docs.aws.amazon.com/systems-manager/latest/userguide/session-manager.html) tunnels, so they
need no inbound ports. Beyond connecting, it can create servers, change their size and disks, take images and
snapshots, run commands across several at once, check their patches, and look at a server's screen when nothing else
can reach it, without opening the AWS console.

<!-- Screenshots: add a couple here, e.g. ![Main window](docs/main-window.png) -->

## Features

**Connecting**
- Remote Desktop, SSH and VNC in tabs. Windows servers open in Remote Desktop, Linux in an embedded SSH terminal;
  VNC (screen sharing) for saved connections, and for AWS servers from their right-click menu.
- AWS servers through Session Manager; any other machine directly (saved connections, or a one-off "connect to a
  computer"). Import connections from RDCMan, mRemoteNG, `.rdp` files and PuTTY.
- **Connect as**: another user for one connection, in a tab of its own.
- **Port forwarding** through a server: any port on it (SQL Server, HTTPS, WinRM ...) or on a host behind it (an RDS
  database, an internal site), as `localhost:<port>` on your PC.
- A lost connection (network change, sleep, a server restart) **reconnects in the same tab**; Windows keeps the
  session, so you land back where you were. Tabs can also be reconnected from their right-click menu.
- **Send Ctrl+Alt+Del** to a Remote Desktop session from its tab's menu (or Ctrl+Alt+End inside it).

**Tabs and terminals**
- Local shells in tabs: Command Prompt, Windows PowerShell, PowerShell 7, Git Bash and WSL distributions, each with
  its own icon. **Claude Code** in a terminal tab (with an installer if it's missing) and **claude.ai** in a tab.
- Terminals can be docked in a panel under the tabs, beside whatever tab is in front.
- Tabs can be dragged into a new order. Remote tabs show where they connect: **AWS**, **RDP** or **SSH**.
- **Your tabs and layout come back** next time: tabs reopen and reconnect, and the window, columns and panels are as
  you left them. Optionally, terminals show their previous output too.

**Managing AWS servers** (right-click a server > Manage server)
- Overview: details, disks, AWS status checks and scheduled events (maintenance, retirement).
- Screen and log: a live console screenshot and the system log, even when the server can't be reached.
- Name and tags; termination and stop protection; instance type (with prices); disks (grow, with the partition
  extended inside Windows or Linux; add; detach); network (security groups, secondary IPs, instance profile); images
  and snapshots (create, and attach a copy of a snapshot to recover files); Run command (on several servers at once);
  patches and the Systems Manager agent (scan, install, installed applications).
- **Volumes and snapshots** (AWS menu): every EBS volume and snapshot in your regions, with costs, filters and cleanup
  of unattached volumes and orphaned snapshots; create several disks at once for a server (each its own size and
  performance, encrypted with your KMS key, tagged and attached), modify, attach, detach, snapshot, copy and delete.
- **New server** wizard: AWS's current Windows and Linux images or your own, instance type with on-demand prices, data
  disks, full network control (specific private IPs, several network interfaces, IPv6), protections, metadata and
  user data, and a costed review before launch. "New server like this one" copies an existing server's settings.
- Start, stop and reboot (several servers at once), and terminate (with a typed confirmation).

**Signing in to AWS**
- Built-in **Microsoft Entra ID** sign-in (SAML): no browser extensions or extra tools. A separate admin account can be
  used without affecting the account signed in to Windows. Background renewal keeps you signed in.
- **AWS IAM Identity Center (SSO)** profiles, through the AWS CLI.
- Profiles are set up in the app and saved in `~/.aws/config`, where other AWS tools can use them too.

**Everyday comfort**
- Light and dark themes, an accent colour, your own app, server-list and terminal fonts (including fonts dropped into
  a `fonts` folder), and terminal colour schemes.
- Minimize or close to the system tray; one copy at a time (optional); formatted server tooltips.
- Updates itself from GitHub releases (a verified, silent update), unless turned off.

## Requirements

**To run**
- Windows 10 (1809 or later) or Windows 11, x64.
- The Microsoft Edge WebView2 Runtime (included with Windows 10 and 11).
- The OpenSSH Client, for SSH (included with Windows 10 and 11; Settings > System > Optional features if not).
- Everything else (the .NET runtime, the Session Manager plugin, the AWS CLI, Microsoft ConPTY) comes with the app.

**On the AWS side**
- Servers with the Systems Manager agent registered (online in Fleet Manager), and an instance profile allowing
  Session Manager (AWS's `AmazonSSMManagedInstanceCore` policy).
- For SSH to AWS servers: an SSH server on the instance and a key or account to log in with.
- Permissions for the signed-in role, depending on what you use:

| Feature | Permissions |
|---|---|
| Server list | `ec2:DescribeInstances`, `ssm:DescribeInstanceInformation` |
| Connecting, port forwarding | `ssm:StartSession` (instances and the `AWS-StartPortForwardingSession` / `AWS-StartPortForwardingSessionToRemoteHost` documents), `ssm:TerminateSession` |
| Start, stop, reboot, terminate | `ec2:StartInstances`, `ec2:StopInstances`, `ec2:RebootInstances`, `ec2:TerminateInstances` |
| Server window | `ec2:Describe*`, `ec2:GetConsoleScreenshot`, `ec2:GetConsoleOutput`, `ec2:CreateTags`, `ec2:DeleteTags`, `ec2:ModifyInstanceAttribute`, `ec2:Monitor/UnmonitorInstances`, `ec2:ModifyVolume`, `ec2:CreateVolume`, `ec2:AttachVolume`, `ec2:DetachVolume`, `ec2:ModifyNetworkInterfaceAttribute`, `ec2:Assign/UnassignPrivateIpAddresses`, `ec2:Associate/Replace/DisassociateIamInstanceProfile`, `ec2:CreateImage`, `ec2:CreateSnapshots`, `iam:PassRole` |
| Run command, patches, inventory | `ssm:SendCommand`, `ssm:GetCommandInvocation`, `ssm:DescribeInstancePatchStates`, `ssm:DescribeInstancePatches`, `ssm:ListInventoryEntries` |
| Volumes and snapshots | `ec2:DescribeVolumes`, `ec2:DescribeSnapshots`, `ec2:DescribeImages`, `ec2:CreateVolume`, `ec2:AttachVolume`, `ec2:DetachVolume`, `ec2:ModifyVolume`, `ec2:CreateSnapshot`, `ec2:CopySnapshot`, `ec2:DeleteVolume`, `ec2:DeleteSnapshot`, `ec2:CreateTags`, `ec2:DeleteTags`, `ec2:GetEbsDefaultKmsKeyId`, `kms:ListAliases` and `kms:DescribeKey` (to list keys), and use of the key for EBS (`kms:CreateGrant`, `kms:GenerateDataKeyWithoutPlaintext`, `kms:Decrypt`) |
| New server | `ec2:RunInstances`, `ec2:Describe*`, `ssm:GetParameters*` (AWS's public image list), `iam:ListInstanceProfiles`, `iam:ListAttachedRolePolicies`, `iam:PassRole`, `pricing:GetProducts` (prices; optional) |

Anything the role isn't allowed to do simply fails with AWS's message; the rest keeps working.

## Installation

Download from [Releases](../../releases):

- **`SwitchboardRM-Setup-<version>.exe`** (recommended): choose **Install for me only** (no admin rights) or
  **Install for all users** (Program Files). It adds a Start menu entry and an uninstaller, upgrades in place, and lets
  Switchboard update itself.
- **`SwitchboardRM-<version>.zip`**: extract anywhere and run `SwitchboardRM.exe`, keeping the `tools` folder beside
  it. Zip copies tell you about updates but don't install them.

Releases are code-signed with an organisational certificate; on PCs that don't trust it, Windows SmartScreen may ask
you to confirm the first time (**More info > Run anyway**).

For deployment tools such as Intune (as a Win32 app):

```
SwitchboardRM-Setup-x.y.z.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /ALLUSERS      (or /CURRENTUSER)
"C:\Program Files\Switchboard Remote Manager\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
```

To leave updates to your deployment tool, turn Switchboard's own update checks off with the DWORD
`DisableUpdateCheck = 1` under `HKLM\SOFTWARE\Policies\SwitchboardRM` (or `HKCU`).

### First-time sign-in setup

Existing profiles in `~/.aws` are picked up automatically. To add one, use **Add profile** (at the bottom of the
toolbar's profile list, in Settings > AWS profile, or on the AWS menu):

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

Then click **Sign in**.

## Using it

| Keys | Does |
|---|---|
| Double-click or Enter | Connect to the selected server |
| Ctrl+T | New shell (the default one; Settings > Terminal & SSH) |
| Ctrl+Alt+Right / Left | Next / previous tab, even inside Remote Desktop |
| Ctrl+Tab / Ctrl+Shift+Tab | Next / previous tab (outside Remote Desktop) |
| Ctrl+Shift+W | Close the current tab (asks first) |
| Ctrl+Shift+T | New shell, from inside a terminal |
| Ctrl+` | Show or hide the docked terminals |
| Ctrl+1 / Ctrl+2 | AWS servers / saved connections |
| Ctrl+L | Show or hide the server list |
| F5 | Refresh the server list |
| Ctrl+, | Settings |
| Ctrl+Alt+End | Ctrl+Alt+Del inside a Remote Desktop session |

- **Right-click a server** for Connect (and Connect with SSH / Remote Desktop / VNC), Connect as, Manage server, Port forwarding, New server like this one,
  Start / Stop / Reboot (on every selected server) and Terminate.
- **Right-click a tab** for Reconnect, Send Ctrl+Alt+Del, Move left / right, Dock to bottom and Close.
- **In terminals:** select text (or Ctrl+Shift+C) to copy, Ctrl+V or right-click to paste. When a session ends,
  Enter starts it again and Esc closes the tab.
- **SSH keys:** `ssh` uses your keys in `%USERPROFILE%\.ssh`, the Windows SSH agent, your `~/.ssh/config`, or one key
  file set in Settings > Terminal & SSH.

## Data and security

| What | Where |
|---|---|
| Settings, saved connections, tabs to reopen | `%APPDATA%\SwitchboardRM\` (`settings.json`, `connections.json`, `tabs.json`) |
| Remote Desktop passwords | Windows Credential Manager, `SwitchboardRM:<user>` (encrypted with your Windows sign-in); one-off and Connect as passwords only in memory |
| AWS profiles and temporary keys | `~/.aws/config` and `~/.aws/credentials`, as used by the AWS CLI |
| SSH keys and known hosts | `%USERPROFILE%\.ssh` (Switchboard only runs `ssh`) |
| Logs (rotated at 10 MB) | `%LOCALAPPDATA%\SwitchboardRM\logs` (File > Open log folder) |
| Browser data (terminals, sign-in, claude.ai), price cache, kept terminal output | `%LOCALAPPDATA%\SwitchboardRM\` |

- No passwords, keys or account details are stored in the repository or the app's files.
- AWS sessions go through Session Manager: access is controlled by IAM, and nothing is exposed to the internet. SSH
  through the tunnel is end-to-end encrypted.
- The Microsoft sign-in runs in its own private browser profile and never uses the account signed in to Windows.
- Direct Remote Desktop connections verify the server certificate and warn if it can't be verified, like `mstsc`.
- Updates are only installed if the download is signed by the same publisher as the running copy.
- Terminal output is only kept on disk if you turn that on (Settings > Terminal & SSH), and only until the next close.

## How this was built

Switchboard was **vibe coded with [Claude](https://claude.ai)**, Anthropic's AI assistant: the code, scripts and
documentation were written by Claude in conversation, from requirements, bug reports and screenshots. **Every change
was human-reviewed**: built, tested on real servers and used day to day by the maintainer before release. The
project notes for AI-assisted work, including its conventions and the lessons learned along the way, are in
[CLAUDE.md](CLAUDE.md).

## Building from source

With the [.NET 10 SDK](https://dotnet.microsoft.com/download), on Windows:

```powershell
git clone <this repository>
cd SwitchboardRM
powershell -ExecutionPolicy Bypass -File .\collect-tools.ps1   # the helper tools, from installed copies (see below)
.\publish.cmd                                                   # the app lands in .\output
```

`collect-tools.ps1` gathers the Session Manager plugin and AWS CLI (install them first with AWS's Windows installers)
and Microsoft ConPTY (from NuGet) into `tools\`, with their licences and `THIRD-PARTY-NOTICES.txt`.
`update-tools.ps1` updates the installed tools with winget and collects them again (`-CheckOnly` just reports).
For development, `dotnet run` uses the project's `tools\` folder in place. `build-installer.ps1` builds an unsigned
installer from `output\` (needs Inno Setup 6).

### Releases

`.github/workflows/build.yml` builds on GitHub's Windows machines:

- **Push to `main`:** a full build with the helper tools bundled and checked. If `<Version>` in the `.csproj` hasn't
  been released yet, it publishes GitHub Release `vX.Y.Z` with the signed installer, the zip and that version's
  [CHANGELOG.md](CHANGELOG.md) notes. Otherwise the build is kept with its run for 7 days.
- **Pull requests:** compile only.
- **Run by hand:** a full build kept with the run; tick **Publish the release** to release the current version.

Builds of `main` are code-signed in the repository's `release` environment, whose secrets `SIGNING_PFX_BASE64` and
`SIGNING_PFX_PASSWORD` hold the certificate; a build of `main` without them fails rather than publish unsigned.

The release routine: bump `<Version>`, add a CHANGELOG entry, then run `git-release.ps1`, which commits and pushes
(and refuses a version that's already released). Versions follow MAJOR.MINOR.PATCH: PATCH for fixes, MINOR for new
features or behaviour changes, MAJOR for changes that break existing setups. The app shows the changelog under
Settings > About > What's new.

## Third-party components

| Component | Licence | Use |
|---|---|---|
| [AWS SDK for .NET](https://github.com/aws/aws-sdk-net) | Apache 2.0 | AWS calls (NuGet) |
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) | see package | sign-in window, terminals, claude.ai (NuGet) |
| [xterm.js](https://github.com/xtermjs/xterm.js) 5.5.0 + fit add-on 0.10.0 | MIT | terminals (in `Assets\terminal\`) |
| [noVNC](https://github.com/novnc/noVNC) 1.5.0 (with pako) | MPL 2.0 (pako: MIT) | VNC tabs (in `Assets\vnc\novnc\`, built into the app) |
| [Microsoft ConPTY](https://github.com/microsoft/terminal) | MIT | terminals, so Windows sign-in windows appear (bundled) |
| [Session Manager plugin](https://github.com/aws/session-manager-plugin) | Apache 2.0 | tunnels (bundled) |
| [AWS CLI v2](https://github.com/aws/aws-cli) | Apache 2.0 | SSO sign-in, and `aws` in Switchboard's shells (bundled) |

The bundled tools' licence files and notices are in `tools\` and listed in `tools\THIRD-PARTY-NOTICES.txt`.

## License

Released under the [MIT License](LICENSE). Copyright © 2026 Nick Morrissey.
