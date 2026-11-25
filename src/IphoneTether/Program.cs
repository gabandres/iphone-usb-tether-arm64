// Bridges the iPhone's "AppleUSBEthernet" USB interface (opened through WinUSB) to a TAP-Windows
// virtual Ethernet adapter. Both sides carry plain Ethernet frames, so Windows does DHCP/ARP/IPv6.
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace IphoneTether;

static class Program
{
    const string AdapterName = "iPhone USB";

    static int Main(string[] args)
    {
        bool ncm = !args.Contains("--legacy");   // NCM batches frames per USB transfer
        bool verbose = args.Contains("-v");
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; Environment.Exit(0); };
        using var tap = Tap.Open(AdapterName);
        Log($"adapter '{AdapterName}' open");
        while (true)
        {
            try
            {
                using var phone = Phone.Open(ncm);
                Log($"phone connected, host MAC {Convert.ToHexString(phone.Mac)}, {(ncm ? "NCM" : "legacy")} framing");
                Run(phone, tap, verbose);
            }
            catch (PhoneNotFoundException) { }
            catch (Exception e) { Log($"link down: {e.Message}"); }
            tap.SetConnected(false);
            Thread.Sleep(2000);
        }
    }

    static void Run(Phone phone, Tap tap, bool verbose)
    {
        using var stop = new CancellationTokenSource();
        Exception? failure = null;
        long rxFrames = 0, txFrames = 0, rxBytes = 0, txBytes = 0;
        void Guard(Action body)
        {
            try { body(); }
            catch (Exception e) { failure ??= e; stop.Cancel(); }
        }

        var rx = new Thread(() => Guard(() =>
        {
            var buf = new byte[65536];
            while (!stop.IsCancellationRequested)
            {
                int n = phone.Read(buf);
                Diag.Transfers++;
                if (n % 512 == 0) Diag.Aligned++;
                if (n > Diag.MaxTransfer) Diag.MaxTransfer = n;
                int got = 0;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                foreach (var (offset, length) in phone.Frames(buf, n))
                {
                    tap.Write(buf.AsSpan(offset, length));
                    rxFrames++; rxBytes += length; got++;
                }
                Diag.Note(ref Diag.MaxTapWriteMs, t0);
                if (got == 0) Diag.Empty++;
            }
        })) { IsBackground = true, Name = "usb->tap" };

        var tx = new Thread(() => Guard(() =>
        {
            var buf = new byte[65536];
            while (!stop.IsCancellationRequested)
            {
                int n = tap.Read(buf);
                if (n < 14) continue;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                phone.Write(buf.AsSpan(0, n));
                Diag.Note(ref Diag.MaxUsbWriteMs, t0);
                txFrames++; txBytes += n;
            }
        })) { IsBackground = true, Name = "tap->usb" };

        if (!phone.Carrier())
        {
            Log("waiting for carrier (is Personal Hotspot on?)");
            while (!phone.Carrier()) Thread.Sleep(1000);
        }
        tap.SetConnected(true);
        Log("carrier on");
        rx.Start(); tx.Start();
        int tick = 0;
        while (!stop.IsCancellationRequested)
        {
            if (!phone.Carrier()) { failure ??= new IOException("carrier off"); break; }
            if (verbose && ++tick % 5 == 0)
            {
                Log($"rx {rxFrames} frames {rxBytes >> 10} KiB, tx {txFrames} frames {txBytes >> 10} KiB | " + Diag.Take());
            }
            stop.Token.WaitHandle.WaitOne(1000);
        }
        stop.Cancel();
        phone.Abort();
        tap.CancelIo();
        rx.Join(3000); tx.Join(3000);
        throw failure ?? new IOException("stopped");
    }

    public static void Log(string s) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {s}");
}

sealed class PhoneNotFoundException : Exception;

static class Diag
{
    public static long Transfers, Aligned, Empty, Trailing, MaxTransfer;
    public static double MaxTapWriteMs, MaxUsbWriteMs;
    public static void Note(ref double max, long t0)
    {
        double ms = System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        if (ms > max) max = ms;
    }
    public static string Take()
    {
        string s = $"transfers {Transfers} aligned512 {Aligned} empty {Empty} trailing {Trailing} maxXfer {MaxTransfer} maxTapWrite {MaxTapWriteMs:F1}ms maxUsbWrite {MaxUsbWriteMs:F1}ms";
        Transfers = Aligned = Empty = Trailing = MaxTransfer = 0; MaxTapWriteMs = MaxUsbWriteMs = 0;
        return s;
    }
}

/// <summary>The iPhone's Ethernet interface (protocol as in Linux's ipheth driver).</summary>
sealed unsafe class Phone : IDisposable
{
    static readonly Guid InterfaceGuid = new("8E2A3C61-5B7D-4F0A-9C1E-6D4B2A7F3E10");
    const ushort InterfaceNumber = 2;
    const byte EpIn = 0x86, EpOut = 0x05;
    const byte CmdGetMac = 0x00, CmdEnableNcm = 0x04, CmdCarrierCheck = 0x45;

    readonly SafeFileHandle _file;
    readonly IntPtr _usb;
    readonly bool _ncm;
    public byte[] Mac { get; }

    Phone(SafeFileHandle file, IntPtr usb, bool ncm)
    {
        _file = file; _usb = usb; _ncm = ncm;
        Mac = Control(true, CmdGetMac, 6);
        if (ncm) Control(false, CmdEnableNcm, 0);
        Check(WinUsb_SetCurrentAlternateSetting(_usb, 1), "select alternate setting");
        byte on = 1;
        WinUsb_SetPipePolicy(_usb, EpOut, 0x01 /* SHORT_PACKET_TERMINATE */, 1, &on);
    }

    public static Phone Open(bool ncm)
    {
        string path = FindPath() ?? throw new PhoneNotFoundException();
        var file = CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000 /* overlapped */, IntPtr.Zero);
        if (file.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "open phone");
        if (!WinUsb_Initialize(file, out IntPtr usb))
        {
            int err = Marshal.GetLastWin32Error();
            file.Dispose();
            throw new Win32Exception(err, "WinUsb_Initialize");
        }
        try { return new Phone(file, usb, ncm); }
        catch { WinUsb_Free(usb); file.Dispose(); throw; }
    }

    static string? FindPath()
    {
        Guid g = InterfaceGuid;
        if (CM_Get_Device_Interface_List_SizeW(out int len, ref g, null, 0) != 0 || len <= 1) return null;
        var buf = new char[len];
        if (CM_Get_Device_Interface_ListW(ref g, null, buf, len, 0) != 0) return null;
        string first = new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return first.Length > 0 ? first : null;
    }

    byte[] Control(bool deviceToHost, byte request, ushort length)
    {
        var setup = new SetupPacket
        {
            RequestType = (byte)(deviceToHost ? 0xC0 : 0x40), Request = request, Index = InterfaceNumber, Length = length,
        };
        var data = new byte[Math.Max((int)length, 1)];
        fixed (byte* p = data)
            Check(WinUsb_ControlTransfer(_usb, setup, p, length, out int n, IntPtr.Zero), $"control request 0x{request:x2}");
        return data[..length];
    }

    public bool Carrier() => Control(true, CmdCarrierCheck, 3)[0] == 0x04;

    public int Read(byte[] buf)
    {
        int n;
        fixed (byte* p = buf)
            Check(WinUsb_ReadPipe(_usb, EpIn, p, buf.Length, out n, IntPtr.Zero), "read");
        return n;
    }

    /// <summary>Ethernet frames inside one bulk-IN transfer, as (offset, length).</summary>
    public IEnumerable<(int, int)> Frames(byte[] buf, int n)
    {
        if (!_ncm)
        {
            // Legacy framing: two bytes of padding, then one frame.
            if (n > 2 + 14) yield return (2, n - 2);
            yield break;
        }
        // NCM: NTH16 header, then a chain of NDP16 tables listing datagram offset/length pairs.
        if (n < 12 || BitConverter.ToUInt32(buf, 0) != 0x484D434E /* "NCMH" */) yield break;
        if (BitConverter.ToUInt16(buf, 8) < n) Diag.Trailing++;
        int ndp = BitConverter.ToUInt16(buf, 10);
        while (ndp != 0 && ndp + 8 <= n && BitConverter.ToUInt32(buf, ndp) == 0x304D434E /* "NCM0" */)
        {
            int ndpLen = BitConverter.ToUInt16(buf, ndp + 4);
            for (int e = ndp + 8; e + 4 <= ndp + ndpLen && e + 4 <= n; e += 4)
            {
                int off = BitConverter.ToUInt16(buf, e), len = BitConverter.ToUInt16(buf, e + 2);
                if (off == 0 || len == 0) break;
                if (off + len <= n && len >= 14) yield return (off, len);
            }
            ndp = BitConverter.ToUInt16(buf, ndp + 6);
        }
    }

    public void Write(ReadOnlySpan<byte> frame)
    {
        fixed (byte* p = frame)
            Check(WinUsb_WritePipe(_usb, EpOut, p, frame.Length, out _, IntPtr.Zero), "write");
    }

    public void Abort()
    {
        WinUsb_AbortPipe(_usb, EpIn);
        WinUsb_AbortPipe(_usb, EpOut);
    }

    public void Dispose() { WinUsb_Free(_usb); _file.Dispose(); }

    static void Check(bool ok, string what)
    {
        if (!ok) throw new IOException($"USB {what} failed (error {Marshal.GetLastWin32Error()})");
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct SetupPacket { public byte RequestType, Request; public ushort Value, Index, Length; }

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string name, uint access, int share, IntPtr sa, int disposition, int flags, IntPtr template);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_Initialize(SafeFileHandle file, out IntPtr usb);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_Free(IntPtr usb);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_SetCurrentAlternateSetting(IntPtr usb, byte setting);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_SetPipePolicy(IntPtr usb, byte pipe, int policy, int length, void* value);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_ControlTransfer(IntPtr usb, SetupPacket setup, byte* buf, int length, out int transferred, IntPtr overlapped);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_ReadPipe(IntPtr usb, byte pipe, byte* buf, int length, out int transferred, IntPtr overlapped);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_WritePipe(IntPtr usb, byte pipe, byte* buf, int length, out int transferred, IntPtr overlapped);
    [DllImport("winusb", SetLastError = true)] static extern bool WinUsb_AbortPipe(IntPtr usb, byte pipe);
    [DllImport("cfgmgr32", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_Interface_List_SizeW(out int length, ref Guid guid, string? deviceId, int flags);
    [DllImport("cfgmgr32", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_Interface_ListW(ref Guid guid, string? deviceId, char[] buffer, int length, int flags);
}

/// <summary>A TAP-Windows (tap0901) adapter: every read or write is one Ethernet frame.</summary>
sealed unsafe class Tap : IDisposable
{
    const string NetClass = @"SYSTEM\CurrentControlSet\Control\Class\{4D36E972-E325-11CE-BFC1-08002BE10318}";
    const string NetNames = @"SYSTEM\CurrentControlSet\Control\Network\{4D36E972-E325-11CE-BFC1-08002BE10318}";
    const uint IoctlSetMediaStatus = 0x00220018;   // TAP_WIN_IOCTL_SET_MEDIA_STATUS

    // Plain Win32 overlapped I/O: one event per direction so a read and a write can be in flight together.
    readonly SafeFileHandle _h;
    readonly ManualResetEvent _readDone = new(false), _writeDone = new(false), _ioctlDone = new(false);
    Tap(SafeFileHandle h) => _h = h;

    public static Tap Open(string adapterName)
    {
        string id = FindInstanceId(adapterName)
            ?? throw new InvalidOperationException($"No TAP adapter named '{adapterName}'. Run scripts\\install-tap.ps1 first.");
        var h = CreateFileW($@"\\.\Global\{id}.tap", 0xC0000000, 0, IntPtr.Zero, 3, 0x40000000 /* overlapped */, IntPtr.Zero);
        if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "open TAP adapter (is another bridge running?)");
        return new Tap(h);
    }

    static string? FindInstanceId(string adapterName)
    {
        using var cls = Registry.LocalMachine.OpenSubKey(NetClass)!;
        foreach (string sub in cls.GetSubKeyNames())
        {
            RegistryKey? k;
            try { k = cls.OpenSubKey(sub); } catch (System.Security.SecurityException) { continue; }
            using (k)
            {
                if (k?.GetValue("ComponentId") as string != "tap0901") continue;
                if (k.GetValue("NetCfgInstanceId") is not string id) continue;
                using var conn = Registry.LocalMachine.OpenSubKey($@"{NetNames}\{id}\Connection");
                if (string.Equals(conn?.GetValue("Name") as string, adapterName, StringComparison.OrdinalIgnoreCase)) return id;
            }
        }
        return null;
    }

    int Finish(bool ok, NativeOverlapped* ov, string what)
    {
        int n = 0;
        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            if (err != 997 /* ERROR_IO_PENDING */) throw new IOException($"TAP {what} failed (error {err})");
        }
        if (!GetOverlappedResult(_h, ov, out n, true)) throw new IOException($"TAP {what} failed (error {Marshal.GetLastWin32Error()})");
        return n;
    }

    public void SetConnected(bool connected)
    {
        int value = connected ? 1 : 0;
        lock (_ioctlDone)
        {
            _ioctlDone.Reset();
            var ov = new NativeOverlapped { EventHandle = _ioctlDone.SafeWaitHandle.DangerousGetHandle() };
            Finish(DeviceIoControl(_h, IoctlSetMediaStatus, &value, 4, &value, 4, out _, &ov), &ov, "set media status");
        }
    }

    public int Read(byte[] buf)
    {
        _readDone.Reset();
        var ov = new NativeOverlapped { EventHandle = _readDone.SafeWaitHandle.DangerousGetHandle() };
        fixed (byte* p = buf) return Finish(ReadFile(_h, p, buf.Length, out _, &ov), &ov, "read");
    }

    public void Write(ReadOnlySpan<byte> frame)
    {
        _writeDone.Reset();
        var ov = new NativeOverlapped { EventHandle = _writeDone.SafeWaitHandle.DangerousGetHandle() };
        fixed (byte* p = frame) Finish(WriteFile(_h, p, frame.Length, out _, &ov), &ov, "write");
    }

    public void CancelIo() => CancelIoEx(_h, IntPtr.Zero);
    public void Dispose() => _h.Dispose();

    [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string name, uint access, int share, IntPtr sa, int disposition, int flags, IntPtr template);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool ReadFile(SafeFileHandle h, byte* buf, int length, out int read, NativeOverlapped* overlapped);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool WriteFile(SafeFileHandle h, byte* buf, int length, out int written, NativeOverlapped* overlapped);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, void* inBuf, int inLen, void* outBuf, int outLen, out int returned, NativeOverlapped* overlapped);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool GetOverlappedResult(SafeFileHandle h, NativeOverlapped* overlapped, out int transferred, bool wait);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);
}
