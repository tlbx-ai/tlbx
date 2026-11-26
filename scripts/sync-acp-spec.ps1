[CmdletBinding()]
param(
    [string]$V1Tag,
    [string]$V2Tag,
    [switch]$CheckOnly
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$specRoot = Join-Path $repoRoot 'docs\acp-spec'
$pinPath = Join-Path $specRoot 'acp-spec-pin.json'
$upstreamRepo = 'agentclientprotocol/agent-client-protocol'

function Get-LatestTag([string]$Prefix) {
    $releases = gh api "repos/$upstreamRepo/releases?per_page=30" | ConvertFrom-Json
    $match = $releases | Where-Object { $_.tag_name -like "$Prefix*" } | Select-Object -First 1
    if (-not $match) {
        throw "No release found in $upstreamRepo matching '$Prefix*'."
    }
    return [string]$match.tag_name
}

function Get-UpstreamFile([string]$Ref, [string]$Path) {
    $uri = "https://raw.githubusercontent.com/$upstreamRepo/$Ref/$Path"
    $response = Invoke-WebRequest -Uri $uri -UseBasicParsing -ErrorAction Stop
    return [string]$response.Content
}

$resolvedV1 = if ($V1Tag) { $V1Tag } else { Get-LatestTag 'schema-v1.' }
$resolvedV2 = if ($V2Tag) { $V2Tag } else { Get-LatestTag 'schema-v2.' }

if ($CheckOnly) {
    if (-not (Test-Path $pinPath)) {
        Write-Error "Missing $pinPath"
        exit 1
    }
    $pin = Get-Content $pinPath -Raw | ConvertFrom-Json
    $v1Matches = $pin.v1Tag -eq $resolvedV1
    $v2Matches = $pin.v2Tag -eq $resolvedV2
    Write-Output "Pinned:   v1=$($pin.v1Tag), v2=$($pin.v2Tag)"
    Write-Output "Upstream: v1=$resolvedV1, v2=$resolvedV2"
    if (-not $v1Matches -or -not $v2Matches) {
        Write-Output "DRIFT_DETECTED=true"
        exit 2
    }
    Write-Output "DRIFT_DETECTED=false"
    exit 0
}

$files = @(
    @{ Generation = 'v1'; Tag = $resolvedV1; Name = 'meta.json' },
    @{ Generation = 'v1'; Tag = $resolvedV1; Name = 'meta.unstable.json' },
    @{ Generation = 'v1'; Tag = $resolvedV1; Name = 'schema.json' },
    @{ Generation = 'v1'; Tag = $resolvedV1; Name = 'schema.unstable.json' },
    @{ Generation = 'v2'; Tag = $resolvedV2; Name = 'meta.json' },
    @{ Generation = 'v2'; Tag = $resolvedV2; Name = 'meta.unstable.json' },
    @{ Generation = 'v2'; Tag = $resolvedV2; Name = 'schema.json' },
    @{ Generation = 'v2'; Tag = $resolvedV2; Name = 'schema.unstable.json' }
)

foreach ($entry in $files) {
    $dir = Join-Path $specRoot $entry.Generation
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    $remotePath = "schema/$($entry.Generation)/$($entry.Name)"
    $localPath = Join-Path $dir $entry.Name
    Write-Host "Fetching $remotePath @ $($entry.Tag)..."
    $raw = Get-UpstreamFile -Ref $entry.Tag -Path $remotePath
    [System.IO.File]::WriteAllText($localPath, $raw.TrimEnd() + "`n", [System.Text.UTF8Encoding]::new($false))
}

$pinObject = [ordered]@{
    repository = $upstreamRepo
    v1Tag      = $resolvedV1
    v2Tag      = $resolvedV2
    syncedAt   = [DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
}
$pinJson = ($pinObject | ConvertTo-Json -Depth 4) + "`n"
[System.IO.File]::WriteAllText($pinPath, $pinJson, [System.Text.UTF8Encoding]::new($false))
Write-Host "Pinned ACP schemas: v1=$resolvedV1, v2=$resolvedV2"
