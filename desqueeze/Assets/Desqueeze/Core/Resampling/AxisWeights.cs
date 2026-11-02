using System;
using System.Collections.Generic;

namespace Desqueeze.Core.Resampling
{
    /// <summary>
    /// Precomputed filter taps for resampling one axis from InSize to OutSize samples.
    ///
    /// Pixel centres are aligned (output pixel i covers the same fraction of the image as the
    /// source span it maps to). When reducing, the kernel is stretched by 1/scale so it
    /// low-pass filters before decimation (proper anti-aliasing). Samples that fall outside the
    /// image are folded onto the edge pixel (edge replication), and every set of taps is
    /// normalised to sum to exactly 1 so flat areas stay flat.
    /// </summary>
    internal sealed class AxisWeights
    {
        public readonly int InSize;
        public readonly int OutSize;
        public readonly int MaxTaps;
        /// <summary>First source index for each output sample.</summary>
        public readonly int[] Start;
        /// <summary>Number of taps for each output sample.</summary>
        public readonly int[] Count;
        /// <summary>Weights, OutSize x MaxTaps, row-major.</summary>
        public readonly float[] Weights;
        /// <summary>The two source samples nearest each output sample (for anti-ringing).</summary>
        public readonly int[] Near0;
        public readonly int[] Near1;

        public bool IsEnlarging => OutSize > InSize;

        public bool IsIdentity
        {
            get
            {
                if (InSize != OutSize) return false;
                for (int i = 0; i < OutSize; i++)
                    if (Count[i] != 1 || Start[i] != i || Math.Abs(Weights[i * MaxTaps] - 1f) > 1e-6f) return false;
                return true;
            }
        }

        private AxisWeights(int inSize, int outSize, int maxTaps)
        {
            InSize = inSize;
            OutSize = outSize;
            MaxTaps = maxTaps;
            Start = new int[outSize];
            Count = new int[outSize];
            Weights = new float[outSize * maxTaps];
            Near0 = new int[outSize];
            Near1 = new int[outSize];
        }

        public static AxisWeights Create(int inSize, int outSize, Kernel1D kernel)
        {
            if (inSize <= 0 || outSize <= 0) throw new ArgumentException("Sizes must be positive.");
            if (inSize == outSize)
            {
                // An axis that is not being resized passes through untouched, whatever the kernel
                // (some kernels, e.g. MKS2021, would otherwise sharpen slightly at 1:1).
                var id = new AxisWeights(inSize, outSize, 1);
                for (int i = 0; i < outSize; i++)
                {
                    id.Start[i] = i;
                    id.Count[i] = 1;
                    id.Weights[i] = 1f;
                    id.Near0[i] = i;
                    id.Near1[i] = i;
                }
                return id;
            }
            double scale = (double)outSize / inSize;
            double filterScale = Math.Max(1.0, 1.0 / scale);
            double support = kernel.Radius * filterScale;
            // The box kernel reduces by exact area averaging (fractional pixel coverage).
            bool areaAverage = ReferenceEquals(kernel, Kernels.Box) && scale < 1.0;
            if (areaAverage) support += 0.5;

            var starts = new int[outSize];
            var lists = new List<double[]>(outSize);
            int maxTaps = 1;
            var tmp = new double[(int)Math.Ceiling(support * 2) + 4];

            for (int i = 0; i < outSize; i++)
            {
                double center = (i + 0.5) / scale; // in source coordinates (pixel edges at integers)
                int jLo = (int)Math.Floor(center - support - 0.5);
                int jHi = (int)Math.Ceiling(center + support - 0.5);
                int lo = Clamp(jLo, 0, inSize - 1);
                int hi = Clamp(jHi, 0, inSize - 1);
                int n = hi - lo + 1;
                if (tmp.Length < n) tmp = new double[n];
                Array.Clear(tmp, 0, n);

                double sum = 0;
                for (int j = jLo; j <= jHi; j++)
                {
                    double w;
                    if (areaAverage)
                    {
                        // Exact area coverage of source pixel [j, j+1] by the output footprint.
                        double l = Math.Max(j, center - 0.5 * filterScale);
                        double r = Math.Min(j + 1.0, center + 0.5 * filterScale);
                        w = r > l ? r - l : 0.0;
                    }
                    else
                    {
                        w = kernel.Evaluate((j + 0.5 - center) / filterScale);
                    }
                    if (w == 0.0) continue;
                    int idx = Clamp(j, 0, inSize - 1);
                    tmp[idx - lo] += w;
                    sum += w;
                }

                if (Math.Abs(sum) < 1e-12)
                {
                    // Degenerate (should not happen with sane kernels): fall back to nearest.
                    int nn = Clamp((int)Math.Floor(center), 0, inSize - 1);
                    lo = nn;
                    n = 1;
                    tmp[0] = 1.0;
                    sum = 1.0;
                }

                // Trim negligible taps from both ends.
                int a = 0, b = n - 1;
                while (a < b && Math.Abs(tmp[a] / sum) < 1e-7) a++;
                while (b > a && Math.Abs(tmp[b] / sum) < 1e-7) b--;

                var w2 = new double[b - a + 1];
                double s2 = 0;
                for (int k = a; k <= b; k++) s2 += tmp[k];
                for (int k = a; k <= b; k++) w2[k - a] = tmp[k] / s2;
                starts[i] = lo + a;
                lists.Add(w2);
                if (w2.Length > maxTaps) maxTaps = w2.Length;
            }

            var aw = new AxisWeights(inSize, outSize, maxTaps);
            for (int i = 0; i < outSize; i++)
            {
                var w2 = lists[i];
                aw.Start[i] = starts[i];
                aw.Count[i] = w2.Length;
                // Store as float but make the float weights sum to exactly 1 by putting the
                // rounding residue on the largest tap.
                int baseIdx = i * maxTaps;
                double fs = 0;
                int big = 0;
                for (int k = 0; k < w2.Length; k++)
                {
                    float f = (float)w2[k];
                    aw.Weights[baseIdx + k] = f;
                    fs += f;
                    if (Math.Abs(w2[k]) > Math.Abs(w2[big])) big = k;
                }
                aw.Weights[baseIdx + big] += (float)(1.0 - fs);

                double c = (i + 0.5) / scale - 0.5; // continuous source index of the output centre
                int n0 = (int)Math.Floor(c);
                aw.Near0[i] = Clamp(n0, 0, inSize - 1);
                aw.Near1[i] = Clamp(n0 + 1, 0, inSize - 1);
            }
            return aw;
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }
}
