# Build both projects (Release). Run from anywhere.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

dotnet build (Join-Path $root 'ClevoFan\ClevoFan.csproj') -c Release
dotnet build (Join-Path $root 'ClevoFan.Tray\ClevoFan.Tray.csproj') -c Release

Write-Host "`nBuilt. CLI:  ClevoFan\bin\Release\net8.0-windows\clevofan.exe" -ForegroundColor Green
Write-Host "Built. Tray: ClevoFan.Tray\bin\Release\net8.0-windows\ClevoFanTray.exe" -ForegroundColor Green
Write-Host "Reminder: put LpcACPIEC.bin in ClevoFan\bin\Release\net8.0-windows\ (tray copies it from there)." -ForegroundColor Yellow
