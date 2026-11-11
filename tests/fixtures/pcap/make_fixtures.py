#!/usr/bin/env python3
"""Generates the PCAP/PCAPNG fixtures for tests/AcsTool.PacketCapture.Tests/FileFormatTests.cs (PT1.2).

Independent of the C# code: plain `struct` packing straight from the formats
(PCAP: draft-ietf-opsawg-pcap; PCAPNG: draft-ietf-opsawg-pcapng). Re-run from this folder:

    python3 make_fixtures.py            # stdlib-only fixtures
    python3 make_fixtures.py --scapy    # also the scapy-*.pcap* files (needs `pip install scapy`)

The expected values are duplicated in FileFormatTests.cs (Fixture* constants); change both together.
"""
import struct
import sys
import os

HERE = os.path.dirname(os.path.abspath(__file__))


def payload(n, seed):
    return bytes((seed + k) & 0xFF for k in range(n))


def pad4(b):
    return b + b"\x00" * ((-len(b)) % 4)


# ---------------------------------------------------------------- PCAP
# (sec, usec, nsec, incl_len, orig_len, seed)
PCAP_FRAMES = [
    (1762000000, 123456, 123456789, 60, 60, 1),
    (1762000001, 999999, 999999999, 54, 1514, 2),
    (1762000002, 0, 5, 1, 1, 3),
]


def pcap(endian, nano, linktype=1, snaplen=65535):
    magic = 0xA1B23C4D if nano else 0xA1B2C3D4
    out = struct.pack(endian + "IHHiIII", magic, 2, 4, 0, 0, snaplen, linktype)
    for sec, usec, nsec, incl, orig, seed in PCAP_FRAMES:
        out += struct.pack(endian + "IIII", sec, nsec if nano else usec, incl, orig)
        out += payload(incl, seed)
    return out


# ---------------------------------------------------------------- PCAPNG
def opt(e, code, value):
    return struct.pack(e + "HH", code, len(value)) + pad4(value)


def opts(e, *items):
    if not items:
        return b""
    return b"".join(opt(e, c, v) for c, v in items) + struct.pack(e + "HH", 0, 0)


def block(e, btype, body):
    body = pad4(body)
    total = 12 + len(body)
    return struct.pack(e + "II", btype, total) + body + struct.pack(e + "I", total)


def shb(e, *options):
    body = struct.pack(e + "IHHq", 0x1A2B3C4D, 1, 0, -1) + opts(e, *options)
    return block(e, 0x0A0D0D0A, body)


def idb(e, linktype, snaplen, *options):
    return block(e, 1, struct.pack(e + "HHI", linktype, 0, snaplen) + opts(e, *options))


def epb(e, ifid, ts, data, orig, *options):
    body = struct.pack(e + "IIIII", ifid, ts >> 32, ts & 0xFFFFFFFF, len(data), orig) + pad4(data) + opts(e, *options)
    return block(e, 6, body)


def spb(e, data, orig):
    return block(e, 3, struct.pack(e + "I", orig) + pad4(data))


def opb(e, ifid, drops, ts, data, orig, *options):
    body = struct.pack(e + "HHIIII", ifid, drops, ts >> 32, ts & 0xFFFFFFFF, len(data), orig) + pad4(data) + opts(e, *options)
    return block(e, 2, body)


def multi_section():
    le, be = "<", ">"
    out = b""
    # Section 0, little-endian: two interfaces, EPB/SPB/OPB, unknown + ISB + NRB blocks (skipped).
    out += shb(le, (1, b"section zero"), (2, b"fixture-hw"), (3, b"fixture-os"), (4, b"fixture-app 1.0"))
    out += idb(le, 1, 0, (2, b"eth0"), (3, b"Ethernet adapter"))                              # if 0: Ethernet, tsresol default 6
    out += idb(le, 0, 262144, (2, b"lo"), (9, b"\x09"), (11, b"\x00tcp port 7547"))            # if 1: Null, ns
    out += epb(le, 0, 1762000000 * 10**6 + 250000, payload(60, 10), 60, (1, b"first comment"))
    out += block(le, 0x00000BAD, b"unknown block body")
    out += epb(le, 1, 1762000000 * 10**9 + 123456789, payload(44, 11), 44, (1, b"line one"), (1, b"line two"), (2, struct.pack(le + "I", 1)))
    out += block(le, 5, struct.pack(le + "III", 0, 0, 0))                                   # ISB, skipped
    out += block(le, 4, struct.pack(le + "HH", 0, 0))                                        # NRB (end record only), skipped
    out += spb(le, payload(33, 12), 33)                                                      # SPB on if 0 (snaplen 0 = unlimited)
    out += opb(le, 1, 7, 1762000001 * 10**9 + 1, payload(20, 13), 40, (1, b"legacy"))
    # Section 1, big-endian: interface ids restart at 0; tsresol 2^-20; if_tsoffset 100 s.
    out += shb(be, (4, b"fixture-be"))
    out += idb(be, 113, 65535, (2, b"any"), (9, b"\x94"), (14, struct.pack(be + "q", 100)))  # if 2: Linux SLL
    out += epb(be, 0, (1762000000 << 20) + (1 << 19), payload(28, 14), 28)                  # +0.5 s
    # Section 2, little-endian again, 100 ns resolution, Raw IP.
    out += shb(le)
    out += idb(le, 101, 0, (9, b"\x07"))                                                     # if 3: Raw
    out += epb(le, 0, 1762000003 * 10**7 + 1234567, payload(20, 15), 20)
    return out


# ---------------------------------------------------------------- golden writer output
# What PcapngWriter / PcapWriter must emit for the inputs in FileFormatTests.GoldenWriter*.
def golden_pcapng():
    e = "<"
    out = shb(e, (4, b"AcsTool test"), (3, b"test-os"))
    out += idb(e, 1, 65535, (2, b"eth0"), (3, b"LAN"), (9, b"\x07"), (11, b"\x00tcp port 7547"))
    out += idb(e, 0, 262144, (9, b"\x07"))
    ts0 = 1762000000 * 10**7 + 1234567          # 100 ns units since the Unix epoch
    out += epb(e, 0, ts0, payload(5, 20), 5, (1, "AcsTool session=00000000-0000-0000-0000-000000000001 exch=1 leg=CpeToProxy".encode()))
    out += epb(e, 1, ts0 + 1, payload(8, 21), 100)
    return out


def golden_pcap():
    e = "<"
    out = struct.pack(e + "IHHiIII", 0xA1B23C4D, 2, 4, 0, 0, 65535, 1)
    out += struct.pack(e + "IIII", 1762000000, 123456700, 5, 5) + payload(5, 20)
    out += struct.pack(e + "IIII", 1762000000, 123456800, 8, 100) + payload(8, 21)
    return out


def write(name, data):
    with open(os.path.join(HERE, name), "wb") as f:
        f.write(data)


def scapy_files():
    from scapy.all import Ether, IP, TCP, Raw, wrpcap, PcapNgWriter  # noqa: F401
    pkts = []
    for i in range(3):
        p = Ether(src="02:00:00:00:00:01", dst="02:00:00:00:00:02") / IP(src="192.0.2.1", dst="192.0.2.2") / \
            TCP(sport=40000 + i, dport=7547, flags="PA", seq=1000 + i) / Raw(b"POST / HTTP/1.1\r\n")
        p.time = 1762000000 + i + 0.5
        pkts.append(p)
    wrpcap(os.path.join(HERE, "scapy-us.pcap"), pkts)
    wrpcap(os.path.join(HERE, "scapy-ns.pcap"), pkts, nano=True)
    pkts[1].comment = b"scapy comment"
    w = PcapNgWriter(os.path.join(HERE, "scapy.pcapng"))
    w.write(pkts)
    w.close()


if __name__ == "__main__":
    write("us-le.pcap", pcap("<", False))
    write("us-be.pcap", pcap(">", False))
    write("ns-le.pcap", pcap("<", True))
    write("ns-be.pcap", pcap(">", True))
    write("multi-section.pcapng", multi_section())
    write("golden-writer.pcapng", golden_pcapng())
    write("golden-writer.pcap", golden_pcap())
    if "--scapy" in sys.argv:
        scapy_files()
