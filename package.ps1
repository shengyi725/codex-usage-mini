param()
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1') -Test
$destination = Join-Path $PSScriptRoot 'dist\CodexUsageMini-v0.2.0-portable.zip'
Compress-Archive -LiteralPath (Join-Path $PSScriptRoot 'dist\CodexUsageMini.exe'), (Join-Path $PSScriptRoot 'dist\CodexUsageMini.exe.config'), (Join-Path $PSScriptRoot 'QUICKSTART.txt') -DestinationPath $destination -Force
Write-Output "Packaged $destination"
