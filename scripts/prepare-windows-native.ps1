param([string]$OutputDirectory = '.build/windows-native')

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$ProgressPreference = 'SilentlyContinue'
Set-Location (Split-Path -Parent $PSScriptRoot)
$root = (Get-Location).Path
$version = (Get-Content -Raw updates/version.json | ConvertFrom-Json).versionName
$build = Join-Path $root '.build'
$source = Join-Path $build 'windows-native-source'
$destination = [IO.Path]::GetFullPath($OutputDirectory, $root)
$weasel = Join-Path $build 'weasel'
$librime = Join-Path $build 'windows-native-librime'
$boost = Join-Path $build 'boost_1_84_0'
$installer = Join-Path $build 'vendor/weasel.exe'
New-Item -ItemType Directory -Force $build,(Split-Path -Parent $installer) | Out-Null

function Get-GitSource([string]$Path, [string]$Repo, [string]$Tag, [string]$Revision) {
    $gitOptions = @('-c', "safe.directory=$($Path.Replace('\', '/'))")
    if (-not (Test-Path "$Path/.git")) {
        git @gitOptions clone --quiet --depth 1 --branch $Tag "https://github.com/$Repo.git" $Path
    }
    if ((git @gitOptions -C $Path rev-parse HEAD).Trim() -ne $Revision) {
        throw "Unexpected source revision for $Repo."
    }
}

function Get-VerifiedAsset([string]$Uri, [string]$Path, [string]$Sha256) {
    if (-not (Test-Path -LiteralPath $Path)) {
        Invoke-WebRequest -Uri $Uri -OutFile "$Path.partial" -TimeoutSec 180
        if ((Get-FileHash -LiteralPath "$Path.partial").Hash -ne $Sha256) {
            throw "Download digest mismatch for $Uri."
        }
        Move-Item -LiteralPath "$Path.partial" -Destination $Path
    }
    if ((Get-FileHash -LiteralPath $Path).Hash -ne $Sha256) { throw "Asset digest mismatch for $Path." }
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio 2022 C++ Build Tools with ATL, MFC, and the Windows SDK.' }
$vs = & $vswhere -products '*' -version '[17.0,18.0)' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -latest -property installationPath
if (-not $vs) { throw 'The MSVC 2022 x64/x86 toolset was not found.' }
$msbuild = Join-Path $vs 'MSBuild/Current/Bin/amd64/MSBuild.exe'
$toolsVersion = (Get-Content -LiteralPath (Join-Path $vs 'VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt') -Raw).Trim()
$libraryTool = Join-Path $vs "VC/Tools/MSVC/$toolsVersion/bin/Hostx64/x64/lib.exe"
$sevenZip = (Get-Command 7z.exe -ErrorAction SilentlyContinue | Select-Object -First 1).Source
if (-not $sevenZip) { $sevenZip = Join-Path $env:ProgramFiles '7-Zip/7z.exe' }
if (-not (Test-Path -LiteralPath $sevenZip)) { throw '7-Zip is needed to extract the pinned build assets.' }

Get-GitSource $weasel 'rime/weasel' '0.17.4' '9cc96e20dc71b80876b12f689bb5863c76c2a7ed'
Get-GitSource $librime 'rime/librime' '1.13.1' '1c23358157934bd6e6d6981f0c0164f05393b497'
Get-VerifiedAsset 'https://github.com/rime/weasel/releases/download/0.17.4/weasel-0.17.4.0-installer.exe' $installer 'cf509534a8f5f8af9c98ed7cbb8f135439f145a8cbe7e50ede42bb5b5ab45c29'

if (-not (Test-Path "$boost/boost/version.hpp")) {
    $archive = Join-Path $build 'boost_1_84_0.7z'
    Get-VerifiedAsset 'https://archives.boost.io/release/1.84.0/source/boost_1_84_0.7z' $archive '81a4d10075731966477276c47324ecd9cac02da49899d36c48eba66e40014f25'
    & $sevenZip x -y "-o$build" $archive | Out-Host
}

Push-Location -LiteralPath $boost
try {
    if (-not (Test-Path b2.exe)) { cmd.exe /d /c bootstrap.bat }
    foreach ($bits in 64,32) {
        & .\b2.exe -j4 --with-serialization --with-thread toolset=msvc-14.3 architecture=x86 "address-model=$bits" `
            define=BOOST_USE_WINAPI_VERSION=0x0603 variant=release link=static runtime-link=static stage
    }
} finally { Pop-Location }

python -B scripts/prepare-windows-native.py --checkout $weasel --source $source --boost $boost --version $version
$headers = Join-Path $source 'librime/include'
New-Item -ItemType Directory -Force $headers | Out-Null
foreach ($name in 'rime_api.h','rime_levers_api.h') {
    $header = Get-ChildItem -LiteralPath (Join-Path $librime 'src') -Recurse -File -Filter $name | Select-Object -First 1
    if (-not $header) { throw "Missing librime header $name." }
    Copy-Item -LiteralPath $header.FullName -Destination $headers -Force
}
$engineHash = 'cd3ccdb56cb5dd1b8483f501130d17a1631936d29bb006426d579b383256489c'
$engineUnpack = Join-Path $build 'windows-native-runtime'
if (-not (Test-Path -LiteralPath $engineUnpack)) {
    & $sevenZip x -y "-o$engineUnpack" '-ir!rime.dll' $installer | Out-Host
}
$engine = Get-ChildItem -LiteralPath $engineUnpack -Recurse -File -Filter rime.dll |
    Where-Object { (Get-FileHash -LiteralPath $_.FullName).Hash -eq $engineHash } | Select-Object -First 1
if (-not $engine) { throw 'The expected x64 input engine was not found.' }
$definition = Join-Path $source 'rime.def'
@('LIBRARY rime.dll','EXPORTS','    rime_get_api') | Set-Content -LiteralPath $definition -Encoding ascii
& $libraryTool /NOLOGO /MACHINE:X64 "/DEF:$definition" "/OUT:$source/lib64/rime.lib"

$solution = Join-Path $source 'weasel.sln'
& $msbuild $solution /m /v:minimal /t:'WeaselTSF;WeaselServer;WeaselDeployer' /p:Configuration=Release /p:Platform=x64
& $msbuild $solution /m /v:minimal /t:WeaselTSF /p:Configuration=Release /p:Platform=Win32

$files = [ordered]@{
    'ShuangpinIme64.dll' = Join-Path $source 'output/weaselx64.dll'
    'ShuangpinIme32.dll' = Join-Path $source 'output/weasel.dll'
    'ShuangpinImeServer.exe' = Join-Path $source 'output/WeaselServer.exe'
    'ShuangpinImeDeployer.exe' = Join-Path $source 'output/WeaselDeployer.exe'
    'rime.dll' = $engine.FullName
    'LICENSE-native-frontend.txt' = Join-Path $source 'LICENSE.txt'
    'LICENSE-input-engine.txt' = Join-Path $librime 'LICENSE'
    'LICENSE-boost.txt' = Join-Path $boost 'LICENSE_1_0.txt'
    'data/shuangpin_link.yaml' = Join-Path $source 'output/data/weasel.yaml'
}
New-Item -ItemType Directory -Force "$destination/data" | Out-Null
foreach ($name in $files.Keys) {
    Copy-Item -LiteralPath $files[$name] -Destination (Join-Path $destination $name) -Force
}
