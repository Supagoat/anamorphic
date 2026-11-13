using System;

namespace Desqueeze.Core.Imaging
{
    /// <summary>
    /// The image viewer's mapping between image pixels and the pixels of the view it draws into. Everything the viewer
    /// places goes through this one mapping: the image's tiles, the pixel under the cursor, and whatever is drawn over
    /// the image (calibration points and lines), so they cannot drift apart when the view is zoomed or panned.
    ///
    /// Zoom is a whole-number magnification (1, 2, 3 ...) or, negative, a whole-number reduction (-2 = 1/2, -3 = 1/3 ...).
    /// The image's top-left corner always sits on a whole view pixel.
    /// </summary>
    public struct ViewTransform
    {
        /// <summary>Magnification (1 or more) or, negative, the reduction (-2 for 1/2).</summary>
        public int Zoom;
        /// <summary>The image point at the centre of the view, in image pixels with pixel edges at whole numbers.</summary>
        public double CentreX, CentreY;
        /// <summary>Size of the view in its own pixels.</summary>
        public double ViewWidth, ViewHeight;

        public ViewTransform(int zoom, double centreX, double centreY, double viewWidth, double viewHeight)
        {
            Zoom = zoom;
            CentreX = centreX;
            CentreY = centreY;
            ViewWidth = viewWidth;
            ViewHeight = viewHeight;
        }

        public int Magnification => Zoom >= 1 ? Zoom : 1;
        public int Reduction => Zoom >= 1 ? 1 : Math.Max(1, -Zoom);
        /// <summary>View pixels per image pixel.</summary>
        public double Scale => (double)Magnification / Reduction;

        /// <summary>View position of the image's top-left corner (a whole pixel).</summary>
        public double OriginX => Math.Floor(ViewWidth * 0.5 - CentreX / Reduction * Magnification);
        public double OriginY => Math.Floor(ViewHeight * 0.5 - CentreY / Reduction * Magnification);

        /// <summary>View position of the left (or top) edge of image column (or row) <paramref name="edge"/>.</summary>
        public double EdgeToViewX(double edge)
        {
            return OriginX + edge * Scale;
        }

        public double EdgeToViewY(double edge)
        {
            return OriginY + edge * Scale;
        }

        /// <summary>View position of an image point given in pixel-centre coordinates (pixel (0, 0) is centred on (0, 0)).</summary>
        public void ImageToView(double x, double y, out double vx, out double vy)
        {
            vx = OriginX + (x + 0.5) * Scale;
            vy = OriginY + (y + 0.5) * Scale;
        }

        /// <summary>The image point (pixel-centre coordinates) at a view position.</summary>
        public void ViewToImage(double vx, double vy, out double x, out double y)
        {
            x = (vx - OriginX) / Scale - 0.5;
            y = (vy - OriginY) / Scale - 0.5;
        }

        /// <summary>The image pixel that covers a view position.</summary>
        public void PixelAt(double vx, double vy, out int px, out int py)
        {
            px = (int)Math.Floor((vx - OriginX) / Scale);
            py = (int)Math.Floor((vy - OriginY) / Scale);
        }

        /// <summary>The same view moved by a drag of (dx, dy) view pixels: the image follows the drag.</summary>
        public ViewTransform Panned(double dx, double dy)
        {
            var t = this;
            t.CentreX -= dx / Scale;
            t.CentreY -= dy / Scale;
            return t;
        }

        /// <summary>The view at another zoom, keeping the image point under view position (ax, ay) where it is.</summary>
        public ViewTransform Zoomed(int zoom, double ax, double ay)
        {
            // the image point (edge coordinates) drawn at the anchor now, from the origin the image is really drawn at
            double ix = (ax - OriginX) / Scale, iy = (ay - OriginY) / Scale;
            var t = this;
            t.Zoom = zoom;
            t.CentreX = ix + (ViewWidth * 0.5 - ax) / t.Scale;
            t.CentreY = iy + (ViewHeight * 0.5 - ay) / t.Scale;
            return t;
        }
    }
}
