<#
.SYNOPSIS
  Copies the helper programs installed on THIS PC into .\tools, together with their licence files and
  third-party notices, so Switchboard can be distributed with them and other PCs don't need the Session Manager
  plugin or the AWS CLI installed. Microsoft sign-in is built into Switchboard and needs nothing from here.

.DESCRIPTION
  Run it from the project folder on a PC where the tools are installed and working (see the README,
  "Installing the helper tools yourself"):

      powershell -ExecutionPolicy Bypass -File .\collect-tools.ps1

  Result:
      tools\THIRD-PARTY-NOTICES.txt           what's bundled: versions, licences, sources
      tools\session-manager-plugin\           the plugin + its LICENSE / NOTICE
      tools\aws-cli\                          AWS CLI v2 (SSO sign-in, and aws commands in Switchboard's shells)
      tools\openssh\                          only if you put an OpenSSH build there yourself (left untouched)
      tools\conpty\                           Microsoft's ConPTY (conpty.dll + OpenConsole.exe, from the Windows
                                              Terminal project), downloaded from NuGet: lets Windows sign-in
                                              windows (WAM) appear for commands in Switchboard's terminals

  Where a tool's installation has no licence file, a LICENSE-NOTE.txt names the licence and where to read it.
  The tools stay in .\tools and Switchboard uses them from there while you develop; publishing copies .\tools
  next to SwitchboardRM.exe with the same layout. Re-run this after updating any of the tools.

  Folders from tools that are no longer bundled (node, aws-azure-login, chromium: Microsoft sign-in used to need them)
  are removed.
#>
param()

$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot 'tools'
$summary = New-Object System.Collections.Generic.List[object]

function Copy-Tree([string]$from, [string]$to) {
    New-Item -ItemType Directory -Force $to | Out-Null
    robocopy $from $to /MIR /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copy failed: $from -> $to (robocopy code $LASTEXITCODE)" }
    $global:LASTEXITCODE = 0
}

$licencePattern = '^(LICEN[CS]E|COPYING|NOTICE|THIRD[-_ ]?PARTY)'

function Copy-Licenses([string]$fromDir, [string]$toDir) {
    Get-ChildItem $fromDir -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match $licencePattern } |
        Copy-Item -Destination $toDir -Force
}

# The licence files in a tool's folder; if there are none, write LICENSE-NOTE.txt naming the licence and its source.
function Ensure-Licence([string]$dir, [string]$tool, [string]$licence, [string]$url) {
    $files = @(Get-ChildItem $dir -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $licencePattern })
    if ($files.Count -gt 0) { return ($files.Name -join ', ') }
    Set-Content -Path (Join-Path $dir 'LICENSE-NOTE.txt') -Encoding UTF8 -Value @(
        "$tool is distributed under the $licence.",
        "Its installation didn't include a licence file. The full licence text is at:",
        "  $url"
    )
    return 'LICENSE-NOTE.txt'
}

function Add-Summary($tool, $version, $licence, $source, $folder, $files) {
    $summary.Add([pscustomobject]@{ Tool = $tool; Version = $version; Licence = $licence; Source = $source; Folder = $folder; Files = $files })
}

New-Item -ItemType Directory -Force $root | Out-Null

# ---------------------------------------------------------------- no longer bundled
# Microsoft sign-in is built into Switchboard now: Node.js, aws-azure-login and Chromium aren't needed any more
foreach ($old in 'node', 'aws-azure-login', 'chromium') {
    $dir = Join-Path $root $old
    if (Test-Path $dir) {
        Remove-Item $dir -Recurse -Force
        Write-Host "Removed tools\$old (no longer needed: Microsoft sign-in is built in)."
    }
}

# ---------------------------------------------------------------- 1. Session Manager plugin
$plugin = (Get-Command session-manager-plugin -ErrorAction SilentlyContinue).Source
if (-not $plugin) {
    $default = Join-Path $env:ProgramFiles 'Amazon\SessionManagerPlugin\bin\session-manager-plugin.exe'
    if (Test-Path $default) { $plugin = $default }
}
if (-not $plugin) { throw 'session-manager-plugin.exe was not found. Install the AWS Session Manager plugin on this PC and run again (see the README).' }
$pluginDir = Join-Path $root 'session-manager-plugin'
Copy-Tree (Split-Path $plugin) $pluginDir
Copy-Licenses (Split-Path (Split-Path $plugin)) $pluginDir   # the licence files sit one level above bin\
$pluginVersion = (& $plugin --version).Trim()
$files = Ensure-Licence $pluginDir 'The AWS Session Manager plugin' 'Apache License 2.0' 'https://github.com/aws/session-manager-plugin/blob/mainline/LICENSE'
Add-Summary 'Session Manager plugin' $pluginVersion 'Apache-2.0' 'https://github.com/aws/session-manager-plugin' 'session-manager-plugin\' $files
Write-Host "Session Manager plugin $pluginVersion  ->  tools\session-manager-plugin"

# ---------------------------------------------------------------- 2. AWS CLI v2 (SSO sign-in, aws commands in shells)
$aws = (Get-Command aws -ErrorAction SilentlyContinue).Source
if (-not $aws) {
    $default = Join-Path $env:ProgramFiles 'Amazon\AWSCLIV2\aws.exe'
    if (Test-Path $default) { $aws = $default }
}
if ($aws) {
    $awsHome = Split-Path $aws
    if ((Split-Path $awsHome -Leaf) -eq 'bin') { $awsHome = Split-Path $awsHome }   # some installs put aws.exe in bin\
    if (-not (Test-Path (Join-Path $awsHome 'aws.exe'))) {
        Write-Warning "Found $aws but not a self-contained AWS CLI v2 folder; skipping the AWS CLI."
    } else {
        $awsDir = Join-Path $root 'aws-cli'
        Copy-Tree $awsHome $awsDir
        # the example sections of "aws <command> help" (thousands of small files): help pages work without them, and
        # nothing else reads them, so they're left out to keep installs and unzipping quick
        $examples = Join-Path $awsDir 'awscli\examples'
        if (Test-Path $examples) {
            $count = (Get-ChildItem $examples -Recurse -File).Count
            Remove-Item $examples -Recurse -Force
            Write-Host "  (left out the CLI's help examples: $count files)"
        }
        $awsVersion = ((& $aws --version) -split ' ')[0] -replace '^aws-cli/', ''
        $files = Ensure-Licence $awsDir 'The AWS CLI v2' 'Apache License 2.0 (it also bundles Python and other components under their own licences)' 'https://github.com/aws/aws-cli/blob/v2/LICENSE.txt'
        Add-Summary 'AWS CLI v2' $awsVersion 'Apache-2.0 (+ bundled Python etc.)' 'https://github.com/aws/aws-cli' 'aws-cli\' $files
        Write-Host "AWS CLI $awsVersion  ->  tools\aws-cli"
    }
} else {
    Write-Warning 'AWS CLI v2 not found; skipping it (only needed for SSO profiles).'
}

# ---------------------------------------------------------------- 3. OpenSSH (only if placed there by hand)
$sshDir = Join-Path $root 'openssh'
if (Test-Path (Join-Path $sshDir 'ssh.exe')) {
    $sshVersion = (Get-Item (Join-Path $sshDir 'ssh.exe')).VersionInfo.ProductVersion
    $files = Ensure-Licence $sshDir 'Win32-OpenSSH' 'BSD-style licences of OpenSSH and its components' 'https://github.com/PowerShell/Win32-OpenSSH/blob/latestw_all/LICENSE.txt'
    Add-Summary 'OpenSSH (Win32-OpenSSH)' $sshVersion 'BSD-style' 'https://github.com/PowerShell/Win32-OpenSSH' 'openssh\' $files
    Write-Host "OpenSSH $sshVersion  (already in tools\openssh, left as is)"
}

# ---------------------------------------------------------------- 4. Microsoft ConPTY (conpty.dll + OpenConsole.exe)
# Switchboard's terminals use it, when present, so their hidden console windows can be owned by Switchboard's window:
# that's what lets WAM sign-in windows (Connect-MgGraph, Connect-AzAccount ...) appear. Without it they still work.
$conptyDir = Join-Path $root 'conpty'
$conptyVersion = $null
$conptyFrom = @()
try {
    $zip = Join-Path $env:TEMP 'switchboard-conpty.zip'
    $unz = Join-Path $env:TEMP 'switchboard-conpty'
    Invoke-WebRequest 'https://www.nuget.org/api/v2/package/Microsoft.Windows.Console.ConPTY' -OutFile $zip -UseBasicParsing
    if (Test-Path $unz) { Remove-Item $unz -Recurse -Force }
    Expand-Archive $zip $unz -Force
    $nuspec = Get-ChildItem $unz -Filter *.nuspec | Select-Object -First 1
    if ($nuspec) { $conptyVersion = ([xml](Get-Content $nuspec.FullName -Raw)).package.metadata.version }
    $conptyFrom += $unz
} catch {
    Write-Warning "Couldn't download Microsoft's ConPTY package from NuGet: $($_.Exception.Message)"
}
try {
    # fallback: the copy inside an installed Windows Terminal
    $wt = Get-AppxPackage -Name Microsoft.WindowsTerminal -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($wt) { $conptyFrom += $wt.InstallLocation; if (-not $conptyVersion) { $conptyVersion = "$($wt.Version) (Windows Terminal)" } }
} catch { }

$conptyDone = $false
foreach ($src in $conptyFrom) {
    # the x64 builds (the package also has arm64 and x86)
    $dll = Get-ChildItem $src -Recurse -Filter conpty.dll -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch 'arm|x86' } | Select-Object -First 1
    $exe = Get-ChildItem $src -Recurse -Filter OpenConsole.exe -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch 'arm|x86' } | Select-Object -First 1
    if (-not ($dll -and $exe)) { continue }
    if (Test-Path $conptyDir) { Remove-Item $conptyDir -Recurse -Force }
    New-Item -ItemType Directory -Force $conptyDir | Out-Null
    Copy-Item $dll.FullName $conptyDir
    Copy-Item $exe.FullName $conptyDir
    Get-ChildItem $src -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $licencePattern } |
        ForEach-Object { Copy-Item $_.FullName $conptyDir }
    $files = Ensure-Licence $conptyDir 'Microsoft ConPTY (Windows Terminal)' 'MIT licence' 'https://github.com/microsoft/terminal/blob/main/LICENSE'
    Add-Summary 'Microsoft ConPTY (conpty.dll, OpenConsole.exe)' $conptyVersion 'MIT' 'https://github.com/microsoft/terminal' 'conpty\' $files
    Write-Host "Microsoft ConPTY $conptyVersion"
    $conptyDone = $true
    break
}
if (-not $conptyDone) {
    Write-Warning ("Microsoft ConPTY not collected: Switchboard's terminals will use Windows' built-in pseudo-console, " +
                   "where Windows sign-in windows (WAM) can't appear.")
}

# ---------------------------------------------------------------- summary notices
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('Third-party tools bundled with Switchboard Remote Manager')
$lines.Add("Collected on $(Get-Date -Format yyyy-MM-dd) by collect-tools.ps1.")
$lines.Add('')
$lines.Add('These programs are made by others and distributed here under their own licences, which are included')
$lines.Add('in each tool''s folder. Switchboard itself is under the MIT License (see the project''s LICENSE file).')
$lines.Add('')
$lines.Add(('{0,-26} {1,-16} {2,-36} {3}' -f 'Tool', 'Version', 'Licence', 'Source'))
$lines.Add(('-' * 120))
foreach ($s in $summary) {
    $lines.Add(('{0,-26} {1,-16} {2,-36} {3}' -f $s.Tool, $s.Version, $s.Licence, $s.Source))
}
$lines.Add('')
$lines.Add('Licence files:')
foreach ($s in $summary) { $lines.Add(('  {0,-26} {1}{2}' -f $s.Tool, $s.Folder, $s.Files)) }
Set-Content -Path (Join-Path $root 'THIRD-PARTY-NOTICES.txt') -Encoding UTF8 -Value $lines

$mb = (Get-ChildItem $root -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ''
Write-Host ("Done. tools folder: {0:N0} MB, with licences and THIRD-PARTY-NOTICES.txt. Publish to ship it." -f $mb) -ForegroundColor Green
