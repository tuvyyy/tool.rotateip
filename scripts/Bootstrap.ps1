$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
New-Item -ItemType Directory -Path .tools -Force | Out-Null
$dotnetDir = Join-Path $projectRoot '.tools\dotnet'
if (!(Test-Path (Join-Path $dotnetDir 'dotnet.exe'))) {
    Invoke-WebRequest -UseBasicParsing 'https://dot.net/v1/dotnet-install.ps1' -OutFile .tools\dotnet-install.ps1
    & .\.tools\dotnet-install.ps1 -Channel 10.0 -InstallDir $dotnetDir
    if (!(Test-Path (Join-Path $dotnetDir 'dotnet.exe'))) { throw '.NET SDK installation failed' }
}

$userPath = [Environment]::GetEnvironmentVariable('PATH', 'User')
if ($userPath -notlike "*$dotnetDir*") {
    $newPath = if ([string]::IsNullOrWhiteSpace($userPath)) { $dotnetDir } else { "$dotnetDir;$userPath" }
    [Environment]::SetEnvironmentVariable('PATH', $newPath, 'User')
}
[Environment]::SetEnvironmentVariable('DOTNET_ROOT', $dotnetDir, 'User')

if (!(Test-Path .tools\InnoPackage\tools\ISCC.exe)) {
    # Portable NuGet distribution of the compiler; no machine-wide installation or administrator access.
    Invoke-WebRequest -UseBasicParsing 'https://api.nuget.org/v3-flatcontainer/tools.innosetup/6.7.3/tools.innosetup.6.7.3.nupkg' -OutFile .tools\innosetup.zip
    Expand-Archive -LiteralPath .tools\innosetup.zip -DestinationPath .tools\InnoPackage -Force
}
