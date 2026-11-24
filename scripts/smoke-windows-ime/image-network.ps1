param([Parameter(Mandatory)][string]$Editor, [Parameter(Mandatory)][string]$AppPath, [switch]$Isolated)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
if (!$Isolated) { throw 'Disposable Windows installer VM required.' }
$state = Join-Path $env:LOCALAPPDATA 'ShuangpinLink/state/device.bin'
if (Test-Path -LiteralPath $state) { throw 'The isolated image test requires an unpaired application.' }
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('spl-image-fixture-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
$rsa = [Security.Cryptography.RSA]::Create(2048)
$req = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=Shuangpin Link loopback fixture', $rsa, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
$san = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
$san.AddIpAddress([System.Net.IPAddress]::Loopback)
$req.CertificateExtensions.Add($san.Build())
$req.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
$cert = $req.CreateSelfSigned([datetimeoffset]::UtcNow.AddMinutes(-2), [datetimeoffset]::UtcNow.AddMinutes(20))
# This already-elevated disposable VM can trust the loopback certificate without a
# desktop trust prompt. Remove the minted certificate by its exact identity below.
$store = [Security.Cryptography.X509Certificates.X509Store]::new('Root', 'LocalMachine')
$store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
$bg = $app = $null
try {
    $store.Add([Security.Cryptography.X509Certificates.X509Certificate2]::new($cert.RawData))
    [IO.File]::WriteAllText((Join-Path $scratch 'cert.pem'), $cert.ExportCertificatePem())
    [IO.File]::WriteAllText((Join-Path $scratch 'key.pem'), $rsa.ExportPkcs8PrivateKeyPem())
    $script = Join-Path $PSScriptRoot 'image-relay.py'
    $bg = Start-Process -FilePath (Get-Command python).Source -ArgumentList @('-B', ('"' + $script + '"'), ('"' + $scratch + '"')) -WindowStyle Hidden -PassThru
    $pair = Join-Path $scratch 'pair.json'
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (!(Test-Path -LiteralPath $pair)) {
        if ($bg.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'Disposable HTTPS relay did not start.' }
        Start-Sleep -Milliseconds 150
    }
    Get-Process -Name ShuangpinLink -ErrorAction SilentlyContinue | Where-Object {
        $_.SessionId -eq (Get-Process -Id $PID).SessionId -and $_.Path -eq $AppPath
    } | ForEach-Object { $_.Kill(); $_.WaitForExit(5000) | Out-Null }
    dotnet $Editor --seed-image-pair $pair
    $app = Start-Process -FilePath $AppPath -ArgumentList '--tray' -WindowStyle Hidden -PassThru
    dotnet $Editor --image-roundtrip $pair
} finally {
    if ($app -and !$app.HasExited) { $app.Kill(); $app.WaitForExit(5000) | Out-Null }
    if ($bg -and !$bg.HasExited) { $bg.Kill(); $bg.WaitForExit(5000) | Out-Null }
    $store.Remove($cert)
    $store.Close()
    $cert.Dispose()
    $rsa.Dispose()
    if (Test-Path -LiteralPath $state) { Remove-Item -LiteralPath $state -Force }
    $full = (Resolve-Path -LiteralPath $scratch).Path
    if ([IO.Path]::GetDirectoryName($full) -eq [IO.Path]::GetTempPath().TrimEnd('\') -and
        [IO.Path]::GetFileName($full) -match '^spl-image-fixture-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}
