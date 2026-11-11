using System;

namespace Desqueeze.Core.Lens
{
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
    ///   xd = xu (1 + kx1 r^2 + kx2 r^4 + kx3 r^6)
    ///   yd = yu (1 + ky1 r^2 + ky2 r^4 + ky3 r^6),        r^2 = xu^2 + yu^2
    ///
    /// Separate x and y coefficients describe anamorphic lenses, whose horizontal and vertical lines bend by different
    /// amounts; equal ones are the classic radial model. Negative k1 is barrel distortion, positive pincushion.
    /// The model is the identity at the centre (no scale change there) and symmetric under left-right and up-down
    /// mirroring, so it does not matter which way a camera was turned for a portrait shot.
    /// </summary>
    [Serializable]
    public sealed class DistortionModel
    {
        /// <summary>k1, k2, k3 for the horizontal displacement.</summary>
        public double[] Kx = new double[3];
        /// <summary>k1, k2, k3 for the vertical displacement.</summary>
        public double[] Ky = new double[3];

        public bool IsIdentity
        {
            get
            {
                if (Kx == null || Ky == null) return true;
                foreach (var k in Kx) if (k != 0) return false;
                foreach (var k in Ky) if (k != 0) return false;
                return true;
            }
        }

        public DistortionModel Clone()
        {
            return new DistortionModel { Kx = (double[])Kx.Clone(), Ky = (double[])Ky.Clone() };
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
            return m;
        }

        private double K(double[] k, int i)
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
            double px = 2 * (kx1 + r2 * (2 * kx2 + 3 * r2 * kx3));   // d(ax)/d(r2) * 2
            double py = 2 * (ky1 + r2 * (2 * ky2 + 3 * r2 * ky3));
            xd = xu * (1 + ax);
            yd = yu * (1 + ay);
            j00 = 1 + ax + px * xu * xu;
            j01 = px * xu * yu;
            j10 = py * xu * yu;
            j11 = 1 + ay + py * yu * yu;
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
        /// moves most, sampled along the frame's edges. Useful as a rough "how much distortion" figure.
        /// </summary>
        public double MaxDisplacement(double halfHeight)
        {
            double worst = 0;
            for (int i = 0; i <= 64; i++)
            {
                double t = -1 + 2.0 * i / 64;
                foreach (var p in new[] { new[] { t, halfHeight }, new[] { t, -halfHeight }, new[] { 1.0, t * halfHeight }, new[] { -1.0, t * halfHeight } })
                {
                    double xu, yu;
                    if (!Undistort(p[0], p[1], out xu, out yu)) continue;
                    worst = Math.Max(worst, Math.Sqrt((xu - p[0]) * (xu - p[0]) + (yu - p[1]) * (yu - p[1])));
                }
            }
            return worst;
        }

        public override string ToString()
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "kx = ({0:0.#####}, {1:0.#####}, {2:0.#####}), ky = ({3:0.#####}, {4:0.#####}, {5:0.#####})",
                K(Kx, 0), K(Kx, 1), K(Kx, 2), K(Ky, 0), K(Ky, 1), K(Ky, 2));
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
