"""Other TIFF readers for the checks: tifffile and ImageMagick, where installed."""
import os
import shutil
import subprocess
import tempfile

import numpy as np

try:
    import tifffile
except ImportError:
    tifffile = None
MAGICK = shutil.which("convert") or shutil.which("magick")


def other_readers(data, shape, bits):
    """(reader, array or None) for tifffile and ImageMagick, where installed."""
    out = []
    h, w, ch = shape
    with tempfile.NamedTemporaryFile(suffix=".tif", delete=False) as f:
        f.write(data)
        path = f.name
    try:
        if tifffile is not None:
            try:
                t = tifffile.imread(path).astype(np.int64)
                out.append(("tifffile", t[:, :, None] if t.ndim == 2 else t))
            except Exception:
                out.append(("tifffile", None))
        if MAGICK:
            fmt = {1: "gray", 2: "graya", 3: "rgb", 4: "rgba"}[ch]
            res = subprocess.run([MAGICK, path, "-depth", str(bits), "-endian", "LSB", fmt + ":-"], capture_output=True)
            m = None
            if res.returncode == 0:
                m = np.frombuffer(res.stdout, np.uint8 if bits == 8 else "<u2").astype(np.int64)
                m = m.reshape(h, w, ch) if m.size == h * w * ch else None
            out.append(("ImageMagick", m))
    finally:
        os.unlink(path)
    return out
