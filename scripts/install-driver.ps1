<#
Run elevated, with the iPhone connected and Personal Hotspot on.
 1. Makes Windows select the iPhone's USB configuration 4 (the one with the Ethernet interface)
    and power-cycles the USB port so it takes effect.
 2. Installs Windows' own WinUSB driver (winusb.inf, Microsoft-signed) on the Ethernet interface
    and gives it the device interface GUID the bridge opens. No certificate, no third-party binary.
#>
param([string]$Log = "$env:TEMP\iphone-tether-install.log")
$ErrorActionPreference = 'Stop'
$InterfaceGuid = '{8E2A3C61-5B7D-4F0A-9C1E-6D4B2A7F3E10}'
Start-Transcript -Path $Log -Force | Out-Null

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

public static class TetherSetup
{
    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SP_DEVINSTALL_PARAMS
    {
        public int cbSize; public int Flags; public int FlagsEx; public IntPtr hwndParent;
        public IntPtr InstallMsgHandler; public IntPtr InstallMsgHandlerContext; public IntPtr FileQueue;
        public IntPtr ClassInstallReserved; public int Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DriverPath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SP_DRVINFO_DATA
    {
        public int cbSize; public int DriverType; public IntPtr Reserved;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string MfgName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProviderName;
        public long DriverDate; public long DriverVersion;
    }

    [DllImport("setupapi", SetLastError = true)]
    static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwnd);
    [DllImport("setupapi", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiOpenDeviceInfoW(IntPtr set, string id, IntPtr hwnd, int flags, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInstallParamsW(IntPtr set, ref SP_DEVINFO_DATA data, ref SP_DEVINSTALL_PARAMS p);
    [DllImport("setupapi", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiSetDeviceInstallParamsW(IntPtr set, ref SP_DEVINFO_DATA data, ref SP_DEVINSTALL_PARAMS p);
    [DllImport("setupapi", SetLastError = true)]
    static extern bool SetupDiBuildDriverInfoList(IntPtr set, ref SP_DEVINFO_DATA data, int type);
    [DllImport("setupapi", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiEnumDriverInfoW(IntPtr set, ref SP_DEVINFO_DATA data, int type, int index, ref SP_DRVINFO_DATA drv);
    [DllImport("setupapi", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiSetSelectedDriverW(IntPtr set, ref SP_DEVINFO_DATA data, ref SP_DRVINFO_DATA drv);
    [DllImport("setupapi", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("newdev", SetLastError = true)]
    static extern bool DiInstallDevice(IntPtr hwnd, IntPtr set, ref SP_DEVINFO_DATA data, ref SP_DRVINFO_DATA drv, int flags, out bool reboot);

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string name, uint access, int share, IntPtr sa, int disp, int flags, IntPtr tmpl);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inLen, byte[] outBuf, int outLen, out int ret, IntPtr ov);

    static void Check(bool ok, string what) { if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), what); }

    // Installs the driver whose description starts with `descriptionPrefix` from `infPath` on the device.
    [DllImport("setupapi", SetLastError = true, EntryPoint = "SetupDiCreateDeviceInfoList")]
    static extern IntPtr SetupDiCreateDeviceInfoListAnyClass(IntPtr classGuid, IntPtr hwnd);

    public static string ForceInstall(string instanceId, string infPath, string descriptionPrefix)
    {
        // The interface has no driver yet, hence no device class: the list must not filter by class.
        IntPtr set = SetupDiCreateDeviceInfoListAnyClass(IntPtr.Zero, IntPtr.Zero);
        Check(set != new IntPtr(-1), "SetupDiCreateDeviceInfoList");
        try
        {
            var dev = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            Check(SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref dev), "SetupDiOpenDeviceInfo");
            var p = new SP_DEVINSTALL_PARAMS { cbSize = Marshal.SizeOf<SP_DEVINSTALL_PARAMS>() };
            Check(SetupDiGetDeviceInstallParamsW(set, ref dev, ref p), "SetupDiGetDeviceInstallParams");
            p.Flags |= 0x00010000;      // DI_ENUMSINGLEINF
            p.FlagsEx |= 0x00000800;    // DI_FLAGSEX_ALLOWEXCLUDEDDRVS
            p.DriverPath = infPath;
            Check(SetupDiSetDeviceInstallParamsW(set, ref dev, ref p), "SetupDiSetDeviceInstallParams");
            Check(SetupDiBuildDriverInfoList(set, ref dev, 1), "SetupDiBuildDriverInfoList");
            for (int i = 0; ; i++)
            {
                var drv = new SP_DRVINFO_DATA { cbSize = Marshal.SizeOf<SP_DRVINFO_DATA>() };
                if (!SetupDiEnumDriverInfoW(set, ref dev, 1, i, ref drv))
                    throw new InvalidOperationException("No driver named '" + descriptionPrefix + "*' in " + infPath);
                if (!drv.Description.StartsWith(descriptionPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                Check(SetupDiSetSelectedDriverW(set, ref dev, ref drv), "SetupDiSetSelectedDriver");
                bool reboot;
                Check(DiInstallDevice(IntPtr.Zero, set, ref dev, ref drv, 0, out reboot), "DiInstallDevice");
                return "installed '" + drv.Description + "'" + (reboot ? " (reboot requested)" : "");
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    [DllImport("setupapi", SetLastError = true)]
    static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, int prop, byte[] buf, int size);

    // Detaches lower filter drivers from the device (takes effect on its next start).
    public static void ClearLowerFilters(string instanceId, Guid classGuid)
    {
        IntPtr set = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        Check(set != new IntPtr(-1), "SetupDiCreateDeviceInfoList");
        try
        {
            var dev = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            Check(SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref dev), "SetupDiOpenDeviceInfo");
            Check(SetupDiSetDeviceRegistryPropertyW(set, ref dev, 0x12, null, 0), "clear SPDRP_LOWERFILTERS");
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    // Same effect as unplugging and replugging the device on that hub port.
    public static void CyclePort(string hubPath, int port)
    {
        using (var h = CreateFileW(hubPath, 0x40000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
        {
            Check(!h.IsInvalid, "open hub");
            var buf = new byte[8];
            BitConverter.GetBytes(port).CopyTo(buf, 0);
            int ret;
            Check(DeviceIoControl(h, 0x220444, buf, 8, buf, 8, out ret, IntPtr.Zero), "IOCTL_USB_HUB_CYCLE_PORT");
        }
    }
}
'@

try {
    $phone = Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -match '^USB\\VID_05AC&PID_12A8\\' } | Select-Object -First 1
    if (-not $phone) { throw 'No iPhone connected.' }
    $key = "HKLM:\SYSTEM\CurrentControlSet\Enum\$($phone.InstanceId)\Device Parameters"
    $usbDeviceClass = [guid]'{88BAE032-5A81-49f0-BC3D-A4FF138216D6}'
    # Apple's AppleLowerFilter re-selects configuration 3 (no Ethernet) on every plug-in, so detach it
    # from this phone. Reinstalling Apple's driver puts it back; rerun this script then.
    $filters = (Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Enum\$($phone.InstanceId)").LowerFilters
    if ($filters) { "detaching lower filters: $filters"; [TetherSetup]::ClearLowerFilters($phone.InstanceId, $usbDeviceClass) }
    Set-ItemProperty $key -Name OriginalConfigurationValue -Value 3 -Type DWord   # index 3 = configuration 4

    $ether = { Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -match '^USB\\VID_05AC&PID_12A8&MI_02\\' } | Select-Object -First 1 }
    if (-not (& $ether)) {
        $hub = (Get-PnpDeviceProperty -InstanceId $phone.InstanceId -KeyName DEVPKEY_Device_Parent).Data
        $port = (Get-PnpDeviceProperty -InstanceId $phone.InstanceId -KeyName DEVPKEY_Device_Address).Data
        "cycling port $port on $hub"
        [TetherSetup]::CyclePort('\\?\' + $hub.Replace('\', '#') + '#{f18a0e88-c30c-11d0-8815-00a0c906bed8}', $port)
        foreach ($i in 1..20) { Start-Sleep 1; if (& $ether) { break } }
    }
    "config index now: $((Get-ItemProperty $key).OriginalConfigurationValue)"
    $dev = & $ether
    if (-not $dev) { throw 'Ethernet interface (MI_02) did not appear. Is Personal Hotspot on?' }

    $devKey = "HKLM:\SYSTEM\CurrentControlSet\Enum\$($dev.InstanceId)\Device Parameters"
    New-ItemProperty $devKey -Name DeviceInterfaceGUIDs -PropertyType MultiString -Value $InterfaceGuid -Force | Out-Null
    [TetherSetup]::ForceInstall($dev.InstanceId, "$env:windir\INF\winusb.inf", 'WinUsb')
    pnputil /restart-device $dev.InstanceId
    Start-Sleep 3
}
catch { "FAILED: $_"; $_.ScriptStackTrace }
finally {
    Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -match 'VID_05AC' } |
        Format-Table FriendlyName, Status, Problem, Service, InstanceId -AutoSize -Wrap | Out-String -Width 200
    Stop-Transcript | Out-Null
}
