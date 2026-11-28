"""
Checks the TIFFs the C# writer made (written/, saved there by the EditMode test TiffTests) with other readers:
tiffref.py, Pillow (libtiff), tifffile and ImageMagick, where installed.

  written-<case>.tif            must hold the picture of test-<case>.tif (as stored, not turned upright), its ICC
                                profile, and its metadata
  written-pattern-<n>ch-<b>bit  must hold TiffConformance's test pattern, which is remade here

Usage: python3 check_written.py [folder]      (default: this script's folder)
"""
import io
import os
import sys

import numpy as np
from PIL import Image

import tiffref
from readers import other_readers



def pattern_cs(w, h, ch, depth, seed):
    """TiffConformance.Pattern, line for line."""
    s = (seed * 2654435761 + 1) & 0xFFFFFFFF
    mx = 65535 if depth == 16 else 255
    alpha = ch in (2, 4)
    out = []
    for y in range(h):
        for x in range(w):
            for c in range(ch):
                s = (s * 1664525 + 1013904223) & 0xFFFFFFFF
                if alpha and c == ch - 1:
                    v = 0 if y % 7 == 0 else mx if y % 7 == 1 else x * mx // (w - 1)
                elif (x + y + c) % 5 == 0:
                    v = (s >> 16) % (mx + 1)
                else:
                    v = (x * (c + 3) * 37 + y * 53) % (mx + 1)
                out.append(v)
    return np.array(out, dtype=np.int64).reshape(h, w, ch)


def dec(data):
    w, h, ch, depth, s, meta = tiffref.Decode(data)
    return np.array(s, dtype=np.int64).reshape(h, w, ch), depth, meta


def main():
    D = sys.argv[1] if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__))
    W = os.path.join(D, "written")
    fails = 0
    names = sorted(f for f in os.listdir(W) if f.startswith("written-") and f.endswith(".tif"))
    if not names:
        sys.exit("nothing in " + W + " - run the TiffTests EditMode test first")
    for f in names:
        data = open(os.path.join(W, f), "rb").read()
        probs = []
        try:
            got, depth, meta = dec(data)
        except Exception as e:
            print("FAIL %-44s tiffref can't read it: %r" % (f, e))
            fails += 1
            continue
        case = f[len("written-"):-len(".tif")]
        if case.startswith("pattern-"):
            ch = int(case.split("-")[1][0])
            bits = int(case.split("-")[2][:-3])
            exp = pattern_cs(1031, 300, ch, bits, 11 * ch + bits)
            src_meta = {}
        else:
            exp, bits, src_meta = dec(open(os.path.join(D, "test-" + case + ".tif"), "rb").read())
        if got.shape != exp.shape or not np.array_equal(got, exp):
            probs.append("tiffref: the pixels differ")
        if src_meta.get("icc") and meta.get("icc") != src_meta["icc"]:
            probs.append("the ICC profile differs")
        if src_meta.get("exif") and b"TestCam" in src_meta["exif"] and b"TestCam" not in (meta.get("exif") or b""):
            probs.append("the camera tags are missing")
        try:
            im = Image.open(io.BytesIO(data))
            im.load()
            if bits == 8 or exp.shape[2] == 1:
                p = np.array(im).astype(np.int64)
                p = p[:, :, None] if p.ndim == 2 else p
                if p.shape != exp.shape or not np.array_equal(p, exp):
                    probs.append("Pillow reads it differently")
            if src_meta.get("icc") and im.info.get("icc_profile") != src_meta["icc"]:
                probs.append("Pillow sees a different ICC profile")
        except Exception as e:
            if bits == 8 or exp.shape[2] == 1:
                probs.append("Pillow can't read it: %r" % e)
        for reader, arr in other_readers(data, exp.shape, bits):
            if arr is None or arr.shape != exp.shape or not np.array_equal(arr, exp):
                probs.append(reader + " reads it differently")
        if probs:
            fails += 1
        print(("FAIL %-44s %s" % (f, "; ".join(probs))) if probs else ("ok   %-44s %s, %d bytes" % (f, "x".join(map(str, exp.shape)) + " %d-bit" % bits, len(data))))
    print("FAILURES:", fails)


if __name__ == "__main__":
    main()
