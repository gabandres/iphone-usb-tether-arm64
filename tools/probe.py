"""Protocol probe: talks to the iPhone's AppleUSBEthernet interface through WinUSB.

Needs scripts\\install-driver.ps1 to have run (WinUSB bound, phone in USB configuration 4).
Prints the MAC address and carrier state, then dumps the first bulk-IN transfers.
Usage: python probe.py [--ncm] [--reads N]
"""
import argparse
import ctypes
import struct
import sys
from ctypes import wintypes as wt

IFACE_GUID = (0x8E2A3C61, 0x5B7D, 0x4F0A, (0x9C, 0x1E, 0x6D, 0x4B, 0x2A, 0x7F, 0x3E, 0x10))
IFNUM = 2
EP_IN, EP_OUT = 0x86, 0x05
CMD_GET_MACADDR, CMD_ENABLE_NCM, CMD_CARRIER_CHECK = 0x00, 0x04, 0x45


class GUID(ctypes.Structure):
    _fields_ = [("a", wt.DWORD), ("b", wt.WORD), ("c", wt.WORD), ("d", ctypes.c_ubyte * 8)]


class Setup(ctypes.Structure):
    _pack_ = 1
    _fields_ = [("RequestType", ctypes.c_ubyte), ("Request", ctypes.c_ubyte), ("Value", wt.WORD),
                ("Index", wt.WORD), ("Length", wt.WORD)]


k32 = ctypes.WinDLL("kernel32", use_last_error=True)
wu = ctypes.WinDLL("winusb", use_last_error=True)
cm = ctypes.WinDLL("cfgmgr32")
k32.CreateFileW.restype = wt.HANDLE
k32.CreateFileW.argtypes = [wt.LPCWSTR, wt.DWORD, wt.DWORD, ctypes.c_void_p, wt.DWORD, wt.DWORD, wt.HANDLE]
wu.WinUsb_Initialize.argtypes = [wt.HANDLE, ctypes.POINTER(ctypes.c_void_p)]
wu.WinUsb_ControlTransfer.argtypes = [ctypes.c_void_p, Setup, ctypes.c_void_p, wt.ULONG,
                                      ctypes.POINTER(wt.ULONG), ctypes.c_void_p]
wu.WinUsb_ReadPipe.argtypes = [ctypes.c_void_p, ctypes.c_ubyte, ctypes.c_void_p, wt.ULONG,
                               ctypes.POINTER(wt.ULONG), ctypes.c_void_p]
wu.WinUsb_SetCurrentAlternateSetting.argtypes = [ctypes.c_void_p, ctypes.c_ubyte]
wu.WinUsb_SetPipePolicy.argtypes = [ctypes.c_void_p, ctypes.c_ubyte, wt.ULONG, wt.ULONG, ctypes.c_void_p]


def fail(what):
    sys.exit(f"{what} failed: winerror {ctypes.get_last_error()}")


def device_path():
    g = GUID(IFACE_GUID[0], IFACE_GUID[1], IFACE_GUID[2], (ctypes.c_ubyte * 8)(*IFACE_GUID[3]))
    n = wt.ULONG(0)
    cm.CM_Get_Device_Interface_List_SizeW(ctypes.byref(n), ctypes.byref(g), None, 0)
    buf = ctypes.create_unicode_buffer(n.value)
    cm.CM_Get_Device_Interface_ListW(ctypes.byref(g), None, buf, n.value, 0)
    paths = [s for s in buf[:n.value].split("\0") if s]
    if not paths:
        sys.exit("iPhone Ethernet interface not found (driver not installed, or phone not in configuration 4)")
    return paths[0]


def control(h, direction_in, request, length=0):
    buf = ctypes.create_string_buffer(max(length, 1))
    n = wt.ULONG(0)
    setup = Setup(0xC0 if direction_in else 0x40, request, 0, IFNUM, length)
    if not wu.WinUsb_ControlTransfer(h, setup, buf, length, ctypes.byref(n), None):
        return None, ctypes.get_last_error()
    return buf.raw[:n.value], 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--ncm", action="store_true", help="send ENABLE_NCM before reading")
    ap.add_argument("--reads", type=int, default=5)
    ap.add_argument("--ncm-first", action="store_true", help="send ENABLE_NCM before selecting the alternate setting")
    ap.add_argument("--alt", type=int, default=1)
    a = ap.parse_args()

    path = device_path()
    print("device:", path)
    fh = k32.CreateFileW(path, 0xC0000000, 3, None, 3, 0x40000000, None)
    if fh in (None, wt.HANDLE(-1).value):
        fail("CreateFile")
    h = ctypes.c_void_p()
    if not wu.WinUsb_Initialize(fh, ctypes.byref(h)):
        fail("WinUsb_Initialize")

    mac, err = control(h, True, CMD_GET_MACADDR, 6)
    print("mac:", mac.hex(":") if mac else f"error {err}")
    if a.ncm and a.ncm_first:
        _, err = control(h, False, CMD_ENABLE_NCM)
        print("enable ncm (before alt):", "ok" if not err else f"error {err}")
    if not wu.WinUsb_SetCurrentAlternateSetting(h, a.alt):
        fail("SetCurrentAlternateSetting")
    if a.ncm and not a.ncm_first:
        _, err = control(h, False, CMD_ENABLE_NCM)
        print("enable ncm (after alt):", "ok" if not err else f"error {err}")
    car, err = control(h, True, CMD_CARRIER_CHECK, 3)
    print("carrier:", car.hex() if car else f"error {err}", "(04 = on)")

    timeout = wt.ULONG(3000)
    wu.WinUsb_SetPipePolicy(h, EP_IN, 3, 4, ctypes.byref(timeout))
    buf = ctypes.create_string_buffer(65536)
    for i in range(a.reads):
        n = wt.ULONG(0)
        if not wu.WinUsb_ReadPipe(h, EP_IN, buf, len(buf), ctypes.byref(n), None):
            print(f"read {i}: error {ctypes.get_last_error()}")
            continue
        d = buf.raw[:n.value]
        print(f"read {i}: {len(d)} bytes  {d[:48].hex()}")
        if d[:4] == b"NCMH":
            print("   NTH16:", struct.unpack_from("<4sHHHH", d, 0))


if __name__ == "__main__":
    main()
