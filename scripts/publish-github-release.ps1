param(
    [Parameter(Mandatory)][string]$ReleaseDirectory,
    [string]$Notes = '',
    [switch]$DraftOnly
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$ProgressPreference = 'SilentlyContinue'
$source = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
Set-Location (Split-Path -Parent $PSScriptRoot)

python -B scripts/release-update.py verify --source $source
$version = Get-Content -LiteralPath updates/version.json -Raw | ConvertFrom-Json
$tag = "v$($version.versionName)"
if ([string]::IsNullOrWhiteSpace($Notes)) { $Notes = "双拼互联 $($version.versionName)" }
$repository = 'winner798/shuangpin-link'
$token = $env:SHUANGPIN_GITHUB_TOKEN
if (-not $token) {
    $env:GCM_INTERACTIVE = 'Never'
    $credentials = "protocol=https`nhost=github.com`nusername=winner798`n`n" | git -c credential.interactive=never credential fill
    $password = $credentials | Where-Object { $_.StartsWith('password=') } | Select-Object -First 1
    if (-not $password) { throw 'Sign in to GitHub or set SHUANGPIN_GITHUB_TOKEN.' }
    $token = $password.Substring(9)
}
$headers = @{
    Authorization = "Bearer $token"
    Accept = 'application/vnd.github+json'
    'User-Agent' = 'ShuangpinLink-Publisher'
    'X-GitHub-Api-Version' = '2026-03-10'
}
$api = "https://api.github.com/repos/$repository"
if ((Invoke-RestMethod -Uri $api -Headers $headers).private) { throw 'GitHub updater requires a public repository.' }
$head = (Invoke-RestMethod -Uri "$api/git/ref/heads/main" -Headers $headers).object.sha
$remoteTree = (Invoke-RestMethod -Uri "$api/git/commits/$head" -Headers $headers).tree.sha
if ((git rev-parse 'HEAD^{tree}').Trim() -ne $remoteTree -or (git status --porcelain)) {
    throw 'Publish from a clean checkout matching GitHub main.'
}
try {
    $tagRef = (Invoke-RestMethod -Uri "$api/git/ref/tags/$tag" -Headers $headers).object
    if ($tagRef.type -eq 'tag') {
        $tagRef = (Invoke-RestMethod -Uri "$api/git/tags/$($tagRef.sha)" -Headers $headers).object
    }
    if ($tagRef.type -ne 'commit' -or $tagRef.sha -ne $head) { throw "$tag points to a different source commit." }
} catch {
    if (-not $_.Exception.Response -or [int]$_.Exception.Response.StatusCode -ne 404) { throw }
}

$release = (Invoke-RestMethod -Uri "$api/releases?per_page=100" -Headers $headers) |
    Where-Object { $_.tag_name -eq $tag } | Select-Object -First 1
if (-not $release) {
    $request = @{tag_name=$tag; target_commitish=$head; name="双拼互联 $($version.versionName)"; body=$Notes; draft=$true; prerelease=$false}
    $release = Invoke-RestMethod -Uri "$api/releases" -Method Post -Headers $headers `
        -ContentType 'application/json; charset=utf-8' -Body ($request | ConvertTo-Json -Compress) -MaximumRedirection 0
}
if ($release.draft -and $release.target_commitish -ne $head) { throw "The draft $tag targets a different source commit." }
$releaseId = [long]$release.id
$packages = Join-Path $source ([string]$version.versionCode)
$files = [ordered]@{
    'ShuangpinLinkSetup.exe' = Join-Path $packages 'windows-x64.exe'
    'ShuangpinLink-Android-arm64-v8a.apk' = Join-Path $packages 'android-arm64-v8a.apk'
    'ShuangpinLink-Android-x86_64.apk' = Join-Path $packages 'android-x86_64.apk'
    'update-manifest.json' = Join-Path $source 'manifest.json'
}
$stored = Invoke-RestMethod -Uri "$api/releases/$releaseId/assets?per_page=100" -Headers $headers
foreach ($filename in $files.Keys) {
    $file = Get-Item -LiteralPath $files[$filename]
    $digest = 'sha256:' + (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $asset = $stored | Where-Object { $_.name -eq $filename } | Select-Object -First 1
    if ($release.draft -and $asset -and $asset.state -eq 'starter' -and $asset.size -eq 0) {
        Invoke-RestMethod -Uri "$api/releases/assets/$($asset.id)" -Method Delete -Headers $headers -MaximumRedirection 0 | Out-Null
        $asset = $null
    }
    if (-not $asset -and $release.draft) {
        Write-Host "Uploading $filename"
        $encoded = [Uri]::EscapeDataString($filename)
        $asset = Invoke-RestMethod -Uri "https://uploads.github.com/repos/$repository/releases/$releaseId/assets?name=$encoded" `
            -Method Post -Headers $headers -ContentType 'application/octet-stream' -InFile $file.FullName `
            -MaximumRedirection 0 -TimeoutSec 300
    }
    if (-not $asset -or $asset.state -ne 'uploaded' -or $asset.size -ne $file.Length -or $asset.digest -ne $digest) {
        throw "Release asset verification failed for $filename. Existing published files were preserved."
    }
}

if ($DraftOnly -and $release.draft) {
    Write-Host "Verified all files in the draft $tag."
    return
}
if ($release.draft) {
    $release = Invoke-RestMethod -Uri "$api/releases/$releaseId" -Method Patch -Headers $headers `
        -ContentType 'application/json' -Body '{"draft":false,"prerelease":false,"make_latest":"true"}' -MaximumRedirection 0
}
if ($release.draft -or $release.tag_name -ne $tag) { throw 'GitHub release is not published.' }
$latest = Invoke-RestMethod -Uri "$api/releases/latest" -Headers $headers
if ($latest.tag_name -ne $tag) { throw 'The published release is not the latest GitHub release.' }
Write-Host $release.html_url
