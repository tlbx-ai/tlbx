#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Stable releases use the promotion PR workflow. Direct main releases were retired.
#>
param(
    [Parameter(Mandatory)][ValidateSet('all')][string[]]$TestCategories,
    [string]$ReleaseTitle,
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$Version,
    [string[]]$ReleaseNotes,
    [switch]$PrepareOnly,
    [switch]$Reprepare
)
& "$PSScriptRoot/promote.ps1" @PSBoundParameters
