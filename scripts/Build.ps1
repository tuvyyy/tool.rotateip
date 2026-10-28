param([switch]$SkipTests, [switch]$SkipInstaller)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
function Invoke-BuildTool {
    param([string]$File, [string[]]$ToolArgs)
    $ErrorActionPreference = 'Continue'
    & $File @ToolArgs
    $toolExitCode = $LASTEXITCODE
    if ($toolExitCode -ne 0) { throw "Build tool failed with exit code $toolExitCode" }
}
$dotnetPath = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $dotnetPath)) { $dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source }
New-Item -ItemType Directory -Path artifacts -Force | Out-Null
& "$PSScriptRoot\New-AppIcon.ps1"
Invoke-BuildTool $dotnetPath @('build','NetworkProfileManager.sln','-c','Release')
if (!$SkipTests) {
    Invoke-BuildTool $dotnetPath @('test','NetworkProfileManager.sln','-c','Release','--no-build','--logger','trx;LogFileName=results.trx','--results-directory','artifacts\tests')
}
Invoke-BuildTool $dotnetPath @('publish','src\NetworkProfileManager.App','-c','Release','-r','win-x64','--self-contained','true','-p:PublishSingleFile=false','-p:PublishTrimmed=false','-p:DebugType=none','-o','artifacts\publish')
if (!$SkipInstaller) {
    $compilerPath = Join-Path $projectRoot '.tools\InnoPackage\tools\ISCC.exe'
    if (!(Test-Path -LiteralPath $compilerPath)) {
        $compilerPath = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source
        if (!$compilerPath) { $compilerPath = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe' }
    }
    if (!(Test-Path -LiteralPath $compilerPath)) { throw 'Inno Setup compiler missing. Run scripts\Bootstrap.ps1 or install Inno Setup 6.' }
    Invoke-BuildTool $compilerPath @('Installer\NetworkProfileManager.iss')
}
Get-ChildItem artifacts\publish\NetworkProfileManager.exe,artifacts\installer\NetworkProfileManager_Setup.exe -ErrorAction SilentlyContinue | Select-Object FullName,Length
