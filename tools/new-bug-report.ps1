<#
.SYNOPSIS
    File a Keystone bug-report issue on GitHub from the command line.

.DESCRIPTION
    Thin wrapper over `gh issue create` that assembles a structured bug-report
    body (Timberborn version, faction, description, Player.log pointer) and
    files it against the repo's GitHub remote. Run from inside the repo.

    Requires the GitHub CLI (`gh`) installed and authenticated (`gh auth login`).

    Note: gh cannot upload file attachments. Use -IncludeLog to stage the
    current Player.log into dump/, then drag it onto the issue in the browser.

.PARAMETER Title
    Short issue title. Required.

.PARAMETER Description
    What happened / steps to reproduce. Prompted for if omitted.

.PARAMETER GameVersion
    Timberborn version, e.g. 1.0.0.0.

.PARAMETER Faction
    Faction in play (Folktails, Iron Teeth, Leaf Coats, Emberpelts, ...).

.PARAMETER Label
    Issue label to apply. Default: bug.

.PARAMETER IncludeLog
    Also copy the current Player.log into dump/ (via copy-player-log.ps1) so
    it's ready to attach to the issue in the GitHub web editor.

.EXAMPLE
    .\tools\new-bug-report.ps1 -Title "Cattails flicker at L3" -GameVersion 1.0.0.0 -Faction Folktails -Description "Cattail flourishes z-fight on river edges at level 3."
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Title,

    [string]$Description,

    [string]$GameVersion,

    [string]$Faction,

    [string]$Label = 'bug',

    [switch]$IncludeLog
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# --- Preflight: gh present and authenticated ---

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI (gh) not found on PATH. Install it and run 'gh auth login'."
}
gh auth status *> $null
if ($LASTEXITCODE -ne 0) {
    throw "gh is not authenticated. Run 'gh auth login' first."
}

if (-not $Description) {
    $Description = Read-Host 'What happened (description / steps to reproduce)'
}

# --- Optional: stage Player.log for manual attachment ---

$logNote = '_Attach your ``Player.log`` to this issue (drag it into the GitHub web editor)._'
if ($IncludeLog) {
    $copyScript = Join-Path $PSScriptRoot 'copy-player-log.ps1'
    if (Test-Path $copyScript) {
        & $copyScript | Out-Null
        $logNote = '_Player.log copied to ``dump/Player.log`` -- drag it onto this issue in the browser._'
    }
}

# --- Assemble the issue body ---

$versionText = if ($GameVersion) { $GameVersion } else { '_unspecified_' }
$factionText = if ($Faction) { $Faction } else { '_unspecified_' }

$body = @"
**Timberborn version:** $versionText
**Faction:** $factionText

### What happened
$Description

### Player.log
$logNote
"@

# --- File the issue (body via temp file to avoid native-arg quoting issues) ---

$tmp = New-TemporaryFile
try {
    [System.IO.File]::WriteAllText($tmp.FullName, $body)
    gh issue create --title $Title --label $Label --body-file $tmp.FullName
    if ($LASTEXITCODE -ne 0) {
        throw "gh issue create failed (exit $LASTEXITCODE). Does the '$Label' label exist on the repo?"
    }
}
finally {
    Remove-Item $tmp.FullName -ErrorAction SilentlyContinue
}
