"""
Learned anisotropic one-axis upscaler (prototype).

For every output pixel we know its sub-pixel phase between two input columns. A 2-D window of
input samples (TAP_COLS columns x TAP_ROWS rows) is filtered with a linear filter chosen by:
  phase bin (position between input columns), local edge angle (structure tensor, input coords),
  edge strength and edge coherence.
Filters are trained by constrained least squares (taps sum to 1) on simulated anamorphic captures.
"""
import sys, os, numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import *
from scipy import ndimage


class Config:
    def __init__(self, phases=8, angles=16, strengths=3, coherences=3, sigma=1.0, space="sigmoid", rows=7, cols=6,
                 noise=0.0, edge_bins=None):
        self.P, self.Q, self.S, self.C = phases, angles, strengths, coherences
        # edge-offset bin edges in input px (None = feature disabled)
        self.E_edges = None if edge_bins is None else np.asarray(edge_bins, float)
        self.rows, self.cols = rows, cols
        self.noise = noise
        self.sigma = sigma
        self.space = space
        self.s_edges = None   # strength thresholds (set during training)
        self.c_edges = None

    @property
    def NT(self): return self.rows * self.cols
    @property
    def RH(self): return self.rows // 2
    @property
    def CL(self): return self.cols // 2 - 1     # columns left of j0

    @property
    def E(self):
        return 1 if getattr(self, "E_edges", None) is None else len(self.E_edges) + 1

    @property
    def nbuckets(self):
        return self.P * self.Q * self.S * self.C * self.E

def luma_ws(img):
    return 0.299 * img[..., 0] + 0.587 * img[..., 1] + 0.114 * img[..., 2]

def tensor_fields(L, sigma):
    """Structure tensor of the input luma in input-pixel coordinates."""
    gx = np.zeros_like(L); gy = np.zeros_like(L)
    gx[:, 1:-1] = (L[:, 2:] - L[:, :-2]) * 0.5
    gy[1:-1, :] = (L[2:, :] - L[:-2, :]) * 0.5
    jxx = ndimage.gaussian_filter(gx * gx, sigma, mode="nearest")
    jxy = ndimage.gaussian_filter(gx * gy, sigma, mode="nearest")
    jyy = ndimage.gaussian_filter(gy * gy, sigma, mode="nearest")
    return jxx, jxy, jyy

def geometry(w, W):
    c = (np.arange(W) + 0.5) * (w / W) - 0.5
    j0 = np.floor(c).astype(int)
    phase = c - j0
    return c, j0, phase

def features(L, W, cfg):
    """Per-output-pixel (H x W) angle, strength, coherence, phase."""
    H, w = L.shape
    jxx, jxy, jyy = tensor_fields(L, cfg.sigma)
    c, j0, phase = geometry(w, W)
    a = np.clip(j0, 0, w - 1); b = np.clip(j0 + 1, 0, w - 1)
    f = phase[None, :]
    def interp(F): return F[:, a] * (1 - f) + F[:, b] * f
    Jxx, Jxy, Jyy = interp(jxx), interp(jxy), interp(jyy)
    tr = Jxx + Jyy
    d = np.sqrt(((Jxx - Jyy) * 0.5) ** 2 + Jxy ** 2)
    l1 = tr * 0.5 + d; l2 = np.maximum(tr * 0.5 - d, 0)
    theta = 0.5 * np.arctan2(2 * Jxy, Jxx - Jyy)          # dominant gradient direction
    theta = np.mod(theta, np.pi)
    s1, s2 = np.sqrt(l1), np.sqrt(l2)
    strength = s1
    coherence = (s1 - s2) / (s1 + s2 + 1e-9)
    return theta, strength, coherence, np.broadcast_to(phase[None, :], (H, W)), j0

def edge_offset(L, W):
    """Sub-pixel position of the strongest horizontal transition near each output pixel, relative to the
    output pixel centre, in input px. Uses the transitions between columns j0-1..j0+2."""
    H, w = L.shape
    c, j0, phase = geometry(w, W)
    g = np.abs(np.diff(np.pad(L, ((0, 0), (2, 3)), mode="edge"), axis=1))   # g[:, k] = |L[k-1] - L[k-2]| padded: transition between cols k-2 and k-1
    # transitions t = j0-1+m (between col j0-1+m and j0+m), m = 0..2  ->  index in g: (j0-1+m) + 2
    idx = np.clip(j0[None, :] + 1 + np.arange(3)[:, None], 1, g.shape[1] - 2)  # (3, W)
    G = g[:, idx]                                    # (H, 3, W)
    m = np.argmax(G, axis=1)                         # strongest of the three transitions
    gi = np.take_along_axis(idx[None].repeat(H, 0), m[:, None, :], 1)[:, 0]   # (H, W) index into g
    rows = np.arange(H)[:, None]
    gm, gl, gr = g[rows, gi], g[rows, gi - 1], g[rows, gi + 1]
    den = gl - 2 * gm + gr
    off = np.where(np.abs(den) > 1e-9, 0.5 * (gl - gr) / den, 0.0)
    off = np.clip(off, -0.5, 0.5)
    xe = (gi - 2) + 0.5 + off                        # transition between cols (gi-2) and (gi-1) sits at gi-2+0.5
    return xe - c[None, :]

def bucket_ids(theta, strength, coherence, phase, cfg, eoff=None):
    qa = np.minimum((theta / np.pi * cfg.Q).astype(int), cfg.Q - 1)
    qs = np.searchsorted(cfg.s_edges, strength)
    qc = np.searchsorted(cfg.c_edges, coherence)
    qp = np.minimum((phase * cfg.P).astype(int), cfg.P - 1)
    b = ((qp * cfg.Q + qa) * cfg.S + qs) * cfg.C + qc
    if cfg.E > 1:
        b = b * cfg.E + np.searchsorted(cfg.E_edges, eoff)
    return b, qp

def windows(img_ws, j0, rows, cfg, pad=None):
    """Gather (len(rows), W, NT, ch) windows: rows y-RH..y+RH, columns j0-CL..j0+cols-CL-1."""
    if pad is None:
        pad = np.pad(img_ws, ((cfg.RH, cfg.RH), (cfg.cols, cfg.cols), (0, 0)), mode="edge")
    rr = (np.asarray(rows)[:, None, None, None] + np.arange(cfg.rows)[None, None, :, None])
    cc = (j0[None, :, None, None] + cfg.cols - cfg.CL + np.arange(cfg.cols)[None, None, None, :])
    win = pad[rr, cc]                     # (R, W, TR, TC, ch)
    return win.reshape(len(rows), len(j0), cfg.NT, img_ws.shape[2])

# ------------------------------------------------------------------------------------------ training
class Accumulator:
    def __init__(self, cfg):
        self.cfg = cfg
        n = cfg.nbuckets
        NT = cfg.NT
        self.ata = np.zeros((n, NT, NT))
        self.atb = np.zeros((n, NT))
        self.cnt = np.zeros(n)

    def add(self, X, y, b):
        order = np.argsort(b, kind="stable")
        b = b[order]; X = X[order]; y = y[order]
        uniq, start = np.unique(b, return_index=True)
        end = np.append(start[1:], len(b))
        for u, s, e in zip(uniq, start, end):
            Xs = X[s:e]
            self.ata[u] += Xs.T @ Xs
            self.atb[u] += Xs.T @ y[s:e]
            self.cnt[u] += e - s

def calibrate(train_cases, cfg, nsamp=200000):
    """Strength/coherence quantile thresholds from the training data."""
    rng = np.random.default_rng(0)
    ss, cs = [], []
    for cap, ref, A in train_cases[:40]:
        L = luma_ws(to_space(cap, cfg.space))
        th, st, co, ph, j0 = features(L, ref.shape[1], cfg)
        idx = rng.integers(0, st.size, 5000)
        ss.append(st.ravel()[idx]); cs.append(co.ravel()[idx])
    ss = np.concatenate(ss); cs = np.concatenate(cs)
    cfg.s_edges = np.quantile(ss, np.linspace(0, 1, cfg.S + 1)[1:-1])
    cfg.c_edges = np.quantile(cs, np.linspace(0, 1, cfg.C + 1)[1:-1])

def accumulate_case(acc, cap, ref, cfg, frac=0.25, rng=None, chunk=48):
    if cfg.noise > 0:   # sensor-noise augmentation (sRGB units), applied to the capture only
        cap = np.clip(cap + rng.normal(0, cfg.noise, cap.shape) * rng.uniform(0, 1), 0, 1)
    NT = cfg.NT
    cap_ws = to_space(cap, cfg.space).astype(np.float32); ref_ws = to_space(ref, cfg.space).astype(np.float32)
    H, W = ref.shape[:2]
    L = luma_ws(cap_ws.astype(np.float64))
    th, st, co, ph, j0 = features(L, W, cfg)
    eoff = edge_offset(L, W) if cfg.E > 1 else None
    bid, _ = bucket_ids(th, st, co, ph, cfg, eoff)
    mask = rng.random((H, W)) < frac
    mask[:3] = mask[-3:] = False; mask[:, :4] = mask[:, -4:] = False
    pad = np.pad(cap_ws, ((cfg.RH, cfg.RH), (cfg.cols, cfg.cols), (0, 0)), mode="edge")
    Xs, ys, bs = [], [], []
    for r0 in range(0, H, chunk):
        rows = np.arange(r0, min(H, r0 + chunk))
        m = mask[rows]
        if not m.any(): continue
        win = windows(cap_ws, j0, rows, cfg, pad)     # (R, W, NT, 3)
        X = win[m]; n = X.shape[0]
        Xs.append(X.transpose(0, 2, 1).reshape(n * 3, NT))
        ys.append(ref_ws[rows][m].reshape(n * 3))
        bs.append(np.repeat(bid[rows][m], 3))
    acc.add(np.concatenate(Xs).astype(np.float64), np.concatenate(ys).astype(np.float64), np.concatenate(bs))

def _shrunk_solve(M, v, one, ridge):
    M = M + ridge * np.trace(M) / len(one) * np.eye(len(one))
    Mi1 = np.linalg.solve(M, one)
    h0 = np.linalg.solve(M, v)
    return h0 + (1.0 - one @ h0) / (one @ Mi1) * Mi1

def solve(acc, ridge=1e-4, prior=400.0):
    """Constrained (sum = 1) ridge LS per bucket, with hierarchical shrinkage towards coarser buckets:
    leaf (phase, angle, strength, coherence, edge) -> (phase, angle, strength, coherence) -> (phase, angle) -> (phase)."""
    cfg = acc.cfg
    NT = cfg.NT
    P, Q, S, C, E = cfg.P, cfg.Q, cfg.S, cfg.C, cfg.E
    ata = acc.ata.reshape(P, Q, S, C, E, NT, NT); atb = acc.atb.reshape(P, Q, S, C, E, NT); cnt = acc.cnt.reshape(P, Q, S, C, E)
    a4, b4, n4 = ata.sum(4), atb.sum(4), cnt.sum(4)                    # (P,Q,S,C)
    a2, b2, n2 = a4.sum((2, 3)), b4.sum((2, 3)), n4.sum((2, 3))        # (P,Q)
    a1, b1, n1 = a2.sum(1), b2.sum(1), n2.sum(1)                        # (P)
    filt = np.zeros((P, Q, S, C, E, NT))
    one = np.ones(NT)
    for p in range(P):
        for q in range(Q):
            M2 = a2[p, q] + prior * a1[p] / max(n1[p], 1); v2 = b2[p, q] + prior * b1[p] / max(n1[p], 1); k2 = n2[p, q] + prior
            for si in range(S):
                for c in range(C):
                    M4 = a4[p, q, si, c] + prior * M2 / k2; v4 = b4[p, q, si, c] + prior * v2 / k2; k4 = n4[p, q, si, c] + prior
                    for e in range(E):
                        if E == 1:
                            M, v = M4, v4
                        else:
                            M = ata[p, q, si, c, e] + prior * M4 / k4; v = atb[p, q, si, c, e] + prior * v4 / k4
                        filt[p, q, si, c, e] = _shrunk_solve(M, v, one, ridge)
    return filt

def train(train_cases, cfg, frac=0.25, seed=0):
    calibrate(train_cases, cfg)
    acc = Accumulator(cfg)
    rng = np.random.default_rng(seed)
    for cap, ref, A in train_cases:
        accumulate_case(acc, cap, ref, cfg, frac, rng)
    return acc

# ------------------------------------------------------------------------------------------ inference
def apply(cap, W, filt, cfg, chunk=64, phase_blend=True):
    cap_ws = to_space(cap, cfg.space)
    H = cap.shape[0]
    L = luma_ws(cap_ws)
    th, st, co, ph, j0 = features(L, W, cfg)
    P = cfg.P
    qa = np.minimum((th / np.pi * cfg.Q).astype(int), cfg.Q - 1)
    qs = np.searchsorted(cfg.s_edges, st)
    qc = np.searchsorted(cfg.c_edges, co)
    qe = np.searchsorted(cfg.E_edges, edge_offset(L, W)) if cfg.E > 1 else np.zeros_like(qa)
    F = filt.reshape(P, cfg.Q, cfg.S, cfg.C, cfg.E, cfg.NT)
    pad = np.pad(cap_ws, ((cfg.RH, cfg.RH), (cfg.cols, cfg.cols), (0, 0)), mode="edge")
    if phase_blend:
        # blend the two nearest phase bins (bin centres at (i + 0.5) / P)
        pp = ph * P - 0.5
        p0 = np.floor(pp).astype(int); fr = pp - p0
        p1 = np.clip(p0 + 1, 0, P - 1); p0 = np.clip(p0, 0, P - 1)
    else:
        p0 = np.minimum((ph * P).astype(int), P - 1); p1 = p0; fr = np.zeros_like(ph)
    out = np.zeros((H, W, cap.shape[2]))
    for r0 in range(0, H, chunk):
        rows = np.arange(r0, min(H, r0 + chunk))
        win = windows(cap_ws, j0, rows, cfg, pad)               # (R, W, NT, ch)
        sl = slice(r0, rows[-1] + 1)
        h = (F[p0[sl], qa[sl], qs[sl], qc[sl], qe[sl]] * (1 - fr[sl])[..., None]
             + F[p1[sl], qa[sl], qs[sl], qc[sl], qe[sl]] * fr[sl][..., None])
        out[sl] = np.einsum("rwtc,rwt->rwc", win, h)
    return from_space(out, cfg.space)
