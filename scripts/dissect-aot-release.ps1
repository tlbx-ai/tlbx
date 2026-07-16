#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Downloads and dissects the latest MidTerm Windows Native AOT release.

.DESCRIPTION
    Fetches the latest GitHub release asset for either the dev prerelease line
    or the stable main line, extracts mt-win-x64.zip, then produces a section,
    import and resource report for mt.exe.
#>

param(
    [ValidateSet("dev", "main")]
    [string]$Channel = "dev",

    [string]$Repository = "tlbx-ai/tlbx",

    [string]$OutputRoot = (Join-Path $PSScriptRoot "..\.dev\aot-dissection"),

    [string]$ToolsRoot = (Join-Path $PSScriptRoot "..\.dev\aot-tools"),

    [switch]$InstallTools,

    [switch]$OpenGui
)

$ErrorActionPreference = "Stop"

function Resolve-AbsolutePath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [System.IO.Path]::GetFullPath($Path)
}

function Get-GitHubJson {
    param([Parameter(Mandatory = $true)][string]$Url)

    return Invoke-RestMethod `
        -Uri $Url `
        -Headers @{ "User-Agent" = "MidTerm-AotDissector"; "Accept" = "application/vnd.github+json" }
}

function Get-GitHubJsonWithFallback {
    param([Parameter(Mandatory = $true)][string]$Path)

    try {
        return Get-GitHubJson -Url "https://api.github.com/$Path"
    }
    catch {
        $gh = Get-Command gh.exe -ErrorAction SilentlyContinue
        if (-not $gh) {
            throw
        }

        $json = & $gh.Source api $Path
        if ($LASTEXITCODE -ne 0 -or -not $json) {
            throw
        }

        return $json | ConvertFrom-Json
    }
}

function Convert-ToObjectArray {
    param([Parameter(Mandatory = $true)]$Value)

    $items = @($Value)
    if ($items.Count -eq 1 -and $items[0] -is [System.Array]) {
        return @($items[0])
    }

    return $items
}

function Assert-ChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Candidate
    )

    $rootPath = Resolve-AbsolutePath $Root
    $candidatePath = Resolve-AbsolutePath $Candidate
    $rootWithSlash = $rootPath.TrimEnd('\') + '\'

    if (-not $candidatePath.StartsWith($rootWithSlash, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside output root. Root='$rootPath' Candidate='$candidatePath'"
    }

    return $candidatePath
}

function Invoke-Download {
    param(
        [Parameter(Mandatory = $true)][string]$Url,
        [Parameter(Mandatory = $true)][string]$DestinationPath
    )

    $parent = Split-Path -Parent $DestinationPath
    if (-not (Test-Path $parent)) {
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
    }

    Write-Host "Downloading $Url" -ForegroundColor Cyan
    Invoke-WebRequest `
        -Uri $Url `
        -OutFile $DestinationPath `
        -Headers @{ "User-Agent" = "MidTerm-AotDissector"; "Accept" = "application/octet-stream" }
}

function Get-DumpbinPath {
    $dumpbin = Get-Command dumpbin.exe -ErrorAction SilentlyContinue
    if ($dumpbin) {
        return $dumpbin.Source
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) {
        throw "dumpbin.exe not found and vswhere.exe is unavailable."
    }

    $resolved = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -find "**\dumpbin.exe" | Select-Object -First 1
    if (-not $resolved) {
        throw "Could not locate dumpbin.exe via vswhere."
    }

    return $resolved
}

function Parse-DumpbinSectionSummary {
    param(
        [AllowEmptyCollection()]
        [AllowEmptyString()]
        [string[]]$Lines
    )

    $sections = foreach ($line in $Lines) {
        if ($line -match '^\s*([0-9A-F]+)\s+(\.\S+)\s*$') {
            $sizeBytes = [Convert]::ToInt64($matches[1], 16)
            [pscustomobject]@{
                section = $matches[2]
                sizeHex = "0x$($matches[1])"
                sizeBytes = $sizeBytes
                sizeMiB = [Math]::Round($sizeBytes / 1MB, 3)
            }
        }
    }

    return @($sections | Sort-Object sizeBytes -Descending)
}

function Convert-ToTextLines {
    param([Parameter(Mandatory = $true)]$Value)

    $lines = foreach ($item in @($Value)) {
        if ($null -eq $item) {
            continue
        }

        foreach ($line in ("$item" -split "`r?`n")) {
            $line
        }
    }

    return @($lines)
}

function Invoke-NativeTextCommand {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$ArgumentList
    )

    $stdoutPath = [System.IO.Path]::GetTempFileName()
    $stderrPath = [System.IO.Path]::GetTempFileName()

    try {
        $process = Start-Process `
            -FilePath $FilePath `
            -ArgumentList $ArgumentList `
            -Wait `
            -NoNewWindow `
            -PassThru `
            -RedirectStandardOutput $stdoutPath `
            -RedirectStandardError $stderrPath

        $stdoutLines = if (Test-Path $stdoutPath) { Get-Content -LiteralPath $stdoutPath } else { @() }
        $stderrLines = if (Test-Path $stderrPath) { Get-Content -LiteralPath $stderrPath } else { @() }

        if ($process.ExitCode -ne 0) {
            $combined = @($stdoutLines + $stderrLines) -join [Environment]::NewLine
            throw "Native command failed with exit code $($process.ExitCode): $combined"
        }

        return Convert-ToTextLines (@($stdoutLines + $stderrLines))
    }
    finally {
        Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
    }
}

function Format-ResourceLabel {
    param([Parameter(Mandatory = $true)][string]$Value)

    $map = @{
        "1"  = "CURSOR"
        "2"  = "BITMAP"
        "3"  = "ICON"
        "4"  = "MENU"
        "5"  = "DIALOG"
        "6"  = "STRING"
        "7"  = "FONTDIR"
        "8"  = "FONT"
        "9"  = "ACCELERATOR"
        "10" = "RCDATA"
        "11" = "MESSAGETABLE"
        "12" = "GROUP_CURSOR"
        "14" = "GROUP_ICON"
        "16" = "VERSION"
        "17" = "DLGINCLUDE"
        "19" = "PLUGPLAY"
        "20" = "VXD"
        "21" = "ANICURSOR"
        "22" = "ANIICON"
        "23" = "HTML"
        "24" = "MANIFEST"
    }

    if ($map.ContainsKey($Value)) {
        return $map[$Value]
    }

    return $Value
}

function Invoke-OffineManagedResourceExtractor {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [Parameter(Mandatory = $true)][string]$MtExePath,
        [Parameter(Mandatory = $true)][string]$DestinationRoot,
        [Parameter(Mandatory = $true)][string]$WebRootPrefix
    )

    $extractorProject = Join-Path $RepoRoot "src\Ai.Tlbx.MidTerm.AotExtractor\Ai.Tlbx.MidTerm.AotExtractor.csproj"
    if (-not (Test-Path $extractorProject)) {
        throw "Offline extractor project not found at $extractorProject"
    }

    $dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
    $toolOutput = Invoke-NativeTextCommand `
        -FilePath $dotnetPath `
        -ArgumentList @(
            "run",
            "--project", $extractorProject,
            "--configuration", "Release",
            "--",
            "--exe", $MtExePath,
            "--output", $DestinationRoot,
            "--webroot-prefix", $WebRootPrefix
        )

    $reportPath = Join-Path $DestinationRoot "report.json"
    if (-not (Test-Path $reportPath)) {
        throw "Offline extractor did not produce $reportPath"
    }

    return [pscustomobject]@{
        toolOutput = $toolOutput
        reportPath = $reportPath
        report = (Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json)
    }
}

function Get-ManifestText {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    if ($Bytes.Length -ge 3 -and $Bytes[0] -eq 0xEF -and $Bytes[1] -eq 0xBB -and $Bytes[2] -eq 0xBF) {
        return [System.Text.Encoding]::UTF8.GetString($Bytes)
    }

    if (($Bytes.Length % 2) -eq 0) {
        $utf16 = [System.Text.Encoding]::Unicode.GetString($Bytes)
        if ($utf16.Trim([char]0) -match '<assembly') {
            return $utf16.Trim([char]0)
        }
    }

    return [System.Text.Encoding]::UTF8.GetString($Bytes)
}

if (-not ("MidTerm.NativeResourceReader" -as [type])) {
    Add-Type -Language CSharp -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MidTerm
{
    public sealed class NativeResourceRecord
    {
        public string Type = string.Empty;
        public string Name = string.Empty;
        public ushort Language;
        public uint Size;
        public byte[] Data = Array.Empty<byte>();
    }

    public static class NativeResourceReader
    {
        private const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;

        private delegate bool EnumResTypeProc(IntPtr hModule, IntPtr lpszType, IntPtr lParam);
        private delegate bool EnumResNameProc(IntPtr hModule, IntPtr lpszType, IntPtr lpszName, IntPtr lParam);
        private delegate bool EnumResLangProc(IntPtr hModule, IntPtr lpszType, IntPtr lpszName, ushort wIDLanguage, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool EnumResourceTypes(IntPtr hModule, EnumResTypeProc lpEnumFunc, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool EnumResourceNames(IntPtr hModule, IntPtr lpszType, EnumResNameProc lpEnumFunc, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool EnumResourceLanguages(IntPtr hModule, IntPtr lpszType, IntPtr lpszName, EnumResLangProc lpEnumFunc, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr FindResourceEx(IntPtr hModule, IntPtr lpType, IntPtr lpName, ushort wLanguage);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LockResource(IntPtr hResData);

        public static List<NativeResourceRecord> Read(string path)
        {
            var module = LoadLibraryEx(path, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE);
            if (module == IntPtr.Zero)
            {
                throw new InvalidOperationException("LoadLibraryEx failed.");
            }

            var records = new List<NativeResourceRecord>();
            try
            {
                EnumResourceTypes(module, (hModule, typePtr, _) =>
                {
                    EnumResourceNames(hModule, typePtr, (hModule2, _, namePtr, __) =>
                    {
                        EnumResourceLanguages(hModule2, typePtr, namePtr, (hModule3, _, ____, language, _____) =>
                        {
                            var resInfo = FindResourceEx(hModule3, typePtr, namePtr, language);
                            if (resInfo == IntPtr.Zero)
                            {
                                return true;
                            }

                            var size = SizeofResource(hModule3, resInfo);
                            var resData = LoadResource(hModule3, resInfo);
                            var locked = LockResource(resData);
                            var bytes = new byte[size];
                            if (size > 0 && locked != IntPtr.Zero)
                            {
                                Marshal.Copy(locked, bytes, 0, (int)size);
                            }

                            records.Add(new NativeResourceRecord
                            {
                                Type = ToResourceLabel(typePtr),
                                Name = ToResourceLabel(namePtr),
                                Language = language,
                                Size = size,
                                Data = bytes
                            });

                            return true;
                        }, IntPtr.Zero);

                        return true;
                    }, IntPtr.Zero);

                    return true;
                }, IntPtr.Zero);
            }
            finally
            {
                FreeLibrary(module);
            }

            return records;
        }

        private static string ToResourceLabel(IntPtr ptr)
        {
            ulong raw = unchecked((ulong)ptr.ToInt64());
            if ((raw >> 16) == 0)
            {
                return ((ushort)raw).ToString();
            }

            return Marshal.PtrToStringUni(ptr) ?? string.Empty;
        }
    }
}
"@
}

$repoRoot = Resolve-AbsolutePath (Join-Path $PSScriptRoot "..")
$toolsRoot = Resolve-AbsolutePath $ToolsRoot
$outputRoot = Resolve-AbsolutePath $OutputRoot
$cacheRoot = Join-Path $outputRoot "_cache"
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
New-Item -ItemType Directory -Force -Path $cacheRoot | Out-Null

$installerScript = Join-Path $PSScriptRoot "install-aot-tools.ps1"
$toolsManifestPath = Join-Path $toolsRoot "tools.json"
if ($InstallTools -or -not (Test-Path $toolsManifestPath)) {
    & $installerScript -ToolsRoot $toolsRoot
}

$toolsManifest = Get-Content -LiteralPath $toolsManifestPath | ConvertFrom-Json
$dumpbinPath = Get-DumpbinPath
$resourceHackerPath = $toolsManifest.resourceHacker.path
$peBearPath = $toolsManifest.peBear.path

if ($Channel -eq "main") {
    $release = Get-GitHubJsonWithFallback -Path "repos/$Repository/releases/latest"
}
else {
    $releases = Convert-ToObjectArray (Get-GitHubJsonWithFallback -Path "repos/$Repository/releases?per_page=30")
    $release = $releases |
        Where-Object { -not $_.draft -and (($_.prerelease -eq $true) -or ($_.tag_name -match '-dev(\.\d+)?$')) } |
        Sort-Object {
            if ($_.published_at) {
                [DateTimeOffset]$_.published_at
            }
            elseif ($_.created_at) {
                [DateTimeOffset]$_.created_at
            }
            else {
                [DateTimeOffset]::MinValue
            }
        } -Descending |
        Select-Object -First 1

    if (-not $release) {
        throw "No prerelease was found for $Repository."
    }
}

$asset = @($release.assets | Where-Object { $_.name -eq "mt-win-x64.zip" } | Select-Object -First 1)
if ($asset.Count -ne 1) {
    throw "Release '$($release.tag_name)' does not contain mt-win-x64.zip."
}

$tag = $release.tag_name
$channelRoot = Join-Path $outputRoot $Channel
$releaseRoot = Join-Path $channelRoot $tag
$zipPath = Join-Path $cacheRoot "$tag-mt-win-x64.zip"
$analysisRoot = Join-Path $releaseRoot "_analysis"
$resourceRoot = Join-Path $analysisRoot "resources"
$webRootAnalysis = Join-Path $analysisRoot "webroot"

New-Item -ItemType Directory -Force -Path $channelRoot | Out-Null
if (-not (Test-Path $zipPath)) {
    Invoke-Download -Url $asset.browser_download_url -DestinationPath $zipPath
}

$safeReleaseRoot = Assert-ChildPath -Root $outputRoot -Candidate $releaseRoot
if (Test-Path $safeReleaseRoot) {
    Remove-Item -LiteralPath $safeReleaseRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $safeReleaseRoot | Out-Null
Expand-Archive -LiteralPath $zipPath -DestinationPath $safeReleaseRoot -Force
New-Item -ItemType Directory -Force -Path $analysisRoot | Out-Null
New-Item -ItemType Directory -Force -Path $resourceRoot | Out-Null
New-Item -ItemType Directory -Force -Path $webRootAnalysis | Out-Null

$mtExe = Join-Path $releaseRoot "mt.exe"
if (-not (Test-Path $mtExe)) {
    throw "mt.exe was not found after extracting $zipPath"
}

$fileInfo = Get-Item -LiteralPath $mtExe
$versionInfo = $fileInfo.VersionInfo

$summaryLines = Invoke-NativeTextCommand -FilePath $dumpbinPath -ArgumentList @("/summary", $mtExe)
$headersLines = Invoke-NativeTextCommand -FilePath $dumpbinPath -ArgumentList @("/headers", $mtExe)
$importsLines = Invoke-NativeTextCommand -FilePath $dumpbinPath -ArgumentList @("/imports", $mtExe)

$summaryPath = Join-Path $analysisRoot "dumpbin-summary.txt"
$headersPath = Join-Path $analysisRoot "dumpbin-headers.txt"
$importsPath = Join-Path $analysisRoot "dumpbin-imports.txt"

$summaryLines | Set-Content -LiteralPath $summaryPath -Encoding UTF8
$headersLines | Set-Content -LiteralPath $headersPath -Encoding UTF8
$importsLines | Set-Content -LiteralPath $importsPath -Encoding UTF8

$sectionSummary = Parse-DumpbinSectionSummary -Lines $summaryLines
$resources = [MidTerm.NativeResourceReader]::Read($mtExe)
$managedResourceExtraction = Invoke-OffineManagedResourceExtractor `
    -RepoRoot $repoRoot `
    -MtExePath $mtExe `
    -DestinationRoot $webRootAnalysis `
    -WebRootPrefix "Ai.Tlbx.MidTerm.wwwroot."

$resourceInventory = foreach ($resource in $resources | Sort-Object Size -Descending) {
    $typeLabel = Format-ResourceLabel $resource.Type
    $nameLabel = $resource.Name
    $safeType = ($typeLabel -replace '[^A-Za-z0-9._-]', '_')
    $safeName = ($nameLabel -replace '[^A-Za-z0-9._-]', '_')
    $extension = ".bin"
    if ($typeLabel -eq "MANIFEST") { $extension = ".xml" }
    elseif ($typeLabel -in @("ICON", "GROUP_ICON", "BITMAP")) { $extension = ".dat" }
    elseif ($typeLabel -eq "VERSION") { $extension = ".version.bin" }

    $resourcePath = Join-Path $resourceRoot ("{0}-{1}-{2}{3}" -f $safeType, $safeName, $resource.Language, $extension)
    if ($typeLabel -eq "MANIFEST") {
        $manifestText = Get-ManifestText -Bytes $resource.Data
        $manifestText | Set-Content -LiteralPath $resourcePath -Encoding UTF8
    }
    else {
        [System.IO.File]::WriteAllBytes($resourcePath, $resource.Data)
    }

    [pscustomobject]@{
        type = $typeLabel
        name = $nameLabel
        language = $resource.Language
        sizeBytes = [int64]$resource.Size
        sizeKiB = [Math]::Round($resource.Size / 1KB, 2)
        extractedTo = $resourcePath
    }
}

$managedWebRootResources = @(
    $managedResourceExtraction.report.resources |
        Where-Object { $_.webRootPath } |
        ForEach-Object {
            [pscustomobject]@{
                assemblyName = $_.assemblyName
                manifestResourceName = $_.resourceName
                webPath = $_.webRootPath
                sizeBytes = [int64]$_.length
                extractedTo = $_.webRootExtractedTo
            }
        }
)
$managedWebRootJsonPath = Join-Path $webRootAnalysis "managed-webroot-resources.json"
$managedWebRootTxtPath = Join-Path $webRootAnalysis "managed-webroot-resources.txt"
$managedWebRootResources | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $managedWebRootJsonPath -Encoding UTF8
$managedWebRootResources |
    ForEach-Object { "{0}`t{1}`t{2}" -f $_.webPath, $_.manifestResourceName, $_.sizeBytes } |
    Set-Content -LiteralPath $managedWebRootTxtPath -Encoding UTF8
$managedWebRootExtraction = [pscustomobject]@{
    reportPath = $managedResourceExtraction.reportPath
    exactRoot = $managedResourceExtraction.report.extractedWebRoot
    allRoot = $managedResourceExtraction.report.extractedAllRoot
    toolOutput = $managedResourceExtraction.toolOutput
}
$managedWebRootExtractionJsonPath = Join-Path $webRootAnalysis "managed-webroot-extraction.json"
$managedWebRootExtraction | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $managedWebRootExtractionJsonPath -Encoding UTF8

$report = [ordered]@{
    generatedAt = (Get-Date).ToString("o")
    channel = $Channel
    repository = $Repository
    release = [ordered]@{
        tag = $tag
        name = $release.name
        prerelease = [bool]$release.prerelease
        publishedAt = $release.published_at
        assetName = $asset.name
        assetUrl = $asset.browser_download_url
    }
    tools = [ordered]@{
        dumpbin = $dumpbinPath
        resourceHacker = $resourceHackerPath
        peBear = $peBearPath
    }
    mtExe = [ordered]@{
        path = $mtExe
        sizeBytes = $fileInfo.Length
        sizeMiB = [Math]::Round($fileInfo.Length / 1MB, 3)
        fileVersion = $versionInfo.FileVersion
        productVersion = $versionInfo.ProductVersion
        sha256 = (Get-FileHash -LiteralPath $mtExe -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    sections = $sectionSummary
    resources = $resourceInventory
    managedWebRootResources = $managedWebRootResources
    managedWebRootExtraction = $managedWebRootExtraction
    managedResourceReport = $managedResourceExtraction.report
}

$reportPath = Join-Path $analysisRoot "report.json"
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding UTF8

Write-Host ""
Write-Host "Dissection complete:" -ForegroundColor Green
Write-Host "  Release tag: $tag" -ForegroundColor DarkGray
Write-Host "  mt.exe:      $mtExe" -ForegroundColor DarkGray
Write-Host "  Report:      $reportPath" -ForegroundColor DarkGray
Write-Host "  Sections:" -ForegroundColor DarkGray
foreach ($section in $sectionSummary) {
    Write-Host ("    {0,-8} {1,8:N3} MiB ({2})" -f $section.section, $section.sizeMiB, $section.sizeHex) -ForegroundColor DarkGray
}
Write-Host "  Resources:" -ForegroundColor DarkGray
foreach ($resource in $resourceInventory) {
    Write-Host ("    {0,-12} {1,-16} lang={2,-5} {3,8:N2} KiB" -f $resource.type, $resource.name, $resource.language, $resource.sizeKiB) -ForegroundColor DarkGray
}
Write-Host ("  Embedded wwwroot files: {0}" -f $managedWebRootResources.Count) -ForegroundColor DarkGray
Write-Host "    Inventory: $managedWebRootTxtPath" -ForegroundColor DarkGray
Write-Host "    Exact:     $($managedWebRootExtraction.exactRoot)" -ForegroundColor DarkGray
Write-Host "    All:       $($managedWebRootExtraction.allRoot)" -ForegroundColor DarkGray

if ($OpenGui) {
    Start-Process -FilePath $resourceHackerPath -ArgumentList @($mtExe)
    Start-Process -FilePath $peBearPath -ArgumentList @($mtExe)
}
