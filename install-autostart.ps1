#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Make ClevoFanTray launch (elevated, no UAC prompt) at every logon via a logon Scheduled Task.
  Copies the built app to a stable local folder, then registers the task. Re-run after rebuilding.

.PARAMETER Source
  Folder containing the built ClevoFanTray.exe. Defaults to this repo's Release output.

.PARAMETER InstallDir
  Stable folder to run the app from at logon. Defaults to %LOCALAPPDATA%\ClevoFan.

.PARAMETER TaskName
  Scheduled Task name. Defaults to 'ClevoFanTray'.

.EXAMPLE
  # defaults (run from the repo root)
  powershell -ExecutionPolicy Bypass -File .\install-autostart.ps1

.EXAMPLE
  # custom build output + install location
  .\install-autostart.ps1 -Source 'D:\builds\tray' -InstallDir 'C:\Tools\ClevoFan'
#>
param(
    [string]$Source     = (Join-Path $PSScriptRoot 'ClevoFan.Tray\bin\Release\net8.0-windows'),
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'ClevoFan'),
    [string]$TaskName   = 'ClevoFanTray'
)
$ErrorActionPreference = 'Stop'

$exe = Join-Path $InstallDir 'ClevoFanTray.exe'

if (-not (Test-Path (Join-Path $Source 'ClevoFanTray.exe'))) {
    throw "Build output not found at '$Source'.`nBuild first: dotnet build ClevoFan.Tray\ClevoFan.Tray.csproj -c Release`nOr pass -Source <path-to-build-output>."
}

Write-Host "Copying app to $InstallDir ..." -ForegroundColor Cyan
robocopy $Source $InstallDir /E /NFL /NDL /NJH /NJS /NP | Out-Null
if (-not (Test-Path $exe)) { throw "Copy failed; '$exe' is missing." }

Write-Host "Registering logon task '$TaskName' (elevated, runs in your session)..." -ForegroundColor Cyan
# Task Scheduler needs the fully-qualified account (DOMAIN\user), not just the bare username.
$me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$action    = New-ScheduledTaskAction -Execute $exe
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User $me
$principal = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                                          -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) `
                                          -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
                       -Principal $principal -Settings $settings -Force | Out-Null

Write-Host "Done. '$TaskName' will launch elevated at every logon (look in the hidden-icons overflow)." -ForegroundColor Green
Write-Host "Start it now without rebooting:  Start-ScheduledTask -TaskName $TaskName" -ForegroundColor Green
