$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'advisory.ps1')
$fixture=Join-Path $PSScriptRoot '.artifacts/advisory-test.json'
$config=@{config=@{protocol_version='v1.0.0';scanner_name='govulncheck';scan_level='package'}}
$sbom=@{SBOM=@{modules=@(@{path='example.org/library';version='v1.0.0'})}}
$moduleOnly=@{finding=@{osv='GO-TEST-1';trace=@(@{module='example.org/library';version='v1.0.0'})}}
$imported=@{finding=@{osv='GO-TEST-2';trace=@(@{module='example.org/library';version='v1.0.0';package='example.org/library/transport'})}}
(@($config,$sbom,$moduleOnly) | ForEach-Object {$_ | ConvertTo-Json -Depth 8 -Compress}) -join "`n" | Set-Content $fixture -Encoding utf8
$report=Read-ConnectGoAuditReport $fixture 'win-x64'
if($report.Findings.Count -ne 0 -or $report.ModuleOnly.Count -ne 1){throw 'Unimported Go advisory was hidden or made blocking'}
(@($config,$sbom,$moduleOnly,$imported) | ForEach-Object {$_ | ConvertTo-Json -Depth 8}) -join "`n" | Set-Content $fixture -Encoding utf8
$report=Read-ConnectGoAuditReport $fixture 'win-x64'
if($report.Findings.Count -ne 1 -or $report.Findings[0].Advisory -ne 'GO-TEST-2'){throw 'Imported-package Go advisory was lost'}
$rejected=$false
try{Assert-NoAdvisoryRegression $report.Findings @() 'new Go module'}catch{$rejected=$true}
if(-not $rejected){throw 'New imported Go vulnerability did not block release'}
Assert-NoAdvisoryRegression $report.Findings $report.Findings 'unchanged Go module'
$rejected=$false
'{"config":' | Set-Content $fixture -Encoding utf8
try{Read-ConnectGoAuditReport $fixture 'win-x64'}catch{$rejected=$true}
if(-not $rejected){throw 'Truncated Go audit response accepted'}
$sbom | ConvertTo-Json -Depth 8 | Set-Content $fixture -Encoding utf8
$rejected=$false
try{Read-ConnectGoAuditReport $fixture 'win-x64'}catch{$rejected=$true}
if(-not $rejected){throw 'Missing Go scanner configuration accepted'}
Write-Host 'Go advisory protocol: imported/new blocking, unchanged baseline, module-only visibility and malformed-response checks passed.'
