using System;

namespace Desqueeze.Core.Imaging
{
    /// <summary>
    /// Splits a line segment into axis-aligned runs, one per pixel row (for a mostly horizontal segment) or column (for a
    /// mostly vertical one) it crosses, so it can be drawn with plain rectangles: no rotation, crisp, and cheap for the
    /// nearly level and nearly upright lines of the calibration overlay.
    /// </summary>
    public static class LineRaster
    {
        /// <param name="horizontal">True: the run lies along x at rows [row, row + width); false: along y at columns [row, row + width).</param>
        /// <param name="row">Whole-number position of the run's top (or left) edge.</param>
        /// <param name="from">Start along the run's direction.</param>
        /// <param name="to">End along the run's direction (greater than <paramref name="from"/>).</param>
        public delegate void RunSink(bool horizontal, double row, double from, double to);

        /// <summary>Emits the runs of the segment (ax, ay) - (bx, by), <paramref name="width"/> thick; returns how many.</summary>
        public static int Runs(double ax, double ay, double bx, double by, double width, RunSink sink)
        {
            double dx = bx - ax, dy = by - ay;
            if (dx * dx + dy * dy < 1e-12) return 0;
            bool horizontal = Math.Abs(dx) >= Math.Abs(dy);
            // s runs along the major axis, c across it
            double s0 = horizontal ? ax : ay, s1 = horizontal ? bx : by;
            double c0 = horizontal ? ay : ax, c1 = horizontal ? by : bx;
            if (s1 < s0)
            {
                double t = s0; s0 = s1; s1 = t;
                t = c0; c0 = c1; c1 = t;
            }
            double half = width * 0.5;
            double slope = (c1 - c0) / (s1 - s0);
            double row = Math.Floor(c0 - half + 0.5);
            if (Math.Abs(slope) < 1e-9)
            {
                sink(horizontal, row, s0, s1);
                return 1;
            }
            int n = 0;
            double from = s0, step = slope > 0 ? 1 : -1;
            for (int guard = 0; guard < 100000; guard++)
            {
                // the line's centre leaves this row where it reaches row + half +/- 0.5
                double leave = s0 + (row + half + 0.5 * step - c0) / slope;
                if (leave >= s1)
                {
                    if (s1 > from) { sink(horizontal, row, from, s1); n++; }
                    return n;
                }
                if (leave > from)
                {
                    sink(horizontal, row, from, leave);
                    n++;
                    from = leave;
                }
                row += step;
            }
            return n;
        }
    }
}
