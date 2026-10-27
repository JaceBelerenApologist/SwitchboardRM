<#
.SYNOPSIS
  Copies the helper programs installed on THIS PC into .\tools, so Switchboard can ship with them and
  other PCs don't need Node.js, npm, aws-azure-login, the Session Manager plugin or the AWS CLI installed.

.DESCRIPTION
  Run it once from the project folder on a PC where everything is installed and working:

      powershell -ExecutionPolicy Bypass -File .\collect-tools.ps1

  The tools stay in .\tools, one subfolder each, and Switchboard uses them from there while you develop.
  Publishing copies .\tools next to SwitchboardRM.exe, keeping the same layout.
  Re-run it whenever you update Node.js, aws-azure-login or the plugin.

  -IncludeChromium also copies Puppeteer's own Chromium (about 300 MB). Without it, the sign-in page
  opens in Microsoft Edge, which every Windows 10/11 PC already has.
#>
param([switch]$IncludeChromium)

$ErrorActionPreference = 'Stop'
$root = Join-Path $PSScriptRoot 'tools'

function Copy-Tree([string]$from, [string]$to) {
    New-Item -ItemType Directory -Force $to | Out-Null
    robocopy $from $to /MIR /NFL /NDL /NJH /NJS /NP /R:1 /W:1 | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copy failed: $from -> $to (robocopy code $LASTEXITCODE)" }
    $global:LASTEXITCODE = 0
}

function Copy-Licenses([string]$fromDir, [string]$toDir) {
    Get-ChildItem $fromDir -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^(LICENSE|NOTICE|THIRD)' } |
        Copy-Item -Destination $toDir -Force
}

New-Item -ItemType Directory -Force $root | Out-Null

# 1. Node.js: only node.exe is needed to run aws-azure-login (npm is just the installer)
$node = (Get-Command node -ErrorAction SilentlyContinue).Source
if (-not $node) { throw 'node.exe was not found on PATH. Install Node.js LTS on this PC (only this PC) and run again.' }
$nodeDir = Join-Path $root 'node'
New-Item -ItemType Directory -Force $nodeDir | Out-Null
Copy-Item $node $nodeDir -Force
Copy-Licenses (Split-Path $node) $nodeDir
Write-Host ("Node.js {0}  ->  tools\node" -f (& $node --version))

# 2. aws-azure-login with all the packages it uses (global npm installs keep their own node_modules)
$npmRoot = (& npm root -g).Trim()
$aal = Join-Path $npmRoot 'aws-azure-login'
if (-not (Test-Path $aal)) { throw "aws-azure-login was not found in $npmRoot. Run: npm install -g aws-azure-login" }
Copy-Tree $aal (Join-Path $root 'aws-azure-login')
$aalVersion = (Get-Content (Join-Path $aal 'package.json') -Raw | ConvertFrom-Json).version
Write-Host "aws-azure-login $aalVersion  ->  tools\aws-azure-login"
if (Test-Path (Join-Path $aal 'node_modules\puppeteer\.local-chromium')) {
    Write-Host '  (its Chromium came along inside node_modules, so Edge won''t be needed)'
}

# 3. Optional: Puppeteer's Chromium from the user cache (newer Puppeteer versions keep it there)
$chromiumDir = Join-Path $root 'chromium'
if ($IncludeChromium) {
    $chrome = Get-ChildItem (Join-Path $env:USERPROFILE '.cache\puppeteer') -Recurse -Filter chrome.exe -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($chrome) {
        Copy-Tree $chrome.DirectoryName $chromiumDir
        Write-Host "Chromium  ->  tools\chromium"
    } else {
        Write-Warning 'No Puppeteer Chromium found in the user cache; the sign-in page will open in Microsoft Edge.'
    }
} elseif (Test-Path $chromiumDir) {
    Remove-Item $chromiumDir -Recurse -Force
    Write-Host 'Removed tools\chromium (run with -IncludeChromium to keep it); Microsoft Edge will be used.'
}

# 4. Session Manager plugin
$plugin = (Get-Command session-manager-plugin -ErrorAction SilentlyContinue).Source
if (-not $plugin) {
    $default = Join-Path $env:ProgramFiles 'Amazon\SessionManagerPlugin\bin\session-manager-plugin.exe'
    if (Test-Path $default) { $plugin = $default }
}
if (-not $plugin) { throw 'session-manager-plugin.exe was not found. Install the AWS Session Manager plugin on this PC and run again.' }
$pluginDir = Join-Path $root 'session-manager-plugin'
Copy-Tree (Split-Path $plugin) $pluginDir
Copy-Licenses (Split-Path (Split-Path $plugin)) $pluginDir
Write-Host ("Session Manager plugin {0}  ->  tools\session-manager-plugin" -f (& $plugin --version))

# 5. AWS CLI v2 (self-contained; only used by profiles that sign in with "aws sso login")
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
        Copy-Tree $awsHome (Join-Path $root 'aws-cli')
        Write-Host ("{0}  ->  tools\aws-cli" -f ((& $aws --version) -split ' ')[0])
    }
} else {
    Write-Warning 'AWS CLI v2 not found; skipping it (only needed for SSO profiles).'
}

$mb = (Get-ChildItem $root -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ''
Write-Host ("Done. tools folder: {0:N0} MB. Publish to ship it with the launcher." -f $mb) -ForegroundColor Green
