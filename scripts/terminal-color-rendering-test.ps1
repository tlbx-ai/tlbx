[CmdletBinding()]
param(
    [ValidateRange(1, 100)]
    [int]$Repeat = 1
)

$script:Esc = [char]27

function Get-Sgr {
    param([Parameter(Mandatory = $true)][string]$Code)
    return ("{0}[{1}m" -f $script:Esc, $Code)
}

function Write-Raw {
    param([Parameter(Mandatory = $true)][string]$Text)
    [Console]::Out.Write($Text)
}

function Write-Ansi {
    param(
        [Parameter(Mandatory = $true)][string]$Sgr,
        [Parameter(Mandatory = $true)][string]$Text
    )

    Write-Raw ("{0}{1}{2}" -f (Get-Sgr $Sgr), $Text, (Get-Sgr "0"))
}

function Write-Section {
    param([Parameter(Mandatory = $true)][string]$Title)

    Write-Raw "`r`n=== $Title ===`r`n"
}

function Write-SgrRow {
    param(
        [Parameter(Mandatory = $true)][string]$Label,
        [Parameter(Mandatory = $true)][string[]]$Codes
    )

    Write-Raw ($Label.PadRight(24))
    foreach ($code in $Codes) {
        Write-Ansi $code (" {0,-10}" -f $code)
    }
    Write-Raw "`r`n"
}

for ($pass = 1; $pass -le $Repeat; $pass++) {
    Write-Section "tlbx terminal color rendering test pass $pass"
    Write-Raw "This script writes real ANSI SGR control sequences through stdout.`r`n"
    Write-Raw "Use Advanced & Diagnostics > Terminal buffer dump to capture rendered text, xterm cell attributes, and raw ESC bytes.`r`n"

    Write-Section "PowerShell Write-Host ConsoleColor"
    foreach ($name in [Enum]::GetNames([ConsoleColor])) {
        Write-Host ("Write-Host foreground {0}" -f $name) -ForegroundColor $name
    }

    Write-Section "ANSI 16-color foreground"
    Write-SgrRow "normal fg" @("30", "31", "32", "33", "34", "35", "36", "37")
    Write-SgrRow "bright fg" @("90", "91", "92", "93", "94", "95", "96", "97")
    Write-SgrRow "bold normal fg" @("1;30", "1;31", "1;32", "1;33", "1;34", "1;35", "1;36", "1;37")

    Write-Section "ANSI 16-color background"
    Write-SgrRow "normal bg" @("30;40", "30;41", "30;42", "30;43", "97;44", "97;45", "30;46", "30;47")
    Write-SgrRow "bright bg" @("30;100", "30;101", "30;102", "30;103", "30;104", "30;105", "30;106", "30;107")

    Write-Section "Text attributes"
    Write-SgrRow "attributes" @("1", "2", "3", "4", "5", "7", "9", "53")
    Write-SgrRow "combined" @("1;31", "2;34", "3;35", "4;36", "7;33", "9;91", "53;92", "1;4;95")

    Write-Section "256-color foreground sample"
    for ($row = 0; $row -lt 16; $row++) {
        for ($col = 0; $col -lt 16; $col++) {
            $index = ($row * 16) + $col
            Write-Ansi ("38;5;{0}" -f $index) (" {0,3}" -f $index)
        }
        Write-Raw "`r`n"
    }

    Write-Section "256-color background sample"
    for ($row = 0; $row -lt 16; $row++) {
        for ($col = 0; $col -lt 16; $col++) {
            $index = ($row * 16) + $col
            Write-Ansi ("30;48;5;{0}" -f $index) (" {0,3}" -f $index)
        }
        Write-Raw "`r`n"
    }

    Write-Section "Truecolor foreground gradient"
    for ($step = 0; $step -le 30; $step++) {
        $r = [Math]::Min(255, $step * 8)
        $g = [Math]::Max(0, 255 - ($step * 8))
        $b = [Math]::Min(255, 64 + ($step * 4))
        Write-Ansi ("38;2;{0};{1};{2}" -f $r, $g, $b) (" rgb({0,3},{1,3},{2,3})" -f $r, $g, $b)
        if (($step + 1) % 3 -eq 0) {
            Write-Raw "`r`n"
        }
    }
    Write-Raw "`r`n"

    Write-Section "Truecolor background blocks"
    for ($step = 0; $step -le 35; $step++) {
        $r = [Math]::Min(255, $step * 7)
        $g = [Math]::Min(255, 32 + ($step * 5))
        $b = [Math]::Max(0, 255 - ($step * 6))
        Write-Ansi ("30;48;2;{0};{1};{2}" -f $r, $g, $b) "  "
    }
    Write-Raw "`r`n"

    Write-Section "Reset and default color check"
    Write-Ansi "31" "red before reset"
    Write-Raw " -> "
    Write-Ansi "0" "default after reset"
    Write-Raw " -> normal text`r`n"
}
