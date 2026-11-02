"""Shared helpers for the one-axis (horizontal) upscaling experiments."""
import os, glob, numpy as np
from PIL import Image
from scipy import ndimage

# Where the training/test photos live and where intermediate files go (override with environment variables).
DATA = os.environ.get("AXISSR_DATA", os.path.expanduser("~/axissr-data"))
WORK = os.environ.get("AXISSR_WORK", os.path.expanduser("~/axissr-work"))

# ---------------------------------------------------------------- datasets
def list_images(split):
    kodak = sorted(glob.glob(f"{DATA}/Kodak-Lossless-True-Color-Image-Suite/PhotoCD_PCD0992/*.png"))
    urban = sorted(glob.glob(f"{DATA}/SelfExSR/data/Urban100/image_SRF_2/*_HR.png"))
    bsd = sorted(glob.glob(f"{DATA}/SelfExSR/data/BSD100/image_SRF_2/*_HR.png"))
    hires = sorted(glob.glob(f"{DATA}/hires/*"))
    if split == "train":
        return bsd + urban[:50]
    if split == "dev":       # quick iteration set (disjoint from train)
        return kodak[::3] + urban[50::6] + hires
    if split == "test":      # full held-out set
        return kodak + urban[50:] + hires
    raise ValueError(split)

def load_gt(path, max_side=1024):
    im = Image.open(path).convert("RGB")
    a = np.asarray(im).astype(np.float64) / 255.0
    h, w = a.shape[:2]
    k = max(w, h) / max_side
    if k > 1.01:  # area-reduce large images in linear light (cleans scan grain too)
        a = from_linear(resize_linear_axis(to_linear(a), int(round(w / k)), int(round(h / k))))
    return a

# ---------------------------------------------------------------- light spaces (values in [0,1])
def to_linear(s):
    s = np.clip(s, 0, 1)
    return np.where(s <= 0.04045, s / 12.92, ((s + 0.055) / 1.055) ** 2.4)

def from_linear(l):
    l = np.clip(l, 0, 1)
    return np.where(l <= 0.0031308, l * 12.92, 1.055 * l ** (1 / 2.4) - 0.055)

SIG_C, SIG_S = 0.75, 6.5
def _logistic(x): return 1.0 / (1.0 + np.exp(SIG_S * (SIG_C - x)))
_O = _logistic(0.0); _SC = _logistic(1.0) - _O
def lin_to_sig(l):
    l = np.clip(l, 0, 1) * _SC + _O
    return SIG_C - np.log(1.0 / l - 1.0) / SIG_S
def sig_to_lin(s):
    return np.clip((_logistic(np.clip(s, 0, 1)) - _O) / _SC, 0, 1)

def to_space(srgb, space):
    if space == "gamma": return srgb.copy()
    lin = to_linear(srgb)
    return lin if space == "linear" else lin_to_sig(lin)

def from_space(v, space):
    if space == "gamma": return np.clip(v, 0, 1)
    if space == "linear": return from_linear(v)
    return from_linear(sig_to_lin(v))

def quant8(srgb):
    return np.floor(np.clip(srgb, 0, 1) * 255 + 0.5) / 255.0

# ---------------------------------------------------------------- kernels and 1-D resampling (same maths as the C# AxisWeights)
def k_lanczos(a):
    return (lambda x: np.where(np.abs(x) < a, np.sinc(x) * np.sinc(x / a), 0.0)), a
def k_mks(x):
    x = np.abs(x); r = np.zeros_like(x)
    m = x < 0.5; r[m] = 577/576 - 239*x[m]**2/144
    m = (x >= 0.5) & (x < 1.5); r[m] = (140*x[m]**2 - 379*x[m] + 239)/144
    m = (x >= 1.5) & (x < 2.5); r[m] = (24*x[m]**2 - 113*x[m] + 130)/-144
    m = (x >= 2.5) & (x < 3.5); r[m] = (4*x[m]**2 - 27*x[m] + 45)/144
    m = (x >= 3.5) & (x < 4.5); r[m] = (4*x[m]**2 - 36*x[m] + 81)/-1152
    return r
def k_cubic(B, C):
    def f(x):
        x = np.abs(x); r = np.zeros_like(x)
        m1 = x < 1; m2 = (x >= 1) & (x < 2)
        r[m1] = ((12-9*B-6*C)*x[m1]**3 + (-18+12*B+6*C)*x[m1]**2 + (6-2*B))/6
        r[m2] = ((-B-6*C)*x[m2]**3 + (6*B+30*C)*x[m2]**2 + (-12*B-48*C)*x[m2] + (8*B+24*C))/6
        return r
    return f, 2
KERNELS = {"lanczos3": k_lanczos(3), "lanczos4": k_lanczos(4), "mks": (k_mks, 4.5),
           "catrom": k_cubic(0, 0.5), "linear": ((lambda x: np.maximum(0, 1 - np.abs(x))), 1)}

def axis_weights(n_in, n_out, kernel):
    """Dense (n_out x n_in) weight matrix: pixel-centre aligned, kernel stretched when reducing, edge replicated."""
    f, R = KERNELS[kernel] if isinstance(kernel, str) else kernel
    scale = n_out / n_in
    fs = max(1.0, 1.0 / scale)
    Wm = np.zeros((n_out, n_in))
    c = (np.arange(n_out) + 0.5) / scale
    lo = np.floor(c - R * fs - 0.5).astype(int); hi = np.ceil(c + R * fs - 0.5).astype(int)
    for i in range(n_out):
        j = np.arange(lo[i], hi[i] + 1)
        w = f((j + 0.5 - c[i]) / fs)
        np.add.at(Wm[i], np.clip(j, 0, n_in - 1), w)
    Wm /= Wm.sum(1, keepdims=True)
    return Wm

_WCACHE = {}
def axis_weights_sparse(n_in, n_out, kernel):
    key = (n_in, n_out, kernel if isinstance(kernel, str) else id(kernel))
    if key not in _WCACHE:
        import scipy.sparse as sp
        _WCACHE[key] = sp.csr_matrix(axis_weights(n_in, n_out, kernel).T)  # (n_in x n_out)
    return _WCACHE[key]

def resize_x(img, n_out, kernel="lanczos3"):
    M = axis_weights_sparse(img.shape[1], n_out, kernel)
    if img.ndim == 2:
        return np.asarray(img @ M)
    return np.stack([np.asarray(img[..., c] @ M) for c in range(img.shape[2])], 2)

def resize_linear_axis(lin, w, h):
    """Area (box) resize in both axes of a linear-light image."""
    out = []
    for c in range(lin.shape[2]):
        out.append(np.asarray(Image.fromarray(lin[..., c].astype(np.float32), "F").resize((w, h), Image.BOX)))
    return np.stack(out, 2).astype(np.float64)

# ---------------------------------------------------------------- capture simulation
def area_squeeze_x(lin, n_out):
    """Exact area integration horizontally (ideal 100%-fill-factor sensor)."""
    n_in = lin.shape[1]
    edges = np.linspace(0, n_in, n_out + 1)
    Wm = np.zeros((n_out, n_in))
    for i in range(n_out):
        a, b = edges[i], edges[i + 1]
        j0, j1 = int(np.floor(a)), int(np.ceil(b))
        for j in range(j0, min(j1, n_in)):
            Wm[i, j] = max(0.0, min(b, j + 1) - max(a, j))
    Wm /= Wm.sum(1, keepdims=True)
    return np.einsum("ij,hjc->hic", Wm, lin)

def capture(gt_srgb, A, model):
    """Simulated anamorphic capture of gt (squeezed by A horizontally) and the matching reference.
    Returns (capture_srgb_quantised, reference_srgb) where reference has the ideal de-squeezed geometry."""
    lin = to_linear(gt_srgb)
    H, W = lin.shape[:2]
    sw = int(round(W / A))
    if model == "area":
        cap = area_squeeze_x(lin, sw); ref = lin
    elif model == "sharp":
        cap = resize_x(lin, sw, "lanczos3"); ref = lin
    elif model == "soft":   # OLPF-like blur of 0.4 sensor px on both axes; reference gets the same per-pixel blur
        cap = area_squeeze_x(lin, sw)
        cap = ndimage.gaussian_filter(cap, (0.4, 0.4, 0), mode="nearest")
        ref = ndimage.gaussian_filter(lin, (0.4, 0.4, 0), mode="nearest")
    else:
        raise ValueError(model)
    return quant8(from_linear(cap)), quant8(from_linear(ref))

def crop_for_factor(gt, A):
    """Crop width so W/A is (close to) an integer and the reference geometry is exact."""
    H, W = gt.shape[:2]
    sw = int(W / A)
    wc = int(round(sw * A))
    x0 = (W - wc) // 2
    return gt[:, x0:x0 + wc]

# ---------------------------------------------------------------- metrics (sRGB, borders shaved)
def _shave(a, b, s=4):
    return a[s:-s, s:-s], b[s:-s, s:-s]

def psnr(a, b):
    a, b = _shave(a, b)
    mse = np.mean((a - b) ** 2)
    return 99.0 if mse == 0 else 10 * np.log10(1.0 / mse)

def luma(x):
    return 0.299 * x[..., 0] + 0.587 * x[..., 1] + 0.114 * x[..., 2]

def gmsd(ref, out):
    ref, out = _shave(ref, out)
    a = luma(ref) * 255; b = luma(out) * 255
    k = np.array([[1/3, 0, -1/3]] * 3)
    ga = np.hypot(ndimage.convolve(a, k), ndimage.convolve(a, k.T))
    gb = np.hypot(ndimage.convolve(b, k), ndimage.convolve(b, k.T))
    return ((2 * ga * gb + 170.0) / (ga ** 2 + gb ** 2 + 170.0)).std()

def edge_psnr(ref, out, frac=0.15):
    """PSNR over the 15% of pixels with the strongest horizontal detail in the reference (where widening matters)."""
    ref, out = _shave(ref, out)
    y = luma(ref)
    gx = np.abs(ndimage.sobel(y, axis=1))
    thr = np.quantile(gx, 1 - frac)
    m = gx >= thr
    mse = np.mean(((ref - out) ** 2)[m])
    return 10 * np.log10(1.0 / max(mse, 1e-12))

def ssim(ref, out):
    from skimage.metrics import structural_similarity
    ref, out = _shave(ref, out)
    return structural_similarity(ref, out, channel_axis=2, data_range=1.0)

def score(ref, out):
    out = quant8(out)
    return dict(psnr=psnr(ref, out), epsnr=edge_psnr(ref, out), ssim=ssim(ref, out), gmsd=gmsd(ref, out))
