<#
Run elevated, once. Creates the virtual Ethernet adapter the bridge feeds ("iPhone USB").
Uses OpenVPN's TAP-Windows driver 9.27.0 (ARM64 build, signed by Microsoft), downloaded from its
GitHub release and checked against a pinned SHA-256.
#>
param([string]$Log = "$env:TEMP\iphone-tether-tap.log")
$ErrorActionPreference = 'Stop'
$Name = 'iPhone USB'
$Url = 'https://github.com/OpenVPN/tap-windows6/releases/download/9.27.0/dist.win10.zip'
$Sha256 = '36E2609B7CEEFEDCB978CE5C48CAF9E0E5AF83423717C4E2E3C1D7EBCA8F62A5'
Start-Transcript -Path $Log -Force | Out-Null
try {
    $adapter = Get-NetAdapter -Name $Name -ErrorAction SilentlyContinue
    if (-not $adapter) {
        $dir = Join-Path $env:ProgramData 'iphone-usb-tether\tap'
        New-Item -ItemType Directory -Force $dir | Out-Null
        $zip = Join-Path $dir 'dist.win10.zip'
        Invoke-WebRequest $Url -OutFile $zip
        if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $Sha256) { throw 'TAP driver download does not match the pinned hash.' }
        Expand-Archive $zip -DestinationPath $dir -Force
        $arm = Join-Path $dir 'dist.win10\arm64'
        $before = @(Get-NetAdapter -IncludeHidden | Where-Object InterfaceDescription -like 'TAP-Windows Adapter V9*').InterfaceGuid
        & (Join-Path $arm 'devcon.exe') install (Join-Path $arm 'OemVista.inf') tap0901
        Start-Sleep 3
        $adapter = Get-NetAdapter -IncludeHidden | Where-Object { $_.InterfaceDescription -like 'TAP-Windows Adapter V9*' -and $_.InterfaceGuid -notin $before } | Select-Object -First 1
        if (-not $adapter) { throw 'TAP adapter was not created.' }
        Rename-NetAdapter -Name $adapter.Name -NewName $Name
    }
    # Prefer this link over Wi-Fi whenever it is up.
    Set-NetIPInterface -InterfaceAlias $Name -InterfaceMetric 10
    Get-NetAdapter -Name $Name | Format-List Name, InterfaceDescription, Status, MacAddress, InterfaceGuid
}
catch { "FAILED: $_"; $_.ScriptStackTrace }
finally { Stop-Transcript | Out-Null }
