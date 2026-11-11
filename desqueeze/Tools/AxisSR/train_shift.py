"""
Train the sub-pixel shift model: the same learned filters + detail network as the de-squeeze model, but for moving
fully sampled rows by fractions of a pixel (scale 0.94-1.08). It is the vertical pass of the two-pass lens-distortion
correction (Desqueeze.Core.Lens.TwoPassWarp), where the corrected rows fall between the sensor's rows.

    AXISSR_DATA=~/axissr-data AXISSR_WORK=~/axissr-shift python3 train_shift.py

Writes Assets/StreamingAssets/desqueeze-shift.bin (use --out to write elsewhere).

Each training image (area-reduced to at most 1024 px) is treated as a 2x oversampled scene. The input is the scene
integrated over pixel cells (a 100%-fill-factor sensor); the target is the same scene integrated over cells displaced
along x by c(x) = s*x + d, s in [0.94, 1.08], d in [0, 1) - so every phase occurs, at the slightly varying scales a
distortion correction has. The same randomised camera processing as the de-squeeze model (lens blur, sharpening, noise,
JPEG) is applied, and images are randomly flipped and transposed.
"""
import os, sys, time, pickle, argparse, numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import *
import degrade, learned as L, mlp, export_model
from scipy import ndimage

K = 2                       # oversampling of the scene relative to the input grid
CLEAN = os.environ.get("SHIFT_CLEAN") == "1"   # experiment: optics blur only, no sharpening/noise/JPEG
SMIN, SMAX = 0.94, 1.08

# ---------------------------------------------------------------- geometry hook: per-pair output positions
_CURRENT_C = {}
_orig_geometry = L.geometry

def _geometry(w, W):
    c = _CURRENT_C.get((w, W))
    if c is None:
        return _orig_geometry(w, W)
    j0 = np.floor(c).astype(int)
    return c, j0, c - j0

L.geometry = _geometry

class Pairs:
    """Cached pairs; loading one also registers its output positions for the geometry hook."""
    def __init__(self, files): self.files = files
    def __len__(self): return len(self.files)
    def _load(self, f):
        d = np.load(f)
        cap, ref, c = d["cap"].astype(np.float64), d["ref"].astype(np.float64), d["c"].astype(np.float64)
        _CURRENT_C.clear()
        _CURRENT_C[(cap.shape[1], ref.shape[1])] = c
        return cap, ref, 1.0
    def __getitem__(self, i):
        return Pairs(self.files[i]) if isinstance(i, slice) else self._load(self.files[i])
    def __iter__(self):
        for f in self.files: yield self._load(f)

# ---------------------------------------------------------------- simulation
def cell_matrix(n_hr, lo, hi):
    """Rows: output cells [lo, hi) in scene (HR) pixels; columns: HR pixels (constant over each)."""
    M = np.zeros((len(lo), n_hr))
    for i, (a, b) in enumerate(zip(lo, hi)):
        j0, j1 = int(np.floor(a)), int(np.ceil(b))
        for j in range(max(j0, 0), min(j1, n_hr)):
            M[i, j] = max(0.0, min(b, j + 1) - max(a, j))
    return M / M.sum(1, keepdims=True)

def make_pair(gt, s, d, p, rng):
    lin = to_linear(gt)
    Hh, Wh = lin.shape[:2]
    h, w = Hh // K, Wh // K
    lin = lin[:h * K, :w * K]
    if p.get("blur", 0) > 0:                                   # optics, in input pixels -> scene pixels
        lin = ndimage.gaussian_filter(lin, (p["blur"] * K, p["blur"] * K, 0), mode="nearest")
    V = lin.reshape(h, K, w * K, 3).mean(1)                     # rows: plain pixel cells
    cin = cell_matrix(w * K, np.arange(w) * K, (np.arange(w) + 1) * K)
    Wout = int(np.floor((w - 2 - d) / s))
    c = s * np.arange(Wout) + d                                 # output x -> input coordinate (pixel centres at integers)
    cout = cell_matrix(w * K, c * K, (c + 1) * K)
    cap = np.tensordot(V, cin, axes=([1], [1])).transpose(0, 2, 1)
    ref = np.tensordot(V, cout, axes=([1], [1])).transpose(0, 2, 1)
    cap, ref = from_linear(cap), from_linear(ref)
    if p.get("usm", 0) > 0:
        r = p.get("usm_r", 1.0)
        cap = degrade._usm(cap, r, r, p["usm"]); ref = degrade._usm(ref, r, r, p["usm"])
    if p.get("noise", 0) > 0:
        cap = np.clip(cap + rng.normal(0, p["noise"], cap.shape), 0, 1)
    if p.get("jpeg", 0) > 0:
        cap = degrade._jpeg(cap, p["jpeg"])
    return quant8(cap), quant8(ref), c

def make_pairs(per_image=6, seed=0):
    out = f"{WORK}/pairs"
    os.makedirs(out, exist_ok=True)
    rng = np.random.default_rng(seed)
    files = []
    for i, path in enumerate(list_images("train")):
        gt0 = None
        for k in range(per_image):
            f = f"{out}/{i:03d}_{k}.npz"
            s, d = rng.uniform(SMIN, SMAX), rng.uniform(0, 1)
            fh, fv, tr, ps = rng.random() < 0.5, rng.random() < 0.5, rng.random() < 0.5, rng.integers(1 << 30)
            if not os.path.exists(f):
                gt0 = load_gt(path) if gt0 is None else gt0
                g = gt0.transpose(1, 0, 2) if tr else gt0
                g = g[:, ::-1] if fh else g
                g = g[::-1] if fv else g
                r2 = np.random.default_rng(ps)
                prof = degrade.random_profile(r2)
                if CLEAN: prof = {k: v for k, v in prof.items() if k == "blur"}
                cap, ref, c = make_pair(np.ascontiguousarray(g), s, d, prof, r2)
                np.savez(f, cap=cap.astype(np.float32), ref=ref.astype(np.float32), c=c)
            files.append(f)
    return Pairs(files)

if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--hidden", default="32,24")
    ap.add_argument("--epochs", type=int, default=14)
    ap.add_argument("--frac", type=float, default=0.03)
    ap.add_argument("--rows", type=int, default=7)
    ap.add_argument("--cols", type=int, default=6)
    ap.add_argument("--out", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..",
                                                  "Assets", "StreamingAssets", "desqueeze-shift.bin"))
    a = ap.parse_args()
    t = time.time()
    pairs = make_pairs()
    print(f"{len(pairs)} training pairs ({time.time()-t:.0f}s)", flush=True)
    cfg = L.Config(space="gamma", phases=16, rows=a.rows, cols=a.cols)
    acc = L.train(pairs, cfg, frac=0.12)
    bank = dict(cfg=cfg, filt=L.solve(acc), cnt=acc.cnt)
    pickle.dump(bank, open(f"{WORK}/bank.pkl", "wb"))
    print(f"filter bank trained ({time.time()-t:.0f}s)", flush=True)
    X, T, S = mlp.sample_data(pairs[:-30], bank, frac=a.frac, with_scale=True)
    Xv, Tv, Sv = mlp.sample_data(pairs[-30:], bank, frac=0.02, seed=9, with_scale=True)
    hidden = tuple(int(h) for h in a.hidden.split(","))
    net = mlp.MLP(X.shape[1], hidden)
    net.train(X, T, S, Xv, Tv, Sv, epochs=a.epochs)
    pickle.dump(net, open(f"{WORK}/net.pkl", "wb"))
    export_model.export(f"{WORK}/bank.pkl", f"{WORK}/net.pkl", os.path.abspath(a.out),
                        name=f"sub-pixel shift v1 ({a.hidden})", scale_min=SMIN, scale_max=SMAX)
    print(f"done ({time.time()-t:.0f}s)")
