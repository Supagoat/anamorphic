# Desqueeze

A Unity (6.3) desktop app that de-squeezes photos shot through anamorphic lenses: open an image,
inspect it pixel-perfectly, choose the anamorphic factor and how the correction is split between
widening and shortening, apply it, and save as maximum-quality JPEG or PNG. Widening uses a
purpose-built single-axis upscaler that rebuilds detail from the fully sampled axis. Shortening uses a
benchmarked high-quality kernel. A batch mode does the same for every image in a folder.

All image processing lives in a plain C# library (`Assets/Desqueeze/Core`, no Unity references),
so it can be tested outside Unity and later given a GPU path behind the same API.

---

## Running it

1. Open this folder in Unity 6.3 (6000.3.11f1).
2. Press **Play** in any scene (the template's `SampleScene` is fine). The app creates itself at
   start-up; nothing needs adding to a scene.
3. To build: **File > Build Profiles**, pick macOS (or Windows/Linux), **Build**.

On first load an editor script sets desktop-friendly player settings (windowed, resizable,
1600 x 1000, keeps running in the background so batches don't pause). Re-apply any time with
**Desqueeze > Apply Recommended Player Settings**.

> The UI is IMGUI (drawn from code), so there are no scene, prefab or UI assets to wire up, and it
> works with the project's "Input System only" setting.

---

## Interactive mode

| Action | How |
|---|---|
| Open | **Open…**, or type/paste a path in the toolbar and press Enter, or Ctrl/Cmd+O |
| Zoom | Mouse wheel (zooms at the cursor), **+ / −**, **Fit**, **1:1**, keys `+` `-` `1` `F` |
| Pan | Drag (left or middle button), arrow keys (Shift = faster) |
| Toggle original / result | **Original / De-squeezed** buttons or Space. Zoom and the relative position are kept, so you compare the same spot |
| Apply | **Apply De-squeeze** (Ctrl/Cmd+Enter) - full resolution, on all cores, cancellable |
| Save | Choose **JPG** or **PNG**, then **Save As…** (Ctrl/Cmd+S). Applies first if the settings changed |

**Pixel-perfect viewing.** Magnification is always a whole number (each image pixel becomes an
exact N x N block of screen pixels, point-sampled, at whole-pixel offsets). Zooming out uses exact
1/N reductions: every screen pixel is the linear-light average of an N x N block, computed on
the CPU, never a GPU-filtered approximation. Large images are tiled, so the GPU texture limit doesn't
apply. The status bar shows the pixel under the cursor and its values.

## Batch mode

Pick an input and an output folder. Every `.jpg/.jpeg/.png` is processed with the current settings
and written **in its own format**: JPEG in gives maximum-quality JPEG out, PNG in gives PNG out (16-bit stays
16-bit). Options: include subfolders (mirrors the structure), overwrite existing, file-name suffix.
The originals can never be overwritten: using the input folder as output requires a suffix.
Hidden files and macOS `._` resource forks are ignored.

---

## The controls

**Anamorphic factor.** Preset buttons come from the config file (1.33x, 1.5x, 1.6x; default
1.5x), plus **Custom** for any value in the configured range.

**Height / width split (one slider).** Any output whose aspect is *factor x source aspect* is a
correct de-squeeze. The slider chooses how much comes from widening versus shortening, and is locked so
`widthScale / heightScale = factor` always holds:

```
widthScale  = factor ^ t
heightScale = factor ^ (t - 1)          t = slider position, 0..1

t = 0    Height only   width x1,           height x1/factor   (e.g. 1.33x -> height x0.752)
t = 0.5  Balanced      width x sqrt(f),    height x1/sqrt(f)  (same total pixel count)
t = 1    Width only    width x factor,     height x1          (keeps every captured row)
```

The panel shows both scale factors and, with an image open, the exact output size, aspect and
megapixels. Rounding to whole pixels changes the aspect by less than 0.05%.

**Widening.** How the stretched axis is enlarged:

* **Detail** (default): cross-axis detail reconstruction (described below). It rebuilds horizontal
  detail from the fully sampled vertical axis, so it is sharper than any interpolation kernel. It is also slower.
* **Detail (fast)**: the learned filters only, without the detail network. You get about half the gain in under half the time.
* **Kernel**: classic interpolation with the selected kernel (the only option before).

Widening only adds resolution when the slider is toward **Width only**. At *Width only*, every captured
row is kept and all of the correction is widening. With a split, the image is widened by `factor ^ t` and
the other axis is then shortened with the selected kernel.

**Resampling.** Algorithm list, plus **Advanced**: the light space used for the shrinking axis and
for the enlarging axis, and optional anti-ringing for the enlarged axis. With **Detail**, the kernel
is only used for shortening.

---

## Choosing "the best" kernel - what was measured

The kernels are used for shortening, and also for widening when **Widening** is set to **Kernel**.
Detail widening is covered in the next section.

Rather than assume, I benchmarked the candidates: windowed-sinc (Lanczos 3/4), Panorama Tools
splines (36/64), Magic Kernel Sharp 2021 (Costella, used by Facebook/Instagram and libvips),
EWA Lanczos Sharp (the radial Jinc filter mpv and ImageMagick use for their best upscaling), cubics,
bilinear and box. Each was also tested in linear, gamma and sigmoidal light.

Method (`Tools/QualityBenchmark/benchmark.py`): 16 ground-truth photos and textures, including
full-resolution Kodak PhotoCD scans. Each was squeezed horizontally by 1.33x, 1.5x and 2x to
simulate a capture (two sensor models: an ideal area-integrating sensor, and a sharp no-AA-filter
one), then de-squeezed. **Width-only results are scored against the exact original** (no reference
filter bias). Balanced and height-only results are scored against the original area-reduced in linear
light. Metrics: PSNR, SSIM, GMSD.

Mean PSNR (dB) with the chosen light spaces (shrink in gamma, enlarge in sigmoidal):

| Kernel | Width only (area / sharp) | Balanced (area / sharp) | Height only (area / sharp) | Average |
|---|---|---|---|---|
| **Magic Kernel Sharp 2021** | **37.10** / 38.75 | **35.57** / **36.25** | **43.37** / 38.55 | **38.26** |
| Spline 64 | 37.05 / 38.86 | 35.42 / 36.10 | 42.68 / 38.29 | 38.07 |
| Lanczos 3 | 37.08 / 38.83 | 35.39 / 36.02 | 42.61 / 38.22 | 38.02 |
| Lanczos 4 | 37.04 / **38.94** | 35.35 / 36.02 | 42.36 / 38.11 | 37.97 |
| Catmull-Rom | 36.37 / 37.63 | 35.46 / 36.14 | 43.21 / **38.59** | 37.90 |

Findings:

* **Light space matters more than the kernel.** Enlarging in *sigmoidal* light beat linear light by
  0.6-0.9 dB for every kernel (sharp filters' overshoot is compressed instead of becoming halos).
  Shrinking in *gamma* light beat linear light by 0.25-1.0 dB: linear-light reduction with a sharp
  kernel crushes the dark side of high-contrast edges to black (about 39 sRGB levels of halo). This
  held even though the reference itself was made in linear light.
* **Magic Kernel Sharp 2021** has the best average and is first or near-first in every scenario,
  so it is the default. Lanczos 4 is marginally sharper (+0.19 dB) for pure widening of very crisp
  (no AA filter) captures and is one click away.
* **EWA Lanczos Sharp scored 0.3-0.5 dB lower** for de-squeezing. It is a 2-D filter, so it also softens
  the axis that isn't being resized. It's included for comparison.
* An axis that isn't resized (e.g. height at "Width only") passes through bit-exact.

Re-run the benchmark on your own images:
`python3 Tools/QualityBenchmark/benchmark.py --images ~/some/photos --work /tmp/desq-bench`
(needs the .NET 8 SDK for `Tools/Bench`, plus numpy, pillow, scipy, scikit-image).

---

## Widening that adds detail: cross-axis reconstruction

Shortening throws captured rows away. Widening keeps them all, but a kernel can only interpolate
between the samples of each row, so the widened image is soft. **Detail** widening is a single-axis
upscaler built for anamorphic captures. It works up to about 2x and does best at 1.6x and below. It rebuilds
horizontal detail mainly from the information in the axis that wasn't squeezed.

### Why the other axis helps

A horizontally squeezed capture is sampled at full density vertically and at 1/factor density
horizontally. Lanczos and Magic Kernel treat each row as an independent 1-D signal, so detail beyond
one row's sampling limit is gone. But any edge that isn't exactly vertical crosses consecutive rows at
different horizontal sub-pixel offsets. Together, the rows above and below sample that edge more finely
than any single row does. This is the same principle as a slanted-edge MTF measurement. The local edge
angle, which the fully sampled vertical axis measures well, says how to combine them.

### The algorithm

For every output pixel, at input position *u*: column *j0* = floor(*u*), phase = *u* − *j0*.

1. **Local geometry.** The 2-D structure tensor of the input (gradients smoothed with a Gaussian, σ = 1 px) is
   interpolated at *u*. It gives the edge angle (16 bins), strength (3) and coherence (3).
2. **Cross-axis learned filter.** A **7-row × 6-column** filter is looked up by angle, strength and
   coherence. It takes taps from the 3 rows above and below, not just the pixel's own row, and is
   blended between the two nearest of 16 sub-pixel phases. The 2,304 filters are solved by
   constrained least squares on simulated anamorphic captures: sensor area integration, lens blur,
   in-camera sharpening, noise and JPEG, at factors 1.15-2.0. The idea comes from RAISR [1], reworked for one
   axis. RAISR does integer 2-D upscales with square patches. Here the sub-pixel phase is a bucket
   dimension, because a non-integer one-axis scale has a continuum of phases, and the window is tall
   so it reaches across rows.
3. **Detail network.** A small network (45 → 32 → 24 → 1, leaky ReLU) predicts the filter's remaining
   error, the predictor idea of NNEDI3 [2]. Its inputs are the same window (contrast-normalised), the phase, the scale and the
   filter's result. It runs once, on luma. Each colour channel gets the correction scaled by its
   local regression slope against luma, as a guided filter [3] does, so colour edges stay aligned at a
   third of the cost. Flat areas (window standard deviation < 0.02) skip the network.
4. **Shortening, if any.** When the slider also shortens the other axis, the widened image is kept at 16 bits and
   then shortened with the selected kernel. A vertical squeeze is transposed, widened, and
   transposed back.

### Results

The held-out test set has 77 images: Kodak (24), Urban100 51-100, and 3 Kodak high-resolution scans.
None of them was used for training. Each was squeezed at 4 factors under 4 capture conditions, which
gives 1,232 cases. The slider was at *Width only*, and results are scored against the ground truth. Higher PSNR and
SSIM are better. Lower GMSD is better.

| Capture simulation | Lanczos 4 | MKS 2021 | Detail (fast) | **Detail** | Detail vs Lanczos 4 |
|---|---|---|---|---|---|
| Sensor area integration | 32.41 | 32.17 | 33.31 | **34.17** | +1.76 dB |
| + soft lens / OLPF (0.4 px) | 33.57 | 33.30 | 34.67 | **35.61** | +2.04 dB |
| + soft lens + in-camera sharpening | 29.86 | 29.64 | 30.72 | **31.41** | +1.55 dB |
| + soft lens + JPEG quality 90 | 31.06 | 30.94 | 31.61 | **32.14** | +1.08 dB |
| All cases, PSNR | 31.72 | 31.51 | 32.58 | **33.33** | +1.61 dB |
| All cases, PSNR on edge pixels | 26.31 | 26.00 | 27.25 | **28.47** | +2.16 dB |
| All cases, SSIM | 0.9472 | 0.9463 | 0.9541 | **0.9570** | |
| All cases, GMSD | 0.0632 | 0.0652 | 0.0557 | **0.0507** | −20% |

| Factor | Lanczos 4 | Detail (fast) | **Detail** | Gain |
|---|---|---|---|---|
| 1.33x | 33.49 | 34.46 | **35.53** | +2.04 dB |
| 1.5x | 32.21 | 33.17 | **33.98** | +1.77 dB |
| 1.6x | 31.51 | 32.42 | **33.17** | +1.66 dB |
| 2x | 29.68 | 30.25 | **30.65** | +0.97 dB |

* **+1.6 dB means about 31% less squared error.** The gain is concentrated on edges and fine texture,
  which is where widening looks soft.
* **Detail beat Lanczos 4 in 98.5% of the cases.** The exceptions were the three smooth high-resolution
  scans. Their average gain shrinks to +0.2 dB, and one of them was up to 1 dB worse when in-camera
  sharpening was simulated. That was above 41 dB, where differences are very hard to see.
* **The gain shrinks above 1.6x.** At 2x there is more missing detail than the neighbouring rows can supply.
* **No sign that design choices overfit.** 320 of the cases use images from the 20-image development
  set that guided those choices. The other 912 cases were never looked at, and gained +1.58 dB, against
  +1.69 dB on the development images.
* **Other conditions** (development set, all factors):
  * Sensor noise: Lanczos 4 33.56, Detail 35.26 (+1.70 dB).
  * Stress test, a capture made with an ideal sinc (Lanczos) filter: Lanczos 4 35.20, Detail 34.37 (−0.83 dB).
    Real sensors integrate over the pixel area, so this only happens when the source has already
    been sinc-resampled. In that case, use **Kernel**.

Two held-out examples (1.5x, 3x nearest-neighbour zoom), in `Tools/AxisSR/examples`:

![Urban100 crop](Tools/AxisSR/examples/crop_urban.png)
![Kodak crop](Tools/AxisSR/examples/crop_kodak08.png)

### What was tried, and what didn't work

The table uses the development set (20 images), 1.5x, mean of the plain-sensor and soft-lens captures.

| Approach | PSNR (dB) | vs Lanczos 4 |
|---|---|---|
| Lanczos 4, sigmoidal (best kernel) | 35.60 | — |
| Magic Kernel Sharp 2021 | 35.26 | −0.34 |
| Classic edge-directed: average along the detected edge across rows (ELA/NEDI-style), best of 3 tunings | 35.20 | −0.40 |
| Learned filters, own row only (1 × 6), chosen by 2-D edge angle | 36.72 | +1.12 |
| Learned filters, 7 rows × 6 (cross-axis) | 36.95 | +1.35 |
| Same, plus 16 blended phases | 37.00 | +1.40 |
| Finer angle bins (32, 48), other tensor scales, 9-row window | ≤ 37.00 | no gain |
| New: fit a sub-pixel step edge across the rows, then transfer its vertical edge profile to the horizontal axis | 37.15 | +1.55 (+0.07 over the filters it refined) |
| New: sub-pixel edge position as an extra bucket dimension | 37.11 | +1.51 (+0.03) |
| Flip test-time augmentation (4 passes) | 37.09 | +1.49 (+0.09, at 4x the cost) |
| **Learned filters + detail network** | **38.21** | **+2.61** |

What the table shows:

* Classic edge-directed interpolation, which averages along the edge across rows, is worse than Lanczos 4.
  Hand-made rules can't weigh noise, blur and aliasing correctly. Learned weights can.
* A learned filter confined to the pixel's own row already gains 1.1 dB. It knows the capture blur and
  uses the 2-D edge angle. Reaching across rows adds about 0.25 dB on average, and 0.4-0.55 dB on edges
  10-50° from vertical, where rows add phase diversity. Near-vertical edges remain the hardest: they
  account for about 40% of the remaining error, and the rows can't help there.
* The step-edge transfer model was spectacular on synthetic edges but added only 0.07 dB on photos,
  for a lot of code. It was dropped. So were the edge-position feature and test-time augmentation.
* The detail network is the largest single gain, but it learns the capture it was trained on. The
  38.21 network was trained on clean captures only, and it fell 1.6 dB *below* Lanczos on a
  sinc-filtered source. The shipped model is trained across random camera processing: blur,
  sharpening, noise and JPEG. That costs about 0.9 dB on these two clean conditions (37.29), but it
  stays ahead of Lanczos on every realistic condition in the results above. The shipped 32-24 luma network with flat
  skip costs another 0.2 dB, and does about 8x less network arithmetic than a 64-48 network per channel.

### Speed

CPU, C# (`AxisSR.cs`), 3000 × 2000 → 4500 × 2000, measured on 2 cores (.NET 8):

| Widening | Time |
|---|---|
| Kernel (Lanczos 4 or MKS 2021) | 0.27 s |
| Detail (fast) | 1.9 s |
| Detail | 5.0 s |

Time grows with the output pixel count and divides across cores. For a 24 MP photo on an 8-core Mac,
expect very roughly 10 s with **Detail**. That is an estimate, not a measurement: Unity's Mono runtime
in the editor is slower than .NET 8, and an IL2CPP build is faster.

[1] Romano, Isidoro, Milanfar, *RAISR: Rapid and Accurate Image Super Resolution*, IEEE TCI 2017.
[2] tritical, *NNEDI3* (neural-network edge-directed interpolation), VapourSynth/AviSynth filter.
[3] He, Sun, Tang, *Guided Image Filtering*, ECCV 2010 / TPAMI 2013.

## Output quality

* **JPEG** is always written at the maximum: quality 100 (every quantisation step = 1), 4:4:4 (no chroma
  subsampling), floating-point colour conversion and DCT straight from the source samples (16-bit
  sources aren't rounded to 8-bit first), and Huffman tables optimised per image. On test images it
  measured about 0.3 dB higher PSNR than libjpeg-turbo at quality 100 / 4:4:4, usually in a smaller file.
* **PNG**: lossless, adaptive row filters, parallel DEFLATE. Bit depth, gray/RGB and alpha are preserved.
* **Metadata**: EXIF (camera, lens, exposure, GPS, orientation) and ICC profiles are carried into
  the output (also across formats). The EXIF pixel-dimension tags are updated to the new size.
* Resampling is done in float on all cores. Colour is premultiplied by alpha, so transparent pixels don't bleed.

---

## Configuration

`Assets/StreamingAssets/desqueeze-config.json` (in a build: next to the app's data, or use the
**Config** button). Restart the app after editing. Fields you leave out keep their defaults.

```json
{
    "presets": [
        { "name": "1.33x", "factor": 1.33 },
        { "name": "1.5x",  "factor": 1.5 },
        { "name": "1.6x",  "factor": 1.6 },
        { "name": "1.8x",  "factor": 1.8 },          <- add more like this
        { "name": "2x",    "factor": 2.0 }
    ],
    "defaultFactor": 1.5,
    "customFactorMin": 1.0, "customFactorMax": 3.0,
    "defaultWidthShare": 0.5,                         // 1 = width only (keeps every captured row)
    "widenMethod": "Detail",                          // Detail | DetailFast | Kernel
    "squeezeAxis": "Horizontal",                      // Vertical = the lens squeezed the image vertically
    "defaultAlgorithm": "MagicKernelSharp2021",      // or Lanczos3, Lanczos4, Spline64, EwaLanczosSharp, ...
    "reduceLight": "Gamma",                           // Gamma | Linear | Sigmoidal
    "enlargeLight": "Sigmoidal",
    "antiRinging": 0.0,
    "defaultSaveFormat": "jpg",
    "batchFileSuffix": "_desqueezed",
    "uiScale": 0,                                     // 0 = automatic (2 on Retina)
    "maxZoom": 64
}
```

(The real file can't contain the `//` and `<-` notes shown above.)

The detail model is `Assets/StreamingAssets/desqueeze-axissr.bin`. If it is missing, the app says so
and widening falls back to the kernel. `Tools/AxisSR` retrains it.

`squeezeAxis` isn't in the UI yet. With `Vertical`, the slider's "width" side means the stretched
(vertical) axis, and the detail widener runs on the transposed image.

The app also remembers your last settings and folders. **Advanced > Reset to defaults** returns to
the config values, and **Desqueeze > Forget Remembered Settings** in the editor clears them.

---

## Project layout

```
Assets/Desqueeze/
  Core/                 Desqueeze.Core  - pure C# (.NET Standard 2.1), no Unity references
    Geometry/           DesqueezeGeometry (the split maths), DesqueezeSettings
    Resampling/         kernels, axis weights, separable + EWA resamplers, light-space conversion,
                        AxisSR (the cross-axis detail widener and its model loader)
    Color/              sRGB tables, sigmoidal curve
    Codecs/             PNG reader/writer, max-quality JPEG encoder, JPEG marker reader, EXIF patcher
    Imaging/            PixelImage (8/16-bit, 1-4 channels), display reductions, transpose
    Batch/              folder scanning and output naming
    Desqueezer.cs       entry point: Desqueezer.Process(image, settings, progress, cancel)
  App/                  Desqueeze.App   - Unity layer: IMGUI UI, viewer, dialogs, modes
  Editor/               player-settings helper and Desqueeze menu
  Tests/                EditMode tests (Window > General > Test Runner)
Assets/StreamingAssets/desqueeze-config.json
Assets/StreamingAssets/desqueeze-axissr.bin   the trained detail model (387 KB)
Tools/Bench/            command-line harness for the core (dotnet run -- selftest | axissr ...)
Tools/QualityBenchmark/ the kernel benchmark above
Tools/AxisSR/           training pipeline for the detail model (see its README)
```

JPEG pixels are decoded by Unity's `ImageConversion` on the main thread. Everything else
(PNG decode, resampling, encoding, file IO) runs on worker threads. Native file dialogs are used
in the Editor, on macOS (AppleScript), on Windows (PowerShell/WinForms) and on Linux (zenity), and
the toolbar path fields work as a fallback anywhere.

### Adding the GPU path later

`Desqueezer.Resize` is the single dispatch point. A GPU implementation can reuse `AxisWeights`
(start index, tap count and normalised weights per output pixel) as the weight buffers for two
compute passes, and apply the same per-pass light spaces (decode, then sigmoidal or gamma, then the
pass, then encode). Keep the CPU path as the reference: run it through `Tools/Bench` and the
benchmark to check the GPU output matches.

The detail widener is a good fit for a compute shader. Every output pixel is independent: a structure
tensor lookup, a 42-tap filter fetched from a table, and a 45-32-24-1 network. The CPU version
(`AxisSR.cs`) processes bands of 16 rows, and its per-pixel code ports almost line for line.

## Limitations

* The detail model was trained on simulated captures: an area sensor, optical blur, sharpening, noise
  and JPEG. On sources that already have hard ringing, such as images that were sinc-resampled, it can lose to
  Lanczos (see the stress test above). If an image looks wrong, choose **Kernel**.

* CMYK and arithmetic-coded JPEGs aren't supported (Unity's decoder). JPEG input is 8-bit, and its
  size is limited by the GPU's maximum texture size (16384 on most Macs). PNG has no such limits.
* Colour is assumed to use the sRGB transfer curve for linear and sigmoidal processing. ICC profiles
  are preserved but not converted.
* The viewer shows pixels as stored and ignores the EXIF orientation flag. The de-squeeze is applied
  to the stored pixels (the sensor's axis, which is the anamorphic axis) and the orientation flag is
  kept, so viewers rotate the output correctly.
