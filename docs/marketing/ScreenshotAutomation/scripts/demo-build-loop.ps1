$ErrorActionPreference = 'Stop'

$steps = @(
    @{ Label = 'restore'; Text = 'packages resolved'; Color = 'Cyan' },
    @{ Label = 'compile'; Text = 'web bundle rebuilt'; Color = 'Green' },
    @{ Label = 'test'; Text = 'focused checks passed'; Color = 'Yellow' },
    @{ Label = 'preview'; Text = 'Dev Browser target refreshed'; Color = 'Magenta' },
    @{ Label = 'watch'; Text = 'waiting for file changes'; Color = 'Blue' }
)

Clear-Host
Write-Host 'tlbx demo build loop' -ForegroundColor White
Write-Host 'Q:\repos\MidTerm  mobile capture fixture' -ForegroundColor DarkGray
Write-Host ''

$i = 0
while ($true) {
    $step = $steps[$i % $steps.Count]
    $stamp = Get-Date -Format 'HH:mm:ss'
    Write-Host ("[{0}] {1,-8} {2}" -f $stamp, $step.Label, $step.Text) -ForegroundColor $step.Color
    Start-Sleep -Milliseconds 850
    $i += 1
}
