using System;

namespace Desqueeze.Core
{
    /// <summary>
    /// Works out the output size of an anamorphic de-squeeze and how the correction is split
    /// between widening the image and shortening it.
    ///
    /// An anamorphic lens with factor A records a scene that is A times wider than the sensor
    /// shows. Any output whose aspect ratio is A x (source aspect) is a correct de-squeeze; what
    /// is free to choose is how much of that comes from width versus height:
    ///
    ///     widthScale / heightScale == A        (the lock that keeps the ratio correct)
    ///
    /// The single "width share" slider t in [0, 1] chooses a point on that line geometrically:
    ///
    ///     widthScale  = A^t
    ///     heightScale = A^(t - 1)
    ///
    ///     t = 0    -> width x1,        height x 1/A   (classic: only reduce height, no upsampling)
    ///     t = 0.5  -> width x sqrt(A), height x 1/sqrt(A)   (total pixel count is preserved)
    ///     t = 1    -> width x A,       height x1      (keeps every captured row; widens only)
    ///
    /// The geometric (log-space) split means equal slider steps feel like equal amounts of
    /// change in either direction, and the midpoint is the pixel-count-preserving split.
    /// </summary>
    public readonly struct DesqueezeGeometry
    {
        /// <summary>Largest output dimension we allow (PNG and baseline JPEG both cap at 65535).</summary>
        public const int MaxDimension = 65535;

        public readonly int SourceWidth;
        public readonly int SourceHeight;
        public readonly int OutputWidth;
        public readonly int OutputHeight;
        public readonly double Factor;
        public readonly double WidthShare;
        /// <summary>Nominal (unrounded) horizontal scale, A^t.</summary>
        public readonly double WidthScale;
        /// <summary>Nominal (unrounded) vertical scale, A^(t-1).</summary>
        public readonly double HeightScale;
        /// <summary>Axis the lens squeezed. For Vertical the slider's "width share" is the share given to growing the height.</summary>
        public readonly SqueezeAxis Axis;

        private DesqueezeGeometry(int sw, int sh, int ow, int oh, double factor, double share, double sx, double sy, SqueezeAxis axis)
        {
            Axis = axis;
            SourceWidth = sw;
            SourceHeight = sh;
            OutputWidth = ow;
            OutputHeight = oh;
            Factor = factor;
            WidthShare = share;
            WidthScale = sx;
            HeightScale = sy;
        }

        public static double WidthScaleFor(double factor, double widthShare)
        {
            return Math.Pow(factor, Clamp01(widthShare));
        }

        public static double HeightScaleFor(double factor, double widthShare)
        {
            return Math.Pow(factor, Clamp01(widthShare) - 1.0);
        }

        /// <summary>Inverse of <see cref="WidthScaleFor"/>: the slider position that gives a width scale.</summary>
        public static double WidthShareForWidthScale(double factor, double widthScale)
        {
            if (factor <= 0 || Math.Abs(factor - 1.0) < 1e-12 || widthScale <= 0) return 0.0;
            return Clamp01(Math.Log(widthScale) / Math.Log(factor));
        }

        public static DesqueezeGeometry Compute(int sourceWidth, int sourceHeight, double factor, double widthShare)
        {
            return Compute(sourceWidth, sourceHeight, factor, widthShare, SqueezeAxis.Horizontal);
        }

        /// <summary>
        /// For a vertical squeeze the roles swap: the stretched axis is the height, and the slider share is
        /// the part of the correction that comes from growing the height.
        /// </summary>
        public static DesqueezeGeometry Compute(int sourceWidth, int sourceHeight, double factor, double stretchShare, SqueezeAxis axis)
        {
            if (sourceWidth <= 0 || sourceHeight <= 0)
                throw new ArgumentException("Source dimensions must be positive.");
            if (!(factor > 0) || double.IsInfinity(factor))
                throw new ArgumentOutOfRangeException(nameof(factor), "Anamorphic factor must be a positive number.");

            double t = Clamp01(stretchShare);
            double grow = Math.Pow(factor, t);
            double shrink = grow / factor;
            double sx = axis == SqueezeAxis.Horizontal ? grow : shrink;
            double sy = axis == SqueezeAxis.Horizontal ? shrink : grow;

            int ow = RoundToInt(sourceWidth * sx);
            int oh = RoundToInt(sourceHeight * sy);
            if (ow > MaxDimension || oh > MaxDimension)
                throw new ArgumentException(
                    "Output would be " + ow + " x " + oh + " px, larger than the " + MaxDimension + " px limit.");
            return new DesqueezeGeometry(sourceWidth, sourceHeight, ow, oh, factor, t, sx, sy, axis);
        }

        /// <summary>The aspect ratio the de-squeeze should produce: A x source aspect.</summary>
        public double TargetAspect => Axis == SqueezeAxis.Horizontal
            ? (double)SourceWidth * Factor / SourceHeight
            : SourceWidth / ((double)SourceHeight * Factor);

        public double OutputAspect => (double)OutputWidth / OutputHeight;

        /// <summary>Aspect error introduced by rounding to whole pixels, in percent (always tiny).</summary>
        public double AspectErrorPercent => (OutputAspect / TargetAspect - 1.0) * 100.0;

        /// <summary>Output pixel count relative to the source.</summary>
        public double PixelCountRatio => (double)OutputWidth * OutputHeight / ((double)SourceWidth * SourceHeight);

        public bool IsIdentity => OutputWidth == SourceWidth && OutputHeight == SourceHeight;

        public override string ToString()
        {
            return SourceWidth + "x" + SourceHeight + " -> " + OutputWidth + "x" + OutputHeight +
                   " (width x" + WidthScale.ToString("0.####") + ", height x" + HeightScale.ToString("0.####") + ")";
        }

        private static int RoundToInt(double v)
        {
            double r = Math.Round(v, MidpointRounding.AwayFromZero);
            if (r < 1) return 1;
            if (r > int.MaxValue) return int.MaxValue;
            return (int)r;
        }

        private static double Clamp01(double v)
        {
            if (double.IsNaN(v)) return 0;
            return v < 0 ? 0 : (v > 1 ? 1 : v);
        }
    }
}
