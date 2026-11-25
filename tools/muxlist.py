"""Asks Apple's usbmuxd (Apple Mobile Device process, 127.0.0.1:27015) which devices it sees,
and whether it holds a pairing record for each. Read-only."""
import plistlib
import socket
import struct


def call(msg):
    s = socket.create_connection(("127.0.0.1", 27015), timeout=5)
    body = plistlib.dumps({"ClientVersionString": "muxlist", "ProgName": "muxlist", **msg})
    s.sendall(struct.pack("<IIII", 16 + len(body), 1, 8, 1) + body)
    head = b""
    while len(head) < 16:
        head += s.recv(16 - len(head))
    size = struct.unpack("<I", head[:4])[0]
    data = b""
    while len(data) < size - 16:
        data += s.recv(size - 16 - len(data))
    s.close()
    return plistlib.loads(data)


devices = call({"MessageType": "ListDevices"}).get("DeviceList", [])
print("devices seen by usbmuxd:", len(devices))
for d in devices:
    p = d["Properties"]
    udid = p["SerialNumber"]
    print(" ", p.get("ConnectionType"), "id", d["DeviceID"], "udid …" + udid[-6:], "product", p.get("ProductID"))
    rec = call({"MessageType": "ReadPairRecord", "PairRecordID": udid})
    print("   pair record held:", "PairRecordData" in rec)
