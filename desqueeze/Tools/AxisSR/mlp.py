"""
Tiny MLP residual corrector on top of the learned linear filters (numpy, manual backprop, Adam).
Input per output sample (one channel): the 7x6 input window, contrast-normalised (NNEDI-style),
plus the sub-pixel phase; it predicts the normalised residual between the linear-filter output and the target.
"""
import os, sys, time, pickle, numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import *
import learned as L

def sample_data(cases, bank, frac=0.04, seed=0, with_scale=False):
    b = bank; cfg = b["cfg"]; filt = b["filt"]
    rng = np.random.default_rng(seed)
    Xs, Ts, Ss = [], [], []
    for cap, ref, A in cases:
        ws = to_space(cap, cfg.space); rws = to_space(ref, cfg.space)
        H, W = ref.shape[:2]
        lin = to_space(L.apply(cap, W, filt, cfg), cfg.space)
        Lm = L.luma_ws(ws)
        c, j0, ph = L.geometry(cap.shape[1], W)
        pad = np.pad(ws, ((cfg.RH, cfg.RH), (cfg.cols, cfg.cols), (0, 0)), mode="edge")
        n = int(H * W * frac)
        ys = rng.integers(3, H - 3, n); xs = rng.integers(4, W - 4, n); ch = rng.integers(0, 3, n)
        rr = ys[:, None, None] + np.arange(cfg.rows)[None, :, None]
        cc = j0[xs][:, None, None] + cfg.cols - cfg.CL + np.arange(cfg.cols)[None, None, :]
        win = pad[rr, cc, ch[:, None, None]].reshape(n, cfg.NT)
        mu = win.mean(1, keepdims=True); sd = win.std(1, keepdims=True) + 1e-3
        feats = [(win - mu) / sd, (ph[xs][:, None] - 0.5) * 2, (lin[ys, xs, ch][:, None] - mu) / sd]
        if with_scale: feats.append(np.full((n, 1), (W / cap.shape[1] - 1.5) * 2))
        x = np.concatenate(feats, 1)
        t = (rws[ys, xs, ch] - lin[ys, xs, ch]) / sd[:, 0]
        Xs.append(x.astype(np.float32)); Ts.append(t.astype(np.float32)); Ss.append(sd[:, 0].astype(np.float32))
    return np.concatenate(Xs), np.concatenate(Ts), np.concatenate(Ss)

class MLP:
    def __init__(self, nin, hidden=(48, 32), seed=0):
        rng = np.random.default_rng(seed)
        sizes = [nin] + list(hidden) + [1]
        self.W = [rng.normal(0, np.sqrt(2 / a), (a, b)).astype(np.float32) for a, b in zip(sizes[:-1], sizes[1:])]
        self.b = [np.zeros(b, np.float32) for b in sizes[1:]]
        self.W[-1] *= 0.1

    def forward(self, x, keep=False):
        acts = [x]; h = x
        for i in range(len(self.W)):
            z = h @ self.W[i] + self.b[i]
            h = np.where(z > 0, z, 0.05 * z) if i < len(self.W) - 1 else z
            acts.append(h)
        return (h[:, 0], acts) if keep else h[:, 0]

    def train(self, X, T, S, Xv, Tv, Sv, epochs=4, bs=2048, lr=2e-3):
        m = [np.zeros_like(w) for w in self.W] + [np.zeros_like(b) for b in self.b]
        v = [np.zeros_like(w) for w in self.W] + [np.zeros_like(b) for b in self.b]
        step = 0; n = len(X); rng = np.random.default_rng(1)
        wts = (S / S.mean()) ** 2                        # weight so the loss equals MSE in output units
        for ep in range(epochs):
            perm = rng.permutation(n); t0 = time.time()
            for k in range(0, n - bs + 1, bs):
                idx = perm[k:k + bs]
                x, t, wt = X[idx], T[idx], wts[idx]
                y, acts = self.forward(x, keep=True)
                g = (2 * wt * (y - t) / bs)[:, None].astype(np.float32)
                grads_W, grads_b = [], []
                for i in reversed(range(len(self.W))):
                    grads_W.insert(0, acts[i].T @ g); grads_b.insert(0, g.sum(0))
                    if i > 0:
                        g = g @ self.W[i].T
                        g = g * np.where(acts[i] > 0, 1.0, 0.05).astype(np.float32)
                step += 1
                total = epochs * (n // bs)
                lr_t = lr * min(1.0, step / 200) * (0.02 + 0.98 * 0.5 * (1 + np.cos(np.pi * min(1.0, step / total))))
                params = self.W + self.b; grads = grads_W + grads_b
                for j in range(len(params)):
                    m[j] = 0.9 * m[j] + 0.1 * grads[j]; v[j] = 0.999 * v[j] + 0.001 * grads[j] ** 2
                    mh = m[j] / (1 - 0.9 ** step); vh = v[j] / (1 - 0.999 ** step)
                    params[j] -= (lr_t * mh / (np.sqrt(vh) + 1e-8)).astype(np.float32)
            yv = self.forward(Xv)
            base = np.mean((Sv * Tv) ** 2); after = np.mean((Sv * (Tv - yv)) ** 2)
            print(f"epoch {ep}: {time.time()-t0:.0f}s  val MSE reduction {10*np.log10(base/after):+.3f} dB", flush=True)

def apply(cap, W, bank, net, chunk=32):
    cfg = bank["cfg"]
    ws = to_space(cap, cfg.space)
    H = cap.shape[0]
    lin = to_space(L.apply(cap, W, bank["filt"], cfg), cfg.space)
    c, j0, ph = L.geometry(cap.shape[1], W)
    pad = np.pad(ws, ((cfg.RH, cfg.RH), (cfg.cols, cfg.cols), (0, 0)), mode="edge")
    out = lin.copy()
    for r0 in range(0, H, chunk):
        rows = np.arange(r0, min(H, r0 + chunk))
        win = L.windows(ws, j0, rows, cfg, pad)             # (R, W, NT, 3)
        win = np.moveaxis(win, 3, 2).reshape(-1, cfg.NT)      # (R*W*3, NT)
        mu = win.mean(1, keepdims=True); sd = win.std(1, keepdims=True) + 1e-3
        phs = np.repeat(np.tile(ph, len(rows)), 3)[:, None]
        l = lin[rows].reshape(-1, 1)
        feats = [(win - mu) / sd, (phs - 0.5) * 2, (l - mu) / sd]
        if net.W[0].shape[0] == cfg.NT + 3: feats.append(np.full((len(l), 1), (W / cap.shape[1] - 1.5) * 2))
        x = np.concatenate(feats, 1).astype(np.float32)
        r = net.forward(x)
        out[rows] = (l[:, 0] + sd[:, 0] * r).reshape(len(rows), W, 3)
    return from_space(out, cfg.space)

def apply_luma(cap, W, bank, net, chunk=32, flat_skip=0.0):
    """Run the network once on luma and transfer its correction to each channel by the channel's local
    regression slope against luma (guided-filter style). ~3x cheaper than per-channel."""
    cfg = bank["cfg"]
    ws = to_space(cap, cfg.space)
    H = cap.shape[0]
    lin = to_space(L.apply(cap, W, bank["filt"], cfg), cfg.space)
    c, j0, ph = L.geometry(cap.shape[1], W)
    pad = np.pad(ws, ((cfg.RH, cfg.RH), (cfg.cols, cfg.cols), (0, 0)), mode="edge")
    out = lin.copy()
    has_scale = net.W[0].shape[0] == cfg.NT + 3
    for r0 in range(0, H, chunk):
        rows = np.arange(r0, min(H, r0 + chunk))
        win = L.windows(ws, j0, rows, cfg, pad).reshape(-1, cfg.NT, 3)        # (N, NT, 3)
        wl = 0.299 * win[..., 0] + 0.587 * win[..., 1] + 0.114 * win[..., 2]   # (N, NT)
        lc = lin[rows].reshape(-1, 3)
        ll = 0.299 * lc[:, 0] + 0.587 * lc[:, 1] + 0.114 * lc[:, 2]
        mu = wl.mean(1, keepdims=True); sdr = wl.std(1, keepdims=True); sd = sdr + 1e-3
        feats = [(wl - mu) / sd, np.tile((ph - 0.5) * 2, len(rows))[:, None], (ll[:, None] - mu) / sd]
        if has_scale: feats.append(np.full((len(ll), 1), (W / cap.shape[1] - 1.5) * 2))
        r = net.forward(np.concatenate(feats, 1).astype(np.float32))
        dl = sd[:, 0] * r                                                       # luma correction
        dl = np.where(sdr[:, 0] < flat_skip, 0.0, dl)
        wc = win - win.mean(1, keepdims=True)
        k = (wc * (wl - mu)[..., None]).mean(1) / np.maximum(sdr ** 2, 1e-8)     # (N, 3) slope of channel vs luma
        k = np.where(sdr ** 2 < 1e-8, 1.0, k)
        out[rows] = (lc + k * dl[:, None]).reshape(len(rows), W, 3)
    return from_space(out, cfg.space)
