#Requires -RunAsAdministrator
# BACK TO NORMAL — hands both fans to the EC firmware and restarts the Clevo Control Center service.
# Use this if anything ever feels off. A reboot also fully reverts to factory behavior.

$exe = Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\clevofan.exe'
if (Test-Path $exe) {
    & $exe auto
} else {
    Write-Warning "clevofan.exe not found at $exe — build first (dotnet build -c Release)."
}

# Bring the factory fan controller back.
Start-Service CCDCHUService -ErrorAction SilentlyContinue
Write-Host "`nBoth fans handed back to the EC, Control Center service restarted. You're on factory defaults." -ForegroundColor Green
