<#
.SYNOPSIS
    Post a Keystone feature idea to GitHub Discussions from the command line.

.DESCRIPTION
    Wraps the GraphQL `createDiscussion` mutation -- there is no native
    `gh discussion` command. Resolves the repo's node ID and the target
    Discussions category ID automatically, creates the discussion, and prints
    its URL. Run from inside the repo.

    Requires the GitHub CLI (`gh`) installed and authenticated (`gh auth login`).

.PARAMETER Title
    Discussion title. Required.

.PARAMETER Body
    Discussion body (Markdown). Prompted for if omitted.

.PARAMETER Category
    Discussions category name (case-insensitive). Default: Ideas.

.EXAMPLE
    .\tools\new-idea.ps1 -Title "Sea and ocean biomes" -Body "Large-water biomes with their own fauna -- whales, manta rays, ocean life."

.EXAMPLE
    .\tools\new-idea.ps1 -Title "Polyculture beats monoculture" -Category Ideas
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Title,

    [string]$Body,

    [string]$Category = 'Ideas'
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

if (-not $Body) {
    $Body = Read-Host 'Idea description (Markdown)'
}

# --- Resolve owner/name from the current repo's GitHub remote ---

$nwo = gh repo view --json nameWithOwner --jq .nameWithOwner
if ($LASTEXITCODE -ne 0 -or -not $nwo) {
    throw "Couldn't resolve the GitHub repo. Run this from inside the repo (with a GitHub remote)."
}
$owner, $name = $nwo.Trim().Split('/')

# --- Look up the repo node ID + category ID (GraphQL variables avoid quoting traps) ---

$query = 'query($owner:String!,$name:String!){repository(owner:$owner,name:$name){id discussionCategories(first:25){nodes{name id}}}}'
$raw = gh api graphql -f owner=$owner -f name=$name -f query=$query
if ($LASTEXITCODE -ne 0) {
    throw "GraphQL repository lookup failed."
}
$repo = ($raw | ConvertFrom-Json).data.repository

$cat = $repo.discussionCategories.nodes | Where-Object { $_.name -ieq $Category } | Select-Object -First 1
if (-not $cat) {
    $available = ($repo.discussionCategories.nodes.name -join ', ')
    throw "Discussions category '$Category' not found. Available: $available"
}
$repoId = $repo.id
$catId = $cat.id

# --- Create the discussion ---
# The body is passed via a temp file (-F body=@file) rather than inline, so
# non-ASCII text (em-dashes, accents) survives: Windows PowerShell 5.1 mangles
# non-ASCII when handing arguments to a native command.

$mutation = 'mutation($repoId:ID!,$catId:ID!,$title:String!,$body:String!){createDiscussion(input:{repositoryId:$repoId,categoryId:$catId,title:$title,body:$body}){discussion{url number}}}'
$bodyFile = New-TemporaryFile
try {
    [System.IO.File]::WriteAllText($bodyFile.FullName, $Body)
    $raw = gh api graphql -f repoId=$repoId -f catId=$catId -f title=$Title -f query=$mutation -F "body=@$($bodyFile.FullName)"
    if ($LASTEXITCODE -ne 0) {
        throw "createDiscussion failed."
    }
}
finally {
    Remove-Item $bodyFile.FullName -ErrorAction SilentlyContinue
}
$discussion = ($raw | ConvertFrom-Json).data.createDiscussion.discussion

Write-Host "Created discussion #$($discussion.number) in '$($cat.name)': $($discussion.url)"
