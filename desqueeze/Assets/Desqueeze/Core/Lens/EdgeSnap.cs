using System;
using Desqueeze.Core.Color;
using Desqueeze.Core.Imaging;

namespace Desqueeze.Core.Lens
{
    /// <summary>
    /// Moves a point placed on a calibration line onto the edge the user meant, to a fraction of a pixel. For a horizontal
    /// line it searches up and down, for a vertical line left and right, for the strongest change of brightness
    /// (a derivative-of-Gaussian across a profile averaged along the line), keeping the polarity of the line's other
    /// points so a line stays on the same side of a mortar joint or frame.
    /// </summary>
    public static class EdgeSnap
    {
        /// <param name="radius">
        /// How far the point may move, in pixels: how uncertain the click was. The point never moves further than this, so
        /// a stronger edge a few pixels away cannot pull it off the one the user clicked.
        /// </param>
        /// <param name="polarity">+1 / -1 to require dark-to-light / light-to-dark (down or right), 0 for either.</param>
        /// <returns>True with the snapped position and the edge's polarity; false if there is no clear edge nearby.</returns>
        public static bool Snap(PixelImage img, double x, double y, bool horizontalLine, double radius, int polarity,
            out double sx, out double sy, out int foundPolarity)
        {
            sx = x;
            sy = y;
            foundPolarity = 0;
            if (img == null) return false;
            radius = Math.Max(0.5, radius);
            double click = horizontalLine ? y : x;
            const double sigma = 1.0;
            int margin = (int)Math.Ceiling(3 * sigma) + 1;
            int lo = (int)Math.Floor((horizontalLine ? y : x) - radius) - margin;
            int hi = (int)Math.Ceiling((horizontalLine ? y : x) + radius) + margin;
            int limit = horizontalLine ? img.Height : img.Width;
            if (lo < 0 || hi > limit - 1) return false;
            int along = (int)Math.Round(horizontalLine ? x : y);
            int alongLimit = horizontalLine ? img.Width : img.Height;
            int a0 = Math.Max(0, along - 4), a1 = Math.Min(alongLimit - 1, along + 4);

            var lut = img.BitDepth == 8 ? Srgb.Decode8 : Srgb.Decode16;
            int n = hi - lo + 1;
            var p = new double[n];
            for (int i = 0; i < n; i++)
            {
                double s = 0;
                for (int a = a0; a <= a1; a++)
                    s += Luma(img, lut, horizontalLine ? a : lo + i, horizontalLine ? lo + i : a);
                p[i] = s / (a1 - a0 + 1);
            }
            int R = (int)Math.Ceiling(3 * sigma);
            var d = new double[n];
            double norm = 0;
            for (int j = -R; j <= R; j++) norm += j * j * Math.Exp(-j * j / (2 * sigma * sigma));
            for (int i = R; i < n - R; i++)
            {
                double s = 0;
                for (int j = -R; j <= R; j++) s += j * Math.Exp(-j * j / (2 * sigma * sigma)) * p[i + j];
                d[i] = s / norm;
            }
            double centre = (horizontalLine ? y : x) - lo;
            int best = -1;
            double bestScore = 0, typical = 0;
            for (int i = margin; i < n - margin; i++) typical = Math.Max(typical, Math.Abs(d[i]));
            for (int i = margin; i < n - margin; i++)
            {
                if (Math.Abs(lo + i - click) > radius + 0.5) continue;     // only edges within reach of the click
                double v = polarity != 0 ? polarity * d[i] : Math.Abs(d[i]);
                if (v <= 0 || v < (polarity != 0 ? polarity * d[i - 1] : Math.Abs(d[i - 1])) ||
                    v <= (polarity != 0 ? polarity * d[i + 1] : Math.Abs(d[i + 1]))) continue;
                // Prefer strong edges near where the user clicked.
                double t = (i - centre) / radius;
                double score = v * Math.Exp(-0.5 * t * t);
                if (score > bestScore) { bestScore = score; best = i; }
            }
            if (best < 0 || Math.Abs(d[best]) < 0.004 || Math.Abs(d[best]) < 0.25 * typical) return false;
            double l = Math.Abs(d[best - 1]), c = Math.Abs(d[best]), r = Math.Abs(d[best + 1]);
            double den = l - 2 * c + r;
            double off = Math.Abs(den) > 1e-12 ? 0.5 * (l - r) / den : 0;
            off = Math.Max(-0.5, Math.Min(0.5, off));
            double pos = lo + best + off;
            if (Math.Abs(pos - click) > radius) return false;             // keep the point where it was put
            if (horizontalLine) sy = pos; else sx = pos;
            foundPolarity = d[best] > 0 ? 1 : -1;
            return true;
        }

        private static double Luma(PixelImage img, float[] lut, int x, int y)
        {
            int o = (y * img.Width + x) * img.Channels;
            if (img.BitDepth == 8)
            {
                var d = img.Data8;
                return img.Channels <= 2 ? lut[d[o]] : 0.2126 * lut[d[o]] + 0.7152 * lut[d[o + 1]] + 0.0722 * lut[d[o + 2]];
            }
            var e = img.Data16;
            return img.Channels <= 2 ? lut[e[o]] : 0.2126 * lut[e[o]] + 0.7152 * lut[e[o + 1]] + 0.0722 * lut[e[o + 2]];
        }
    }
}
