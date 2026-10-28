param(
    [string]$CandidatePublish = 'C:\toolrotateip\artifacts\release-candidate\publish',
    [string]$BaselinePublish = 'C:\toolrotateip\artifacts\qa-baseline\publish'
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $workspace
$dotnet = Join-Path $workspace '.tools\dotnet\dotnet.exe'
$compiler = Join-Path $workspace '.tools\InnoPackage\tools\ISCC.exe'
$harness = Join-Path $workspace 'artifacts\qa-harness-build\bin\ReleaseHarness\release\ReleaseHarness.dll'
$token = [Guid]::NewGuid().ToString('N')
$qaId = "NPM-QA-$token"
$qaName = "Network Profile Manager QA $token"
$sandbox = [IO.Path]::GetFullPath((Join-Path $workspace "artifacts\qa-install\$token"))
$allowed = [IO.Path]::GetFullPath((Join-Path $workspace 'artifacts\qa-install')) + '\'
if (!$sandbox.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid QA sandbox' }
$install = Join-Path $sandbox 'app'
$data = Join-Path $sandbox 'data'
$setupOutput = Join-Path $sandbox 'setups'
New-Item -ItemType Directory -Force -Path $setupOutput | Out-Null
$steps = New-Object 'System.Collections.Generic.List[object]'
$report = Join-Path $workspace 'artifacts\logs\installer-smoke.json'
$qaApp = $null
function Record([string]$id, [string]$expected, [string]$actual) {
    $steps.Add([pscustomobject]@{ Id=$id; Expected=$expected; Actual=$actual; Status='PASS' })
}
function RunNative([string]$exe, [string[]]$toolArgs, [string]$log) {
    $ErrorActionPreference = 'Continue'
    & $exe @toolArgs > $log 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Tool returned $LASTEXITCODE; see $log" }
}
function RunSetup([string]$exe, [string[]]$toolArgs) {
    $process = Start-Process -FilePath $exe -ArgumentList $toolArgs -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(60000)) { $process.Kill(); throw 'QA setup/uninstall timed out' }
    if ($process.ExitCode -ne 0) { throw "QA setup/uninstall exit code $($process.ExitCode)" }
}
function VerifyData([string]$stage) {
    RunNative $dotnet @($harness,'verify',$data) (Join-Path $sandbox "$stage-data.json")
    Record "DATA-$stage" 'DPAPI, theme, browser selections and profile marker preserved' 'Harness assertions passed'
}
try {
    $registry = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\${qaId}_is1"
    if (Test-Path -LiteralPath $registry) { throw 'QA uninstall key unexpectedly exists' }
    RunNative $dotnet @($harness,'seed',$data) (Join-Path $sandbox 'seed.json')
    $mutex = (& $dotnet $harness mutex $data | Out-String).Trim()
    foreach ($build in @(@('old','1.0.0',$BaselinePublish),@('new','1.0.1',$CandidatePublish))) {
        RunNative $compiler @("/DAppName=$qaName","/DAppId=$qaId","/DAppMutexName=$mutex","/DAppVersion=$($build[1])","/DPublishRoot=$($build[2])","/O$setupOutput","/FQA_$($build[0])",'Installer\NetworkProfileManager.iss') (Join-Path $sandbox "compile-$($build[0]).log")
    }
    Record 'INS-BUILD-QA' 'Same installer source builds sandbox variants' 'Old and new installers compiled'
    $common = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/DIR=`"$install`"","/GROUP=`"$qaName`"",'/TASKS=desktopicon')
    RunSetup (Join-Path $setupOutput 'QA_old.exe') ($common + @("/LOG=`"$sandbox\install-old.log`""))
    $exe = Join-Path $install 'NetworkProfileManager.exe'
    if (!(Test-Path -LiteralPath $exe)) { throw 'Installed EXE is missing' }
    if (!(Test-Path -LiteralPath $registry)) { throw 'User uninstall registry key missing' }
    Record 'INS-OLD' 'Old payload installs without administrator requirement' ([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion)
    $desktopLink = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) "$qaName.lnk"
    $menuLink = Join-Path ([Environment]::GetFolderPath('Programs')) "$qaName\$qaName.lnk"
    $shell = New-Object -ComObject WScript.Shell
    foreach ($link in @($desktopLink,$menuLink)) {
        if (!(Test-Path -LiteralPath $link)) { throw 'QA shortcut missing' }
        if ($shell.CreateShortcut($link).TargetPath -ne $exe) { throw 'QA shortcut target mismatch' }
    }
    Record 'INS-SHORTCUTS' 'Desktop and Start Menu shortcuts target sandbox EXE' 'Both shortcuts verified'
    RunSetup (Join-Path $setupOutput 'QA_new.exe') ($common + @("/LOG=`"$sandbox\upgrade.log`""))
    if (![Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion.StartsWith('1.0.1')) { throw 'Upgrade version mismatch' }
    VerifyData 'upgrade'
    $oldRoot = $env:DOTNET_ROOT; $oldPath = $env:PATH
    try {
        $env:DOTNET_ROOT = Join-Path $sandbox 'no-system-runtime'
        $env:PATH = [Environment]::GetFolderPath('System')
        $qaApp = Start-Process -FilePath $exe -ArgumentList '--data-directory',"`"$data`"" -WindowStyle Hidden -PassThru
    } finally { $env:DOTNET_ROOT=$oldRoot; $env:PATH=$oldPath }
    for ($attempt=0; $attempt -lt 100; $attempt++) { Start-Sleep -Milliseconds 100; $qaApp.Refresh(); if ($qaApp.HasExited -or $qaApp.MainWindowTitle) { break } }
    if ($qaApp.HasExited -or !$qaApp.MainWindowTitle -or !$qaApp.Responding) { throw 'Installed self-contained application failed to show a responsive window' }
    Record 'INS-RUN' 'Installed EXE works with no usable DOTNET_ROOT/Python PATH' 'Responsive WPF window observed'
    if (!$qaApp.CloseMainWindow() -or !$qaApp.WaitForExit(15000)) { throw 'Installed application did not close cleanly' }
    VerifyData 'app-exit'
    RunSetup (Join-Path $install 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',"/LOG=`"$sandbox\uninstall.log`"")
    if ((Test-Path -LiteralPath $exe) -or (Test-Path -LiteralPath $registry) -or (Test-Path -LiteralPath $desktopLink) -or (Test-Path -LiteralPath $menuLink)) { throw 'Uninstall left application registration/files/shortcuts' }
    VerifyData 'uninstall'
    Record 'INS-UNINSTALL' 'App files, registry and shortcuts removed; user data preserved' 'All assertions passed'
    RunSetup (Join-Path $setupOutput 'QA_new.exe') ($common + @("/LOG=`"$sandbox\reinstall.log`""))
    if (!(Test-Path -LiteralPath $exe)) { throw 'Reinstalled EXE missing' }
    VerifyData 'reinstall'
    RunSetup (Join-Path $install 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')
    Record 'INS-REINSTALL' 'Reinstall and second uninstall work' 'All assertions passed'
    $identity = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    [pscustomobject]@{ Status='PASS'; Namespace=$qaId; Sandbox=$sandbox; CandidatePublish=$CandidatePublish;
        CurrentTokenAdministrator=$identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator);
        Signed=(Get-AuthenticodeSignature (Join-Path $setupOutput 'QA_new.exe')).Status.ToString();
        Steps=$steps; ProductionNamespaceInstalled=$false } | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 $report
} catch {
    [pscustomobject]@{ Status='FAIL'; Sandbox=$sandbox; Error=$_.Exception.Message; Steps=$steps } | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 $report
    throw
} finally {
    if ($qaApp -and !$qaApp.HasExited) { $null=$qaApp.CloseMainWindow(); if (!$qaApp.WaitForExit(5000)) { $qaApp.Kill() } }
}
