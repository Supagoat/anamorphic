# TIFF test files

What the apps' TIFF reader and writer (`Core/Codecs/TiffCodec.cs`) are held to. The EditMode test **TiffTests**
(Window > General > Test Runner) runs `Core/Testing/TiffConformance.cs` on this folder and writes
`tiff-report.txt` and the TIFFs it made (`written/`) here.

| Files | What |
|---|---|
| `test-*.tif` + `expect-*.png` | 46 files to read and the picture each must give, upright: grey, grey + alpha, RGB, RGBA and palette; 8 and 16 bits; both byte orders; strips and tiles; interleaved and planar; none, LZW, ZIP and PackBits, with and without the predictor; pre-multiplied alpha; WhiteIsZero; a thumbnail first; an orientation tag with EXIF, GPS, IPTC and an ICC profile |
| `encode-*.tif.zz` | what the writer must make, byte for byte, when it writes 10 of them uncompressed (zlib-packed) |
| `reject-*.tif` | 7 files that must be refused with a message: JPEG-compressed, CMYK, float, 1-bit, BigTIFF, cut short, a PNG |
| `manifest.txt` / `manifest.json` | the lists the C# check and `run_tests.py` read |

The scripts (Python 3 with numpy and Pillow; tifffile and ImageMagick are used where installed):

* `make_tests.py .` remakes the files (Pillow/libtiff writes the common layouts, its own writer the rest).
* `tiffref.py` is a line-for-line Python copy of `TiffCodec.cs`. `run_tests.py .` checks it against every file
  here and checks that Pillow, tifffile and ImageMagick read what it writes; `fuzz.py .` feeds it damaged files.
* `check_written.py` reads the TIFFs the C# writer made (`written/`) with tiffref, Pillow, tifffile and ImageMagick.
