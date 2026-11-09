; Switchboard Remote Manager - installer (Inno Setup 6)
;
; Packages everything in ..\output (SwitchboardRM.exe, tools\, fonts\) into SwitchboardRM-Setup-<version>.exe.
;
;   Install for me only   no admin rights needed  ->  %LocalAppData%\Programs\Switchboard Remote Manager
;   Install for all users asks for admin (UAC)    ->  C:\Program Files\Switchboard Remote Manager
;
; Silent:     SwitchboardRM-Setup-x.y.z.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /ALLUSERS   (or /CURRENTUSER)
; Uninstall:  "<install folder>\unins000.exe" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART
; Settings in %APPDATA%\SwitchboardRM are kept on uninstall, so reinstalling picks up where it left off.
;
; Built by build-installer.ps1 (locally) or the GitHub workflow:
;   ISCC.exe /DAppVersion=2.19.0 installer\SwitchboardRM.iss
;   ... plus /DSign "/Ssigntool=<command with $f>" to sign the setup and its uninstaller.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef AppPublisher
  #define AppPublisher "Nick Morrissey"
#endif
#define AppName "Switchboard Remote Manager"
#define AppExe  "SwitchboardRM.exe"

[Setup]
; The AppId must never change: it's how a newer version finds and upgrades an installed one.
AppId={{3E4F5DA2-D33A-402B-8839-516C3670C9B2}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright=Copyright (C) 2026 Nick Morrissey
AppPublisherURL=https://github.com/JaceBelerenApologist/SwitchboardRM
AppSupportURL=https://github.com/JaceBelerenApologist/SwitchboardRM/issues
AppUpdatesURL=https://github.com/JaceBelerenApologist/SwitchboardRM/releases
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}

; "Install for me only" (default, no admin) or "Install for all users" (admin): a choice in the wizard, or
; /CURRENTUSER and /ALLUSERS on the command line. {autopf} follows it: Program Files or %LocalAppData%\Programs.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UsePreviousAppDir=yes

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

; close a running Switchboard before replacing its files
CloseApplications=yes
RestartApplications=no

WizardStyle=modern
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}

OutputDir=..\installer-output
OutputBaseFilename=SwitchboardRM-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
LZMANumBlockThreads=4

#ifdef Sign
; the signing command comes from the build (ISCC /Ssigntool=...): signs the setup and the uninstaller
SignTool=signtool
SignedUninstaller=yes
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\output\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; an upgrade replaces the bundled tools completely, so nothing from an older version is left mixed in
Type: filesandordirs; Name: "{app}\tools"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
