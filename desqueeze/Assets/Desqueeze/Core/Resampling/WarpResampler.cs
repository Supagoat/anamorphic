using System;
using System.Threading;
using System.Threading.Tasks;
using Desqueeze.Core.Color;
using Desqueeze.Core.Imaging;

namespace Desqueeze.Core.Resampling
{
    /// <summary>Maps output pixels to source positions for <see cref="WarpResampler"/>.</summary>
    public abstract class WarpMap
    {
        /// <summary>
        /// For the output pixel centred at (<paramref name="x"/>, <paramref name="y"/>), the source position (pixel centres at
        /// whole numbers) and the Jacobian d(source)/d(output): j00 = dsx/dx, j01 = dsx/dy, j10 = dsy/dx, j11 = dsy/dy.
        /// </summary>
        public abstract void Map(double x, double y, out double sx, out double sy,
            out double j00, out double j01, out double j10, out double j11);
    }

    public enum WarpKernel
    {
        /// <summary>
        /// Radial Lanczos: weight = sinc(r) sinc(r/a) of the Euclidean distance r (Fisheye Friend's CPU renderer). Round
        /// footprint, but not an interpolating kernel on a square grid (it does not vanish at the diagonal neighbours), so
        /// it softens the image even where the map is the identity: 1.6-3.9 dB below <see cref="TensorLanczos"/> in tests.
        /// </summary>
        RadialLanczos = 0,
        /// <summary>
        /// Separable (tensor) Lanczos along the source axes: exact where the map is the identity, sharpest in tests.
        /// The default.
        /// </summary>
        TensorLanczos = 1,
        /// <summary>Separable Catmull-Rom bicubic.</summary>
        Bicubic = 2,
    }

    public sealed class WarpOptions
    {
        public WarpKernel Kernel = WarpKernel.TensorLanczos;
        /// <summary>Lanczos lobes (support radius in source pixels where the map does not shrink).</summary>
        public double Radius = 3;
        /// <summary>Light space the filtering happens in.</summary>
        public LightSpace Light = LightSpace.Gamma;
        /// <summary>Widen the footprint along directions where the map shrinks the image (elliptical weighted average), so detail
        /// that becomes too fine is averaged instead of aliasing.</summary>
        public bool Antialias = true;
        /// <summary>Output bit depth; 0 = same as the source.</summary>
        public int OutputBitDepth;
        /// <summary>Colour outside the source (gamma-encoded 0..1 per colour channel); black if null. Images with alpha become transparent there.</summary>
        public float[] Background;
    }

    /// <summary>
    /// Resamples an image through an arbitrary geometric mapping (lens distortion correction).
    ///
    /// Built like Fisheye Friend's CPU renderer: each output pixel is mapped back to its source position and the source
    /// pixels around it are weighted by a Lanczos-3 kernel and normalised, with colour premultiplied by alpha and source
    /// edges extended (and without its half-pixel offset). The kernel is separable by default (see
    /// <see cref="WarpKernel"/>); the radial one is available. Where the mapping shrinks the image along some direction
    /// the footprint is stretched along that direction by the local Jacobian (an elliptical weighted average, Heckbert
    /// 1989) so the result does not alias, and the boundary of the source inside the output is anti-aliased against
    /// the background.
    /// </summary>
    public static class WarpResampler
    {
        private const int LutSize = 16384;

        public static PixelImage Warp(PixelImage src, int outW, int outH, WarpMap map, WarpOptions o = null,
            ProgressTracker progress = null, CancellationToken ct = default)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (map == null) throw new ArgumentNullException(nameof(map));
            o = o ?? new WarpOptions();
            int depth = o.OutputBitDepth == 0 ? src.BitDepth : o.OutputBitDepth;
            var dst = new PixelImage(outW, outH, src.Channels, depth)
            {
                Metadata = src.Metadata != null ? src.Metadata.Clone() : new ImageMetadata(),
                SourceFormat = src.SourceFormat,
            };

            int W = src.Width, H = src.Height, ch = src.Channels;
            bool alpha = src.HasAlpha;
            int cc = alpha ? ch - 1 : ch;
            double R = o.Kernel == WarpKernel.Bicubic ? 2 : o.Radius;
            LightSpace work = o.Light;
            float[] colorLut = BuildDecodeLut(src.BitDepth, work);
            float alphaScale = 1f / src.MaxValue;
            float[] weightLut = BuildWeightLut(o.Kernel, R);
            double lutScale = (LutSize - 1) / (R * R);
            float[] weight1d = BuildWeightLut1D(o.Kernel, R);   // separable kernels: indexed by the offset itself, interpolated
            double lut1Scale = (LutSize - 1) / R;

            // Background in the working light space.
            var bg = new float[ch];
            for (int c = 0; c < cc; c++)
            {
                float g = o.Background != null && c < o.Background.Length ? o.Background[c] : 0f;
                bg[c] = ToWorking(g, work);
            }
            if (alpha) bg[cc] = 0f;   // transparent outside

            int done = 0;
            Parallel.For(0, outH, new ParallelOptions { CancellationToken = ct }, y =>
            {
                var row = new float[outW * ch];
                var acc = new double[ch];
                var wx = new double[16];
                for (int x = 0; x < outW; x++)
                {
                    double sx, sy, j00, j01, j10, j11;
                    map.Map(x, y, out sx, out sy, out j00, out j01, out j10, out j11);
                    int ro = x * ch;

                    // How far inside the source this output pixel is, in output pixels (anti-aliased boundary).
                    double nx = Math.Sqrt(j00 * j00 + j01 * j01), ny = Math.Sqrt(j10 * j10 + j11 * j11);
                    double inX = Math.Min(sx + 0.5, W - 0.5 - sx) / Math.Max(nx, 1e-9);
                    double inY = Math.Min(sy + 0.5, H - 0.5 - sy) / Math.Max(ny, 1e-9);
                    double cover = Math.Min(1, 0.5 + Math.Min(inX, inY));
                    if (double.IsNaN(sx) || double.IsNaN(sy) || cover <= 0)
                    {
                        // Outside the source: the background colour, or fully transparent for images with alpha.
                        for (int c = 0; c < ch; c++) row[ro + c] = alpha ? 0f : bg[c];
                        continue;
                    }

                    // Footprint: M M^T with M = J, singular values clamped to >= 1 (no blur where the map enlarges).
                    double a = j00 * j00 + j01 * j01, b = j00 * j10 + j01 * j11, d = j10 * j10 + j11 * j11;
                    double q00 = 1, q01 = 0, q11 = 1, hx = R, hy = R;
                    if (o.Antialias && (a > 1 || d > 1))
                    {
                        double tr = 0.5 * (a + d), disc = Math.Sqrt(0.25 * (a - d) * (a - d) + b * b);
                        double l1 = tr + disc, l2 = Math.Max(tr - disc, 0);
                        double e1x, e1y;
                        if (Math.Abs(b) > 1e-12) { e1x = b; e1y = l1 - a; }
                        else if (a >= d) { e1x = 1; e1y = 0; }
                        else { e1x = 0; e1y = 1; }
                        double n1 = Math.Sqrt(e1x * e1x + e1y * e1y);
                        e1x /= n1; e1y /= n1;
                        double e2x = -e1y, e2y = e1x;
                        double c1 = Math.Max(l1, 1), c2 = Math.Max(l2, 1);
                        // (M M^T)^-1 and the ellipse's bounding box
                        q00 = e1x * e1x / c1 + e2x * e2x / c2;
                        q01 = e1x * e1y / c1 + e2x * e2y / c2;
                        q11 = e1y * e1y / c1 + e2y * e2y / c2;
                        hx = R * Math.Sqrt(c1 * e1x * e1x + c2 * e2x * e2x);
                        hy = R * Math.Sqrt(c1 * e1y * e1y + c2 * e2y * e2y);
                    }

                    int x0 = (int)Math.Ceiling(sx - hx), x1 = (int)Math.Floor(sx + hx);
                    int y0 = (int)Math.Ceiling(sy - hy), y1 = (int)Math.Floor(sy + hy);
                    for (int c = 0; c < ch; c++) acc[c] = 0;
                    double wsum = 0;
                    bool tensor = o.Kernel != WarpKernel.RadialLanczos;
                    double invSx = 1, invSy = 1;
                    if (tensor && o.Antialias)
                    {
                        invSx = 1 / Math.Max(1, Math.Sqrt(a));
                        invSy = 1 / Math.Max(1, Math.Sqrt(d));
                    }
                    if (tensor)
                    {
                        // Horizontal weights once per output pixel; the vertical weight once per source row.
                        int nTaps = x1 - x0 + 1;
                        if (wx.Length < nTaps) wx = new double[Math.Max(nTaps, 2 * wx.Length)];
                        for (int k = 0; k < nTaps; k++)
                        {
                            double u = Math.Abs((x0 + k - sx) * invSx);
                            wx[k] = u >= R ? 0 : Lookup(weight1d, u * lut1Scale);
                        }
                    }
                    for (int yy = y0; yy <= y1; yy++)
                    {
                        double dy = yy - sy;
                        int py = yy < 0 ? 0 : (yy >= H ? H - 1 : yy);
                        int rowBase = py * W;
                        double wy = 0;
                        if (tensor)
                        {
                            double v = Math.Abs(dy * invSy);
                            if (v >= R) continue;
                            wy = Lookup(weight1d, v * lut1Scale);
                            if (wy == 0) continue;
                        }
                        for (int xx = x0; xx <= x1; xx++)
                        {
                            double dx = xx - sx;
                            double w;
                            if (!tensor)
                            {
                                double r2 = q00 * dx * dx + 2 * q01 * dx * dy + q11 * dy * dy;
                                if (r2 >= R * R) continue;
                                w = weightLut[(int)(r2 * lutScale)];
                            }
                            else
                            {
                                w = wy * wx[xx - x0];
                                if (w == 0) continue;
                            }
                            int px = xx < 0 ? 0 : (xx >= W ? W - 1 : xx);
                            int si = (rowBase + px) * ch;
                            if (src.BitDepth == 8)
                            {
                                var s8 = src.Data8;
                                float al = alpha ? s8[si + cc] * alphaScale : 1f;
                                for (int c = 0; c < cc; c++) acc[c] += w * colorLut[s8[si + c]] * al;
                                if (alpha) acc[cc] += w * al;
                            }
                            else
                            {
                                var s16 = src.Data16;
                                float al = alpha ? s16[si + cc] * alphaScale : 1f;
                                for (int c = 0; c < cc; c++) acc[c] += w * colorLut[s16[si + c]] * al;
                                if (alpha) acc[cc] += w * al;
                            }
                            wsum += w;
                        }
                    }
                    double inv = wsum > 1e-12 ? 1 / wsum : 0;
                    for (int c = 0; c < ch; c++)
                    {
                        double v = acc[c] * inv;
                        if (cover < 1) v = alpha ? v * cover : v * cover + bg[c] * (1 - cover);
                        row[ro + c] = (float)v;
                    }
                }
                if (work == LightSpace.Sigmoidal)
                {
                    if (alpha) RowCodec.ConvertWithAlpha(row, 0, outW * ch, ch, LightSpace.Sigmoidal, LightSpace.Linear);
                    else RowCodec.Convert(row, 0, row.Length, LightSpace.Sigmoidal, LightSpace.Linear);
                }
                RowCodec.Encode(row, 0, dst, y, work == LightSpace.Gamma ? LightSpace.Gamma : LightSpace.Linear);
                int n = Interlocked.Increment(ref done);
                if (progress != null && (n & 15) == 0) progress.Report((float)n / outH);
            });
            if (progress != null) progress.Report(1f);
            return dst;
        }

        private static float ToWorking(float gammaValue, LightSpace work)
        {
            if (work == LightSpace.Gamma) return gammaValue;
            var t = new[] { (float)Srgb.ToLinear(gammaValue) };
            if (work == LightSpace.Sigmoidal) RowCodec.Convert(t, 0, 1, LightSpace.Linear, LightSpace.Sigmoidal);
            return t[0];
        }

        private static float[] BuildDecodeLut(int depth, LightSpace work)
        {
            int max = depth == 8 ? 255 : 65535;
            var lut = new float[max + 1];
            if (work == LightSpace.Gamma)
            {
                for (int i = 0; i <= max; i++) lut[i] = i / (float)max;
                return lut;
            }
            Array.Copy(depth == 8 ? Srgb.Decode8 : Srgb.Decode16, lut, max + 1);
            if (work == LightSpace.Sigmoidal) RowCodec.Convert(lut, 0, lut.Length, LightSpace.Linear, LightSpace.Sigmoidal);
            return lut;
        }

        /// <summary>Kernel weight as a function of the squared distance (radial) or squared 1-D offset (separable).</summary>
        private static float[] BuildWeightLut(WarpKernel kernel, double R)
        {
            var lut = new float[LutSize];
            for (int i = 0; i < LutSize; i++)
            {
                double r = Math.Sqrt(i * R * R / (LutSize - 1));
                double w;
                if (kernel == WarpKernel.Bicubic)
                {
                    const double A = -0.5;   // Catmull-Rom
                    w = r < 1 ? ((A + 2) * r - (A + 3)) * r * r + 1 : (r < 2 ? ((A * r - 5 * A) * r + 8 * A) * r - 4 * A : 0);
                }
                else
                {
                    w = r < R ? Sinc(r) * Sinc(r / R) : 0;
                }
                lut[i] = (float)w;
            }
            return lut;
        }

        private static double Lookup(float[] lut, double t)
        {
            int i = (int)t;
            if (i >= LutSize - 1) return lut[LutSize - 1];
            double f = t - i;
            return lut[i] + (lut[i + 1] - lut[i]) * f;
        }

        /// <summary>Kernel weight as a function of the 1-D offset, for the separable kernels.</summary>
        private static float[] BuildWeightLut1D(WarpKernel kernel, double R)
        {
            var lut = new float[LutSize];
            for (int i = 0; i < LutSize; i++)
            {
                double r = i * R / (LutSize - 1);
                double w;
                if (kernel == WarpKernel.Bicubic)
                {
                    const double A = -0.5;   // Catmull-Rom
                    w = r < 1 ? ((A + 2) * r - (A + 3)) * r * r + 1 : (r < 2 ? ((A * r - 5 * A) * r + 8 * A) * r - 4 * A : 0);
                }
                else
                {
                    w = r < R ? Sinc(r) * Sinc(r / R) : 0;
                }
                lut[i] = (float)w;
            }
            return lut;
        }

        private static double Sinc(double x)
        {
            if (Math.Abs(x) < 1e-9) return 1;
            double p = Math.PI * x;
            return Math.Sin(p) / p;
        }
    }
}
