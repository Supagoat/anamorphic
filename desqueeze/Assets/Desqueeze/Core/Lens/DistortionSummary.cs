using System;
using System.Globalization;

namespace Desqueeze.Core.Lens
{
    /// <summary>Kinds of distortion, for describing a measurement.</summary>
    public enum DistortionType
    {
        None = 0,
        /// <summary>Straight lines bow outwards (the edges of the frame are squeezed towards the centre).</summary>
        Barrel = 1,
        /// <summary>Straight lines bow inwards.</summary>
        Pincushion = 2,
        /// <summary>Barrel near the centre turning to pincushion towards the edges, or the reverse.</summary>
        Moustache = 3,
    }

    /// <summary>Human-readable figures for a <see cref="DistortionModel"/>.</summary>
    public static class DistortionSummary
    {
        /// <summary>
        /// Whether points are pushed outwards (barrel) or inwards (pincushion) by the correction, looking at the frame's
        /// outline and at half its size.
        /// </summary>
        public static DistortionType Classify(DistortionModel m, double halfHeight)
        {
            if (m == null || m.IsIdentity) return DistortionType.None;
            double inner = MeanRadialCorrection(m, halfHeight, 0.5), outer = MeanRadialCorrection(m, halfHeight, 1.0);
            const double eps = 1e-6;
            if (Math.Abs(outer) < eps && Math.Abs(inner) < eps) return DistortionType.None;
            // The inner ring moves 1/8 as much for pure r^2 distortion; a sign change between the rings is a moustache.
            if (inner * outer < 0 && Math.Abs(inner) > eps) return DistortionType.Moustache;
            return outer > 0 ? DistortionType.Barrel : DistortionType.Pincushion;
        }

        /// <summary>Mean radial movement the correction applies along the frame's outline scaled by <paramref name="scale"/> (positive = outwards).</summary>
        private static double MeanRadialCorrection(DistortionModel m, double halfHeight, double scale)
        {
            double sum = 0;
            int n = 0;
            for (int i = 0; i <= 32; i++)
            {
                double t = -1 + 2.0 * i / 32;
                foreach (var p in new[] { new[] { t, halfHeight }, new[] { t, -halfHeight }, new[] { 1.0, t * halfHeight }, new[] { -1.0, t * halfHeight } })
                {
                    double xd = p[0] * scale, yd = p[1] * scale, xu, yu;
                    if (!m.Undistort(xd, yd, out xu, out yu)) continue;
                    sum += Math.Sqrt(xu * xu + yu * yu) - Math.Sqrt(xd * xd + yd * yd);
                    n++;
                }
            }
            return n > 0 ? sum / n : 0;
        }

        /// <summary>True when the model bends horizontal and vertical lines by different amounts.</summary>
        public static bool IsAnamorphic(DistortionModel m)
        {
            if (m == null) return false;
            for (int i = 0; i < 3; i++)
                if (Math.Abs(m.Kx[i] - m.Ky[i]) > 1e-9) return true;
            return false;
        }

        /// <summary>
        /// "Barrel, up to 1.2% at the edges" style text. The percentage is the largest movement of any point of the frame's
        /// outline, relative to the frame's (de-squeezed) width.
        /// </summary>
        public static string Describe(DistortionModel m, double halfHeight)
        {
            var type = Classify(m, halfHeight);
            if (type == DistortionType.None) return "No distortion";
            double pct = m.MaxDisplacement(halfHeight) * 50;      // normalised units span 2 across the width
            string kind = type == DistortionType.Barrel ? "Barrel" : type == DistortionType.Pincushion ? "Pincushion" : "Moustache";
            return kind + (IsAnamorphic(m) ? " (anamorphic)" : "") + ", up to " +
                   pct.ToString(pct < 1 ? "0.00" : "0.0", CultureInfo.InvariantCulture) + "% at the edges";
        }
    }
}
