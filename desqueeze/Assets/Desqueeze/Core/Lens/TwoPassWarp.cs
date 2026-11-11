using System;
using System.Threading;
using System.Threading.Tasks;
using Desqueeze.Core.Color;
using Desqueeze.Core.Imaging;
using Desqueeze.Core.Resampling;

namespace Desqueeze.Core.Lens
{
    /// <summary>How one pass of <see cref="TwoPassWarp"/> resamples.</summary>
    public enum WarpPassMethod
    {
        /// <summary>The learned cross-axis reconstruction with its detail network (<see cref="AxisSR"/>).</summary>
        Detail = 0,
        /// <summary>The learned filters only.</summary>
        DetailFast = 1,
        /// <summary>1-D Lanczos 3.</summary>
        Kernel = 2,
    }

    /// <summary>Settings for <see cref="TwoPassWarp"/>.</summary>
    public sealed class TwoPassOptions
    {
        public WarpPassMethod Horizontal = WarpPassMethod.Detail;
        public WarpPassMethod Vertical = WarpPassMethod.Kernel;
        /// <summary>Kernel and light space for a kernel pass.</summary>
        public Kernel1D HorizontalKernel = Kernels.Lanczos(3), VerticalKernel = Kernels.Lanczos(3);
        public LightSpace HorizontalLight = LightSpace.Sigmoidal, VerticalLight = LightSpace.Sigmoidal;
        public AxisSRModel DetailModel, ShiftModel;
        /// <summary>8 or 16; 0 = same as the source.</summary>
        public int OutputBitDepth;
    }

    /// <summary>
    /// Lens-distortion correction as two 1-D passes (Catmull and Smith's separable warp).
    ///
    /// Pass 1 resamples each row of the source at the positions the distortion asks for, giving an image with the output's
    /// columns and the source's rows. Pass 2 resamples each column at the vertical positions. For a squeezed capture, pass 1
    /// is also the de-squeeze, so the learned reconstruction (<see cref="AxisSR"/>) samples the lens' own pixels exactly
    /// where the corrected image needs them, instead of de-squeezing onto a grid that a second, linear interpolation then
    /// has to move. That second interpolation of an already resampled image is what limits a 2-D warp: even a perfect
    /// de-squeeze warped with any linear kernel measured only 38.5-39.5 dB.
    ///
    /// Distortion is mild and smooth, so a row of the output maps to a gently curved line on the source and the two
    /// passes do not run into the separable warp's "bottleneck" problem (which needs rotations near 90 degrees).
    /// </summary>
    public static class TwoPassWarp
    {
        private const int LutSize = 4096;

        public static PixelImage Correct(PixelImage src, double srcSqueeze, DistortionModel model, CanvasRect canvas, int outW, int outH,
            WarpPassMethod horizontal, WarpPassMethod vertical, AxisSRModel detailModel, AxisSRModel shiftModel, int outputBitDepth,
            ProgressTracker progress = null, CancellationToken ct = default)
        {
            return Correct(src, srcSqueeze, model, canvas, outW, outH, new TwoPassOptions
            {
                Horizontal = horizontal, Vertical = vertical, DetailModel = detailModel, ShiftModel = shiftModel, OutputBitDepth = outputBitDepth,
            }, progress, ct);
        }

        public static PixelImage Correct(PixelImage src, double srcSqueeze, DistortionModel model, CanvasRect canvas, int outW, int outH,
            TwoPassOptions o, ProgressTracker progress = null, CancellationToken ct = default)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            o = o ?? new TwoPassOptions();
            WarpPassMethod horizontal = o.Horizontal, vertical = o.Vertical;
            AxisSRModel detailModel = o.DetailModel, shiftModel = o.ShiftModel;
            int outputBitDepth = o.OutputBitDepth;
            var frame = new LensFrame(src.Width, src.Height, srcSqueeze);
            double ux = canvas.Width / outW, uy = canvas.Height / outH;
            if ((horizontal != WarpPassMethod.Kernel || vertical != WarpPassMethod.Kernel) && detailModel == null)
                throw new InvalidOperationException("The detail model is not loaded.");

            // ---- pass 1: rows ------------------------------------------------------------------------------------------
            AxisSR.RowPositions rowPos = (v, pos) => RowPositions(model, frame, canvas.X0, ux, v, pos);
            PixelImage mid;
            var p1 = ProgressTracker.Slice(progress, 0f, 0.75f);
            if (horizontal == WarpPassMethod.Kernel)
            {
                mid = KernelRows(src, outW, rowPos, o.HorizontalKernel, o.HorizontalLight, p1, ct);
            }
            else
            {
                double nominal = outW * frame.ScaleX / canvas.Width;      // output columns per source column
                mid = AxisSR.ResampleRows(src, outW, rowPos, nominal, detailModel,
                    new AxisSROptions { UseNetwork = horizontal == WarpPassMethod.Detail, OutputBitDepth = 16 }, p1, ct);
            }
            ct.ThrowIfCancellationRequested();

            // ---- pass 2: columns ---------------------------------------------------------------------------------------
            var p2 = ProgressTracker.Slice(progress, 0.75f, 1f);
            PixelImage result;
            if (vertical == WarpPassMethod.Kernel)
            {
                result = KernelColumns(mid, src, outW, outH, model, frame, canvas, ux, uy, o.VerticalKernel, o.VerticalLight, outputBitDepth, p2, ct);
            }
            else
            {
                var t = Transforms.Transpose(mid);
                mid = null;
                double nominal = outH * frame.ScaleY / canvas.Height;
                AxisSR.RowPositions colPos = (x, pos) =>
                {
                    double xu = canvas.X0 + (x + 0.5) * ux;
                    for (int y = 0; y < pos.Length; y++)
                    {
                        double xd, yd, px, py;
                        model.Distort(xu, canvas.Y0 + (y + 0.5) * uy, out xd, out yd);
                        frame.ToPixel(xd, yd, out px, out py);
                        pos[y] = py;
                    }
                };
                var r = AxisSR.ResampleRows(t, outH, colPos, nominal, shiftModel ?? detailModel,
                    new AxisSROptions { UseNetwork = vertical == WarpPassMethod.Detail, OutputBitDepth = 16 }, p2, ct);
                result = Transforms.Transpose(r);
                result = ApplyCoverage(result, src, model, frame, canvas, ux, uy, outputBitDepth);
            }
            result.Metadata = src.Metadata != null ? src.Metadata.Clone() : new ImageMetadata();
            result.SourceFormat = src.SourceFormat;
            return result;
        }

        /// <summary>
        /// For source row <paramref name="v"/>: the source x that each output column samples, i.e. where the vertical
        /// line of the output at that column crosses the row (solved by Newton's method along the column).
        /// </summary>
        private static void RowPositions(DistortionModel m, LensFrame frame, double x0, double ux, int v, double[] pos)
        {
            double ydTarget = (v + 0.5 - frame.Height / 2) * frame.ScaleY;
            double yu = ydTarget;
            for (int x = 0; x < pos.Length; x++)
            {
                double xu = x0 + (x + 0.5) * ux;
                double xd = 0, yd;
                bool ok = false;
                for (int it = 0; it < 12; it++)
                {
                    double j00, j01, j10, j11;
                    m.Distort(xu, yu, out xd, out yd, out j00, out j01, out j10, out j11);
                    double e = yd - ydTarget;
                    if (Math.Abs(e) < 1e-13) { ok = true; break; }
                    if (Math.Abs(j11) < 1e-9) break;
                    double step = e / j11;
                    if (Math.Abs(step) > 0.25) step = 0.25 * Math.Sign(step);
                    yu -= step;
                }
                if (!ok) m.Distort(xu, yu, out xd, out yd);
                pos[x] = (xd + 1) / frame.ScaleX - 0.5;
            }
        }

        // ============================================================================ kernel passes

        /// <summary>Kernel table over [0, radius], <see cref="LutSize"/> + 1 entries.</summary>
        private static float[] Table(Kernel1D k)
        {
            var lut = new float[LutSize + 1];
            for (int i = 0; i <= LutSize; i++) lut[i] = (float)k.Evaluate(i * k.Radius / LutSize);
            return lut;
        }

        private static double Kw(float[] lut, double radius, double d)
        {
            double t = Math.Abs(d) * (LutSize / radius);
            int i = (int)t;
            if (i >= LutSize) return 0;
            return lut[i] + (lut[i + 1] - lut[i]) * (t - i);
        }

        private static float[] DecodeLut(PixelImage img, LightSpace work)
        {
            int max = img.BitDepth == 8 ? 255 : 65535;
            var lut = new float[max + 1];
            if (work == LightSpace.Gamma)
            {
                for (int i = 0; i <= max; i++) lut[i] = i / (float)max;
                return lut;
            }
            Array.Copy(img.BitDepth == 8 ? Srgb.Decode8 : Srgb.Decode16, lut, max + 1);
            if (work == LightSpace.Sigmoidal) RowCodec.Convert(lut, 0, lut.Length, LightSpace.Linear, LightSpace.Sigmoidal);
            return lut;
        }

        private static float Get(PixelImage img, float[] lut, int idx)
        {
            return img.BitDepth == 8 ? lut[img.Data8[idx]] : lut[img.Data16[idx]];
        }

        /// <summary>Horizontal pass with a 1-D Lanczos 3 (footprint widened where the row is shrunk).</summary>
        private static PixelImage KernelRows(PixelImage src, int outW, AxisSR.RowPositions rowPos, Kernel1D kernel, LightSpace work,
            ProgressTracker progress, CancellationToken ct)
        {
            if (src.HasAlpha && work == LightSpace.Sigmoidal) work = LightSpace.Linear;
            var dst = new PixelImage(outW, src.Height, src.Channels, 16);
            var lut = DecodeLut(src, work);
            var k = Table(kernel);
            double R = kernel.Radius;
            int W = src.Width, ch = src.Channels, cc = src.HasAlpha ? ch - 1 : ch;
            float aScale = 1f / src.MaxValue;
            int done = 0;
            Parallel.For(0, src.Height, new ParallelOptions { CancellationToken = ct }, v =>
            {
                var pos = new double[outW];
                rowPos(v, pos);
                var row = new float[outW * ch];
                var acc = new double[ch];
                int rb = v * W * ch;
                for (int x = 0; x < outW; x++)
                {
                    double s = pos[x];
                    double scale = Math.Max(1, Math.Abs(x + 1 < outW ? pos[x + 1] - s : s - pos[x - 1]));
                    double hw = R * scale;
                    int a = (int)Math.Ceiling(s - hw), b = (int)Math.Floor(s + hw);
                    Array.Clear(acc, 0, ch);
                    double ws = 0;
                    for (int i = a; i <= b; i++)
                    {
                        double w = Kw(k, R, (i - s) / scale);
                        if (w == 0) continue;
                        int si = rb + (i < 0 ? 0 : (i >= W ? W - 1 : i)) * ch;
                        float al = src.HasAlpha ? (src.BitDepth == 8 ? src.Data8[si + cc] : src.Data16[si + cc]) * aScale : 1f;
                        for (int c = 0; c < cc; c++) acc[c] += w * Get(src, lut, si + c) * al;
                        if (src.HasAlpha) acc[cc] += w * al;
                        ws += w;
                    }
                    for (int c = 0; c < ch; c++) row[x * ch + c] = (float)(acc[c] / ws);
                }
                if (work == LightSpace.Sigmoidal) RowCodec.Convert(row, 0, row.Length, LightSpace.Sigmoidal, LightSpace.Linear);
                RowCodec.Encode(row, 0, dst, v, work == LightSpace.Gamma ? LightSpace.Gamma : LightSpace.Linear);
                int n = Interlocked.Increment(ref done);
                if (progress != null && (n & 31) == 0) progress.Report((float)n / src.Height);
            });
            return dst;
        }

        /// <summary>
        /// Vertical pass with a 1-D Lanczos 3, plus the anti-aliased boundary of the source (black or transparent outside).
        /// </summary>
        private static PixelImage KernelColumns(PixelImage mid, PixelImage src, int outW, int outH, DistortionModel model, LensFrame frame,
            CanvasRect canvas, double ux, double uy, Kernel1D kernel, LightSpace work, int outputBitDepth, ProgressTracker progress, CancellationToken ct)
        {
            if (mid.HasAlpha && work == LightSpace.Sigmoidal) work = LightSpace.Linear;
            var dst = new PixelImage(outW, outH, mid.Channels, outputBitDepth == 0 ? src.BitDepth : outputBitDepth);
            var lut = DecodeLut(mid, work);
            var k = Table(kernel);
            double R = kernel.Radius;
            int H = mid.Height, ch = mid.Channels, cc = mid.HasAlpha ? ch - 1 : ch;
            bool alpha = mid.HasAlpha;
            float aScale = 1f / mid.MaxValue;
            float bg = work == LightSpace.Sigmoidal ? Bg0() : 0f;
            int done = 0;
            double px2 = 1 / frame.ScaleX, py2 = 1 / frame.ScaleY;
            Parallel.For(0, outH, new ParallelOptions { CancellationToken = ct }, y =>
            {
                var row = new float[outW * ch];
                var acc = new double[ch];
                double yu = canvas.Y0 + (y + 0.5) * uy;
                for (int x = 0; x < outW; x++)
                {
                    double xu = canvas.X0 + (x + 0.5) * ux;
                    double xd, yd, a, b, c, d, sx, sy;
                    model.Distort(xu, yu, out xd, out yd, out a, out b, out c, out d);
                    frame.ToPixel(xd, yd, out sx, out sy);
                    double j00 = px2 * a * ux, j01 = px2 * b * uy, j10 = py2 * c * ux, j11 = py2 * d * uy;
                    double nx = Math.Sqrt(j00 * j00 + j01 * j01), ny = Math.Sqrt(j10 * j10 + j11 * j11);
                    double inX = Math.Min(sx + 0.5, frame.Width - 0.5 - sx) / Math.Max(nx, 1e-9);
                    double inY = Math.Min(sy + 0.5, frame.Height - 0.5 - sy) / Math.Max(ny, 1e-9);
                    double cover = Math.Min(1, 0.5 + Math.Min(inX, inY));
                    int ro = x * ch;
                    if (cover <= 0 || double.IsNaN(sy))
                    {
                        for (int q = 0; q < ch; q++) row[ro + q] = alpha ? 0f : bg;
                        continue;
                    }
                    double scale = Math.Max(1, ny);
                    double hw = R * scale;
                    int lo = (int)Math.Ceiling(sy - hw), hi = (int)Math.Floor(sy + hw);
                    Array.Clear(acc, 0, ch);
                    double ws = 0;
                    for (int i = lo; i <= hi; i++)
                    {
                        double w = Kw(k, R, (i - sy) / scale);
                        if (w == 0) continue;
                        int si = ((i < 0 ? 0 : (i >= H ? H - 1 : i)) * outW + x) * ch;
                        float al = alpha ? (mid.BitDepth == 8 ? mid.Data8[si + cc] : mid.Data16[si + cc]) * aScale : 1f;
                        for (int q = 0; q < cc; q++) acc[q] += w * Get(mid, lut, si + q) * al;
                        if (alpha) acc[cc] += w * al;
                        ws += w;
                    }
                    for (int q = 0; q < ch; q++)
                    {
                        double val = acc[q] / ws;
                        if (cover < 1) val = alpha ? val * cover : val * cover + bg * (1 - cover);
                        row[ro + q] = (float)val;
                    }
                }
                if (work == LightSpace.Sigmoidal) RowCodec.Convert(row, 0, row.Length, LightSpace.Sigmoidal, LightSpace.Linear);
                RowCodec.Encode(row, 0, dst, y, work == LightSpace.Gamma ? LightSpace.Gamma : LightSpace.Linear);
                int n = Interlocked.Increment(ref done);
                if (progress != null && (n & 31) == 0) progress.Report((float)n / outH);
            });
            return dst;
        }

        private static float Bg0()
        {
            var t = new[] { 0f };
            RowCodec.Convert(t, 0, 1, LightSpace.Linear, LightSpace.Sigmoidal);
            return t[0];
        }

        /// <summary>Anti-aliased boundary for the learned vertical pass (gamma blend with black, or alpha).</summary>
        private static PixelImage ApplyCoverage(PixelImage img, PixelImage src, DistortionModel model, LensFrame frame, CanvasRect canvas,
            double ux, double uy, int outputBitDepth)
        {
            int depth = outputBitDepth == 0 ? src.BitDepth : outputBitDepth;
            var dst = new PixelImage(img.Width, img.Height, img.Channels, depth);
            int ch = img.Channels, cc = img.HasAlpha ? ch - 1 : ch;
            double px2 = 1 / frame.ScaleX, py2 = 1 / frame.ScaleY;
            Parallel.For(0, img.Height, y =>
            {
                double yu = canvas.Y0 + (y + 0.5) * uy;
                for (int x = 0; x < img.Width; x++)
                {
                    double xu = canvas.X0 + (x + 0.5) * ux;
                    double xd, yd, a, b, c, d, sx, sy;
                    model.Distort(xu, yu, out xd, out yd, out a, out b, out c, out d);
                    frame.ToPixel(xd, yd, out sx, out sy);
                    double nx = Math.Sqrt(Math.Pow(px2 * a * ux, 2) + Math.Pow(px2 * b * uy, 2));
                    double ny = Math.Sqrt(Math.Pow(py2 * c * ux, 2) + Math.Pow(py2 * d * uy, 2));
                    double inX = Math.Min(sx + 0.5, frame.Width - 0.5 - sx) / Math.Max(nx, 1e-9);
                    double inY = Math.Min(sy + 0.5, frame.Height - 0.5 - sy) / Math.Max(ny, 1e-9);
                    double cover = Math.Max(0, Math.Min(1, 0.5 + Math.Min(inX, inY)));
                    int o = (y * img.Width + x) * ch;
                    for (int q = 0; q < ch; q++)
                    {
                        double v = img.BitDepth == 8 ? img.Data8[o + q] / 255.0 : img.Data16[o + q] / 65535.0;
                        if (q < cc || !img.HasAlpha) v *= (img.HasAlpha && q < cc) ? 1 : cover;
                        else v *= cover;
                        if (depth == 8) dst.Data8[o + q] = (byte)Math.Round(Math.Max(0, Math.Min(1, v)) * 255);
                        else dst.Data16[o + q] = (ushort)Math.Round(Math.Max(0, Math.Min(1, v)) * 65535);
                    }
                }
            });
            return dst;
        }
    }
}
