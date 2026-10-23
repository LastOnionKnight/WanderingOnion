# release.ps1 - Unified release script for GearGoblin and Tonberry Tactics
# ASCII only (PowerShell 5.1 chokes on UTF-8 BOM).
#
# What it does:
#   1. Finds the .csproj file in the current directory.
#   2. Reads <Version>X.Y.Z</Version> from it.
#   3. Stages all currently-modified tracked files.
#   3.5 v0.4.6 BUILD GATE (carried forward in v0.4.7): runs `dotnet build --configuration Release` and
#       bails on failure. Catches typos, unclosed tags, broken imports
#       locally instead of letting them reach origin/main.
#   4. Pulls the CHANGELOG entry for this version (if CHANGELOG.md exists).
#   5. Commits with that message (now BOM-free under v0.4.6 + v0.4.7 â€” uses
#      [System.IO.File]::WriteAllText with UTF8Encoding(false) instead of
#      Set-Content -Encoding UTF8 which emits a BOM under PS 5.1).
#   6. Tags vX.Y.Z.
#   7. Pushes commits and tag to origin/main with --follow-tags.
#
# Same script works in:
#   D:\GearGoblin-v0.1\GearGoblin\
#   D:\TonberryTactics-workspace\TonberryTactics\
# Just drop it next to the .csproj and run .\release.ps1
#
# Flags:
#   -DryRun       Show what would happen, do not commit/tag/push.
#   -Message msg  Override the auto-generated commit message.
#   -SkipPush     Commit and tag locally, do not push.
#   -SkipBuild    Skip the dotnet build gate (rare; only for fast iteration
#                 when you've just verified the build manually).

param(
    [switch]$DryRun,
    [string]$Message = "",
    [switch]$SkipPush,
    [switch]$SkipBuild   # v0.4.6+: bypass the dotnet build gate (rare; fast-iterate only)
)

$ErrorActionPreference = "Stop"

# ---- 1. Find the csproj ------------------------------------------------------

$csprojFiles = Get-ChildItem -Path $PSScriptRoot -Filter "*.csproj" -File
if ($csprojFiles.Count -eq 0) {
    Write-Host "ERROR: No .csproj file found in $PSScriptRoot." -ForegroundColor Red
    Write-Host "Run this script from the project root." -ForegroundColor Yellow
    exit 1
}
if ($csprojFiles.Count -gt 1) {
    Write-Host "ERROR: Multiple .csproj files found. Cannot auto-detect project." -ForegroundColor Red
    $csprojFiles | ForEach-Object { Write-Host "  - $($_.Name)" }
    exit 1
}

$csproj      = $csprojFiles[0]
$projectName = [System.IO.Path]::GetFileNameWithoutExtension($csproj.Name)
Write-Host ""
Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "  release.ps1" -ForegroundColor Cyan
Write-Host "==================================================" -ForegroundColor Cyan
Write-Host "  Project:  $projectName" -ForegroundColor White
Write-Host "  csproj:   $($csproj.Name)" -ForegroundColor White
Write-Host "  cwd:      $PSScriptRoot" -ForegroundColor White

# ---- 2. Read the version from csproj -----------------------------------------

$csprojContent = Get-Content $csproj.FullName -Raw
if ($csprojContent -match "<Version>([\d\.]+)</Version>") {
    $version = $Matches[1]
} else {
    Write-Host "ERROR: Could not find <Version>X.Y.Z</Version> in $($csproj.Name)." -ForegroundColor Red
    exit 1
}

# Normalize version: tag is vX.Y.Z (no trailing .0).
# Versions like 0.4.2.0 -> 0.4.2 for the tag.
$tagVersion = $version -replace '\.0+$', ''
if ($tagVersion -notmatch '^\d+\.\d+\.\d+') {
    $tagVersion = $version  # fall back to raw if normalization confuses things
}
$tag = "v$tagVersion"
Write-Host "  Version:  $version" -ForegroundColor White
Write-Host "  Tag:      $tag" -ForegroundColor White
Write-Host ""

# ---- 3. Check git state ------------------------------------------------------

# Are we in a git repo?
git rev-parse --is-inside-work-tree 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Not inside a git repository." -ForegroundColor Red
    exit 1
}

# Does the tag already exist?
$existingTag = git tag --list $tag
if ($existingTag) {
    Write-Host "ERROR: Tag $tag already exists. Bump the version in $($csproj.Name) first." -ForegroundColor Red
    exit 1
}

# What's the current branch?
$branch = git rev-parse --abbrev-ref HEAD
Write-Host "  Branch:   $branch" -ForegroundColor White

# ---- 2.5. Sync with remote (v0.6.5.2+) --------------------------------------
#
# Fetch and rebase before staging. Without this step, any commits on
# origin/main that we don't have locally (most commonly the
# github-actions[bot] repo.json bumps that fire after every tag push) will
# cause the final `git push` to be rejected as non-fast-forward â€” exactly
# the failure mode that derailed the v0.6.5.1 ship.
#
# --autostash handles the working-tree dropin changes during the rebase:
# git stashes them automatically, runs the rebase, then restores them on
# top. If the rebase would conflict with our dropin files (unlikely; the
# bot only touches repo.json), the rebase aborts cleanly and we surface
# the error.
#
# We do this BEFORE the status display so the "Changes to be committed"
# view reflects the post-rebase state, not whatever was in the working
# tree before sync.

Write-Host "Syncing with origin/$branch (fetch + rebase + autostash)..." -ForegroundColor Cyan
git fetch origin $branch
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: git fetch failed." -ForegroundColor Red
    exit 1
}
git pull --rebase --autostash origin $branch
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "ERROR: git pull --rebase failed." -ForegroundColor Red
    Write-Host "If the rebase aborted mid-way (conflict), resolve manually:" -ForegroundColor Yellow
    Write-Host "  git status   # see what's conflicting" -ForegroundColor Yellow
    Write-Host "  git rebase --abort   # back out cleanly" -ForegroundColor Yellow
    Write-Host "Then re-extract the dropin and re-run release.ps1." -ForegroundColor Yellow
    exit 1
}
Write-Host ""

# Show what's staged + unstaged.
$status = git status --short
if (-not $status) {
    Write-Host ""
    Write-Host "WARNING: No changes detected. Nothing to commit." -ForegroundColor Yellow
    Write-Host "Did you forget to copy the dropin files into place?" -ForegroundColor Yellow
    if (-not $DryRun) { exit 1 }
}

Write-Host ""
Write-Host "Changes to be committed:" -ForegroundColor Yellow
git status --short
Write-Host ""

# ---- 3.5. Build gate (v0.4.6+, carried forward) -----------------------------------------------
#
# Run `dotnet build` against the project before we commit/tag/push, so that
# a typo, unclosed </div>, or import-resolution issue is caught locally
# instead of reaching origin/main and only failing in CI / Cloudflare's
# build queue (which is how the v0.4.5 era TT hotfix friction happened).
#
# Configuration is Release by default â€” same build flavor that ships to
# users. Output goes through Out-Host so the build log is visible in the
# console; we capture the exit code to decide whether to bail.

if (-not $SkipBuild) {
    Write-Host "Running build gate: dotnet build --configuration Release..." -ForegroundColor Cyan

    # Extract changelog for embedded resource
    $changelogPath = Join-Path $PSScriptRoot "CHANGELOG.md"
    if (Test-Path $changelogPath) {
        $utf8 = New-Object System.Text.UTF8Encoding $false
        $changelogContent = [System.IO.File]::ReadAllText($changelogPath, $utf8)
        $parts = $changelogContent -split '(?m)^## \['
        $numToTake = [Math]::Min(16, $parts.Length)
        $extracted = $parts[0]
        for ($i = 1; $i -lt $numToTake; $i++) {
            $extracted += "## [" + $parts[$i]
        }
        $resourcePath = Join-Path $PSScriptRoot "Resources\about-changelog.txt"
        $dir = Split-Path $resourcePath
        if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        $utf8NoBom = New-Object System.Text.UTF8Encoding $false
        [System.IO.File]::WriteAllText($resourcePath, $extracted.Trim(), $utf8NoBom)
        Write-Host "Extracted $( $numToTake - 1 ) changelog entries to $resourcePath" -ForegroundColor Cyan
    }

    Write-Host "----------------------------------------" -ForegroundColor DarkGray
    dotnet build --configuration Release --nologo
    $buildExit = $LASTEXITCODE
    Write-Host "----------------------------------------" -ForegroundColor DarkGray
    if ($buildExit -ne 0) {
        Write-Host ""
        Write-Host "ERROR: dotnet build failed (exit code $buildExit)." -ForegroundColor Red
        Write-Host "Fix the build before releasing. To bypass (NOT recommended), pass -SkipBuild." -ForegroundColor Yellow
        exit 1
    }
    Write-Host "Build gate: OK." -ForegroundColor Green
    Write-Host ""
} else {
    Write-Host "Build gate skipped (-SkipBuild)." -ForegroundColor Yellow
    Write-Host ""
}

# ---- 4. Generate commit message from CHANGELOG (if it exists) ----------------

if (-not $Message) {
    $changelogPath = Join-Path $PSScriptRoot "CHANGELOG.md"
    if (Test-Path $changelogPath) {
        $utf8 = New-Object System.Text.UTF8Encoding $false
        $changelog = [System.IO.File]::ReadAllText($changelogPath, $utf8)
        # Match "## [X.Y.Z] - YYYY-MM-DD" through to the next "## [" or EOF.
        # Use [\s\S] for cross-line matching.
        $pattern = "## \[$([regex]::Escape($tagVersion))\][\s\S]*?(?=## \[|\z)"
        if ($changelog -match $pattern) {
            $entry = $Matches[0].Trim()
            # First line: "## [X.Y.Z] - 2026-05-11"
            # Use that as the subject. Strip the brackets.
            $firstLine = ($entry -split "`n")[0].Trim()
            $subject = $firstLine -replace '^## \[(.+?)\]\s*[-]+\s*(.+)$', "$projectName $1 - $2"
            if ($subject -eq $firstLine) {
                $subject = "$projectName $tagVersion"
            }
            # Body: everything after the subject line, trimmed.
            $body = ($entry -split "`n", 2)[1].Trim()
            $Message = "$subject`n`n$body"
        } else {
            Write-Host "WARNING: No CHANGELOG entry found for version $tagVersion. Using fallback message." -ForegroundColor Yellow
            $Message = "$projectName $tagVersion"
        }
    } else {
        $Message = "$projectName $tagVersion"
    }
}

Write-Host "Commit message preview:" -ForegroundColor Yellow
Write-Host "----------------------------------------" -ForegroundColor DarkGray
Write-Host $Message
Write-Host "----------------------------------------" -ForegroundColor DarkGray
Write-Host ""

# ---- 5. Dry-run early exit ---------------------------------------------------

if ($DryRun) {
    Write-Host "DRY RUN - would now:" -ForegroundColor Cyan
    Write-Host "  git add -A"
    Write-Host "  git commit -m <message above>"
    Write-Host "  git tag $tag"
    if (-not $SkipPush) {
        Write-Host "  git push origin $branch --follow-tags"
    }
    Write-Host ""
    Write-Host "Done (dry run). No changes made." -ForegroundColor Green
    exit 0
}

# ---- 6. Commit + tag + push --------------------------------------------------

Write-Host "Staging all changes..." -ForegroundColor Cyan
git add -A
if ($LASTEXITCODE -ne 0) { Write-Host "git add failed" -ForegroundColor Red; exit 1 }

Write-Host "Committing..." -ForegroundColor Cyan
# v0.4.6+: write the commit message with NO BOM. PS 5.1's `Set-Content -Encoding UTF8`
# emits a UTF-8 BOM, which git then preserves verbatim â€” the result is commit
# subjects that look like 'ï»¿GearGoblin 0.4.5' (invisible BOM prefix). The
# explicit [System.Text.UTF8Encoding]::new($false) constructor disables BOM
# emission and gives us clean ASCII/UTF-8 messages.
$msgFile = [System.IO.Path]::GetTempFileName()
[System.IO.File]::WriteAllText($msgFile, $Message, [System.Text.UTF8Encoding]::new($false))
git commit -F $msgFile
$commitExit = $LASTEXITCODE
Remove-Item $msgFile -Force
if ($commitExit -ne 0) {
    Write-Host "git commit failed" -ForegroundColor Red
    exit 1
}

Write-Host "Tagging $tag..." -ForegroundColor Cyan
git tag -a $tag -m "$projectName $tagVersion"
if ($LASTEXITCODE -ne 0) { Write-Host "git tag failed" -ForegroundColor Red; exit 1 }

if ($SkipPush) {
    Write-Host ""
    Write-Host "Local commit + tag complete. Push skipped (-SkipPush)." -ForegroundColor Green
    Write-Host "To push later: git push origin $branch --follow-tags" -ForegroundColor White
    exit 0
}

Write-Host "Pushing to origin/$branch with tags..." -ForegroundColor Cyan
git push origin $branch --follow-tags
if ($LASTEXITCODE -ne 0) {
    Write-Host "git push failed" -ForegroundColor Red
    Write-Host "Local commit + tag are saved. Retry with: git push origin $branch --follow-tags" -ForegroundColor Yellow
    exit 1
}

Write-Host ""
Write-Host "==================================================" -ForegroundColor Green
Write-Host "  Release complete: $projectName $tag" -ForegroundColor Green
Write-Host "==================================================" -ForegroundColor Green
Write-Host ""
Write-Host "⚠️  REMINDER: Do not leave Core and Web behind!" -ForegroundColor Yellow
Write-Host "If this was a lockstep version bump, make sure to also bump and run release in:" -ForegroundColor Yellow
Write-Host "  - GearGoblin.Core" -ForegroundColor Yellow
Write-Host "  - TonberryTactics (Web)" -ForegroundColor Yellow
Write-Host "==================================================" -ForegroundColor Green

# ── Dropin artifact pruning (added 2026-05-20) ──────────────────────
# Keep the last $Keep dropin zips + extracted folders in ~/Downloads
# for rollback. Runs at end-of-script so a failed release leaves the
# rollback artifacts untouched.

function Invoke-DropinPrune {
    [CmdletBinding()]
    param(
        [int]    $Keep         = 3,
        [string] $DownloadsDir = "$env:USERPROFILE\Downloads",
        [string] $Pattern      = "GearGoblin-v*-dropin"
    )

    Write-Host ""
    Write-Host "── Dropin cleanup (keep last $Keep for rollback) ──" -ForegroundColor Cyan

    $zips = @(Get-ChildItem -Path $DownloadsDir -Filter "$Pattern.zip" -File -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime -Descending)
    $dirs = @(Get-ChildItem -Path $DownloadsDir -Filter $Pattern -Directory -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime -Descending)

    if ($zips.Count -gt $Keep) {
        foreach ($file in ($zips | Select-Object -Skip $Keep)) {
            Write-Host "  Removing $($file.Name) ($($file.LastWriteTime.ToString('yyyy-MM-dd')))" -ForegroundColor DarkGray
            Remove-Item $file.FullName -Force
        }
    }

    if ($dirs.Count -gt $Keep) {
        foreach ($dir in ($dirs | Select-Object -Skip $Keep)) {
            Write-Host "  Removing $($dir.Name)/" -ForegroundColor DarkGray
            Remove-Item $dir.FullName -Recurse -Force
        }
    }

    $kept = $zips | Select-Object -First $Keep | ForEach-Object { $_.Name }
    if ($kept) {
        Write-Host "  Retained: $($kept -join ', ')" -ForegroundColor Green
    } else {
        Write-Host "  Nothing to retain (no dropins found)" -ForegroundColor DarkGray
    }
}

Invoke-DropinPrune
