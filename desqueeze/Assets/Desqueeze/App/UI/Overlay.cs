using UnityEngine;

namespace Desqueeze.App.UI
{
    /// <summary>
    /// Draws thin lines and small squares over the image view during a Repaint.
    ///
    /// Everything is drawn with the same call the image's tiles are drawn with (GUI.DrawTexture inside a GUI clip of the
    /// view), so marks and image share one coordinate system by construction: whatever the GUI does to place the image on
    /// screen (window position, display scaling, the editor's Game view zoom), it does to the marks as well. An earlier
    /// version drew through GL in raw frame-buffer pixels, which only lines up when one GUI unit is exactly one
    /// frame-buffer pixel at the window's origin; where it isn't, the marks slid away from the image.
    ///
    /// Coordinates passed in are GUI points at the top level (not inside a GUI area), like the rectangle given to
    /// <see cref="Begin"/>, which also clips. Lines are drawn as runs of axis-aligned rectangles, one per pixel row (or
    /// column) the line crosses, so a nearly level curve of a thousand pixels costs a few dozen rectangles.
    /// </summary>
    internal static class Overlay
    {
        private static Rect _clip;          // top-level GUI coordinates
        private static bool _open;

        // the run being accumulated
        private static bool _runOpen, _runHorizontal;
        private static float _runRow, _runFrom, _runTo, _runWidth;
        private static Color _runColor;

        /// <summary>Starts drawing, clipped to <paramref name="clip"/>. Only during Repaint; returns false otherwise.</summary>
        public static bool Begin(Rect clip)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return false;
            _clip = clip;
            GUI.BeginClip(clip);
            _open = true;
            _runOpen = false;
            return true;
        }

        public static void End()
        {
            if (!_open) return;
            Flush();
            _open = false;
            GUI.EndClip();
        }

        private static readonly Desqueeze.Core.Imaging.LineRaster.RunSink Sink = AddRun;
        private static float _lineWidth;
        private static Color _lineColor;

        /// <summary>A line segment <paramref name="width"/> points wide. Consecutive segments of a polyline join seamlessly.</summary>
        public static void Line(Vector2 a, Vector2 b, float width, Color color)
        {
            if (!_open) return;
            a -= _clip.position;
            b -= _clip.position;
            if (!ClipSegment(ref a, ref b, width)) return;
            _lineWidth = Mathf.Max(1f, Mathf.Round(width));
            _lineColor = color;
            Desqueeze.Core.Imaging.LineRaster.Runs(a.x, a.y, b.x, b.y, _lineWidth, Sink);
        }

        private static void AddRun(bool horizontal, double row, double from, double to)
        {
            Run(horizontal, (float)row, (float)from, (float)to, _lineWidth, _lineColor);
        }

        /// <summary>A filled square centred on <paramref name="c"/>, snapped to whole points.</summary>
        public static void Square(Vector2 c, float half, Color color)
        {
            if (!_open) return;
            Flush();
            c -= _clip.position;
            if (c.x + half < 0 || c.x - half > _clip.width || c.y + half < 0 || c.y - half > _clip.height) return;
            float size = Mathf.Max(1f, Mathf.Round(2 * half));
            Fill(new Rect(Mathf.Round(c.x - size * 0.5f), Mathf.Round(c.y - size * 0.5f), size, size), color);
        }

        /// <summary>A square outline of the given thickness.</summary>
        public static void Box(Vector2 c, float half, float thickness, Color color)
        {
            if (!_open) return;
            Flush();
            c -= _clip.position;
            if (c.x + half < 0 || c.x - half > _clip.width || c.y + half < 0 || c.y - half > _clip.height) return;
            float t = Mathf.Max(1f, Mathf.Round(thickness));
            float size = Mathf.Max(2 * t + 1, Mathf.Round(2 * half));
            float x = Mathf.Round(c.x - size * 0.5f), y = Mathf.Round(c.y - size * 0.5f);
            Fill(new Rect(x, y, size, t), color);
            Fill(new Rect(x, y + size - t, size, t), color);
            Fill(new Rect(x, y + t, t, size - 2 * t), color);
            Fill(new Rect(x + size - t, y + t, t, size - 2 * t), color);
        }

        /// <summary>Adds a piece of a line to the current run, or starts a new run if it doesn't continue it.</summary>
        private static void Run(bool horizontal, float row, float from, float to, float width, Color color)
        {
            if (to <= from) return;
            if (_runOpen && _runHorizontal == horizontal && _runRow == row && _runWidth == width && _runColor == color &&
                from <= _runTo + 0.01f && to >= _runFrom - 0.01f)
            {
                _runFrom = Mathf.Min(_runFrom, from);
                _runTo = Mathf.Max(_runTo, to);
                return;
            }
            Flush();
            _runOpen = true;
            _runHorizontal = horizontal;
            _runRow = row;
            _runFrom = from;
            _runTo = to;
            _runWidth = width;
            _runColor = color;
        }

        private static void Flush()
        {
            if (!_runOpen) return;
            _runOpen = false;
            float w = Mathf.Max(1f, _runWidth);
            Fill(_runHorizontal ? new Rect(_runFrom, _runRow, _runTo - _runFrom, w) : new Rect(_runRow, _runFrom, w, _runTo - _runFrom), _runColor);
        }

        private static void Fill(Rect r, Color color)
        {
            var old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(r, Texture2D.whiteTexture, ScaleMode.StretchToFill, true);
            GUI.color = old;
        }

        /// <summary>Liang-Barsky clipping against the clip rectangle (in its own coordinates), grown by the line's width.</summary>
        private static bool ClipSegment(ref Vector2 a, ref Vector2 b, float width)
        {
            float t0 = 0, t1 = 1;
            float dx = b.x - a.x, dy = b.y - a.y;
            float m = width + 1;
            if (!Clip(-dx, a.x + m, ref t0, ref t1)) return false;
            if (!Clip(dx, _clip.width + m - a.x, ref t0, ref t1)) return false;
            if (!Clip(-dy, a.y + m, ref t0, ref t1)) return false;
            if (!Clip(dy, _clip.height + m - a.y, ref t0, ref t1)) return false;
            var a0 = a;
            if (t1 < 1) b = new Vector2(a0.x + t1 * dx, a0.y + t1 * dy);
            if (t0 > 0) a = new Vector2(a0.x + t0 * dx, a0.y + t0 * dy);
            return true;
        }

        private static bool Clip(float p, float q, ref float t0, ref float t1)
        {
            if (Mathf.Abs(p) < 1e-9f) return q >= 0;
            float r = q / p;
            if (p < 0)
            {
                if (r > t1) return false;
                if (r > t0) t0 = r;
            }
            else
            {
                if (r < t0) return false;
                if (r < t1) t1 = r;
            }
            return true;
        }
    }
}
