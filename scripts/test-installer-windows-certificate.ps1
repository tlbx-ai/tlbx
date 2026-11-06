# Certificate/key reuse contracts using only own temporary files and unnamed CNG keys.
[CmdletBinding()]
param([string]$EvidenceDirectory = '', [string]$NativeExecutable = '')
$ErrorActionPreference='Stop'
if([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT){return}
Add-Type -AssemblyName System.Security
$repoRoot=Split-Path -Parent $PSScriptRoot
$t=$null;$e=$null;$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $repoRoot 'install.ps1'),[ref]$t,[ref]$e)
if($e.Count){throw ($e.Message -join '; ')}
foreach($name in @('Test-ExistingCertificate','Generate-Certificate')) {
    $n=$ast.Find({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
    if(-not $n){throw "Missing certificate function: $name"};Invoke-Expression $n.Extent.Text
}
if(-not $EvidenceDirectory){$EvidenceDirectory=Join-Path $repoRoot '.artifacts/installer-windows-certificate'}
New-Item -ItemType Directory -Path $EvidenceDirectory -Force|Out-Null
$fixtureRoot=Join-Path ([IO.Path]::GetTempPath()) ('tlbx-cert-fixture-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'keys') -Force|Out-Null
$CertificateFileName='tlbx.pem';$CertificateKeyId='tlbx';$script:regenerating=$false
function Write-Log { param($Message,$Level) if($Message -match 'No valid certificate found'){$script:regenerating=$true} }
$results=New-Object 'Collections.Generic.List[object]'
function Assert-Pair([string]$Case,[bool]$Expected,[bool]$IsService) {
    $result=Test-ExistingCertificate -SettingsDir $fixtureRoot -IsService $IsService
    $actual=$null -ne $result
    if($result){$result.Certificate.Dispose()}
    if($actual -ne $Expected){throw "Certificate case '$Case' expected $Expected, got $actual."}
    $results.Add(@{case=$Case;accepted=$actual;service=$IsService})
}
function New-FixtureKey([bool]$Rsa, [bool]$Ecdh = $false) {
    $parameters=New-Object Security.Cryptography.CngKeyCreationParameters
    $parameters.ExportPolicy=[Security.Cryptography.CngExportPolicies]::AllowExport -bor [Security.Cryptography.CngExportPolicies]::AllowPlaintextExport
    $algorithm=if($Rsa){[Security.Cryptography.CngAlgorithm]::Rsa}elseif($Ecdh){[Security.Cryptography.CngAlgorithm]::ECDiffieHellmanP384}else{[Security.Cryptography.CngAlgorithm]::ECDsaP384}
    if($Rsa){$parameters.Parameters.Add([Security.Cryptography.CngProperty]::new('Length',[BitConverter]::GetBytes(2048),[Security.Cryptography.CngPropertyOptions]::None))}
    $key=[Security.Cryptography.CngKey]::Create($algorithm,[NullString]::Value,$parameters)
    if(-not $key.IsEphemeral -or $key.KeyName){$key.Dispose();throw 'Fixture attempted a persisted CNG key.'}
    return $key
}
function Write-FixtureCertificate($Key,[bool]$Rsa,[DateTimeOffset]$NotBefore,[DateTimeOffset]$NotAfter) {
    $private=if($Rsa){[Security.Cryptography.RSACng]::new($Key)}else{[Security.Cryptography.ECDsaCng]::new($Key)}
    try{
        $request=if($Rsa){[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=tlbx-cert-fixture',$private,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)}else{[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=tlbx-cert-fixture',$private,[Security.Cryptography.HashAlgorithmName]::SHA384)}
        $cert=$request.CreateSelfSigned($NotBefore,$NotAfter)
        try{
            $base64=[Convert]::ToBase64String($cert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert),[Base64FormattingOptions]::InsertLineBreaks)
            [IO.File]::WriteAllText((Join-Path $fixtureRoot 'tlbx.pem'),"-----BEGIN CERTIFICATE-----`r`n$base64`r`n-----END CERTIFICATE-----`r`n")
        }finally{$cert.Dispose()}
    }finally{$private.Dispose()}
}
$keyPath=Join-Path $fixtureRoot 'keys\tlbx.dpapi'
try {
    foreach($algorithm in @('ECDSA-P384','ECDH-P384','RSA')) {
        $rsa=$algorithm -eq 'RSA';$ecdh=$algorithm -eq 'ECDH-P384'
        $key=New-FixtureKey $rsa $ecdh;$otherKey=New-FixtureKey $rsa $ecdh
        $raw=$null;$otherRaw=$null
        try {
            $raw=$key.Export([Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
            $otherRaw=$otherKey.Export([Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
            if($ecdh){
                $imported=[Security.Cryptography.CngKey]::Import($raw,[Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
                try{if($imported.AlgorithmGroup -ne [Security.Cryptography.CngAlgorithmGroup]::ECDiffieHellman){throw 'ECDH regression fixture did not exercise a real ECDH PKCS8 import.'}}finally{$imported.Dispose()}
            }
            foreach($service in @($false,$true)) {
                $scope=if($service){[Security.Cryptography.DataProtectionScope]::LocalMachine}else{[Security.Cryptography.DataProtectionScope]::CurrentUser}
                Write-FixtureCertificate $key $rsa ([DateTimeOffset]::UtcNow.AddDays(-1)) ([DateTimeOffset]::UtcNow.AddDays(90))
                $validCertificatePem = [IO.File]::ReadAllText((Join-Path $fixtureRoot 'tlbx.pem'))
                [IO.File]::WriteAllBytes($keyPath,[Security.Cryptography.ProtectedData]::Protect($raw,$null,$scope))
                Assert-Pair "$algorithm valid $scope" $true $service
                # CryptUnprotectData follows the blob's embedded scope; switching
                # the API scope is NOT a reliable rejection under the same user.
                Assert-Pair "$algorithm opposite API scope, same OS identity" $true (-not $service)
                Remove-Item -LiteralPath $keyPath -Force
                Assert-Pair "$algorithm missing key" $false $service
                [IO.File]::WriteAllBytes($keyPath,[byte[]]@(1,2,3))
                Assert-Pair "$algorithm corrupt DPAPI" $false $service
                [IO.File]::WriteAllBytes($keyPath,[Security.Cryptography.ProtectedData]::Protect($otherRaw,$null,$scope))
                Assert-Pair "$algorithm valid different private key" $false $service
                [IO.File]::WriteAllBytes($keyPath,[Security.Cryptography.ProtectedData]::Protect([byte[]]@(1,2,3),$null,$scope))
                Assert-Pair "$algorithm invalid PKCS8" $false $service
                [IO.File]::WriteAllBytes($keyPath,[Security.Cryptography.ProtectedData]::Protect($raw,[byte[]]@(7,8,9),$scope))
                Assert-Pair "$algorithm undecryptable blob" $false $service
                [IO.File]::WriteAllBytes($keyPath,[Security.Cryptography.ProtectedData]::Protect($raw,$null,$scope))
                Write-FixtureCertificate $key $rsa ([DateTimeOffset]::UtcNow.AddDays(-90)) ([DateTimeOffset]::UtcNow.AddDays(-1))
                Assert-Pair "$algorithm expired" $false $service
                Write-FixtureCertificate $key $rsa ([DateTimeOffset]::UtcNow.AddDays(1)) ([DateTimeOffset]::UtcNow.AddDays(90))
                Assert-Pair "$algorithm not yet valid" $false $service
                Write-FixtureCertificate $key $rsa ([DateTimeOffset]::UtcNow.AddDays(-1)) ([DateTimeOffset]::UtcNow.AddDays(20))
                Assert-Pair "$algorithm near expiry" $false $service
            }
        }finally{
            if($raw){[Array]::Clear($raw,0,$raw.Length)}
            if($otherRaw){[Array]::Clear($otherRaw,0,$otherRaw.Length)}
            $otherKey.Dispose();$key.Dispose()
        }
    }
    # Real Generate-Certificate must choose regeneration, not reuse a bad pair.
    # No executable is present in this fixture, so no native process is launched.
    [IO.File]::WriteAllText((Join-Path $fixtureRoot 'tlbx.pem'),$validCertificatePem)
    [IO.File]::WriteAllBytes($keyPath,[byte[]]@(1,2,3))
    $script:regenerating=$false
    Generate-Certificate -InstallDir $fixtureRoot -SettingsDir $fixtureRoot -IsService $true|Out-Null
    if(-not $script:regenerating){throw 'Generate-Certificate reused an invalid pair instead of attempting regeneration.'}
    if($NativeExecutable){
        $nativeSettings=Join-Path $fixtureRoot 'native generated profile'
        $nativeOut=Join-Path $EvidenceDirectory "native-certificate-$($PSVersionTable.PSVersion.Major).stdout.log"
        $nativeErr=Join-Path $EvidenceDirectory "native-certificate-$($PSVersionTable.PSVersion.Major).stderr.log"
        & $NativeExecutable --generate-cert --user-mode --settings-dir $nativeSettings --force > $nativeOut 2> $nativeErr
        if($LASTEXITCODE -ne 0){throw "Native certificate generation failed; see $nativeErr"}
        $nativePair=Test-ExistingCertificate -SettingsDir $nativeSettings -IsService $false
        if(-not $nativePair){throw 'Real native-generated certificate/key pair failed verification.'}
        try{
            $nativeRaw=$null;$nativeKey=$null
            try{
                $nativeRaw=[Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes((Join-Path $nativeSettings 'keys\tlbx.dpapi')),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
                $nativeKey=[Security.Cryptography.CngKey]::Import($nativeRaw,[Security.Cryptography.CngKeyBlobFormat]::Pkcs8PrivateBlob)
                @{shell=$PSVersionTable.PSVersion.ToString();accepted=$true;algorithm=$nativeKey.Algorithm.Algorithm;group=$nativeKey.AlgorithmGroup.AlgorithmGroup;keySize=$nativeKey.KeySize;ephemeral=$nativeKey.IsEphemeral;fingerprint=$nativePair.Thumbprint}|ConvertTo-Json|Set-Content (Join-Path $EvidenceDirectory "native-certificate-$($PSVersionTable.PSVersion.Major)-proof.json")
            }finally{if($nativeRaw){[Array]::Clear($nativeRaw,0,$nativeRaw.Length)};if($nativeKey){$nativeKey.Dispose()}}
            Write-Host 'PASS: real native-generated DPAPI/PKCS8 certificate pair verified; no listener, service or trust entry created.'
        }finally{$nativePair.Certificate.Dispose()}
    }
    $results|ConvertTo-Json -Depth 4|Set-Content (Join-Path $EvidenceDirectory "certificate-$($PSVersionTable.PSVersion.Major)-proof.json")
    Write-Host "PASS: $($results.Count) actual DPAPI/CNG certificate cases plus regeneration branch; PowerShell $($PSVersionTable.PSVersion); no trust/SCM changes."
} finally {
    $resolved=[IO.Path]::GetFullPath($fixtureRoot)
    $tempRoot=[IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')+'\'
    if(-not $resolved.StartsWith($tempRoot,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notmatch '^tlbx-cert-fixture-[a-f0-9]{32}$'){throw 'Invalid certificate fixture cleanup path.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
