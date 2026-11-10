<#
.SYNOPSIS
  Checks the helper tools installed on this PC (the Session Manager plugin and the AWS CLI),
  updates any that are out of date, and then refreshes the project's tools\ folder with collect-tools.ps1.

.DESCRIPTION
  Not part of the build: run it whenever you want to bring the tools up to date.

      powershell -ExecutionPolicy Bypass -File .\update-tools.ps1              # check, update, refresh tools\
      powershell -ExecutionPolicy Bypass -File .\update-tools.ps1 -CheckOnly   # just report what's out of date
      powershell -ExecutionPolicy Bypass -File .\update-tools.ps1 -NoCollect   # update, but leave tools\ alone

  The Session Manager plugin and the AWS CLI are updated with winget (built into Windows 10/11 as "App Installer"),
  using their official installers; Windows may ask for admin rights for those. Microsoft ConPTY is fetched fresh by
  collect-tools.ps1 each time it runs.

  OpenSSH and the WebView2 runtime (which Switchboard's Microsoft sign-in and terminals use) are Windows components
  kept up to date by Windows Update, so they aren't handled here.
#>
param(
    [switch]$CheckOnly,
    [switch]$NoCollect
)

$ErrorActionPreference = 'Stop'
$results = New-Object System.Collections.Generic.List[object]

function Get-Ver([string]$text) {
    # first dotted version number in the text, e.g. "v22.11.0" -> 22.11.0, "aws-cli/2.18.3 Python/..." -> 2.18.3
    if ($text -match '(\d+)\.(\d+)(\.\d+)?(\.\d+)?') { return [version]$Matches[0] }
    return $null
}

function Add-Result($tool, $installed, $latest, $status) {
    $results.Add([pscustomobject]@{ Tool = $tool; Installed = $installed; Latest = $latest; Status = $status })
}

function Refresh-Path {
    # pick up PATH changes made by installers during this run
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
}

$winget = (Get-Command winget -ErrorAction SilentlyContinue).Source
if (-not $winget) {
    Write-Warning 'winget was not found (it comes with "App Installer" from the Microsoft Store). The Session Manager plugin and the AWS CLI will only be checked, not updated.'
}

function Get-WingetLatest([string]$id) {
    if (-not $winget) { return $null }
    $out = & $winget show --id $id --exact --accept-source-agreements --disable-interactivity 2>$null
    $line = $out | Where-Object { $_ -match '^\s*Version:\s*(\S+)' } | Select-Object -First 1
    if ($line -and $line -match '^\s*Version:\s*(\S+)') { return Get-Ver $Matches[1] }
    return $null
}

function Update-Winget([string]$id, [string]$name) {
    Write-Host "  Updating $name with winget ..." -ForegroundColor Cyan
    & $winget upgrade --id $id --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
    if ($LASTEXITCODE -ne 0) {
        # winget doesn't always recognise a copy installed by hand; installing over it is the fallback
        Write-Host "  winget upgrade didn't apply (code $LASTEXITCODE); trying a fresh install over it ..." -ForegroundColor Yellow
        & $winget install --id $id --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
    }
    $ok = ($LASTEXITCODE -eq 0)
    $global:LASTEXITCODE = 0
    Refresh-Path
    return $ok
}

# One winget-managed tool: compare, update if older, report.
function Check-WingetTool([string]$name, [string]$id, [scriptblock]$installedVersion) {
    $installed = $null
    try { $installed = & $installedVersion } catch { }
    $latest = Get-WingetLatest $id
    $iText = if ($installed) { $installed.ToString() } else { 'not installed' }
    $lText = if ($latest) { $latest.ToString() } else { 'unknown' }

    if (-not $latest) { Add-Result $name $iText $lText 'couldn''t check'; return }
    if ($installed -and $installed -ge $latest) { Add-Result $name $iText $lText 'up to date'; return }
    if ($CheckOnly) { Add-Result $name $iText $lText 'update available'; return }

    if (Update-Winget $id $name) {
        $now = $null
        try { $now = & $installedVersion } catch { }
        $nText = if ($now) { $now.ToString() } else { '?' }
        Add-Result $name $iText $lText "updated to $nText"
    } else {
        Add-Result $name $iText $lText 'update failed (see above)'
    }
}

Write-Host 'Checking the helper tools on this PC ...' -ForegroundColor Green

# ---------------------------------------------------------------- 1. Session Manager plugin
Check-WingetTool 'Session Manager plugin' 'Amazon.SessionManagerPlugin' {
    $p = (Get-Command session-manager-plugin -ErrorAction SilentlyContinue).Source
    if (-not $p) { $p = Join-Path $env:ProgramFiles 'Amazon\SessionManagerPlugin\bin\session-manager-plugin.exe' }
    if (Test-Path $p) { Get-Ver (& $p --version) } else { $null }
}

# ---------------------------------------------------------------- 2. AWS CLI v2
Check-WingetTool 'AWS CLI v2' 'Amazon.AWSCLI' {
    $a = (Get-Command aws -ErrorAction SilentlyContinue).Source
    if (-not $a) { $a = Join-Path $env:ProgramFiles 'Amazon\AWSCLIV2\aws.exe' }
    if (Test-Path $a) { Get-Ver (& $a --version) } else { $null }
}

# ---------------------------------------------------------------- report
Write-Host ''
$results | Format-Table -AutoSize | Out-String -Width 220 | Write-Host   # explicit width: no console when run hidden

$changed = @($results | Where-Object { $_.Status -like 'updated*' }).Count -gt 0
$failed = @($results | Where-Object { $_.Status -like '*failed*' }).Count -gt 0

if ($CheckOnly) {
    Write-Host 'Check only: nothing was changed. Run without -CheckOnly to update.' -ForegroundColor Green
    return
}

# ---------------------------------------------------------------- refresh tools\
$collect = Join-Path $PSScriptRoot 'collect-tools.ps1'
if ($NoCollect) {
    Write-Host 'Skipped refreshing tools\ (-NoCollect).' -ForegroundColor Green
} elseif (-not (Test-Path $collect)) {
    Write-Host 'collect-tools.ps1 isn''t next to this script, so tools\ wasn''t refreshed.' -ForegroundColor Yellow
} else {
    Write-Host 'Refreshing tools\ from the installed tools (collect-tools.ps1) ...' -ForegroundColor Green
    & $collect
    Write-Host ''
    Write-Host 'tools\ is up to date. Publish (publish.cmd) to put it in output\.' -ForegroundColor Green
    if ($changed) {
        Write-Host 'Tools were updated: release this as a new PATCH version if you distribute the bundle.' -ForegroundColor Green
    }
}
if ($failed) { Write-Host 'Some updates failed; see the messages above.' -ForegroundColor Yellow }
