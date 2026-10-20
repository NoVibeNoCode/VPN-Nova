#!/usr/bin/env python3
"""Packs PNGs from a make-icon.swift .iconset into a Windows .ico (PNG-compressed entries, Vista+).
Usage: make-ico.py <icon.iconset> <out.ico>"""
import struct
import sys
from pathlib import Path

src, out = Path(sys.argv[1]), Path(sys.argv[2])
# size → file; 48 px is rendered as 32@2x scaled by Windows from the 64 entry.
files = {16: "icon_16x16.png", 32: "icon_32x32.png", 64: "icon_32x32@2x.png", 128: "icon_128x128.png", 256: "icon_256x256.png"}
images = [(size, (src / name).read_bytes()) for size, name in files.items()]

header = struct.pack("<HHH", 0, 1, len(images))
offset = 6 + 16 * len(images)
entries, blobs = b"", b""
for size, data in images:
    dim = 0 if size >= 256 else size
    entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset + len(blobs))
    blobs += data
out.write_bytes(header + entries + blobs)
print(f"✓ {out} ({', '.join(str(s) for s, _ in images)} px)")
