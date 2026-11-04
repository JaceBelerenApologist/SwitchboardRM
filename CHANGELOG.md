# Changelog

All notable changes to Switchboard Remote Manager (called AWS RDP Launcher before 2.0). Versions follow MAJOR.MINOR.PATCH:
PATCH for fixes, MINOR for new features, MAJOR for changes that break existing setups.

## 2.14.2 (2026-11-05)

- Fixed: after sleep or unlocking, parts of the window sometimes stayed blank until hovered. Switchboard now redraws
  all its windows (and their title bars) itself after waking, unlocking or a display change, and again a few
  seconds later once the display has settled.

## 2.14.1 (2026-11-05)

- Fixed: the taskbar sometimes showed the generic app icon instead of Switchboard's, especially after unlocking the
  PC. The icon is now also set as the window class's icon (what the taskbar falls back to when the app is busy),
  and re-sent after unlocking, waking, display changes and display-scaling changes.

## 2.14.0 (2026-11-05)

- Terminal colour schemes, in Settings > Appearance > Terminal colours: Match app (the default), Dracula, Campbell,
  One Half Dark, One Half Light, Solarized Dark, Solarized Light, Nord, Gruvbox Dark and Tokyo Night. A scheme sets
  the terminal's background, text, cursor, selection and 16 console colours, for every program; open terminals
  change straight away. A preview shows the chosen scheme.
- "Also colour PowerShell to match" (on by default): new PowerShell tabs start with the scheme's own colours for the
  prompt, dir listings, tables, errors, progress and syntax, without editing your profile. It's passed in directly,
  so execution policy doesn't block it (Windows PowerShell 5.1 gets the parts it supports), and a prompt from your
  profile is kept.

## 2.13.1 (2026-11-05)

- Fixed: clicking the docked terminals' expand arrow sometimes expanded the panel and immediately collapsed it
  again (the click reached both the arrow and the strip). Each click now acts once.

## 2.13.0 (2026-11-05)

- Collapsing the docked terminals now leaves their header strip at the bottom ("Docked terminals (2)") with an up
  arrow, instead of the panel disappearing. Click the arrow or the strip (or press Ctrl+`) to expand it again; the
  terminals keep running while collapsed. The panel only goes away completely when nothing is docked.

## 2.12.0 (2026-11-04)

- Fonts folder: .ttf, .otf and .ttc files in a "fonts" folder next to SwitchboardRM.exe are loaded when Switchboard
  starts, for Switchboard only (nothing is installed in Windows). They appear in Settings > Appearance as
  "(fonts folder)" and can be the app font or the terminal font; terminals are given the font files directly.
  Weights come from the file names (Regular, Bold, SemiBold, Italic ...). Publishing copies the project's fonts\
  folder into output\fonts\.

## 2.11.0 (2026-11-04)

- Connect to any computer without saving it first: "Remote Desktop to a computer ..." at the top of the + menu on
  the main tabs, and Server > Connect to a computer. Enter the computer (and optionally a user and password) and
  connect, by Remote Desktop or SSH. Nothing is saved unless "Add to saved connections" is ticked; a password typed
  for a one-off connection is only kept in memory for that tab.

## 2.10.1 (2026-11-04)

- Fixed: the + after the last tab (on the main tabs and in the docked panel) did nothing and didn't highlight.
  Clicks in the empty part of the tab strip now reach it.

## 2.10.0 (2026-11-04)

- Settings > Terminal & SSH > Open new terminals docked: new shells and SSH sessions open in the docked panel
  instead of a tab of their own. Move back to a tab still works on any of them.

## 2.9.0 (2026-11-04)

- Docked terminals: right-click a shell or SSH tab and choose Dock to bottom to put it in a panel under the tabs,
  so it's visible alongside a remote desktop (which resizes to make room rather than being covered). It carries on
  running when moved.
- The panel is shared by every tab, has its own tab strip for several docked terminals (with a + for a new shell
  straight into it), and a header with Move back to a tab, Hide and Close (which asks first).
- Drag the bar between the panel and the tabs to resize it; double-click the bar for a third of the window. The
  height is remembered.
- Ctrl+` (or View > Docked terminals) shows and hides the panel; hidden terminals keep running. Inside a docked
  terminal, Ctrl+Tab and Ctrl+Shift+Tab move between the docked terminals and Ctrl+Shift+W closes that one.

## 2.8.1 (2026-11-02)

- Fixed: Ctrl+Alt+Left / Ctrl+Alt+Right didn't switch tabs from inside a Remote Desktop session. The keys are now
  caught by Switchboard before they reach the session (and aren't sent to the remote computer); holding them down
  no longer races through the tabs.

## 2.8.0 (2026-11-02)

- Keyboard tab switching: Ctrl+Alt+Right / Ctrl+Alt+Left for the next / previous tab work everywhere, including
  inside a Remote Desktop session; Ctrl+Tab / Ctrl+Shift+Tab do the same in the main window and terminal tabs.
  Both wrap around at the ends.
- Ctrl+Shift+W closes the current tab, after asking (remote sessions: disconnect; shells: anything running stops).
  Not available from inside a Remote Desktop session; switch tabs first, or use the × or middle-click.
- Ctrl+Shift+T opens a new shell, also from inside a terminal tab (where Ctrl+T and Ctrl+W stay with the shell).
- View menu: Next tab, Previous tab, Close tab, with their keys.

## 2.7.0 (2026-11-02)

- Local shells in tabs: Command Prompt, Windows PowerShell, and PowerShell 7, Git Bash and WSL distributions when
  installed. Open one with the terminal button on the toolbar (the arrow picks which), File > New shell (Ctrl+T),
  or the + after the last tab. They have a terminal icon on their tab and work like the SSH terminal (copy, paste,
  fonts, theme); Enter restarts one that has ended.
- Each shell starts in your user folder with AWS_PROFILE set to the profile in use and the bundled AWS CLI,
  Session Manager plugin and Node.js on its PATH.
- Settings > SSH is now Terminal & SSH, with the default shell. Closing Switchboard counts open shells too.

## 2.6.1 (2026-11-02)

- Sign in and Sign out on the toolbar have icons, like the server buttons: a person (in the accent colour) and a
  power symbol.

## 2.6.0 (2026-11-02)

Customisation, in Settings > Appearance. Everything applies straight away, to every window.

- Theme: Light, Dark, or Match Windows (follows Windows' light/dark setting, even while open).
- Accent colour: Purple (the default, unchanged), Blue, Teal, Green, Amber, Red, Pink, Graphite, the Windows
  accent colour (follows it when it changes), or any custom colour. The hover, text and highlight shades are worked
  out from it, keeping text readable (at least 4.5:1 contrast) and using dark text on light accents.
- App font (Segoe UI by default, or another installed UI font) and server list text size.
- SSH terminal font (Cascadia Mono by default, or another installed monospace font) and text size.
- Reset appearance puts everything back to the defaults.

## 2.5.0 (2026-11-02)

- Sign out button on the toolbar, next to Sign in: signs out of AWS now (removes the profile's temporary keys;
  open tabs keep running). Enabled while you're signed in. AWS > Sign out still does the same.

## 2.4.1 (2026-10-27)

- Release packaging: collect-tools.ps1 now also gathers the bundled tools' licences, so a release can be
  redistributed: each tool's licence files go in its folder (or a LICENSE-NOTE.txt where the install has none),
  aws-azure-login gets a THIRD-PARTY-NOTICES.txt listing every package with its licence text, and
  tools\THIRD-PARTY-NOTICES.txt summarises all bundled tools with versions, licences and sources.
- README: step-by-step installation, either from a release download or by installing the helper tools yourself
  (Session Manager plugin, Node.js and aws-azure-login, AWS CLI, OpenSSH), first-time sign-in setup, and how to
  build, release and update a bundle.

## 2.4.0 (2026-10-27)

- Nothing organisation-specific is built into the app any more: no built-in Entra tenant ID, default profile name
  or company name. The Microsoft sign-in setup window shows the tenant ID and app ID URI of an existing profile on
  the PC (read from ~/.aws/config at the time), or says to ask your administrator.
- No default AWS profile: Switchboard uses the last profile you picked, or the first one it finds; with none,
  Sign in opens Add profile.
- Builds record source paths as \src\SwitchboardRM, not the folder they were built in.

## 2.3.1 (2026-10-27)

- Microsoft sign-in asks Entra for the profile's own account: the profile's azure_default_username (from
  ~/.aws/config) is added to the sign-in page as login_hint, so Entra shouldn't silently use the account signed in
  to Windows when that's a different one (e.g. a separate admin account). Applies to Sign in and the background
  renewal. Done by a small script loaded ahead of aws-azure-login, which itself is unchanged; the log notes whether
  the hint was applied. Profiles without azure_default_username sign in as before.

## 2.3.0 (2026-10-27)

- No background sign-in while Windows is locked or asleep: the sign-in checks and automatic renewal pause, and a
  renewal already running when you lock the PC is stopped, so nothing tries to sign in (or sends authenticator
  prompts) while you're away. Checking resumes 30-60 seconds after you unlock or the PC wakes. Open RDP and SSH
  tabs aren't affected.

## 2.2.0 (2026-10-23)

- Remove AWS profiles: "Remove profile ..." in Settings > AWS profile (for the selected profile) and in the AWS
  menu (for the one in use). It deletes the profile's sections from ~/.aws/config and ~/.aws/credentials,
  saving a *.switchboard-backup copy of each file first. Not allowed for the profile in use while AWS tabs are open.
- Fixed: in Settings > AWS profile, the profile buttons were taller than the profile list and sat lower; they
  now match its height and line up with it.

## 2.1.0 (2026-10-23)

- Add AWS profiles from the app: "Add profile ..." at the bottom of the toolbar's profile list, in Settings >
  AWS profile and in the AWS menu. Pick Microsoft sign-in (aws-azure-login) or AWS IAM Identity Center (SSO);
  the matching setup window opens and the new profile is selected. Existing profiles aren't changed.
- The profile lists are now pick-only, so typing can no longer swap the profile in use by accident.
- Closing Switchboard with AWS sessions open shows a small card while the sessions end and you're signed out,
  and always waits for AWS to end the sessions (it could exit too early when sign-out on close was off).
- Fixed: after signing out (on close or AWS > Sign out), Sign in said the profile "isn't set up for Microsoft
  sign-in". Profiles are now read from ~/.aws/config and ~/.aws/credentials directly, and signing out removes
  only the keys, leaving the profile in place.

## 2.0.0 (2026-10-23)

- New name: **Switchboard Remote Manager** (Switchboard RM), since it now does far more than AWS Remote Desktop.
  The program is SwitchboardRM.exe and its data lives under SwitchboardRM. On first start it copies your 1.x
  settings, saved connections and saved passwords across, leaving the old ones in place.
- Signs out of AWS when Switchboard closes (removes the profile's temporary keys, or "aws sso logout" for SSO
  profiles), after ending open sessions. Setting in Settings > AWS profile; AWS > Sign out does it any time.
- With "Keep me signed in" off, starting Switchboard no longer opens the sign-in by itself.
- Import connections from RDCMan (.rdg), Remote Desktop (.rdp) files, mRemoteNG (confCons.xml) and PuTTY saved
  sessions (File or Server > Import connections). Addresses, ports, users, names and groups come across;
  passwords don't. Connections that are already saved are skipped.
- Saved connections can have a Group, shown as a column on the Saved tab and editable in the Add/Edit dialog.

## 1.2.0 (2026-10-23)

- SSH terminals in tabs, alongside Remote Desktop: Linux servers connect with SSH by default (through the
  Session Manager tunnel to port 22, so no open inbound ports), saved connections can be SSH, and
  Server > Connect with SSH / Remote Desktop picks either for any AWS server. Uses Windows' own OpenSSH client
  in an embedded terminal (xterm.js in WebView2), so your ~/.ssh keys, the SSH agent and known_hosts all work.
  Settings > SSH sets the user name for AWS servers and an optional key file.

## 1.1.0 (2026-10-23)

- Saved RDP connections to any computer, alongside the AWS servers: add, edit and remove them from the toolbar,
  the Server menu or the right-click menu. They connect directly, show whether the RDP port answers, and are
  kept in their own connections.json file.
- The server list has two tabs, AWS servers and Saved RDP, each with its own columns (Ctrl+1 / Ctrl+2).
- "Keep me signed in to AWS" (Settings and the AWS menu) turns the background sign-in checks and renewal off
  when you don't need them; the launcher then signs in only when you connect or change a server.

## 1.0.0 (2026-10-22)

First versioned release.

- Lists EC2 instances across chosen regions, with name, type, OS, region, IP, instance ID and live status.
- Opens each server in its own tab through a hidden Session Manager tunnel; the remote desktop resizes with the window.
- Start, stop and reboot servers from the toolbar, the Server menu or the right-click menu, with live status updates.
- Microsoft sign-in through aws-azure-login, with a sign-in status heartbeat and automatic background renewal.
- Signs in automatically when you connect or change a server while signed out.
- RDP passwords saved in Windows Credential Manager.
- Movable and hideable list columns, "Windows only" and "Hide offline" filters, collapsible server list.
- Light and dark themes with a purple accent; settings window; splash screen.
- Ships as a single self-contained .exe with its helper tools (Node.js, aws-azure-login, Session Manager plugin, AWS CLI) in tools\.
