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
        public string Warning;
    }

    /// <summary>
    /// Fits a <see cref="DistortionModel"/> to lines that should be straight (the plumb-line method): the coefficients are
    /// chosen so that, once corrected, the points of each line lie on a straight line. Straightness does not depend on
    /// perspective, so the wall need not be photographed square-on.
    ///
    /// Horizontal lines measure how the lens moves points up and down (ky), vertical lines how it moves them sideways (kx).
    /// With both kinds the two axes are fitted separately (anamorphic lenses bend them differently); with only one kind
    /// they share one set of coefficients. A second-order term is added when there are enough points. A robust
    /// (Huber) weighting keeps a single misplaced point from dragging the fit.
    /// </summary>
    public static class DistortionFit
    {
        /// <param name="lines">Lines in pixel coordinates of an image in sensor orientation (squeeze along x).</param>
        /// <param name="width">Image width in pixels.</param>
        /// <param name="height">Image height in pixels.</param>
        /// <param name="factor">Squeeze factor of the image.</param>
        public static DistortionFitResult Fit(IList<CalibrationLine> lines, int width, int height, double factor)
        {
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

            // Candidate models, simplest first. The data chooses (Bayesian information criterion): a separate vertical
            // coefficient or a 2nd-order term is only used when the lines really show it, so short or few lines don't
            // produce wild corrections in the corners.
            // A direction only gets its own coefficient when its lines can measure it (long, and away from the centre);
            // a 2nd-order term needs lines that reach well out. Otherwise short lines would extrapolate wildly.
            double hReach, hSpan, vReach, vSpan;
            Coverage(data, lines, index, frame, out hReach, out hSpan, out vReach, out vSpan);
            bool hOk = hasH && hReach >= 0.4 && hSpan >= 0.4, vOk = hasV && vReach >= 0.4 && vSpan >= 0.4;
            bool farH = hReach >= 0.6 && hSpan >= 0.6, farV = vReach >= 0.6 && vSpan >= 0.6;
            var candidates = new List<int[]>();                 // {separate (0/1), order}
            candidates.Add(new[] { 0, 1 });
            if (constraints >= 12 && (farH || farV)) candidates.Add(new[] { 0, 2 });
            if (hOk && vOk)
            {
                candidates.Add(new[] { 1, 1 });
                if (constraints >= 16 && farH && farV) candidates.Add(new[] { 1, 2 });
            }
            double bestBic = double.MaxValue;
            double[] param = null;
            bool separate = false;
            int order = 1;
            Func<double[], DistortionModel> build = null;
            foreach (var cand in candidates)
            {
                bool sep = cand[0] == 1;
                int ord = cand[1];
                Func<double[], DistortionModel> b = p => BuildModel(p, sep, ord);
                var pr = new double[sep ? 2 * ord : ord];
                double rss = FitRobust(b, pr, data, sep, ord, pxPerUnit);
                int dof = Math.Max(1, constraints);
                double bic = dof * Math.Log(Math.Max(rss / dof, 1e-30)) + pr.Length * Math.Log(dof);
                if (bic < bestBic - 1e-9)
                {
                    bestBic = bic;
                    param = pr;
                    separate = sep;
                    order = ord;
                    build = b;
                }
            }

            var final = build(param);
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
            result.Kind = (separate ? "separate horizontal and vertical (anamorphic)" : "radial (same in both directions)") +
                          (order == 2 ? ", 2nd order" : ", 1st order");
            result.Warning = CoverageWarning(hReach, hSpan, vReach, vSpan, hasH, hasV, separate);
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

        private static DistortionModel BuildModel(double[] p, bool separate, int order)
        {
            var m = new DistortionModel();
            for (int k = 0; k < order; k++)
            {
                if (separate)
                {
                    m.Kx[k] = p[2 * k];
                    m.Ky[k] = p[2 * k + 1];
                }
                else
                {
                    m.Kx[k] = m.Ky[k] = p[k];
                }
            }
            return m;
        }

        /// <summary>Iteratively reweighted (Huber) least squares; returns the weighted sum of squared distances.</summary>
        private static double FitRobust(Func<double[], DistortionModel> build, double[] param, List<double[][]> data, bool separate, int order, double pxPerUnit)
        {
            var weights = new List<double[]>();
            foreach (var pts in data) { var w = new double[pts.Length]; for (int j = 0; j < w.Length; j++) w[j] = 1; weights.Add(w); }
            double huber = 2.5 / pxPerUnit;       // points more than 2.5 px off count less
            double rss = 0;
            for (int round = 0; round < 4; round++)
            {
                Func<double[], double[]> residuals = p => Residuals(build(p), data, weights, p, order, separate);
                LevenbergMarquardt(residuals, param);
                var model = build(param);
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

        private static double[] Residuals(DistortionModel m, List<double[][]> data, List<double[]> weights, double[] p, int order, bool separate)
        {
            var r = new List<double>();
            for (int l = 0; l < data.Count; l++)
            {
                var d = LineDistances(m, data[l], weights[l]);
                for (int j = 0; j < d.Length; j++) r.Add(d[j] * Math.Sqrt(weights[l][j]));
            }
            // Gentle ridge on the higher-order terms: they only move when the data asks for it.
            for (int k = 0; k < p.Length; k++)
            {
                bool high = separate ? k >= 2 : k >= 1;
                r.Add((high ? 2e-4 : 2e-5) * p[k]);
            }
            return r.ToArray();
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

        private static void LevenbergMarquardt(Func<double[], double[]> f, double[] p)
        {
            int n = p.Length;
            double lambda = 1e-3;
            var r = f(p);
            double cost = Dot(r, r);
            for (int iter = 0; iter < 100; iter++)
            {
                int m = r.Length;
                var J = new double[m, n];
                for (int k = 0; k < n; k++)
                {
                    double h = 1e-7;
                    double old = p[k];
                    p[k] = old + h;
                    var rp = f(p);
                    p[k] = old - h;
                    var rm = f(p);
                    p[k] = old;
                    for (int i = 0; i < m; i++) J[i, k] = (rp[i] - rm[i]) / (2 * h);
                }
                var A = new double[n, n];
                var g = new double[n];
                for (int i = 0; i < m; i++)
                    for (int a = 0; a < n; a++)
                    {
                        g[a] += J[i, a] * r[i];
                        for (int b = 0; b < n; b++) A[a, b] += J[i, a] * J[i, b];
                    }
                bool improved = false;
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    var M = new double[n, n];
                    var rhs = new double[n];
                    for (int a = 0; a < n; a++)
                    {
                        for (int b = 0; b < n; b++) M[a, b] = A[a, b];
                        M[a, a] += lambda * (A[a, a] + 1e-18);
                        rhs[a] = -g[a];
                    }
                    var delta = Solve(M, rhs);
                    if (delta == null) { lambda *= 10; continue; }
                    var q = new double[n];
                    for (int a = 0; a < n; a++) q[a] = p[a] + delta[a];
                    var rq = f(q);
                    double c2 = Dot(rq, rq);
                    if (c2 < cost)
                    {
                        double step = 0;
                        for (int a = 0; a < n; a++) step += Math.Abs(delta[a]);
                        Array.Copy(q, p, n);
                        r = rq;
                        bool done = cost - c2 < 1e-15 * (1 + cost) || step < 1e-12;
                        cost = c2;
                        lambda = Math.Max(1e-12, lambda * 0.3);
                        improved = true;
                        if (done) return;
                        break;
                    }
                    lambda *= 10;
                }
                if (!improved) return;
            }
        }

        private static double Dot(double[] a, double[] b)
        {
            double s = 0;
            for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
            return s;
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
