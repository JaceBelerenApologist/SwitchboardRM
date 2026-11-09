<#
.SYNOPSIS
  Builds the installer (installer-output\SwitchboardRM-Setup-<version>.exe) from what's in output\ now. Unsigned.

.DESCRIPTION
  For testing locally; release installers are built (and signed) by the GitHub workflow. Run publish.cmd first, so
  output\ holds the build you want. Needs Inno Setup 6 (https://jrsoftware.org/isinfo.php, or: winget install JRSoftware.InnoSetup).

      powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
#>
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$m = [regex]::Match((Get-Content SwitchboardRM.csproj -Raw), '<Version>\s*([^<\s]+)\s*</Version>')
if (-not $m.Success) { throw 'No <Version> in SwitchboardRM.csproj' }
$version = $m.Groups[1].Value
if (-not (Test-Path 'output\SwitchboardRM.exe')) { throw 'output\SwitchboardRM.exe is missing: run publish.cmd first.' }

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
          "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 was not found. Install it: winget install JRSoftware.InnoSetup' }

& $iscc /Q "/DAppVersion=$version" 'installer\SwitchboardRM.iss'
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed ($LASTEXITCODE)" }
$setup = Get-Item "installer-output\SwitchboardRM-Setup-$version.exe"
Write-Host ('Built {0} ({1:N0} MB, unsigned)' -f $setup.FullName, ($setup.Length / 1MB)) -ForegroundColor Green
