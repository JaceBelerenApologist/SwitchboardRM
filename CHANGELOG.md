# Changelog

All notable changes to Switchboard Remote Manager (called AWS RDP Launcher before 2.0). Versions follow MAJOR.MINOR.PATCH:
PATCH for fixes, MINOR for new features, MAJOR for changes that break existing setups.

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
