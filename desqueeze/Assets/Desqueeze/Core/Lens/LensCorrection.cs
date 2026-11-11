using System;
using System.Threading;
using Desqueeze.Core.Imaging;
using Desqueeze.Core.Resampling;

namespace Desqueeze.Core.Lens
{
    /// <summary>What happens at the edges after distortion correction, where the corrected picture no longer fills a rectangle.</summary>
    public enum DistortionEdges
    {
        /// <summary>The largest rectangle of the picture's aspect ratio that is filled everywhere.</summary>
        AutoCrop = 0,
        /// <summary>Everything the source covers; the corners or edges that it doesn't are filled with black (or transparent).</summary>
        Uncropped = 1,
    }

    /// <summary>Where distortion correction happens relative to the de-squeeze.</summary>
    public enum DistortionOrder
    {
        /// <summary>
        /// De-squeeze and correct together in two 1-D passes (<see cref="TwoPassWarp"/>): each row is widened by the detail
        /// reconstruction at the corrected positions, then each column is resampled once, which also shortens the height.
        /// </summary>
        AfterDesqueeze = 0,
        /// <summary>Correct the squeezed image, then de-squeeze it.</summary>
        BeforeDesqueeze = 1,
        /// <summary>One resampling from the squeezed capture straight to the corrected, de-squeezed output (kernel only).</summary>
        SinglePass = 2,
    }

    /// <summary>A rectangle in the model's normalised, undistorted coordinates.</summary>
    public struct CanvasRect
    {
        public double X0, Y0, X1, Y1;

        public CanvasRect(double x0, double y0, double x1, double y1)
        {
            X0 = x0; Y0 = y0; X1 = x1; Y1 = y1;
        }

        public double Width => X1 - X0;
        public double Height => Y1 - Y0;

        public override string ToString()
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, "[{0:0.0000}, {1:0.0000}] x [{2:0.0000}, {3:0.0000}]", X0, X1, Y0, Y1);
        }
    }

    /// <summary>Applies a <see cref="DistortionModel"/> to an image.</summary>
    public static class LensCorrection
    {
        /// <summary>
        /// The part of the undistorted plane the corrected picture shows, for a frame spanning x in [-1, 1] and
        /// y in [-halfHeight, halfHeight]. The model should already be scaled by the correction amount.
        /// </summary>
        public static CanvasRect Canvas(DistortionModel model, double halfHeight, DistortionEdges edges)
        {
            if (model == null || model.IsIdentity) return new CanvasRect(-1, -halfHeight, 1, halfHeight);
            if (edges == DistortionEdges.Uncropped)
            {
                // Bounding box of the source frame's outline once undistorted.
                double x0 = double.MaxValue, y0 = double.MaxValue, x1 = double.MinValue, y1 = double.MinValue;
                const int n = 512;
                for (int i = 0; i <= n; i++)
                {
                    double t = -1 + 2.0 * i / n;
                    foreach (var p in new[] { new[] { t, -halfHeight }, new[] { t, halfHeight }, new[] { -1.0, t * halfHeight }, new[] { 1.0, t * halfHeight } })
                    {
                        double xu, yu;
                        if (!model.Undistort(p[0], p[1], out xu, out yu)) continue;
                        x0 = Math.Min(x0, xu); x1 = Math.Max(x1, xu);
                        y0 = Math.Min(y0, yu); y1 = Math.Max(y1, yu);
                    }
                }
                return new CanvasRect(x0, y0, x1, y1);
            }
            // Largest centred rectangle of the frame's aspect whose outline the lens maps inside the source frame.
            double lo = 0.05, hi = 4;
            for (int it = 0; it < 50; it++)
            {
                double s = 0.5 * (lo + hi);
                if (Fits(model, s, halfHeight)) lo = s; else hi = s;
            }
            return new CanvasRect(-lo, -lo * halfHeight, lo, lo * halfHeight);
        }

        private static bool Fits(DistortionModel model, double s, double halfHeight)
        {
            const int n = 256;
            const double tol = 1e-9;
            for (int i = 0; i <= n; i++)
            {
                double t = -1 + 2.0 * i / n;
                foreach (var p in new[] { new[] { t * s, -s * halfHeight }, new[] { t * s, s * halfHeight }, new[] { -s, t * s * halfHeight }, new[] { s, t * s * halfHeight } })
                {
                    double xd, yd;
                    model.Distort(p[0], p[1], out xd, out yd);
                    if (Math.Abs(xd) > 1 + tol || Math.Abs(yd) > halfHeight + tol) return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Output size in pixels for a canvas, at <paramref name="pixelsPerUnitX"/> / <paramref name="pixelsPerUnitY"/>
        /// pixels per normalised unit (W/2 and W*squeeze/2 for an image W wide with that squeeze left in it).
        /// </summary>
        public static void CanvasSize(CanvasRect r, double pixelsPerUnitX, double pixelsPerUnitY, out int width, out int height)
        {
            width = Math.Max(1, (int)Math.Round(r.Width * pixelsPerUnitX));
            height = Math.Max(1, (int)Math.Round(r.Height * pixelsPerUnitY));
        }

        /// <summary>
        /// Corrects <paramref name="src"/> (sensor orientation, with <paramref name="srcSqueeze"/> still in it: 1 for a
        /// de-squeezed image) onto <paramref name="canvas"/>, rendered <paramref name="outWidth"/> x <paramref name="outHeight"/>
        /// pixels. The output may itself be squeezed: its squeeze is implied by the canvas and the size.
        /// </summary>
        public static PixelImage Correct(PixelImage src, double srcSqueeze, DistortionModel model, CanvasRect canvas,
            int outWidth, int outHeight, WarpOptions options, ProgressTracker progress = null, CancellationToken ct = default)
        {
            var map = new DistortionMap(model, new LensFrame(src.Width, src.Height, srcSqueeze), canvas, outWidth, outHeight);
            return WarpResampler.Warp(src, outWidth, outHeight, map, options, progress, ct);
        }

        /// <summary>Output pixel -> undistorted normalised point -> lens -> source pixel.</summary>
        private sealed class DistortionMap : WarpMap
        {
            private readonly DistortionModel _m;
            private readonly LensFrame _src;
            private readonly double _x0, _y0, _ux, _uy;

            public DistortionMap(DistortionModel m, LensFrame src, CanvasRect canvas, int outW, int outH)
            {
                _m = m;
                _src = src;
                _x0 = canvas.X0;
                _y0 = canvas.Y0;
                _ux = canvas.Width / outW;
                _uy = canvas.Height / outH;
            }

            public override void Map(double x, double y, out double sx, out double sy,
                out double j00, out double j01, out double j10, out double j11)
            {
                double xu = _x0 + (x + 0.5) * _ux, yu = _y0 + (y + 0.5) * _uy;
                double xd, yd, a, b, c, d;
                _m.Distort(xu, yu, out xd, out yd, out a, out b, out c, out d);
                _src.ToPixel(xd, yd, out sx, out sy);
                double px = 1 / _src.ScaleX, py = 1 / _src.ScaleY;
                j00 = px * a * _ux;
                j01 = px * b * _uy;
                j10 = py * c * _ux;
                j11 = py * d * _uy;
            }
        }
    }
}
