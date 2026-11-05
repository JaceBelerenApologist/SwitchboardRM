<#
.SYNOPSIS
  Copies the helper programs installed on THIS PC into .\tools, together with their licence files and
  third-party notices, so Switchboard can be distributed with them and other PCs don't need Node.js, npm,
  aws-azure-login, the Session Manager plugin or the AWS CLI installed.

.DESCRIPTION
  Run it from the project folder on a PC where the tools are installed and working (see the README,
  "Installing the helper tools yourself"):

      powershell -ExecutionPolicy Bypass -File .\collect-tools.ps1

  Result:
      tools\THIRD-PARTY-NOTICES.txt           what's bundled: versions, licences, sources
      tools\node\                             node.exe + Node's LICENSE (covers what's built into node.exe)
      tools\aws-azure-login\                  the package + node_modules; THIRD-PARTY-NOTICES.txt lists every
                                              package with its version, licence and licence text
      tools\session-manager-plugin\           the plugin + its LICENSE / NOTICE
      tools\aws-cli\                          AWS CLI v2 (only used by SSO profiles) + its licence files
      tools\chromium\                         only with -IncludeChromium
      tools\openssh\                          only if you put an OpenSSH build there yourself (left untouched)

  Where a tool's installation has no licence file, a LICENSE-NOTE.txt names the licence and where to read it.
  The tools stay in .\tools and Switchboard uses them from there while you develop; publishing copies .\tools
  next to SwitchboardRM.exe with the same layout. Re-run this after updating any of the tools.

  -IncludeChromium also copies Puppeteer's own Chromium (about 300 MB) for the sign-in page.
#>
param([switch]$IncludeChromium)

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

# ---------------------------------------------------------------- 1. Node.js
# Only node.exe is needed to run aws-azure-login (npm is just the installer). Node's LICENSE file also covers
# everything built into node.exe (OpenSSL, ICU, V8, libuv, ...).
$node = (Get-Command node -ErrorAction SilentlyContinue).Source
if (-not $node) { throw 'node.exe was not found on PATH. Install Node.js LTS on this PC and run again (see the README).' }
$nodeDir = Join-Path $root 'node'
if (Test-Path $nodeDir) { Remove-Item $nodeDir -Recurse -Force }
New-Item -ItemType Directory -Force $nodeDir | Out-Null
Copy-Item $node $nodeDir -Force
Copy-Licenses (Split-Path $node) $nodeDir
$nodeVersion = (& $node --version).Trim()
$files = Ensure-Licence $nodeDir 'Node.js' 'MIT License (with the licences of its bundled components)' 'https://github.com/nodejs/node/blob/main/LICENSE'
Add-Summary 'Node.js' $nodeVersion 'MIT (+ bundled components)' 'https://nodejs.org' 'node\' $files
Write-Host "Node.js $nodeVersion  ->  tools\node"

# ---------------------------------------------------------------- 2. aws-azure-login and its packages
$npmRoot = (& npm root -g).Trim()
$aal = Join-Path $npmRoot 'aws-azure-login'
if (-not (Test-Path $aal)) { throw "aws-azure-login was not found in $npmRoot. Run: npm install -g aws-azure-login" }
$aalDir = Join-Path $root 'aws-azure-login'
Copy-Tree $aal $aalDir
$aalVersion = (Get-Content (Join-Path $aal 'package.json') -Raw | ConvertFrom-Json).version

# Every package (aws-azure-login itself and everything under node_modules): name, version, licence, licence text.
$packages = @{}
Get-ChildItem $aalDir -Recurse -Filter package.json -File -ErrorAction SilentlyContinue | ForEach-Object {
    try { $pkg = Get-Content $_.FullName -Raw | ConvertFrom-Json } catch { return }
    if (-not $pkg.name -or -not $pkg.version) { return }   # helper package.json files that aren't packages
    $key = "$($pkg.name)@$($pkg.version)"
    if ($packages.ContainsKey($key)) { return }
    $licence = $null
    if ($pkg.license -is [string]) { $licence = $pkg.license }
    elseif ($pkg.license.type) { $licence = $pkg.license.type }
    elseif ($pkg.licenses) { $licence = (@($pkg.licenses) | ForEach-Object { $_.type }) -join ' OR ' }
    if (-not $licence) { $licence = 'not stated' }
    $text = Get-ChildItem $_.DirectoryName -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^(LICEN[CS]E|COPYING|NOTICE)' } |
        ForEach-Object { "--- $($_.Name) ---`r`n" + (Get-Content $_.FullName -Raw) }
    $packages[$key] = [pscustomobject]@{ Key = $key; Licence = $licence; Text = ($text -join "`r`n") }
}
$sorted = $packages.Values | Sort-Object Key
$out = New-Object System.Text.StringBuilder
[void]$out.AppendLine("Third-party notices for aws-azure-login $aalVersion and the packages it uses")
[void]$out.AppendLine("Generated by collect-tools.ps1 on $(Get-Date -Format yyyy-MM-dd). $($sorted.Count) packages.")
[void]$out.AppendLine('')
[void]$out.AppendLine('Licences used:')
$sorted | Group-Object Licence | Sort-Object Count -Descending | ForEach-Object {
    [void]$out.AppendLine(('  {0,-30} {1} package(s)' -f $_.Name, $_.Count))
}
[void]$out.AppendLine('')
[void]$out.AppendLine('Packages:')
foreach ($p in $sorted) { [void]$out.AppendLine(('  {0,-55} {1}' -f $p.Key, $p.Licence)) }
[void]$out.AppendLine('')
[void]$out.AppendLine('Licence texts (as included with each package):')
foreach ($p in $sorted) {
    [void]$out.AppendLine('')
    [void]$out.AppendLine(('=' * 100))
    [void]$out.AppendLine("$($p.Key)  ($($p.Licence))")
    [void]$out.AppendLine(('=' * 100))
    if ($p.Text) { [void]$out.AppendLine($p.Text) }
    else { [void]$out.AppendLine("(no licence file in the package; see https://www.npmjs.com/package/$($p.Key.Substring(0, $p.Key.LastIndexOf('@'))))") }
}
Set-Content -Path (Join-Path $aalDir 'THIRD-PARTY-NOTICES.txt') -Encoding UTF8 -Value $out.ToString()
$noText = @($sorted | Where-Object { -not $_.Text }).Count
Add-Summary 'aws-azure-login' $aalVersion 'MIT (packages: see its notices)' 'https://github.com/aws-azure-login/aws-azure-login' 'aws-azure-login\' 'LICENSE, THIRD-PARTY-NOTICES.txt'
Write-Host "aws-azure-login $aalVersion ($($sorted.Count) packages)  ->  tools\aws-azure-login"
if ($noText -gt 0) { Write-Host "  $noText package(s) have no licence file of their own; the notices name their licence and npm page." }

# ---------------------------------------------------------------- 3. Chromium (optional)
$chromiumDir = Join-Path $root 'chromium'
if ($IncludeChromium) {
    $chrome = Get-ChildItem (Join-Path $env:USERPROFILE '.cache\puppeteer') -Recurse -Filter chrome.exe -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($chrome) {
        Copy-Tree $chrome.DirectoryName $chromiumDir
        $chromeVersion = (Get-Item (Join-Path $chromiumDir 'chrome.exe')).VersionInfo.ProductVersion
        $files = Ensure-Licence $chromiumDir 'Chromium' 'BSD 3-Clause licence (with the licences of its many bundled components, listed at chrome://credits)' 'https://chromium.googlesource.com/chromium/src/+/main/LICENSE'
        Add-Summary 'Chromium' $chromeVersion 'BSD-3-Clause (+ components)' 'https://www.chromium.org' 'chromium\' $files
        Write-Host "Chromium $chromeVersion  ->  tools\chromium"
    } else {
        Write-Warning 'No Puppeteer Chromium found in the user cache; skipping it.'
    }
} elseif (Test-Path $chromiumDir) {
    Remove-Item $chromiumDir -Recurse -Force
    Write-Host 'Removed tools\chromium (run with -IncludeChromium to keep it).'
}

# ---------------------------------------------------------------- 4. Session Manager plugin
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

# ---------------------------------------------------------------- 5. AWS CLI v2 (only for SSO profiles)
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
        $awsVersion = ((& $aws --version) -split ' ')[0] -replace '^aws-cli/', ''
        $files = Ensure-Licence $awsDir 'The AWS CLI v2' 'Apache License 2.0 (it also bundles Python and other components under their own licences)' 'https://github.com/aws/aws-cli/blob/v2/LICENSE.txt'
        Add-Summary 'AWS CLI v2' $awsVersion 'Apache-2.0 (+ bundled Python etc.)' 'https://github.com/aws/aws-cli' 'aws-cli\' $files
        Write-Host "AWS CLI $awsVersion  ->  tools\aws-cli"
    }
} else {
    Write-Warning 'AWS CLI v2 not found; skipping it (only needed for SSO profiles).'
}

# ---------------------------------------------------------------- 6. OpenSSH (only if placed there by hand)
$sshDir = Join-Path $root 'openssh'
if (Test-Path (Join-Path $sshDir 'ssh.exe')) {
    $sshVersion = (Get-Item (Join-Path $sshDir 'ssh.exe')).VersionInfo.ProductVersion
    $files = Ensure-Licence $sshDir 'Win32-OpenSSH' 'BSD-style licences of OpenSSH and its components' 'https://github.com/PowerShell/Win32-OpenSSH/blob/latestw_all/LICENSE.txt'
    Add-Summary 'OpenSSH (Win32-OpenSSH)' $sshVersion 'BSD-style' 'https://github.com/PowerShell/Win32-OpenSSH' 'openssh\' $files
    Write-Host "OpenSSH $sshVersion  (already in tools\openssh, left as is)"
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
$lines.Add('')
$lines.Add('aws-azure-login''s own dependencies are listed, with their licence texts, in aws-azure-login\THIRD-PARTY-NOTICES.txt.')
Set-Content -Path (Join-Path $root 'THIRD-PARTY-NOTICES.txt') -Encoding UTF8 -Value $lines

$mb = (Get-ChildItem $root -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ''
$next = if (Test-Path (Join-Path $PSScriptRoot 'publish.cmd')) { 'Publish to ship it.' } else { 'Restart Switchboard to use it.' }
Write-Host ("Done. tools folder: {0:N0} MB, with licences and THIRD-PARTY-NOTICES.txt. {1}" -f $mb, $next) -ForegroundColor Green
