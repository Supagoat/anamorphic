using UnityEngine;
using UnityEngine.Rendering;

namespace Desqueeze.App.UI
{
    /// <summary>
    /// Draws anti-alias-free thin lines and small squares over the image view during a Repaint, batched through GL so a few
    /// thousand segments cost almost nothing. Coordinates are GUI points at the top level (not inside a GUI area); everything
    /// is clipped to the rectangle given to <see cref="Begin"/>. Falls back to GUI textures if the GL shader is missing.
    /// </summary>
    internal static class Overlay
    {
        private static Material _mat;
        private static bool _triedMaterial;
        private static Rect _clip;
        private static bool _gl;
        private static bool _open;

        private static Material Mat
        {
            get
            {
                if (_mat == null && !_triedMaterial)
                {
                    _triedMaterial = true;
                    var shader = Shader.Find("Hidden/Internal-Colored");
                    if (shader != null)
                    {
                        _mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                        _mat.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                        _mat.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                        _mat.SetInt("_Cull", (int)CullMode.Off);
                        _mat.SetInt("_ZWrite", 0);
                        _mat.SetInt("_ZTest", (int)CompareFunction.Always);
                    }
                }
                return _mat;
            }
        }

        /// <summary>Starts a batch clipped to <paramref name="clip"/>. Only during Repaint.</summary>
        public static bool Begin(Rect clip)
        {
            if (Event.current.type != EventType.Repaint) return false;
            _clip = clip;
            var m = Mat;
            _gl = m != null;
            if (_gl)
            {
                m.SetPass(0);
                GL.PushMatrix();
                GL.LoadPixelMatrix();
                GL.Begin(GL.QUADS);
            }
            _open = true;
            return true;
        }

        public static void End()
        {
            if (!_open) return;
            _open = false;
            if (_gl)
            {
                GL.End();
                GL.PopMatrix();
            }
        }

        /// <summary>A line segment <paramref name="width"/> points wide.</summary>
        public static void Line(Vector2 a, Vector2 b, float width, Color color)
        {
            if (!_open || !ClipSegment(ref a, ref b)) return;
            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 1e-4f) return;
            if (_gl)
            {
                float ppp = UiStyles.PixelsPerPoint;
                Vector2 n = new Vector2(-d.y, d.x) / len * (width * 0.5f);
                GL.Color(color);
                Vertex(a + n, ppp);
                Vertex(b + n, ppp);
                Vertex(b - n, ppp);
                Vertex(a - n, ppp);
            }
            else
            {
                var m = GUI.matrix;
                var old = GUI.color;
                GUI.color = color;
                float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
                GUIUtility.RotateAroundPivot(angle, a);
                GUI.DrawTexture(new Rect(a.x, a.y - width * 0.5f, len, width), Texture2D.whiteTexture);
                GUI.matrix = m;
                GUI.color = old;
            }
        }

        /// <summary>A filled square centred on <paramref name="c"/>.</summary>
        public static void Square(Vector2 c, float half, Color color)
        {
            if (!_open) return;
            if (c.x + half < _clip.xMin || c.x - half > _clip.xMax || c.y + half < _clip.yMin || c.y - half > _clip.yMax) return;
            if (_gl)
            {
                float ppp = UiStyles.PixelsPerPoint;
                float x0 = Mathf.Max(c.x - half, _clip.xMin), x1 = Mathf.Min(c.x + half, _clip.xMax);
                float y0 = Mathf.Max(c.y - half, _clip.yMin), y1 = Mathf.Min(c.y + half, _clip.yMax);
                GL.Color(color);
                Vertex(new Vector2(x0, y0), ppp);
                Vertex(new Vector2(x1, y0), ppp);
                Vertex(new Vector2(x1, y1), ppp);
                Vertex(new Vector2(x0, y1), ppp);
            }
            else
            {
                var old = GUI.color;
                GUI.color = color;
                GUI.DrawTexture(new Rect(c.x - half, c.y - half, 2 * half, 2 * half), Texture2D.whiteTexture);
                GUI.color = old;
            }
        }

        /// <summary>A square outline (a ring) of the given thickness.</summary>
        public static void Box(Vector2 c, float half, float thickness, Color color)
        {
            Line(new Vector2(c.x - half, c.y - half + thickness * 0.5f), new Vector2(c.x + half, c.y - half + thickness * 0.5f), thickness, color);
            Line(new Vector2(c.x - half, c.y + half - thickness * 0.5f), new Vector2(c.x + half, c.y + half - thickness * 0.5f), thickness, color);
            Line(new Vector2(c.x - half + thickness * 0.5f, c.y - half + thickness), new Vector2(c.x - half + thickness * 0.5f, c.y + half - thickness), thickness, color);
            Line(new Vector2(c.x + half - thickness * 0.5f, c.y - half + thickness), new Vector2(c.x + half - thickness * 0.5f, c.y + half - thickness), thickness, color);
        }

        private static void Vertex(Vector2 p, float ppp)
        {
            GL.Vertex3(p.x * ppp, Screen.height - p.y * ppp, 0);
        }

        /// <summary>Liang-Barsky clipping against the batch's rectangle.</summary>
        private static bool ClipSegment(ref Vector2 a, ref Vector2 b)
        {
            float t0 = 0, t1 = 1;
            float dx = b.x - a.x, dy = b.y - a.y;
            if (!Clip(-dx, a.x - _clip.xMin, ref t0, ref t1)) return false;
            if (!Clip(dx, _clip.xMax - a.x, ref t0, ref t1)) return false;
            if (!Clip(-dy, a.y - _clip.yMin, ref t0, ref t1)) return false;
            if (!Clip(dy, _clip.yMax - a.y, ref t0, ref t1)) return false;
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
