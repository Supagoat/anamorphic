using System;
using System.Threading;
using Desqueeze.Core.Imaging;

namespace Desqueeze.Core.Calibration
{
    /// <summary>
    /// A de-squeezed close-up of a found circle, and where the circle must sit if the factor is right.
    ///
    /// The axis the lens did not squeeze holds the circle's true radius. After the de-squeeze that radius is
    /// scaled by the unsqueezed axis' scale, which gives the ring (<see cref="RingRadius"/>). The squeezed axis
    /// is scaled by the other scale; the two only agree, and the circle is round, when the factor matches the lens.
    /// </summary>
    public sealed class CalibrationPreview
    {
        public PixelImage Image;
        /// <summary>Circle centre in the preview, pixel-centre coordinates.</summary>
        public double CenterX, CenterY;
        /// <summary>The radius the circle should have in the preview (pixels).</summary>
        public double RingRadius;
        /// <summary>Where the circle's edge actually lands in the preview along x and y (pixels).</summary>
        public double ImageRadiusX, ImageRadiusY;
        /// <summary>Settings the preview was rendered with.</summary>
        public double Factor, Share;
        public SqueezeAxis Axis;

        /// <summary>
        /// Renders the close-up with the kernel selected in <paramref name="settings"/> (the learned widener would only
        /// change detail, not geometry, and the kernel keeps the preview interactive).
        /// </summary>
        public static CalibrationPreview Render(PixelImage source, EllipseFit fit, DesqueezeSettings settings, SqueezeAxis axis,
            CancellationToken ct = default)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (fit == null) throw new ArgumentNullException(nameof(fit));
            var s = settings.Clone();
            s.Validate();
            double grow = DesqueezeGeometry.WidthScaleFor(s.Factor, s.WidthShare);
            double shrink = DesqueezeGeometry.HeightScaleFor(s.Factor, s.WidthShare);
            bool horiz = axis == SqueezeAxis.Horizontal;
            double sx = horiz ? grow : shrink, sy = horiz ? shrink : grow;

            // Source region: the circle and the ring (which, in source pixels, is r_true / factor wide along the squeezed
            // axis) with a margin around them.
            double rt = fit.TrueRadius(axis);
            double hx = 1.45 * (horiz ? Math.Max(fit.RadiusX, rt / s.Factor) : rt) + 8;
            double hy = 1.45 * (horiz ? rt : Math.Max(fit.RadiusY, rt / s.Factor)) + 8;
            int x0, w, y0, h;
            Span(fit.CenterX, hx, sx, source.Width, out x0, out w);
            Span(fit.CenterY, hy, sy, source.Height, out y0, out h);
            int ow = Math.Max(1, (int)Math.Round(w * sx)), oh = Math.Max(1, (int)Math.Round(h * sy));
            if (ow > DesqueezeGeometry.MaxDimension || oh > DesqueezeGeometry.MaxDimension)
                throw new ArgumentException("The circle is too large to preview.");

            var crop = Crop(source, x0, y0, w, h);
            s.Widen = WidenMethod.Kernel;
            if (s.Algorithm == ResampleAlgorithm.EwaLanczosSharp) s.Algorithm = ResampleAlgorithm.MagicKernelSharp2021;
            var img = Desqueezer.Resize(crop, ow, oh, s, null, ct);

            // Map with the scales the resampler really used (whole-pixel sizes).
            double ax = (double)ow / w, ay = (double)oh / h;
            return new CalibrationPreview
            {
                Image = img,
                CenterX = (fit.CenterX - x0 + 0.5) * ax - 0.5,
                CenterY = (fit.CenterY - y0 + 0.5) * ay - 0.5,
                RingRadius = rt * (horiz ? ay : ax),
                ImageRadiusX = fit.RadiusX * ax,
                ImageRadiusY = fit.RadiusY * ay,
                Factor = s.Factor,
                Share = s.WidthShare,
                Axis = axis,
            };
        }

        /// <summary>
        /// A crop [start, start+length) covering centre +- half, clamped to the image. Its length is nudged by a few pixels so
        /// length * scale is as close to a whole number as possible: the preview's actual scale then matches the
        /// factor to within about 0.01% (well below one step of the factor's last digit) unless the crop meets the image border.
        /// </summary>
        private static void Span(double centre, double half, double scale, int size, out int start, out int length)
        {
            int a = Math.Max(0, (int)Math.Floor(centre - half));
            int b = Math.Min(size, (int)Math.Ceiling(centre + half) + 1);
            int bestLen = b - a, bestA = a;
            double bestErr = double.MaxValue;
            for (int extra = 0; extra <= 24; extra++)
            {
                int len = (b - a) + extra;
                if (len > size) break;
                int s0 = a - extra / 2;
                if (s0 < 0) s0 = 0;
                if (s0 + len > size) s0 = size - len;
                double v = len * scale;
                double err = Math.Abs(v - Math.Round(v)) / v;
                if (err < bestErr - 1e-12)
                {
                    bestErr = err;
                    bestLen = len;
                    bestA = s0;
                }
            }
            start = bestA;
            length = Math.Max(1, bestLen);
        }

        private static PixelImage Crop(PixelImage src, int x0, int y0, int w, int h)
        {
            var dst = src.CreateLike(w, h);
            int ch = src.Channels;
            for (int y = 0; y < h; y++)
            {
                int s = ((y0 + y) * src.Width + x0) * ch, d = y * w * ch;
                if (src.BitDepth == 8) Buffer.BlockCopy(src.Data8, s, dst.Data8, d, w * ch);
                else Buffer.BlockCopy(src.Data16, s * 2, dst.Data16, d * 2, w * ch * 2);
            }
            return dst;
        }
    }
}
