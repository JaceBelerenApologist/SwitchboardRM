# Changelog

All notable changes to Switchboard Remote Manager (called AWS RDP Launcher before 2.0). Versions follow MAJOR.MINOR.PATCH:
PATCH for fixes, MINOR for new features, MAJOR for changes that break existing setups.

## 3.18.0 (2026-12-03)

- Send Ctrl+Alt+Del on a Remote Desktop tab's right-click menu (while connected): opens the server's security screen
  (lock, sign out, change password, Task Manager), or its sign-in at a lock screen. Pressing Ctrl+Alt+Del yourself
  always goes to this PC; Ctrl+Alt+End inside the session does the same as this menu item.
- Password fields (Settings > RDP credentials, the connection editor, Connect as) have an eye button: click it to show
  the password and check it for typos before saving, and again to hide it.

## 3.17.1 (2026-12-02)

- Fixed: a Remote Desktop tab closed as soon as its connection dropped, even when that was a network blip (switching
  from cable to Wi-Fi, sleep, a dead tunnel) or the server restarting. Now only a deliberate end closes it: signing
  out or choosing Disconnect inside the server, another connection taking over, or closing the tab. A lost connection
  reconnects in the same tab instead, behind a "connection lost" notice with Reconnect now and Close tab: a fresh
  Session Manager tunnel for AWS servers, retries every 5 seconds and then every 15 for up to 10 minutes, straight away
  when the network comes back, and a wait while there's no network or the AWS sign-in has expired. Windows keeps the
  session on the server, so it comes back as it was.

## 3.17.0 (2026-12-01)

- Per-monitor display scaling: Switchboard now draws its windows natively at each screen's own scaling instead of
  having Windows stretch them. Fixed: a maximized window could fall a pixel short of the screen's edges (a line of
  wallpaper showing, and clicks at the very top going to the window behind it), and text could look slightly blurry,
  on screens whose scaling differs from the main one at sign-in, or after the scaling changed. Windows look the same as
  before on the screen they open on, and rescale when moved to a screen with different scaling.

## 3.16.0 (2026-11-30)

- Reconnect on a tab's right-click menu: for a session left stale, for example after Windows was locked long enough
  for its tunnel to time out. Remote Desktop gets a fresh connection and tunnel with the same user (including a
  Connect as user and a typed or one-off password, kept in memory) and stays in the same tab position; SSH reconnects
  in place. Restored tabs waiting to reconnect also get a right-click menu (Reconnect, Close tab).
- Shell tabs show their own program's icon instead of one shared terminal glyph: Command Prompt, Windows PowerShell,
  PowerShell 7, Git Bash, WSL and Claude Code each look like themselves (read from the program, as Windows shows it).
  The shell menus show the same icons. Shells without an icon of their own keep the terminal glyph.

## 3.15.0 (2026-11-29)

- Remote tabs show where they connect, as a small badge before the name: AWS (in AWS orange) for servers reached
  through Session Manager, RDP or SSH (in the accent colour) for saved connections. Before the name, so long names
  don't hide it.
- Server window, Screen and log: the console screenshot refreshes every 5 seconds while that page is showing ("Refresh
  every 5 seconds", on by default), with the time it was taken under it. It stops when another page is shown or the
  window closes.

## 3.14.0 (2026-11-28)

- Tabs can be rearranged: drag a tab along the strip (main tabs and the docked panel), or right-click it > Move left /
  Move right. Remote Desktop tabs stay connected while moving: tabs are reordered without taking their page out of
  the window. The new order is what's restored next time.

## 3.13.1 (2026-11-27)

- The layout is remembered: the window's position, size and maximized state (on whichever screen it was, or the
  default if that screen is gone), along with the columns, the list width and the docked panel's height. It's saved a
  moment after each change instead of only on exit.
- Fixed: column widths and other layout changes could be lost. They were only written when Switchboard closed
  normally, and a Windows shutdown or restart with sessions open stopped at the "close them and exit?" question until
  Windows ended the app. Now a Windows shutdown or sign-out saves the layout and tabs and closes without asking.

## 3.13.0 (2026-11-26)

Everything since 3.7.0:

- Restore my tabs when Switchboard starts (Settings > Terminal & SSH, on by default): the tabs open when Switchboard
  closed reopen next time, in order, docked where they were, with the same tab selected. Shells reopen; Claude Code
  continues its last conversation; claude.ai reopens; Remote Desktop and SSH tabs reconnect (Connect as tabs with the
  same user; typed passwords aren't kept). The first five remote tabs reconnect at start-up and any more open as
  Reconnect placeholders, so a big set doesn't hit every server at once. AWS tabs follow once you're signed in and the
  server list has loaded. Windows keeps a disconnected Remote Desktop session on the server, so reconnecting lands
  back in it with its apps still open.
- Also restore terminal output (off by default): terminals' scrollback (the last 5,000 lines) is kept on this PC until
  the next close and shown dimmed above the new shell, under a "restored from last session" line. What was running
  can't come back; shells keep their own command history (PowerShell's PSReadLine, bash's history file).
- Server list font (Settings > Appearance > Fonts): the server list can have a font of its own, separate from the
  app font: any of the text or monospace fonts offered, or one from the fonts folder. "Same as the app font" is the
  default, as before.
- Fixed: the Server window's page buttons ignored the app font. Their font was set after the theme from the window's
  own base font (Segoe UI), undoing the theme for those buttons only.
- Port forwarding (right-click an AWS server > Port forwarding): forward any port on the server (SQL Server,
  HTTPS, WinRM ... presets, or any number), or on a host reached through it (an RDS database, an internal site), to
  localhost on this PC, through Session Manager. A fixed local port, or any free one. The address is copied to the
  clipboard. Server > Port forwards lists the active forwards with Copy address, Open in browser (web ports) and
  Stop; forwards end when Switchboard closes.
- Several servers can be selected in the list (Ctrl/Shift-click): Start, Stop and Reboot then apply to every selected
  AWS server after one confirmation listing them. Everything else acts on the first selected server.
- Terminate (right-click an AWS server, or the Server window's Protection page): says which disks are deleted with
  the server and which are kept, refuses while termination protection is on, and only terminates once the server's
  name is typed. Its open tabs and forwards are closed afterwards.
- Server window, Run command page: run a PowerShell (Windows) or shell (Linux) script through Systems Manager on the
  server, and optionally on other online servers in the same region with the same platform, all at once. Each
  server's output is shown with its result; running on several servers asks first. The last ten commands are kept
  for reuse.
- Server window, Patches and agent page: the Systems Manager agent's status (online or connection lost, version and
  whether it's the latest, OS, last check-in), patch compliance (missing, failed, waiting for a reboot, installed,
  last operation, baseline) with the missing and failed patches listed (KB, title, classification, severity). Scan
  for patches now, or install them now (no reboot by default; "reboot if needed" on request), using AWS's patch
  baseline. Installed applications from Systems Manager inventory, with a filter.
- Server window, Network page: per network interface, its security groups (from the VPC's) and source/destination
  check; secondary private IPv4 addresses, added (a specific address, checked against the subnet and what's in use,
  or the next free one) or removed. The server's instance profile (IAM role) can be attached, changed or removed, with
  a warning when the role lacks Session Manager permissions: the way to fix a server Switchboard can't reach.
- Server window, Images and snapshots page: create an image (AMI) of the server, rebooting it first for a consistent
  image (recommended) or not; the account's own images with their state; snapshot all the server's disks at once; the
  server's snapshots; and attach a copy of a snapshot as a new disk to recover files, without touching the server's
  own disks (on Windows it's brought online with a drive letter).
- Server window, Instance type page: the current type with its price, and every type that runs on the server's
  architecture with its price and the monthly difference. Change type stops the server (after asking), waits for it
  to stop, changes the type and starts it again if it was running, showing each step. Stop protection is respected.
- Server window, Disks page: the server's disks (device, size, type, IOPS, throughput, encryption).
  - Grow a disk (optionally changing its type, e.g. gp2 to gp3) while the server runs, then extend its partition and
    file system inside the OS through Systems Manager: Windows extends the disk's last partition (found by the volume
    ID; a last partition without a drive letter is left alone), Linux grows the partition and the XFS or ext4 file
    system.
  - Add a disk (size, type, encryption) in the server's zone at the next free device name; on Windows it can be
    initialized and formatted (GPT, NTFS, next free letter) straight away.
  - Detach a disk (not the system disk); the disk is kept and can be attached again.

## 3.7.0 (2026-11-20)

- Server window (right-click an AWS server > Manage server, or Server > Manage server), with its pages on the left:
  - Overview: instance type, state, zone, platform, image, launch time, IP addresses, instance profile, key pair,
    security groups, disks (size, type, IOPS, encryption), AWS status checks (system and instance) and scheduled
    events (maintenance, retirement), protections and monitoring.
  - Screen and log: a console screenshot and the system log, which work even when Remote Desktop and Session Manager
    can't reach the server.
  - Name and tags: rename the server and edit its tags (AWS's own aws: tags are left alone).
  - Protection: termination and stop protection, what an OS shutdown does (switching to "terminate" asks first), and
    detailed monitoring.
  More pages follow in later versions.

## 3.6.0 (2026-11-19)

- Allow only one copy of Switchboard (Settings > Appearance > Window, on by default): starting Switchboard again
  brings the open window to the front (from the tray or minimized too) instead of opening a second copy. One copy per
  Windows session, so each user on a shared server has their own. Untick it to run several copies.

## 3.5.0 (2026-11-19)

- Connect as (right-click a server): connect with another user, in a tab of its own (titled with the user), even when
  you're already connected as yourself. Remote Desktop: DOMAIN\user or user@domain and an optional password; left
  empty, the password saved for that user is used, or Remote Desktop asks. A typed password stays in memory for that
  tab only, unless "Save the password" stores it in Windows Credential Manager. SSH: the login name (the password or
  key is entered in the terminal). Reconnecting the tab keeps its user. Recently used users are offered in a list.

## 3.4.0 (2026-11-18)

- System tray options (Settings > Appearance > Window), both off by default: Minimize to the system tray, and Close to
  the system tray (the close button keeps Switchboard running). Sessions, tunnels and sign-in renewal carry on in the
  tray. Click the tray icon to open the window again (as it was, maximized included); right-click it for Open or
  Exit. Exit (tray menu or File) really closes, with the usual question about open sessions; Windows shutting down,
  signing out and updates close it normally.
- The first time the close button is used, Switchboard asks whether to keep running in the tray or exit, with
  "Don't ask me again" (which sets the option) and a note that it can be changed in Settings. Without the tick, the
  answer is for that time only. Choosing the option in Settings means it isn't asked.

## 3.3.0 (2026-11-17)

- New server is now a four-screen wizard (Back / Next):
  1. Server: region, image, instance type with its on-demand price (per hour and per month, for the image's OS;
     Windows includes its licence), name, tags, system disk and extra data disks, with the disks' monthly cost.
  2. Network: VPC, subnet, tenancy, placement group, hostname type and DNS records, and the network interfaces. Each
     interface: a specific primary private IPv4 or automatic, secondary IPv4s (a number or addresses), IPv6 addresses,
     security groups, description, source/destination check, and a public IPv4 only on a single-interface server.
     Specific addresses are checked before launch (in the subnet's range, not one of AWS's reserved addresses, not in
     use), and "Show addresses already in use" lists the subnet's used addresses. Interfaces are limited to what the
     instance type allows.
  3. Access and protection: instance profile (Session Manager checked), key pair, termination and stop protection,
     what an OS shutdown does, detailed monitoring, metadata tokens and hop limit, and user data.
  4. Review: everything, with the estimated cost (instance + disks, on-demand list price in USD; discounts not
     included), then Launch.
- New server like this one copies all of that from the server, including its interfaces and data disks, but never
  its addresses.
- Prices come from the AWS Pricing API (needs pricing:GetProducts) and are kept for three days.

## 3.2.0 (2026-11-16)

- New server (AWS > New server): create an EC2 server from one screen. Image: AWS's current Windows Server, Amazon
  Linux and Ubuntu builds, or your own images (newest first). Instance type: every type that can run the image, with
  vCPUs and memory (type to search). Disk size and type, name (with a warning if the name is already used in the
  region) and optional tags. Network: subnet (the VPC is only asked when there's more than one), security groups, and
  a public IP only if wanted. Instance profile: listed with a mark when it has Session Manager permissions, and a
  warning when it doesn't (Switchboard can't connect without them). Optional key pair. A review lists everything
  before anything is created; instance metadata is set to tokens only (IMDSv2). Choices are remembered per region.
- New server like this one (right-click an EC2 server): the same screen, filled in from that server's image, type,
  disk, subnet, security groups, instance profile, key pair and tags.
- Updates: Switchboard checks GitHub for a newer release once a day (and on Help > Check for updates) and shows a
  notice under the toolbar: What's new, Update now, Later, Skip this version. Update now downloads the setup, checks
  it (size, checksum, an intact signature from the same publisher as the running copy) and installs it silently for
  the same scope, then Switchboard starts again, still signed in. Copies that weren't installed with the setup open
  the release page instead. Settings > About turns the daily check off; administrators can turn it off with
  DisableUpdateCheck = 1 under HKLM or HKCU\SOFTWARE\Policies\SwitchboardRM.

## 3.1.1 (2026-11-15)

- Fixed: the toolbar terminal button's dropdown didn't use the app font (Settings > Appearance) like the other menus.

## 3.1.0 (2026-11-15)

- Claude in the shell menus (the terminal button's arrow, the + menus, File > Open shell), in a group of its own:
  - Claude Code, Anthropic's command-line agent, in a terminal tab when it's installed (native installer or npm). It
    starts in your user folder with the same environment as Switchboard's shells, AWS_PROFILE included. If it isn't
    installed, "Install Claude Code ..." runs Anthropic's installer in a tab, after asking.
  - Claude (claude.ai): the Claude website in a tab, signed in with your own account and remembered in a private
    browser profile. Choosing it again switches to the open tab. Sign-in pop-ups (Google, Microsoft, Apple) open as
    pop-ups; other links open in your normal browser. It closes without asking and isn't counted as an open shell
    when Switchboard exits.

## 3.0.2 (2026-11-14)

- WSL distributions installed or removed while Switchboard is running now show up in the shell menus (the terminal
  button's arrow, the + menus, File > Open shell): the list is checked again each time a menu opens, and an open menu
  updates itself if it changed.
- When a shell or SSH session has ended, Esc closes its tab, as Enter starts it again (the message says so).

## 3.0.1 (2026-11-14)

- Fixed: "Keep me signed in" on the toolbar could still be cut short ("Keep me s..."), especially with a wider app
  font. The extra room added in 2.18.6 was taken away again when the text was drawn; checkboxes now draw their text in
  all the room they have, and the toolbar's is sized from its text in the current font whenever that font changes.
- The AWS CLI's help examples (about 4,000 small files) are left out of the bundle; help pages work without them.
- The log no longer grows without limit: when app.log passes 10 MB it becomes app.1.log (replacing the previous one)
  and a new app.log starts, so logs take at most about 20 MB.

## 3.0.0 (2026-11-13)

- Microsoft sign-in is built in, and is now the only Microsoft sign-in: Switchboard's own sign-in window, on
  Microsoft's WebView2 engine (kept up to date by Windows). aws-azure-login, Node.js and Puppeteer's Chromium are no
  longer used or bundled, along with the login-hint shim they needed; the "built-in sign-in (preview)" setting is gone.
  The release is roughly 450 MB smaller, with most of its files gone, and sign-in no longer runs a 2022 browser engine.
- Profiles are unchanged: their settings in ~/.aws/config keep the names aws-azure-login uses, so a separately
  installed aws-azure-login can still use them. aws-azure-login's old sign-in browser profile (~/.aws/chromium) is no
  longer used and can be deleted.
- Still bundled: the Session Manager plugin, the AWS CLI (SSO sign-in, and aws commands in Switchboard's shells) and
  Microsoft ConPTY. Settings > About lists them, plus the WebView2 version Microsoft sign-in uses.
- collect-tools.ps1 no longer collects Node.js, aws-azure-login or Chromium, and removes their old folders from tools\;
  update-tools.ps1 no longer checks them; the GitHub build no longer installs them, and checks they're not in the
  release.

## 2.21.2 (2026-11-12)

- The AWS servers tab's tooltips show each server's instance ID and region, under its name.

## 2.21.1 (2026-11-12)

- Edit profile: a button next to Add and Remove in Settings > AWS profile, and in the AWS menu, opening the selected
  profile's sign-in settings (it replaces the separate Sign-in settings section).
- Fixed: the profile list and its buttons no longer make Settings > AWS profile scroll sideways. The list now spans
  the page, with Edit, Add and Remove underneath it.

## 2.21.0 (2026-11-12)

- Profile setup is now entirely in the app, with no terminal windows. Add profile asks for a name and sign-in type,
  then shows that type's settings form; AWS > Profile sign-in settings, Settings > AWS profile > Sign-in settings and
  the "not set up yet" prompt open the same forms for an existing profile.
- Microsoft profiles: tenant ID, app ID URI, account, AWS region, session length, default role and "stay signed in",
  written to ~/.aws/config under the same names aws-azure-login uses, so both sign-ins can use the profile. A new
  profile is filled in from an existing Microsoft profile on the PC. Check sign-in tries the settings in the built-in
  sign-in window (no keys saved) and lists the AWS roles the account has, to pick the default from.
- SSO profiles: start URL, SSO region, account ID, role (permission set) and AWS region, in the standard form the AWS
  CLI and SDKs read. Sign-in still uses "aws sso login".
- Only the profile's own settings change in ~/.aws/config; everything else in the file is kept as it was.

## 2.20.0 (2026-11-12)

- Built-in Microsoft sign-in (preview), in Settings > AWS profile: signs in with Switchboard's own sign-in window,
  using Microsoft's WebView2 browser engine (kept up to date by Windows), instead of aws-azure-login and its 2022
  Chromium. It reads the same profile settings in ~/.aws/config (tenant, app ID, account, default role, duration,
  region) and writes the same AWS keys to ~/.aws/credentials, so the rest of Switchboard works unchanged and you can
  switch back at any time. Its sign-in window has a private profile ("stay signed in" is remembered there for
  background renewals), doesn't use the Windows account, and catches Microsoft's hand-off to AWS before anything
  loads, so it can't go on to the AWS console. Several AWS roles: the profile's default, otherwise you choose. Each
  step is in the log. aws-azure-login stays the default for now; SSO profiles are unaffected.

## 2.19.0 (2026-11-11)

- An installer: releases now include SwitchboardRM-Setup-<version>.exe (signed, like the app) alongside the zip. It
  offers "Install for me only" (no admin rights; %LocalAppData%\Programs) or "Install for all users" (admin; Program
  Files), adds Start menu and optional desktop shortcuts and an uninstaller, upgrades an installed version in place
  (closing Switchboard first), and installs silently with /VERYSILENT /ALLUSERS or /CURRENTUSER. Uninstalling keeps
  your settings. The app itself is unchanged.

## 2.18.6 (2026-11-10)

- Fixed: Ctrl+Alt+Left / Ctrl+Alt+Right didn't switch tabs while a remote desktop had the keyboard. The Remote Desktop
  control reads the keyboard outside Switchboard's own message handling, so these keys are now taken by a low-level
  keyboard hook that sees them first (kept ahead of any hook the RDP control adds). It only acts while Switchboard is
  the active window and not in a terminal, swallows only that arrow, and leaves every other key alone (plain
  Ctrl+arrow still moves by word in the remote session).
- The toolbar's "Keep me signed in" checkbox is 100 px wider.

## 2.18.5 (2026-11-10)

- SwitchboardRM.exe is now code-signed (SHA-256, timestamped) with LaPrairie Group's code-signing certificate, so
  Windows shows a known publisher on the organisation's PCs. Release builds of main are signed automatically; no
  changes to the app itself.

## 2.18.4 (2026-11-10)

- Fixed properly: sign-in going on to the AWS console in the sign-in window instead of completing (2.18.3 made it
  worse, especially when the Microsoft session was remembered). The cause was a race in aws-azure-login's window
  mode: it starts the browser already loading the sign-in page and only then turns on the interception that catches
  Microsoft's hand-off to AWS, so a quick sign-in could finish first. The sign-in window now opens on a small local
  page, aws-azure-login turns its interception on, and only then does the window go to the Microsoft sign-in page.

## 2.18.3 (2026-11-10)

- Fixed: the first sign-in sometimes opened the AWS console in the small sign-in window instead of finishing. When
  Chromium started slowly (a new or changed sign-in profile), aws-azure-login watched the wrong browser tab for the
  sign-in hand-off. Switchboard's sign-in shim now waits for the sign-in window and makes aws-azure-login watch that
  one; the log notes when it had to.
- Fixed: the account hint (azure_default_username) wasn't added to interactive sign-ins, only background ones; it's
  now added to both. The shim is loaded for every sign-in, with or without a configured account.

## 2.18.2 (2026-11-09)

- Release of 2.18.1's fixes with the new build process: every push to main is a full build with the helper tools
  bundled and checked (including Puppeteer's Chromium and ConPTY), and the release for a new version is published
  automatically. No changes to the app itself since 2.18.1.

## 2.18.1 (2026-11-09)

- Fixed: a release build could sign in with Microsoft Edge, which then went straight on to the AWS console in the
  sign-in window. Its bundled aws-azure-login had arrived without Puppeteer's Chromium, and Switchboard fell back to
  Edge. Sign-in now only ever uses Puppeteer's own Chromium: no Edge, no tools\chromium, and PUPPETEER_EXECUTABLE_PATH
  is cleared for sign-in so nothing on the PC can change that. If that Chromium is missing, sign-in stops with a
  message saying so.
- collect-tools.ps1 stops with an error if aws-azure-login has no complete Puppeteer Chromium, lists that Chromium in
  the notices, and no longer has -IncludeChromium (an old tools\chromium is removed). The GitHub build makes sure
  Puppeteer's Chromium download isn't skipped, downloads it explicitly if it's missing, and fails rather than publish
  a release without it.

## 2.18.0 (2026-11-07)

- Windows sign-in windows (WAM) now appear for commands in Switchboard's terminals, such as Connect-MgGraph and
  Connect-AzAccount, keeping single sign-on, device-based Conditional Access and token protection. Terminals use
  Microsoft's ConPTY (conpty.dll and OpenConsole.exe from the Windows Terminal project) from tools\conpty, and give each
  terminal's hidden console window to Switchboard's window, which is what WAM needs to show itself. Without
  tools\conpty, terminals use Windows' built-in pseudo-console as before. Settings > About says which is in use.
- collect-tools.ps1 gathers Microsoft ConPTY into tools\conpty (from NuGet, or an installed Windows Terminal), with its
  MIT licence in the notices; release builds include it.
- Removed: Help > Check for tool updates / Update helper tools and the matching Settings > About buttons, and the
  copies of update-tools.ps1 and collect-tools.ps1 inside the app. Release builds bundle the tools, so users don't
  need them installed or updated themselves; the scripts stay in the project for maintainers.

## 2.17.2 (2026-11-06)

- Fixed: when Puppeteer's own Chromium was missing or incomplete, sign-in still failed ("Failed to launch the browser
  process"). The fallback browser (tools\chromium or Microsoft Edge) was passed in a setting aws-azure-login never
  reads; it's now passed as PUPPETEER_EXECUTABLE_PATH, which Puppeteer uses. The same check now covers an installed
  (not bundled) aws-azure-login. The log says which browser sign-in uses when it isn't Puppeteer's own.

## 2.17.1 (2026-11-06)

- Fixed: sign-in failed with "Failed to launch the browser process" when Puppeteer's Chromium in the tools folder was
  incomplete (its folder there, but no chrome.exe). Switchboard now checks for chrome.exe itself and otherwise uses
  tools\chromium or Microsoft Edge. collect-tools.ps1 warns when it collects an incomplete Chromium.

## 2.17.0 (2026-11-06)

- Saved connections have a Description: a field in the Add/Edit and Connect dialogs, and a Description column on the
  Saved tab (sortable, searched, shown in full in the row's tooltip). It's added to an existing column layout once.
- Imports bring descriptions along where the other tool has them: RDCMan's comment and mRemoteNG's description.

## 2.16.0 (2026-11-06)

- Update the helper tools from inside Switchboard: Help > Check for tool updates (report only) and Help > Update
  helper tools (also on Settings > About). update-tools.ps1 runs in a tab, so its progress can be watched: it updates
  Node.js, aws-azure-login, the Session Manager plugin and the AWS CLI on this PC (winget and npm), then refreshes
  the tools folder. Not while AWS tabs are open (their tunnels use the plugin being replaced).
- update-tools.ps1 and collect-tools.ps1 are built into Switchboard: if either is missing from its folder, it's written
  back before running (an edited copy is left as it is).

## 2.15.1 (2026-11-06)

- Fixed: Settings showed "Terminal _SSH" instead of "Terminal & SSH" (Windows drew the "&" as a keyboard-shortcut
  underline). Settings text now shows "&" as written.

## 2.15.0 (2026-11-05)

- Nothing outlives Switchboard: every tunnel, ssh, shell and sign-in helper it starts is tied to it (a Windows job
  object), so they all end when Switchboard does, including when it crashes or is killed from Task Manager. AWS
  sessions behind those tunnels no longer stay open until Session Manager's idle timeout.
- Clean-up after a crash: if the last run didn't close cleanly, Switchboard removes the AWS keys it left behind when
  it next starts (with "Sign out of AWS when Switchboard closes" on), before anything else.

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
