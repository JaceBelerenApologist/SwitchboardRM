<#
.SYNOPSIS
  Commits the project, tags it with its version and pushes both to GitHub. Doesn't change any version numbers.

.DESCRIPTION
  Run it from the project folder after making (and versioning) your changes:

      powershell -ExecutionPolicy Bypass -File .\git-release.ps1                  # version from SwitchboardRM.csproj
      powershell -ExecutionPolicy Bypass -File .\git-release.ps1 -Version 2.6.1   # or say which version
      powershell -ExecutionPolicy Bypass -File .\git-release.ps1 -DryRun          # only show what it would do

  1. The version: -Version, or <Version> in SwitchboardRM.csproj (the number the app shows).
  2. Checks: the version looks like X.Y.Z, CHANGELOG.md has a section for it, the tag vX.Y.Z doesn't exist yet
     (here or on GitHub), and no file over 50 MB is about to be committed.
  3. Commits everything with "Release X.Y.Z" and that version's changelog notes as the description.
  4. If GitHub has commits you don't have yet (e.g. an edit made on the website), rebases onto them first.
  5. Pushes, tags vX.Y.Z (with the changelog notes), pushes the tag, and prints the link to create the GitHub Release.

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

function Invoke-Git {
    # run git; stop with its own message if it fails
    $out = & git @args 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed:`n$($out -join "`n")" }
    return $out
}

function Stop-With([string]$why) {
    Write-Host $why -ForegroundColor Red
    exit 1
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Stop-With 'git was not found. Install Git for Windows first.' }
if (-not (Test-Path .git)) { Stop-With 'This folder isn''t a git repository yet (see the README for connecting it to GitHub).' }
$remote = (& git remote get-url origin 2>$null)
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
    Write-Warning "CHANGELOG.md has no section for $Version. The commit and tag will have no release notes."
}

# ---------------------------------------------------------------- the tag mustn't exist yet
& git rev-parse -q --verify "refs/tags/$tag" 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) { Stop-With "The tag $tag already exists here. Bump the version first, or delete the tag if it was a mistake." }
$global:LASTEXITCODE = 0
$remoteTag = & git ls-remote --tags origin "refs/tags/$tag" 2>$null
if ($remoteTag) { Stop-With "The tag $tag already exists on GitHub. Bump the version first." }

# ---------------------------------------------------------------- what's being committed
$branch = (Invoke-Git rev-parse --abbrev-ref HEAD | Select-Object -First 1).ToString().Trim()
Invoke-Git add -A | Out-Null
$staged = @(& git diff --cached --name-status)
$big = @(& git diff --cached --name-only --diff-filter=AM | Where-Object { (Test-Path $_) -and (Get-Item $_).Length -gt 50MB })
if ($big.Count -gt 0) {
    Invoke-Git reset -q | Out-Null
    Stop-With ("These files are over 50 MB, too big for a GitHub repository (they belong in a release download):`n  " + ($big -join "`n  "))
}

$subject = if ($Message) { $Message } else { "Release $Version" }

Write-Host ''
Write-Host "Repository : $remote"
Write-Host "Branch     : $branch"
Write-Host "Tag        : $tag"
Write-Host "Commit     : $(if ($staged.Count) { "$subject ($($staged.Count) changed file(s))" } else { 'nothing new to commit (tag and push only)' })"
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
    $answer = Read-Host "Commit, tag $tag and push to GitHub? (y/N)"
    if ($answer -notmatch '^(y|yes)$') { Invoke-Git reset -q | Out-Null; Write-Host 'Cancelled; nothing was changed.'; exit 0 }
}

# ---------------------------------------------------------------- commit, catch up, push, tag
if ($staged.Count) {
    $msgFile = [System.IO.Path]::GetTempFileName()
    Set-Content -Path $msgFile -Value ($subject + $(if ($notes) { "`n`n" + $notes } else { '' })) -Encoding UTF8
    Invoke-Git commit -q -F $msgFile | Out-Null
    Remove-Item $msgFile
    Write-Host "Committed: $subject"
}

Invoke-Git fetch -q origin | Out-Null
$hasUpstream = (& git rev-parse --verify -q "origin/$branch" 2>$null)
$global:LASTEXITCODE = 0
if ($hasUpstream) {
    $behind = [int](Invoke-Git rev-list --count "HEAD..origin/$branch" | Select-Object -First 1).ToString()
    if ($behind -gt 0) {
        Write-Host "GitHub has $behind commit(s) you don't have; putting yours on top of them ..."
        & git pull --rebase -q origin $branch
        if ($LASTEXITCODE -ne 0) {
            Stop-With ("The rebase hit a conflict. Fix the files git lists, then run 'git rebase --continue' " +
                       "(or 'git rebase --abort' to undo it), and run this script again.")
        }
    }
}

Invoke-Git push -q -u origin $branch | Out-Null
Write-Host "Pushed $branch"

$tagMsgFile = [System.IO.Path]::GetTempFileName()
Set-Content -Path $tagMsgFile -Value ("Switchboard Remote Manager $Version" + $(if ($notes) { "`n`n" + $notes } else { '' })) -Encoding UTF8
Invoke-Git tag -a $tag -F $tagMsgFile | Out-Null
Remove-Item $tagMsgFile
Invoke-Git push -q origin $tag | Out-Null
Write-Host "Tagged and pushed $tag"

# ---------------------------------------------------------------- where to make the release
$web = $remote -replace '^git@github\.com:', 'https://github.com/' -replace '\.git$', ''
Write-Host ''
if (Test-Path (Join-Path $PSScriptRoot '.github\workflows\build.yml')) {
    # the Build workflow builds the release (with the tools bundled) for every version tag
    Write-Host "Done. GitHub is now building the $tag release (about 10 minutes); follow it at:" -ForegroundColor Green
    Write-Host "  $web/actions"
    Write-Host "When it finishes, the release with the zipped app is at:"
    Write-Host "  $web/releases/tag/$tag"
} else {
    Write-Host "Done. Create the GitHub Release (and attach the zipped output\ folder) at:" -ForegroundColor Green
    Write-Host "  $web/releases/new?tag=$tag"
}
