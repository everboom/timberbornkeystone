<#
.SYNOPSIS
    Find a Timberborn mod in the Steam Workshop folder by name, id, or list all.

.DESCRIPTION
    Scans the local Steam Workshop content folder for Timberborn (app 1062090),
    reads each mod's manifest.json to extract its Id, Name, and Version, and
    prints matches.  With no search term, lists every installed mod.

.PARAMETER Search
    Optional substring to match against mod Name or Id (case-insensitive).
    Omit to list all installed mods.

.PARAMETER WorkshopDir
    Path to the Timberborn workshop content folder.  Resolved in order:
    1. This parameter.
    2. KEYSTONE_WORKSHOP_DIR environment variable.
    If neither is set, the script errors.

.PARAMETER Full
    Show the full folder path for each match instead of just the Steam item id.

.EXAMPLE
    .\tools\find-workshop-mod.ps1 chronicle
    # Finds "Beaver Chronicles" (or anything with "chronicle" in name/id)

.EXAMPLE
    .\tools\find-workshop-mod.ps1
    # Lists all installed Timberborn workshop mods

.EXAMPLE
    .\tools\find-workshop-mod.ps1 -Search timber -Full
    # Search with full paths shown
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Search,

    [string]$WorkshopDir,

    [switch]$Full
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# --- Resolve workshop directory ---

if (-not $WorkshopDir) {
    $WorkshopDir = $env:KEYSTONE_WORKSHOP_DIR
}
if (-not $WorkshopDir) {
    Write-Error "Workshop directory not set. Pass -WorkshopDir or set KEYSTONE_WORKSHOP_DIR."
    return
}
if (-not (Test-Path $WorkshopDir)) {
    Write-Error "Workshop directory not found: $WorkshopDir`nSet -WorkshopDir or KEYSTONE_WORKSHOP_DIR."
    return
}

# --- Scan mod folders ---

$modFolders = Get-ChildItem -Path $WorkshopDir -Directory | Sort-Object Name

$results = [System.Collections.Generic.List[PSCustomObject]]::new()

foreach ($folder in $modFolders) {
    $manifest = $null
    $manifestPath = $null

    # Try root manifest first, then version subfolders (newest name first)
    $rootManifest = Join-Path $folder.FullName 'manifest.json'
    if (Test-Path $rootManifest) {
        $manifestPath = $rootManifest
    }
    else {
        $versionDirs = Get-ChildItem -Path $folder.FullName -Directory |
            Where-Object { $_.Name -like 'version-*' } |
            Sort-Object Name -Descending
        foreach ($vd in $versionDirs) {
            $candidate = Join-Path $vd.FullName 'manifest.json'
            if (Test-Path $candidate) {
                $manifestPath = $candidate
                break
            }
        }
    }

    if (-not $manifestPath) { continue }

    try {
        $raw = Get-Content -Path $manifestPath -Raw -Encoding UTF8
        # Workshop manifests are often sloppy JSON: trailing commas,
        # whole-line comments (// ...), block comments (/* ... */).
        # Strip before parsing so PowerShell 5.1's ConvertFrom-Json
        # can handle them. Only strip // on lines that are purely a
        # comment (not inside string values where URLs contain //).
        $raw = $raw -replace '/\*[\s\S]*?\*/', ''
        $raw = $raw -replace '(?m)^\s*//.*$', ''
        $raw = $raw -replace ',\s*([\]}])', '$1'
        $manifest = $raw | ConvertFrom-Json
    }
    catch {
        Write-Warning "Skipping $($folder.Name): failed to parse manifest.json"
        continue
    }

    $modId   = if ($manifest.Id)      { $manifest.Id }      else { '(none)' }
    $modName = if ($manifest.Name)    { $manifest.Name }    else { '(none)' }
    $version = if ($manifest.Version) { $manifest.Version } else { '?' }

    $results.Add([PSCustomObject]@{
        SteamId  = $folder.Name
        Id       = $modId
        Name     = $modName
        Version  = $version
        Path     = $folder.FullName
    })
}

# --- Filter ---

if ($Search) {
    $results = @($results | Where-Object {
        $_.Name -like "*$Search*" -or $_.Id -like "*$Search*"
    })
}

# --- Output ---

if ($results.Count -eq 0) {
    if ($Search) {
        Write-Host "No mods matching '$Search' in $WorkshopDir"
    }
    else {
        Write-Host "No mods found in $WorkshopDir"
    }
    return
}

if ($Full) {
    $results | Format-Table -AutoSize -Property Id, Name, Version, Path
}
else {
    $results | Format-Table -AutoSize -Property Id, Name, Version, SteamId
}
