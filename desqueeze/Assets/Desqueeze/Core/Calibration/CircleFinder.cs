using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Desqueeze.Core.Color;
using Desqueeze.Core.Imaging;

namespace Desqueeze.Core.Calibration
{
    /// <summary>
    /// A squeezed circle found in an image: an ellipse whose axes run along the image axes.
    /// Coordinates are in source pixels with pixel centres at whole numbers (pixel (0,0) is centred on (0,0)).
    /// </summary>
    public sealed class EllipseFit
    {
        public double CenterX, CenterY;
        /// <summary>Semi-axis along x (half the width of the squeezed circle).</summary>
        public double RadiusX;
        /// <summary>Semi-axis along y (half the height of the squeezed circle).</summary>
        public double RadiusY;
        /// <summary>Edge points the final fit used, out of <see cref="Rays"/> rays.</summary>
        public int EdgePoints;
        public int Rays;
        /// <summary>Root-mean-square distance of the edge points from the ellipse, in pixels.</summary>
        public double RmsError;
        /// <summary>True when the circle is darker than its surroundings.</summary>
        public bool DarkInside;
        /// <summary>The circle is drawn as a line only a few pixels wide (its two edges blur together along the squeezed axis).</summary>
        public bool ThinOutline;

        /// <summary>The radius the circle really has: the axis the lens did not squeeze.</summary>
        public double TrueRadius(SqueezeAxis axis)
        {
            return axis == SqueezeAxis.Horizontal ? RadiusY : RadiusX;
        }

        /// <summary>The radius along the squeezed axis.</summary>
        public double SqueezedRadius(SqueezeAxis axis)
        {
            return axis == SqueezeAxis.Horizontal ? RadiusX : RadiusY;
        }

        /// <summary>The squeeze factor this circle implies: true radius / squeezed radius.</summary>
        public double MeasuredFactor(SqueezeAxis axis)
        {
            return TrueRadius(axis) / SqueezedRadius(axis);
        }

        /// <summary>A short warning when the measurement deserves less trust, or null.</summary>
        public string Warning
        {
            get
            {
                double r = Math.Min(RadiusX, RadiusY);
                if (RmsError > Math.Max(0.6, 0.003 * Math.Max(RadiusX, RadiusY)))
                    return "The outline does not fit an ellipse well - is the circle tilted, partly hidden or distorted? Check the ring closely.";
                if ((double)EdgePoints / Math.Max(1, Rays) < 0.6)
                    return "Only part of the outline was found; a fully visible circle with a clean edge measures better.";
                if (ThinOutline && r < 150)
                    return "This circle is a thin drawn line; a filled circle, or a larger one, measures more precisely.";
                if (r < 40)
                    return "This circle is small; a larger one gives a more precise result.";
                return null;
            }
        }

        public override string ToString()
        {
            return "centre (" + CenterX.ToString("0.00") + ", " + CenterY.ToString("0.00") + "), radii " +
                   RadiusX.ToString("0.00") + " x " + RadiusY.ToString("0.00") + ", " + EdgePoints + "/" + Rays +
                   " edge points, rms " + RmsError.ToString("0.000") + " px";
        }
    }

    /// <summary>
    /// Finds the squeezed circle around a point the user clicked.
    ///
    /// 1. Rays are cast from the click. Along each ray the luminance profile (linear light, averaged over three
    ///    parallel lines) is differentiated with a derivative-of-Gaussian filter, and its strongest edges become candidates.
    /// 2. RANSAC fits axis-aligned ellipses through candidates on four rays in different quadrants and keeps the
    ///    ellipse that the most rays agree on, with one edge polarity (dark-to-light or light-to-dark) all round.
    ///    The click must lie inside it. When concentric ellipses are almost equally supported (the inner and outer
    ///    edges of a drawn ring), the outer one is the circle.
    /// 3. Rays are cast again from the fitted centre, each edge is located to a fraction of a pixel in a narrow window
    ///    around the ellipse, and the ellipse is refitted by minimising the radial distances (Gauss-Newton, with
    ///    outliers trimmed). This is done twice.
    ///
    /// The ellipse is constrained to the image axes because an anamorphic lens squeezes along one image axis; a circle
    /// photographed face-on therefore becomes an axis-aligned ellipse.
    /// </summary>
    public static class CircleFinder
    {
        /// <summary>Optional diagnostics sink (for tools and tests).</summary>
        public static Action<string> Log;

        private const int SearchRays = 256;
        private const int FitRays = 360;
        private const int Iterations = 12000;

        private struct Candidate
        {
            public double R;      // distance from the click along the ray
            public int Sign;      // +1: darker inside, brighter outside (derivative > 0 going outward)
            public float Strength;
        }

        private struct Ellipse
        {
            public double Cx, Cy, A, B;
            public int Sign;
            public int Score;
            public double Weight;
        }

        /// <summary>
        /// Finds the circle around (<paramref name="clickX"/>, <paramref name="clickY"/>) (pixel-centre coordinates).
        /// Returns null and sets <paramref name="error"/> when there is no clear circle there.
        /// </summary>
        public static EllipseFit Find(PixelImage image, double clickX, double clickY, out string error,
            CancellationToken ct = default)
        {
            error = null;
            if (image == null) throw new ArgumentNullException(nameof(image));
            if (clickX < 0 || clickY < 0 || clickX > image.Width - 1 || clickY > image.Height - 1)
            {
                error = "Click inside the image.";
                return null;
            }
            var luma = new Luma(image);

            // ---- 1. candidate edges along rays from the click
            double maxR = Math.Min(0.5 * Math.Max(image.Width, image.Height), 4000);
            var deriv = new float[SearchRays][];
            var margins = new int[SearchRays];
            Parallel.For(0, SearchRays, i =>
            {
                double th = 2 * Math.PI * i / SearchRays;
                deriv[i] = RayDerivative(luma, clickX, clickY, Math.Cos(th), Math.Sin(th), maxR, out margins[i]);
            });
            ct.ThrowIfCancellationRequested();
            // Noise level: the median edge response along all rays (most samples see no edge). Candidates must stand
            // well above it, so texture and sensor noise around the click are not mistaken for a small circle.
            var all = new List<float>();
            for (int i = 0; i < SearchRays; i++)
                for (int k = margins[i]; k < deriv[i].Length - margins[i]; k += 3) all.Add(Math.Abs(deriv[i][k]));
            if (all.Count == 0)
            {
                error = "Click further from the edge of the image.";
                return null;
            }
            all.Sort();
            // About 5 sigma of sensor noise, estimated from the flattest fifth of the samples (not from texture).
            float noiseFloor = 20 * all[all.Count / 5];
            var cands = new Candidate[SearchRays][];
            for (int i = 0; i < SearchRays; i++) cands[i] = RayCandidates(deriv[i], margins[i], noiseFloor);

            // ---- 2. RANSAC
            var models = Ransac(cands, clickX, clickY, maxR, ct);
            if (models.Count == 0)
            {
                error = "No circle found around that point. Click inside a round object with a clear edge.";
                return null;
            }
            // ---- 3. verify the leading candidates by refining each; keep the one whose outline is found all round
            // with the smallest error (an ellipse stitched together from unrelated edges fits badly once measured precisely).
            models.Sort((x, y) => y.Score != x.Score ? y.Score.CompareTo(x.Score) : y.Weight.CompareTo(x.Weight));
            var distinct = new List<Ellipse>();
            foreach (var m in models)
            {
                if (m.Score < 0.4 * models[0].Score || distinct.Count >= 10) break;
                // One candidate per centre and polarity: variants of one shape (e.g. flat ellipses resting on the same two
                // lines of texture) must not crowd out a different shape.
                bool dup = false;
                foreach (var d in distinct)
                {
                    double tol = Math.Max(2, 0.05 * Math.Min(Math.Min(d.A, d.B), Math.Min(m.A, m.B)));
                    if (d.Sign == m.Sign && Math.Abs(d.Cx - m.Cx) < tol && Math.Abs(d.Cy - m.Cy) < tol) { dup = true; break; }
                }
                if (!dup) distinct.Add(m);
            }
            var fits = new List<Refined>();
            foreach (var m in distinct)
            {
                ct.ThrowIfCancellationRequested();
                var r = Refine(luma, m, cands, clickX, clickY, image.Width, image.Height, noiseFloor / SearchStep);
                if (Log != null)
                    Log(string.Format("  candidate c=({0:0.00},{1:0.00}) r={2:0.00}x{3:0.00} sign {4} score {5} -> {6}", m.Cx, m.Cy, m.A, m.B, m.Sign, m.Score,
                        r == null ? "rejected" : string.Format("c=({0:0.00},{1:0.00}) r={2:0.00}x{3:0.00} pts {4} rms {5:0.000} q {6:0.0}", r.Cx, r.Cy, r.A, r.B, r.Points, r.Rms, r.Quality)));
                if (r != null) fits.Add(r);
            }
            if (fits.Count == 0)
            {
                error = "The edge of that circle is too unclear to measure. Try a circle with a sharper, cleaner outline.";
                return null;
            }
            var best = fits[0];
            foreach (var f in fits) if (f.Quality > best.Quality) best = f;
            // Of the outlines that fit cleanly, the largest is the circle: the outer edge of a drawn ring, a dial rather
            // than the marks inside it, a wheel rather than a blob of texture inside it.
            var chosen = best;
            foreach (var f in fits)
                if (f.Quality >= 0.75 * best.Quality && f.A * f.B > chosen.A * chosen.B) chosen = f;
            bool thinOutline = false;
            foreach (var f in fits)
            {
                if (f == chosen || f.Sign == chosen.Sign || f.Quality < 0.5 * chosen.Quality) continue;
                double gap = Math.Min(chosen.A - f.A, chosen.B - f.B);
                if (gap > 0 && Math.Min(chosen.A, chosen.B) / Math.Max(chosen.A, chosen.B) > 0.2 &&
                    Math.Abs(f.Cx - chosen.Cx) < 1 && Math.Abs(f.Cy - chosen.Cy) < 1 && Math.Max(chosen.A - f.A, chosen.B - f.B) < 6)
                    thinOutline = true;   // the circle is a line only a few pixels wide
            }
            return new EllipseFit
            {
                CenterX = chosen.Cx,
                CenterY = chosen.Cy,
                RadiusX = chosen.A,
                RadiusY = chosen.B,
                EdgePoints = chosen.Points,
                Rays = FitRays,
                RmsError = chosen.Rms,
                DarkInside = chosen.Sign > 0,
                ThinOutline = thinOutline,
            };
        }

        private sealed class Refined
        {
            public double Cx, Cy, A, B, Rms, Quality;
            public int Points, Sign;
        }

        /// <summary>
        /// Sub-pixel refinement of one RANSAC ellipse: a least-squares start from the rays that agreed with it, then three
        /// rounds of measuring the edge on 360 rays from the centre and refitting. Null if the outline isn't found.
        /// </summary>
        private static Refined Refine(Luma luma, Ellipse m, Candidate[][] cands, double clickX, double clickY, int width, int height, double floorPerPx)
        {
            double cx = m.Cx, cy = m.Cy, a = m.A, b = m.B;
            var inliers = InlierPoints(m, cands, clickX, clickY);
            double rms0, ix = cx, iy = cy, ia = a, ib = b;
            if (inliers.Count >= 12 && FitEllipse(inliers, ref ix, ref iy, ref ia, ref ib, out rms0) &&
                Math.Abs(ia / a - 1) < 0.1 && Math.Abs(ib / b - 1) < 0.1)
            {
                cx = ix; cy = iy; a = ia; b = ib;
            }
            List<double[]> pts = null;
            double rms = 0;
            for (int pass = 0; pass < 3; pass++)
            {
                pts = EdgePoints(luma, cx, cy, a, b, m.Sign, pass == 0 ? 0.05 : 0.02, pass == 0 ? 4 : 2.5, width, height, floorPerPx);
                if (pts.Count < Math.Max(24, FitRays / 4)) return null;
                if (!FitEllipse(pts, ref cx, ref cy, ref a, ref b, out rms)) return null;
            }
            if ((clickX - cx) * (clickX - cx) / (a * a) + (clickY - cy) * (clickY - cy) / (b * b) >= 1) return null;
            // Quality: how much of the outline was found, discounted by how far the points scatter from the ellipse.
            double q = pts.Count * Math.Exp(-(rms / 0.6) * (rms / 0.6));
            return new Refined { Cx = cx, Cy = cy, A = a, B = b, Rms = rms, Points = pts.Count, Sign = m.Sign, Quality = q };
        }

        // ============================================================================ stage 1

        private const double SearchStep = 0.5;              // px between profile samples in the search

        private static float[] RayDerivative(Luma luma, double ox, double oy, double dx, double dy, double maxR, out int margin)
        {
            const double sigma = 2.0;                       // samples (1 px)
            double end = Math.Min(maxR, DistanceToBorder(ox, oy, dx, dy, luma.Width, luma.Height, 2));
            int n = (int)(end / SearchStep);
            if (n < 16)
            {
                margin = 0;
                return new float[0];
            }
            var p = new float[n];
            Profile(luma, ox, oy, dx, dy, 0, SearchStep, n, p);
            return Derivative(p, sigma, out margin);
        }

        private static Candidate[] RayCandidates(float[] d, int margin, float noiseFloor)
        {
            int n = d.Length;
            float max = 0;
            for (int k = margin; k < n - margin; k++) max = Math.Max(max, Math.Abs(d[k]));
            if (max <= 0) return new Candidate[0];
            // relative to the ray's strongest edge, to the image's noise, and absolute (linear luma per sample)
            float floor = Math.Max(Math.Max(0.05f * max, noiseFloor), 0.0015f);
            var list = new List<Candidate>();
            for (int k = Math.Max(margin, 1); k < n - margin - 1; k++)
            {
                float v = Math.Abs(d[k]);
                if (v < floor || v < Math.Abs(d[k - 1]) || v <= Math.Abs(d[k + 1])) continue;
                double r = (k + SubSample(Math.Abs(d[k - 1]), v, Math.Abs(d[k + 1]))) * SearchStep;
                if (r < 3.0) continue;
                list.Add(new Candidate { R = r, Sign = d[k] > 0 ? 1 : -1, Strength = v });
            }
            // Keep the 6 nearest edges (the clicked object's outline is usually among them, even where it is faint)
            // and the 6 strongest of the rest; nearest first, as RANSAC favours the first edges out from the click.
            if (list.Count > 12)
            {
                var keep = list.GetRange(0, 6);
                var rest = list.GetRange(6, list.Count - 6);
                rest.Sort((x, y) => y.Strength.CompareTo(x.Strength));
                keep.AddRange(rest.GetRange(0, 6));
                list = keep;
            }
            list.Sort((x, y) => x.R.CompareTo(y.R));
            return list.ToArray();
        }

        // ============================================================================ stage 2

        private static List<Ellipse> Ransac(Candidate[][] cands, double ox, double oy, double maxR, CancellationToken ct)
        {
            int n = cands.Length;
            var cos = new double[n];
            var sin = new double[n];
            for (int i = 0; i < n; i++)
            {
                cos[i] = Math.Cos(2 * Math.PI * i / n);
                sin[i] = Math.Sin(2 * Math.PI * i / n);
            }
            // Rays with candidates, per quadrant (spread-out samples give well-conditioned fits).
            var quad = new List<int>[4];
            for (int q = 0; q < 4; q++) quad[q] = new List<int>();
            for (int i = 0; i < n; i++)
                if (cands[i].Length > 0) quad[i * 4 / n].Add(i);
            for (int q = 0; q < 4; q++)
                if (quad[q].Count == 0) return new List<Ellipse>();

            // Scale for conditioning: median candidate distance.
            var rs = new List<double>();
            foreach (var c in cands) foreach (var x in c) rs.Add(x.R);
            rs.Sort();
            double S = Math.Max(4, rs[rs.Count / 2]);

            int minScore = Math.Max(16, (int)(0.3 * n));
            var models = new List<Ellipse>();
            var rnd = new Random(20261105);
            var M = new double[4, 5];
            var rays = new int[4];
            var picks = new double[4];
            for (int it = 0; it < Iterations; it++)
            {
                if ((it & 255) == 0) ct.ThrowIfCancellationRequested();
                int sign = 0;
                bool ok = true;
                for (int q = 0; q < 4 && ok; q++)
                {
                    var qr = quad[q];
                    int ray = qr[rnd.Next(qr.Count)];
                    var cs = cands[ray];
                    // Every other iteration favours the first edges out from the click (a solid shape's outline is usually
                    // the nearest one: nearest half the time, the next a quarter, ...). The rest favour strong edges
                    // (chance proportional to strength squared), which finds outlines drawn over a busy background such as
                    // a wheel rim or a hoop, whose inside is full of fainter texture.
                    int found = -1;
                    if ((it & 1) == 0)
                    {
                        int last = -1;
                        for (int t = 0; t < cs.Length; t++)
                        {
                            if (sign != 0 && cs[t].Sign != sign) continue;
                            last = t;
                            if (rnd.Next(2) == 0) { found = t; break; }
                        }
                        if (found < 0) found = last;
                    }
                    else
                    {
                        double total = 0;
                        for (int t = 0; t < cs.Length; t++)
                            if (sign == 0 || cs[t].Sign == sign) total += (double)cs[t].Strength * cs[t].Strength;
                        double pick = rnd.NextDouble() * total;
                        for (int t = 0; t < cs.Length && total > 0; t++)
                        {
                            if (sign != 0 && cs[t].Sign != sign) continue;
                            found = t;
                            pick -= (double)cs[t].Strength * cs[t].Strength;
                            if (pick <= 0) break;
                        }
                    }
                    if (found < 0) { ok = false; break; }
                    if (sign == 0) sign = cs[found].Sign;
                    rays[q] = ray;
                    picks[q] = cs[found].R;
                }
                if (!ok) continue;

                // A u^2 + C v^2 + D u + E v = 1, with (u, v) the point relative to the click, divided by S.
                for (int q = 0; q < 4; q++)
                {
                    double u = picks[q] * cos[rays[q]] / S, v = picks[q] * sin[rays[q]] / S;
                    M[q, 0] = u * u; M[q, 1] = v * v; M[q, 2] = u; M[q, 3] = v; M[q, 4] = 1;
                }
                double[] sol;
                if (!Solve4(M, out sol)) continue;
                double A = sol[0], C = sol[1], D = sol[2], E = sol[3];
                if (!(A > 0 && C > 0)) continue;
                double cu = -D / (2 * A), cv = -E / (2 * C);
                double G = 1 + A * cu * cu + C * cv * cv;
                if (!(G > 0)) continue;
                double a = Math.Sqrt(G / A) * S, b = Math.Sqrt(G / C) * S;
                double ex = ox + cu * S, ey = oy + cv * S;
                if (a < 6 || b < 6 || a > maxR || b > maxR || a > 4 * b || b > 4 * a) continue;
                double inside = ((ox - ex) / a) * ((ox - ex) / a) + ((oy - ey) / b) * ((oy - ey) / b);
                if (inside >= 1) continue;   // the click must be inside the circle

                var e = new Ellipse { Cx = ex, Cy = ey, A = a, B = b, Sign = sign };
                Score(ref e, cands, cos, sin, ox, oy);
                if (e.Score < minScore) continue;
                AddModel(models, e);
            }
            return models;
        }

        private static void Score(ref Ellipse e, Candidate[][] cands, double[] cos, double[] sin, double ox, double oy)
        {
            int score = 0;
            double weight = 0;
            double dx0 = ox - e.Cx, dy0 = oy - e.Cy;
            double ia2 = 1 / (e.A * e.A), ib2 = 1 / (e.B * e.B);
            double c0 = dx0 * dx0 * ia2 + dy0 * dy0 * ib2 - 1;    // < 0: the click is inside
            for (int i = 0; i < cands.Length; i++)
            {
                var cs = cands[i];
                if (cs.Length == 0) continue;
                double qa = cos[i] * cos[i] * ia2 + sin[i] * sin[i] * ib2;
                double qb = 2 * (cos[i] * dx0 * ia2 + sin[i] * dy0 * ib2);
                double r = (-qb + Math.Sqrt(qb * qb - 4 * qa * c0)) / (2 * qa);
                double tol = Math.Max(1.5, 0.015 * r);
                foreach (var c in cs)
                {
                    if (c.Sign != e.Sign || Math.Abs(c.R - r) > tol) continue;
                    score++;
                    weight += c.Strength;
                    break;
                }
            }
            e.Score = score;
            e.Weight = weight;
        }

        /// <summary>For each search ray, the candidate that agrees with the ellipse (as image points).</summary>
        private static List<double[]> InlierPoints(Ellipse e, Candidate[][] cands, double ox, double oy)
        {
            var pts = new List<double[]>();
            int n = cands.Length;
            double dx0 = ox - e.Cx, dy0 = oy - e.Cy;
            double ia2 = 1 / (e.A * e.A), ib2 = 1 / (e.B * e.B);
            double c0 = dx0 * dx0 * ia2 + dy0 * dy0 * ib2 - 1;
            for (int i = 0; i < n; i++)
            {
                double co = Math.Cos(2 * Math.PI * i / n), si = Math.Sin(2 * Math.PI * i / n);
                double qa = co * co * ia2 + si * si * ib2, qb = 2 * (co * dx0 * ia2 + si * dy0 * ib2);
                double r = (-qb + Math.Sqrt(qb * qb - 4 * qa * c0)) / (2 * qa);
                double tol = Math.Max(1.5, 0.015 * r), bestD = tol;
                double found = -1;
                foreach (var c in cands[i])
                {
                    double d = Math.Abs(c.R - r);
                    if (c.Sign == e.Sign && d <= bestD) { bestD = d; found = c.R; }
                }
                if (found > 0) pts.Add(new[] { ox + found * co, oy + found * si });
            }
            return pts;
        }

        private static void AddModel(List<Ellipse> models, Ellipse e)
        {
            for (int i = 0; i < models.Count; i++)
            {
                var m = models[i];
                double r = Math.Min(m.A, m.B);
                if (m.Sign == e.Sign && Math.Abs(m.Cx - e.Cx) < 0.02 * r + 1 && Math.Abs(m.Cy - e.Cy) < 0.02 * r + 1 &&
                    Math.Abs(m.A / e.A - 1) < 0.02 && Math.Abs(m.B / e.B - 1) < 0.02)
                {
                    if (e.Score > m.Score || (e.Score == m.Score && e.Weight > m.Weight)) models[i] = e;
                    return;
                }
            }
            models.Add(e);
        }

        // ============================================================================ stage 3

        /// <summary>Sub-pixel edge points on rays from the centre, searched within +-window*r of the predicted ellipse.</summary>
        private static List<double[]> EdgePoints(Luma luma, double cx, double cy, double a, double b, int sign,
            double window, double minWindow, int width, int height, double floorPerPx)
        {
            const double step = 0.25;
            const double sigma = 4.0;                       // samples (1 px)
            var found = new double[FitRays][];
            var strength = new float[FitRays];
            Parallel.For(0, FitRays, i =>
            {
                double th = 2 * Math.PI * (i + 0.5) / FitRays;
                double dx = Math.Cos(th), dy = Math.Sin(th);
                double rp = 1 / Math.Sqrt(dx * dx / (a * a) + dy * dy / (b * b));
                double w = Math.Max(minWindow, window * rp);
                // Sample beyond the window by the filter's half-width, so the whole window gets a derivative.
                double ext = (3 * sigma + 2) * step;
                double r0 = Math.Max(0.5, rp - w - ext), r1 = rp + w + ext;
                if (r1 + 1 > DistanceToBorder(cx, cy, dx, dy, width, height, 0)) return;   // edge too close to the border
                int n = (int)((r1 - r0) / step) + 1;
                var p = new float[n];
                ArcProfile(luma, cx, cy, th, 1.0 / rp, r0, step, n, p);
                var d = Derivative(p, sigma, out int margin);
                int kLo = Math.Max(margin + 1, (int)Math.Ceiling((rp - w - r0) / step));
                int kHi = Math.Min(n - margin - 2, (int)Math.Floor((rp + w - r0) / step));
                if (kHi - kLo < 3) return;
                // Peaks above the noise are weighted by their distance from the expected edge: a faint outline right
                // where the ellipse predicts it beats a strong background edge further away.
                float floor = (float)Math.Max(floorPerPx * step, 1e-4);
                int bestK = -1;
                double bestScore = 0;
                for (int k = kLo; k <= kHi; k++)
                {
                    float v = sign * d[k];
                    if (v < floor || v < sign * d[k - 1] || v <= sign * d[k + 1]) continue;
                    double t = (r0 + k * step - rp) / (0.35 * w);
                    double sc = v * Math.Exp(-t * t);
                    if (sc > bestScore) { bestScore = sc; bestK = k; }
                }
                if (bestK <= kLo || bestK >= kHi) return;   // none, or at the window's edge (the edge may lie outside)
                float bestV = sign * d[bestK];
                double off = SubSample(sign * d[bestK - 1], bestV, sign * d[bestK + 1]);
                double r = r0 + (bestK + off) * step;
                found[i] = new[] { cx + r * dx, cy + r * dy };
                strength[i] = bestV;
            });
            // Drop rays whose "edge" is much weaker than typical (they crossed something else, or nothing).
            var sv = new List<float>();
            for (int i = 0; i < FitRays; i++) if (found[i] != null) sv.Add(strength[i]);
            var pts = new List<double[]>();
            if (sv.Count == 0) return pts;
            sv.Sort();
            float med = sv[sv.Count / 2];
            for (int i = 0; i < FitRays; i++)
                if (found[i] != null && strength[i] >= 0.35f * med) pts.Add(found[i]);
            return pts;
        }

        /// <summary>
        /// Geometric fit of an axis-aligned ellipse: minimises the radial distance of each point from the ellipse
        /// (Levenberg-Marquardt), trims outliers beyond 3 sigma, and refits.
        /// </summary>
        private static bool FitEllipse(List<double[]> pts, ref double cx, ref double cy, ref double a, ref double b, out double rms)
        {
            rms = 0;
            var p = new[] { cx, cy, a, b };
            var use = new bool[pts.Count];
            for (int i = 0; i < use.Length; i++) use[i] = true;
            for (int round = 0; round < 3; round++)
            {
                if (!LevenbergMarquardt(pts, use, p)) return false;
                // residuals and robust trimming
                var res = new double[pts.Count];
                var abs = new List<double>();
                for (int i = 0; i < pts.Count; i++)
                {
                    res[i] = Residual(pts[i], p);
                    if (use[i]) abs.Add(Math.Abs(res[i]));
                }
                abs.Sort();
                double sigma = Math.Max(0.1, 1.4826 * abs[abs.Count / 2]);
                int kept = 0;
                for (int i = 0; i < pts.Count; i++)
                {
                    use[i] = Math.Abs(res[i]) <= 3 * sigma;
                    if (use[i]) kept++;
                }
                if (kept < 12) return false;
            }
            double ss = 0;
            int m = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                if (!use[i]) continue;
                double r = Residual(pts[i], p);
                ss += r * r;
                m++;
            }
            for (int i = pts.Count - 1; i >= 0; i--) if (!use[i]) pts.RemoveAt(i);
            rms = Math.Sqrt(ss / Math.Max(1, m));
            cx = p[0]; cy = p[1]; a = p[2]; b = p[3];
            return a > 0 && b > 0;
        }

        /// <summary>Signed radial distance of a point from the ellipse (positive outside).</summary>
        private static double Residual(double[] pt, double[] p)
        {
            double dx = pt[0] - p[0], dy = pt[1] - p[1];
            double d = Math.Sqrt(dx * dx + dy * dy);
            double u = dx / p[2], v = dy / p[3];
            double rho = Math.Sqrt(u * u + v * v);
            return rho > 0 ? d * (1 - 1 / rho) : -Math.Min(p[2], p[3]);
        }

        private static bool LevenbergMarquardt(List<double[]> pts, bool[] use, double[] p)
        {
            double lambda = 1e-3;
            double cost = Cost(pts, use, p);
            var J = new double[4];
            for (int iter = 0; iter < 50; iter++)
            {
                var JtJ = new double[4, 5];
                var Jte = new double[4];
                for (int i = 0; i < pts.Count; i++)
                {
                    if (!use[i]) continue;
                    double e = Residual(pts[i], p);
                    for (int k = 0; k < 4; k++)
                    {
                        double h = 1e-4 * Math.Max(1, Math.Abs(p[k]) * 0.01);
                        double old = p[k];
                        p[k] = old + h;
                        double ep = Residual(pts[i], p);
                        p[k] = old - h;
                        double em = Residual(pts[i], p);
                        p[k] = old;
                        J[k] = (ep - em) / (2 * h);
                    }
                    for (int r = 0; r < 4; r++)
                    {
                        Jte[r] += J[r] * e;
                        for (int c = 0; c < 4; c++) JtJ[r, c] += J[r] * J[c];
                    }
                }
                bool improved = false;
                for (int attempt = 0; attempt < 10; attempt++)
                {
                    var Mx = new double[4, 5];
                    for (int r = 0; r < 4; r++)
                    {
                        for (int c = 0; c < 4; c++) Mx[r, c] = JtJ[r, c] + (r == c ? lambda * (JtJ[r, r] + 1e-12) : 0);
                        Mx[r, 4] = -Jte[r];
                    }
                    double[] delta;
                    if (!Solve4(Mx, out delta)) return false;
                    var q = new[] { p[0] + delta[0], p[1] + delta[1], p[2] + delta[2], p[3] + delta[3] };
                    if (q[2] <= 0 || q[3] <= 0) { lambda *= 10; continue; }
                    double c2 = Cost(pts, use, q);
                    if (c2 <= cost)
                    {
                        double step = Math.Abs(delta[0]) + Math.Abs(delta[1]) + Math.Abs(delta[2]) + Math.Abs(delta[3]);
                        Array.Copy(q, p, 4);
                        cost = c2;
                        lambda = Math.Max(1e-9, lambda * 0.3);
                        improved = true;
                        if (step < 1e-7) return true;
                        break;
                    }
                    lambda *= 10;
                }
                if (!improved) return true;   // converged (no further decrease possible)
            }
            return true;
        }

        private static double Cost(List<double[]> pts, bool[] use, double[] p)
        {
            double s = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                if (!use[i]) continue;
                double r = Residual(pts[i], p);
                s += r * r;
            }
            return s;
        }

        // ============================================================================ helpers

        /// <summary>Luma profile along a ray, each sample the mean of three parallel lines one pixel apart.</summary>
        private static void Profile(Luma luma, double ox, double oy, double dx, double dy, double r0, double step, int n, float[] dst)
        {
            double px = -dy, py = dx;
            for (int k = 0; k < n; k++)
            {
                double r = r0 + k * step;
                double x = ox + r * dx, y = oy + r * dy;
                dst[k] = (luma.At(x - px, y - py) + luma.At(x, y) + luma.At(x + px, y + py)) * (1f / 3f);
            }
        }

        /// <summary>
        /// Luma profile along a ray from the centre, each sample the mean of three points on an arc one pixel apart
        /// (angles th - dth, th, th + dth). Unlike parallel lines, the arc stays at the same distance from the centre, so
        /// the curved edge is not pulled inward on small circles.
        /// </summary>
        private static void ArcProfile(Luma luma, double ox, double oy, double th, double dth, double r0, double step, int n, float[] dst)
        {
            double c0 = Math.Cos(th), s0 = Math.Sin(th);
            double c1 = Math.Cos(th - dth), s1 = Math.Sin(th - dth);
            double c2 = Math.Cos(th + dth), s2 = Math.Sin(th + dth);
            for (int k = 0; k < n; k++)
            {
                double r = r0 + k * step;
                dst[k] = (luma.At(ox + r * c1, oy + r * s1) + luma.At(ox + r * c0, oy + r * s0) + luma.At(ox + r * c2, oy + r * s2)) * (1f / 3f);
            }
        }

        /// <summary>Derivative-of-Gaussian of a profile, per sample. The first and last <paramref name="margin"/> values are 0.</summary>
        private static float[] Derivative(float[] p, double sigma, out int margin)
        {
            int R = (int)Math.Ceiling(3 * sigma);
            var w = new double[2 * R + 1];
            double norm = 0;
            for (int j = -R; j <= R; j++)
            {
                double g = Math.Exp(-j * j / (2 * sigma * sigma));
                w[j + R] = j * g;
                norm += j * j * g;
            }
            for (int j = 0; j < w.Length; j++) w[j] /= norm;
            margin = R;
            var d = new float[p.Length];
            for (int k = R; k < p.Length - R; k++)
            {
                double s = 0;
                for (int j = -R; j <= R; j++) s += w[j + R] * p[k + j];
                d[k] = (float)s;
            }
            return d;
        }

        /// <summary>Offset of a parabola's peak through three samples, in [-0.5, 0.5].</summary>
        private static double SubSample(double l, double c, double r)
        {
            double den = l - 2 * c + r;
            if (Math.Abs(den) < 1e-12) return 0;
            double o = 0.5 * (l - r) / den;
            return o < -0.5 ? -0.5 : (o > 0.5 ? 0.5 : o);
        }

        private static double DistanceToBorder(double ox, double oy, double dx, double dy, int w, int h, double margin)
        {
            double t = double.MaxValue;
            double lo = margin, hiX = w - 1 - margin, hiY = h - 1 - margin;
            if (dx > 1e-12) t = Math.Min(t, (hiX - ox) / dx);
            else if (dx < -1e-12) t = Math.Min(t, (lo - ox) / dx);
            if (dy > 1e-12) t = Math.Min(t, (hiY - oy) / dy);
            else if (dy < -1e-12) t = Math.Min(t, (lo - oy) / dy);
            return Math.Max(0, t);
        }

        /// <summary>Solves a 4x4 system given as an augmented 4x5 matrix (Gaussian elimination, partial pivoting).</summary>
        private static bool Solve4(double[,] m0, out double[] x)
        {
            var m = (double[,])m0.Clone();
            x = new double[4];
            for (int c = 0; c < 4; c++)
            {
                int piv = c;
                for (int r = c + 1; r < 4; r++) if (Math.Abs(m[r, c]) > Math.Abs(m[piv, c])) piv = r;
                if (Math.Abs(m[piv, c]) < 1e-14) return false;
                if (piv != c)
                    for (int k = 0; k < 5; k++) { double t = m[c, k]; m[c, k] = m[piv, k]; m[piv, k] = t; }
                for (int r = 0; r < 4; r++)
                {
                    if (r == c) continue;
                    double f = m[r, c] / m[c, c];
                    if (f == 0) continue;
                    for (int k = c; k < 5; k++) m[r, k] -= f * m[c, k];
                }
            }
            for (int c = 0; c < 4; c++) x[c] = m[c, 4] / m[c, c];
            return !(double.IsNaN(x[0]) || double.IsNaN(x[1]) || double.IsNaN(x[2]) || double.IsNaN(x[3]));
        }

        /// <summary>Linear-light luminance with bilinear interpolation (pixel centres at whole numbers, edges clamped).</summary>
        private sealed class Luma
        {
            private readonly PixelImage _img;
            private readonly float[] _lut;
            private readonly int _ch;
            public readonly int Width, Height;

            public Luma(PixelImage img)
            {
                _img = img;
                _lut = img.BitDepth == 8 ? Srgb.Decode8 : Srgb.Decode16;
                _ch = img.Channels;
                Width = img.Width;
                Height = img.Height;
            }

            private float Px(int x, int y)
            {
                if (x < 0) x = 0; else if (x >= Width) x = Width - 1;
                if (y < 0) y = 0; else if (y >= Height) y = Height - 1;
                int o = (y * Width + x) * _ch;
                if (_img.BitDepth == 8)
                {
                    var d = _img.Data8;
                    return _ch <= 2 ? _lut[d[o]] : 0.2126f * _lut[d[o]] + 0.7152f * _lut[d[o + 1]] + 0.0722f * _lut[d[o + 2]];
                }
                var e = _img.Data16;
                return _ch <= 2 ? _lut[e[o]] : 0.2126f * _lut[e[o]] + 0.7152f * _lut[e[o + 1]] + 0.0722f * _lut[e[o + 2]];
            }

            public float At(double x, double y)
            {
                int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
                float fx = (float)(x - x0), fy = (float)(y - y0);
                float a = Px(x0, y0), b = Px(x0 + 1, y0), c = Px(x0, y0 + 1), d = Px(x0 + 1, y0 + 1);
                return (a + (b - a) * fx) * (1 - fy) + (c + (d - c) * fx) * fy;
            }
        }
    }
}
