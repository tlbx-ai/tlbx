$ErrorActionPreference = 'SilentlyContinue'

Clear-Host
Write-Host 'Agent console' -ForegroundColor White
Write-Host 'local tools detected for this demo machine' -ForegroundColor DarkGray
Write-Host ''

$tools = @(
    @{ Name = 'codex'; Command = 'codex'; Args = @('--version') },
    @{ Name = 'grok'; Command = 'grok'; Args = @('--version') },
    @{ Name = 'copilot'; Command = 'copilot'; Args = @('--version') }
)

foreach ($tool in $tools) {
    $cmd = Get-Command $tool.Command -ErrorAction SilentlyContinue
    if ($cmd) {
        $version = (& $tool.Command @($tool.Args) 2>$null | Select-Object -First 1)
        if (-not $version) {
            $version = 'installed'
        }
        Write-Host ("{0,-8} {1}" -f $tool.Name, $version) -ForegroundColor Green
    } else {
        Write-Host ("{0,-8} not installed" -f $tool.Name) -ForegroundColor DarkGray
    }
}

Write-Host ''
Write-Host 'ready for supervised local agent work' -ForegroundColor Cyan
Write-Host ''

$ticks = @('checking repo context', 'waiting for prompt', 'watching tool output', 'ready')
$i = 0
while ($true) {
    $stamp = Get-Date -Format 'HH:mm:ss'
    Write-Host ("[{0}] {1}" -f $stamp, $ticks[$i % $ticks.Count]) -ForegroundColor DarkCyan
    Start-Sleep -Seconds 2
    $i += 1
}
