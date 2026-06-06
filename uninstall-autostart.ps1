#Requires -RunAsAdministrator
# Remove the ClevoFanTray logon autostart.
param(
    [string]$TaskName   = 'ClevoFanTray',
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'ClevoFan')
)
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
Write-Host "Removed the '$TaskName' logon task." -ForegroundColor Green
Write-Host "App files remain in $InstallDir (delete that folder to fully remove)." -ForegroundColor Yellow
