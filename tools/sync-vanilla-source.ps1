<#
.SYNOPSIS
    Mirror the decompiled vanilla Timberborn C# source for each game version
    into a repo-local, gitignored folder so it can be searched/read without
    permission prompts on the external drive.

.DESCRIPTION
    For every "vX.Y" folder under -AssetsRoot, locates the AssetRipper-exported
    "Scripts" directory (its parent layout varies between exports) and mirrors
    it to dump/vanilla-src/<vX.Y> via robocopy /MIR. Idempotent: re-running only
    copies changed files. The destination lives under dump/ which is gitignored.

.PARAMETER AssetsRoot
    Root holding per-version export folders. Resolved in order:
    1. This parameter.
    2. KEYSTONE_ASSETS_ROOT environment variable.
    3. Default "D:\Timberborn Assets".

.PARAMETER DestRoot
    Where to mirror to. Defaults to <repo>/dump/vanilla-src.

.EXAMPLE
    .\tools\sync-vanilla-source.ps1
    # Mirrors every vX.Y export's Scripts tree into dump/vanilla-src/
#>
[CmdletBinding()]
param(
    [string]$AssetsRoot,
    [string]$DestRoot = (Join-Path $PSScriptRoot '..\dump\vanilla-src')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $AssetsRoot) { $AssetsRoot = $env:KEYSTONE_ASSETS_ROOT }
if (-not $AssetsRoot) { $AssetsRoot = 'D:\Timberborn Assets' }

if (-not (Test-Path $AssetsRoot)) {
    Write-Error "Assets root not found: $AssetsRoot`nPass -AssetsRoot or set KEYSTONE_ASSETS_ROOT."
    return
}

$DestRoot = [System.IO.Path]::GetFullPath($DestRoot)
New-Item -ItemType Directory -Force -Path $DestRoot | Out-Null

$versionDirs = Get-ChildItem -Path $AssetsRoot -Directory |
    Where-Object { $_.Name -match '^v\d+\.\d+' } |
    Sort-Object Name

if (-not $versionDirs) {
    Write-Warning "No 'vX.Y' version folders found under $AssetsRoot."
    return
}

foreach ($vd in $versionDirs) {
    # The "Scripts" dir sits at a version-specific depth (e.g.
    # v1.0\Content\Scripts vs v1.1\AssetRipper_export_*\Scripts).
    $scripts = Get-ChildItem -Path $vd.FullName -Directory -Recurse -Filter 'Scripts' -ErrorAction SilentlyContinue |
        Select-Object -First 1
    if (-not $scripts) {
        Write-Warning "  $($vd.Name): no Scripts dir found, skipping."
        continue
    }

    $dest = Join-Path $DestRoot $vd.Name
    Write-Host "Mirroring $($vd.Name): $($scripts.FullName) -> $dest"

    # /MIR mirror, /NFL /NDL quiet file/dir lists, /NP no progress,
    # /R:1 /W:1 fail fast on locked files, multithreaded.
    robocopy $scripts.FullName $dest /MIR /NFL /NDL /NJH /NJS /NP /R:1 /W:1 /MT:16 | Out-Null
    # robocopy exit codes 0-7 are success; 8+ is a real failure.
    if ($LASTEXITCODE -ge 8) {
        Write-Error "  robocopy failed for $($vd.Name) (exit $LASTEXITCODE)."
    }
    else {
        $count = (Get-ChildItem -Path $dest -Recurse -File -Filter '*.cs' | Measure-Object).Count
        Write-Host "  done: $count .cs files."
    }
}

Write-Host "`nVanilla source mirrored under $DestRoot"

# robocopy sets $LASTEXITCODE to 1 on a successful copy; normalize so callers
# (and the tool harness) don't read this script as failed.
exit 0
