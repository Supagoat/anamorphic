"""Realistic anamorphic capture simulation with randomised camera processing, plus fixed test conditions."""
import io, os, sys, numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import *
from scipy import ndimage
from PIL import Image

def _usm(img, sx, sy, amount):
    """Unsharp mask in gamma space (as cameras/raw developers do), Gaussian radius (sx, sy) px."""
    blur = ndimage.gaussian_filter(img, (sy, sx, 0), mode="nearest")
    return np.clip(img + amount * (img - blur), 0, 1)

def _jpeg(img, q):
    b = io.BytesIO()
    Image.fromarray((np.clip(img, 0, 1) * 255 + 0.5).astype(np.uint8)).save(b, "JPEG", quality=int(q), subsampling=0)
    return np.asarray(Image.open(io.BytesIO(b.getvalue()))).astype(np.float64) / 255

def make_pair(gt, A, p, rng=None):
    """gt: sRGB image already cropped for factor A. p: dict of processing parameters.
    Returns (capture, reference) in sRGB, quantised to 8 bits."""
    lin = to_linear(gt)
    H, W = lin.shape[:2]
    sw = int(round(W / A))
    cap = area_squeeze_x(lin, sw)
    ref = lin
    s = p.get("blur", 0.0)                              # optical/OLPF blur in sensor px (both axes)
    if s > 0:
        cap = ndimage.gaussian_filter(cap, (s, s, 0), mode="nearest")
        ref = ndimage.gaussian_filter(ref, (s, s, 0), mode="nearest")
    if p.get("lanczos", False):                          # idealised brick-wall capture (stress test only)
        cap = resize_x(lin, sw, "lanczos3"); ref = lin
    cap = from_linear(cap); ref = from_linear(ref)
    if p.get("usm", 0) > 0:                              # in-camera sharpening at the camera's pixel scale
        r = p.get("usm_r", 1.0)
        cap = _usm(cap, r, r, p["usm"])
        ref = _usm(ref, r, r, p["usm"])
    if p.get("noise", 0) > 0:
        rng = rng or np.random.default_rng(0)
        cap = np.clip(cap + rng.normal(0, p["noise"], cap.shape), 0, 1)
    if p.get("jpeg", 0) > 0:
        cap = _jpeg(cap, p["jpeg"])
    return quant8(cap), quant8(ref)

def random_profile(rng):
    p = {}
    if rng.random() < 0.55: p["blur"] = rng.uniform(0.15, 0.6)
    if rng.random() < 0.4:
        p["usm"] = rng.uniform(0.2, 1.0); p["usm_r"] = rng.uniform(0.6, 1.4)
    if rng.random() < 0.35: p["noise"] = rng.uniform(0.002, 0.01)
    if rng.random() < 0.3: p["jpeg"] = rng.uniform(84, 97)
    return p

TEST_CONDITIONS = {
    "area":        dict(),
    "soft":        dict(blur=0.4),
    "soft+sharpen": dict(blur=0.4, usm=0.8, usm_r=1.0),
    "area+noise":  dict(noise=0.008),
    "soft+jpeg90": dict(blur=0.3, jpeg=90),
    "brickwall":   dict(lanczos=True),
}
