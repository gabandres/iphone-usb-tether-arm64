# iphone-usb-tether-arm64

iPhone USB tethering (Personal Hotspot over the cable) on **Windows on ARM64**, where Apple ships
no tethering driver (`netaapl64.sys` exists for x64 only).

It is a user-mode bridge, not a kernel driver:

```
iPhone "AppleUSBEthernet" interface  <-- WinUSB -->  iphone-tether.exe  <-->  TAP-Windows adapter "iPhone USB"
```

Both ends carry plain Ethernet frames, so Windows does DHCP, ARP and IPv6 on the adapter itself.
Nothing here needs test-signing mode or a certificate: WinUSB is Windows' own driver, and the
TAP-Windows ARM64 driver is signed by Microsoft.

Built and tested on one machine (Snapdragon, Windows 11 26300) with one iPhone. Treat it as a
working experiment.

## What it measured

On a cellular link of about 20–25 Mbps, download speed over USB and over the 5 GHz hotspot was the
same within noise: the cellular link is the limit. The difference is local latency: ping to the
phone was about 1 ms (max 6) over USB against about 16 ms (max 38) over Wi-Fi.

## Requirements

- Windows 11 on ARM64, .NET 10 SDK
- [Apple Devices](https://apps.microsoft.com/detail/9np83lwlpz9k) from the Microsoft Store (native
  ARM64). It pairs the phone ("Trust this computer") and installs Apple's USB driver.
- iPhone with Personal Hotspot on, connected by cable, trusted

## Setup

1. Install Apple Devices, open it once, plug in the phone and trust the PC.
2. Elevated, with the phone connected: `scripts\install-driver.ps1`
3. Elevated, once: `scripts\install-tap.ps1`
4. `dotnet run -c Release --project src\IphoneTether -- -v` to try it, or
   `scripts\install-autostart.ps1` to run it hidden at every logon.

The "iPhone USB" adapter goes up when the phone has carrier and gets its address from the phone.

## What the scripts change

`install-driver.ps1`
- Detaches Apple's `AppleLowerFilter` from the phone's USB device. That filter re-selects USB
  configuration 3 (photos + mux, no Ethernet) on every plug-in.
- Sets `OriginalConfigurationValue` to 3, so Windows selects configuration 4, the one containing
  the Ethernet interface, and cycles the USB port.
- Installs `winusb.inf` on the Ethernet interface (`USB\VID_05AC&PID_12A8&MI_02`) and gives it the
  device interface GUID the bridge opens.

These are per-device settings: a different phone, or a reinstall of Apple's driver, needs the script
run again. The effect of detaching the filter on photo import and sync was not tested.

`install-tap.ps1`
- Downloads TAP-Windows 9.27.0 from the OpenVPN release (pinned SHA-256), installs the ARM64 driver,
  names the adapter "iPhone USB" and gives it a low interface metric so it is preferred over Wi-Fi.

## Protocol

The Ethernet interface is vendor class `ff/fd/01`; the protocol is the one in Linux's `ipheth`
driver:

- Vendor control requests on the interface: `0x00` get host MAC, `0x45` carrier check (first byte
  `0x04` = on), `0x04` enable NCM framing.
- Alternate setting 1 enables the bulk endpoints (`0x86` in, `0x05` out).
- Phone to host: NCM transfer blocks (`NCMH`/`NCM0`), or with `--legacy` one frame per transfer
  behind two padding bytes. Host to phone: one bare Ethernet frame per transfer.

`tools\probe.py` dumps the MAC, carrier state and first transfers for debugging.
