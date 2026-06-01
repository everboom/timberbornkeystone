# inspect-faction-buildings.ps1
#
# Walks a Timberborn mod folder, finds every blueprint that declares
# a BlockObjectSpec, and prints a categorisable summary: name, size,
# occupations, whether it's enterable, and a suggested Keystone
# classification (default settle / no-aura / transparent) based on
# name and shape heuristics. Used when adding a new faction's
# buildings to Keystone.Core.Buildings.Factions/.
#
# Usage:
#   tools\inspect-faction-buildings.ps1 -ModRoot <path>
#   tools\inspect-faction-buildings.ps1 -ModRoot <path> -FactionSuffix Emberpelts
#
# Output: a markdown table to stdout. Pipe to a file with `> out.md`
# if you want to keep it.
#
# Heuristics (the "Suggested" column):
#   - transparent: name contains Beehive / Scarecrow / Weathervane / Dynamite
#   - noaura-auto: name contains Zipline / Tube / Overhang / SuspensionBridge
#       (already caught by the BlueprintNamePolicy.IsStructuralPath
#        substring heuristic — no list entry needed)
#   - noaura?: shape/name suggests a no-aura candidate (small footprint,
#              decoration / flag / sensor / production-in-the-wild)
#   - default: has EnterableSpec and no special-case match — likely
#              full settle + aura
#   - path: occupies as Path with no EnterableSpec — vanilla classifies
#           as Path; only the substring heuristic could promote it
#
# Suggestions are advisory. Read the table, agree or override.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ModRoot,

    [Parameter(Mandatory = $false)]
    [string] $FactionSuffix = ""
)

if (-not (Test-Path -LiteralPath $ModRoot)) {
    Write-Error "ModRoot not found: $ModRoot"
    exit 1
}

# Patterns we suggest a category from. Order = priority (first match wins).
$transparentNamePatterns = @('Beehive', 'Scarecrow', 'Weathervane', 'Dynamite')
$noAuraAutoPatterns      = @('Zipline', 'Tube', 'Overhang', 'SuspensionBridge')
$noAuraDecorationPatterns = @(
    'Lantern', 'Hedge', 'Bench', 'Hammock', 'Shrub',
    'BeaverStatue', 'BeaverBust', 'Brazier', 'Bell', 'DecorativeClock',
    'PoleBanner', 'SquareBanner', 'BulletinPole', 'WoodFence', 'MetalFence',
    'Pillar', 'Decal'
)
$noAuraProductionPatterns = @('Forester', 'TappersShack', 'Farmhouse', 'FarmHouse')
$noAuraFlagPatterns       = @('Flag')
$noAuraSensorPatterns     = @(
    'StreamGauge', 'ContaminationSensor', 'DepthSensor', 'FlowSensor',
    'WeatherStation', 'Chronometer', 'PopulationCounter', 'PowerMeter',
    'ResourceCounter', 'ScienceCounter', 'Indicator', 'Lever', 'Memory',
    'Relay', 'Speaker', 'Timer', 'HttpAdapter', 'HttpLever'
)

function Test-NameMatchesAny {
    param(
        [string] $Name,
        [string[]] $Patterns
    )
    foreach ($p in $Patterns) {
        if ($Name -like "*$p*") { return $true }
    }
    return $false
}

function Suggest-Category {
    param(
        [string] $BlueprintName,
        [bool]   $HasEnterableSpec,
        [bool]   $HasBuildingSpec,
        [string] $Occupations,
        [string] $SizeStr
    )

    if (Test-NameMatchesAny -Name $BlueprintName -Patterns $transparentNamePatterns) {
        return 'transparent'
    }
    if (Test-NameMatchesAny -Name $BlueprintName -Patterns $noAuraAutoPatterns) {
        return 'noaura-auto'
    }
    if (Test-NameMatchesAny -Name $BlueprintName -Patterns $noAuraDecorationPatterns) {
        return 'noaura? (decoration)'
    }
    if (Test-NameMatchesAny -Name $BlueprintName -Patterns $noAuraProductionPatterns) {
        return 'noaura? (production in wild)'
    }
    if (Test-NameMatchesAny -Name $BlueprintName -Patterns $noAuraFlagPatterns) {
        return 'noaura? (designation flag)'
    }
    if (Test-NameMatchesAny -Name $BlueprintName -Patterns $noAuraSensorPatterns) {
        return 'noaura? (sensor)'
    }

    # Shape-only fallbacks.
    if (-not $HasBuildingSpec) {
        return '(no BuildingSpec)'
    }
    if ($Occupations -like '*Path*' -and -not $HasEnterableSpec) {
        return 'path (only substring heuristic can promote)'
    }
    if ($HasEnterableSpec) {
        return 'default (enterable)'
    }
    return 'default? (no Enterable, no path)'
}

# Collect rows first so we can sort + render at the end.
$rows = New-Object System.Collections.Generic.List[object]

$files = Get-ChildItem -LiteralPath $ModRoot -Recurse -Filter '*.blueprint.json' -ErrorAction SilentlyContinue
foreach ($file in $files) {
    # Skip .optional overlays — those are modifiers on existing
    # blueprints, not standalone buildings.
    if ($file.Name -like '*.optional.blueprint.json') { continue }

    # Faction suffix filter (e.g. "Emberpelts") — applied to the
    # filename minus the .blueprint.json extension.
    $stem = $file.Name -replace '\.blueprint\.json$', ''
    if ($FactionSuffix -ne "") {
        if ($stem -notlike "*.$FactionSuffix") { continue }
    }

    # Parse JSON. Skip silently on parse failure — we only need
    # well-formed blueprints.
    try {
        $json = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    } catch {
        continue
    }

    # Must have a BlockObjectSpec to be a placeable thing.
    if (-not $json.PSObject.Properties['BlockObjectSpec']) { continue }

    $bos = $json.BlockObjectSpec
    $hasBuildingSpec  = [bool]$json.PSObject.Properties['BuildingSpec']
    $hasEnterableSpec = [bool]$json.PSObject.Properties['EnterableSpec']

    # Size: BlockObjectSpec.Size = { X, Y, Z }.
    $sizeStr = ''
    if ($bos.PSObject.Properties['Size']) {
        $sz = $bos.Size
        $sizeStr = "{0}x{1}x{2}" -f $sz.X, $sz.Y, $sz.Z
    }

    # Occupations from the first Block entry (representative of the
    # ground-level voxel for path detection).
    $occupations = ''
    if ($bos.PSObject.Properties['Blocks'] -and $bos.Blocks.Count -gt 0) {
        $firstBlock = $bos.Blocks[0]
        if ($firstBlock.PSObject.Properties['Occupations']) {
            $occupations = [string]$firstBlock.Occupations
        }
    }

    # Science cost (cheap signal for "real building" vs "decoration").
    $scienceCost = 0
    if ($hasBuildingSpec -and $json.BuildingSpec.PSObject.Properties['ScienceCost']) {
        $scienceCost = [int]$json.BuildingSpec.ScienceCost
    }

    $suggested = Suggest-Category `
        -BlueprintName $stem `
        -HasEnterableSpec $hasEnterableSpec `
        -HasBuildingSpec $hasBuildingSpec `
        -Occupations $occupations `
        -SizeStr $sizeStr

    $rows.Add([PSCustomObject]@{
        Name        = $stem
        Size        = $sizeStr
        Occupations = $occupations
        Enterable   = if ($hasEnterableSpec) { 'yes' } else { '' }
        Building    = if ($hasBuildingSpec)  { 'yes' } else { '' }
        Sci         = if ($scienceCost -gt 0) { $scienceCost } else { '' }
        Suggested   = $suggested
    })
}

$suffixNote = ''
if ($FactionSuffix -ne '') { $suffixNote = ", suffix .$FactionSuffix" }

if ($rows.Count -eq 0) {
    Write-Host "(no buildings found under $ModRoot$suffixNote)"
    exit 0
}

# Markdown header.
Write-Host ""
Write-Host "## Buildings under $ModRoot$suffixNote"
Write-Host ""
Write-Host ("Total: {0} block-object blueprint(s)." -f $rows.Count)
Write-Host ""

# Render table. Sort by suggested-category-group, then name, so the
# reader can scan transparent/noaura/default in coherent chunks.
$categoryOrder = @{
    'transparent'                         = 0
    'noaura-auto'                         = 1
    'noaura? (decoration)'                = 2
    'noaura? (production in wild)'        = 3
    'noaura? (designation flag)'          = 4
    'noaura? (sensor)'                    = 5
    'path (only substring heuristic can promote)' = 6
    'default (enterable)'                 = 7
    'default? (no Enterable, no path)'    = 8
    '(no BuildingSpec)'                   = 9
}

$sorted = $rows | Sort-Object `
    @{ Expression = { if ($categoryOrder.ContainsKey($_.Suggested)) { $categoryOrder[$_.Suggested] } else { 99 } } }, `
    @{ Expression = 'Name' }

Write-Host "| Name | Size | Occupations | Ent | Bld | Sci | Suggested |"
Write-Host "|------|------|-------------|-----|-----|-----|-----------|"
foreach ($r in $sorted) {
    Write-Host ("| {0} | {1} | {2} | {3} | {4} | {5} | {6} |" -f `
        $r.Name, $r.Size, $r.Occupations, $r.Enterable, $r.Building, $r.Sci, $r.Suggested)
}

# Summary counts by suggested category.
Write-Host ""
Write-Host "### Summary"
Write-Host ""
$rows | Group-Object -Property Suggested | Sort-Object -Property Count -Descending | ForEach-Object {
    Write-Host ("- **{0}**: {1}" -f $_.Name, $_.Count)
}
