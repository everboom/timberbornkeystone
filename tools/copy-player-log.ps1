<#
.SYNOPSIS
    Copy Timberborn's Player.log and Player-prev.log into the repo's
    dump/ folder for easy inspection.

.DESCRIPTION
    Timberborn writes diagnostic output to
    %USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn\Player.log
    (current/last run) and Player-prev.log (the run before that, kept
    after the next start rotates it). On a crash, the relevant
    traceback is usually in Player.log; if you've already restarted
    the game and lost it, Player-prev.log has the previous session.

    This script copies both files (whichever exist) into dump/ under
    the repo root with fixed names (Player.log, Player-prev.log), so
    the latest log is always at the same path. The dump/ folder is
    gitignored.

    Run after a crash or unexpected behavior, then point Claude at
    dump/Player.log so it can read the traceback directly without
    needing it pasted into the chat.

.PARAMETER SourceDir
    Override the source directory. Defaults to
    %USERPROFILE%\AppData\LocalLow\Mechanistry\Timberborn.

.PARAMETER DestDir
    Override the destination directory. Defaults to <repo>\dump.

.EXAMPLE
    .\tools\copy-player-log.ps1

.EXAMPLE
    .\tools\copy-player-log.ps1 -DestDir C:\tmp\timberlogs
#>
[CmdletBinding()]
param(
    [string]$SourceDir,
    [string]$DestDir
)

$ErrorActionPreference = 'Stop'

if (-not $SourceDir) {
    $SourceDir = Join-Path $env:USERPROFILE 'AppData\LocalLow\Mechanistry\Timberborn'
}

if (-not $DestDir) {
    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $repoRoot  = Split-Path -Parent $scriptDir
    $DestDir   = Join-Path $repoRoot 'dump'
}

if (-not (Test-Path $SourceDir)) {
    Write-Error "Source directory not found: $SourceDir"
    exit 1
}

if (-not (Test-Path $DestDir)) {
    New-Item -ItemType Directory -Path $DestDir | Out-Null
}

$logs = @('Player.log', 'Player-prev.log')
$copied = 0
$missing = @()

foreach ($name in $logs) {
    $src = Join-Path $SourceDir $name
    if (-not (Test-Path $src)) {
        $missing += $name
        continue
    }
    $dst = Join-Path $DestDir $name
    # /B = binary copy; -Force handles the read-only-by-OneDrive case some users hit.
    Copy-Item -Path $src -Destination $dst -Force
    $size = (Get-Item $dst).Length
    $mtime = (Get-Item $src).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')
    Write-Output ("  {0,-16} -> {1}  ({2:N0} bytes, src mtime {3})" -f $name, $dst, $size, $mtime)
    $copied++
}

if ($copied -eq 0) {
    Write-Warning "No log files found in $SourceDir"
    exit 1
}

if ($missing.Count -gt 0) {
    Write-Output ""
    Write-Output ("Note: not present in source (skipped): {0}" -f ($missing -join ', '))
}
