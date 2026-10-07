param(
    [Parameter(Mandatory)][string]$ConfigPath
)

$ErrorActionPreference = 'Stop'
$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
# A second invocation is a failed acceptance, even if the first one timed out.
$attempt = [IO.File]::Open($config.attemptPath, 'CreateNew', 'Write', 'None')
$attempt.Dispose()
$clock = [Diagnostics.Stopwatch]::StartNew()
$result = [ordered]@{
    root = $env:CODEX_SESSION_ID
    thread = $env:CODEX_THREAD_ID
    inherited = $env:MT_SESSION_ID
    pid = $PID
    expectedSessionId = $config.sessionId
    topic = $config.topic
    ok = $false
    error = $null
}
try {
    $env:MT_BASE_URL = $config.baseUrl
    . $config.helperPath
    # This MUST be the first scoped helper invocation. No manual proof or retry.
    mt_topic $config.topic
    $result.resolved = $env:MT_SESSION_ID
    $snapshot = mt_sessions | ConvertFrom-Json
    $result.readback = ($snapshot.sessions | Where-Object id -EQ $result.resolved).topic
    $result.ok = $result.resolved -eq $config.sessionId -and $result.readback -eq $config.topic
    "Resolved $($result.resolved); topic readback $($result.readback)" |
        Set-Content -LiteralPath $config.stdoutPath -Encoding utf8
}
catch {
    $result.error = $_.Exception.Message
    $result.contextError = $script:_MContextError
    $result.error | Set-Content -LiteralPath $config.stderrPath -Encoding utf8
}
finally {
    $result.elapsedMs = $clock.ElapsedMilliseconds
    $pendingPath = $config.resultPath + '.pending'
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $pendingPath -Encoding utf8
    Move-Item -LiteralPath $pendingPath -Destination $config.resultPath
}
if (-not $result.ok) { throw ($result.error ?? 'Session identity or topic readback did not match.') }
