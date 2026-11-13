using System;
using System.Collections.Generic;

namespace Desqueeze.Core.Lens
{
    /// <summary>A point placed on a calibration line, in image pixels (pixel centres at whole numbers).</summary>
    [Serializable]
    public struct LinePoint
    {
        public double x, y;

        public LinePoint(double x, double y)
        {
            this.x = x;
            this.y = y;
        }
    }

    /// <summary>Points along something that is straight in reality: a horizontal or a vertical line in the photo.</summary>
    [Serializable]
    public sealed class CalibrationLine
    {
        /// <summary>"horizontal" or "vertical".</summary>
        public string orientation = "horizontal";
        public List<LinePoint> points = new List<LinePoint>();

        public bool IsHorizontal => orientation != "vertical";

        public CalibrationLine Clone()
        {
            return new CalibrationLine { orientation = orientation, points = new List<LinePoint>(points) };
        }
    }

    public sealed class DistortionFitResult
    {
        public DistortionModel Model;
        /// <summary>Which model was fitted, in words.</summary>
        public string Kind;
        /// <summary>Straightness error (RMS distance of the points from their lines) before and after correction, in pixels of the de-squeezed image.</summary>
        public double RmsBefore, RmsAfter;
        /// <summary>Per line, RMS error after correction (pixels); NaN for lines with fewer than 3 points.</summary>
        public double[] LineRms;
        /// <summary>Per line and point, the signed distance from the corrected line (pixels), for highlighting misplaced points.</summary>
        public double[][] PointError;
        public int Lines, Points;
        /// <summary>Which candidate model won (0 = plain radial; see <see cref="DistortionFit"/>).</summary>
        public int Rung;
        public string Warning;
    }

    /// <summary>Tuning of <see cref="DistortionFit"/>; the defaults are what the app uses.</summary>
    public sealed class DistortionFitOptions
    {
        /// <summary>Most complex model the fit may choose: 0 = plain radial ... <see cref="DistortionFit.RungCount"/> - 1 = everything.</summary>
        public int MaxRung = int.MaxValue;
        /// <summary>Ridge on the general terms of the lowest degree, and its growth per degree (a prior that distortion is mostly low-order).</summary>
        public double FreeRidge = 0.02, FreeRidgeGrowth = 1.3;
        /// <summary>
        /// How firmly the terms that level and upright lines cannot see are held to what the radial pattern implies
        /// (see <see cref="DistortionFit"/>). Strong, so they don't soak up noise; lines at other angles can still move them.
        /// </summary>
        public double HiddenRidge = 0.3;
        /// <summary>Ridge on the 2nd-order radial coefficient and on the difference between the axes' radial coefficients (1st, 2nd order).</summary>
        public double SecondOrderRidge = 2e-4, SplitRidge = 2e-5, SplitRidge2 = 2e-4;
        /// <summary>Precision of a placed point, in de-squeezed pixels: improvements in straightness below this don't justify a more complex model.</summary>
        public double PointPrecision = 0.2;
        /// <summary>Points further than this (pixels) from their corrected line count less (Huber).</summary>
        public double OutlierPixels = 2.5;
        /// <summary>A candidate needs this many points (beyond two per line) for each of its terms, and the general ones this many lines.</summary>
        public double PointsPerTerm = 2;
        public int MinLinesForGeneral = 2;
        /// <summary>Writes each candidate's score to the console (for the bench).</summary>
        public bool Trace;
    }

    /// <summary>
    /// Fits a <see cref="DistortionModel"/> to lines that should be straight (the plumb-line method): the coefficients are
    /// chosen so that, once corrected, the points of each line lie on a straight line. Straightness does not depend on
    /// perspective, so the wall need not be photographed square-on, and the lines may run at any angle.
    ///
    /// The model is as simple as the points allow and as complex as they demand. Candidates, simplest first: one radial
    /// coefficient; two; separate ones for the horizontal and the vertical displacement (anamorphic lenses bend the two
    /// differently); then general polynomials, first with only the terms a centred lens can have (symmetric about both
    /// axes: x (a x^2 + b y^2 + c x^4 + d x^2 y^2 + e y^4 ...) and the same for y, the form used for anamorphic lenses in
    /// film work), then with every term, up to 7th degree. The general terms are what straightens a line that bows one way
    /// in the middle of the frame and the other way at its sides, lines that bow by amounts no radial curve follows, and
    /// bending that is not the same left and right or top and bottom. Each candidate is fitted to all the points; the one
    /// with the best information criterion wins, so extra terms are used only when the lines really show what they
    /// describe.
    ///
    /// Level and upright lines cannot show everything. A horizontal line stays straight whatever the lens does to the
    /// spacing of points along it, or however it shifts and tilts each line as a whole: in the vertical displacement, the
    /// terms with x to a power below 2 are hidden from them, and likewise the terms of the horizontal displacement with y
    /// to a power below 2 from vertical lines. Those hidden terms decide whether diagonals come out straight, so they are
    /// not left to chance: each is tied to the value the radial pattern implies from the terms that can be seen (the x^3
    /// term of the horizontal displacement equals its x y^2 term, as it does for any radial distortion, and so on up the
    /// orders), and the ones that have no radial counterpart are held at zero. Lines at other angles can still move
    /// them. A robust (Huber) weighting keeps a single misplaced point from dragging the fit.
    /// </summary>
    public static class DistortionFit
    {
        // ------------------------------------------------------------------ the candidate models

        private const int Shared = 0, Split = 1, MonoX = 2, MonoY = 3;

        /// <summary>One adjustable part of the model: a displacement field phi(x, y).</summary>
        private struct Basis
        {
            public int Kind, M, I, J;
            public double Scale;
            public bool Symmetric, Hidden;
        }

        /// <summary>A prior: Weight * sum(Coef[k] * p[Index[k]]) is added to the residuals.</summary>
        private sealed class Prior
        {
            public double Weight;
            public int[] Index;
            public double[] Coef;
        }

        /// <summary>Number of candidate models (see <see cref="DistortionFitOptions.MaxRung"/>).</summary>
        public const int RungCount = 11;

        private static readonly string[] RungNames =
        {
            "radial, 1st order",
            "radial, 2nd order",
            "separate horizontal and vertical, 1st order",
            "separate horizontal and vertical, 2nd order",
            "general, symmetric, 5th degree",
            "general, symmetric 5th degree with 3rd-degree asymmetry",
            "general, symmetric, 7th degree",
            "general, symmetric 7th degree with 3rd-degree asymmetry",
            "general, 5th degree",
            "general, 5th degree with symmetric 7th",
            "general, 7th degree",
        };

        // per rung: radial order, separate axes, symmetric polynomial degree, full polynomial degree
        private static readonly int[][] RungSpec =
        {
            new[] { 1, 0, 0, 0 }, new[] { 2, 0, 0, 0 }, new[] { 1, 1, 0, 0 }, new[] { 2, 1, 0, 0 },
            new[] { 0, 0, 5, 0 }, new[] { 0, 0, 5, 3 }, new[] { 0, 0, 7, 0 }, new[] { 0, 0, 7, 3 },
            new[] { 0, 0, 5, 5 }, new[] { 0, 0, 7, 5 }, new[] { 0, 0, 7, 7 },
        };

        private static bool IsGeneral(int rung)
        {
            return RungSpec[rung][2] > 0;
        }

        private static Basis[] BuildBases(double halfHeight)
        {
            var list = new List<Basis>();
            list.Add(new Basis { Kind = Shared, M = 1 });
            list.Add(new Basis { Kind = Shared, M = 2 });
            list.Add(new Basis { Kind = Split, M = 1 });
            list.Add(new Basis { Kind = Split, M = 2 });
            for (int deg = 2; deg <= 7; deg++)
                for (int i = deg; i >= 0; i--)
                {
                    int j = deg - i;
                    double scale = Math.Pow(halfHeight, -j);             // so the coefficient is the displacement at the frame's edge
                    list.Add(new Basis { Kind = MonoX, I = i, J = j, Scale = scale, Symmetric = i % 2 == 1 && j % 2 == 0, Hidden = j < 2 });
                    list.Add(new Basis { Kind = MonoY, I = i, J = j, Scale = scale, Symmetric = i % 2 == 0 && j % 2 == 1, Hidden = i < 2 });
                }
            return list.ToArray();
        }

        private static int Find(Basis[] bases, int kind, int i, int j)
        {
            for (int k = 0; k < bases.Length; k++)
                if (bases[k].Kind == kind && bases[k].I == i && bases[k].J == j) return k;
            return -1;
        }

        private static bool[] RungMask(Basis[] bases, int rung)
        {
            var spec = RungSpec[rung];
            var m = new bool[bases.Length];
            for (int k = 0; k < bases.Length; k++)
            {
                var b = bases[k];
                if (b.Kind == Shared) m[k] = b.M <= spec[0];
                else if (b.Kind == Split) m[k] = spec[1] == 1 && b.M <= spec[0];
                else
                {
                    int deg = b.I + b.J;
                    m[k] = deg <= spec[3] || (b.Symmetric && deg <= spec[2]);
                }
            }
            return m;
        }

        /// <summary>The priors of a rung: a light ridge on what the lines can see, and the hidden terms tied to the radial pattern.</summary>
        private static List<Prior> BuildPriors(Basis[] bases, bool[] mask, double halfHeight, DistortionFitOptions o, bool measuresX, bool measuresY)
        {
            var list = new List<Prior>();
            for (int k = 0; k < bases.Length; k++)
            {
                if (!mask[k]) continue;
                var b = bases[k];
                if (b.Kind == Shared)
                {
                    list.Add(new Prior { Weight = b.M == 1 ? 1e-6 : o.SecondOrderRidge, Index = new[] { k }, Coef = new[] { 1.0 } });
                    continue;
                }
                if (b.Kind == Split)
                {
                    list.Add(new Prior { Weight = b.M == 1 ? o.SplitRidge : o.SplitRidge2, Index = new[] { k }, Coef = new[] { 1.0 } });
                    continue;
                }
                int deg = b.I + b.J;
                if (!b.Hidden)
                {
                    // A displacement no line measures (the horizontal one without vertical lines, or the reverse) mirrors
                    // the measured one, x and y exchanged, as it would for a radial lens.
                    bool measured = b.Kind == MonoX ? measuresX : measuresY;
                    int mirror = measured ? -1 : Find(bases, b.Kind == MonoX ? MonoY : MonoX, b.J, b.I);
                    if (mirror >= 0 && mask[mirror] && !bases[mirror].Hidden)
                        list.Add(new Prior { Weight = o.HiddenRidge, Index = new[] { k, mirror }, Coef = new[] { 1.0, -bases[mirror].Scale / b.Scale } });
                    else
                        list.Add(new Prior { Weight = o.FreeRidge * Math.Pow(o.FreeRidgeGrowth, deg - 2), Index = new[] { k }, Coef = new[] { 1.0 } });
                    continue;
                }
                // Hidden. The pure odd power (x^(2n+1) in x, y^(2n+1) in y) follows the radial pattern
                // K (x^2 + y^2)^n: its coefficient is 1/n of the term with the other variable squared.
                int partner = -1;
                double ratio = 0;
                if (b.Symmetric && deg >= 3)
                {
                    int n = (deg - 1) / 2;
                    if (b.Kind == MonoX && b.J == 0)
                    {
                        partner = Find(bases, MonoX, deg - 2, 2);
                        ratio = Math.Pow(halfHeight, -2) / n;       // in the scaled coefficients: c = p * Scale
                    }
                    else if (b.Kind == MonoY && b.I == 0)
                    {
                        partner = Find(bases, MonoY, 2, deg - 2);
                        ratio = Math.Pow(halfHeight, 2) / n;
                    }
                    if (partner >= 0 && !mask[partner]) partner = -1;
                }
                list.Add(partner >= 0
                    ? new Prior { Weight = o.HiddenRidge, Index = new[] { k, partner }, Coef = new[] { 1.0, -ratio } }
                    : new Prior { Weight = o.HiddenRidge, Index = new[] { k }, Coef = new[] { 1.0 } });
            }
            return list;
        }

        private static DistortionModel BuildModel(Basis[] bases, double[] p)
        {
            var m = new DistortionModel();
            var terms = new Dictionary<int, DistortionTerm>();
            for (int k = 0; k < bases.Length; k++)
            {
                if (p[k] == 0) continue;
                var b = bases[k];
                switch (b.Kind)
                {
                    case Shared: m.Kx[b.M - 1] += p[k]; m.Ky[b.M - 1] += p[k]; break;
                    case Split: m.Kx[b.M - 1] += p[k]; m.Ky[b.M - 1] -= p[k]; break;
                    default:
                        int key = b.I * 16 + b.J;
                        DistortionTerm t;
                        if (!terms.TryGetValue(key, out t)) t = new DistortionTerm(b.I, b.J, 0, 0);
                        if (b.Kind == MonoX) t.X += p[k] * b.Scale; else t.Y += p[k] * b.Scale;
                        terms[key] = t;
                        break;
                }
            }
            var keys = new List<int>(terms.Keys);
            keys.Sort((x, y) => (x / 16 + x % 16) != (y / 16 + y % 16) ? (x / 16 + x % 16).CompareTo(y / 16 + y % 16) : y.CompareTo(x));
            m.Terms = new DistortionTerm[keys.Count];
            for (int i = 0; i < keys.Count; i++) m.Terms[i] = terms[keys[i]];
            return m;
        }

        /// <summary>
        /// A model as the parameters of a general rung: its radial coefficients expanded into monomials
        /// (x r^2 = x^3 + x y^2, x r^4 = x^5 + 2 x^3 y^2 + x y^4), terms the rung lacks dropped.
        /// </summary>
        private static double[] ParametersOf(DistortionModel m, Basis[] bases, bool[] mask)
        {
            var p = new double[bases.Length];
            Action<int, int, int, double> add = (kind, i, j, c) =>
            {
                int k = Find(bases, kind, i, j);
                if (k >= 0 && mask[k]) p[k] += c / bases[k].Scale;
            };
            add(MonoX, 3, 0, m.Kx[0]); add(MonoX, 1, 2, m.Kx[0]);
            add(MonoY, 2, 1, m.Ky[0]); add(MonoY, 0, 3, m.Ky[0]);
            add(MonoX, 5, 0, m.Kx[1]); add(MonoX, 3, 2, 2 * m.Kx[1]); add(MonoX, 1, 4, m.Kx[1]);
            add(MonoY, 4, 1, m.Ky[1]); add(MonoY, 2, 3, 2 * m.Ky[1]); add(MonoY, 0, 5, m.Ky[1]);
            if (m.Terms != null)
                foreach (var t in m.Terms)
                {
                    add(MonoX, t.I, t.J, t.X);
                    add(MonoY, t.I, t.J, t.Y);
                }
            return p;
        }

        /// <summary>The displacement each active basis gives at (x, y).</summary>
        private static void BasisValues(Basis[] bases, int[] active, double x, double y, double[] fx, double[] fy)
        {
            double r2 = x * x + y * y;
            var px = new double[8];
            var py = new double[8];
            px[0] = py[0] = 1;
            for (int i = 1; i < 8; i++) { px[i] = px[i - 1] * x; py[i] = py[i - 1] * y; }
            for (int a = 0; a < active.Length; a++)
            {
                var b = bases[active[a]];
                switch (b.Kind)
                {
                    case Shared:
                    case Split:
                        double rr = b.M == 1 ? r2 : r2 * r2;
                        fx[a] = x * rr;
                        fy[a] = b.Kind == Shared ? y * rr : -y * rr;
                        break;
                    case MonoX:
                        fx[a] = b.Scale * px[b.I] * py[b.J];
                        fy[a] = 0;
                        break;
                    default:
                        fx[a] = 0;
                        fy[a] = b.Scale * px[b.I] * py[b.J];
                        break;
                }
            }
        }

        // ------------------------------------------------------------------ the fit

        /// <param name="lines">Lines in pixel coordinates of an image in sensor orientation (squeeze along x).</param>
        /// <param name="width">Image width in pixels.</param>
        /// <param name="height">Image height in pixels.</param>
        /// <param name="factor">Squeeze factor of the image.</param>
        public static DistortionFitResult Fit(IList<CalibrationLine> lines, int width, int height, double factor, DistortionFitOptions options = null)
        {
            var o = options ?? new DistortionFitOptions();
            var frame = new LensFrame(width, height, factor);
            double pxPerUnit = width * factor / 2;    // de-squeezed pixels per normalised unit

            // Normalised points of the usable lines (3+ points).
            var data = new List<double[][]>();
            var index = new List<int>();
            bool hasH = false, hasV = false;
            int nPts = 0, constraints = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                if (l == null || l.points == null || l.points.Count < 3) continue;
                var pts = new double[l.points.Count][];
                for (int j = 0; j < pts.Length; j++)
                {
                    double x, y;
                    frame.ToNormalised(l.points[j].x, l.points[j].y, out x, out y);
                    pts[j] = new[] { x, y };
                }
                data.Add(pts);
                index.Add(i);
                if (l.IsHorizontal) hasH = true; else hasV = true;
                nPts += pts.Length;
                constraints += pts.Length - 2;
            }

            var result = new DistortionFitResult
            {
                Lines = data.Count,
                Points = nPts,
                LineRms = new double[lines.Count],
                PointError = new double[lines.Count][],
            };
            for (int i = 0; i < lines.Count; i++) result.LineRms[i] = double.NaN;
            if (data.Count == 0)
            {
                result.Model = new DistortionModel();
                result.Kind = "none";
                result.Warning = "Add at least one line with three or more points.";
                return result;
            }

            double hReach, hSpan, vReach, vSpan;
            Coverage(data, lines, index, frame, out hReach, out hSpan, out vReach, out vSpan);
            bool hOk = hasH && hReach >= 0.4 && hSpan >= 0.4, vOk = hasV && vReach >= 0.4 && vSpan >= 0.4;

            var bases = BuildBases(frame.HalfHeight);
            double huber = o.OutlierPixels / pxPerUnit;
            double floor = o.PointPrecision / pxPerUnit;
            int dof = Math.Max(1, constraints);

            // Candidates, simplest first. A direction only gets its own coefficients when its lines can measure it (long,
            // and away from the centre); the general terms need both directions and enough points to pin them down. Among
            // the candidates the data chooses (Bayesian information criterion, with the precision of a placed point as a
            // floor so that straightness finer than anyone can click doesn't count).
            var radial = new List<KeyValuePair<bool[], double[]>>();
            double bestBic = double.MaxValue, bestRss = double.MaxValue;
            double[] best = null;
            DistortionModel closest = null;       // the fit with the smallest error so far: where the next general fit starts
            int bestRung = 0;
            int top = Math.Min(RungCount - 1, Math.Max(0, o.MaxRung));
            for (int rung = 0; rung <= top; rung++)
            {
                var spec = RungSpec[rung];
                var mask = RungMask(bases, rung);
                int count = 0;
                for (int k = 0; k < mask.Length; k++) if (mask[k] && !bases[k].Hidden) count++;
                bool general = IsGeneral(rung);
                if (rung > 0)
                {
                    if (spec[1] == 1 && !(hOk && vOk)) continue;
                    if (general && !(hOk || vOk)) continue;
                    if (constraints < o.PointsPerTerm * count) continue;
                    if (general && data.Count < o.MinLinesForGeneral) continue;
                }
                double[] p;
                if (general)
                {
                    p = ParametersOf(closest ?? new DistortionModel(), bases, mask);
                }
                else
                {
                    // Start from the richest simpler radial model already fitted.
                    p = new double[bases.Length];
                    int startCount = -1;
                    foreach (var sol in radial)
                    {
                        bool subset = true;
                        int n = 0;
                        for (int k = 0; k < mask.Length; k++)
                            if (sol.Key[k]) { n++; if (!mask[k]) { subset = false; break; } }
                        if (subset && n > startCount) { startCount = n; Array.Copy(sol.Value, p, p.Length); }
                    }
                }
                var priors = BuildPriors(bases, mask, frame.HalfHeight, o, vOk, hOk);
                double rss = FitRobust(bases, mask, priors, p, data, huber);
                if (o.Trace)
                    Console.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "    rung {0,2} ({1,2} terms): rms {2:0.000} px, bic {3:0.0}  {4}",
                        rung, count, Math.Sqrt(rss / dof) * pxPerUnit, dof * Math.Log(rss / dof + floor * floor) + count * Math.Log(dof), RungNames[rung]));
                if (double.IsNaN(rss) || double.IsInfinity(rss)) continue;
                if (!general) radial.Add(new KeyValuePair<bool[], double[]>(mask, (double[])p.Clone()));
                if (rss < bestRss)
                {
                    bestRss = rss;
                    closest = BuildModel(bases, p);
                }
                double bic = dof * Math.Log(rss / dof + floor * floor) + count * Math.Log(dof);
                if (bic < bestBic - 1e-9)
                {
                    bestBic = bic;
                    best = p;
                    bestRung = rung;
                }
            }

            var final = BuildModel(bases, best);
            double ssB = 0, ssA = 0;
            var zero = new DistortionModel();
            for (int l = 0; l < data.Count; l++)
            {
                var before = LineDistances(zero, data[l]);
                var after = LineDistances(final, data[l]);
                double s = 0;
                var pe = new double[after.Length];
                for (int j = 0; j < after.Length; j++)
                {
                    ssB += before[j] * before[j];
                    ssA += after[j] * after[j];
                    s += after[j] * after[j];
                    pe[j] = after[j] * pxPerUnit;
                }
                result.LineRms[index[l]] = Math.Sqrt(s / after.Length) * pxPerUnit;
                result.PointError[index[l]] = pe;
            }
            result.Model = final;
            result.RmsBefore = Math.Sqrt(ssB / nPts) * pxPerUnit;
            result.RmsAfter = Math.Sqrt(ssA / nPts) * pxPerUnit;
            result.Kind = RungNames[bestRung];
            result.Rung = bestRung;
            result.Warning = CoverageWarning(hReach, hSpan, vReach, vSpan, hasH, hasV, RungSpec[bestRung][1] == 1 || IsGeneral(bestRung));
            return result;
        }

        /// <summary>
        /// How far the lines reach: for horizontal lines, the largest distance from the centre (fraction of the half-height)
        /// and the longest half-span (fraction of the half-width); the same for vertical lines with the axes swapped.
        /// </summary>
        private static void Coverage(List<double[][]> data, IList<CalibrationLine> lines, List<int> index, LensFrame frame,
            out double hReach, out double hSpan, out double vReach, out double vSpan)
        {
            hReach = hSpan = vReach = vSpan = 0;
            double hh = frame.HalfHeight;
            for (int l = 0; l < data.Count; l++)
            {
                double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
                foreach (var p in data[l]) { x0 = Math.Min(x0, p[0]); x1 = Math.Max(x1, p[0]); y0 = Math.Min(y0, p[1]); y1 = Math.Max(y1, p[1]); }
                if (lines[index[l]].IsHorizontal)
                {
                    hReach = Math.Max(hReach, Math.Max(Math.Abs(y0), Math.Abs(y1)) / hh);
                    hSpan = Math.Max(hSpan, (x1 - x0) / 2);
                }
                else
                {
                    vReach = Math.Max(vReach, Math.Max(Math.Abs(x0), Math.Abs(x1)));
                    vSpan = Math.Max(vSpan, (y1 - y0) / (2 * hh));
                }
            }
        }

        /// <summary>Advice when the lines can't measure the lens well: they must be long and reach towards the frame's edges.</summary>
        private static string CoverageWarning(double hReach, double hSpan, double vReach, double vSpan, bool hasH, bool hasV, bool separate)
        {
            if (!hasH || !hasV)
                return "Only " + (hasH ? "horizontal" : "vertical") + " lines: both directions get the same correction. Add " +
                       (hasH ? "vertical" : "horizontal") + " lines to measure them separately.";
            var tips = new List<string>();
            if (hReach < 0.6 || hSpan < 0.6) tips.Add("a long horizontal line near the top or bottom edge");
            if (vReach < 0.6 || vSpan < 0.6) tips.Add("a tall vertical line near the left or right edge");
            if (tips.Count == 0) return null;
            return (separate ? "The lines don't reach far enough to measure the corners precisely" :
                       "The lines don't reach far enough to measure horizontal and vertical bending separately, so both get the same correction") +
                   ". Add " + string.Join(" and ", tips) + ".";
        }

        /// <summary>
        /// The straight line a calibration line becomes after correction, drawn back onto the uncorrected image: a curve
        /// through which the points should run if the model explains them. Points in pixel coordinates; null if the line
        /// has fewer than 2 points.
        /// </summary>
        public static List<LinePoint> FittedCurve(CalibrationLine line, DistortionModel model, int width, int height, double factor, int samples = 64)
        {
            if (line == null || line.points.Count < 2) return null;
            var frame = new LensFrame(width, height, factor);
            var pts = new double[line.points.Count][];
            for (int j = 0; j < pts.Length; j++)
            {
                double x, y, xu, yu;
                frame.ToNormalised(line.points[j].x, line.points[j].y, out x, out y);
                model.Undistort(x, y, out xu, out yu);
                pts[j] = new[] { xu, yu };
            }
            double cx, cy, dx, dy;
            FitLine(pts, null, out cx, out cy, out dx, out dy);
            double tmin = double.MaxValue, tmax = double.MinValue;
            foreach (var p in pts)
            {
                double t = (p[0] - cx) * dx + (p[1] - cy) * dy;
                tmin = Math.Min(tmin, t);
                tmax = Math.Max(tmax, t);
            }
            double ext = 0.05 * (tmax - tmin);
            tmin -= ext;
            tmax += ext;
            var curve = new List<LinePoint>(samples + 1);
            for (int i = 0; i <= samples; i++)
            {
                double t = tmin + (tmax - tmin) * i / samples;
                double xd, yd, px, py;
                model.Distort(cx + t * dx, cy + t * dy, out xd, out yd);
                frame.ToPixel(xd, yd, out px, out py);
                curve.Add(new LinePoint(px, py));
            }
            return curve;
        }

        // ------------------------------------------------------------------ internals

        /// <summary>
        /// Iteratively reweighted (Huber) least squares over the active parameters; returns the weighted sum of squared
        /// distances (normalised units), or NaN if the fit failed.
        /// </summary>
        private static double FitRobust(Basis[] bases, bool[] mask, List<Prior> priors, double[] p, List<double[][]> data, double huber)
        {
            var active = new List<int>();
            for (int k = 0; k < mask.Length; k++) if (mask[k]) active.Add(k);
            var act = active.ToArray();
            var slot = new int[bases.Length];
            for (int k = 0; k < slot.Length; k++) slot[k] = -1;
            for (int a = 0; a < act.Length; a++) slot[act[a]] = a;
            var weights = new List<double[]>();
            foreach (var pts in data) { var w = new double[pts.Length]; for (int j = 0; j < w.Length; j++) w[j] = 1; weights.Add(w); }
            double rss = double.NaN;
            for (int round = 0; round < 4; round++)
            {
                if (!GaussNewton(bases, act, slot, priors, p, data, weights)) return double.NaN;
                var model = BuildModel(bases, p);
                rss = 0;
                for (int l = 0; l < data.Count; l++)
                {
                    var d = LineDistances(model, data[l], weights[l]);
                    for (int j = 0; j < d.Length; j++)
                    {
                        double a = Math.Abs(d[j]);
                        rss += weights[l][j] * d[j] * d[j];
                        weights[l][j] = a <= huber ? 1 : huber / a;
                    }
                }
            }
            return rss;
        }

        /// <summary>
        /// Levenberg-Marquardt over the active model parameters and, for every line, its direction and offset. The residual
        /// of a point is its distance from its line once corrected; the derivatives are analytic (the corrected point
        /// depends on the parameters through the inverse of the lens function, so by the implicit function theorem
        /// du/dp = -J^-1 phi(u)).
        /// </summary>
        private static bool GaussNewton(Basis[] bases, int[] act, int[] slot, List<Prior> priors, double[] p, List<double[][]> data, List<double[]> weights)
        {
            int P = act.Length, L = data.Count, N = P + 2 * L;
            var lineParams = new double[2 * L];
            var model = BuildModel(bases, p);
            // lines through the points as corrected now
            for (int l = 0; l < L; l++)
            {
                var u = Corrected(model, data[l]);
                if (u == null) return false;
                double cx, cy, dx, dy;
                FitLine(u, weights[l], out cx, out cy, out dx, out dy);
                double ang = Math.Atan2(dy, dx);
                lineParams[2 * l] = ang;
                lineParams[2 * l + 1] = -Math.Sin(ang) * cx + Math.Cos(ang) * cy;
            }
            double cost = Cost(priors, p, lineParams, data, weights, model);
            if (double.IsInfinity(cost)) return false;
            double lambda = 1e-3;
            var fx = new double[P];
            var fy = new double[P];
            var row = new double[P + 2];
            var trial = (double[])p.Clone();
            var trialLines = new double[2 * L];
            for (int iter = 0; iter < 60; iter++)
            {
                var A = new double[N, N];
                var g = new double[N];
                for (int l = 0; l < L; l++)
                {
                    double ang = lineParams[2 * l], rho = lineParams[2 * l + 1];
                    double sn = Math.Sin(ang), cs = Math.Cos(ang);
                    int ia = P + 2 * l;
                    var pts = data[l];
                    for (int j = 0; j < pts.Length; j++)
                    {
                        double ux, uy, qx, qy, j00, j01, j10, j11;
                        if (!model.Undistort(pts[j][0], pts[j][1], out ux, out uy)) return false;
                        model.Distort(ux, uy, out qx, out qy, out j00, out j01, out j10, out j11);
                        double det = j00 * j11 - j01 * j10;
                        if (Math.Abs(det) < 1e-9) return false;
                        // n . J^-1 as a row vector: n = (-sin, cos)
                        double gx = (-sn * j11 - cs * j10) / det, gy = (sn * j01 + cs * j00) / det;
                        BasisValues(bases, act, ux, uy, fx, fy);
                        double sw = Math.Sqrt(weights[l][j]);
                        double r = sw * (-sn * ux + cs * uy - rho);
                        for (int a = 0; a < P; a++) row[a] = -sw * (gx * fx[a] + gy * fy[a]);
                        row[P] = sw * (-cs * ux - sn * uy);
                        row[P + 1] = -sw;
                        for (int a = 0; a < P + 2; a++)
                        {
                            double ra = row[a];
                            if (ra == 0) continue;
                            int ka = a < P ? a : ia + (a - P);
                            g[ka] += ra * r;
                            for (int b = a; b < P + 2; b++)
                            {
                                int kb = b < P ? b : ia + (b - P);
                                A[ka, kb] += ra * row[b];
                            }
                        }
                    }
                }
                foreach (var pr in priors)
                {
                    double r = 0;
                    for (int k = 0; k < pr.Index.Length; k++) r += pr.Coef[k] * p[pr.Index[k]];
                    r *= pr.Weight;
                    for (int k = 0; k < pr.Index.Length; k++)
                    {
                        int a = slot[pr.Index[k]];
                        double ja = pr.Weight * pr.Coef[k];
                        g[a] += ja * r;
                        for (int k2 = 0; k2 < pr.Index.Length; k2++)
                        {
                            int b = slot[pr.Index[k2]];
                            if (b >= a) A[a, b] += ja * pr.Weight * pr.Coef[k2];
                        }
                    }
                }
                for (int a = 0; a < N; a++)
                    for (int b = 0; b < a; b++) A[a, b] = A[b, a];

                bool improved = false;
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    var M = (double[,])A.Clone();
                    var rhs = new double[N];
                    for (int a = 0; a < N; a++)
                    {
                        M[a, a] += lambda * (A[a, a] + 1e-18);
                        rhs[a] = -g[a];
                    }
                    var delta = Solve(M, rhs);
                    if (delta == null) { lambda *= 10; continue; }
                    Array.Copy(p, trial, p.Length);
                    for (int a = 0; a < P; a++) trial[act[a]] = p[act[a]] + delta[a];
                    for (int a = 0; a < 2 * L; a++) trialLines[a] = lineParams[a] + delta[P + a];
                    var trialModel = BuildModel(bases, trial);
                    double c2 = Cost(priors, trial, trialLines, data, weights, trialModel);
                    if (c2 < cost)
                    {
                        double step = 0;
                        for (int a = 0; a < N; a++) step = Math.Max(step, Math.Abs(delta[a]));
                        Array.Copy(trial, p, p.Length);
                        Array.Copy(trialLines, lineParams, 2 * L);
                        model = trialModel;
                        bool done = cost - c2 < 1e-12 * (1e-12 + cost) || step < 1e-11;
                        cost = c2;
                        lambda = Math.Max(1e-12, lambda * 0.3);
                        improved = true;
                        if (done) return true;
                        break;
                    }
                    lambda *= 10;
                }
                if (!improved) return true;
            }
            return true;
        }

        private static double Cost(List<Prior> priors, double[] p, double[] lineParams, List<double[][]> data, List<double[]> weights, DistortionModel model)
        {
            double c = 0;
            for (int l = 0; l < data.Count; l++)
            {
                double ang = lineParams[2 * l], rho = lineParams[2 * l + 1];
                double sn = Math.Sin(ang), cs = Math.Cos(ang);
                var pts = data[l];
                for (int j = 0; j < pts.Length; j++)
                {
                    double ux, uy;
                    if (!model.Undistort(pts[j][0], pts[j][1], out ux, out uy)) return double.PositiveInfinity;
                    double r = -sn * ux + cs * uy - rho;
                    c += weights[l][j] * r * r;
                }
            }
            foreach (var pr in priors)
            {
                double r = 0;
                for (int k = 0; k < pr.Index.Length; k++) r += pr.Coef[k] * p[pr.Index[k]];
                r *= pr.Weight;
                c += r * r;
            }
            return c;
        }

        private static double[][] Corrected(DistortionModel m, double[][] pts)
        {
            var u = new double[pts.Length][];
            for (int j = 0; j < pts.Length; j++)
            {
                double xu, yu;
                if (!m.Undistort(pts[j][0], pts[j][1], out xu, out yu)) return null;
                u[j] = new[] { xu, yu };
            }
            return u;
        }

        /// <summary>Signed distances of the corrected points from their best-fit straight line (normalised units).</summary>
        private static double[] LineDistances(DistortionModel m, double[][] pts, double[] w = null)
        {
            var u = new double[pts.Length][];
            for (int j = 0; j < pts.Length; j++)
            {
                double xu, yu;
                m.Undistort(pts[j][0], pts[j][1], out xu, out yu);
                u[j] = new[] { xu, yu };
            }
            double cx, cy, dx, dy;
            FitLine(u, w, out cx, out cy, out dx, out dy);
            var d = new double[pts.Length];
            for (int j = 0; j < pts.Length; j++) d[j] = -(u[j][0] - cx) * dy + (u[j][1] - cy) * dx;
            return d;
        }

        /// <summary>Total-least-squares line: centroid (cx, cy) and unit direction (dx, dy).</summary>
        private static void FitLine(double[][] p, double[] w, out double cx, out double cy, out double dx, out double dy)
        {
            double sw = 0, sx = 0, sy = 0;
            for (int i = 0; i < p.Length; i++)
            {
                double wi = w != null ? w[i] : 1;
                sw += wi; sx += wi * p[i][0]; sy += wi * p[i][1];
            }
            cx = sx / sw;
            cy = sy / sw;
            double xx = 0, xy = 0, yy = 0;
            for (int i = 0; i < p.Length; i++)
            {
                double wi = w != null ? w[i] : 1;
                double ax = p[i][0] - cx, ay = p[i][1] - cy;
                xx += wi * ax * ax; xy += wi * ax * ay; yy += wi * ay * ay;
            }
            double angle = 0.5 * Math.Atan2(2 * xy, xx - yy);   // direction of the largest spread
            dx = Math.Cos(angle);
            dy = Math.Sin(angle);
            // Orient it from the first point to the last. atan2 flips the direction of a near-vertical line whenever the
            // tiny cross term changes sign, which would flip the sign of every residual of the line between two
            // evaluations and stall the optimiser short of the best fit.
            if (p.Length >= 2)
            {
                double ex = p[p.Length - 1][0] - p[0][0], ey = p[p.Length - 1][1] - p[0][1];
                if (dx * ex + dy * ey < 0)
                {
                    dx = -dx;
                    dy = -dy;
                }
            }
        }

        private static double[] Solve(double[,] A, double[] b)
        {
            int n = b.Length;
            var M = (double[,])A.Clone();
            var x = (double[])b.Clone();
            for (int c = 0; c < n; c++)
            {
                int piv = c;
                for (int r = c + 1; r < n; r++) if (Math.Abs(M[r, c]) > Math.Abs(M[piv, c])) piv = r;
                if (Math.Abs(M[piv, c]) < 1e-30) return null;
                if (piv != c)
                {
                    for (int k = 0; k < n; k++) { double t = M[c, k]; M[c, k] = M[piv, k]; M[piv, k] = t; }
                    double tb = x[c]; x[c] = x[piv]; x[piv] = tb;
                }
                for (int r = 0; r < n; r++)
                {
                    if (r == c) continue;
                    double f = M[r, c] / M[c, c];
                    if (f == 0) continue;
                    for (int k = c; k < n; k++) M[r, k] -= f * M[c, k];
                    x[r] -= f * x[c];
                }
            }
            for (int c = 0; c < n; c++) x[c] /= M[c, c];
            return x;
        }
    }
}
