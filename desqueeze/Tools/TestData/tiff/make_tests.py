"""
Makes the TIFF test files the apps' TIFF check (TiffConformance, run by the EditMode test TiffTests) is held to:

  test-*.tif          files to read: every layout the apps read (see the cases below), each with
  expect-*.png        the picture it must decode to, already upright
  encode-*.tif.zz     what the writer must produce, byte for byte, when it writes the matching test file uncompressed
                      (made by tiffref.py, the line-for-line Python mirror of TiffCodec.cs; zlib-packed)
  reject-*.tif        files that must be refused with a message (JPEG-compressed, CMYK, float, 1-bit, BigTIFF, ...)
  manifest.txt        the list the C# check reads; manifest.json, the one run_tests.py reads

Pillow (libtiff) writes the common variants; the Tiff class below writes those Pillow can't (16-bit RGB, big-endian,
planar, tiles, pre-multiplied alpha, WhiteIsZero, a thumbnail first, EXIF/GPS/IPTC/ICC with an orientation). Every
test file is also opened with Pillow as a sanity check. run_tests.py checks tiffref.py against all of it, and against
Pillow for what tiffref writes; fuzz.py feeds tiffref damaged files.

Usage: python3 make_tests.py <out dir>      (python3 make_tests.py . remakes this folder)
"""
import json
import os
import struct
import sys
import zlib

import numpy as np
from PIL import Image

OUT = sys.argv[1]
os.makedirs(OUT, exist_ok=True)


def pattern(w, h, ch, bits, seed=1):
    rng = np.random.default_rng(seed)
    mx = 255 if bits == 8 else 65535
    y, x = np.mgrid[0:h, 0:w]
    planes = []
    for c in range(ch):
        base = (x * (c + 1) * 37 + y * (3 - c % 3) * 53) % (mx + 1) if bits == 8 else (x * 911 * (c + 1) + y * 1777) % 65536
        noise = rng.integers(0, mx + 1, size=(h, w))
        p = np.where((x + y + c) % 5 == 0, noise, base)
        if c == ch - 1 and ch in (2, 4):          # alpha: a gradient with fully transparent and opaque parts
            p = (x * mx // max(1, w - 1)).astype(np.int64)
            p[(y % 7) == 0] = 0
            p[(y % 7) == 1] = mx
        planes.append(p)
    a = np.stack(planes, axis=-1).astype(np.uint8 if bits == 8 else np.uint16)
    return a


def write_png(path, a):
    h, w, ch = a.shape
    bits = 8 if a.dtype == np.uint8 else 16
    ctype = {1: 0, 2: 4, 3: 2, 4: 6}[ch]
    raw = bytearray()
    be = a.astype('>u2') if bits == 16 else a
    rowbytes = be.reshape(h, -1).view(np.uint8) if bits == 16 else be.reshape(h, -1)
    for r in range(h):
        raw.append(0)
        raw.extend(rowbytes[r].tobytes())

    def chunk(t, d):
        return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xFFFFFFFF)

    data = b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, bits, ctype, 0, 0, 0)) + \
           chunk(b'IDAT', zlib.compress(bytes(raw), 9)) + chunk(b'IEND', b'')
    open(path, 'wb').write(data)


def lzw_encode(data):
    """TIFF LZW (MSB-first, early change), as libtiff writes it."""
    out = bytearray()
    acc = 0
    nacc = 0

    def emit(code, width):
        nonlocal acc, nacc
        acc = (acc << width) | code
        nacc += width
        while nacc >= 8:
            out.append((acc >> (nacc - 8)) & 0xFF)
            nacc -= 8
        acc &= (1 << nacc) - 1

    table = {bytes([i]): i for i in range(256)}
    width, nxt = 9, 258
    emit(256, width)
    w = b''
    for byte in data:
        wk = w + bytes([byte])
        if wk in table:
            w = wk
            continue
        emit(table[w], width)
        table[wk] = nxt
        nxt += 1
        if nxt == 4094:                      # table full: libtiff emits Clear and starts again
            emit(256, width)
            table = {bytes([i]): i for i in range(256)}
            width, nxt = 9, 258
        elif nxt > (1 << width) - 1:
            width += 1
        w = bytes([byte])
    if w:
        emit(table[w], width)
        nxt += 1
        if nxt == 4094:
            emit(256, width)
            width = 9
        elif nxt > (1 << width) - 1:
            width += 1
    emit(257, width)
    if nacc:
        out.append((acc << (8 - nacc)) & 0xFF)
    return bytes(out)


def packbits(data):
    out = bytearray()
    i = 0
    n = len(data)
    while i < n:
        j = i
        while j + 1 < n and data[j + 1] == data[i] and j - i < 127:
            j += 1
        if j > i:
            out.append((257 - (j - i + 1)) & 0xFF)
            out.append(data[i])
            i = j + 1
        else:
            j = i
            while j < n and j - i < 128 and (j + 1 >= n or data[j + 1] != data[j]):
                j += 1
            cnt = max(1, j - i)
            out.append(cnt - 1)
            out.extend(data[i:i + cnt])
            i += cnt
    return bytes(out)


def encode_block(block, bits, le, compression, predictor, spp_block):
    """block: (rows, cols, spp) array of this strip/tile. Returns compressed bytes."""
    b = block.astype(np.int64)
    if predictor == 2:
        mask = 0xFF if bits == 8 else 0xFFFF
        d = b.copy()
        d[:, 1:, :] = (b[:, 1:, :] - b[:, :-1, :]) & mask
        b = d
    if bits == 8:
        raw = b.astype(np.uint8).tobytes()
    else:
        raw = b.astype('<u2' if le else '>u2').tobytes()
    if compression == 1:
        return raw
    if compression == 5:
        return lzw_encode(raw)
    if compression == 8:
        return zlib.compress(raw, 6)
    if compression == 32773:
        return packbits(raw)
    raise ValueError(compression)


class Tiff:
    """A small TIFF writer: one or more directories, each an image (with tags) laid out as asked."""

    def __init__(self, le=True):
        self.le = le
        self.e = '<' if le else '>'
        self.buf = bytearray((b'II' if le else b'MM') + struct.pack(self.e + 'HI', 42, 0))
        self.ifd_offsets = []

    def align(self):
        if len(self.buf) & 1:
            self.buf.append(0)

    def put(self, data):
        self.align()
        at = len(self.buf)
        self.buf.extend(data)
        return at

    def value(self, typ, vals):
        e = self.e
        if isinstance(vals, (bytes, bytearray)):
            return bytes(vals)
        if typ in (1, 7):
            return bytes(vals) if not isinstance(vals, (bytes, bytearray)) else bytes(vals)
        if typ == 2:
            s = vals.encode('ascii') + b'\x00'
            return s
        if typ == 3:
            return struct.pack(e + 'H' * len(vals), *vals)
        if typ == 4:
            return struct.pack(e + 'I' * len(vals), *vals)
        if typ == 5:
            out = b''
            for n, d in vals:
                out += struct.pack(e + 'II', n, d)
            return out
        raise ValueError(typ)

    def count(self, typ, vals):
        if isinstance(vals, (bytes, bytearray)):
            return len(vals) // {1: 1, 3: 2, 4: 4, 5: 8, 7: 1}[typ]
        if typ == 2:
            return len(vals) + 1
        if typ in (1, 7):
            return len(vals)
        return len(vals)

    def directory(self, entries):
        """entries: {tag: (type, values)} where type 4 'IFD' values may be ('ifd', entries_dict)."""
        e = self.e
        # sub-directories first
        resolved = {}
        for tag, (typ, vals) in entries.items():
            if isinstance(vals, tuple) and len(vals) == 2 and vals[0] == 'ifd':
                at = self.directory(vals[1])
                resolved[tag] = (4, [at])
            else:
                resolved[tag] = (typ, vals)
        tags = sorted(resolved)
        values = {}
        for tag in tags:
            typ, vals = resolved[tag]
            v = self.value(typ, vals)
            if len(v) > 4:
                values[tag] = self.put(v)
        self.align()
        at = len(self.buf)
        d = struct.pack(e + 'H', len(tags))
        for tag in tags:
            typ, vals = resolved[tag]
            v = self.value(typ, vals)
            cnt = self.count(typ, vals)
            if len(v) <= 4:
                d += struct.pack(e + 'HHI', tag, typ, cnt) + v + b'\x00' * (4 - len(v))
            else:
                d += struct.pack(e + 'HHII', tag, typ, cnt, values[tag])
        d += struct.pack(e + 'I', 0)
        self.buf.extend(d)
        return at

    def image(self, a, compression=1, predictor=1, planar=1, tile=None, rows_per_strip=None, photometric=None,
              extra=None, colormap=None, subfile=0, more=None):
        h, w, spp = a.shape
        bits = 8 if a.dtype == np.uint8 else 16
        if photometric is None:
            photometric = 2 if spp >= 3 else 1
        planes = [a] if planar == 1 else [a[:, :, c:c + 1] for c in range(spp)]
        offsets, counts = [], []
        if tile:
            tw, th = tile
            for p in planes:
                for ty in range(0, h, th):
                    for tx in range(0, w, tw):
                        blk = np.zeros((th, tw, p.shape[2]), dtype=a.dtype)
                        part = p[ty:ty + th, tx:tx + tw, :]
                        blk[:part.shape[0], :part.shape[1], :] = part
                        data = encode_block(blk, bits, self.le, compression, predictor, p.shape[2])
                        offsets.append(self.put(data))
                        counts.append(len(data))
        else:
            rps = rows_per_strip or h
            for p in planes:
                for y in range(0, h, rps):
                    data = encode_block(p[y:y + rps], bits, self.le, compression, predictor, p.shape[2])
                    offsets.append(self.put(data))
                    counts.append(len(data))
        t = {254: (4, [subfile]), 256: (4, [w]), 257: (4, [h]), 258: (3, [bits] * spp), 259: (3, [compression]),
             262: (3, [photometric]), 277: (3, [spp]), 284: (3, [planar]),
             282: (5, [(300, 1)]), 283: (5, [(300, 1)]), 296: (3, [2])}
        if tile:
            t[322] = (3, [tile[0]])
            t[323] = (3, [tile[1]])
            t[324] = (4, offsets)
            t[325] = (4, counts)
        else:
            t[273] = (4, offsets)
            t[278] = (4, [rows_per_strip or h])
            t[279] = (4, counts)
        if predictor != 1:
            t[317] = (3, [predictor])
        if extra is not None:
            t[338] = (3, extra)
        if colormap is not None:
            t[320] = (3, colormap)
        if more:
            t.update(more)
        at = self.directory(t)
        self.ifd_offsets.append(at)
        return at

    def save(self, path):
        e = self.e
        struct.pack_into(e + 'I', self.buf, 4, self.ifd_offsets[0])
        # chain the directories
        for i in range(len(self.ifd_offsets) - 1):
            at = self.ifd_offsets[i]
            n = struct.unpack_from(e + 'H', self.buf, at)[0]
            struct.pack_into(e + 'I', self.buf, at + 2 + 12 * n, self.ifd_offsets[i + 1])
        open(path, 'wb').write(self.buf)


manifest = []


def add(name, tif_path, expect, note, meta=None):
    write_png(os.path.join(OUT, 'expect-' + name + '.png'), expect)
    entry = {"name": name, "file": os.path.basename(tif_path), "expect": 'expect-' + name + '.png', "note": note}
    if meta:
        entry.update(meta)
    manifest.append(entry)
    try:
        Image.open(tif_path).load()
        entry["pillowOpens"] = True
    except Exception as ex:
        entry["pillowOpens"] = False
        entry["pillowError"] = str(ex)[:80]


W, H = 37, 23        # odd sizes: partial tiles and strips

# ---- written by Pillow (libtiff)
for mode, ch in (("RGB", 3), ("L", 1), ("RGBA", 4), ("LA", 2)):
    a = pattern(W, H, ch, 8, seed=ch)
    im = Image.fromarray(a[:, :, 0] if ch == 1 else a, mode)
    for comp in ("raw", "tiff_lzw", "tiff_adobe_deflate", "packbits"):
        name = "pillow-%s8-%s" % (mode.lower(), comp.replace("tiff_", ""))
        p = os.path.join(OUT, "test-" + name + ".tif")
        try:
            im.save(p, compression=comp)
        except Exception as ex:
            print("skip", name, ex)
            continue
        add(name, p, a, "Pillow/libtiff, " + mode + " 8-bit, " + comp)
# predictor via libtiff
a = pattern(W, H, 3, 8, seed=9)
p = os.path.join(OUT, "test-pillow-rgb8-lzw-predictor.tif")
Image.fromarray(a, "RGB").save(p, compression="tiff_lzw", tiffinfo={317: 2})
add("pillow-rgb8-lzw-predictor", p, a, "Pillow/libtiff, RGB 8-bit, LZW with predictor")
# 16-bit grey
a16 = pattern(W, H, 1, 16, seed=3)
for comp in ("raw", "tiff_lzw", "tiff_adobe_deflate"):
    name = "pillow-gray16-" + comp.replace("tiff_", "")
    p = os.path.join(OUT, "test-" + name + ".tif")
    Image.fromarray(a16[:, :, 0], "I;16").save(p, compression=comp)
    add(name, p, a16, "Pillow/libtiff, grey 16-bit, " + comp)
# palette
idx = (pattern(W, H, 1, 8, seed=4) % 64).astype(np.uint8)
pal_im = Image.fromarray(idx[:, :, 0], "P")
pal = [((i * 4) & 255, (255 - i * 4) & 255, (i * 37) % 256) for i in range(256)]
pal_im.putpalette([v for c in pal for v in c])
p = os.path.join(OUT, "test-pillow-palette8-lzw.tif")
pal_im.save(p, compression="tiff_lzw")
exp = np.array([[pal[v] for v in row] for row in idx[:, :, 0]], dtype=np.uint8)
add("pillow-palette8-lzw", p, exp, "Pillow/libtiff, 8-bit palette, LZW")

# ---- written here: the variants Pillow can't write
cases = [
    # name, channels, bits, little-endian, compression, predictor, planar, tile, rows per strip
    ("rgb16-le-none", 3, 16, True, 1, 1, 1, None, 5),
    ("rgb16-be-none", 3, 16, False, 1, 1, 1, None, 5),
    ("rgb16-le-lzw-pred", 3, 16, True, 5, 2, 1, None, 8),
    ("rgb16-be-lzw-pred", 3, 16, False, 5, 2, 1, None, 8),
    ("rgba16-le-deflate-pred", 4, 16, True, 8, 2, 1, None, 4),
    ("rgba16-be-deflate-pred", 4, 16, False, 8, 2, 1, None, 4),
    ("gray16-be-lzw-pred", 1, 16, False, 5, 2, 1, None, 7),
    ("la16-le-packbits", 2, 16, True, 32773, 1, 1, None, 6),
    ("rgb8-be-lzw-pred", 3, 8, False, 5, 2, 1, None, 3),
    ("rgb8-planar-lzw", 3, 8, True, 5, 1, 2, None, 6),
    ("rgb16-planar-deflate-pred", 3, 16, True, 8, 2, 2, None, 6),
    ("rgba8-planar-be-packbits", 4, 8, False, 32773, 1, 2, None, 9),
    ("rgb8-tiled-deflate", 3, 8, True, 8, 1, 1, (16, 16), None),
    ("rgb16-tiled-be-lzw-pred", 3, 16, False, 5, 2, 1, (16, 16), None),
    ("rgba16-tiled-planar-lzw-pred", 4, 16, True, 5, 2, 2, (16, 16), None),
    ("gray8-tiled-none", 1, 8, True, 1, 1, 1, (32, 16), None),
]
for name, ch, bits, le, comp, pred, planar, tile, rps in cases:
    a = pattern(W, H, ch, bits, seed=len(name))
    t = Tiff(le)
    t.image(a, compression=comp, predictor=pred, planar=planar, tile=tile, rows_per_strip=rps,
            extra=[2] if ch in (2, 4) else None)
    p = os.path.join(OUT, "test-" + name + ".tif")
    t.save(p)
    add(name, p, a, "written here: %d ch, %d-bit, %s, compression %d, predictor %d, planar %d, %s" %
        (ch, bits, "LE" if le else "BE", comp, pred, planar, "tiles %s" % (tile,) if tile else "strips of %d" % rps))

# a big image: many strips, LZW tables that fill and reset
big = pattern(301, 203, 3, 16, seed=77)
t = Tiff(True)
t.image(big, compression=5, predictor=2, rows_per_strip=64)
p = os.path.join(OUT, "test-big-rgb16-lzw-pred.tif")
t.save(p)
add("big-rgb16-lzw-pred", p, big, "301 x 203 RGB 16-bit LZW with predictor, strips of 64 rows")
big8 = pattern(613, 401, 1, 8, seed=78)
t = Tiff(False)
t.image(big8, compression=5, rows_per_strip=401)
p = os.path.join(OUT, "test-big-gray8-be-lzw.tif")
t.save(p)
add("big-gray8-be-lzw", p, big8, "613 x 401 grey 8-bit, big-endian, one LZW strip (the table fills many times)")

# pre-multiplied (associated) alpha: the decoder must give straight alpha back
a = pattern(W, H, 4, 16, seed=21).astype(np.int64)
alpha = a[:, :, 3:4]
pm = (a[:, :, :3] * alpha + 32767) // 65535
stored = np.concatenate([pm, alpha], axis=-1).astype(np.uint16)
exp = np.where(alpha == 0, 0, np.minimum(65535, (pm * 65535 + alpha // 2) // np.maximum(alpha, 1)))
exp = np.concatenate([exp, alpha], axis=-1).astype(np.uint16)
t = Tiff(True)
t.image(stored, compression=8, predictor=2, rows_per_strip=6, extra=[1])
p = os.path.join(OUT, "test-rgba16-associated.tif")
t.save(p)
add("rgba16-associated", p, exp, "pre-multiplied (associated) alpha, 16-bit: decoded to straight alpha")

# WhiteIsZero grey
a = pattern(W, H, 1, 8, seed=31)
t = Tiff(True)
t.image((255 - a.astype(np.int64)).astype(np.uint8), compression=1, photometric=0, rows_per_strip=23)
p = os.path.join(OUT, "test-gray8-whiteiszero.tif")
t.save(p)
add("gray8-whiteiszero", p, a, "WhiteIsZero grey: inverted on reading")

# a thumbnail first, then the full image (as some scanners and cameras write)
thumb = pattern(9, 6, 3, 8, seed=41)
full = pattern(W, H, 3, 8, seed=42)
t = Tiff(True)
t.image(thumb, subfile=1)
t.image(full, compression=8)
p = os.path.join(OUT, "test-rgb8-thumbnail-first.tif")
t.save(p)
add("rgb8-thumbnail-first", p, full, "a reduced-size image first: the full-size one must be read")

# orientation 6 (a portrait), EXIF and GPS directories, ICC profile, big-endian metadata
upright = pattern(23, 37, 3, 16, seed=51)                # displayed 23 wide, 37 tall
stored = np.rot90(upright, k=1).copy()                   # 37 x 23 as stored; orientation 6 turns it clockwise
icc = bytes(range(256)) * 3 + b'test-icc-profile'
exif_ifd = {0x9003: (2, "2026:11:26 12:00:00"), 0x829A: (5, [(1, 250)]), 0x8827: (3, [400]),
            0xA002: (4, [37]), 0xA003: (4, [23]), 0xA005: (4, ('ifd', {1: (2, "R98")}))}
gps_ifd = {0: (1, [2, 3, 0, 0]), 1: (2, "N"), 2: (5, [(51, 1), (30, 1), (0, 1)])}
IPTC = b'\x1c\x02\x00\x00\x02\x00\x04\x1c\x02\x78\x00\x0bIPTC-caption'
IPTC += b'\x00' * (-len(IPTC) % 4)
for le in (True, False):
    t = Tiff(le)
    t.image(stored, compression=5, predictor=2, rows_per_strip=8, more={
        271: (2, "TestCam"), 272: (2, "T-1 Mark II"), 274: (3, [6]), 306: (2, "2026:11:26 12:00:00"),
        315: (2, "A. Photographer"), 33432: (2, "(c) test"), 34675: (7, icc),
        34665: (4, ('ifd', exif_ifd)), 34853: (4, ('ifd', gps_ifd)), 33723: (4, IPTC)})
    name = "rgb16-orient6-exif-icc-" + ("le" if le else "be")
    p = os.path.join(OUT, "test-" + name + ".tif")
    t.save(p)
    add(name, p, upright, "orientation 6 (turned upright on reading), EXIF + GPS + interop directories, ICC profile, " +
        ("little" if le else "big") + "-endian",
        {"sourceOrientation": 6, "iccLength": len(icc), "exifStrings": ["TestCam", "T-1 Mark II", "2026:11:26 12:00:00", "A. Photographer", "(c) test", "R98", "IPTC-caption"]})

# an IPTC record too large for a JPEG's EXIF: left out of the metadata, everything else kept
a = pattern(W, H, 3, 8, seed=61)
t = Tiff(True)
t.image(a, compression=8, more={271: (2, "TestCam"), 33723: (7, bytes(range(256)) * 280)})
p = os.path.join(OUT, "test-rgb8-big-iptc.tif")
t.save(p)
add("rgb8-big-iptc", p, a, "a 70 KB IPTC record: dropped so the EXIF still fits a JPEG; the rest is kept",
    {"exifStrings": ["TestCam"], "exifMax": 65000})

# many strips, smooth (compresses well), for the writer's strip layout
yy, xx = np.mgrid[0:400, 0:700]
band = yy // 50
wide = np.stack([xx * 93, band * 8000 + xx, (xx * 59 + band * 77) & 0xFFFF, 65535 - xx * 90], axis=-1).astype(np.uint16)
t = Tiff(True)
t.image(wide, compression=8, predictor=2, rows_per_strip=37, extra=[2])
p = os.path.join(OUT, "test-wide-rgba16-strips.tif")
t.save(p)
add("wide-rgba16-strips", p, wide, "700 x 400 RGBA 16-bit in 11 strips: the written copy has 3 strips")

# what the writer must produce, byte for byte, uncompressed (decoded as stored, not turned upright); zlib-packed
import tiffref
encodes = []
for name in ("rgb16-orient6-exif-icc-le", "rgb16-orient6-exif-icc-be", "pillow-rgba8-lzw", "pillow-gray16-lzw",
             "pillow-la8-raw", "rgb8-planar-lzw", "big-gray8-be-lzw", "wide-rgba16-strips", "rgb8-big-iptc", "pillow-palette8-lzw"):
    src = "test-" + name + ".tif"
    w, h, ch, depth, samples, meta = tiffref.Decode(open(os.path.join(OUT, src), 'rb').read())
    ref = tiffref.Encode(w, h, ch, depth, samples, meta, deflate=False)
    open(os.path.join(OUT, "encode-" + name + ".tif.zz"), 'wb').write(zlib.compress(ref, 9))
    encodes.append((name, src, "encode-" + name + ".tif.zz", len(ref)))

# files that must be refused with a clear message
import io
rejects = []
def reject(name, data):
    open(os.path.join(OUT, "reject-" + name + ".tif"), 'wb').write(data)
    rejects.append((name, "reject-" + name + ".tif"))
rgb = pattern(8, 8, 3, 8)
for name, im, kw in (("jpeg", Image.fromarray(rgb, "RGB"), {"compression": "jpeg"}),
                     ("cmyk", Image.fromarray(rgb, "RGB").convert("CMYK"), {}),
                     ("float", Image.fromarray(np.zeros((8, 8), np.float32), "F"), {}),
                     ("1bit", Image.fromarray(rgb[:, :, 0], "L").convert("1"), {})):
    b = io.BytesIO()
    im.save(b, format="TIFF", **kw)
    reject(name, b.getvalue())
reject("bigtiff", b"II+\x00\x08\x00\x00\x00\x10\x00\x00\x00\x00\x00\x00\x00")
reject("truncated", open(os.path.join(OUT, "test-rgb16-le-none.tif"), 'rb').read()[:9])
reject("png-renamed", open(os.path.join(OUT, "expect-rgb16-le-none.png"), 'rb').read())

with open(os.path.join(OUT, "manifest.txt"), "w") as f:
    f.write("# TIFF conformance cases (made by make_tests.py). Tab-separated:\n")
    f.write("# decode  name  tiff  expected-png  orientation  icc-length  exif-strings(|)  exif-max-length   ('-' = no check)\n")
    f.write("# encode  name  tiff  uncompressed-writer-output(zlib)  its-length\n")
    f.write("# reject  name  tiff   (must be refused with a message)\n")
    for m in manifest:
        f.write("\t".join(["decode", m["name"], m["file"], m["expect"], str(m.get("sourceOrientation", "-")),
                           str(m.get("iccLength", "-")), "|".join(m.get("exifStrings", [])) or "-",
                           str(m.get("exifMax", "-"))]) + "\n")
    for e in encodes:
        f.write("\t".join(["encode", e[0], e[1], e[2], str(e[3])]) + "\n")
    for e in rejects:
        f.write("\t".join(["reject", e[0], e[1]]) + "\n")

json.dump({"format": "fisheye-friend-tiff-tests", "cases": manifest}, open(os.path.join(OUT, "manifest.json"), "w"), indent=1)
print(len(manifest), "test files;", sum(1 for m in manifest if not m.get("pillowOpens")), "Pillow can't open:",
      [m["name"] for m in manifest if not m.get("pillowOpens")])
