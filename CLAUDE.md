# CLAUDE.md: Switchboard Remote Manager

This file is read at the start of every Claude Code session in this repository. It records how the maintainer works,
how the project is built and released, how the code fits together, and the lessons that cost real time to learn.
**Treat this repository as public (MIT)** (it may be private at times, but is written to be publishable). Never
write organisation-specific data here or anywhere in the repo: no tenant IDs, app IDs, account names, role ARNs, AWS
account numbers, hostnames or certificate details.

---

## 1. What this is

**Switchboard Remote Manager** (`SwitchboardRM.exe`, called *AWS RDP Launcher* before 2.0) is a Windows desktop app
(WinForms, .NET 10, self-contained single-file) for reaching servers from one tabbed window:

- **AWS servers** (EC2, and non-EC2 machines managed by Systems Manager) listed per profile and region, connected
  through **Session Manager tunnels**: Remote Desktop (embedded MsRdpClient ActiveX) or SSH (embedded terminal). No
  open inbound ports needed. Start, stop and reboot instances.
- **Saved connections** (direct RDP / SSH, not AWS), importable from RDCMan, mRemoteNG, `.rdp` files and PuTTY.
- **Local shells** in tabs (Command Prompt, PowerShell 5.1/7, Git Bash, WSL distributions), **Claude Code** in a
  terminal tab, and **claude.ai** in a web tab.
- **Sign-in to AWS:** built-in Microsoft Entra ID (SAML) sign-in, or AWS IAM Identity Center (SSO) via the AWS CLI.
  Status countdown, background renewal, sign-out on close.

Primary user: the maintainer, an experienced IT administrator who signs in to AWS with a separate Entra admin account
(not the account signed in to Windows). Others may use it outside the maintainer's organisation, so generic behaviour
(SSO, no org assumptions) matters.

---

## 2. Working with the maintainer (read this first)

- **"No code changes yet" / "just discuss" means exactly that.** Explain, compare options, recommend, and wait.
  Only change code when asked ("do it", "build it", "go ahead", "make the changes").
- **Treat every detail the maintainer gives as deliberate.** Don't second-guess it or ask them to re-verify or
  troubleshoot something they've already stated. If something they report contradicts your expectations, the
  expectation is wrong: investigate the code.
- **Find the real cause before fixing.** Several bugs here took more than one attempt because the first fix treated a
  symptom (see section 9). Say plainly what the cause was, including when it was your own earlier mistake.
- **Test what can be tested, and say what can't.** Pure logic (parsing, file writers, key mapping, script steps)
  gets real tests; Windows-only UI behaviour gets a clear "check this on Windows" note. Never claim untested things work.
- **Every app change gets a version bump and a CHANGELOG entry** (section 5). Repo-only changes (workflow, scripts,
  README) don't bump the app version unless they change what a release contains and the maintainer wants it out.
- **Never run `git-release.ps1`, push, tag, publish a release, or touch GitHub settings unless asked.**
- **Don't install or remove software on the maintainer's PC unless asked.**
- Answers: direct, structured, no filler. Explain the "why" briefly; give exact commands/paths when action is needed.
  Wording in the app's UI: plain English, specific, honest about limits (see section 8).

---

## 3. Build, run, release: commands

All from the project folder, on Windows, with the **.NET 10 SDK**.

| Task | Command |
|---|---|
| Build | `dotnet build` |
| Run (development) | `dotnet run` (uses the project's own `tools\` folder in place) |
| Publish (self-contained single file) | `.\publish.cmd` → `output\SwitchboardRM.exe` + `output\tools\` + `output\fonts\` |
| Collect bundled tools into `tools\` | `powershell -ExecutionPolicy Bypass -File .\collect-tools.ps1` |
| Update installed tools, then collect | `powershell -ExecutionPolicy Bypass -File .\update-tools.ps1` (`-CheckOnly`, `-NoCollect`) |
| Build an unsigned test installer | `.\publish.cmd`, then `powershell -ExecutionPolicy Bypass -File .\build-installer.ps1` (needs Inno Setup 6) |
| Release | bump `<Version>` + CHANGELOG, then `powershell -ExecutionPolicy Bypass -File .\git-release.ps1` (**only when asked**) |

`git-release.ps1`: reads the version from the `.csproj`, refuses a version already released (tag exists here or on
GitHub), checks the CHANGELOG has a section for it, refuses files over 50 MB, commits everything (`Release X.Y.Z` +
changelog notes), rebases onto GitHub if behind, pushes. **It does not tag**: the workflow creates the tag and the
release. `-DryRun` previews, `-Yes` skips the prompt, `-Version` overrides.

---

## 4. CI / GitHub Actions (`.github/workflows/build.yml`)

Runs on `windows-latest`.

- **Push to `main`:** full build with tools bundled. A step *"Will this build publish a release?"* decides: if
  `<Version>` has no GitHub release yet, the run publishes **Release `vX.Y.Z`** (tag created on that commit) titled
  **`SwitchboardRM vX.Y.Z`**, with the setup `.exe` and the zip, and that version's CHANGELOG section as notes; it then
  keeps **no** artifact (the release has the files). Otherwise the build is kept as an artifact for **7 days**.
  An existing release is never replaced.
- **Pull requests:** compile only (no tools, no secrets).
- **Manual run:** full build (tools optional), artifact only; tick *Publish the release* to release the current version.
- **Signing:** builds of `main` run in the **`release` environment** (restricted to `main`), whose secrets
  `SIGNING_PFX_BASE64` and `SIGNING_PFX_PASSWORD` hold an organisational code-signing certificate (internal AD CS,
  not publicly trusted). `SwitchboardRM.exe`, the installer and its uninstaller are signed SHA-256 with a timestamp
  (DigiCert, fallback Sectigo) using the newest x64 `signtool.exe` from the Windows SDK. A `main` build without the
  secrets **fails** rather than publishing unsigned. The PFX is written to `RUNNER_TEMP` only for the signing step and
  deleted. The internal CA isn't trusted on GitHub's runners, so the workflow checks *signed + timestamped + signer*,
  not chain validity.
- **Check the bundle:** fails if `output\` lacks `SwitchboardRM.exe`, `tools\session-manager-plugin\…exe`,
  `tools\aws-cli\aws.exe`, `tools\conpty\conpty.dll`, `tools\conpty\OpenConsole.exe` or `THIRD-PARTY-NOTICES.txt`, or
  if retired folders (`tools\node`, `tools\aws-azure-login`, `tools\chromium`) are present.
- **Installer** built by Inno Setup (installed via Chocolatey if missing), signed via ISCC `/Ssigntool=` (see 9.4).
- Actions **storage** filled up once (artifacts of ~1 GB builds at 90-day retention). Hence 7-day retention and
  no artifact on release runs. Keep it that way.

---

## 5. Versioning and CHANGELOG

- `MAJOR.MINOR.PATCH` in `SwitchboardRM.csproj` `<Version>`. **PATCH** = fixes; **MINOR** = new features or behaviour
  changes; **MAJOR** = removing something or breaking existing setups (3.0.0 removed aws-azure-login).
- Every version has a `## X.Y.Z (YYYY-MM-DD)` section at the top of `CHANGELOG.md` (newest first). Bullets in plain
  English, user-facing, explaining what changed and why it matters; fixes start with **"Fixed:"** and state the cause.
- `CHANGELOG.md` is embedded in the exe and shown in-app (Settings > About > *What's new in this version*; Help > About opens that page). The workflow publishes the matching
  section as release notes, so it must exist before releasing.
- If several changes accumulate before a release, they share one version; if a version was already released,
  start the next one. Ask when it's unclear whether a version went out.

---

## 6. Architecture map

| File | Responsibility |
|---|---|
| `Program.cs` | Entry: `Migration.Run()`, `FontLoader.LoadAll()`, `TERM=xterm-256color`, crash marker (`RunMarker`), `StartupContext` (splash → `MainForm.PrepareAsync`), crash clean-up (signs out leftover keys if the last run didn't exit cleanly) |
| `MainForm.cs` (~3,300 lines) | Main window: toolbar, menus, server list, tabs, docked terminal panel, connect flow, sign-in/renewal heartbeat, shells, Claude tabs, tab keys, wake/unlock handling, update notice, New server entry points, system tray |
| `AwsServices.cs` | `Aws` (config/credentials paths, `ConfigValue`, `SetConfigValues`, `SetCredentials`, `SignOutAsync`, profiles list/remove, identity), inventory (SSM + EC2 per region), Session Manager tunnel (SDK `StartSession` + `session-manager-plugin.exe`), instance actions |
| `EntraSignIn.cs` | Built-in Microsoft sign-in: SAML request, WebView2 sign-in window, hand-off capture, roles, STS `AssumeRoleWithSAML`, writes keys |
| `ProfileSetup.cs`, `ProfileForms.cs` | Add / Edit / Remove profiles in-app: Microsoft (Entra) form with *Check sign-in* (lists roles), SSO form |
| `RdpHost.cs` | MsRdpClient ActiveX host (AxHost), dynamic resolution, focus-release event sink |
| `TerminalHost.cs` | WebView2 + xterm.js terminal page, input/output bridge, fonts-folder fonts sent to the page, schemes, app shortcuts from JS, Enter = restart / Esc = close after the session ends |
| `PseudoConsole.cs` | ConPTY: Microsoft `conpty.dll` (with `ConptyReparentPseudoConsole` → WAM works) or built-in fallback; env block, working dir, job object, suspended start |
| `Shells.cs` | Shell discovery (cmd, Windows PowerShell, PowerShell 7, Git Bash, WSL — re-listed on menu open, throttled), default shell, Claude Code detection + installer command |
| `ServerLaunch.cs` | New server: curated AWS images (SSM public parameters `/aws/service/ami-windows-latest`, `ami-amazon-linux-latest`, Canonical Ubuntu) + own AMIs, instance types by architecture, VPCs/subnets/security groups, key pairs, instance profiles (IAM, flags `AmazonSSMManagedInstanceCore`), reading a server to clone, `RunInstances` (IMDSv2 required) |
| `NewServerForm.cs` | The New server wizard: 1 Server (prices, disks), 2 Network (`InterfaceEditor` per network interface), 3 Access and protection, 4 Review (cost estimate); remembered per-region choices (`AppSettings.LaunchDefaults`) |
| `Updater.cs` | GitHub `releases/latest` check, install detection (Inno uninstall key `{AppId}_is1`, HKLM = all users, HKCU = per user), download + size/SHA-256 check, WinVerifyTrust + same-publisher (subject + issuer) check, silent setup with `/SBUPDATE=1`; policy `DisableUpdateCheck` |
| `ClaudeWeb.cs` | The claude.ai tab (WebView2, own profile, sign-in pop-ups kept, other links to the default browser) |
| `TerminalSchemes.cs` | Terminal colour schemes + generated PowerShell styling (`-EncodedCommand`, avoids execution policy) |
| `Theme.cs` | Palettes, accent math (WCAG), fonts, custom controls: `ThemedCheckBox`, `ThemedListView`, `ThemedTabControl` (+ button, shell icons, close ×), `AccentSwatch`, renderers, title bar |
| `SettingsForm.cs` | Settings pages: AWS profile, RDP credentials, Terminal & SSH, Appearance, About |
| `Models.cs` | `AppSettings` (settings.json), `Instance`, OS names, `Log` (rotation) |
| `ServerAdmin.cs`, `ServerForm.cs` | The Server window (Manage server, EC2 only): pages on the left, each built on first view and themed as shown. 3.7.0: Overview (details, disks, status checks, scheduled events), Screen and log (`GetConsoleScreenshot` with WakeUp, `GetConsoleOutput` Latest with fallback), Name and tags (`CreateTags`/`DeleteTags` diff, aws: tags untouched), Protection (`ModifyInstanceAttribute` one attribute per call, `Monitor`/`UnmonitorInstances`). 3.8.0: Instance type (stop → wait → `ModifyInstanceAttribute` InstanceType → start if it was running; stop protection respected) and Disks (`ModifyVolume` + wait for optimizing/completed, then `ServerAdmin.ExtendScript` through Run Command; `CreateVolume` + `AttachVolume` at the next free xvdf…/sdf… + `FormatNewDiskScript` on Windows; `DetachVolume` keeps the disk). In-OS scripts find the disk by its NVMe serial (volume ID without the dash); a partition without a letter reports `[char]0`, so letter checks use `-notmatch '^[A-Za-z]$'`. `ServerAdmin.RunScriptAsync` (AWS-RunPowerShellScript / AWS-RunShellScript, polls `GetCommandInvocation`) is the building block for the Run Command page. 3.9.0: Network (per interface: `ModifyNetworkInterfaceAttribute` Groups / SourceDestCheck, `Assign`/`UnassignPrivateIpAddresses` with the wizard's address check; instance profile via `DescribeIamInstanceProfileAssociations` then Associate / Replace / Disassociate) and Images and snapshots (`CreateImage` NoReboot = !reboot, tagged SourceInstance; `CreateSnapshots` for all disks at once; restore = `CreateVolume` from the snapshot + attach + `BringOnlineScript` on Windows, never touching the original disks). Pages load their lists on first view (`ShowPage` → Load…Async by page index). 3.10.0: Run command (one `SendCommand` to this server plus ticked peers of the same platform — online, same region, passed in by MainForm as `CommandTarget`s — then `GetCommandInvocation` per server; recent commands in `AppSettings.RecentCommands`) and Patches and agent (`DescribeInstanceInformation`, `DescribeInstancePatchStates`, `DescribeInstancePatches` State=Missing/Failed, `AWS-RunPatchBaseline` Scan/Install with RebootOption NoReboot by default, `ListInventoryEntries` AWS:Application). 3.11.0: Terminate button (Protection page, `TerminateForm`) |
| `VncHost.cs`, `Assets\vnc\` | VNC tabs (3.20.0): noVNC 1.5.0 (MPL 2.0) + Switchboard's `vnc.html`, embedded (`SwitchboardRM.vnc\<path>`) and served to WebView2 from the .exe at `http://switchboard.vnc/` via `WebResourceRequested`. `VncRelay` = loopback WebSocket server (random token in the path, `WebSocket.CreateFromStream` after a hand-made 101) relaying to the VNC server or the Session Manager tunnel (port 5900). Passwords: Credential Manager key `vnc:<host:port or instance id>`, value `user\u0001password`, tried once before asking. Page <-> host by web messages (credentials, connected, disconnected, securityfailure, clipboard, reconnect / cad, paste, focus, close) |
| `VolumeAdmin.cs` | EBS across the scanned regions (3.23.0): `VolumesAsync` / `SnapshotsAsync` (with AMIs needing each snapshot, and whether its source volume exists), `KeysAsync` (customer-managed KMS keys usable for EBS + AWS managed, account EBS default marked; AWSSDK.KeyManagementService), create (waits for available), attach, detach, modify, delete, snapshot, copy, tags; `MonthlyCost` estimates (Pricing API storage + gp3/io extras scaled by the region's gp3 price); `VolumeRules.Check` = EBS limits per type (gp3 baseline 3,000 IOPS at any size; 500 IOPS/GiB above it; throughput <= IOPS/4); `NextDevice` |
| `AddDisksForm.cs` | Several disks at once: rows (drive/name, size, type, IOPS, MB/s, cost, status), encryption key (last used per region in `AppSettings.LastKmsKeys`, else account default), tags (server's offered), attach. `ForServer` (Server window Disks page; names "Server - Drive") / `Standalone` (Volumes window; zone + optional server; or from a snapshot). Callers read `Created`, not DialogResult |
| `VolumesForm.cs`, `VolumeDialogs.cs` | AWS > Volumes and snapshots: pages Volumes / Snapshots / Cleanup like the Server window; small dialogs: `TagsForm`, `TypedConfirmForm`, `ModifyVolumeForm`, `ChoiceForm` |
| `NotesForm.cs` | Release notes viewer (update notice's What's new, Settings > About > What's new): `NotesView` parses the CHANGELOG's markdown (## version (date), - bullets with indented continuation lines and sub-points, 1. steps, **bold**, `code`, "Fixed:" shown as a FIXED tag) and lays it out and draws it itself, in the app font (an embedded browser couldn't use fonts-folder fonts) |
| `SessionRestore.cs` | Restoring tabs: `tabs.json` (settings folder) holds `TabState`s in tab order (main, then docked) plus the selected index; terminal output (option, off by default) in `%LOCALAPPDATA%\SwitchboardRM\scrollback\tabN.txt`, read on close through `window.sbScrollback()` in terminal.html (last 5,000 lines, wrapped lines joined; ~3 s overall limit) and replayed dimmed by `TerminalHost.Replay`. Captured in `OnFormClosing` after the sessions question (`_closeConfirmed` / `_tabsSaved` stop it asking or saving twice when the close restarts after reading output). `RestoreTabsAsync` runs at the end of start-up: local tabs at once, the first 5 remote tabs reconnect, the rest become `_placeholders` (Reconnect opens a new tab at the end: RDP pages aren't moved between positions, which risks the ActiveX control), AWS tabs wait in `_pendingRestore` until `_aws` loads. Claude Code reopens with `--continue`; Connect as keeps the user, never the password |
| `PortForwarding.cs`, `TerminateForm.cs` | Port forwarding: `Tunnel.StartAsync(inst, profile, remotePort, remoteHost, localPort)` uses `AWS-StartPortForwardingSessionToRemoteHost` when a host is given (else `AWS-StartPortForwardingSession`); a fixed local port is checked free first. `MainForm._forwards` (`PortForward`) is shown in the non-modal `ForwardsForm` (Server > Port forwards) and disposed on exit. Terminate: `ServerAdmin.TerminationInfoAsync` lists disks deleted/kept (DeleteOnTermination) and protection; Terminate is enabled only when the typed name matches; afterwards MainForm closes the server's tabs and forwards. Bulk: the server list is multi-select; `RunActionAsync` runs Start/Stop/Reboot on every selected EC2 server after one confirmation (other commands use the first selected) |
| `SingleInstance.cs` | One copy per Windows session (setting `SingleInstance`, default on): named mutex `Local\SwitchboardRM.Running` claimed in `Program.Main`; a second start signals `Local\SwitchboardRM.Activate` (after `AllowSetForegroundWindow`) and exits; MainForm brings itself forward (from the tray too). `--updated` (installer relaunch after an update) waits up to 20 s for the old copy to exit. The crash marker is unaffected: `RunMarker` already ignores a marker whose process is still a running Switchboard |
| `ConnectAsForm.cs` | Connect as dialog; the session carries `AsUser` / `AsPassword` (memory only), used by `StartRdpAsync` and `SshCommand`, kept on reconnect; Connect as always opens its own tab |
| `Connections.cs`, `ConnectionImport.cs` | Saved connections (connections.json), connection dialog (also "connect now"), importers |
| `CredentialStore.cs` | Windows Credential Manager (`SwitchboardRM:<user>`) |
| `Tools.cs` | Bundled tool locations (`tools\`), About-page tool list |
| `ProcessJob.cs` | Job object (kill-on-close) for every helper process; `RunMarker` (crash detection) |
| `TabHotkeyHook.cs` | Low-level keyboard hook for Ctrl+Alt+Left/Right inside Remote Desktop |
| `FontLoader.cs` | Fonts from `fonts\` next to the exe (private to the app) |
| `AppIcon.cs` | Icon + `TaskbarIcon` (class icon fallback, re-sent on wake/display change) |
| `Cli.cs` | Runs helper CLIs hidden (used for `aws sso login/logout`) |
| `Migration.cs` | One-time migration from AWS RDP Launcher data |
| `installer\SwitchboardRM.iss` | Inno Setup: per-user or all-users install, upgrades, silent install, signing hook |
| `Assets\terminal\` | `terminal.html` (placeholders `/*THEME*/`, `/*FONT_FAMILY*/`, `/*FONT_SIZE*/`), xterm.js 5.5.0 + fit 0.10.0 |
| Scripts | `collect-tools.ps1`, `update-tools.ps1`, `build-installer.ps1`, `git-release.ps1`, `publish.cmd` |

---

## 7. How the key parts work

### 7.1 Microsoft (Entra ID) sign-in: `EntraSignIn.cs`
Reads the profile's `~/.aws/config` keys **under aws-azure-login's names** (kept for compatibility):
`azure_tenant_id`, `azure_app_id_uri`, `azure_default_username`, `azure_default_role_arn`,
`azure_default_duration_hours`, `azure_default_remember_me`, `region`.
1. Builds the SAML `AuthnRequest` exactly as aws-azure-login did (NameIDPolicy `emailAddress`), raw-deflate + base64
   → `https://login.microsoftonline.com/<tenant>/saml2?SAMLRequest=…&login_hint=<account>`. ACS endpoint by region:
   `signin.aws.amazon.com/saml`, GovCloud `signin.amazonaws-us-gov.com/saml`, China `signin.amazonaws.cn/saml`.
2. Sign-in window = WebView2 with its own user data folder `%LOCALAPPDATA%\SwitchboardRM\SignIn`,
   `AllowSingleSignOnUsingOSPrimaryAccount = false` (never silently uses the Windows account).
3. `AddWebResourceRequestedFilter` on the ACS endpoints **before** navigating, so the POST with `SAMLResponse` is
   always caught (no race), answered locally ("Signed in…"), window closed.
4. Roles from the `https://aws.amazon.com/SAML/Attributes/Role` attribute (pair order either way): the only one,
   the profile default, or a picker (interactive only).
5. STS `AssumeRoleWithSAML` with `AnonymousAWSCredentials`, duration from the profile; keys written to the profile's
   `[name]` section in `~/.aws/credentials`: `aws_access_key_id`, `aws_secret_access_key`, `aws_session_token`,
   `aws_expiration` (ISO 8601 UTC). Only those keys change; written via temp file + move.
- **Background renewal:** same flow in a hidden off-screen window; succeeds only with a remembered session
  ("Stay signed in"); gives up after 45 s and the status asks the user to sign in.
- Returns `(Code, Out, Err)`: 0 = signed in, -2 = cancelled, else failure text.
- `GetSamlAsync` is reused by the profile form's *Check sign-in* (no keys written).

### 7.2 SSO profiles
Detected by `sso_start_url` / `sso_session`. Sign-in/out run `aws sso login/logout --profile X` through the bundled
or installed AWS CLI. SSO form writes `sso_start_url`, `sso_region`, `sso_account_id`, `sso_role_name`, `region`.
The AWS CLI is bundled **only** for this and for `aws` in shells (decision: keep it bundled for now; SDK-based SSO
deferred because nobody can test SSO yet).

### 7.3 Sign-in status and renewal (MainForm)
Expiry from `aws_expiration` in the credentials file; countdown in the toolbar; periodic AWS identity check;
"Keep me signed in" renews in the background before expiry; paused while Windows is locked/asleep (cancels a running
renewal); sign-out on close removes only temporary keys (never the `[profile]` heading).

### 7.4 Tunnels, RDP, SSH
SDK `StartSession` (port forwarding) → `session-manager-plugin.exe` carries the data channel to a local port →
RDP ActiveX connects to `localhost:<port>` (dynamic resolution via `UpdateSessionDisplaySettings`), or `ssh.exe`
runs in a terminal against it with `HostKeyAlias=<instance-id>`. Linux defaults to SSH, Windows to RDP.

### 7.5 Terminals and shells
`TerminalHost` = WebView2 (`%LOCALAPPDATA%\SwitchboardRM\WebView2`) + xterm.js; `PseudoConsole` runs the program.
Shells start in the user folder with `AWS_PROFILE` = current profile and bundled `aws-cli` + `session-manager-plugin`
first on `PATH`. PowerShell tabs get the colour scheme's styling via `-NoExit -EncodedCommand` (runs after the user's
profile; replaces only PowerShell's default prompt). Docked panel (shared, collapsible strip) holds terminals only.
Claude Code is just another terminal tab (`claude.exe` from PATH or `%USERPROFILE%\.local\bin`, or npm `claude.cmd`
via `cmd /c`); if missing, the menu offers Anthropic's installer (`irm https://claude.ai/install.ps1 | iex`) in a tab.

### 7.6 Keyboard
Main window: Enter connect, Ctrl+T new shell, Ctrl+Shift+T new shell (also from terminals), Ctrl+L list, Ctrl+1/2
list tabs, F5 refresh, Ctrl+, settings, Ctrl+Tab / Ctrl+Shift+Tab and **Ctrl+Alt+Right/Left** next/previous tab
(Ctrl+Alt+arrows also inside Remote Desktop via `TabHotkeyHook`), Ctrl+Shift+W close tab (asks; × and middle-click
don't), Ctrl+` dock panel. Terminals keep Ctrl+W/Ctrl+T/Ctrl+C for the shell; app keys are intercepted in
`terminal.html` (`appShortcut`).

### 7.7 New server and updates
- **New server:** everything is read-only until Launch on the review screen; the writes are `RunInstances` (1 instance,
  tags on instance + volumes) and, only when an interface has source/destination check off,
  `ModifyNetworkInterfaceAttribute` after launch (RunInstances can't set it). Specific private IPs are checked first
  (subnet range, AWS's 5 reserved addresses, `DescribeNetworkInterfaces` for in-use). Prices: Pricing API (`us-east-1`
  endpoint, filters `regionCode`, `operatingSystem`, `tenancy=Shared`, `preInstalledSw=NA`, `capacitystatus=Used`,
  `licenseModel=No License required`), cached 3 days in `%LOCALAPPDATA%\SwitchboardRM\prices`. Data disks use device
  names `xvdf…` (Windows) / `/dev/sdf…` (Linux). A public IPv4 can only be requested with a single interface. Needs EC2 describe/run permissions, `iam:ListInstanceProfiles`,
  `iam:ListAttachedRolePolicies` and `iam:PassRole`. The SDK v4 returns null for empty lists and some numbers: read
  them through the null-safe helpers in `ServerLaunch` (`L`, `N`, `B`).
- **Updates:** automatic check at most every 20 h (first a minute after start), quiet on failure; a skipped version
  stays skipped for automatic checks. Update now (installed copies only): download → verify → `RunSetup` → close with
  `_updating` set (no exit questions, **no sign-out**, so the relaunched copy is still signed in). The installer's
  `[Run]` entry with `Check: IsAppUpdate` restarts the app only for `/SBUPDATE=1`. Unsigned (development) copies refuse
  to self-update and offer the release page.

### 7.8 System tray (Settings > Appearance > Window)
- Settings: `MinimizeToTray`, `CloseToTray` (both off by default), `CloseChoiceMade`, `TrayHintShown`.
- `OnFormClosing`: only `CloseReason.UserClosing` that isn't Exit (`_exitRequested`) or an update (`_updating`) is
  diverted. If `CloseToTray` is on, the window hides to the tray. If it's off and `CloseChoiceMade` is false, a
  `TaskDialog` asks first (Keep running in the tray / Exit / Cancel, *Don't ask me again*, footnote pointing to
  Settings). The tick sets `CloseToTray` + `CloseChoiceMade`; without it the answer is for that time only. Changing
  the option in Settings also sets `CloseChoiceMade`. Shutdown, sign-out and updates always close normally.
- `ExitApp()` (tray menu and File > Exit) sets `_exitRequested`, restores the window if hidden (so the "close open
  sessions?" question has an owner) and closes; if that question is answered "no", `_exitRequested` is reset.
- Minimize to tray: the `Resize` handler hides the window when minimized; `_restoreState` remembers Normal/Maximized
  for `RestoreFromTray()`. The tray icon is visible while either option is on, or while the window is hidden; it's
  disposed in `OnFormClosed` (otherwise it lingers until hovered). The "still running" balloon shows once
  (`TrayHintShown`), and not at all if the first-close dialog just explained it.

### 7.9 Robustness features
Job object kills all helper processes when the app ends (even on crash); crash marker → next start removes leftover
AWS keys (if sign-out-on-close is on); taskbar icon set as class icon + re-sent on unlock/wake/display change; full
redraw after wake (now, +1 s, +3 s); `app.log` rotates at 10 MB to `app.1.log`; tunnel logs deleted after 7 days.

---

## 8. Conventions

**C#**: .NET 10 WinForms, `ImplicitUsings` on, `Nullable` **disabled**, file-scoped `namespace SwitchboardRM;`,
classes `internal` unless needed otherwise. Comments explain *why* (the code shows *what*). UI work on the UI thread
(`BeginInvoke` from events raised elsewhere; never dispose a control inside its own event, defer it). Log with
`Log.Write` (never throws). Scale pixel values with `LogicalToDeviceUnits` / `DeviceDpi`. Themed colours from
`Theme.Current`; new windows call `Theme.Apply(form, Theme.Current.IsDark)` and `Theme.ApplyTitleBar` in
`OnHandleCreated`. Settings: add properties to `AppSettings` with safe defaults; removed properties are simply ignored
when old `settings.json` files load. **Never write next to the exe** (it may live in Program Files): use
`%APPDATA%\SwitchboardRM` (settings) or `%LOCALAPPDATA%\SwitchboardRM` (logs, WebView2 data).

**UI text**: plain, specific English. Say what will happen and what it affects ("The server keeps running; only this
tab closes."). No jargon where a plain word works. Dialog questions for destructive actions; none for harmless ones.

**PowerShell scripts**: **CRLF line endings, ASCII only** (enforced by `.gitattributes`; non-ASCII breaks
Windows PowerShell 5.1 and Inno Setup). Must run under **Windows PowerShell 5.1 and PowerShell 7**. Judge native
commands by `$LASTEXITCODE`, never by stderr (9.1). Parse-check with
`[System.Management.Automation.Language.Parser]::ParseFile(...)` after editing.

**Repo hygiene**: never commit `tools\`, `output\`, `bin\`, `obj\`, `installer-output\`, PFX files, or secrets.
`fonts\` is not ignored: commit only fonts whose licence allows it.

---

## 9. Hard-won lessons (don't relearn these)

1. **Windows PowerShell 5.1 + `$ErrorActionPreference='Stop'` + a native command writing to stderr = fatal**, even
   for harmless Git warnings (`LF will be replaced by CRLF`) or `git push` progress. `git-release.ps1` routes Git
   through `Git-Raw` (local `ErrorActionPreference='Continue'`, `2>&1 | % { "$_" }`, `-c core.safecrlf=false`) and
   checks exit codes. PowerShell 7 doesn't show this, so tests on pwsh 7 alone miss it.
2. **PowerShell passes `$null` to a C# `string` parameter as `""`.** Use `string.IsNullOrEmpty`.
3. **PowerShell function names are case-insensitive and shadow programs**: a function named `Git` calls itself.
4. **Inno Setup sign command placeholders:** `$q` = quote, `$f` = file, literal `$` = `$$` (passwords!).
5. **The built-in Windows pseudo-console gives each terminal a hidden console window with no owner**, so WAM
   sign-in windows (`Connect-MgGraph`, `Connect-AzAccount`) are invisible. Fix in use: Microsoft's `conpty.dll` +
   `OpenConsole.exe` (Windows Terminal project, MIT, from NuGet `Microsoft.Windows.Console.ConPTY`) and
   `ConptyReparentPseudoConsole(hpc, mainWindow)`; MSAL takes `GetAncestor(GetConsoleWindow(), GA_ROOTOWNER)` as the
   parent. Use the **same** DLL for create/resize/close. They must be real files in `tools\conpty` (the DLL launches
   `OpenConsole.exe`), not embedded in the single-file exe.
6. **The RDP ActiveX control reads the keyboard outside the app's message loop.** `ProcessCmdKey`, `IMessageFilter`
   and its own `OnFocusReleased` event all failed. What works: a `WH_KEYBOARD_LL` hook, re-installed whenever an RDP
   tab is selected/focused or the window activates (newest hook runs first), tracking modifiers itself, swallowing
   only the arrow (down, repeats and up).
7. **`TabControl` reports its empty strip area as `HTTRANSPARENT`**, so a "+" drawn there gets no clicks. Override
   `WM_NCHITTEST` → `HTCLIENT`.
8. **A `ToolStripDropDown` inherits its owner item's font.** The terminal button uses the icon font (Segoe MDL2),
   which has no letters → fallback font. Set `owner.Font = _menu.Font` when building menus.
9. **`ThemedCheckBox`**: padding must never be subtracted from the text area (it was, so "widening" did nothing);
   the toolbar's "Keep me signed in" is sized from its text in the current font and re-sized on font/text change.
10. **`&` in WinForms text is a mnemonic marker** ("Terminal & SSH" became "Terminal _SSH"). Use
    `UseMnemonic = false` on labels / `TextFormatFlags.NoPrefix` when drawing.
11. **Sign-in race (historical, aws-azure-login):** it launched Chromium already on the sign-in URL, then turned on
    interception; remembered sessions completed first and the window landed on the AWS console. The built-in sign-in
    sets the filter before navigating. Don't reintroduce a navigate-then-intercept order.
12. **xterm.js sends Esc as `"\x1b"` alone; arrows/function keys send sequences starting with it.** Compare exactly.
13. **WebView2: one environment per user data folder, with consistent options.** Separate folders are used for
    terminals (`WebView2`), Entra sign-in (`SignIn`) and claude.ai (`ClaudeWeb`).
14. **Taskbar icon turns generic after unlock** when the app is busy: set the window *class* icon too, and re-send
    icons on unlock/wake/display change. Windows also sometimes skips repainting after wake: force `RedrawWindow`.
15. **Microsoft sign-in must never use Edge's Windows-account SSO or the Windows account.** (A fallback to Edge once
    signed in straight to the AWS console with the wrong account.) The WebView2 sign-in has OS-account SSO disabled
    and a private profile.
16. **Tools are collected on the build machine**, so what a release contains depends on that machine; `Check the
    bundle` exists because a release once shipped without its browser.
17. **The AWS CLI v2 is thousands of files** (bundled Python, `botocore\data`). `awscli\examples` (~4,000 help files)
    is removed by `collect-tools.ps1`; the rest is required while the CLI is bundled.
18. **Credential and config writers must touch only their own keys** and write via temp file + move. Never rewrite
    or reorder a user's `~/.aws/config` / `credentials` beyond the profile being changed.
19. **Profile listing reads `~/.aws/config` and `~/.aws/credentials` directly**, not through the SDK's credential
    chain (which needs working keys), so signed-out profiles still appear.
20. **Job object + ConPTY:** start the child `CREATE_SUSPENDED`, assign to the job, then `ResumeThread`, so
    grandchildren are in the job too.
21. **`Theme.Apply(form, ...)` only themes controls that are in the form's control tree at that moment.** In a
    wizard that swaps screens in and out, theme each screen as it's shown (`ShowPage` calls it); controls added later
    (rows, editors) are themed when added. Forgetting this left the 3.3.0 wizard's controls white in dark mode.
22. **WinForms projects implicitly import `System.Drawing` and `System.Windows.Forms`**, which clash with AWS SDK
    names: `Image` (use `using Ec2Image = Amazon.EC2.Model.Image;`). AWS service namespaces also clash with each other
    (`ResourceType` is in both `Amazon.EC2` and `Amazon.SimpleSystemsManagement`). In a file using several services,
    import only one service namespace, alias the others' models (`using Ssm = Amazon.SimpleSystemsManagement.Model;`)
    and write their clients and enum types in full (`Amazon.EC2.ResourceType.Instance`, `Amazon.Pricing.FilterType`).
23. **The .NET AWS SDK flattens some API structures:** `RunInstancesRequest.Monitoring` is a plain `bool` (there's no
    `RunInstancesMonitoringEnabled` class). Check property types against the SDK, not the API reference. SDK v4 also
    returns `null` for empty lists and many numbers/bools: read through null-safe helpers (`L`, `N`, `B` in `ServerLaunch`).
23b. **A .NET class can't have a property with its own name, so the SDK renames such fields:** EC2's
    `InstanceStatus.InstanceStatus` is `InstanceStatus.Status` in .NET.
23d. **`TextRenderer` ignores `Graphics.TranslateTransform` unless given `TextFormatFlags.PreserveGraphicsTranslateTransform`.**
    In a scrolled, owner-drawn view (NotesView), text without it stays put while shapes move. Also invalidate fully in
    `OnScroll` / `OnMouseWheel`, since scrolling only repaints the revealed strip.
23c. **Don't name members after built-in Control/Form members** (`Layout`, `Text`, `Tag`, `Region`, `Font`, `Created`,
    `Update`, `Size` ...): it
    hides them (CS0108) or clashes. Use `Arrange`, `Draw`, `DrawTag`, `SelectedRegion` and the like.
24. **C# forbids reusing a local name in a nested scope, even if the outer one is declared later in the method**
    (CS0136): give disk/type/etc. variables distinct names (`wantDisk`, `wantImage`).
25. **`X509Certificate.CreateFromSignedFile` is marked obsolete (SYSLIB0057)** but is still the only built-in way to
    read a signed exe's signer; it's used deliberately in `Updater.cs` with the warning suppressed around it.
26. **GitHub answers 404, not 401/403, for a private repository to unauthenticated API calls.** The updater's
    `releases/latest` check therefore fails while the repo is private (see 14).
25b. **Tab icons and badges are drawn by `ThemedTabControl`, in room made with leading spaces in the tab text** (the
    native control sizes tabs from their text). Shell tabs: `Tag = ShellTag`, text `IconSpace + name`. Remote tabs:
    `Tag = BadgeTag("AWS"|"RDP"|"SSH")`, text `BadgeRoom(badge) + title` (spaces measured in the strip's font). Every
    remote title change must go through `MainForm.Badged(...)` or the badge overlaps the name.
25c. **DPI: the app is `PerMonitorV2` (since 3.17.0; it was `SystemAware`, which made Windows bitmap-stretch it on
    screens scaled differently from the sign-in primary: maximized windows a pixel short, blurry text).** Every form
    gets `Theme.EnableDpiRescaling` (from `Theme.Apply` / `ApplyTitleBar`): `AutoScaleMode = Dpi` with
    `AutoScaleDimensions = CurrentAutoScaleDimensions`, so it keeps the sizes it was built with on its first screen and
    WinForms rescales it when it moves to another DPI. Size new UI with `LogicalToDeviceUnits` / `DeviceDpi` where it
    must track the current screen. `ThemedTabControl` sizes its own drawing with `Px(n)` (n pixels at 100%, scaled by
    `DeviceDpi`), derives the badge font from the tab font, and resets its tab padding in `RescaleConstantsForDpi`.
25d. **A dropped RDP connection isn't necessarily the end.** `RdpHost.Disconnected` (the control's `OnDisconnected`,
    DISPID 4, plus `ExtendedDisconnectReason`) is recorded on the session; `MainForm.IsDeliberateEnd` closes the tab
    only for reason 1 (this side) or 2 (remote user), or extended 1, 2, 5, 11, 12 (admin disconnect/logoff, replaced,
    user disconnect/logoff). Everything else goes to `AutoReconnectAsync`: same tab, `Session.Overlay`, new tunnel,
    `StartRdpAsync` on the same control; retries 5 s then 15 s up to 10 min, at once on `NetworkAvailabilityChanged`.
25e. **Don't use ListView's own item tooltips (`ShowItemToolTips`) for the server list:** the native tip appears under
    the cursor and captures the mouse when moving down, so hover tracking stops. `HoverTip` is a click-through layered
    window (`WS_EX_TRANSPARENT` + `HTTRANSPARENT`) driven by the list's MouseMove. It draws a `TipContent` card
    (title, subtitle, label/value rows with optional colour, tags, note) from `MainForm.TipFor`, falling back to the
    item's `ToolTipText`.
25f. **Always make styled fonts with `Theme.CreateFont(family, size, style)`, never `new Font(someFont, FontStyle.Bold)`.**
    A fonts-folder family often has only a regular file; asking for its bold directly makes Windows substitute some
    other font that has one (it drew the tray menu's bold item in the terminal font). `CreateFont` falls back to the
    family's regular style when the requested one doesn't exist, and records it in `Theme.IsFauxBold`;
    `ThemedRenderer.OnRenderItemText` then draws menu text twice (+1 px) for a fake bold; labels and flat buttons get
    a Paint handler (`Theme.AttachFauxBold`, from `ApplyFont`) that repaints background + text twice when their font is
    a fake bold (checked at paint time); owner-drawn text (HoverTip, TerminalPreview) does the same itself.
25h. **Restoring tabs: the first `ReconnectAtStart` (5) remote tabs reconnect at once; the rest start as placeholders
    and `ReconnectPlaceholdersAsync` reconnects them in batches of 5, `ReconnectBatchPause` apart**, via
    `ActivatePlaceholderAsync(page, background: true)` (back in the placeholder's position, selection unchanged). AWS tabs
    whose server isn't listed (not signed in) wait and are retried each pass. Remote tabs go into the strip through
    `ShowSessionTab`, which returns the page to use: replacing a placeholder (`_replacing`), the connection's controls
    move into the placeholder's own TabPage (no insert/remove/re-select, so the visible page isn't repainted) and, in the
    background, their windows are created without showing the tab (`c.Handle`; the RDP AxHost needs one for `GetOcx`).
    Only if that fails is the tab shown for an instant under WM_SETREDRAW off. Swapping pages flashed the screen.
25i. **SDK v4 response shapes differ per call:** `CreateSnapshot` returns the snapshot on `.Snapshot` (`.Snapshot.SnapshotId`),
    `CreateVolume` on `.Volume`, while `CopySnapshot` has `.SnapshotId` directly. Check the response type, don't assume.
25g. **The tab strip's overflow arrows are a native `msctls_updown32` child** that the theme can't reach: `ThemedTabControl`
    finds it (`ScrollArrowsRect`) and skins it with a `NativeWindow` (classic style via `SetWindowTheme`, redrawn after
    paint and mouse messages). Since 3.21.0 the tabs are `TabSizeMode.Fixed` (`UpdateTabWidths`: 240-120 px, sharing
    the strip) and **the + is a hidden last TabPage (`_plusPage`) kept inside the strip**: `TabCount` is hidden (`new`)
    to count real tabs only (use `base.TabCount` for the raw count inside the class), `KeepPlusLast` adds/moves/removes
    it (keeping the selected tab), it's never selected (`OnSelectedIndexChanged` redirects), and clicks on its slot are
    handled in WndProc. Outside the class, use the strip as `ThemedTabControl`, never as a plain `TabControl` (that
    would count the + tab). Repaint fully on WM_HSCROLL (Windows shifts pixels when scrolling). **Setting `ItemSize` re-enters
    `OnResize`** (the tab control nudges its size by a pixel to re-lay out), so `UpdateTabWidths` guards against
    re-entry; without it, the app died at start-up with a stack overflow (no log, no dialog). **Open menus with `Theme.ShowMenuOnScreen(menu, screenPoint)`**, not `menu.Show(...)`:
    WinForms lets a menu run onto the next screen when opened near a screen edge.
26a. **Reordering tabs must not remove and re-insert pages:** taking an RDP page out of its TabControl (even
    briefly) drops the connection. `ThemedTabControl.MoveTab` reorders by assigning positions
    (`TabPages[i] = page`, which only relabels the native tab items) in neighbour swaps, then sets `SelectedIndex`.
    Dragging a tab (left button, past `SystemInformation.DragSize`) and the tab menu's Move left/right use it.
26b. **Anything saved only in `OnFormClosing` is lost on a Windows shutdown or restart** if the close stops to ask a
    question: Windows ends the app first. Layout is saved shortly after each change (`LayoutChanged` → `SaveLayoutNow`,
    1 s timer), and `CloseReason.WindowsShutDown` / `TaskManagerClosing` skip the questions (and terminal output) and
    just save and close. Window bounds are stored as the *normal* bounds (`RestoreBounds` when maximized) plus a
    maximized flag, and only reapplied if a decent part of them is on a connected screen.
27. **Before delivering a zip, check it matches the working copy** (unzip and `diff -rq`): a zip packaged before a
    fix once shipped the bug that had already been fixed.

---

## 10. User's PC: data locations

| What | Where |
|---|---|
| Settings, saved connections, crash marker | `%APPDATA%\SwitchboardRM\` (`settings.json`, `connections.json`, `running.marker`) |
| Logs | `%LOCALAPPDATA%\SwitchboardRM\logs\` (`app.log`, `app.1.log`, `tunnel-*.log`) |
| WebView2 data | `%LOCALAPPDATA%\SwitchboardRM\WebView2` (terminals), `\SignIn` (Entra), `\ClaudeWeb` |
| Price cache (New server) | `%LOCALAPPDATA%\SwitchboardRM\prices\` (kept 3 days) |
| Saved passwords | Windows Credential Manager, `SwitchboardRM:<user>` |
| AWS profiles / keys | `~/.aws/config`, `~/.aws/credentials` (or `AWS_CONFIG_FILE` / `AWS_SHARED_CREDENTIALS_FILE`) |
| Profile removal backups | `*.switchboard-backup` next to the AWS files |
| Install (installer) | `%LOCALAPPDATA%\Programs\Switchboard Remote Manager` (per user) or `C:\Program Files\Switchboard Remote Manager` |

Bundled next to the exe: `tools\session-manager-plugin\`, `tools\aws-cli\`, `tools\conpty\`, optional
`tools\openssh\`, `tools\THIRD-PARTY-NOTICES.txt`; optional `fonts\`. **No longer bundled (3.0.0):** Node.js,
aws-azure-login, Puppeteer's Chromium, the login-hint shim. `~/.aws/chromium` is a leftover that can be deleted.

---

## 11. Installer (`installer\SwitchboardRM.iss`)

- **AppId `{3E4F5DA2-D33A-402B-8839-516C3670C9B2}` — never change it** (it's how upgrades find the install).
- `PrivilegesRequired=lowest` + `PrivilegesRequiredOverridesAllowed=dialog commandline`: "Install for me only"
  (default, no admin) or "all users" (UAC). `{autopf}` follows the choice.
- Silent: `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /ALLUSERS` or `/CURRENTUSER`. Uninstall keeps settings.
- `[InstallDelete]` wipes `{app}\tools` on upgrade so retired tools disappear. `MinVersion=10.0.17763` (ConPTY).
- Publisher shown in Apps & features: `AppPublisher` define (default the copyright holder).
- File must stay **ASCII**.

---

## 12. Code signing (context)

Organisational code-signing certificate from an internal AD CS Enterprise CA (template based on *Code Signing*,
exportable key, manager approval), requested by an admin account, exported as PFX, stored only as GitHub environment
secrets. Trusted on the organisation's PCs only; gives no SmartScreen reputation elsewhere. Third-party binaries are
**never** re-signed (they keep their makers' signatures; Microsoft ConPTY is Microsoft-signed). A Defender for
Endpoint *certificate allow indicator* would use the public `.cer` (extractable from a signed exe), never the PFX.

---

## 13. Testing approach

- Pure logic: compile the relevant C# into a harness with `Add-Type` (pwsh) or a small console project and run
  realistic cases (SAML request/role parsing, config/credentials writers, key mapping, Esc handling, log rotation,
  WSL refresh throttling). PowerShell steps: parse-check, then run against stand-ins.
- Windows-only behaviour needs a real run; describe exactly what to check. Typical manual checks: sign-in (window +
  background renewal, log lines `Built-in sign-in: …`), RDP tab keys, terminals (WAM via `Connect-MgGraph` after
  `Disconnect-MgGraph`), docking, theme/font changes, wake/unlock, installer per-user vs all-users.
- CI is the real test of the workflow and installer; read failing step logs and fix the actual cause.

---

## 14. Open items / roadmap (discussed, not built unless noted)

- **Built:** the auto-updater (3.2.0), the New server wizard with prices, data disks and full network control (3.3.0),
  system tray options (3.4.0). See 7.7 and 7.8.
- **Updater vs. private repository (open decision):** the repository is currently **private**, so the updater's
  anonymous GitHub check gets 404 and can't work. Options discussed: make the repo public (works as built); keep code
  private and publish releases to a separate public repo (workflow pushes with a token secret; `AppInfo.Repo` points
  there); an internal update location (S3 / web server / share, URL set by setting or policy); or disable the updater by
  policy (`DisableUpdateCheck = 1`) and deploy updates with Intune (Win32 app with supersedence). Never embed a GitHub
  token in the app.
- **New server, later:** launching several identical servers at once (a count, with numbered names) was discussed and
  deferred.
- **SSO sign-in via the AWS SDK** (removes the bundled AWS CLI): deferred until someone with SSO can test it.
  Option meanwhile: stop bundling the CLI and use an installed one (declined for now: keep bundling).
- **Smaller bundle:** single-file compression (`EnableCompressionInSingleFile`, ~80–100 MB off, slower first start).
- **MCP server** idea: expose Switchboard (list instances, start/stop, run SSM commands) to Claude Code / Claude
  desktop as tools. Idea only.
- **Native Claude panel on the Claude API** (separate billing, data-policy check): idea only.
- **Linux/macOS port:** would need Avalonia (not Mono), FreeRDP or external RDP clients, Unix PTYs, Keychain/libsecret.
  Shared core library first. Not planned.

---

## 15. Before finishing any change

1. Version bumped (if the app changed) and CHANGELOG section written.
2. Structure/compile check; tests for testable logic; scripts parse; workflow YAML valid.
3. README updated if behaviour, setup, files or commands changed.
4. No org-specific data, secrets or bundled tools added to the repo.
5. Summarise for the maintainer: what changed, why (cause), what was tested, what to check on Windows.
