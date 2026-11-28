"""
Checks tiffref.py (the line-for-line mirror of TiffCodec.cs) against the test files from make_tests.py, then checks
that what tiffref.Encode writes is read back exactly, by tiffref and by Pillow/libtiff, metadata included.

Usage: python3 run_tests.py <test dir>      (python3 run_tests.py . here)
"""
import io
import json
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import zlib

import numpy as np
from PIL import Image

import tiffref
from readers import other_readers

D = sys.argv[1]
man = json.load(open(os.path.join(D, "manifest.json")))
fails = 0


def read_png(path):
    b = open(path, 'rb').read()
    p = 8
    idat = b''
    while p < len(b):
        n, t = struct.unpack('>I4s', b[p:p + 8])
        c = b[p + 8:p + 8 + n]
        if t == b'IHDR':
            w, h, bits, ct = struct.unpack('>IIBB', c[:10])
        elif t == b'IDAT':
            idat += c
        p += 12 + n
    ch = {0: 1, 4: 2, 2: 3, 6: 4}[ct]
    raw = zlib.decompress(idat)
    bpp = ch * bits // 8
    rows = [raw[y * (1 + w * bpp) + 1:(y + 1) * (1 + w * bpp)] for y in range(h)]
    a = np.frombuffer(b''.join(rows), dtype=np.uint8 if bits == 8 else '>u2').reshape(h, w, ch)
    return a.astype(np.int64)


def orientation(exif):
    if not exif:
        return 1
    le = exif[0:2] == b'II'
    e = '<' if le else '>'
    off = struct.unpack(e + 'I', exif[4:8])[0]
    n = struct.unpack(e + 'H', exif[off:off + 2])[0]
    for i in range(n):
        tag, typ, cnt = struct.unpack(e + 'HHI', exif[off + 2 + 12 * i:off + 10 + 12 * i])
        if tag == 274:
            return struct.unpack(e + 'H', exif[off + 10 + 12 * i:off + 12 + 12 * i])[0]
    return 1


def upright(a, o):
    if o == 6:
        return np.rot90(a, k=-1)
    if o == 8:
        return np.rot90(a, k=1)
    if o == 3:
        return a[::-1, ::-1]
    return a


def decode(data):
    w, h, ch, depth, samples, meta = tiffref.Decode(data)
    a = np.array(samples, dtype=np.int64).reshape(h, w, ch)
    return a, depth, meta


for c in man["cases"]:
    data = open(os.path.join(D, c["file"]), 'rb').read()
    exp = read_png(os.path.join(D, c["expect"]))
    try:
        a, depth, meta = decode(data)
    except Exception as ex:
        print("FAIL %-34s decode: %r" % (c["name"], ex))
        fails += 1
        continue
    o = orientation(meta.get('exif'))
    a = upright(a, o)
    probs = []
    if a.shape != exp.shape:
        probs.append("shape %s, expected %s" % (a.shape, exp.shape))
    elif not np.array_equal(a, exp):
        bad = np.argwhere(a != exp)
        y, x, k = bad[0]
        probs.append("%d samples differ, first at (%d,%d,%d): %d vs %d" % (len(bad), x, y, k, a[y, x, k], exp[y, x, k]))
    if "sourceOrientation" in c and o != c["sourceOrientation"]:
        probs.append("orientation %d, expected %d" % (o, c["sourceOrientation"]))
    if "iccLength" in c and len(meta.get('icc') or b'') != c["iccLength"]:
        probs.append("ICC %d bytes, expected %d" % (len(meta.get('icc') or b''), c["iccLength"]))
    for s in c.get("exifStrings", []):
        if s.encode() not in (meta.get('exif') or b''):
            probs.append("EXIF lacks %r" % s)
    if "exifMax" in c and len(meta.get('exif') or b'') > c["exifMax"]:
        probs.append("EXIF is %d bytes" % len(meta['exif']))
    # round trip: encode what was decoded, both ways, and read it back
    for deflate in (True, False):
        h, w, ch = a.shape
        bits = 16 if exp.max() > 255 or depth == 16 else 8
        enc = tiffref.Encode(w, h, ch, bits, a.reshape(-1).tolist(), meta, deflate=deflate, strip_bytes=700)
        b, _, m2 = decode(enc)
        if b.shape != a.shape or not np.array_equal(b, a):
            probs.append("round trip (deflate=%s) differs" % deflate)
        if orientation(m2.get('exif')) not in (1,):
            probs.append("round trip orientation %d" % orientation(m2.get('exif')))
        for s in c.get("exifStrings", []):
            if s.encode() not in (m2.get('exif') or b''):
                probs.append("round trip EXIF lacks %r" % s)
        if meta.get('icc') and m2.get('icc') != meta.get('icc'):
            probs.append("round trip ICC differs")
        # and Pillow reads what we write (where Pillow supports the mode)
        try:
            im = Image.open(io.BytesIO(enc))
            im.load()
            if bits == 8 or ch == 1:
                p = np.array(im).astype(np.int64)
                if p.ndim == 2:
                    p = p[:, :, None]
                if p.shape != a.shape or not np.array_equal(p, a):
                    probs.append("Pillow reads our %d-bit %d-channel TIFF differently (mode %s)" % (bits, ch, im.mode))
            if meta.get('icc') and im.info.get('icc_profile') != meta['icc']:
                probs.append("Pillow sees a different ICC")
            ex = im.getexif()
            if c.get("exifStrings") and ex.get(271) != "TestCam":
                probs.append("Pillow sees Make=%r" % ex.get(271))
            if "R98" in c.get("exifStrings", []):
                sub = ex.get_ifd(0x8769)
                if sub.get(0x9003) != "2026:11:26 12:00:00":
                    probs.append("Pillow EXIF sub-IFD DateTimeOriginal=%r" % sub.get(0x9003))
                gps = ex.get_ifd(0x8825)
                if gps.get(1) != "N":
                    probs.append("Pillow GPS LatitudeRef=%r" % gps.get(1))
        except Exception as ex:
            if not (bits == 16 and ch > 1):
                probs.append("Pillow can't read ours: %r" % ex)
        for reader, got in other_readers(enc, a.shape, bits):
            if got is None or got.shape != a.shape or not np.array_equal(got, a):
                probs.append("%s reads our TIFF differently" % reader)
    if probs:
        fails += 1
        print("FAIL %-34s %s" % (c["name"], "; ".join(probs)))
    else:
        print("ok   %-34s %s" % (c["name"], c["note"]))

# rejections: these must fail with a clear message, not crash or mis-read
def must_reject(name, data, words):
    global fails
    try:
        tiffref.Decode(data)
        print("FAIL reject %-28s decoded" % name)
        fails += 1
    except Exception as ex:
        msg = str(ex)
        ok = any(w in msg for w in words)
        print(("ok   reject %-28s %s" if ok else "FAIL reject %-28s %s") % (name, msg))
        if not ok:
            fails += 1

rgb = np.zeros((4, 4, 3), np.uint8)
buf = io.BytesIO()
Image.fromarray(rgb, "RGB").save(buf, format="TIFF", compression="jpeg")
must_reject("jpeg-compressed", buf.getvalue(), ["JPEG", "compression"])
buf = io.BytesIO()
Image.fromarray(rgb, "RGB").convert("CMYK").save(buf, format="TIFF")
must_reject("cmyk", buf.getvalue(), ["CMYK", "photometric"])
buf = io.BytesIO()
Image.fromarray(np.zeros((4, 4), np.float32), "F").save(buf, format="TIFF")
must_reject("float", buf.getvalue(), ["float", "sample format", "32-bit"])
buf = io.BytesIO()
Image.fromarray(np.zeros((4, 4), np.uint8), "L").convert("1").save(buf, format="TIFF")
must_reject("1-bit", buf.getvalue(), ["1-bit"])
must_reject("truncated", open(os.path.join(D, "test-rgb16-le-none.tif"), 'rb').read()[:9], ["TIFF", "outside", "truncated"])
print("FAILURES:", fails)
