using System;
using System.Threading;
using System.Threading.Tasks;
using Desqueeze.Core.Imaging;

namespace Desqueeze.Core.Resampling
{
    /// <summary>
    /// Elliptical Weighted Average (EWA) resampler with a Jinc-windowed Jinc kernel ("EWA Lanczos Sharp").
    ///
    /// Unlike separable filters, the weight of each source pixel depends on its true 2-D distance
    /// from the output pixel centre, so the filter is the same in every direction (no "square"
    /// frequency response, fewer jaggies on diagonals). The footprint is an ellipse stretched along
    /// any axis that is being reduced, which handles the de-squeeze's mixed enlarge/reduce correctly
    /// in a single pass. This is the scaler mpv ships as ewa_lanczossharp and ImageMagick uses for
    /// -distort Resize.
    /// </summary>
    internal static class EwaResampler
    {
        private const int LutSize = 4096;
        private static float[] s_lut;
        private static readonly double Radius = Kernels.JincZero3;
        private static readonly double Radius2 = Radius * Radius;

        private sealed class AxisTaps
        {
            public int MaxTaps;
            public int[] Count;
            /// <summary>Squared normalised distance per tap (OutSize x MaxTaps).</summary>
            public float[] D2;
            /// <summary>Source index per tap, clamped to the image (edge replication).</summary>
            public int[] Idx;
            public int[] Near0;
            public int[] Near1;
            public bool Enlarging;

            public static AxisTaps Create(int inSize, int outSize, double blur)
            {
                double scale = (double)outSize / inSize;
                double f = Math.Max(1.0, 1.0 / scale) * blur;
                double support = Radius * f;
                int maxTaps = (int)Math.Ceiling(support * 2) + 2;
                var t = new AxisTaps
                {
                    MaxTaps = maxTaps,
                    Count = new int[outSize],
                    D2 = new float[outSize * maxTaps],
                    Idx = new int[outSize * maxTaps],
                    Near0 = new int[outSize],
                    Near1 = new int[outSize],
                    Enlarging = outSize > inSize,
                };
                for (int i = 0; i < outSize; i++)
                {
                    double c = (i + 0.5) / scale;
                    int jLo = (int)Math.Floor(c - support - 0.5);
                    int jHi = (int)Math.Ceiling(c + support - 0.5);
                    int n = 0;
                    for (int j = jLo; j <= jHi && n < maxTaps; j++)
                    {
                        double d = (j + 0.5 - c) / f;
                        double d2 = d * d;
                        if (d2 >= Radius2) continue;
                        t.D2[i * maxTaps + n] = (float)d2;
                        t.Idx[i * maxTaps + n] = j < 0 ? 0 : (j >= inSize ? inSize - 1 : j);
                        n++;
                    }
                    t.Count[i] = n;
                    int n0 = (int)Math.Floor(c - 0.5);
                    t.Near0[i] = n0 < 0 ? 0 : (n0 >= inSize ? inSize - 1 : n0);
                    t.Near1[i] = n0 + 1 < 0 ? 0 : (n0 + 1 >= inSize ? inSize - 1 : n0 + 1);
                }
                return t;
            }
        }

        private static float[] Lut
        {
            get
            {
                if (s_lut != null) return s_lut;
                var t = new float[LutSize + 1];
                for (int i = 0; i <= LutSize; i++)
                {
                    double r2 = Radius2 * i / LutSize;
                    t[i] = (float)Kernels.JincLanczos3(Math.Sqrt(r2));
                }
                t[LutSize] = 0f;
                s_lut = t;
                return t;
            }
        }

        private sealed class Workspace
        {
            public float[] In = new float[0];
            public float[] Out = new float[0];
            public double[] Acc = new double[4];
        }

        public static void Run(PixelImage src, PixelImage dst, DesqueezeSettings s, ProgressTracker progress, CancellationToken ct)
        {
            double blur = Kernels.LanczosSharpBlur;
            var tx = AxisTaps.Create(src.Width, dst.Width, blur);
            var ty = AxisTaps.Create(src.Height, dst.Height, blur);
            float[] lut = Lut;
            float lutScale = (float)(LutSize / Radius2);

            // One 2-D pass, so one light space: the enlarging setting if either axis grows.
            LightSpace domain = (tx.Enlarging || ty.Enlarging) ? s.EnlargeLight : s.ReduceLight;
            if (domain == LightSpace.Sigmoidal && src.HasAlpha) domain = LightSpace.Linear;
            LightSpace baseDomain = domain == LightSpace.Gamma ? LightSpace.Gamma : LightSpace.Linear;
            float ar = s.AntiRinging;
            bool antiRing = ar > 0f && (tx.Enlarging || ty.Enlarging);

            int ch = src.Channels;
            int inRowLen = src.Width * ch;
            int outW = dst.Width;
            int outH = dst.Height;
            int outRowLen = outW * ch;
            int bandRows = Math.Max(4, Math.Min(32, outH / Math.Max(1, Environment.ProcessorCount * 4)));
            int bands = (outH + bandRows - 1) / bandRows;
            var codec = new RowCodec(src, baseDomain);
            long rowsDone = 0;

            var options = new ParallelOptions { CancellationToken = ct };
            Parallel.For(0, bands, options, () => new Workspace(), (b, state, ws) =>
            {
                ct.ThrowIfCancellationRequested();
                int r0 = b * bandRows;
                int r1 = Math.Min(outH, r0 + bandRows);

                int inLo = int.MaxValue, inHi = -1;
                for (int r = r0; r < r1; r++)
                {
                    int bo = r * ty.MaxTaps;
                    for (int k = 0; k < ty.Count[r]; k++)
                    {
                        int iy = ty.Idx[bo + k];
                        if (iy < inLo) inLo = iy;
                        if (iy > inHi) inHi = iy;
                    }
                    if (ty.Near0[r] < inLo) inLo = ty.Near0[r];
                    if (ty.Near1[r] > inHi) inHi = ty.Near1[r];
                }
                int nIn = inHi - inLo + 1;
                if (ws.In.Length < nIn * inRowLen) ws.In = new float[nIn * inRowLen];
                if (ws.Out.Length < outRowLen) ws.Out = new float[outRowLen];
                float[] inBuf = ws.In;
                float[] outRow = ws.Out;
                double[] acc = ws.Acc;

                for (int iy = inLo; iy <= inHi; iy++)
                {
                    int off = (iy - inLo) * inRowLen;
                    codec.Decode(iy, inBuf, off);
                    if (domain != baseDomain) RowCodec.Convert(inBuf, off, inRowLen, baseDomain, domain);
                }

                for (int r = r0; r < r1; r++)
                {
                    int yo = r * ty.MaxTaps;
                    int yCount = ty.Count[r];
                    for (int x = 0; x < outW; x++)
                    {
                        int xo = x * tx.MaxTaps;
                        int xCount = tx.Count[x];
                        for (int c = 0; c < ch; c++) acc[c] = 0;
                        double wsum = 0;
                        for (int ky = 0; ky < yCount; ky++)
                        {
                            float dy2 = ty.D2[yo + ky];
                            int rowOff = (ty.Idx[yo + ky] - inLo) * inRowLen;
                            for (int kx = 0; kx < xCount; kx++)
                            {
                                float r2 = dy2 + tx.D2[xo + kx];
                                if (r2 >= Radius2) continue;
                                float p = r2 * lutScale;
                                int li = (int)p;
                                float w = lut[li] + (lut[li + 1] - lut[li]) * (p - li);
                                int pi = rowOff + tx.Idx[xo + kx] * ch;
                                wsum += w;
                                switch (ch)
                                {
                                    case 1:
                                        acc[0] += w * inBuf[pi];
                                        break;
                                    case 2:
                                        acc[0] += w * inBuf[pi];
                                        acc[1] += w * inBuf[pi + 1];
                                        break;
                                    case 3:
                                        acc[0] += w * inBuf[pi];
                                        acc[1] += w * inBuf[pi + 1];
                                        acc[2] += w * inBuf[pi + 2];
                                        break;
                                    default:
                                        acc[0] += w * inBuf[pi];
                                        acc[1] += w * inBuf[pi + 1];
                                        acc[2] += w * inBuf[pi + 2];
                                        acc[3] += w * inBuf[pi + 3];
                                        break;
                                }
                            }
                        }
                        double inv = Math.Abs(wsum) > 1e-12 ? 1.0 / wsum : 0.0;
                        int o = x * ch;
                        for (int c = 0; c < ch; c++) outRow[o + c] = (float)(acc[c] * inv);

                        if (antiRing)
                        {
                            // Clamp toward the range of the 2x2 nearest source pixels.
                            int ra = (ty.Near0[r] - inLo) * inRowLen;
                            int rb = (ty.Near1[r] - inLo) * inRowLen;
                            int ca = tx.Near0[x] * ch;
                            int cb = tx.Near1[x] * ch;
                            for (int c = 0; c < ch; c++)
                            {
                                float p00 = inBuf[ra + ca + c], p01 = inBuf[ra + cb + c];
                                float p10 = inBuf[rb + ca + c], p11 = inBuf[rb + cb + c];
                                float lo = Math.Min(Math.Min(p00, p01), Math.Min(p10, p11));
                                float hi = Math.Max(Math.Max(p00, p01), Math.Max(p10, p11));
                                float v = outRow[o + c];
                                float cl = v < lo ? lo : (v > hi ? hi : v);
                                outRow[o + c] = v + (cl - v) * ar;
                            }
                        }
                    }
                    if (domain != baseDomain) RowCodec.Convert(outRow, 0, outRowLen, domain, baseDomain);
                    RowCodec.Encode(outRow, 0, dst, r, baseDomain);
                }

                long done = Interlocked.Add(ref rowsDone, r1 - r0);
                if (progress != null) progress.Report((float)done / outH);
                return ws;
            }, ws => { });
        }
    }
}
