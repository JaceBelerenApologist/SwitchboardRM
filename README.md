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
- Microsoft Entra ID sign-in through [aws-azure-login](https://github.com/aws-azure-login/aws-azure-login), or AWS
  IAM Identity Center (SSO) profiles.
- Sign-in status with time remaining, optional background renewal ("Keep me signed in"), paused while Windows is
  locked or asleep.
- Asks Entra for each profile's own account (`login_hint`), so a separate admin account isn't swapped for the
  account signed in to Windows.
- Signs you in automatically when you connect while signed out; optionally signs out of AWS when the app closes,
  after ending all open sessions.
- Add and remove AWS profiles from the app.

**Everything else**
- RDP passwords saved in Windows Credential Manager (never in files).
- Light and dark themes, splash screen, settings window, About page with bundled tool versions.
- Ships as one self-contained `.exe` plus a `tools\` folder: nothing to install on the target PC.

## Requirements

**To run the published app**
- Windows 10 or 11, x64.
- The Microsoft Edge WebView2 Runtime, for the SSH terminal only (included with Windows 10/11).
- The OpenSSH Client, for SSH only (Windows Settings > Apps > Optional features), unless bundled in `tools\openssh\`.
- Node.js, aws-azure-login, the Session Manager plugin and the AWS CLI are *not* needed if they were bundled
  into `tools\` (see [Bundling the helper tools](#bundling-the-helper-tools)).

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

## Getting started

```powershell
git clone <this repository>
cd SwitchboardRM

# optional but recommended: bundle the helper tools from this PC (see below)
powershell -ExecutionPolicy Bypass -File .\collect-tools.ps1

# build the finished app into .\output
.\publish.cmd
```

`output\` then holds everything needed to run it:

```
output\
  SwitchboardRM.exe          the app, the AWS SDK and the .NET runtime in one file
  tools\                     the bundled helpers, if collected
```

Copy `output\` anywhere and run `SwitchboardRM.exe`. On first start, use **Add profile** (in the profile list,
Settings, or the AWS menu) to set up an AWS profile, then **Sign in**. Existing profiles in `~/.aws` are picked up
automatically.

For development, open the folder in Visual Studio or VS Code and run it (`dotnet run`). Debug builds use the tools
straight from the project's `tools\` folder.

## Bundling the helper tools

`collect-tools.ps1` copies the helpers installed on the current PC into `tools\`, one subfolder each, with their
licence files. Publishing mirrors `tools\` into `output\tools\`, and the app uses those copies before anything
installed on the PC.

| Folder | Contents | Needed for |
|---|---|---|
| `tools\node\` | `node.exe` | running aws-azure-login |
| `tools\aws-azure-login\` | the npm package and its `node_modules` | Microsoft Entra sign-in |
| `tools\session-manager-plugin\` | `session-manager-plugin.exe` | every AWS connection |
| `tools\aws-cli\` | AWS CLI v2 | SSO profiles only |
| `tools\chromium\` | Chromium, with `-IncludeChromium` | optional browser for the sign-in page |
| `tools\openssh\` | an OpenSSH build, added by hand | optional; otherwise Windows' OpenSSH is used |

Run the script on a PC where Node.js (`npm install -g aws-azure-login`), the Session Manager plugin and the AWS CLI
are installed and working, and re-run it after updating any of them.

## Usage notes

- **Profile list** (toolbar): the AWS profile to use. "Add profile ..." at the bottom sets up a new one.
- **Sign in** gets AWS access through the Microsoft sign-in page; the status next to it shows the time left.
- **Keep me signed in** (toolbar checkbox): checks the sign-in every few minutes and renews it in the background.
  Off: nothing runs in the background, and you're signed in when you connect.
- **Double-click** a server to connect. Close tabs with ×, middle-click or right-click.
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

## How it works

| Part | Files |
|---|---|
| Main window, list, tabs, connect flow | `MainForm.cs` |
| AWS SDK calls, inventory, Session Manager tunnel, profiles | `AwsServices.cs` |
| Remote Desktop (ActiveX MsRdpClient) host | `RdpHost.cs` |
| SSH terminal: WebView2 + xterm.js, ConPTY | `TerminalHost.cs`, `PseudoConsole.cs`, `Assets\terminal\` |
| Saved connections and import | `Connections.cs`, `ConnectionImport.cs` |
| Sign-in helpers (aws-azure-login, SSO, login hint) | `SignInSetup.cs`, `ProfileSetup.cs`, `Tools.cs`, `Assets\login-hint-shim.js` |
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
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) | see package | SSH terminal host (NuGet) |
| [xterm.js](https://github.com/xtermjs/xterm.js) 5.5.0 + fit add-on 0.10.0 | MIT | SSH terminal (in `Assets\terminal\`) |
| [aws-azure-login](https://github.com/aws-azure-login/aws-azure-login) | MIT | Entra sign-in (bundled by `collect-tools.ps1`) |
| [Node.js](https://nodejs.org) | MIT | runs aws-azure-login (bundled) |
| [Session Manager plugin](https://github.com/aws/session-manager-plugin) | Apache 2.0 | tunnels (bundled) |
| [AWS CLI v2](https://github.com/aws/aws-cli) | Apache 2.0 | SSO sign-in (bundled) |

The bundled tools' licence files are copied into `tools\` alongside them.

## License

Released under the [MIT License](LICENSE). Copyright © 2026 Nick Morrissey.
