using System;

namespace Desqueeze.Core
{
    /// <summary>Resampling algorithms available for the de-squeeze.</summary>
    public enum ResampleAlgorithm
    {
        /// <summary>Elliptical weighted average with a Jinc-windowed Jinc ("EWA Lanczos Sharp").</summary>
        EwaLanczosSharp = 0,
        /// <summary>Separable 3-lobe Lanczos (windowed sinc).</summary>
        Lanczos3 = 1,
        /// <summary>Separable 4-lobe Lanczos.</summary>
        Lanczos4 = 2,
        /// <summary>Separable Panorama Tools 36-tap spline (3-lobe).</summary>
        Spline36 = 3,
        /// <summary>Separable Panorama Tools 64-tap spline (4-lobe).</summary>
        Spline64 = 4,
        /// <summary>John Costella's Magic Kernel Sharp 2021.</summary>
        MagicKernelSharp2021 = 5,
        /// <summary>Cubic, B=0 C=0.5.</summary>
        CatmullRom = 6,
        /// <summary>Cubic, B=C=1/3.</summary>
        Mitchell = 7,
        /// <summary>Triangle filter.</summary>
        Bilinear = 8,
        /// <summary>Nearest neighbour when enlarging, exact area average when reducing.</summary>
        Box = 9,
    }

    /// <summary>The light space a resampling pass is computed in.</summary>
    public enum LightSpace
    {
        /// <summary>Physically linear light (sRGB decoded). Keeps the brightness of fine detail correct.</summary>
        Linear = 0,
        /// <summary>The file's own gamma-encoded (sRGB) values. Gentlest halos on dark edges.</summary>
        Gamma = 1,
        /// <summary>
        /// Linear light through an inverse sigmoid (mpv's "sigmoid-upscaling", ImageMagick's sigmoidal-contrast resize):
        /// compresses overshoot near black and white so sharp filters don't produce halos.
        /// Images with alpha use Linear instead.
        /// </summary>
        Sigmoidal = 2,
    }

    /// <summary>The axis of the (upright) image that the lens squeezed. See <see cref="SqueezeDirection"/> for the setting.</summary>
    public enum SqueezeAxis
    {
        Horizontal = 0,
        Vertical = 1,
    }

    /// <summary>How the stretched axis is enlarged.</summary>
    public enum WidenMethod
    {
        /// <summary>Learned cross-axis detail reconstruction: filter bank + detail network (sharpest, slower).</summary>
        Detail = 0,
        /// <summary>Learned filter bank only (fast, most of the gain).</summary>
        DetailFast = 1,
        /// <summary>The classic kernel selected in <see cref="DesqueezeSettings.Algorithm"/>.</summary>
        Kernel = 2,
    }

    /// <summary>Everything that controls a de-squeeze. Shared by interactive and batch modes.</summary>
    [Serializable]
    public sealed class DesqueezeSettings
    {
        /// <summary>Anamorphic squeeze factor, e.g. 1.33, 1.5, 1.6, 2.0.</summary>
        public double Factor = 1.5;

        /// <summary>
        /// How the correction is split: 0 = shorten height only, 1 = widen width only.
        /// See <see cref="DesqueezeGeometry"/>.
        /// </summary>
        public double WidthShare = 0.5;

        /// <summary>
        /// Default chosen by benchmark (see README): Magic Kernel Sharp 2021 had the best average fidelity
        /// across width-only, balanced and height-only de-squeezes.
        /// </summary>
        public ResampleAlgorithm Algorithm = ResampleAlgorithm.MagicKernelSharp2021;

        /// <summary>
        /// Light space for the axis being shortened (usually height). Gamma measured best: linear-light
        /// reduction with sharp kernels crushes the dark side of high-contrast edges.
        /// </summary>
        public LightSpace ReduceLight = LightSpace.Gamma;

        /// <summary>Light space for the axis being enlarged (usually width). Sigmoidal measured best.</summary>
        public LightSpace EnlargeLight = LightSpace.Sigmoidal;

        /// <summary>
        /// How the stretched axis is enlarged. The learned methods reconstruct detail using the fully sampled
        /// other axis; they need <see cref="Resampling.AxisSRModel.Default"/> to be loaded, otherwise the kernel is used.
        /// </summary>
        public WidenMethod Widen = WidenMethod.Detail;

        /// <summary>
        /// Which axis to de-squeeze. Auto follows the camera's orientation for each image (see <see cref="AxisChooser"/>).
        /// </summary>
        public SqueezeDirection Direction = SqueezeDirection.Auto;

        /// <summary>
        /// Anti-ringing strength for the enlarged axis, 0..1. Clamps each new sample toward the range of its
        /// nearest source samples, removing overshoot halos at a small cost in micro-contrast.
        /// </summary>
        public float AntiRinging = 0f;

        /// <summary>
        /// Lens distortion to correct, measured in the sensor's orientation (see <see cref="Lens.DistortionModel"/>);
        /// null or identity for none. Usually comes from the active lens profile.
        /// </summary>
        public Lens.DistortionModel Distortion;

        /// <summary>How much of <see cref="Distortion"/> to correct: 0 = none, 1 = as measured, up to 2.</summary>
        public double DistortionAmount = 1;

        /// <summary>Crop the corrected picture to the pixels the source covers, or keep everything it shows.</summary>
        public Lens.DistortionEdges DistortionEdges = Lens.DistortionEdges.AutoCrop;

        /// <summary>
        /// Where the correction happens. De-squeezing first measured best (see README): the detail widener works on
        /// the captured pixels, and the warp then resamples an image that is already full resolution on both axes.
        /// </summary>
        public Lens.DistortionOrder DistortionOrder = Lens.DistortionOrder.AfterDesqueeze;

        /// <summary>The distortion scaled by <see cref="DistortionAmount"/>, or null when nothing is to be corrected.</summary>
        public Lens.DistortionModel ActiveDistortion()
        {
            if (Distortion == null || Distortion.IsIdentity || !(DistortionAmount > 0)) return null;
            var m = Distortion.Scaled(DistortionAmount);
            return m.IsIdentity ? null : m;
        }

        public DesqueezeSettings Clone()
        {
            var c = (DesqueezeSettings)MemberwiseClone();
            c.Distortion = Distortion != null ? Distortion.Clone() : null;
            return c;
        }

        public void Validate()
        {
            if (!(Factor > 0) || double.IsInfinity(Factor))
                throw new ArgumentOutOfRangeException(nameof(Factor), "Anamorphic factor must be a positive number.");
            if (double.IsNaN(WidthShare)) WidthShare = 0;
            if (WidthShare < 0) WidthShare = 0;
            if (WidthShare > 1) WidthShare = 1;
            if (float.IsNaN(AntiRinging)) AntiRinging = 0;
            if (AntiRinging < 0) AntiRinging = 0;
            if (AntiRinging > 1) AntiRinging = 1;
            if (double.IsNaN(DistortionAmount) || DistortionAmount < 0) DistortionAmount = 0;
            if (DistortionAmount > 2) DistortionAmount = 2;
        }

        public override string ToString()
        {
            return "factor " + Factor.ToString("0.###") + ", width share " + WidthShare.ToString("0.###") +
                   ", " + Algorithm + ", widen " + Widen + ", direction " + Direction +
                   ", reduce " + ReduceLight + ", enlarge " + EnlargeLight +
                   (AntiRinging > 0 ? ", AR " + AntiRinging.ToString("0.##") : "") +
                   (ActiveDistortion() != null ? ", distortion " + Distortion + " x" + DistortionAmount.ToString("0.##") +
                       " " + DistortionEdges + " " + DistortionOrder : "");
        }
    }
}
