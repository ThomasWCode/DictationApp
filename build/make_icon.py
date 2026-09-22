"""Generate build/app.ico (a microphone glyph) with the Python standard library only.

Run: python build/make_icon.py
The ICO container holds PNG-encoded images (supported since Windows Vista),
so no third-party imaging library is needed. Sizes: 16, 24, 32, 48, 256.
"""
from __future__ import annotations

import math
import struct
import zlib
from pathlib import Path

BG = (0x1F, 0x6F, 0xEB)      # accent blue
FG = (0xFF, 0xFF, 0xFF)      # white glyph


def _sample(x: float, y: float, size: float) -> tuple[int, int, int, int]:
    """Return RGBA for pixel centre (x, y) on a canvas of `size` pixels."""
    cx, cy = size / 2, size / 2
    r = size / 2 - 0.5
    d = math.hypot(x - cx, y - cy)
    if d > r:
        return (0, 0, 0, 0)
    # anti-alias the disc edge
    edge = min(1.0, max(0.0, r - d + 0.5))
    # microphone capsule: rounded rectangle
    cw, ch = size * 0.22, size * 0.42
    top = size * 0.20
    rx0, rx1 = cx - cw / 2, cx + cw / 2
    ry0, ry1 = top, top + ch
    rad = cw / 2
    inside_capsule = False
    if rx0 <= x <= rx1 and ry0 + rad <= y <= ry1 - rad:
        inside_capsule = True
    elif math.hypot(x - cx, y - (ry0 + rad)) <= rad or math.hypot(x - cx, y - (ry1 - rad)) <= rad:
        inside_capsule = True
    # cradle arc: ring segment around the capsule
    arc_r_outer, arc_r_inner = size * 0.24, size * 0.19
    arc_cy = ry1 - rad
    da = math.hypot(x - cx, y - arc_cy)
    inside_arc = arc_r_inner <= da <= arc_r_outer and y >= arc_cy
    # stem and base
    stem = abs(x - cx) <= size * 0.03 and arc_cy + arc_r_outer - 1 <= y <= size * 0.82
    base = abs(x - cx) <= size * 0.16 and size * 0.78 <= y <= size * 0.84
    if inside_capsule or inside_arc or stem or base:
        return (*FG, int(255 * edge))
    return (*BG, int(255 * edge))


def render(size: int) -> bytes:
    rows = []
    ss = 3  # supersampling for smoother edges
    for j in range(size):
        row = bytearray([0])  # PNG filter type 0
        for i in range(size):
            acc = [0, 0, 0, 0]
            for sj in range(ss):
                for si in range(ss):
                    px = _sample(i + (si + 0.5) / ss, j + (sj + 0.5) / ss, size)
                    for k in range(4):
                        acc[k] += px[k]
            row.extend(v // (ss * ss) for v in acc)
        rows.append(bytes(row))
    raw = b"".join(rows)

    def chunk(tag: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(raw, 9))
    png += chunk(b"IEND", b"")
    return png


def main() -> None:
    sizes = [16, 24, 32, 48, 256]
    images = [(s, render(s)) for s in sizes]
    header = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    entries, blobs = b"", b""
    for s, data in images:
        entries += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(data), offset)
        blobs += data
        offset += len(data)
    out = Path(__file__).with_name("app.ico")
    out.write_bytes(header + entries + blobs)
    print(f"wrote {out} ({out.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
