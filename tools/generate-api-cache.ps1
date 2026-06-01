<#
.SYNOPSIS
    Reflect over every Timberborn.*.dll and emit a tiered API reference.

.DESCRIPTION
    Walks the Managed directory of a Timberborn install, loads each
    Timberborn.* assembly, and writes a markdown reference with:
      - A type index for every public, non-nested type (kind + tier marker).
      - Full member signatures for "Tier A" types: all interfaces, plus
        concrete types whose name matches a modding-extension-point pattern
        (Service / System / Tracker / Manager / Registry / Provider /
        Spawner / Highlighter / Marker / Renderer / Drawer / Tool /
        Factory / Pool / Mediator / Hub / Configurator / Spec).
      - Per-DLL section with anchored heading and link from a top-level index.

    The output is mechanical and re-runnable; companion human-curated notes
    live in `docs/timberborn-api.md`.

.PARAMETER ManagedDir
    Either the Timberborn install root (containing Timberborn_Data\Managed)
    or the Managed directory itself. Falls back to KEYSTONE_TIMBERBORN_DIR.

.PARAMETER OutFile
    Output markdown path. Defaults to `<repo>\docs\timberborn-api-full.md`.

.EXAMPLE
    .\generate-api-cache.ps1
    .\generate-api-cache.ps1 -ManagedDir '<Timberborn install>'
#>
[CmdletBinding()]
param(
    [string]$ManagedDir = $env:KEYSTONE_TIMBERBORN_DIR,
    [string]$OutFile = (Join-Path $PSScriptRoot '..\docs\timberborn-api-full.md')
)

$ErrorActionPreference = 'Stop'

# --- Resolve managed dir -----------------------------------------------------

if (-not $ManagedDir) {
    throw "ManagedDir not specified. Pass -ManagedDir <path> or set KEYSTONE_TIMBERBORN_DIR env var."
}
$asInstallRoot = Join-Path $ManagedDir 'Timberborn_Data\Managed'
if (Test-Path -PathType Container $asInstallRoot) {
    $ManagedDir = $asInstallRoot
}
if (-not (Test-Path -PathType Container $ManagedDir)) {
    throw "ManagedDir does not exist: $ManagedDir"
}
$ManagedDir = (Resolve-Path $ManagedDir).Path

# --- Tier-A heuristic --------------------------------------------------------

$tierAPattern = '(Service|System|Tracker|Manager|Registry|Provider|Spawner|Highlighter|Marker|Renderer|Drawer|Tool|Factory|Pool|Mediator|Hub|Configurator|Spec|Module|Decorator|Listener|Validator|Component|Behavior|Event|EventArgs|Loader|Deserializer|Serializer|Instantiator|Bundle|Converter|Mapper|Cache|Initializer|Chain)$'

# Foundational types that don't match the suffix heuristic but are
# load-bearing enough that we always want their signatures cached.
# Match by short name (last segment of FullName).
$tierAExplicit = @(
    'BlockObject',
    'Blocks',
    'PositionedBlocks',
    'Block',
    'Placement',
    'EventBus',
    'OnEventAttribute',
    'BaseComponent',
    'Building',
    'Blueprint',
    'BlueprintAsset',
    'AssetRef',
    # Vanilla designation-area singletons. Same player-intent shape as
    # PlantingService (which gets auto-tiered via its 'Service' suffix)
    # but their type names don't match the suffix heuristic.
    'TreeCuttingArea'
)

# --- Watched interfaces (for the implementers appendix) ---------------------

$watchedInterfaces = [ordered]@{
    'Singleton lifecycle' = @(
        'Timberborn.SingletonSystem.ILoadableSingleton',
        'Timberborn.SingletonSystem.IPostLoadableSingleton',
        'Timberborn.SingletonSystem.IUpdatableSingleton',
        'Timberborn.SingletonSystem.ILateUpdatableSingleton',
        'Timberborn.SingletonSystem.IUnloadableSingleton'
    )
    'Entity lifecycle' = @(
        'Timberborn.EntitySystem.IInitializableEntity',
        'Timberborn.EntitySystem.IDeletableEntity',
        'Timberborn.BlockSystem.IFinishedStateListener',
        'Timberborn.WorldPersistence.IPersistentEntity'
    )
    'Modding hooks' = @(
        'Bindito.Core.IConfigurator',
        'Timberborn.DebuggingUI.IDebuggingPanel',
        'Timberborn.ModManagerScene.IModStarter'
    )
}

# --- Load assemblies ---------------------------------------------------------

Write-Host "Loading Timberborn DLLs from $ManagedDir..."
$dlls = Get-ChildItem -Path $ManagedDir -Filter 'Timberborn.*.dll' | Sort-Object Name
$assemblies = [ordered]@{}
$failures = @{}
foreach ($d in $dlls) {
    try {
        $a = [System.Reflection.Assembly]::LoadFrom($d.FullName)
        $assemblies[$d.BaseName] = $a
    } catch {
        $failures[$d.BaseName] = $_.Exception.Message
    }
}
Write-Host "Loaded $($assemblies.Count) of $($dlls.Count) DLLs."
if ($failures.Count -gt 0) {
    Write-Warning "Failed to load $($failures.Count) DLLs: $($failures.Keys -join ', ')"
}

# --- Helpers -----------------------------------------------------------------

function Get-TypeKind {
    param($t)
    if ($t.IsInterface) { return 'I' }
    if ($t.IsEnum) { return 'E' }
    if ($t.IsValueType) { return 'S' }
    if ($t.IsAbstract -and $t.IsSealed) { return 'X' }  # static
    if ($t.IsAbstract) { return 'A' }
    return 'C'
}

function Get-KindLabel {
    param([string]$kind)
    switch ($kind) {
        'I' { 'interface' }
        'E' { 'enum' }
        'S' { 'struct' }
        'A' { 'abstract class' }
        'X' { 'static class' }
        default { 'class' }
    }
}

# Auto-generated record/spec methods carry no signal -- they're implied by
# the IEquatable<T> / `<Clone>$` shape. Skipping them across the board
# (records, structs, regular classes) cuts ~30% off the cache without
# losing anything we'd actually grep for.
$noiseMethodNames = @('ToString', 'GetHashCode', 'Equals',
                       'op_Equality', 'op_Inequality', '<Clone>$')

function Get-PublicMembers {
    param($t)
    $bf = [System.Reflection.BindingFlags]::Public -bor `
          [System.Reflection.BindingFlags]::Instance -bor `
          [System.Reflection.BindingFlags]::Static -bor `
          [System.Reflection.BindingFlags]::DeclaredOnly
    # Records and record-like types have an auto-gen `<Clone>$` method.
    # Their parameterless ctor is also auto-generated and uninteresting.
    $isRecord = $t.GetMethod('<Clone>$',
        [System.Reflection.BindingFlags]'Public,NonPublic,Instance,DeclaredOnly') -ne $null
    $out = New-Object System.Collections.Generic.List[string]
    try {
        foreach ($m in $t.GetMembers($bf)) {
            if ($m.MemberType -eq 'Method') {
                $n = $m.Name
                if ($n.StartsWith('get_') -or $n.StartsWith('set_') -or
                    $n.StartsWith('add_') -or $n.StartsWith('remove_')) {
                    continue
                }
                if ($noiseMethodNames -contains $n) { continue }
            }
            if ($m.MemberType -eq 'Constructor' -and $isRecord -and
                $m.GetParameters().Count -eq 0) {
                continue
            }
            try {
                $out.Add($m.ToString())
            } catch {
                $out.Add("  (skipped: $($_.Exception.Message))")
            }
        }
    } catch {
        $out.Add("  (member enumeration failed: $($_.Exception.Message))")
    }
    return $out
}

function Get-AnchorName {
    param([string]$name)
    return ($name.ToLower() -replace '\.', '')
}

# Returns the public, non-nested, non-compiler-generated types from an
# assembly. Tries GetExportedTypes() first (clean, fast); if the runtime
# rejects a type (e.g., default-interface-method support varies between
# .NET runtimes), falls back to GetTypes() and salvages the partial set
# from ReflectionTypeLoadException.Types.
function Get-PublicTypes {
    param($assembly)
    $types = $null
    try {
        $types = $assembly.GetExportedTypes()
    } catch {
        try {
            $types = $assembly.GetTypes()
        } catch [System.Reflection.ReflectionTypeLoadException] {
            $types = $_.Exception.Types | Where-Object { $_ -ne $null }
        } catch {
            return @()
        }
    }
    $out = New-Object System.Collections.Generic.List[object]
    foreach ($t in $types) {
        try {
            if (-not $t.IsPublic) { continue }
            if ($t.IsNested) { continue }
            $name = $t.FullName
            if (-not $name) { continue }
            if ($name.StartsWith('<') -or $name.Contains('+<') -or $name.Contains('__StaticArrayInit')) { continue }
            $out.Add($t)
        } catch { }
    }
    return $out
}

# --- Build content -----------------------------------------------------------

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine('# Timberborn 1.0 API -- Full Reference (autogenerated)')
[void]$sb.AppendLine('')
[void]$sb.AppendLine("Generated by ``tools/generate-api-cache.ps1`` on $(Get-Date -Format 'yyyy-MM-dd').  ")
[void]$sb.AppendLine("Source: ``$ManagedDir``  ")
[void]$sb.AppendLine("Loaded: $($assemblies.Count) of $($dlls.Count) Timberborn DLLs.")
[void]$sb.AppendLine('')
[void]$sb.AppendLine('Do NOT edit by hand -- regenerate after Timberborn version bumps. Companion to `timberborn-api.md` (curated, annotated notes).')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('## Legend')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('Type-kind markers in the per-DLL type index:')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('- `[I]` interface')
[void]$sb.AppendLine('- `[E]` enum')
[void]$sb.AppendLine('- `[S]` struct (value type)')
[void]$sb.AppendLine('- `[A]` abstract class')
[void]$sb.AppendLine('- `[X]` static class')
[void]$sb.AppendLine('- `[C]` class')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('Tier markers:')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('- `(A)` -- full member signatures dumped below the type index.')
[void]$sb.AppendLine('- _no marker_ -- name-only entry (data POCO, entity component, internal-shaped helper).')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('Tier A is all public interfaces plus concrete types whose name ends in one of:')
[void]$sb.AppendLine('Service, System, Tracker, Manager, Registry, Provider, Spawner, Highlighter, Marker, Renderer, Drawer, Tool, Factory, Pool, Mediator, Hub, Configurator, Spec, Module, Decorator, Listener, Validator, Component, Behavior, Event, EventArgs.')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('**Appendix:** [Common interface implementers](#common-interface-implementers) -- precomputed list of every type that implements a load-bearing Timberborn interface (singleton lifecycle, entity lifecycle, modding hooks).')
[void]$sb.AppendLine('')

# --- Index --------------------------------------------------------------------

[void]$sb.AppendLine('## Index')
[void]$sb.AppendLine('')
$indexCounts = [ordered]@{}
foreach ($name in $assemblies.Keys) {
    $count = (Get-PublicTypes $assemblies[$name]).Count
    $indexCounts[$name] = $count
    if ($count -eq 0) { continue }   # DLL has no public surface -- skip both index and section
    $anchor = Get-AnchorName $name
    [void]$sb.AppendLine("- [$name](#$anchor) -- $count types")
}
if ($failures.Count -gt 0) {
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('**Failed to load:**')
    foreach ($k in $failures.Keys) {
        [void]$sb.AppendLine("- $k -- $($failures[$k])")
    }
}
[void]$sb.AppendLine('')
[void]$sb.AppendLine('---')
[void]$sb.AppendLine('')

# --- Per-DLL sections --------------------------------------------------------

# Collect implementers of watched interfaces during the per-DLL pass.
$implementers = @{}
foreach ($groupName in $watchedInterfaces.Keys) {
    foreach ($iface in $watchedInterfaces[$groupName]) {
        $implementers[$iface] = New-Object System.Collections.Generic.List[string]
    }
}

foreach ($name in $assemblies.Keys) {
    $a = $assemblies[$name]
    $types = Get-PublicTypes $a | Sort-Object FullName

    if (-not $types -or $types.Count -eq 0) {
        continue   # skip empty DLLs entirely (no heading, no separator)
    }

    [void]$sb.AppendLine("## $name")
    [void]$sb.AppendLine('')

    [void]$sb.AppendLine("### Types ($($types.Count))")
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('```')
    $tierA = New-Object System.Collections.Generic.List[object]
    foreach ($t in $types) {
        $kind = Get-TypeKind $t
        $simple = $t.Name -replace '`\d+', ''
        $isTierA = $t.IsInterface -or ($simple -match $tierAPattern) -or ($tierAExplicit -contains $simple)
        $mark = if ($isTierA) { ' (A)' } else { '' }
        [void]$sb.AppendLine("[$kind] $($t.FullName)$mark")
        if ($isTierA) { $tierA.Add($t) }

        # Record implementers of watched interfaces (every type, not just Tier-A,
        # so we catch entity components that implement IInitializableEntity etc.).
        if (-not $t.IsInterface) {
            try {
                foreach ($iface in $t.GetInterfaces()) {
                    if ($implementers.ContainsKey($iface.FullName)) {
                        $implementers[$iface.FullName].Add($t.FullName)
                    }
                }
            } catch { }
        }
    }
    [void]$sb.AppendLine('```')
    [void]$sb.AppendLine('')

    if ($tierA.Count -gt 0) {
        [void]$sb.AppendLine('### Signatures')
        [void]$sb.AppendLine('')
        foreach ($t in $tierA) {
            $kind = Get-TypeKind $t
            $kindLabel = Get-KindLabel $kind
            [void]$sb.AppendLine("#### ``$($t.FullName)``")
            [void]$sb.AppendLine('')
            [void]$sb.AppendLine("*$kindLabel*  ")

            try {
                # Filter out IEquatable: every record spec implements it,
                # the assembly-qualified form is huge, and it adds zero info.
                $iface = $t.GetInterfaces() | Where-Object {
                    $_.IsPublic -and $_.FullName -notlike 'System.IEquatable*'
                } | ForEach-Object { ($_.FullName -replace '`\d+', '') }
                if ($iface -and $iface.Count -gt 0) {
                    [void]$sb.AppendLine("Implements: $($iface -join ', ')  ")
                }
                $bt = $t.BaseType
                if ($bt -and $bt.FullName -ne 'System.Object' -and $bt.FullName -ne 'System.ValueType' -and $bt.FullName -ne 'System.Enum') {
                    [void]$sb.AppendLine("Extends: $($bt.FullName -replace '`\d+', '')  ")
                }
            } catch { }
            [void]$sb.AppendLine('')

            $members = Get-PublicMembers $t
            if ($members.Count -gt 0) {
                [void]$sb.AppendLine('```')
                foreach ($m in $members) { [void]$sb.AppendLine($m) }
                [void]$sb.AppendLine('```')
            } else {
                [void]$sb.AppendLine('(no public declared members)')
            }
            [void]$sb.AppendLine('')
        }
    }

    [void]$sb.AppendLine('---')
    [void]$sb.AppendLine('')
}

# --- Common interface implementers appendix ---------------------------------

[void]$sb.AppendLine('## Common interface implementers')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('Implementers of load-bearing Timberborn interfaces, computed across all scanned assemblies. Use this to answer "what types hook into X" questions without grepping the per-DLL sections.')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('Includes every implementing class, not just Tier-A types -- entity components that implement `IInitializableEntity`, `IPersistentEntity`, etc. show up here even when their per-DLL entry was name-only.')
[void]$sb.AppendLine('')
[void]$sb.AppendLine('**Caveat:** only `public, non-nested` types are counted (same filter as the per-DLL index). Mechanistry authors most of its own configurators as internal types, so `Bindito.Core.IConfigurator` shows almost no implementers -- this is expected, not a bug. Mod authors write their own public configurators because Bindito needs to discover them at load time.')
[void]$sb.AppendLine('')

foreach ($groupName in $watchedInterfaces.Keys) {
    [void]$sb.AppendLine("### $groupName")
    [void]$sb.AppendLine('')
    foreach ($iface in $watchedInterfaces[$groupName]) {
        $list = $implementers[$iface]
        $sorted = @($list | Sort-Object -Unique)
        [void]$sb.AppendLine("**``$iface``** -- $($sorted.Count) implementers  ")
        [void]$sb.AppendLine('')
        if ($sorted.Count -eq 0) {
            [void]$sb.AppendLine('(none found in scanned assemblies)')
        } else {
            [void]$sb.AppendLine('```')
            foreach ($impl in $sorted) {
                [void]$sb.AppendLine($impl)
            }
            [void]$sb.AppendLine('```')
        }
        [void]$sb.AppendLine('')
    }
}

# --- Write -------------------------------------------------------------------

$outDir = Split-Path $OutFile -Parent
if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Path $outDir | Out-Null
}
# Compact common BCL prefixes -- they're noise once you know the convention.
# Order matters: longest prefixes must run before shorter ones.
$content = $sb.ToString()
$content = $content -replace '\bSystem\.Collections\.Generic\.', ''
$content = $content -replace '\bSystem\.Collections\.Immutable\.', ''
$content = $content -replace '\bSystem\.Threading\.Tasks\.', ''
$content = $content -replace '\bSystem\.Threading\.', ''
$content = $content -replace '\bSystem\.', ''

$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($OutFile, $content, $utf8NoBom)

$line = ($content -split "`r?`n").Count
$kb = [math]::Round([System.Text.Encoding]::UTF8.GetByteCount($content) / 1KB)
Write-Host "Wrote $line lines / $kb KB to $OutFile"
