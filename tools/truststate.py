"""Asks the phone (lockdownd, through Apple's usbmuxd) whether it accepts this PC's pairing record.
Prints the phone's answer to StartSession: a SessionID means trusted; otherwise the error name
(PasswordProtected = phone locked, PairingDialogResponsePending = Trust prompt showing,
InvalidHostID / UserDeniedPairing = not paired). Prints no keys."""
import plistlib
import socket
import struct


def recvn(s, n):
    data = b""
    while len(data) < n:
        chunk = s.recv(n - len(data))
        if not chunk:
            raise ConnectionError("closed")
        data += chunk
    return data


def mux(s, msg):
    body = plistlib.dumps({"ClientVersionString": "truststate", "ProgName": "truststate", **msg})
    s.sendall(struct.pack("<IIII", 16 + len(body), 1, 8, 1) + body)
    size = struct.unpack("<I", recvn(s, 16)[:4])[0]
    return plistlib.loads(recvn(s, size - 16))


def lockdown(s, msg):
    body = plistlib.dumps({"Label": "truststate", **msg})
    s.sendall(struct.pack(">I", len(body)) + body)
    return plistlib.loads(recvn(s, struct.unpack(">I", recvn(s, 4))[0]))


def connect():
    return socket.create_connection(("127.0.0.1", 27015), timeout=8)


with connect() as s:
    devices = mux(s, {"MessageType": "ListDevices"}).get("DeviceList", [])
for d in devices:
    udid = d["Properties"]["SerialNumber"]
    with connect() as s:
        record = mux(s, {"MessageType": "ReadPairRecord", "PairRecordID": udid}).get("PairRecordData")
    with connect() as s:
        r = mux(s, {"MessageType": "Connect", "DeviceID": d["DeviceID"], "PortNumber": socket.htons(62078)})
        if r.get("Number") != 0:
            print("…" + udid[-6:], "cannot reach lockdownd:", r)
            continue
        print("…" + udid[-6:], "lockdown type:", lockdown(s, {"Request": "QueryType"}).get("Type"))
        locked = lockdown(s, {"Request": "GetValue", "Key": "PasswordProtected"})
        print("   passcode set:", locked.get("Value", locked.get("Error")))
        if not record:
            print("   no pairing record on this PC")
            continue
        rec = plistlib.loads(record)
        a = lockdown(s, {"Request": "StartSession", "HostID": rec["HostID"], "SystemBUID": rec["SystemBUID"]})
        print("   StartSession:", "trusted (session opened)" if "SessionID" in a else a.get("Error"))
