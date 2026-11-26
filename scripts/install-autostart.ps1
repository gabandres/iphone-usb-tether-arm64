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

# Build into a staging folder first: a failed build must not take the running bridge down.
$stage = "$dir.new"
dotnet publish (Join-Path $root 'src\IphoneTether') -c Release -o $stage --nologo -v q
if ($LASTEXITCODE) { throw 'publish failed; the running bridge was left alone' }
Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
# The bridge installed here (and only that one) must be gone before its files are replaced. It can
# outlive its task, for example when the task was unregistered while it ran.
$mine = { Get-Process iphone-tether -ErrorAction SilentlyContinue | Where-Object Path -like "$dir\*" }
& $mine | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
& $mine | Stop-Process -Force
& $mine | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $dir | Out-Null
Copy-Item "$stage\*" $dir -Recurse -Force
Remove-Item $stage -Recurse -Force

$action = New-ScheduledTaskAction -Execute "$dir\iphone-tether.exe"
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
# Priority 4 = normal. The task default (7) is below normal for CPU and I/O, which starves the
# bridge on a busy machine: ping to the phone went from 1 ms to hundreds.
$settings = New-ScheduledTaskSettingsSet -Priority 4 -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1)
try {
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Force -ErrorAction Stop | Out-Null
}
catch {
    # A task of this name created from an elevated shell cannot be replaced from a normal one.
    Write-Warning "Could not update the task definition ($_). The existing task is started as it is; to replace it, run once elevated: Unregister-ScheduledTask -TaskName '$TaskName' -Confirm:`$false"
}
Start-ScheduledTask -TaskName $TaskName
"Task '$TaskName' registered and started."
