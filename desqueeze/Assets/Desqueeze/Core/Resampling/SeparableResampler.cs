using System;
using System.Threading;
using System.Threading.Tasks;
using Desqueeze.Core.Imaging;

namespace Desqueeze.Core.Resampling
{
    /// <summary>
    /// Two-pass (vertical then horizontal) resampler with a 1-D kernel.
    ///
    /// The image is processed in bands of output rows, in parallel. Each band decodes only the
    /// source rows it needs into float, so memory stays small even for very large images and no
    /// precision is lost between passes. Each pass can use its own light space: the reduced axis is
    /// filtered in its own light space (see <see cref="DesqueezeSettings.ReduceLight"/> and EnlargeLight).
    /// </summary>
    internal static class SeparableResampler
    {
        private sealed class Workspace
        {
            public float[] In = new float[0];
            public float[] Tmp = new float[0];
            public float[] Out = new float[0];

            public void Ensure(int inLen, int tmpLen, int outLen)
            {
                if (In.Length < inLen) In = new float[inLen];
                if (Tmp.Length < tmpLen) Tmp = new float[tmpLen];
                if (Out.Length < outLen) Out = new float[outLen];
            }
        }

        public static void Run(PixelImage src, PixelImage dst, Kernel1D kernel, DesqueezeSettings s,
            ProgressTracker progress, CancellationToken ct)
        {
            var wx = AxisWeights.Create(src.Width, dst.Width, kernel);
            var wy = AxisWeights.Create(src.Height, dst.Height, kernel);

            // Light space per pass: the enlarged axis and the reduced axis can differ.
            LightSpace? sv = PassSpace(wy, s, src.HasAlpha);
            LightSpace? sh = PassSpace(wx, s, src.HasAlpha);
            // An axis that is not resized is a plain copy: give it the other pass' space (no extra conversions).
            LightSpace dv = sv ?? sh ?? s.ReduceLight;
            LightSpace dh = sh ?? dv;
            // Rows are decoded straight into gamma or linear; sigmoidal goes through linear.
            LightSpace decodeSpace = dv == LightSpace.Gamma ? LightSpace.Gamma : LightSpace.Linear;
            LightSpace encodeSpace = dh == LightSpace.Gamma ? LightSpace.Gamma : LightSpace.Linear;

            float ar = s.AntiRinging;
            bool arV = ar > 0f && wy.IsEnlarging;
            bool arH = ar > 0f && wx.IsEnlarging;
            bool identityV = wy.IsIdentity;
            bool identityH = wx.IsIdentity;

            int ch = src.Channels;
            int inRowLen = src.Width * ch;
            int outRowLen = dst.Width * ch;
            int outH = dst.Height;
            int bandRows = Math.Max(4, Math.Min(64, outH / Math.Max(1, Environment.ProcessorCount * 4)));
            int bands = (outH + bandRows - 1) / bandRows;
            var codec = new RowCodec(src, decodeSpace);
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
                    int a = wy.Start[r];
                    int z = a + wy.Count[r] - 1;
                    if (arV)
                    {
                        a = Math.Min(a, wy.Near0[r]);
                        z = Math.Max(z, wy.Near1[r]);
                    }
                    if (a < inLo) inLo = a;
                    if (z > inHi) inHi = z;
                }
                int nIn = inHi - inLo + 1;
                ws.Ensure(nIn * inRowLen, inRowLen, outRowLen);
                float[] inBuf = ws.In;

                for (int iy = inLo; iy <= inHi; iy++)
                {
                    int off = (iy - inLo) * inRowLen;
                    codec.Decode(iy, inBuf, off);
                    if (dv != decodeSpace) RowCodec.Convert(inBuf, off, inRowLen, decodeSpace, dv);
                }

                for (int r = r0; r < r1; r++)
                {
                    // ---- vertical pass -> ws.Tmp (or the decoded row itself when 1:1)
                    float[] vRow;
                    int vOff;
                    if (identityV && dv == dh)
                    {
                        vRow = inBuf;
                        vOff = (r - inLo) * inRowLen;
                    }
                    else if (identityV)
                    {
                        Array.Copy(inBuf, (r - inLo) * inRowLen, ws.Tmp, 0, inRowLen);
                        vRow = ws.Tmp;
                        vOff = 0;
                    }
                    else
                    {
                        Vertical(wy, r, inBuf, inLo, inRowLen, ws.Tmp);
                        if (arV) AntiRingVertical(wy, r, inBuf, inLo, inRowLen, ws.Tmp, ar);
                        vRow = ws.Tmp;
                        vOff = 0;
                    }
                    if (dv != dh)
                    {
                        if (src.HasAlpha) RowCodec.ConvertWithAlpha(vRow, vOff, inRowLen, ch, dv, dh);
                        else RowCodec.Convert(vRow, vOff, inRowLen, dv, dh);
                    }

                    // ---- horizontal pass -> ws.Out
                    float[] hRow;
                    int hOff;
                    if (identityH)
                    {
                        hRow = vRow;
                        hOff = vOff;
                        if (dh != encodeSpace)
                        {
                            Array.Copy(vRow, vOff, ws.Out, 0, outRowLen);
                            hRow = ws.Out;
                            hOff = 0;
                        }
                    }
                    else
                    {
                        Horizontal(wx, vRow, vOff, ch, ws.Out);
                        if (arH) AntiRingHorizontal(wx, vRow, vOff, ch, ws.Out, ar);
                        hRow = ws.Out;
                        hOff = 0;
                    }
                    if (dh != encodeSpace) RowCodec.Convert(hRow, hOff, outRowLen, dh, encodeSpace);
                    RowCodec.Encode(hRow, hOff, dst, r, encodeSpace);
                }

                long done = Interlocked.Add(ref rowsDone, r1 - r0);
                if (progress != null) progress.Report((float)done / outH);
                return ws;
            }, ws => { });
        }

        /// <summary>
        /// Light space for one pass: enlarging and reducing passes use their own setting. An axis that is
        /// not resized takes the other axis' space (its pass is a copy). Sigmoidal needs opaque images.
        /// </summary>
        internal static LightSpace? PassSpace(AxisWeights w, DesqueezeSettings s, bool hasAlpha)
        {
            if (w.InSize == w.OutSize) return null;
            LightSpace ls = w.IsEnlarging ? s.EnlargeLight : s.ReduceLight;
            if (ls == LightSpace.Sigmoidal && hasAlpha) ls = LightSpace.Linear;
            return ls;
        }

        private static void Vertical(AxisWeights wy, int r, float[] inBuf, int inLo, int rowLen, float[] outRow)
        {
            int start = wy.Start[r];
            int cnt = wy.Count[r];
            int wo = r * wy.MaxTaps;
            float[] w = wy.Weights;

            float w0 = w[wo];
            int o0 = (start - inLo) * rowLen;
            for (int i = 0; i < rowLen; i++) outRow[i] = w0 * inBuf[o0 + i];
            for (int k = 1; k < cnt; k++)
            {
                float wk = w[wo + k];
                int ok = (start + k - inLo) * rowLen;
                for (int i = 0; i < rowLen; i++) outRow[i] += wk * inBuf[ok + i];
            }
        }

        private static void AntiRingVertical(AxisWeights wy, int r, float[] inBuf, int inLo, int rowLen, float[] outRow, float strength)
        {
            int a = (wy.Near0[r] - inLo) * rowLen;
            int b = (wy.Near1[r] - inLo) * rowLen;
            for (int i = 0; i < rowLen; i++)
            {
                float p = inBuf[a + i], q = inBuf[b + i];
                float lo = p < q ? p : q;
                float hi = p < q ? q : p;
                float v = outRow[i];
                float c = v < lo ? lo : (v > hi ? hi : v);
                outRow[i] = v + (c - v) * strength;
            }
        }

        private static void Horizontal(AxisWeights wx, float[] row, int rowOff, int ch, float[] outRow)
        {
            int outW = wx.OutSize;
            int maxTaps = wx.MaxTaps;
            float[] w = wx.Weights;
            int[] start = wx.Start;
            int[] count = wx.Count;
            switch (ch)
            {
                case 1:
                    for (int x = 0; x < outW; x++)
                    {
                        int p = rowOff + start[x];
                        int wo = x * maxTaps;
                        int cnt = count[x];
                        float a0 = 0;
                        for (int k = 0; k < cnt; k++) a0 += w[wo + k] * row[p + k];
                        outRow[x] = a0;
                    }
                    break;
                case 2:
                    for (int x = 0; x < outW; x++)
                    {
                        int p = rowOff + start[x] * 2;
                        int wo = x * maxTaps;
                        int cnt = count[x];
                        float a0 = 0, a1 = 0;
                        for (int k = 0; k < cnt; k++, p += 2)
                        {
                            float wk = w[wo + k];
                            a0 += wk * row[p];
                            a1 += wk * row[p + 1];
                        }
                        outRow[x * 2] = a0;
                        outRow[x * 2 + 1] = a1;
                    }
                    break;
                case 3:
                    for (int x = 0; x < outW; x++)
                    {
                        int p = rowOff + start[x] * 3;
                        int wo = x * maxTaps;
                        int cnt = count[x];
                        float a0 = 0, a1 = 0, a2 = 0;
                        for (int k = 0; k < cnt; k++, p += 3)
                        {
                            float wk = w[wo + k];
                            a0 += wk * row[p];
                            a1 += wk * row[p + 1];
                            a2 += wk * row[p + 2];
                        }
                        int o = x * 3;
                        outRow[o] = a0;
                        outRow[o + 1] = a1;
                        outRow[o + 2] = a2;
                    }
                    break;
                default:
                    for (int x = 0; x < outW; x++)
                    {
                        int p = rowOff + start[x] * 4;
                        int wo = x * maxTaps;
                        int cnt = count[x];
                        float a0 = 0, a1 = 0, a2 = 0, a3 = 0;
                        for (int k = 0; k < cnt; k++, p += 4)
                        {
                            float wk = w[wo + k];
                            a0 += wk * row[p];
                            a1 += wk * row[p + 1];
                            a2 += wk * row[p + 2];
                            a3 += wk * row[p + 3];
                        }
                        int o = x * 4;
                        outRow[o] = a0;
                        outRow[o + 1] = a1;
                        outRow[o + 2] = a2;
                        outRow[o + 3] = a3;
                    }
                    break;
            }
        }

        private static void AntiRingHorizontal(AxisWeights wx, float[] row, int rowOff, int ch, float[] outRow, float strength)
        {
            int outW = wx.OutSize;
            for (int x = 0; x < outW; x++)
            {
                int a = rowOff + wx.Near0[x] * ch;
                int b = rowOff + wx.Near1[x] * ch;
                int o = x * ch;
                for (int c = 0; c < ch; c++)
                {
                    float p = row[a + c], q = row[b + c];
                    float lo = p < q ? p : q;
                    float hi = p < q ? q : p;
                    float v = outRow[o + c];
                    float cl = v < lo ? lo : (v > hi ? hi : v);
                    outRow[o + c] = v + (cl - v) * strength;
                }
            }
        }
    }
}
