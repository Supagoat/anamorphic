using System.Globalization;

namespace Desqueeze.Core.Lens
{
    /// <summary>Human-readable figures for a <see cref="DistortionModel"/>.</summary>
    public static class DistortionSummary
    {
        /// <summary>
        /// "Lines bend by up to 1.2% of the frame width": the largest bow the lens gives a straight line running the length or
        /// the height of the frame (see <see cref="DistortionModel.MaxBend"/>), relative to the frame's de-squeezed width.
        /// Says nothing about the shape of the distortion, which can be anything the model describes.
        /// </summary>
        public static string Describe(DistortionModel m, double halfHeight)
        {
            if (m == null || m.IsIdentity) return "No distortion";
            double pct = m.MaxBend(halfHeight) * 50;      // normalised units span 2 across the width
            return "Lines bend by up to " + pct.ToString(pct < 1 ? "0.00" : "0.0", CultureInfo.InvariantCulture) + "% of the frame width";
        }
    }
}
