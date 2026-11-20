param([string]$Dotnet = 'dotnet', [string]$OutputDirectory = 'dist/windows')
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Set-Location (Split-Path -Parent $PSScriptRoot)
if ($Dotnet -ne 'dotnet') { $Dotnet = (Resolve-Path $Dotnet).Path }
$version = (Get-Content -LiteralPath updates/version.json -Raw | ConvertFrom-Json).versionName
python -B scripts/prepare.py windows
New-Item -ItemType Directory -Force $OutputDirectory,.build/windows-client | Out-Null
& $Dotnet publish windows/Link/Link.csproj -c Release -r win-x64 --self-contained true "-p:Version=$version" -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .build/windows-client --nologo
@'
from zipfile import ZipFile, ZIP_DEFLATED
from pathlib import Path
base = Path('.build')
with ZipFile(base / 'setup-payload.zip', 'w', ZIP_DEFLATED, compresslevel=6) as output:
    output.write(base / 'windows-client/ShuangpinLink.exe', 'ShuangpinLink.exe')
    output.write(base / 'vendor/weasel.exe', 'weasel.exe')
    for file in sorted((base / 'windows-data').rglob('*')):
        if file.is_file():
            output.write(file, 'rime/' + file.relative_to(base / 'windows-data').as_posix())
'@ | python -B -
& $Dotnet publish windows/Setup/Setup.csproj -c Release -r win-x64 --self-contained true "-p:Version=$version" -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $OutputDirectory --nologo
$package = (Resolve-Path (Join-Path $OutputDirectory 'ShuangpinLinkSetup.exe')).Path
$check = Start-Process -FilePath $package -ArgumentList '--check' -PassThru -Wait -WindowStyle Hidden
if ($check.ExitCode -ne 0) { throw 'Windows installation bundle check failed.' }
Get-FileHash -Algorithm SHA256 $package | Format-List

