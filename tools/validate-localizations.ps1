#requires -Version 5.1
<#
.SYNOPSIS
  Validate Keystone localization CSVs the way Timberborn's loader (LINQtoCSV)
  will read them, so a malformed row fails the build instead of crashing the
  game at language-load time.

.DESCRIPTION
  The recurring failure mode is an UNQUOTED COMMA in the Text or Comment
  column: it creates extra fields, and LINQtoCSV throws an AggregatedException
  ("1 or more exceptions while reading data using type LocalizationRecord").

  PowerShell's Import-Csv does NOT catch this — it silently drops the extra
  fields — so this script uses Microsoft.VisualBasic.FileIO.TextFieldParser,
  which is quote-aware and reports the true field count per row. Every data
  row must have exactly 3 fields (ID, Text, Comment) and a non-empty ID.

  Exit code 0 = all good; 1 = at least one malformed row (details printed).
#>
[CmdletBinding()]
param(
  # Repo root. If omitted, derived from this script's own location.
  [string]$Root
)

$ErrorActionPreference = 'Stop'

if (-not $Root) {
  $scriptPath = if ($PSCommandPath) { $PSCommandPath } else { $MyInvocation.MyCommand.Path }
  $scriptDir = Split-Path -Parent $scriptPath
  $Root = (Resolve-Path (Join-Path $scriptDir '..')).Path
}
Add-Type -AssemblyName Microsoft.VisualBasic

$locDir = Join-Path $Root 'unity-assets/Keystone/Data/Localizations'
if (-not (Test-Path $locDir)) {
  Write-Error "Localization directory not found: $locDir"
  exit 1
}

$files = Get-ChildItem -Path $locDir -Filter *.csv -File
if (-not $files) {
  Write-Host "No localization CSVs found in $locDir (nothing to validate)."
  exit 0
}

$failed = $false
foreach ($file in $files) {
  $parser = New-Object Microsoft.VisualBasic.FileIO.TextFieldParser($file.FullName)
  try {
    $parser.TextFieldType = [Microsoft.VisualBasic.FileIO.FieldType]::Delimited
    $parser.SetDelimiters(',')
    $parser.HasFieldsEnclosedInQuotes = $true
    $parser.TrimWhiteSpace = $false

    $lineNo = 0
    while (-not $parser.EndOfData) {
      $lineNo++
      try {
        $fields = $parser.ReadFields()
      } catch {
        Write-Host "  ERROR $($file.Name):$lineNo  unparseable (likely an unbalanced quote): $($_.Exception.Message)"
        $failed = $true
        continue
      }
      if ($fields.Count -ne 3) {
        $preview = ($fields -join ' | ')
        Write-Host "  ERROR $($file.Name):$lineNo  has $($fields.Count) fields, expected 3 (unquoted comma?): $preview"
        $failed = $true
      } elseif ([string]::IsNullOrWhiteSpace($fields[0])) {
        Write-Host "  ERROR $($file.Name):$lineNo  empty ID"
        $failed = $true
      }
    }
  } finally {
    $parser.Close()
  }
}

if ($failed) {
  Write-Error "Localization CSV validation FAILED. Quote any field containing a comma."
  exit 1
}

Write-Host "Localization CSV validation OK ($($files.Count) file(s))."
exit 0
