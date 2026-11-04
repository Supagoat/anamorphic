using Desqueeze.Core.Imaging;

namespace Desqueeze.Core
{
    /// <summary>Which way to de-squeeze, as the user chooses it.</summary>
    public enum SqueezeDirection
    {
        /// <summary>
        /// Decide per image: vertical when the camera was turned 90° (EXIF orientation 5-8), or when the upright
        /// image is taller than wide (the turn was already baked into the pixels); horizontal otherwise.
        /// </summary>
        Auto = 0,
        /// <summary>Always across the width of the upright image.</summary>
        Horizontal = 1,
        /// <summary>Always along the height of the upright image.</summary>
        Vertical = 2,
    }

    /// <summary>The axis a de-squeeze will use for one image, and why.</summary>
    public readonly struct AxisChoice
    {
        public readonly SqueezeAxis Axis;
        /// <summary>True when <see cref="SqueezeDirection.Auto"/> made the decision.</summary>
        public readonly bool Automatic;
        /// <summary>Short explanation for the UI, e.g. "camera turned 90° (orientation tag)".</summary>
        public readonly string Reason;

        public AxisChoice(SqueezeAxis axis, bool automatic, string reason)
        {
            Axis = axis;
            Automatic = automatic;
            Reason = reason;
        }

        public bool IsVertical => Axis == SqueezeAxis.Vertical;

        public override string ToString()
        {
            return (IsVertical ? "vertical" : "horizontal") + " - " + Reason;
        }
    }

    /// <summary>
    /// Decides the squeeze axis of an upright image (see <see cref="Codecs.ImageIO.MakeUpright"/>).
    ///
    /// An anamorphic lens squeezes along the sensor's long side. When the camera is turned 90° for a portrait
    /// shot, the lens turns with it, so in the upright picture the squeeze runs vertically. Cameras record the
    /// turn in the EXIF orientation tag; software that rotates the pixels itself usually resets the tag, but then
    /// the picture is taller than it is wide, which a sensor never is.
    /// </summary>
    public static class AxisChooser
    {
        public static AxisChoice Choose(SqueezeDirection direction, PixelImage upright)
        {
            int o = upright != null && upright.Metadata != null ? upright.Metadata.SourceOrientation : 0;
            return upright == null
                ? Choose(direction, 0, 0, o)
                : Choose(direction, upright.Width, upright.Height, o);
        }

        /// <param name="width">Width of the upright image.</param>
        /// <param name="height">Height of the upright image.</param>
        /// <param name="sourceOrientation">EXIF orientation of the source file (1-8), or 0 if it had none.</param>
        public static AxisChoice Choose(SqueezeDirection direction, int width, int height, int sourceOrientation)
        {
            switch (direction)
            {
                case SqueezeDirection.Horizontal:
                    return new AxisChoice(SqueezeAxis.Horizontal, false, "set by you");
                case SqueezeDirection.Vertical:
                    return new AxisChoice(SqueezeAxis.Vertical, false, "set by you");
            }
            if (Orientation.SwapsAxes(sourceOrientation))
                return new AxisChoice(SqueezeAxis.Vertical, true, "camera turned 90° (orientation tag)");
            bool tagged = Orientation.IsValid(sourceOrientation);
            if (height > width && width > 0)
                return new AxisChoice(SqueezeAxis.Vertical, true,
                    tagged ? "portrait image, already rotated" : "portrait image, no orientation tag");
            return new AxisChoice(SqueezeAxis.Horizontal, true,
                tagged ? "camera level (orientation tag)" : "landscape image, no orientation tag");
        }
    }
}
