using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Desqueeze.Core.Color;
using Desqueeze.Core.Imaging;

namespace Desqueeze.Core.Resampling
{
    /// <summary>One fully-connected layer of the detail network. Weights are [In][Out], row-major.</summary>
    public sealed class MlpLayer
    {
        public int In, Out;
        public float[] W;     // [In][Out] as stored in the model file
        public float[] WT;    // [Out][In], used for inference
        public float[] B;
    }

    /// <summary>
    /// Trained model for <see cref="AxisSR"/>: a bank of linear filters chosen per local situation, plus an
    /// optional small network that corrects their output. Produced by Tools/AxisSR/train (see README).
    /// </summary>
    public sealed class AxisSRModel
    {
        public string Name = "";
        public int Rows, Cols;              // input window: Rows x Cols samples
        public int P, Q, S, C, E;           // phase, angle, strength, coherence, edge-offset bins
        public float Sigma;                 // structure-tensor smoothing (input px)
        public LightSpace Space;            // working light space
        public float ScaleMin, ScaleMax;    // stretch range the model was trained for
        public float[] StrengthEdges, CoherenceEdges;
        public float[] Filters;             // [P][Q][S][C][E][Rows*Cols]
        public MlpLayer[] Layers;           // null or empty = no network
        public float Leak = 0.05f;
        public bool NetworkHasScaleInput;

        public int Taps => Rows * Cols;
        public int HalfRows => Rows / 2;
        public int LeftCols => Cols / 2 - 1;
        public bool HasNetwork => Layers != null && Layers.Length > 0;

        /// <summary>The model used by <see cref="Desqueezer"/> when a learned widening method is selected.
        /// Set it once at start-up (the app loads it from StreamingAssets).</summary>
        public static AxisSRModel Default { get; set; }

        public static AxisSRModel Load(string path)
        {
            return Load(File.ReadAllBytes(path));
        }

        public static AxisSRModel Load(byte[] data)
        {
            using (var br = new BinaryReader(new MemoryStream(data)))
            {
                if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "DSQM") throw new InvalidDataException("Not a Desqueeze model file.");
                int version = br.ReadInt32();
                if (version != 1) throw new InvalidDataException("Unsupported model version " + version + ".");
                var m = new AxisSRModel();
                int nameLen = br.ReadInt32();
                m.Name = Encoding.UTF8.GetString(br.ReadBytes(nameLen));
                m.Rows = br.ReadInt32(); m.Cols = br.ReadInt32();
                m.P = br.ReadInt32(); m.Q = br.ReadInt32(); m.S = br.ReadInt32(); m.C = br.ReadInt32(); m.E = br.ReadInt32();
                m.Sigma = br.ReadSingle();
                m.Space = (LightSpace)(br.ReadInt32() switch { 0 => (int)LightSpace.Gamma, 1 => (int)LightSpace.Linear, _ => (int)LightSpace.Sigmoidal });
                br.ReadInt32(); // reserved
                m.ScaleMin = br.ReadSingle(); m.ScaleMax = br.ReadSingle();
                m.StrengthEdges = ReadFloats(br);
                m.CoherenceEdges = ReadFloats(br);
                float[] edgeEdges = ReadFloats(br);
                if (m.E != 1 || edgeEdges.Length != 0) throw new InvalidDataException("Edge-offset classes are not supported by this build.");
                if (m.StrengthEdges.Length != m.S - 1 || m.CoherenceEdges.Length != m.C - 1) throw new InvalidDataException("Model thresholds don't match its class counts.");
                m.Filters = ReadFloats(br);
                if (m.Filters.Length != m.P * m.Q * m.S * m.C * m.E * m.Taps) throw new InvalidDataException("Model filter bank has the wrong size.");
                int layers = br.ReadInt32();
                if (layers > 0)
                {
                    m.Leak = br.ReadSingle();
                    m.NetworkHasScaleInput = br.ReadInt32() != 0;
                    m.Layers = new MlpLayer[layers];
                    for (int i = 0; i < layers; i++)
                    {
                        var l = new MlpLayer { In = br.ReadInt32(), Out = br.ReadInt32() };
                        l.W = ReadFloats(br, l.In * l.Out);
                        l.B = ReadFloats(br, l.Out);
                        l.WT = new float[l.In * l.Out];
                        for (int a = 0; a < l.In; a++)
                            for (int b = 0; b < l.Out; b++) l.WT[b * l.In + a] = l.W[a * l.Out + b];
                        m.Layers[i] = l;
                    }
                    int expectIn = m.Taps + 2 + (m.NetworkHasScaleInput ? 1 : 0);
                    if (m.Layers[0].In != expectIn || m.Layers[layers - 1].Out != 1)
                        throw new InvalidDataException("Model network has unexpected input/output sizes.");
                }
                return m;
            }
        }

        private static float[] ReadFloats(BinaryReader br)
        {
            return ReadFloats(br, br.ReadInt32());
        }

        private static float[] ReadFloats(BinaryReader br, int n)
        {
            byte[] bytes = br.ReadBytes(n * 4);
            if (bytes.Length != n * 4) throw new InvalidDataException("Model file is truncated.");
            var f = new float[n];
            Buffer.BlockCopy(bytes, 0, f, 0, bytes.Length);
            if (!BitConverter.IsLittleEndian)
                for (int i = 0; i < n; i++)
                {
                    byte[] b = BitConverter.GetBytes(f[i]);
                    Array.Reverse(b);
                    f[i] = BitConverter.ToSingle(b, 0);
                }
            return f;
        }
    }

    public sealed class AxisSROptions
    {
        /// <summary>Run the detail network on top of the learned filters (slower, sharper).</summary>
        public bool UseNetwork = true;
        /// <summary>Skip the network where the local window is flat (its correction is negligible there).
        /// Expressed as the window's standard deviation in [0,1] working units; 0 = always run.</summary>
        public float FlatSkip = 0.02f;
        /// <summary>Bit depth of the result (8 or 16); 0 = same as the source.</summary>
        public int OutputBitDepth;
        /// <summary>
        /// Run the network once on luma and transfer its correction to each colour channel by the channel's
        /// local regression slope against luma (about 3x faster than running it per channel).
        /// </summary>
        public bool NetworkOnLuma = true;
    }

    /// <summary>
    /// Learned one-axis upscaler for anamorphic de-squeeze ("cross-axis detail reconstruction").
    ///
    /// Only one axis is enlarged (by up to ~2x); the other axis is fully sampled and is used as the
    /// source of extra information. Each output sample is computed from a window spanning several
    /// input rows (Rows x Cols) with a linear filter chosen by the sample's sub-pixel phase and the local
    /// edge angle, strength and coherence (structure tensor of the full-resolution 2-D neighbourhood).
    /// A small network then corrects the filter output using the contrast-normalised window, which lets
    /// it place edges with sub-pixel accuracy - something no linear filter can do.
    /// Both stages were trained on simulated anamorphic captures with realistic camera processing.
    /// </summary>
    public static class AxisSR
    {
        public static PixelImage UpscaleX(PixelImage src, int outWidth, AxisSRModel model, AxisSROptions options = null,
            ProgressTracker progress = null, CancellationToken ct = default)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (outWidth < src.Width) throw new ArgumentException("AxisSR only enlarges.");
            options = options ?? new AxisSROptions();
            int outDepth = options.OutputBitDepth == 0 ? src.BitDepth : options.OutputBitDepth;
            var dst = new PixelImage(outWidth, src.Height, src.Channels, outDepth)
            {
                Metadata = src.Metadata != null ? src.Metadata.Clone() : new ImageMetadata(),
                SourceFormat = src.SourceFormat,
            };
            new Runner(src, dst, model, options).Run(progress, ct);
            return dst;
        }

        /// <summary>
        /// Source positions for one row: <paramref name="positions"/>[x] receives the source x (pixel centres at whole
        /// numbers) that output column x samples on source row <paramref name="row"/>. Positions must increase along the row.
        /// </summary>
        public delegate void RowPositions(int row, double[] positions);

        /// <summary>
        /// Resamples each row at arbitrary, row-dependent positions (a horizontal warp), reconstructing detail as
        /// <see cref="UpscaleX"/> does. <paramref name="nominalScale"/> is the typical output/input sample ratio, given to
        /// the network as its scale feature. Used for the horizontal pass of a two-pass lens-distortion correction, which
        /// thereby happens in the same resampling as the de-squeeze.
        /// </summary>
        public static PixelImage ResampleRows(PixelImage src, int outWidth, RowPositions positions, double nominalScale, AxisSRModel model,
            AxisSROptions options = null, ProgressTracker progress = null, CancellationToken ct = default)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (model == null) throw new ArgumentNullException(nameof(model));
            if (positions == null) throw new ArgumentNullException(nameof(positions));
            options = options ?? new AxisSROptions();
            int outDepth = options.OutputBitDepth == 0 ? src.BitDepth : options.OutputBitDepth;
            var dst = new PixelImage(outWidth, src.Height, src.Channels, outDepth)
            {
                Metadata = src.Metadata != null ? src.Metadata.Clone() : new ImageMetadata(),
                SourceFormat = src.SourceFormat,
            };
            new Runner(src, dst, model, options, positions, nominalScale).Run(progress, ct);
            return dst;
        }

        /// <summary>Enlarges along the requested axis (Y is handled by transposing).</summary>
        public static PixelImage Upscale(PixelImage src, int outLength, SqueezeAxis axis, AxisSRModel model, AxisSROptions options = null,
            ProgressTracker progress = null, CancellationToken ct = default)
        {
            if (axis == SqueezeAxis.Horizontal) return UpscaleX(src, outLength, model, options, progress, ct);
            var t = Transforms.Transpose(src);
            var r = UpscaleX(t, outLength, model, options, progress, ct);
            var back = Transforms.Transpose(r);
            back.Metadata = r.Metadata;
            back.SourceFormat = src.SourceFormat;
            return back;
        }

        private sealed class Runner
        {
            private readonly PixelImage _src, _dst;
            private readonly AxisSRModel _m;
            private readonly AxisSROptions _o;
            private readonly int _w, _h, _W, _ch, _cc;
            private readonly bool _alpha;
            private readonly float[] _decode;          // integer sample -> working space
            private readonly float _maxOut;
            private readonly float[] _gauss;           // structure-tensor smoothing kernel
            private readonly int _gr;                  // its radius
            private readonly int[] _j0;
            private readonly float[] _phase;
            private readonly int[] _p0, _p1;
            private readonly float[] _pf;
            private readonly float _scaleFeature;
            private readonly RowPositions _rowPositions;
            private const int BandRows = 16;

            public Runner(PixelImage src, PixelImage dst, AxisSRModel m, AxisSROptions o, RowPositions rowPositions, double nominalScale)
                : this(src, dst, m, o)
            {
                _rowPositions = rowPositions;
                _scaleFeature = (float)((nominalScale - 1.5) * 2.0);
            }

            public Runner(PixelImage src, PixelImage dst, AxisSRModel m, AxisSROptions o)
            {
                _src = src; _dst = dst; _m = m; _o = o;
                _w = src.Width; _h = src.Height; _W = dst.Width;
                _ch = src.Channels; _alpha = src.HasAlpha; _cc = _alpha ? _ch - 1 : _ch;
                _maxOut = dst.MaxValue;
                bool linear = m.Space != LightSpace.Gamma;
                if (src.BitDepth == 8)
                {
                    _decode = new float[256];
                    for (int i = 0; i < 256; i++) _decode[i] = linear ? Srgb.Decode8[i] : i / 255f;
                }
                else
                {
                    _decode = new float[65536];
                    for (int i = 0; i < 65536; i++) _decode[i] = linear ? Srgb.Decode16[i] : i / 65535f;
                }
                // Gaussian matching scipy.ndimage.gaussian_filter(sigma, truncate=4)
                _gr = (int)(4.0 * m.Sigma + 0.5);
                _gauss = new float[2 * _gr + 1];
                double gs = 0;
                for (int k = -_gr; k <= _gr; k++) gs += Math.Exp(-0.5 * k * k / (m.Sigma * (double)m.Sigma));
                for (int k = -_gr; k <= _gr; k++) _gauss[k + _gr] = (float)(Math.Exp(-0.5 * k * k / (m.Sigma * (double)m.Sigma)) / gs);
                // per-output-column geometry
                _j0 = new int[_W]; _phase = new float[_W]; _p0 = new int[_W]; _p1 = new int[_W]; _pf = new float[_W];
                double ratio = (double)_w / _W;
                for (int x = 0; x < _W; x++)
                {
                    double c = (x + 0.5) * ratio - 0.5;
                    int j = (int)Math.Floor(c);
                    double ph = c - j;
                    _j0[x] = j;
                    _phase[x] = (float)ph;
                    double pp = ph * m.P - 0.5;
                    int a = (int)Math.Floor(pp);
                    _pf[x] = (float)(pp - a);
                    _p1[x] = Clamp(a + 1, 0, m.P - 1);
                    _p0[x] = Clamp(a, 0, m.P - 1);
                }
                _scaleFeature = (float)(((double)_W / _w - 1.5) * 2.0);
            }

            private sealed class Workspace
            {
                public int[] J0, P0, P1;                   // per-row geometry when positions vary by row
                public float[] Phase, Pf;
                public double[] Pos;
                public float[] Luma, Jxx, Jxy, Jyy, Hxx, Hxy, Hyy, Planes, LumaPlane, Filter, Win, X, Y;
                public int[] RowOff;
                public float[] Lin;
                public float[][] Act;
            }

            public void Run(ProgressTracker progress, CancellationToken ct)
            {
                int bands = (_h + BandRows - 1) / BandRows;
                long done = 0;
                var po = new ParallelOptions { CancellationToken = ct };
                Parallel.For(0, bands, po, () => NewWorkspace(), (b, state, ws) =>
                {
                    ct.ThrowIfCancellationRequested();
                    int r0 = b * BandRows, r1 = Math.Min(_h, r0 + BandRows);
                    ProcessBand(r0, r1, ws);
                    long d = Interlocked.Add(ref done, r1 - r0);
                    if (progress != null) progress.Report((float)d / _h);
                    return ws;
                }, ws => { });
            }

            private Workspace NewWorkspace()
            {
                int tensorRows = BandRows + 2 * _gr;
                int planeRows = BandRows + 2 * _m.HalfRows;
                var ws = new Workspace
                {
                    Luma = new float[(tensorRows + 2) * _w],
                    Hxx = new float[tensorRows * _w], Hxy = new float[tensorRows * _w], Hyy = new float[tensorRows * _w],
                    Jxx = new float[BandRows * _w], Jxy = new float[BandRows * _w], Jyy = new float[BandRows * _w],
                    Planes = new float[_ch * planeRows * _w],
                    LumaPlane = new float[planeRows * _w],
                    Filter = new float[_m.Taps],
                    Win = new float[_m.Taps],
                    RowOff = new int[_m.Rows],
                    Lin = new float[_ch],
                };
                if (_rowPositions != null)
                {
                    ws.J0 = new int[_W]; ws.P0 = new int[_W]; ws.P1 = new int[_W];
                    ws.Phase = new float[_W]; ws.Pf = new float[_W]; ws.Pos = new double[_W];
                }
                if (_m.HasNetwork)
                {
                    ws.X = new float[_m.Layers[0].In];
                    ws.Act = new float[_m.Layers.Length][];
                    for (int i = 0; i < _m.Layers.Length; i++) ws.Act[i] = new float[_m.Layers[i].Out];
                }
                return ws;
            }

            private float Sample(int y, int x, int c)
            {
                int s = (y * _w + x) * _ch + c;
                return _src.BitDepth == 8 ? _decode[_src.Data8[s]] : _decode[_src.Data16[s]];
            }

            private float LumaAt(int y, int x)
            {
                if (_cc >= 3) return 0.299f * Sample(y, x, 0) + 0.587f * Sample(y, x, 1) + 0.114f * Sample(y, x, 2);
                return Sample(y, x, 0);
            }

            private void ProcessBand(int r0, int r1, Workspace ws)
            {
                int w = _w, H = _h;
                int nb = r1 - r0;
                // ---- structure tensor for rows r0..r1-1 -------------------------------------------------
                // products are needed for real rows [t0, t1]; luma for [t0-1, t1+1]
                int t0 = Math.Max(0, r0 - _gr), t1 = Math.Min(H - 1, r1 - 1 + _gr);
                int l0 = Math.Max(0, t0 - 1), l1 = Math.Min(H - 1, t1 + 1);
                for (int y = l0; y <= l1; y++)
                {
                    int o = (y - l0) * w;
                    for (int x = 0; x < w; x++) ws.Luma[o + x] = LumaAt(y, x);
                }
                for (int y = t0; y <= t1; y++)
                {
                    int lo = (y - l0) * w;
                    int ho = (y - t0) * w;
                    // gradient products for this row, then horizontal Gaussian (edge-replicated)
                    // compute raw products into Jxx/Jxy/Jyy scratch first (row-sized reuse of Win is too small, use Hxx tail)
                    for (int x = 0; x < w; x++)
                    {
                        float gx = (x > 0 && x < w - 1) ? (ws.Luma[lo + x + 1] - ws.Luma[lo + x - 1]) * 0.5f : 0f;
                        float gy = (y > 0 && y < H - 1) ? (ws.Luma[lo + w + x] - ws.Luma[lo - w + x]) * 0.5f : 0f;
                        ws.Jxx[x] = gx * gx;   // first row of Jxx used as scratch
                        ws.Jxy[x] = gx * gy;
                        ws.Jyy[x] = gy * gy;
                    }
                    for (int x = 0; x < w; x++)
                    {
                        float axx = 0, axy = 0, ayy = 0;
                        for (int k = -_gr; k <= _gr; k++)
                        {
                            int xx = x + k;
                            xx = xx < 0 ? 0 : (xx >= w ? w - 1 : xx);
                            float g = _gauss[k + _gr];
                            axx += g * ws.Jxx[xx];
                            axy += g * ws.Jxy[xx];
                            ayy += g * ws.Jyy[xx];
                        }
                        ws.Hxx[ho + x] = axx; ws.Hxy[ho + x] = axy; ws.Hyy[ho + x] = ayy;
                    }
                }
                for (int y = r0; y < r1; y++)
                {
                    int jo = (y - r0) * w;
                    for (int x = 0; x < w; x++)
                    {
                        float axx = 0, axy = 0, ayy = 0;
                        for (int k = -_gr; k <= _gr; k++)
                        {
                            int yy = y + k;
                            yy = yy < 0 ? 0 : (yy >= H ? H - 1 : yy);
                            int ho = (yy - t0) * w + x;
                            float g = _gauss[k + _gr];
                            axx += g * ws.Hxx[ho];
                            axy += g * ws.Hxy[ho];
                            ayy += g * ws.Hyy[ho];
                        }
                        ws.Jxx[jo + x] = axx; ws.Jxy[jo + x] = axy; ws.Jyy[jo + x] = ayy;
                    }
                }

                // ---- working-space planes for the filter windows ------------------------------------------
                int RH = _m.HalfRows;
                int planeRows = nb + 2 * RH;
                int planeSize = planeRows * w;
                for (int c = 0; c < _ch; c++)
                    for (int py = 0; py < planeRows; py++)
                    {
                        int y = Clamp(r0 - RH + py, 0, H - 1);
                        int o = c * planeSize + py * w;
                        for (int x = 0; x < w; x++) ws.Planes[o + x] = Sample(y, x, c);
                    }

                bool lumaNet = _m.HasNetwork && _o.UseNetwork && _o.NetworkOnLuma;
                if (lumaNet)
                {
                    int n = planeRows * w;
                    if (_cc >= 3)
                        for (int i = 0; i < n; i++)
                            ws.LumaPlane[i] = 0.299f * ws.Planes[i] + 0.587f * ws.Planes[planeSize + i] + 0.114f * ws.Planes[2 * planeSize + i];
                    else
                        Array.Copy(ws.Planes, ws.LumaPlane, n);
                }

                // ---- per output sample ------------------------------------------------------------------------
                var m = _m;
                int NT = m.Taps, cols = m.Cols, CL = m.LeftCols;
                float[] F = m.Filters;
                float[] h = ws.Filter, win = ws.Win;
                int strideE = NT, strideC = m.E * strideE, strideS = m.C * strideC, strideQ = m.S * strideS, strideP = m.Q * strideQ;
                int[] J0 = _j0, P0 = _p0, P1 = _p1;
                float[] PH = _phase, PF = _pf;
                for (int y = r0; y < r1; y++)
                {
                    int jo = (y - r0) * w;
                    int py0 = y - r0;                       // first plane row of this sample's window
                    if (_rowPositions != null)
                    {
                        _rowPositions(y, ws.Pos);
                        for (int x = 0; x < _W; x++)
                        {
                            double c = ws.Pos[x];
                            if (double.IsNaN(c)) c = -10;
                            c = Math.Max(-8, Math.Min(w + 7, c));
                            int j = (int)Math.Floor(c);
                            double ph = c - j;
                            ws.J0[x] = j;
                            ws.Phase[x] = (float)ph;
                            double pp = ph * m.P - 0.5;
                            int a = (int)Math.Floor(pp);
                            ws.Pf[x] = (float)(pp - a);
                            ws.P1[x] = Clamp(a + 1, 0, m.P - 1);
                            ws.P0[x] = Clamp(a, 0, m.P - 1);
                        }
                        J0 = ws.J0; P0 = ws.P0; P1 = ws.P1; PH = ws.Phase; PF = ws.Pf;
                    }
                    for (int x = 0; x < _W; x++)
                    {
                        int j0 = J0[x];
                        float f = PH[x];
                        int a = Clamp(j0, 0, w - 1), bb = Clamp(j0 + 1, 0, w - 1);
                        float Jxx = ws.Jxx[jo + a] * (1 - f) + ws.Jxx[jo + bb] * f;
                        float Jxy = ws.Jxy[jo + a] * (1 - f) + ws.Jxy[jo + bb] * f;
                        float Jyy = ws.Jyy[jo + a] * (1 - f) + ws.Jyy[jo + bb] * f;
                        double tr = (double)Jxx + Jyy;
                        double dd = Math.Sqrt(((double)Jxx - Jyy) * ((double)Jxx - Jyy) * 0.25 + (double)Jxy * Jxy);
                        double e1 = tr * 0.5 + dd, e2 = Math.Max(tr * 0.5 - dd, 0);
                        double theta = 0.5 * Math.Atan2(2.0 * Jxy, (double)Jxx - Jyy);
                        if (theta < 0) theta += Math.PI;
                        double s1 = Math.Sqrt(Math.Max(e1, 0)), s2 = Math.Sqrt(e2);
                        double coh = (s1 - s2) / (s1 + s2 + 1e-9);
                        int qa = Math.Min((int)(theta / Math.PI * m.Q), m.Q - 1);
                        int qs = CountBelow(m.StrengthEdges, s1);
                        int qc = CountBelow(m.CoherenceEdges, coh);
                        int baseIdx = qa * strideQ + qs * strideS + qc * strideC;
                        int i0 = P0[x] * strideP + baseIdx, i1 = P1[x] * strideP + baseIdx;
                        float fr = PF[x];
                        for (int t = 0; t < NT; t++) h[t] = F[i0 + t] * (1 - fr) + F[i1 + t] * fr;

                        // window row offsets (columns are clamped only near the left/right edges)
                        int x0 = j0 - CL;
                        bool interior = x0 >= 0 && x0 + cols <= w;
                        for (int r = 0; r < m.Rows; r++) ws.RowOff[r] = (py0 + r) * w;
                        for (int c = 0; c < _ch; c++)
                        {
                            int po = c * planeSize;
                            float lin = 0;
                            if (interior)
                            {
                                int t = 0;
                                for (int r = 0; r < m.Rows; r++)
                                {
                                    int ro = po + ws.RowOff[r] + x0;
                                    for (int k = 0; k < cols; k++) lin += h[t++] * ws.Planes[ro + k];
                                }
                            }
                            else
                            {
                                int t = 0;
                                for (int r = 0; r < m.Rows; r++)
                                {
                                    int ro = po + ws.RowOff[r];
                                    for (int k = 0; k < cols; k++)
                                    {
                                        int xx = x0 + k;
                                        xx = xx < 0 ? 0 : (xx >= w ? w - 1 : xx);
                                        lin += h[t++] * ws.Planes[ro + xx];
                                    }
                                }
                            }
                            ws.Lin[c] = lin < 0 ? 0 : (lin > 1 ? 1 : lin);   // the network was trained on the clipped filter output
                        }

                        if (!(m.HasNetwork && _o.UseNetwork))
                        {
                            for (int c = 0; c < _ch; c++) Store(y, x, c, ws.Lin[c]);
                        }
                        else if (lumaNet)
                        {
                            RefineLuma(ws, x0, w, planeSize, f, y, x);
                        }
                        else
                        {
                            for (int c = 0; c < _ch; c++)
                            {
                                bool isAlpha = _alpha && c == _ch - 1;
                                if (isAlpha) { Store(y, x, c, ws.Lin[c]); continue; }
                                Gather(ws.Planes, c * planeSize, ws, x0, w, win);
                                Store(y, x, c, Refine(win, ws.Lin[c], f, ws));
                            }
                        }
                    }
                }
            }

            private void Gather(float[] plane, int offset, Workspace ws, int x0, int w, float[] win)
            {
                int cols = _m.Cols, t = 0;
                for (int r = 0; r < _m.Rows; r++)
                {
                    int ro = offset + ws.RowOff[r];
                    for (int k = 0; k < cols; k++)
                    {
                        int xx = x0 + k;
                        xx = xx < 0 ? 0 : (xx >= w ? w - 1 : xx);
                        win[t++] = plane[ro + xx];
                    }
                }
            }

            /// <summary>
            /// Luma network: one network evaluation on the luma window; each colour channel gets the correction
            /// scaled by its local regression slope against luma (so colour edges keep their own contrast and sign).
            /// </summary>
            private void RefineLuma(Workspace ws, int x0, int w, int planeSize, float phase, int y, int x)
            {
                int NT = _m.Taps;
                float[] wl = ws.Win;
                Gather(ws.LumaPlane, 0, ws, x0, w, wl);
                float linL = _cc >= 3 ? 0.299f * ws.Lin[0] + 0.587f * ws.Lin[1] + 0.114f * ws.Lin[2] : ws.Lin[0];
                double sum = 0;
                for (int k = 0; k < NT; k++) sum += wl[k];
                float mu = (float)(sum / NT);
                double ss = 0;
                for (int k = 0; k < NT; k++) { double d = wl[k] - mu; ss += d * d; }
                float var = (float)(ss / NT);
                float sdRaw = (float)Math.Sqrt(var);
                if (sdRaw < _o.FlatSkip || sdRaw == 0f)
                {
                    for (int c = 0; c < _ch; c++) Store(y, x, c, ws.Lin[c]);
                    return;
                }
                float dl = Network(wl, mu, sdRaw, linL, phase, ws);
                int cols = _m.Cols;
                for (int c = 0; c < _ch; c++)
                {
                    bool isAlpha = _alpha && c == _ch - 1;
                    if (isAlpha || _cc < 3) { Store(y, x, c, isAlpha ? ws.Lin[c] : ws.Lin[c] + dl); continue; }
                    // slope = cov(channel, luma) / var(luma) over the window
                    int po = c * planeSize;
                    double sc = 0, scl = 0;
                    int t = 0;
                    for (int r = 0; r < _m.Rows; r++)
                    {
                        int ro = po + ws.RowOff[r];
                        for (int k = 0; k < cols; k++)
                        {
                            int xx = x0 + k;
                            xx = xx < 0 ? 0 : (xx >= w ? w - 1 : xx);
                            float v = ws.Planes[ro + xx];
                            sc += v;
                            scl += v * (wl[t++] - mu);
                        }
                    }
                    float slope = var > 1e-8f ? (float)(scl / NT) / var : 1f;
                    Store(y, x, c, ws.Lin[c] + slope * dl);
                }
            }

            /// <summary>Per-channel network correction.</summary>
            private float Refine(float[] win, float lin, float phase, Workspace ws)
            {
                int NT = _m.Taps;
                double sum = 0;
                for (int k = 0; k < NT; k++) sum += win[k];
                float mu = (float)(sum / NT);
                double ss = 0;
                for (int k = 0; k < NT; k++) { double d = win[k] - mu; ss += d * d; }
                float sdRaw = (float)Math.Sqrt(ss / NT);
                if (sdRaw < _o.FlatSkip) return lin;
                return lin + Network(win, mu, sdRaw, lin, phase, ws);
            }

            /// <summary>Evaluates the network on a contrast-normalised window; returns the correction in working units.</summary>
            private float Network(float[] win, float mu, float sdRaw, float lin, float phase, Workspace ws)
            {
                int NT = _m.Taps;
                float sd = sdRaw + 1e-3f;
                float inv = 1f / sd;
                float[] x = ws.X;
                for (int k = 0; k < NT; k++) x[k] = (win[k] - mu) * inv;
                x[NT] = (phase - 0.5f) * 2f;
                x[NT + 1] = (lin - mu) * inv;
                if (_m.NetworkHasScaleInput) x[NT + 2] = _scaleFeature;
                return sd * Forward(x, ws);
            }

            /// <summary>
            /// Network forward pass. Written with pointers (no bounds checks) and 4-way unrolling because it
            /// dominates the run time; everything else in the library is safe code.
            /// </summary>
            private unsafe float Forward(float[] x, Workspace ws)
            {
                var layers = _m.Layers;
                float leak = _m.Leak;
                float[] input = x;
                for (int li = 0; li < layers.Length; li++)
                {
                    var L = layers[li];
                    float[] o = ws.Act[li];
                    int nOut = L.Out, nIn = L.In;
                    bool hidden = li < layers.Length - 1;
                    // WT is [Out][In]: each output is a dot product over a contiguous weight row, with four
                    // outputs accumulated at once in registers (inputs are loaded once per four outputs).
                    fixed (float* po = o, pw = L.WT, pb = L.B, pin = input)
                    {
                        int j = 0;
                        for (; j + 3 < nOut; j += 4)
                        {
                            float* w0 = pw + j * nIn, w1 = w0 + nIn, w2 = w1 + nIn, w3 = w2 + nIn;
                            float a0 = pb[j], a1 = pb[j + 1], a2 = pb[j + 2], a3 = pb[j + 3];
                            for (int i = 0; i < nIn; i++)
                            {
                                float xi = pin[i];
                                a0 += xi * w0[i];
                                a1 += xi * w1[i];
                                a2 += xi * w2[i];
                                a3 += xi * w3[i];
                            }
                            if (hidden)
                            {
                                if (a0 < 0) a0 *= leak;
                                if (a1 < 0) a1 *= leak;
                                if (a2 < 0) a2 *= leak;
                                if (a3 < 0) a3 *= leak;
                            }
                            po[j] = a0; po[j + 1] = a1; po[j + 2] = a2; po[j + 3] = a3;
                        }
                        for (; j < nOut; j++)
                        {
                            float* wr = pw + j * nIn;
                            float a = pb[j];
                            for (int i = 0; i < nIn; i++) a += pin[i] * wr[i];
                            if (hidden && a < 0) a *= leak;
                            po[j] = a;
                        }
                    }
                    input = o;
                }
                return input[0];
            }


            private void Store(int y, int x, int c, float v)
            {
                if (_m.Space != LightSpace.Gamma)
                {
                    if (_m.Space == LightSpace.Sigmoidal) v = SigmoidCurve.Default.FromSigmoid(v);
                    v = Srgb.Encode(v);
                }
                v = v < 0 ? 0 : (v > 1 ? 1 : v);
                int o = (y * _W + x) * _ch + c;
                if (_dst.BitDepth == 8) _dst.Data8[o] = (byte)(v * _maxOut + 0.5f);
                else _dst.Data16[o] = (ushort)(v * _maxOut + 0.5f);
            }

            private static int CountBelow(float[] edges, double v)
            {
                int n = 0;
                for (int i = 0; i < edges.Length; i++) if (edges[i] < v) n++;
                return n;
            }

            private static int Clamp(int v, int lo, int hi)
            {
                return v < lo ? lo : (v > hi ? hi : v);
            }
        }
    }
}
