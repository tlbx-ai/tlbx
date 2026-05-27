param(
    [string]$BaseUrl = $env:MIDTERM_BASE_URL,
    [string]$RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path,
    [switch]$Reset
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($BaseUrl)) {
    $BaseUrl = 'https://127.0.0.1:2100'
}

$BaseUrl = $BaseUrl.TrimEnd('/')

function Invoke-MtJson {
    param(
        [string]$Method = 'GET',
        [string]$Path,
        [object]$Body = $null
    )

    $uri = "$BaseUrl$Path"
    $params = @{
        Method = $Method
        Uri = $uri
        SkipCertificateCheck = $true
    }
    if ($null -ne $Body) {
        $params.ContentType = 'application/json'
        $params.Body = ($Body | ConvertTo-Json -Depth 12)
    }
    Invoke-RestMethod @params
}

function New-DemoSession {
    param(
        [string]$Name,
        [string]$Command,
        [int]$Cols = 62,
        [int]$Rows = 34
    )

    $created = Invoke-MtJson -Method POST -Path '/api/sessions' -Body @{
        shell = 'Pwsh'
        workingDirectory = $RepoRoot
        cols = $Cols
        rows = $Rows
        surface = 'marketing-mobile-demo'
    }

    $sessionId = $created.id
    Invoke-MtJson -Method PUT -Path "/api/sessions/$sessionId/name" -Body @{ name = $Name } | Out-Null
    Invoke-MtJson -Method PUT -Path "/api/sessions/$sessionId/topic" -Body @{ topic = 'Marketing mobile vertical demo' } | Out-Null
    Start-Sleep -Milliseconds 500
    Invoke-MtJson -Method POST -Path "/api/sessions/$sessionId/input/text" -Body @{
        text = $Command
        appendNewline = $true
    } | Out-Null

    [pscustomobject]@{
        name = $Name
        id = $sessionId
    }
}

$sessions = Invoke-MtJson -Path '/api/sessions'
$existing = @($sessions.sessions | Where-Object {
    $_.surface -eq 'marketing-mobile-demo' -or
    $_.name -in @('System Monitor', 'Editor TUI', 'Build Loop', 'Agent Console', 'Dev Browser')
})

if ($Reset) {
    foreach ($session in $existing) {
        try {
            Invoke-MtJson -Method DELETE -Path "/api/sessions/$($session.id)" | Out-Null
        } catch {
            Write-Warning "Could not delete demo session $($session.id): $($_.Exception.Message)"
        }
    }
    Start-Sleep -Milliseconds 800
    $existing = @()
}

if ($existing.Count -gt 0) {
    Write-Host 'Existing marketing demo sessions found. Use -Reset to recreate them.' -ForegroundColor Yellow
    $existing | Select-Object name, id, surface
    return
}

$buildLoop = Join-Path $PSScriptRoot 'demo-build-loop.ps1'
$agentConsole = Join-Path $PSScriptRoot 'demo-agent-console.ps1'

$created = @()
$planFile = Join-Path $RepoRoot 'docs\marketing\mobile-vertical-demo-plan-2026-05.md'

$created += New-DemoSession -Name 'Editor TUI' -Command "edit `"$planFile`"" -Cols 82 -Rows 34
$created += New-DemoSession -Name 'Build Loop' -Command "pwsh -NoLogo -NoProfile -ExecutionPolicy Bypass -File `"$buildLoop`"" -Cols 82 -Rows 34
$created += New-DemoSession -Name 'Agent Console' -Command "pwsh -NoLogo -NoProfile -ExecutionPolicy Bypass -File `"$agentConsole`"" -Cols 82 -Rows 34
$created += New-DemoSession -Name 'Dev Browser' -Command "Clear-Host; Write-Host 'Dev Browser validation session' -ForegroundColor White; Write-Host 'Preview target: http://127.0.0.1:4177/' -ForegroundColor Cyan" -Cols 82 -Rows 34

$created | ConvertTo-Json -Depth 4
