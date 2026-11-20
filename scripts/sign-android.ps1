param(
    [Parameter(Mandatory)][string]$UnsignedApk,
    [Parameter(Mandatory)][string]$OutputApk,
    [Parameter(Mandatory)][string]$BuildTools,
    [Parameter(Mandatory)][string]$Keystore,
    [string]$Alias = 'shuangpin-link'
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
if (-not $env:SHUANGPIN_SIGNING_PASSWORD) { throw 'Set SHUANGPIN_SIGNING_PASSWORD for this session.' }
$toolDirectory = (Resolve-Path $BuildTools).Path
$keyFile = (Resolve-Path $Keystore).Path
$source = (Resolve-Path $UnsignedApk).Path
$output = [System.IO.Path]::GetFullPath($OutputApk)
if ($source -eq $output) { throw 'Choose a different output APK path.' }
[void](New-Item -ItemType Directory -Force (Split-Path $output -Parent))
$aligned = Join-Path (Split-Path $output -Parent) ([guid]::NewGuid().ToString('N') + '.aligned.apk')
try {
    & (Join-Path $toolDirectory 'zipalign.exe') -P 16 -f -v 4 $source $aligned | Select-Object -Last 1
    & (Join-Path $toolDirectory 'apksigner.bat') sign --ks $keyFile --ks-key-alias $Alias --ks-pass env:SHUANGPIN_SIGNING_PASSWORD --key-pass env:SHUANGPIN_SIGNING_PASSWORD --out $output $aligned
    & (Join-Path $toolDirectory 'apksigner.bat') verify --verbose --print-certs $output
    Get-FileHash -Algorithm SHA256 $output | Format-List
} finally {
    Remove-Item -LiteralPath $aligned, "$output.idsig" -ErrorAction SilentlyContinue
}
