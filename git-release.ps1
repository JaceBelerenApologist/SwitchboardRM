<#
.SYNOPSIS
  Commits the project and pushes it to GitHub, where the Build workflow publishes the release for its version.
  Doesn't change any version numbers.

.DESCRIPTION
  Run it from the project folder after making (and versioning) your changes:

      powershell -ExecutionPolicy Bypass -File .\git-release.ps1                  # version from SwitchboardRM.csproj
      powershell -ExecutionPolicy Bypass -File .\git-release.ps1 -Version 2.6.1   # or say which version
      powershell -ExecutionPolicy Bypass -File .\git-release.ps1 -DryRun          # only show what it would do

  1. The version: -Version, or <Version> in SwitchboardRM.csproj (the number the app shows).
  2. Checks: the version looks like X.Y.Z, CHANGELOG.md has a section for it, vX.Y.Z hasn't been released yet
     (no tag here or on GitHub), and no file over 50 MB is about to be committed.
  3. Commits everything with "Release X.Y.Z" and that version's changelog notes as the description.
  4. If GitHub has commits you don't have yet (e.g. an edit made on the website), rebases onto them first.
  5. Pushes. GitHub's Build workflow then builds it with the tools bundled and publishes release vX.Y.Z (making the
     tag), with that version's changelog notes. The script prints where to follow it.

  It asks before pushing; -Yes skips the question. -Message replaces the commit's first line.
#>
param(
    [string]$Version,
    [string]$Message,
    [switch]$Yes,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Git-Raw {
    # Runs git and returns what it printed as plain text; its exit code is left in $LASTEXITCODE for the caller.
    # Git prints warnings (like "LF will be replaced by CRLF") on its error output, and Windows PowerShell 5.1 turns
    # anything there into errors that 'Stop' made fatal, so errors are judged by the exit code instead.
    $ErrorActionPreference = 'Continue'
    & git -c core.safecrlf=false @args 2>&1 | ForEach-Object { "$_" }
}

function Invoke-Git {
    # run git; stop with its own message if it fails
    $out = Git-Raw @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed:`n$($out -join "`n")" }
    return $out
}

function Stop-With([string]$why) {
    Write-Host $why -ForegroundColor Red
    exit 1
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Stop-With 'git was not found. Install Git for Windows first.' }
if (-not (Test-Path .git)) { Stop-With 'This folder isn''t a git repository yet (see the README for connecting it to GitHub).' }
$remote = Git-Raw remote get-url origin
$remote = if ($LASTEXITCODE -eq 0) { "$($remote | Select-Object -First 1)".Trim() } else { $null }
if (-not $remote) { Stop-With 'There''s no "origin" remote. Add it with: git remote add origin <repository URL>' }

# ---------------------------------------------------------------- version
if (-not $Version) {
    $proj = Get-ChildItem -Filter *.csproj | Select-Object -First 1
    if (-not $proj) { Stop-With 'No .csproj found here; pass -Version X.Y.Z.' }
    $m = [regex]::Match((Get-Content $proj.FullName -Raw), '<Version>\s*([^<\s]+)\s*</Version>')
    if (-not $m.Success) { Stop-With "No <Version> in $($proj.Name); pass -Version X.Y.Z." }
    $Version = $m.Groups[1].Value
    Write-Host "Version $Version (from $($proj.Name))"
}
$Version = $Version.Trim().TrimStart('v', 'V')
if ($Version -notmatch '^\d+\.\d+\.\d+$') { Stop-With "'$Version' isn't a version like 2.6.1." }
$tag = "v$Version"

# ---------------------------------------------------------------- changelog notes for this version
$notes = ''
if (Test-Path CHANGELOG.md) {
    $lines = Get-Content CHANGELOG.md
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match ('^##\s+' + [regex]::Escape($Version) + '(\s|$)')) { $start = $i; break }
    }
    if ($start -ge 0) {
        $body = New-Object System.Collections.Generic.List[string]
        for ($i = $start + 1; $i -lt $lines.Count -and $lines[$i] -notmatch '^##\s'; $i++) { $body.Add($lines[$i]) }
        $notes = ($body -join "`n").Trim()
    }
}
if (-not $notes) {
    Write-Warning "CHANGELOG.md has no section for $Version. The commit and the release will have no release notes."
}

# ---------------------------------------------------------------- the tag mustn't exist yet
Git-Raw rev-parse -q --verify "refs/tags/$tag" | Out-Null
if ($LASTEXITCODE -eq 0) { Stop-With "$tag is already released (the tag exists here). Bump the version first." }
$global:LASTEXITCODE = 0
$remoteTag = Git-Raw ls-remote --tags origin "refs/tags/$tag"
if ($LASTEXITCODE -ne 0) { $remoteTag = $null }   # couldn't ask GitHub: the push will say if something's wrong
if ($remoteTag) { Stop-With "$tag is already released on GitHub. Bump the version first." }

# ---------------------------------------------------------------- what's being committed
$branch = (Invoke-Git rev-parse --abbrev-ref HEAD | Select-Object -First 1).ToString().Trim()
Invoke-Git add -A | Out-Null
$staged = @(Git-Raw diff --cached --name-status | Where-Object { $_ })
$big = @(Git-Raw diff --cached --name-only --diff-filter=AM | Where-Object { $_ -and (Test-Path $_) -and (Get-Item $_).Length -gt 50MB })
if ($big.Count -gt 0) {
    Invoke-Git reset -q | Out-Null
    Stop-With ("These files are over 50 MB, too big for a GitHub repository (they belong in a release download):`n  " + ($big -join "`n  "))
}

$subject = if ($Message) { $Message } else { "Release $Version" }

Write-Host ''
Write-Host "Repository : $remote"
Write-Host "Branch     : $branch"
Write-Host "Release    : $tag (published by GitHub's Build workflow)"
Write-Host "Commit     : $(if ($staged.Count) { "$subject ($($staged.Count) changed file(s))" } else { 'nothing new to commit (push only)' })"
if ($staged.Count) { $staged | Select-Object -First 25 | ForEach-Object { Write-Host "   $_" } }
if ($staged.Count -gt 25) { Write-Host "   ... and $($staged.Count - 25) more" }
if ($notes) { Write-Host ''; Write-Host 'Release notes:'; ($notes -split "`n") | ForEach-Object { Write-Host "   $_" } }
Write-Host ''

if ($DryRun) {
    Invoke-Git reset -q | Out-Null   # leave nothing staged
    Write-Host 'Dry run: nothing was committed, tagged or pushed.' -ForegroundColor Green
    exit 0
}
if (-not $Yes) {
    $answer = Read-Host "Commit and push to GitHub (which then publishes $tag)? (y/N)"
    if ($answer -notmatch '^(y|yes)$') { Invoke-Git reset -q | Out-Null; Write-Host 'Cancelled; nothing was changed.'; exit 0 }
}

# ---------------------------------------------------------------- commit, catch up, push
if ($staged.Count) {
    $msgFile = [System.IO.Path]::GetTempFileName()
    Set-Content -Path $msgFile -Value ($subject + $(if ($notes) { "`n`n" + $notes } else { '' })) -Encoding UTF8
    Invoke-Git commit -q -F $msgFile | Out-Null
    Remove-Item $msgFile
    Write-Host "Committed: $subject"
}

Invoke-Git fetch -q origin | Out-Null
$hasUpstream = Git-Raw rev-parse --verify -q "origin/$branch"
if ($LASTEXITCODE -ne 0) { $hasUpstream = $null }
$global:LASTEXITCODE = 0
if ($hasUpstream) {
    $behind = [int](Invoke-Git rev-list --count "HEAD..origin/$branch" | Select-Object -First 1).ToString()
    if ($behind -gt 0) {
        Write-Host "GitHub has $behind commit(s) you don't have; putting yours on top of them ..."
        Git-Raw pull --rebase -q origin $branch | ForEach-Object { Write-Host $_ }
        if ($LASTEXITCODE -ne 0) {
            Stop-With ("The rebase hit a conflict. Fix the files git lists, then run 'git rebase --continue' " +
                       "(or 'git rebase --abort' to undo it), and run this script again.")
        }
    }
}

$before = Git-Raw rev-parse -q --verify "origin/$branch"
if ($LASTEXITCODE -ne 0) { $before = $null }
$global:LASTEXITCODE = 0
Invoke-Git push -q -u origin $branch | Out-Null
$after = Git-Raw rev-parse -q --verify "origin/$branch"
if ($LASTEXITCODE -ne 0) { $after = $null }
$global:LASTEXITCODE = 0
$pushedNew = "$before" -ne "$after"
Write-Host $(if ($pushedNew) { "Pushed $branch" } else { "$branch was already up to date on GitHub" })


# ---------------------------------------------------------------- where to make the release
$web = $remote -replace '^git@github\.com:', 'https://github.com/' -replace '\.git$', ''
Write-Host ''
if (-not $pushedNew) {
    # no new commit means no automatic build: start one by hand with the release box ticked
    Write-Host "Nothing new reached GitHub, so no build starts by itself. To publish $tag from what's there now, run" -ForegroundColor Yellow
    Write-Host "the Build workflow by hand with 'Publish the release' ticked:"
    Write-Host "  $web/actions/workflows/build.yml"
} else {
    Write-Host "Done. GitHub is building and publishing $tag (about 10 minutes); follow it at:" -ForegroundColor Green
    Write-Host "  $web/actions"
    Write-Host "When it finishes, the release with the zipped app is at:"
    Write-Host "  $web/releases/tag/$tag"
}
