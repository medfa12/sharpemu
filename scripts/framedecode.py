#!/usr/bin/env python3
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
# Extract [LOADER][FRAMEDUMP] RGB frames from a boot log -> PNG (pure python, no PIL).
# Usage: framedecode.py <logfile> [outdir]
import sys, re, base64, zlib, struct, os

def write_png(path, w, h, rgb):
    def chunk(typ, data):
        c = typ + data
        return struct.pack(">I", len(data)) + c + struct.pack(">I", zlib.crc32(c) & 0xffffffff)
    raw = bytearray()
    stride = w * 3
    for y in range(h):
        raw.append(0)  # filter type none
        raw += rgb[y*stride:(y+1)*stride]
    sig = b"\x89PNG\r\n\x1a\n"
    ihdr = struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)
    with open(path, "wb") as f:
        f.write(sig)
        f.write(chunk(b"IHDR", ihdr))
        f.write(chunk(b"IDAT", zlib.compress(bytes(raw), 9)))
        f.write(chunk(b"IEND", b""))

def main():
    log = sys.argv[1]
    outdir = sys.argv[2] if len(sys.argv) > 2 else os.path.dirname(os.path.abspath(log))
    tag = os.path.splitext(os.path.basename(log))[0]
    pat = re.compile(r"\[FRAMEDUMP\] frame=(\d+) w=(\d+) h=(\d+) fmt=RGB nonblack=(\d+) b64=(\S+)")
    gpat = re.compile(r"\[GIMGDUMP\] addr=0x([0-9A-Fa-f]+) w=(\d+) h=(\d+) fmt=RGB b64=(\S+)")
    n = 0
    with open(log, "r", errors="replace") as f:
        for line in f:
            g = gpat.search(line)
            if g:
                addr, w, h, b64 = g.group(1), int(g.group(2)), int(g.group(3)), g.group(4)
                try:
                    rgb = base64.b64decode(b64)
                except Exception as e:
                    print(f"gimg {addr}: b64 fail {e}"); continue
                if len(rgb) < w*h*3:
                    print(f"gimg {addr}: short {len(rgb)}<{w*h*3}"); continue
                nz = sum(1 for i in range(0, w*h*3, 3) if rgb[i] or rgb[i+1] or rgb[i+2])
                out = os.path.join(outdir, f"{tag}-img{addr}.png")
                write_png(out, w, h, rgb[:w*h*3])
                print(f"gimg 0x{addr} {w}x{h} nonblack={nz} -> {out}")
                n += 1
                continue
            m = pat.search(line)
            if not m:
                continue
            frame, w, h, nonblack, b64 = m.group(1), int(m.group(2)), int(m.group(3)), int(m.group(4)), m.group(5)
            try:
                rgb = base64.b64decode(b64)
            except Exception as e:
                print(f"frame {frame}: b64 decode failed: {e}"); continue
            if len(rgb) < w*h*3:
                print(f"frame {frame}: short rgb {len(rgb)} < {w*h*3}"); continue
            out = os.path.join(outdir, f"{tag}-f{frame}.png")
            write_png(out, w, h, rgb[:w*h*3])
            print(f"frame {frame} {w}x{h} nonblack={nonblack} -> {out}")
            n += 1
    if n == 0:
        print("no FRAMEDUMP frames found in log")

main()
