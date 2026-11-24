param(
    [Parameter(Mandatory)][string]$Installer,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$LegacyProfile,
    [switch]$Isolated
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
if (-not $Isolated) { throw 'Use this installer smoke test in a disposable Windows VM.' }
$installerPath = (Resolve-Path -LiteralPath $Installer).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
[void](New-Item -ItemType Directory -Force -Path $output)
$session = (Get-Process -Id $PID).SessionId
& {
    "Windows $([Environment]::OSVersion.Version), session $session, interactive=$([Environment]::UserInteractive)"
    Get-Process -Name explorer, ctfmon -ErrorAction SilentlyContinue |
        Select-Object Name, Id, SessionId | Format-Table -AutoSize | Out-String
} | Set-Content -LiteralPath (Join-Path $output 'environment.txt')
if (-not [Environment]::UserInteractive -or $session -eq 0) {
    throw 'A Windows user desktop is required to test real keyboard input.'
}

$editorBuild = Join-Path $output 'editor-build'
dotnet build (Join-Path $PSScriptRoot 'smoke-windows-ime/Editor.csproj') --configuration Release --output $editorBuild
$editorAssembly = Join-Path $editorBuild 'Editor.dll'

$legacyDefault = Join-Path $env:APPDATA 'Rime/default.custom.yaml'
$legacyData = Split-Path -Parent $legacyDefault
$legacyTip = 'HKLM:/SOFTWARE/Microsoft/CTF/TIP/{A3F4CDED-B1E9-41EE-9CA6-7B4D0DE6CB0A}'
$legacyHash = $null
if ($LegacyProfile) {
    $oldApp = Join-Path $env:LOCALAPPDATA 'ShuangpinLink/app'
    if ((Test-Path -LiteralPath $legacyData) -or (Test-Path -LiteralPath $oldApp) -or (Test-Path -LiteralPath $legacyTip)) {
        throw 'The legacy-profile fixture requires a fresh VM with a clean user profile.'
    }
    dotnet $editorAssembly --seed-legacy-tip 2>&1 | Tee-Object -FilePath (Join-Path $output 'legacy-tip-before.txt')
    if (-not (Test-Path -LiteralPath $legacyTip)) { throw 'The fixture did not register the legacy machine TIP.' }
    [void](New-Item -ItemType Directory -Path $legacyData, $oldApp -Force)
    [void](New-Item -ItemType File -Path (Join-Path $oldApp 'ShuangpinLink.exe'))
    [void](New-Item -ItemType File -Path (Join-Path $legacyData 'predict.db'))
    [void](New-Item -ItemType File -Path (Join-Path $legacyData 'double_pinyin_mspy.schema.yaml'))
    "patch:`n  preset_color_schemes/shuangpin_link:`n    name: 旧版测试" |
        Set-Content -LiteralPath (Join-Path $legacyData 'weasel.custom.yaml')
    "patch:`n  schema_list:`n    - schema: bopomofo" | Set-Content -LiteralPath $legacyDefault
    "var:`n  previously_selected_schema: bopomofo" | Set-Content -LiteralPath (Join-Path $legacyData 'user.yaml')
    $legacyHash = (Get-FileHash -LiteralPath $legacyDefault).Hash
}

function Save-NativeDiagnostics {
    $logRoot = Join-Path $env:TEMP 'rime.shuangpin_link'
    if (-not (Test-Path -LiteralPath $logRoot -PathType Container)) { return }
    $logs = Get-ChildItem -LiteralPath $logRoot -File -Filter 'rime.shuangpin_link.*' |
        Where-Object { $_.Name -match '\.log(?:\.|$)' -and -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) } |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 4
    & {
        foreach ($log in $logs) {
            "--- $($log.Name) ---"
            Get-Content -LiteralPath $log.FullName -Tail 100
        }
    } | Set-Content -LiteralPath (Join-Path $output 'native-deployment.txt')
}

try {
    $setupOut = Join-Path $output 'setup-output.txt'
    $setupError = Join-Path $output 'setup-error.txt'
    $installed = Start-Process -FilePath $installerPath -ArgumentList '--quiet' -PassThru `
        -RedirectStandardOutput $setupOut -RedirectStandardError $setupError
    if (-not $installed.WaitForExit(240000)) {
        $installed.Kill()
        throw 'Setup did not finish in four minutes.'
    }
    if ($installed.ExitCode -ne 0) {
        Get-Content -LiteralPath $setupError -Tail 40 | ForEach-Object { Write-Host $_ }
        throw "Setup failed ($($installed.ExitCode))."
    }

    $native = (Get-ItemProperty -LiteralPath 'HKLM:/SOFTWARE/ShuangpinLink/IME').InstallDir
    $dll = (Get-Item -LiteralPath (Join-Path $native 'ShuangpinIme64.dll')).FullName
    $files = Get-ChildItem -LiteralPath $native -File -Recurse
    $files | Select-Object Name, @{Name='Description';Expression={$_.VersionInfo.FileDescription}} |
        Format-Table -AutoSize | Out-String -Width 170 | Set-Content -LiteralPath (Join-Path $output 'installed-files.txt')
    $unexpected = $files | Where-Object {
        $_.Extension -in '.exe', '.dll', '.ime' -and (
            $_.Name -match 'weasel' -or $_.VersionInfo.FileDescription -match '小狼毫|Weasel' -or
            $_.VersionInfo.ProductName -match '小狼毫|Weasel'
        )
    }
    if ($unexpected) { throw "Stock IME executable identity found: $($unexpected.Name -join ', ')" }
    if ($LegacyProfile) {
        if (-not (Test-Path -LiteralPath $legacyTip)) { throw 'The installer removed the legacy machine TIP.' }
        dotnet $editorAssembly --assert-legacy-tip-hidden 2>&1 | Tee-Object -FilePath (Join-Path $output 'legacy-tip-after.txt')
    } elseif (Test-Path -LiteralPath $legacyTip) {
        throw 'The clean installer registered the legacy Windows IME profile.'
    }

    $rime = Join-Path $env:APPDATA 'ShuangpinLink/Rime'
    if ($LegacyProfile) {
        if (-not (Test-Path -LiteralPath (Join-Path $rime 'legacy-dictionary-migrated'))) {
            throw 'Setup did not recognize the legacy 双拼互联 installation.'
        }
        if ((Get-FileHash -LiteralPath $legacyDefault).Hash -ne $legacyHash) {
            throw 'Setup changed the legacy input data rather than deploying the product profile separately.'
        }
    }
    $compiled = Join-Path $rime 'build/default.yaml'
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    while (-not (Test-Path -LiteralPath $compiled) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
    $default = Get-Content -Raw -LiteralPath $compiled
    $schemes = [regex]::Matches($default, '(?m)^ *- schema: *(\w+) *\r?$').Groups |
        Where-Object Name -eq '1' | Select-Object -ExpandProperty Value
    if (($schemes -join ',') -ne 'double_pinyin_mspy,rime_ice') {
        throw "Unexpected deployed scheme list: $($schemes -join ', ')"
    }
    $schemes | Set-Content -LiteralPath (Join-Path $output 'deployed-schemes.txt')

    # Setup launches the settings window asynchronously. Let it finish taking
    # its initial focus before activating the independent editor's input profile.
    $appPath = Join-Path $env:LOCALAPPDATA 'ShuangpinLink/app/ShuangpinLink.exe'
    $appDeadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        $settings = Get-Process -Name ShuangpinLink -ErrorAction SilentlyContinue |
            Where-Object { $_.SessionId -eq $session -and $_.Path -eq $appPath -and $_.MainWindowHandle -ne 0 } |
            Select-Object -First 1
        if ($null -ne $settings -and $settings.WaitForInputIdle(1000)) { break }
        $settings = $null
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $appDeadline)
    if ($null -eq $settings) { throw 'The installed settings window did not finish opening.' }
    Write-Host "Installed settings window is ready (PID $($settings.Id))."
    $settings.Dispose()

    dotnet $editorAssembly $output $dll 2>&1 |
        Tee-Object -FilePath (Join-Path $output 'editor.txt')

    $selected = Get-Content -Raw -LiteralPath (Join-Path $rime 'user.yaml')
    if ($selected -notmatch '(?m)^ +previously_selected_schema: rime_ice *\r?$') {
        throw 'The keyboard scheme picker did not save the selection of 普通拼音.'
    }
} catch {
    try { Save-NativeDiagnostics }
    catch { Write-Warning ("Could not collect the native deployment diagnostics: " + $_.Exception.Message) }
    throw
}
