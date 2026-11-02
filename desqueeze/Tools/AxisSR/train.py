"""
Train the cross-axis detail model (learned filter bank + detail network) and export it for the app.

    python3 train.py                 # trains and writes desqueeze-axissr.bin into Assets/StreamingAssets
    AXISSR_DATA=~/photos python3 train.py --hidden 32,24 --epochs 14

Needs numpy, scipy, pillow. Images: see README.md in this folder.
"""
import os, sys, time, pickle, argparse, numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from common import *
import degrade, learned as L, mlp, export_model

class LazyPairs:
    """List-like view over cached training pairs; loads one at a time (they don't all fit in memory)."""
    def __init__(self, files): self.files = files
    def __len__(self): return len(self.files)
    def _load(self, f):
        d = np.load(f); return (d["cap"].astype(np.float64), d["ref"].astype(np.float64), float(d["A"]))
    def __getitem__(self, i):
        return LazyPairs(self.files[i]) if isinstance(i, slice) else self._load(self.files[i])
    def __iter__(self):
        for f in self.files: yield self._load(f)

def make_pairs(per_image=6, seed=0, amin=1.15, amax=2.0):
    """Simulated anamorphic captures with random camera processing (blur, sharpening, noise, JPEG) and ratios."""
    out = f"{WORK}/pairs"
    os.makedirs(out, exist_ok=True)
    rng = np.random.default_rng(seed)
    files = []
    for i, p in enumerate(list_images("train")):
        gt0 = None
        for k in range(per_image):
            f = f"{out}/{i:03d}_{k}.npz"
            A = rng.uniform(amin, amax) if rng.random() < 0.8 else rng.choice([1.33, 1.5, 1.6, 2.0])
            flip_h, flip_v, prof_seed = rng.random() < 0.5, rng.random() < 0.5, rng.integers(1 << 30)
            if not os.path.exists(f):
                gt0 = load_gt(p) if gt0 is None else gt0
                g = gt0[:, ::-1] if flip_h else gt0
                g = g[::-1] if flip_v else g
                g = crop_for_factor(g, A)
                r2 = np.random.default_rng(prof_seed)
                cap, ref = degrade.make_pair(g, A, degrade.random_profile(r2), r2)
                np.savez(f, cap=cap.astype(np.float32), ref=ref.astype(np.float32), A=A)
            files.append(f)
    return LazyPairs(files)

if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--hidden", default="32,24")
    ap.add_argument("--epochs", type=int, default=14)
    ap.add_argument("--frac", type=float, default=0.03, help="fraction of output pixels sampled for the network")
    ap.add_argument("--out", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..",
                                                  "Assets", "StreamingAssets", "desqueeze-axissr.bin"))
    a = ap.parse_args()
    t = time.time()
    pairs = make_pairs()
    print(f"{len(pairs)} training pairs ({time.time()-t:.0f}s)", flush=True)
    cfg = L.Config(space="gamma", phases=16)
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
                        name=f"cross-axis v1 ({a.hidden})")
    print(f"done ({time.time()-t:.0f}s)")
