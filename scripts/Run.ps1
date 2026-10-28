$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$applicationPath = Join-Path $projectRoot 'artifacts\publish\NetworkProfileManager.exe'
if (!(Test-Path -LiteralPath $applicationPath)) { throw 'Build first: .\scripts\Build.ps1' }
Start-Process -FilePath $applicationPath
