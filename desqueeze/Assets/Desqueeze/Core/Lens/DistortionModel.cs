using System;

namespace Desqueeze.Core.Lens
{
    /// <summary>One general term of a <see cref="DistortionModel"/>: the monomial xu^I yu^J moves a point by (X, Y) times its value.</summary>
    [Serializable]
    public struct DistortionTerm
    {
        public int I, J;
        public double X, Y;

        public DistortionTerm(int i, int j, double x, double y)
        {
            I = i;
            J = j;
            X = x;
            Y = y;
        }
    }

    /// <summary>
    /// Geometric distortion of a lens: where the lens puts a point (distorted) that should be at a given place
    /// (undistorted). It works in normalised, de-squeezed coordinates of the image in sensor orientation (squeeze along x):
    ///
    ///   x = 2 (px + 0.5) / W - 1,      y = ((py + 0.5) - H / 2) * 2 / (W * factor)
    ///
    /// for an image of W x H sensor pixels (pixel centres at whole numbers) squeezed by <c>factor</c>. The frame spans
    /// x in [-1, 1] and y in [-H/(W*factor), H/(W*factor)], in units that are equal on both axes once de-squeezed; the same
    /// numbers describe any de-squeezed output, whatever its size. Because x is relative to the image width, a profile
    /// made on one resolution applies to any other of the same frame width (and to crops that keep the width).
    ///
    ///   xd = xu (1 + kx1 r^2 + kx2 r^4 + kx3 r^6) + sum over terms  X_ij xu^i yu^j
    ///   yd = yu (1 + ky1 r^2 + ky2 r^4 + ky3 r^6) + sum over terms  Y_ij xu^i yu^j,        r^2 = xu^2 + yu^2
    ///
    /// The first part is the usual radial model, with separate coefficients for the two axes. The general terms
    /// (<see cref="Terms"/>, monomials of total degree 2 and up) describe everything else a lens can do: lines that bend
    /// one way near the middle of the frame and the other way towards its sides, bending that differs along the two axes
    /// in ways no radial curve follows, and bending that is not symmetric about the centre (a decentred or tilted element).
    /// The model is the identity at the centre, to first order.
    /// </summary>
    [Serializable]
    public sealed class DistortionModel
    {
        /// <summary>Highest total degree a general term may have.</summary>
        public const int MaxDegree = 9;

        /// <summary>k1, k2, k3 for the horizontal displacement.</summary>
        public double[] Kx = new double[3];
        /// <summary>k1, k2, k3 for the vertical displacement.</summary>
        public double[] Ky = new double[3];
        /// <summary>General polynomial terms; empty for a purely radial model.</summary>
        public DistortionTerm[] Terms = new DistortionTerm[0];

        public bool HasTerms
        {
            get
            {
                if (Terms == null) return false;
                foreach (var t in Terms) if (t.X != 0 || t.Y != 0) return true;
                return false;
            }
        }

        public bool IsIdentity
        {
            get
            {
                if (Kx != null) foreach (var k in Kx) if (k != 0) return false;
                if (Ky != null) foreach (var k in Ky) if (k != 0) return false;
                return !HasTerms;
            }
        }

        public DistortionModel Clone()
        {
            return new DistortionModel
            {
                Kx = (double[])Kx.Clone(),
                Ky = (double[])Ky.Clone(),
                Terms = Terms != null ? (DistortionTerm[])Terms.Clone() : new DistortionTerm[0],
            };
        }

        /// <summary>The model with every coefficient multiplied by <paramref name="amount"/> (0 = none, 1 = as measured).</summary>
        public DistortionModel Scaled(double amount)
        {
            var m = Clone();
            for (int i = 0; i < 3; i++)
            {
                m.Kx[i] *= amount;
                m.Ky[i] *= amount;
            }
            for (int i = 0; i < m.Terms.Length; i++)
            {
                m.Terms[i].X *= amount;
                m.Terms[i].Y *= amount;
            }
            return m;
        }

        /// <summary>True when both models have the same coefficients (within <paramref name="tolerance"/>).</summary>
        public bool SameAs(DistortionModel o, double tolerance = 0)
        {
            if (o == null) return false;
            for (int i = 0; i < 3; i++)
                if (Math.Abs(K(Kx, i) - K(o.Kx, i)) > tolerance || Math.Abs(K(Ky, i) - K(o.Ky, i)) > tolerance) return false;
            return TermsWithin(this, o, tolerance) && TermsWithin(o, this, tolerance);
        }

        private static bool TermsWithin(DistortionModel a, DistortionModel b, double tolerance)
        {
            if (a.Terms == null) return true;
            foreach (var t in a.Terms)
            {
                double bx = 0, by = 0;
                if (b.Terms != null)
                    foreach (var u in b.Terms)
                        if (u.I == t.I && u.J == t.J) { bx += u.X; by += u.Y; }
                double ax = 0, ay = 0;
                foreach (var u in a.Terms)
                    if (u.I == t.I && u.J == t.J) { ax += u.X; ay += u.Y; }
                if (Math.Abs(ax - bx) > tolerance || Math.Abs(ay - by) > tolerance) return false;
            }
            return true;
        }

        private static double K(double[] k, int i)
        {
            return k != null && i < k.Length ? k[i] : 0;
        }

        /// <summary>Undistorted -> distorted.</summary>
        public void Distort(double xu, double yu, out double xd, out double yd)
        {
            double r2 = xu * xu + yu * yu;
            double ax = r2 * (K(Kx, 0) + r2 * (K(Kx, 1) + r2 * K(Kx, 2)));
            double ay = r2 * (K(Ky, 0) + r2 * (K(Ky, 1) + r2 * K(Ky, 2)));
            xd = xu * (1 + ax);
            yd = yu * (1 + ay);
            var c = Compiled();
            if (c == null) return;
            unsafe
            {
                double* px = stackalloc double[MaxDegree + 1];
                double* py = stackalloc double[MaxDegree + 1];
                Powers(xu, yu, px, py, c.MaxPower);
                fixed (int* pi = c.I, pj = c.J)
                fixed (double* cx = c.X, cy = c.Y)
                {
                    for (int k = 0; k < c.Count; k++)
                    {
                        double v = px[pi[k]] * py[pj[k]];
                        xd += cx[k] * v;
                        yd += cy[k] * v;
                    }
                }
            }
        }

        private static unsafe void Powers(double x, double y, double* px, double* py, int max)
        {
            px[0] = 1;
            py[0] = 1;
            for (int i = 1; i <= max; i++)
            {
                px[i] = px[i - 1] * x;
                py[i] = py[i - 1] * y;
            }
        }

        /// <summary>The terms laid out for evaluation: valid, non-zero ones only, in plain arrays.</summary>
        private sealed class CompiledTerms
        {
            public DistortionTerm[] Source;
            public int Count, MaxPower;
            public int[] I, J;
            public double[] X, Y, XI, XJ, YI, YJ;     // coefficients, and coefficients times the powers (for the derivatives)
        }

        [NonSerialized] private CompiledTerms _compiled;

        /// <summary>
        /// The compiled terms, or null when there are none. Rebuilt when <see cref="Terms"/> is replaced; a model's terms are
        /// not edited in place once it has been used (<see cref="Scaled"/> and <see cref="Clone"/> make new arrays).
        /// </summary>
        private CompiledTerms Compiled()
        {
            var terms = Terms;
            if (terms == null || terms.Length == 0) return null;
            var c = _compiled;
            if (c != null && ReferenceEquals(c.Source, terms)) return c.Count > 0 ? c : null;
            c = new CompiledTerms { Source = terms };
            int n = 0;
            foreach (var t in terms)
                if (t.I >= 0 && t.J >= 0 && t.I <= MaxDegree && t.J <= MaxDegree && (t.X != 0 || t.Y != 0)) n++;
            c.I = new int[n]; c.J = new int[n];
            c.X = new double[n]; c.Y = new double[n];
            c.XI = new double[n]; c.XJ = new double[n]; c.YI = new double[n]; c.YJ = new double[n];
            foreach (var t in terms)
            {
                if (!(t.I >= 0 && t.J >= 0 && t.I <= MaxDegree && t.J <= MaxDegree && (t.X != 0 || t.Y != 0))) continue;
                int k = c.Count++;
                c.I[k] = t.I; c.J[k] = t.J;
                c.X[k] = t.X; c.Y[k] = t.Y;
                c.XI[k] = t.X * t.I; c.XJ[k] = t.X * t.J;
                c.YI[k] = t.Y * t.I; c.YJ[k] = t.Y * t.J;
                c.MaxPower = Math.Max(c.MaxPower, Math.Max(t.I, t.J));
            }
            _compiled = c;
            return c.Count > 0 ? c : null;
        }

        /// <summary>Undistorted -> distorted, with the Jacobian d(distorted)/d(undistorted).</summary>
        public void Distort(double xu, double yu, out double xd, out double yd,
            out double j00, out double j01, out double j10, out double j11)
        {
            double kx1 = K(Kx, 0), kx2 = K(Kx, 1), kx3 = K(Kx, 2);
            double ky1 = K(Ky, 0), ky2 = K(Ky, 1), ky3 = K(Ky, 2);
            double r2 = xu * xu + yu * yu;
            double ax = r2 * (kx1 + r2 * (kx2 + r2 * kx3));
            double ay = r2 * (ky1 + r2 * (ky2 + r2 * ky3));
            double qx = 2 * (kx1 + r2 * (2 * kx2 + 3 * r2 * kx3));   // d(ax)/d(r2) * 2
            double qy = 2 * (ky1 + r2 * (2 * ky2 + 3 * r2 * ky3));
            xd = xu * (1 + ax);
            yd = yu * (1 + ay);
            j00 = 1 + ax + qx * xu * xu;
            j01 = qx * xu * yu;
            j10 = qy * xu * yu;
            j11 = 1 + ay + qy * yu * yu;
            var ct = Compiled();
            if (ct == null) return;
            unsafe
            {
                // powers shifted by one, so index 0 holds the power -1 (never used with a non-zero factor)
                double* px = stackalloc double[MaxDegree + 2];
                double* py = stackalloc double[MaxDegree + 2];
                px[0] = 0;
                py[0] = 0;
                Powers(xu, yu, px + 1, py + 1, ct.MaxPower);
                fixed (int* pi = ct.I, pj = ct.J)
                fixed (double* cx = ct.X, cy = ct.Y, cxi = ct.XI, cxj = ct.XJ, cyi = ct.YI, cyj = ct.YJ)
                {
                    for (int k = 0; k < ct.Count; k++)
                    {
                        int i = pi[k], j = pj[k];
                        double xi = px[i + 1], yj = py[j + 1];
                        double v = xi * yj, vx = px[i] * yj, vy = xi * py[j];     // x^i y^j, x^(i-1) y^j, x^i y^(j-1)
                        xd += cx[k] * v;
                        yd += cy[k] * v;
                        j00 += cxi[k] * vx;
                        j01 += cxj[k] * vy;
                        j10 += cyi[k] * vx;
                        j11 += cyj[k] * vy;
                    }
                }
            }
        }

        /// <summary>
        /// Distorted -> undistorted (Newton's method). Returns false if it does not converge, which only happens far outside
        /// the range a lens profile is meant for.
        /// </summary>
        public bool Undistort(double xd, double yd, out double xu, out double yu)
        {
            xu = xd;
            yu = yd;
            for (int it = 0; it < 40; it++)
            {
                double fx, fy, a, b, c, d;
                Distort(xu, yu, out fx, out fy, out a, out b, out c, out d);
                double ex = fx - xd, ey = fy - yd;
                if (Math.Abs(ex) < 1e-13 && Math.Abs(ey) < 1e-13) return true;
                double det = a * d - b * c;
                if (Math.Abs(det) < 1e-12) return false;
                double dx = (d * ex - b * ey) / det, dy = (a * ey - c * ex) / det;
                // damp large steps (strong distortion far out)
                double s = Math.Sqrt(dx * dx + dy * dy), lim = 0.25 * (1 + Math.Sqrt(xu * xu + yu * yu));
                if (s > lim) { dx *= lim / s; dy *= lim / s; }
                xu -= dx;
                yu -= dy;
            }
            double rx, ry;
            Distort(xu, yu, out rx, out ry);
            return Math.Abs(rx - xd) < 1e-9 && Math.Abs(ry - yd) < 1e-9;
        }

        /// <summary>
        /// The largest distortion within the frame: how far (as a fraction of the half-width) the lens moves the point that
        /// moves most, sampled over the frame. Useful as a rough "how much distortion" figure.
        /// </summary>
        public double MaxDisplacement(double halfHeight)
        {
            double worst = 0;
            for (int i = 0; i <= 32; i++)
                for (int j = 0; j <= 32; j++)
                {
                    double x = -1 + 2.0 * i / 32, y = (-1 + 2.0 * j / 32) * halfHeight, xu, yu;
                    if (!Undistort(x, y, out xu, out yu)) continue;
                    worst = Math.Max(worst, Math.Sqrt((xu - x) * (xu - x) + (yu - y) * (yu - y)));
                }
            return worst;
        }

        /// <summary>
        /// How far the lens bends a straight line: over lines running the length and the height of the frame at several
        /// positions, the largest distance between the line as the lens draws it and the straight line through its ends,
        /// as a fraction of the half-width. Zero for a lens that only magnifies unevenly without bending anything.
        /// </summary>
        public double MaxBend(double halfHeight)
        {
            double worst = 0;
            const int n = 48;
            for (int k = 0; k <= 8; k++)
            {
                double c = -1 + 2.0 * k / 8;
                for (int dir = 0; dir < 2; dir++)
                {
                    double x0 = 0, y0 = 0, x1 = 0, y1 = 0;
                    for (int pass = 0; pass < 2; pass++)
                        for (int i = 0; i <= n; i++)
                        {
                            double t = -1 + 2.0 * i / n, xd, yd;
                            Distort(dir == 0 ? t : c, (dir == 0 ? c : t) * halfHeight, out xd, out yd);
                            if (pass == 0)
                            {
                                if (i == 0) { x0 = xd; y0 = yd; }
                                if (i == n) { x1 = xd; y1 = yd; }
                                continue;
                            }
                            double lx = x1 - x0, ly = y1 - y0, len = Math.Sqrt(lx * lx + ly * ly);
                            if (len < 1e-12) continue;
                            worst = Math.Max(worst, Math.Abs((xd - x0) * ly - (yd - y0) * lx) / len);
                        }
                }
            }
            return worst;
        }

        /// <summary>All coefficients, precisely enough to tell two models apart (used as a cache key).</summary>
        public override string ToString()
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            sb.AppendFormat(ci, "kx = ({0:0.########}, {1:0.########}, {2:0.########}), ky = ({3:0.########}, {4:0.########}, {5:0.########})",
                K(Kx, 0), K(Kx, 1), K(Kx, 2), K(Ky, 0), K(Ky, 1), K(Ky, 2));
            if (Terms != null && Terms.Length > 0)
            {
                sb.Append(", terms");
                foreach (var t in Terms)
                    sb.AppendFormat(ci, " x{0}y{1}:({2:0.########}, {3:0.########})", t.I, t.J, t.X, t.Y);
            }
            return sb.ToString();
        }
    }

    /// <summary>Converts between pixel coordinates and the model's normalised coordinates for one image.</summary>
    public readonly struct LensFrame
    {
        /// <summary>Image size in pixels and the squeeze still present in it (1 for a de-squeezed image).</summary>
        public readonly double Width, Height, Squeeze;

        public LensFrame(double width, double height, double squeeze)
        {
            Width = width;
            Height = height;
            Squeeze = squeeze;
        }

        /// <summary>Normalised units per pixel along x and y.</summary>
        public double ScaleX => 2 / Width;
        public double ScaleY => 2 / (Width * Squeeze);
        /// <summary>Half the frame's height in normalised units.</summary>
        public double HalfHeight => Height / (Width * Squeeze);

        public void ToNormalised(double px, double py, out double x, out double y)
        {
            x = (px + 0.5) * ScaleX - 1;
            y = (py + 0.5 - Height / 2) * ScaleY;
        }

        public void ToPixel(double x, double y, out double px, out double py)
        {
            px = (x + 1) / ScaleX - 0.5;
            py = y / ScaleY + Height / 2 - 0.5;
        }
    }
}
