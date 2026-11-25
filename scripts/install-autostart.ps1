<#
No elevation needed. Publishes the bridge to %LOCALAPPDATA%\iphone-usb-tether and registers a
scheduled task that runs it at every logon (it has no window; it logs to bridge.log in that folder).
The bridge idles until the phone is plugged in.
Remove with:  Unregister-ScheduledTask -TaskName 'iPhone USB tether' -Confirm:$false
#>
$ErrorActionPreference = 'Stop'
$TaskName = 'iPhone USB tether'
$dir = Join-Path $env:LOCALAPPDATA 'iphone-usb-tether'
$root = Split-Path $PSScriptRoot -Parent

Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root 'src\IphoneTether') -c Release -o $dir --nologo -v q
if ($LASTEXITCODE) { throw 'publish failed' }

$action = New-ScheduledTaskAction -Execute "$dir\iphone-tether.exe"
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $TaskName
"Task '$TaskName' registered and started."
