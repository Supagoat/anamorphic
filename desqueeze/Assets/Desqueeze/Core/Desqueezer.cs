using System;
using System.Threading;
using Desqueeze.Core.Imaging;
using Desqueeze.Core.Resampling;

namespace Desqueeze.Core
{
    /// <summary>
    /// Thread-safe progress reporting that the UI can poll. Work is reported as a fraction of the
    /// current stage, which is mapped into an overall range set with <see cref="BeginStage"/>.
    /// </summary>
    public sealed class ProgressTracker
    {
        private float _value;
        private string _stage = "";
        private float _rangeStart;
        private float _rangeSpan = 1f;
        private readonly ProgressTracker _parent;
        private readonly float _pa, _pb;

        public ProgressTracker() { }

        private ProgressTracker(ProgressTracker parent, float a, float b)
        {
            _parent = parent;
            _pa = a;
            _pb = b;
        }

        /// <summary>A tracker whose 0..1 maps onto [a, b] of this tracker's current stage (null-safe helper).</summary>
        public static ProgressTracker Slice(ProgressTracker parent, float a, float b)
        {
            return parent == null ? null : new ProgressTracker(parent, a, b);
        }

        /// <summary>Overall progress, 0..1.</summary>
        public float Value => Volatile.Read(ref _value);

        /// <summary>Name of the current stage, for display.</summary>
        public string Stage => Volatile.Read(ref _stage);

        public void BeginStage(string stage, float start, float span)
        {
            Volatile.Write(ref _stage, stage);
            _rangeStart = start;
            _rangeSpan = span;
            Volatile.Write(ref _value, start);
        }

        /// <summary>Reports progress within the current stage (0..1).</summary>
        public void Report(float fractionOfStage)
        {
            if (fractionOfStage < 0) fractionOfStage = 0;
            if (fractionOfStage > 1) fractionOfStage = 1;
            if (_parent != null)
            {
                _parent.Report(_pa + (_pb - _pa) * fractionOfStage);
                return;
            }
            Volatile.Write(ref _value, _rangeStart + _rangeSpan * fractionOfStage);
        }

        public void Reset()
        {
            Volatile.Write(ref _stage, "");
            _rangeStart = 0;
            _rangeSpan = 1;
            Volatile.Write(ref _value, 0f);
        }
    }

    /// <summary>Entry point for de-squeezing an image.</summary>
    public static class Desqueezer
    {
        /// <summary>
        /// De-squeezes <paramref name="source"/> according to <paramref name="settings"/>.
        /// <paramref name="source"/> should be upright (see <see cref="Codecs.ImageIO.MakeUpright"/>); the squeeze
        /// axis comes from <see cref="DesqueezeSettings.Direction"/> via <see cref="AxisChooser"/>.
        /// Returns a new image with the same channel layout and bit depth, carrying the source metadata.
        /// Runs on the calling thread and uses all cores; call it from a background task in UI code.
        /// </summary>
        public static PixelImage Process(PixelImage source, DesqueezeSettings settings,
            ProgressTracker progress = null, CancellationToken ct = default)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            var s = settings.Clone();
            s.Validate();
            if (AxisChooser.Choose(s.Direction, source).Axis == SqueezeAxis.Horizontal)
            {
                var g = DesqueezeGeometry.Compute(source.Width, source.Height, s.Factor, s.WidthShare, SqueezeAxis.Horizontal);
                return Resize(source, g.OutputWidth, g.OutputHeight, s, progress, ct);
            }

            // Vertical squeeze: turn the image a quarter turn so the squeeze runs horizontally, run exactly the horizontal
            // pipeline, and turn the result back. When the camera's orientation tag says how it was turned, the internal
            // turn undoes that, so the resampler sees the sensor's own pixels and the result is identical to
            // de-squeezing the sideways file and rotating it afterwards.
            int o = source.Metadata != null ? source.Metadata.SourceOrientation : 0;
            int turn = Orientation.SwapsAxes(o) ? Orientation.Inverse(o) : Orientation.Rotate90Clockwise;
            var turned = Orientation.Apply(source, turn);
            var gt = DesqueezeGeometry.Compute(turned.Width, turned.Height, s.Factor, s.WidthShare, SqueezeAxis.Horizontal);
            var r = Resize(turned, gt.OutputWidth, gt.OutputHeight, s, progress, ct);
            turned = null;
            ct.ThrowIfCancellationRequested();
            var back = Orientation.Apply(r, Orientation.Inverse(turn));
            back.Metadata = source.Metadata != null ? source.Metadata.Clone() : new ImageMetadata();
            back.SourceFormat = source.SourceFormat;
            return back;
        }

        /// <summary>The output geometry <see cref="Process"/> will produce for this image and these settings.</summary>
        public static DesqueezeGeometry Geometry(PixelImage source, DesqueezeSettings settings)
        {
            var axis = AxisChooser.Choose(settings.Direction, source).Axis;
            return DesqueezeGeometry.Compute(source.Width, source.Height, settings.Factor, settings.WidthShare, axis);
        }

        /// <summary>
        /// The axis being enlarged when resizing between these sizes, if exactly one axis grows and the other does
        /// not (the case the learned method handles).
        /// </summary>
        private static bool StretchAxis(int srcW, int srcH, int outW, int outH, out SqueezeAxis axis)
        {
            axis = SqueezeAxis.Horizontal;
            if (outW > srcW && outH <= srcH) return true;
            axis = SqueezeAxis.Vertical;
            return outH > srcH && outW <= srcW;
        }

        /// <summary>Resamples to an explicit size using the algorithm and options in <paramref name="settings"/>.</summary>
        public static PixelImage Resize(PixelImage source, int width, int height, DesqueezeSettings settings,
            ProgressTracker progress = null, CancellationToken ct = default)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (width <= 0 || height <= 0) throw new ArgumentException("Output size must be positive.");
            var s = settings.Clone();
            s.Validate();
            var dst = source.CreateLike(width, height);

            if (width == source.Width && height == source.Height)
            {
                if (source.BitDepth == 8) Buffer.BlockCopy(source.Data8, 0, dst.Data8, 0, source.Data8.Length);
                else Buffer.BlockCopy(source.Data16, 0, dst.Data16, 0, source.Data16.Length * 2);
                if (progress != null) progress.Report(1f);
                return dst;
            }

            PixelImage learned = TryLearned(source, width, height, s, progress, ct);
            if (learned != null) return learned;

            if (s.Algorithm == ResampleAlgorithm.EwaLanczosSharp)
                EwaResampler.Run(source, dst, s, progress, ct);
            else
                SeparableResampler.Run(source, dst, Kernels.For(s.Algorithm), s, progress, ct);
            return dst;
        }

        /// <summary>Largest enlargement of the stretched axis handled by the learned method (it was trained up to 2x).</summary>
        public const double MaxLearnedScale = 2.25;

        /// <summary>True when Resize/Process would use the learned cross-axis reconstruction for these settings.</summary>
        public static bool UsesLearned(int srcW, int srcH, int outW, int outH, DesqueezeSettings s)
        {
            if (s.Widen == WidenMethod.Kernel || AxisSRModel.Default == null) return false;
            SqueezeAxis axis;
            if (!StretchAxis(srcW, srcH, outW, outH, out axis)) return false;
            bool horiz = axis == SqueezeAxis.Horizontal;
            double scale = horiz ? (double)outW / srcW : (double)outH / srcH;
            return scale <= MaxLearnedScale;
        }

        /// <summary>
        /// Learned path: enlarge the stretched axis first, at full resolution on the other axis (that is where the
        /// extra information comes from), then shorten the other axis with the selected kernel if needed.
        /// </summary>
        private static PixelImage TryLearned(PixelImage source, int width, int height, DesqueezeSettings s,
            ProgressTracker progress, CancellationToken ct)
        {
            if (!UsesLearned(source.Width, source.Height, width, height, s)) return null;
            var model = AxisSRModel.Default;
            SqueezeAxis axis;
            StretchAxis(source.Width, source.Height, width, height, out axis);
            bool horiz = axis == SqueezeAxis.Horizontal;
            int outLen = horiz ? width : height;
            bool shrinkOther = horiz ? height != source.Height : width != source.Width;
            var opts = new AxisSROptions
            {
                UseNetwork = s.Widen == WidenMethod.Detail,
                OutputBitDepth = shrinkOther ? 16 : source.BitDepth,   // keep precision for the second pass
            };
            var grown = AxisSR.Upscale(source, outLen, axis, model, opts,
                ProgressTracker.Slice(progress, 0f, shrinkOther ? 0.85f : 1f), ct);
            if (!shrinkOther)
            {
                grown.Metadata = source.Metadata != null ? source.Metadata.Clone() : new ImageMetadata();
                return grown;
            }
            var dst = source.CreateLike(width, height);
            var reduceKernel = s.Algorithm == ResampleAlgorithm.EwaLanczosSharp
                ? Kernels.For(ResampleAlgorithm.MagicKernelSharp2021)    // EWA is 2-D; use a separable kernel for this 1-D pass
                : Kernels.For(s.Algorithm);
            SeparableResampler.Run(grown, dst, reduceKernel, s, ProgressTracker.Slice(progress, 0.85f, 1f), ct);
            return dst;
        }
    }
}
