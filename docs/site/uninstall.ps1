# tlbx GitHub Pages bootstrap uninstaller
# Usage: irm https://get.tlbx.ai/uninstall.ps1 | iex

param(
    [switch]$Elevated,
    [string]$OriginalUserProfile,
    [string]$OriginalLocalAppData,
    [string]$OriginalTempRoot
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$scriptUrl = 'https://raw.githubusercontent.com/tlbx-ai/tlbx/main/uninstall.ps1'
if ($PSVersionTable.PSVersion.Major -lt 6) {
    $scriptContent = Invoke-RestMethod -Uri $scriptUrl -UseBasicParsing
} else {
    $scriptContent = Invoke-RestMethod -Uri $scriptUrl
}
$scriptBlock = [ScriptBlock]::Create($scriptContent)

& $scriptBlock @PSBoundParameters
