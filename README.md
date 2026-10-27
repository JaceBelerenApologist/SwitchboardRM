# Switchboard Remote Manager (Switchboard RM)

Formerly "AWS RDP Launcher". Version 2.0 copies 1.x settings, saved connections and saved passwords across on
first start; the old copies are left in place.

Lists the EC2 instances Systems Manager (Fleet Manager) manages in ca-central-1 and ca-west-1,
and opens each one in a tab inside the app using a hidden Session Manager tunnel.

To build: the .NET 10 SDK. To run the published app: nothing, if `tools` was collected first
(see "Shipping it without installs" below); the .exe carries its own .NET runtime. Otherwise it uses installed copies of the AWS Session Manager
plugin and aws-azure-login (the "Set up..." step installs the latter through Node.js).
The AWS CLI is only needed for profiles that sign in with `aws sso login`.

## Run it
1. Unzip this folder, then in VS Code use File > Open Folder and pick it.
2. Terminal > New Terminal, type `dotnet run`, press Enter.
   The first build downloads the AWS SDK packages from NuGet, so it takes a little longer.

## Make the finished app
Double-click `publish.cmd` (or in Visual Studio: right-click the project > Publish > "Output",
or in a terminal: `dotnet publish -c Release`). Everything lands in `output\` next to the project:

    output\
      SwitchboardRM.exe          the app with the AWS SDK and .NET built in: nothing to install
      tools\
        node\                     node.exe
        aws-azure-login\          the package and its node_modules
        session-manager-plugin\   session-manager-plugin.exe
        aws-cli\                  AWS CLI v2
        chromium\                 only if collected with -IncludeChromium

Copy the whole `output` folder to any Windows 10/11 x64 PC and make a shortcut to the .exe.
Publishing again replaces the .exe and only copies tool files that changed.

## Using it
- Opens, checks your AWS sign-in, and loads the list. If you're signed out it starts the Microsoft sign-in.
  The right end of the toolbar shows who you're signed in as (or "Not signed in").
- Toolbar, left: the **list** icon shows or hides the server list (pressed = shown; also Ctrl+L) and the
  **refresh** icon (F5) reloads the servers without signing in again.
- Toolbar, after the divider: **AWS profile** picks which AWS account/profile to use ("Add profile ..." at the
  bottom of the list sets up a new one); **Sign in** gets fresh
  AWS access through the Microsoft sign-in page (lasts about an hour).
- Double-click (or press Enter on) a server to connect. Each server gets a tab.
- The remote desktop follows the window size: resize the window and the remote resolution changes to fit
  (Windows Server 2012 R2 or newer; older servers get a scaled picture instead).
- Close a tab with its × button, middle-click, or right-click > Close tab. The tunnel and the AWS session close with it.
- Hiding the server list lets the remote desktop take the space.
- The list shows every EC2 instance (except terminated ones) plus anything else Systems Manager manages.
  **Status**: Online (green, can connect), Agent offline (red: running but the SSM agent isn't answering),
  Starting / Stopping (amber), Stopped, or "Running, no SSM" (no agent, so it can't be reached from here).
  Tick **Hide offline** to show only servers you can connect to.
- **Connect**, **Start**, **Stop** and **Reboot** are on the toolbar, in the **Server** menu, and on the
  right-click menu; they act on the selected server and are only enabled when they make sense.
  Right-click a server to **Start**, **Stop** or **Reboot** it (needs ec2:StartInstances / StopInstances /
  RebootInstances), connect, or copy its ID or IP. The Status column follows the change live.
  Double-clicking a stopped server offers to start it and connects once it's online.
- **Saved RDP connections** (not AWS): the **+** button, Server > Add RDP connection, or right-click the list.
  They have their own **Saved RDP** tab next to **AWS servers** (Ctrl+1 / Ctrl+2), connect directly (no tunnel, no AWS sign-in), and their Status shows
  whether the RDP port answers. Each can sign in as its own user or the default one. They're stored in
  `%APPDATA%\SwitchboardRM\connections.json`, separate from the settings; passwords stay in Credential Manager.
- **SSH**: Linux servers open in an SSH terminal tab (Windows ones in Remote Desktop); Server > Connect with
  SSH / Remote Desktop overrides that. AWS servers are reached through the Session Manager tunnel to port 22,
  so they need sshd and your key but no open inbound ports. It runs Windows' own `ssh.exe` (Windows Settings >
  Apps > Optional features > OpenSSH Client) in an embedded terminal, so your `~/.ssh` keys, the Windows SSH
  agent and `known_hosts` work as usual; host keys for AWS servers are remembered by instance ID.
  Copy: select text (or Ctrl+Shift+C); paste: Ctrl+V or right-click. Press Enter in an ended session to reconnect.
  Settings > SSH: the user name for AWS servers (blank = guessed from the OS) and an optional key file.
  To ship a specific OpenSSH build, put it in `tools\openssh\`.
- **Import connections** (File or Server menu): from RDCMan `.rdg` files, `.rdp` files, mRemoteNG's
  `confCons.xml`, or PuTTY's saved sessions. Address, port, user, name and group are imported (not passwords),
  and anything already saved is skipped, so importing again is safe.
- Click a column header to sort. Drag headers to reorder columns (Name always stays first), and right-click
  the headers to choose which columns are shown or to reset them. Order, choice and widths are remembered.

## Shipping it without installs
On a PC where the helpers are installed and working, run this from the project folder:

    powershell -ExecutionPolicy Bypass -File .\collect-tools.ps1

It copies them into the project's `tools\` folder, one subfolder each, with their licence files:

    tools\node\                     node.exe (runs aws-azure-login; npm isn't needed)
    tools\aws-azure-login\          the package and its node_modules
    tools\session-manager-plugin\   session-manager-plugin.exe (carries the RDP tunnel)
    tools\aws-cli\                  AWS CLI v2 (only used by SSO profiles)
    tools\chromium\                 only with -IncludeChromium; otherwise the sign-in page opens in Microsoft Edge

The tools stay there: while developing, Switchboard uses them straight from the project folder (nothing is
copied into bin\ on build). Publishing copies `tools\` next to the .exe with the same layout, and Switchboard
uses those copies before anything installed on the PC. Re-run the script after updating any of the tools.

## Settings (File > Settings, Ctrl+,  or the Settings button)
- **AWS profile**: pick the profile, see whether you're signed in, sign in, run first-time Microsoft sign-in setup,
  and choose which regions to scan.
- **RDP credentials**: save passwords in Windows Credential Manager (encrypted with your Windows sign-in;
  entries named `SwitchboardRM:<user>` in Control Panel > Credential Manager > Windows Credentials) and pick
  which user every connection signs in as. If that user has no saved password, Remote Desktop asks.
- **Appearance**: dark mode (also View > Dark mode). Both looks use a purple accent.
- **About**: version, what's new, links to the log, settings and tools folders and documentation,
  bundled tool versions, and a button that copies version details for support requests.

## Versions
The version is set once, in `SwitchboardRM.csproj` (`<Version>1.0.0</Version>`), and shows on the splash,
Settings > About (also Help > About) and the .exe's Properties > Details. For each release, bump it
(PATCH 1.0.1 for fixes, MINOR 1.1.0 for new features, MAJOR 2.0.0 for changes that break existing setups)
and add a section to `CHANGELOG.md`, which the About page shows as "What's new".

## Settings file and logs
- Settings: `%APPDATA%\SwitchboardRM\settings.json` (profile, RDP user, regions, dark mode, list width). Passwords are never in this file.
- Logs: File > Open log folder (tunnel output and errors go to files there, not on screen).
