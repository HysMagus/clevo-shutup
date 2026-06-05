# Build the tray app if needed, then launch it elevated (UAC prompt).
$root = $PSScriptRoot
$exe  = Join-Path $root 'ClevoFan.Tray\bin\Release\net8.0-windows\ClevoFanTray.exe'

if (-not (Test-Path $exe)) {
    Write-Host "Building tray app..." -ForegroundColor Cyan
    dotnet build (Join-Path $root 'ClevoFan.Tray\ClevoFan.Tray.csproj') -c Release
}

if (-not (Test-Path $exe)) { throw "Build did not produce $exe" }

Write-Host "Launching Clevo Fan (look for the icon in the hidden-icons overflow '^')." -ForegroundColor Green
Start-Process $exe -Verb RunAs
