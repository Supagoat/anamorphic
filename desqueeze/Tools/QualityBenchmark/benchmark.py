#!/usr/bin/env python3
"""
Desqueeze quality benchmark.

Simulates anamorphic captures from ground-truth photos, de-squeezes them with every algorithm /
light-space combination through the C# core (Tools/Bench), and scores the results against the
ground truth with PSNR, SSIM and GMSD.

How the simulation works, for a factor A:
  1. A ground-truth photo G is cropped so its width is an exact multiple of A.
  2. The "capture" S is G squeezed horizontally by A (area-averaged in linear light = an ideal
     sensor, or Lanczos = a sharp, no-AA-filter sensor).
  3. S is de-squeezed at width shares t = 1 (width only), 0.5 (balanced) and 0 (height only).
  4. t = 1 is compared with G itself (an exact reference). t < 1 is compared with G area-reduced
     to the same size in linear light (what a lower-resolution sensor would have recorded).

Usage:
  python3 Tools/QualityBenchmark/benchmark.py --images /path/to/photos --work /tmp/desq-bench \
      [--bench "dotnet run -c Release --project Tools/Bench --"] [--quick]

Requires: numpy, pillow, scipy, scikit-image.
"""
import argparse, collections, json, os, shlex, subprocess, sys
import numpy as np
from PIL import Image
from scipy import ndimage
from skimage.metrics import structural_similarity

FACTORS = {1.33: 100, 1.5: 2, 2.0: 1}   # factor -> width multiple that makes width*factor an integer
SHARES = [1.0, 0.5, 0.0]
CAPTURES = {"area": Image.BOX, "sharp": Image.LANCZOS}
KERNELS = ["MagicKernelSharp2021", "Lanczos3", "Lanczos4", "Spline64", "Spline36", "CatmullRom",
           "Mitchell", "Bilinear", "Box", "EwaLanczosSharp"]
LIGHT_COMBOS = [("Gamma", "Sigmoidal"), ("Linear", "Sigmoidal"), ("Linear", "Linear"), ("Gamma", "Gamma")]


def to_lin(u8):
    x = u8.astype(np.float64) / 255
    return np.where(x <= 0.04045, x / 12.92, ((x + 0.055) / 1.055) ** 2.4)


def to_srgb8(l):
    l = np.clip(l, 0, 1)
    s = np.where(l <= 0.0031308, l * 12.92, 1.055 * l ** (1 / 2.4) - 0.055)
    return np.clip(np.floor(s * 255 + 0.5), 0, 255).astype(np.uint8)


def resize_linear(u8, w, h, flt):
    lin = to_lin(u8)
    chans = [lin] if lin.ndim == 2 else [lin[..., c] for c in range(lin.shape[2])]
    out = [np.array(Image.fromarray(c.astype(np.float32), "F").resize((w, h), flt)) for c in chans]
    return to_srgb8(out[0] if lin.ndim == 2 else np.stack(out, 2))


def rnd(x):
    return int(np.floor(x + 0.5))


def gmsd(a, b):
    def luma(x):
        return x if x.ndim == 2 else 0.299 * x[..., 0] + 0.587 * x[..., 1] + 0.114 * x[..., 2]
    a = luma(a.astype(np.float64)); b = luma(b.astype(np.float64))
    k = np.array([[1 / 3, 0, -1 / 3]] * 3)
    ga = np.hypot(ndimage.convolve(a, k), ndimage.convolve(a, k.T))
    gb = np.hypot(ndimage.convolve(b, k), ndimage.convolve(b, k.T))
    return ((2 * ga * gb + 170.0) / (ga ** 2 + gb ** 2 + 170.0)).std()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--images", required=True, help="folder of ground-truth photos (png/jpg)")
    ap.add_argument("--work", required=True, help="scratch folder")
    ap.add_argument("--bench", default="dotnet run -c Release --project Tools/Bench --")
    ap.add_argument("--max-side", type=int, default=1536, help="ground truths are area-reduced to at most this size")
    ap.add_argument("--quick", action="store_true", help="fewer variants")
    args = ap.parse_args()

    for d in ["gt", "cap", "ref", "out"]:
        os.makedirs(os.path.join(args.work, d), exist_ok=True)
    kernels = KERNELS[:4] + ["EwaLanczosSharp"] if args.quick else KERNELS
    combos = LIGHT_COMBOS[:2] if args.quick else LIGHT_COMBOS

    jobs, meta = [], []
    for fn in sorted(os.listdir(args.images)):
        if not fn.lower().endswith((".png", ".jpg", ".jpeg")):
            continue
        g = np.array(Image.open(os.path.join(args.images, fn)).convert("RGB"))
        h, w = g.shape[:2]
        k = max(1.0, max(w, h) / args.max_side)
        if k > 1:
            g = resize_linear(g, rnd(w / k), rnd(h / k), Image.BOX)
        name = os.path.splitext(fn)[0]
        H, W = g.shape[:2]
        for A, m in FACTORS.items():
            sw = (int(W / A) // m) * m
            wc = rnd(sw * A)
            x0 = (W - wc) // 2
            gc = g[:, x0:x0 + wc]
            for cap, flt in CAPTURES.items():
                spath = f"{args.work}/cap/{name}_{A}_{cap}.png"
                Image.fromarray(resize_linear(gc, sw, H, flt)).save(spath)
                for t in SHARES:
                    ow, oh = rnd(sw * A ** t), rnd(H * A ** (t - 1))
                    rpath = f"{args.work}/ref/{name}_{A}_{t}.png"
                    if not os.path.exists(rpath):
                        Image.fromarray(gc if t == 1.0 else resize_linear(gc, ow, oh, Image.BOX)).save(rpath)
                    for kern in kernels:
                        for red, enl in combos:
                            v = f"{kern}|{red}|{enl}"
                            o = f"{args.work}/out/{name}_{A}_{cap}_{t}_{kern}_{red}_{enl}.png"
                            jobs.append("\t".join(map(str, [spath, o, ow, oh, kern, red, enl, 0])))
                            meta.append(dict(img=name, A=A, cap=cap, t=t, variant=v, out=o, ref=rpath))
    manifest = os.path.join(args.work, "manifest.tsv")
    open(manifest, "w").write("\n".join(jobs) + "\n")
    print(f"{len(jobs)} jobs; running the C# core...", flush=True)
    subprocess.run(shlex.split(args.bench) + ["batch", manifest], check=True)

    rows = []
    for m in meta:
        o = np.array(Image.open(m["out"]).convert("RGB")); r = np.array(Image.open(m["ref"]).convert("RGB"))
        mse = ((o.astype(np.float64) - r) ** 2).mean()
        rows.append(dict(m, psnr=99.0 if mse == 0 else 10 * np.log10(255 ** 2 / mse),
                         ssim=structural_similarity(r, o, channel_axis=2, data_range=255), gmsd=gmsd(r, o)))
    json.dump(rows, open(os.path.join(args.work, "scores.json"), "w"))

    for t in SHARES:
        for cap in CAPTURES:
            g = collections.defaultdict(list)
            for r in rows:
                if r["t"] == t and r["cap"] == cap:
                    g[r["variant"]].append(r)
            print(f"\n== width share t={t}, {cap} capture  (higher PSNR/SSIM and lower GMSD are better)")
            for v, rs in sorted(g.items(), key=lambda kv: -np.mean([x["psnr"] for x in kv[1]])):
                print(f"{v:48s} PSNR {np.mean([x['psnr'] for x in rs]):7.3f}  SSIM {np.mean([x['ssim'] for x in rs]):.4f}"
                      f"  GMSD {np.mean([x['gmsd'] for x in rs]):.4f}")


if __name__ == "__main__":
    sys.exit(main())
