# MidTerm GitHub Pages bootstrap installer
# Usage: irm https://tlbx-ai.github.io/MidTerm/install.ps1 | iex
# Dev:   & ([scriptblock]::Create((irm https://tlbx-ai.github.io/MidTerm/install.ps1))) -Dev

param(
    [string]$RunAsUser,
    [string]$RunAsUserSid,
    [string]$PasswordHash,
    [int]$Port = 2000,
    [string]$BindAddress = "",
    [switch]$ServiceMode,
    [switch]$ConfigureFirewall,
    [switch]$TrustCert,
    [string]$LogFile,
    [string]$ReplayFile,
    [switch]$Dev
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$branch = if ($Dev) { 'dev' } else { 'main' }
$scriptUrl = "https://raw.githubusercontent.com/tlbx-ai/MidTerm/$branch/install.ps1"
if ($PSVersionTable.PSVersion.Major -lt 6) {
    $scriptContent = Invoke-RestMethod -Uri $scriptUrl -UseBasicParsing
} else {
    $scriptContent = Invoke-RestMethod -Uri $scriptUrl
}
$scriptBlock = [ScriptBlock]::Create($scriptContent)

& $scriptBlock @PSBoundParameters
