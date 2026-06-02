<#
.SYNOPSIS
    Promote a GitHub Discussion (idea) to a tracked Issue, then close the
    discussion as resolved.

.DESCRIPTION
    For an idea that has been accepted onto the roadmap / is now under
    development. In one shot this:

      1. Fetches the discussion's title and body (GraphQL).
      2. Creates a new Issue from that title/body, with a back-link footer
         ("Promoted from discussion #N"), and applies a label (default:
         enhancement).
      3. Edits the discussion body to append a forward-link to the new issue
         ("Promoted to issue #M -- now under development") via the
         `updateDiscussion` mutation.
      4. Closes the discussion with reason RESOLVED via the `closeDiscussion`
         mutation, so the idea has a single source of truth (the issue) for
         active work.

    There is no native `gh discussion` command, so the discussion read/edit/
    close all go through `gh api graphql`. Issue creation uses `gh issue
    create`. Run from inside the repo. Requires the GitHub CLI installed and
    authenticated (`gh auth login`).

.PARAMETER DiscussionNumber
    The discussion number to promote (the #N from its URL). Required.

.PARAMETER Title
    Override the issue title. Defaults to the discussion's title.

.PARAMETER Label
    Issue label to apply. Default: enhancement. The label must already exist
    on the repo (gh issue create fails otherwise).

.PARAMETER CloseReason
    Discussion close reason: RESOLVED (default), OUTDATED, or DUPLICATE.

.EXAMPLE
    .\tools\promote-idea.ps1 -DiscussionNumber 17

.EXAMPLE
    .\tools\promote-idea.ps1 -DiscussionNumber 17 -Label roadmap -Title "Sea and ocean biomes (MVP)"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [int]$DiscussionNumber,

    [string]$Title,

    [string]$Label = 'enhancement',

    [ValidateSet('RESOLVED', 'OUTDATED', 'DUPLICATE')]
    [string]$CloseReason = 'RESOLVED'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Decode native-command (gh) stdout as UTF-8 so the round-tripped discussion
# body keeps its em-dashes/accents on Windows PowerShell 5.1.
$prevOutputEncoding = [Console]::OutputEncoding
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

try {
    # --- Preflight: gh present and authenticated ---

    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "GitHub CLI (gh) not found on PATH. Install it and run 'gh auth login'."
    }
    gh auth status *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "gh is not authenticated. Run 'gh auth login' first."
    }

    # --- Resolve owner/name from the current repo's GitHub remote ---

    $nwo = gh repo view --json nameWithOwner --jq .nameWithOwner
    if ($LASTEXITCODE -ne 0 -or -not $nwo) {
        throw "Couldn't resolve the GitHub repo. Run this from inside the repo (with a GitHub remote)."
    }
    $owner, $name = $nwo.Trim().Split('/')

    # --- Fetch the discussion (id + title + body + url) ---
    # number is Int!, so it must go through -F (typed) not -f (string).

    $query = 'query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){discussion(number:$number){id number title body url}}}'
    $raw = gh api graphql -f owner=$owner -f name=$name -F number=$DiscussionNumber -f query=$query
    if ($LASTEXITCODE -ne 0) {
        throw "GraphQL discussion lookup failed."
    }
    $discussion = ($raw | ConvertFrom-Json).data.repository.discussion
    if (-not $discussion) {
        throw "Discussion #$DiscussionNumber not found in $owner/$name."
    }

    $issueTitle = if ($Title) { $Title } else { $discussion.title }

    # --- 1. Create the issue from the discussion's body + a back-link footer ---

    $issueBody = @"
$($discussion.body)

---
_Promoted from discussion #$($discussion.number): $($discussion.url)_
"@

    $issueUrl = $null
    $tmp = New-TemporaryFile
    try {
        [System.IO.File]::WriteAllText($tmp.FullName, $issueBody)
        $createOut = gh issue create --title $issueTitle --label $Label --body-file $tmp.FullName
        if ($LASTEXITCODE -ne 0) {
            throw "gh issue create failed (exit $LASTEXITCODE). Does the '$Label' label exist on the repo?"
        }
        # gh prints the new issue URL to stdout; take the last http(s) line.
        $issueUrl = $createOut | Where-Object { $_ -match '^https?://' } | Select-Object -Last 1
    }
    finally {
        Remove-Item $tmp.FullName -ErrorAction SilentlyContinue
    }
    if (-not $issueUrl) {
        throw "Issue was created but its URL couldn't be parsed from gh output."
    }
    $issueNumber = ($issueUrl -split '/')[-1]

    # --- 2. Append a forward-link to the issue in the discussion body ---

    $newBody = @"
$($discussion.body)

---
**Promoted to issue #$issueNumber -- now under development:** $issueUrl
"@

    $updateMutation = 'mutation($id:ID!,$body:String!){updateDiscussion(input:{discussionId:$id,body:$body}){discussion{url}}}'
    $bodyFile = New-TemporaryFile
    try {
        [System.IO.File]::WriteAllText($bodyFile.FullName, $newBody)
        gh api graphql -f id=$($discussion.id) -f query=$updateMutation -F "body=@$($bodyFile.FullName)" *> $null
        if ($LASTEXITCODE -ne 0) {
            throw "updateDiscussion failed (issue #$issueNumber was already created)."
        }
    }
    finally {
        Remove-Item $bodyFile.FullName -ErrorAction SilentlyContinue
    }

    # --- 3. Close the discussion ---
    # CloseReason is ValidateSet-constrained, so it's safe to inline as the enum
    # literal (enum variables aren't reliably coerced from -f string values).

    $closeMutation = "mutation(`$id:ID!){closeDiscussion(input:{discussionId:`$id,reason:$CloseReason}){discussion{url}}}"
    gh api graphql -f id=$($discussion.id) -f query=$closeMutation *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "closeDiscussion failed (issue #$issueNumber created and discussion body updated; close it manually)."
    }

    Write-Host "Promoted discussion #$($discussion.number) -> issue #$issueNumber"
    Write-Host "  Issue:      $issueUrl"
    Write-Host "  Discussion: $($discussion.url) (closed: $CloseReason)"
}
finally {
    [Console]::OutputEncoding = $prevOutputEncoding
}
