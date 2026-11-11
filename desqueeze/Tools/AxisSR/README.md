# AxisSR: training the cross-axis detail model

These scripts train `Assets/StreamingAssets/desqueeze-axissr.bin`, the model behind the app's
**Detail** widening. The app does not need them. Use them to retrain, for example on photos from your
own lens and camera.

```
pip install numpy scipy pillow
AXISSR_DATA=~/axissr-data AXISSR_WORK=~/axissr-work python3 train.py
```

The script writes the model straight into `Assets/StreamingAssets` (use `--out` to write it elsewhere). A full run takes
about 15 minutes on 2 cores and needs about 4 GB of RAM. The pairs cache in `AXISSR_WORK` takes about 3 GB of disk.
Options: `--hidden 32,24` (network size; 64,48 gained about 0.1 dB for roughly 2.5x the network cost), `--epochs 14`,
`--frac 0.03` (share of output pixels sampled for the network).

## Images

`AXISSR_DATA` should contain:

| Folder | Source | Used for |
|---|---|---|
| `SelfExSR/data/BSD100/image_SRF_2/*_HR.png` | [jbhuang0604/SelfExSR](https://github.com/jbhuang0604/SelfExSR) | training (100) |
| `SelfExSR/data/Urban100/image_SRF_2/*_HR.png` | same repository | 1-50 training, 51-100 test |
| `Kodak-Lossless-True-Color-Image-Suite/PhotoCD_PCD0992/*.png` | [MohamedBakrAli/Kodak-Lossless-True-Color-Image-Suite](https://github.com/MohamedBakrAli/Kodak-Lossless-True-Color-Image-Suite) | test (24) |
| `hires/*` | `Kodak_examples_uncompressed/*.dib` from the same Kodak repository | test |

Images are area-reduced to at most 1024 px on the long side, in linear light, which also removes
scan grain. Any sharp photos work for training: to use your own, replace `list_images("train")` in
`common.py`.

## What the scripts do

* `degrade.py` simulates anamorphic captures. Each ground-truth image is squeezed horizontally by area
  integration in linear light, which is what a sensor does. Then a random camera profile is applied:
  * optical/OLPF blur of 0.15-0.6 px. It is applied to the reference too: the model should undo the
    squeeze, not the lens.
  * in-camera unsharp masking, also applied to the reference;
  * noise;
  * JPEG at quality 84-97.

  Each image yields 6 pairs, with factors drawn from 1.15-2.0 plus the 1.33/1.5/1.6/2.0 presets.
  Images are randomly flipped. `TEST_CONDITIONS` holds the fixed conditions used for evaluation.
* `learned.py` trains the filter bank. For every output pixel it computes the sub-pixel phase, plus the
  angle, strength and coherence of the local structure tensor (σ = 1, input pixels). It then solves a
  least-squares problem for each bucket, 16 phases × 16 angles × 3 strengths × 3 coherences. Each
  bucket gets a 7-row × 6-column filter whose taps sum to 1, with ridge regularisation. Rare buckets
  are shrunk hierarchically: toward their (phase, angle) parent, and that toward the phase-only filter.
* `mlp.py` trains the detail network (45 → 32 → 24 → 1, leaky ReLU). Its inputs are the window
  normalised by its mean and standard deviation, the phase, the filter result and the scale factor. It
  predicts the remaining error in units of the window's standard deviation. In the app it runs once on
  luma, and each colour channel receives the correction scaled by its local regression slope against luma.
* `export_model.py` writes the binary that `AxisSRModel.Load` reads. The format is documented at the
  top of that file.

To check a model with the C# core: `Tools/Bench` → `axissr in.png out.png <outWidth> model.bin [net 0|1] [flatSkip] [X|Y]`.

## Experiment: a sub-pixel shift model

`train_shift.py` trains the same architecture to move fully sampled rows by fractions of a pixel (scale 0.94-1.08),
for the column pass of the two-pass lens-distortion correction. It takes about 2 minutes. It did not beat Lanczos 3
(see the main README, "Second round"), so the app does not use it. `SHIFT_CLEAN=1` trains it on clean captures only.
