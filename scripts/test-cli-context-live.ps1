<#
.SYNOPSIS
Concurrent first-call acceptance with two real Codex shared-daemon TUIs.
.DESCRIPTION
Consumes paid model calls. Uses an existing daemon and server; never restarts either.
The helper snapshot contains local ephemeral credentials: keep the artifact directory private.
Native TUI output stays attached to each owned terminal. Named stdout/stderr logs record
the model-issued tool command; terminal-tail files preserve the real TUI evidence.
.EXAMPLE
./scripts/test-cli-context-live.ps1 -BaseUrl https://localhost:2000 `
  -HelperPath ./.tlbx/tlbx_cli.ps1 `
  -Socket unix://C:/Users/johan/.codex/app-server-control/app-server-control.sock `
  -Model gpt-6-luna
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][uri]$BaseUrl,
    [Parameter(Mandatory)][string]$HelperPath,
    [Parameter(Mandatory)][string]$Socket,
    [Parameter(Mandatory)][string]$Model,
    [string]$WorkingDirectory = 'Q:\repos\Jpa',
    [string]$ArtifactDirectory,
    [ValidateRange(15, 180)][int]$TimeoutSeconds = 90,
    [switch]$KeepSessions
)

$ErrorActionPreference = 'Stop'
$runId = [Guid]::NewGuid().ToString('N')
if (-not $ArtifactDirectory) {
    $ArtifactDirectory = Join-Path $PSScriptRoot "../.dev/cli-context-live/$runId"
}
$ArtifactDirectory = [IO.Path]::GetFullPath($ArtifactDirectory)
New-Item -ItemType Directory -Path $ArtifactDirectory -Force | Out-Null
$helperSnapshot = Join-Path $ArtifactDirectory 'helper.ps1'
Copy-Item -LiteralPath (Resolve-Path -LiteralPath $HelperPath).Path -Destination $helperSnapshot
$callScript = Join-Path $PSScriptRoot 'test-cli-context-live-call.ps1'
$savedBaseUrl = $env:MT_BASE_URL
$env:MT_BASE_URL = $BaseUrl.AbsoluteUri.TrimEnd('/')
$owned = [Collections.Generic.List[string]]::new()
$clients = [Collections.Generic.List[object]]::new()
$summary = [ordered]@{
    runId = $runId
    baseUrl = $env:MT_BASE_URL
    model = $Model
    callerRoot = $env:CODEX_SESSION_ID
    ok = $false
    clients = @()
    error = $null
    cleanupErrors = @()
    keptSessions = [bool]$KeepSessions
}

function Quote-Literal([string]$Value) { "'" + $Value.Replace("'", "''") + "'" }
function Invoke-TestApi([string]$Method, [string]$Path, [object]$Body = $null) {
    $headers = if ($env:MT_API_KEY) { @{ Authorization = "Bearer $env:MT_API_KEY" } } else { @{ Cookie = $script:_MK } }
    $request = @{ Method = $Method; Uri = "$($env:MT_BASE_URL)$Path"; Headers = $headers; SkipCertificateCheck = $true; TimeoutSec = 15 }
    if ($null -ne $Body) { $request.Body = $Body | ConvertTo-Json -Compress; $request.ContentType = 'application/json' }
    $response = Invoke-WebRequest @request
    if ($response.Headers['Content-Type'] -match 'json') { return $response.Content | ConvertFrom-Json }
    return $response.Content
}
function Read-Sessions { (Invoke-TestApi GET '/api/sessions').sessions }

try {
    . $helperSnapshot
    $before = @(Read-Sessions)
    for ($index = 1; $index -le 2; $index++) {
        $created = Invoke-TestApi POST '/api/sessions' @{ shell = 'Pwsh'; workingDirectory = $WorkingDirectory; cols = 140; rows = 40; launchRequestId = [Guid]::NewGuid().ToString() }
        if ($created.id -notmatch '^[0-9a-f]{8}$') { throw 'Session creation did not return an eight-character session ID.' }
        $owned.Add($created.id)
        $configPath = Join-Path $ArtifactDirectory "client-$index.config.json"
        $config = [ordered]@{
            sessionId = $created.id
            topic = "CLI first-call $($runId.Substring(0, 8))/$index"
            baseUrl = $env:MT_BASE_URL
            helperPath = $helperSnapshot
            resultPath = Join-Path $ArtifactDirectory "client-$index.result.json"
            attemptPath = Join-Path $ArtifactDirectory "client-$index.attempt"
            stdoutPath = Join-Path $ArtifactDirectory "client-$index.stdout.log"
            stderrPath = Join-Path $ArtifactDirectory "client-$index.stderr.log"
            socket = $Socket
            model = $Model
            workingDirectory = $WorkingDirectory
            callScript = $callScript
        }
        '' | Set-Content -LiteralPath $config.stdoutPath
        '' | Set-Content -LiteralPath $config.stderrPath
        $config | ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding utf8
        $launcherPath = Join-Path $ArtifactDirectory "client-$index.launch.ps1"
        $launcher = '$config = Get-Content -LiteralPath ' + (Quote-Literal $configPath) + ' -Raw | ConvertFrom-Json' + "`n" + @'
$env:MT_BASE_URL = $config.baseUrl
Remove-Item Env:MT_SESSION_ID -ErrorAction SilentlyContinue
function Quote-Literal([string]$Value) { "'" + $Value.Replace("'", "''") + "'" }
$command = '& ' + (Quote-Literal $config.callScript) + ' -ConfigPath ' + (Quote-Literal $configPath)
$prompt = 'Protocol acceptance test only. Do not read or edit project files. Your only tool call must execute this exact PowerShell command: ' + "`n" + $command + "`n" + 'Do not retry or repair. Then respond done.'
& codex --yolo --remote $config.socket --cd $config.workingDirectory -m $config.model -c 'model_reasoning_effort="low"' -c 'service_tier="default"' $prompt
'@
        # ConfigPath is a launcher-local variable, passed verbatim to the model tool call.
        $launcher = '$configPath = ' + (Quote-Literal $configPath) + "`n" + $launcher
        $launcher | Set-Content -LiteralPath $launcherPath -Encoding utf8
        $clients.Add([pscustomobject]@{ config = $config; launcherPath = $launcherPath })
    }
    # Launch both before waiting for either result.
    foreach ($client in $clients) {
        $body = @{ text = '& ' + (Quote-Literal $client.launcherPath); appendNewline = $true }
        Invoke-TestApi POST "/api/sessions/$($client.config.sessionId)/input/text" $body | Out-Null
    }
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while (@($clients | Where-Object { -not (Test-Path -LiteralPath $_.config.resultPath) }).Count -gt 0) {
        if ([DateTime]::UtcNow -ge $deadline) { throw "First-call acceptance timed out after $TimeoutSeconds seconds; no retry performed." }
        Start-Sleep -Milliseconds 500
    }
    $results = @($clients | ForEach-Object { Get-Content -LiteralPath $_.config.resultPath -Raw | ConvertFrom-Json })
    $summary.clients = $results
    for ($index = 0; $index -lt 2; $index++) {
        if (-not $results[$index].ok -or $results[$index].resolved -ne $owned[$index]) {
            throw "Client $($index + 1) failed its first scoped command: $($results[$index].error)"
        }
        if ($results[$index].root -notmatch '^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$') {
            throw "Client $($index + 1) did not report a canonical Codex root ID."
        }
        if ($summary.callerRoot -and $results[$index].root -eq $summary.callerRoot) {
            throw "Client $($index + 1) resolved the supervisor's Codex root."
        }
    }
    if ($results[0].root -eq $results[1].root) { throw 'The two real clients did not have distinct Codex roots.' }
    $after = @(Read-Sessions)
    foreach ($session in $before) {
        $current = @($after | Where-Object id -EQ $session.id)
        if ($current.Count -ne 1 -or $current[0].topic -cne $session.topic) {
            throw "Pre-existing session $($session.id) disappeared or its topic changed."
        }
    }
    foreach ($client in $clients) {
        $current = @($after | Where-Object id -EQ $client.config.sessionId)
        if ($current.Count -ne 1 -or $current[0].topic -cne $client.config.topic) {
            throw "Client $($client.config.sessionId) overwrote a sibling topic or lost its own topic."
        }
    }
    $summary.ok = $true
}
catch { $summary.error = $_.Exception.Message }
finally {
    foreach ($sessionId in $owned) {
        try {
            Invoke-TestApi GET "/api/sessions/$sessionId/buffer/tail?lines=100&stripAnsi=true" |
                Set-Content -LiteralPath (Join-Path $ArtifactDirectory "$sessionId.terminal.log") -Encoding utf8
        } catch { $summary.cleanupErrors += "Tail $sessionId failed: $($_.Exception.Message)" }
        if (-not $KeepSessions) {
            try { Invoke-TestApi DELETE "/api/sessions/$sessionId" | Out-Null }
            catch { $summary.cleanupErrors += "Delete $sessionId failed: $($_.Exception.Message)" }
        }
    }
    if (-not $KeepSessions -and $owned.Count -gt 0) {
        try {
            $remaining = @(Read-Sessions | Where-Object { $_.id -in $owned })
            if ($remaining.Count -gt 0) { $summary.cleanupErrors += 'Test-owned sessions remain after deletion.' }
        } catch { $summary.cleanupErrors += "Cleanup verification failed: $($_.Exception.Message)" }
    }
    if ($summary.cleanupErrors.Count -gt 0) { $summary.ok = $false }
    $summaryPath = Join-Path $ArtifactDirectory 'summary.json'
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding utf8
    $env:MT_BASE_URL = $savedBaseUrl
}
Write-Output "Acceptance summary: $summaryPath"
if (-not $summary.ok) { throw ($summary.error ?? ($summary.cleanupErrors -join '; ')) }
