using System;
using System.Collections.Generic;
using Desqueeze.Core;
using Desqueeze.Core.Imaging;
using Desqueeze.Core.Lens;
using NUnit.Framework;

namespace Desqueeze.Tests
{
    /// <summary>EditMode tests for lens profiles and distortion correction.</summary>
    public class LensTests
    {
        private static DistortionModel Model(double kx1, double ky1, double kx2 = 0, double ky2 = 0)
        {
            var m = new DistortionModel();
            m.Kx[0] = kx1;
            m.Ky[0] = ky1;
            m.Kx[1] = kx2;
            m.Ky[1] = ky2;
            return m;
        }

        /// <summary>Straight lines of the undistorted plane, as the lens put them on a W x H capture.</summary>
        private static List<CalibrationLine> Lines(DistortionModel m, int w, int h, double f)
        {
            var frame = new LensFrame(w, h, f);
            double hh = frame.HalfHeight;
            var lines = new List<CalibrationLine>();
            for (int k = 0; k < 7; k++)
            {
                double c = -0.92 + 1.84 * k / 6;
                foreach (bool horiz in new[] { true, false })
                {
                    var l = new CalibrationLine { orientation = horiz ? "horizontal" : "vertical" };
                    for (int i = 0; i <= 24; i++)
                    {
                        double t = -1.2 + 2.4 * i / 24;
                        double xd, yd, px, py;
                        m.Distort(horiz ? t : c, horiz ? c * hh : t * hh, out xd, out yd);
                        if (Math.Abs(xd) > 0.99 || Math.Abs(yd) > 0.99 * hh) continue;
                        frame.ToPixel(xd, yd, out px, out py);
                        l.points.Add(new LinePoint(px, py));
                    }
                    if (l.points.Count >= 3) lines.Add(l);
                }
            }
            return lines;
        }

        [Test]
        public void Model_UndistortInvertsDistort()
        {
            var rnd = new Random(1);
            foreach (var m in new[] { Model(-0.03, -0.03), Model(0.04, 0.04), Model(-0.06, -0.02, 0.01, 0.004) })
                for (int i = 0; i < 200; i++)
                {
                    double xu = rnd.NextDouble() * 2 - 1, yu = (rnd.NextDouble() * 2 - 1) * 0.7, xd, yd, x, y;
                    m.Distort(xu, yu, out xd, out yd);
                    Assert.IsTrue(m.Undistort(xd, yd, out x, out y));
                    Assert.AreEqual(xu, x, 1e-9);
                    Assert.AreEqual(yu, y, 1e-9);
                }
        }

        [Test]
        public void Fit_RecoversKnownDistortion()
        {
            foreach (var truth in new[] { Model(-0.03, -0.03), Model(0.025, 0.025), Model(-0.04, -0.02) })
            {
                var r = DistortionFit.Fit(Lines(truth, 6000, 4000, 1.5), 6000, 4000, 1.5);
                Assert.Less(r.RmsAfter, 0.1, truth.ToString());
                // compare the corrections over the frame, in de-squeezed pixels
                double hh = 4000.0 / (6000 * 1.5), ppu = 6000 * 1.5 / 2, worst = 0;
                for (int i = 0; i <= 20; i++)
                    for (int j = 0; j <= 20; j++)
                    {
                        double x = -1 + i / 10.0, y = (-1 + j / 10.0) * hh, ax, ay, bx, by;
                        truth.Undistort(x, y, out ax, out ay);
                        r.Model.Undistort(x, y, out bx, out by);
                        worst = Math.Max(worst, Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by)) * ppu);
                    }
                Assert.Less(worst, 1.0, truth + " fitted as " + r.Model);
            }
        }

        /// <summary>A lens no radial model describes: horizontals bow one way in the middle of the frame and the other way at its sides.</summary>
        private static DistortionModel Complex()
        {
            var m = new DistortionModel();
            m.Terms = new[]
            {
                new DistortionTerm(3, 0, -0.02, 0), new DistortionTerm(1, 2, -0.02, 0),
                new DistortionTerm(2, 1, 0, -0.05), new DistortionTerm(4, 1, 0, 0.07), new DistortionTerm(0, 3, 0, -0.01),
            };
            return m;
        }

        [Test]
        public void Model_GeneralTermsInvertAndScale()
        {
            var m = Complex();
            var rnd = new Random(2);
            for (int i = 0; i < 300; i++)
            {
                double xu = rnd.NextDouble() * 2 - 1, yu = (rnd.NextDouble() * 2 - 1) * 0.45, xd, yd, x, y, a, b, c, d, x1, y1, x2, y2;
                m.Distort(xu, yu, out xd, out yd, out a, out b, out c, out d);
                Assert.IsTrue(m.Undistort(xd, yd, out x, out y));
                Assert.AreEqual(xu, x, 1e-9);
                Assert.AreEqual(yu, y, 1e-9);
                m.Distort(xu + 1e-6, yu, out x1, out y1);
                m.Distort(xu - 1e-6, yu, out x2, out y2);
                Assert.AreEqual((x1 - x2) / 2e-6, a, 1e-6);
                Assert.AreEqual((y1 - y2) / 2e-6, c, 1e-6);
            }
            Assert.IsFalse(m.IsIdentity);
            Assert.IsTrue(m.Scaled(0).IsIdentity);
            Assert.IsTrue(m.Scaled(0.5).Scaled(2).SameAs(m, 1e-15));
            Assert.IsFalse(m.Scaled(0.5).SameAs(m));
        }

        [Test]
        public void Fit_StraightensLinesNoRadialModelCan()
        {
            var truth = Complex();
            var lines = Lines(truth, 6000, 4000, 1.5);
            var r = DistortionFit.Fit(lines, 6000, 4000, 1.5);
            Assert.GreaterOrEqual(r.Rung, 4, r.Kind);
            Assert.Less(r.RmsAfter, 0.3, r.Kind);
            var radialOnly = DistortionFit.Fit(lines, 6000, 4000, 1.5, new DistortionFitOptions { MaxRung = 3 });
            Assert.Greater(radialOnly.RmsAfter, 4 * r.RmsAfter);
            // a plain radial lens still gets the plain radial model
            Assert.AreEqual(0, DistortionFit.Fit(Lines(Model(-0.03, -0.03), 6000, 4000, 1.5), 6000, 4000, 1.5).Rung);
        }

        [Test]
        public void Profile_GeneralTermsRoundTrip()
        {
            var p = new LensProfile { name = "Complex", squeezeFactor = 1.5, hasDistortion = true, distortion = Complex() };
            string json = LensProfileJson.Write(p);
            var back = LensProfileJson.Read(json);
            Assert.IsTrue(back.distortion.SameAs(p.distortion));
            Assert.AreEqual(json, LensProfileJson.Write(back));
            Assert.IsTrue(json.Contains("\"formatVersion\": 2"));
        }

        [Test]
        public void Viewer_MarksFollowTheImage()
        {
            var rnd = new Random(4);
            foreach (int zoom in new[] { -8, -2, 1, 3, 16 })
                for (int k = 0; k < 100; k++)
                {
                    var t = new ViewTransform(zoom, rnd.NextDouble() * 6000, rnd.NextDouble() * 4000, 1000, 700);
                    double x = rnd.NextDouble() * 6000, y = rnd.NextDouble() * 4000, vx, vy, bx, by, wx, wy;
                    t.ImageToView(x, y, out vx, out vy);
                    t.ViewToImage(vx, vy, out bx, out by);
                    Assert.AreEqual(x, bx, 1e-9);
                    Assert.AreEqual(y, by, 1e-9);
                    // a mark at a pixel's centre sits midway between the edges the image is drawn with
                    int px = (int)x;
                    t.ImageToView(px, 0, out vx, out vy);
                    Assert.AreEqual(0.5 * (t.EdgeToViewX(px) + t.EdgeToViewX(px + 1)), vx, 1e-9);
                    // panning moves marks exactly as far as the image
                    t.ImageToView(x, y, out vx, out vy);
                    t.Panned(37, -12).ImageToView(x, y, out wx, out wy);
                    Assert.AreEqual(vx + 37, wx, 1e-6);
                    Assert.AreEqual(vy - 12, wy, 1e-6);
                }
        }

        [Test]
        public void Profile_JsonRoundTrip()
        {
            var p = new LensProfile
            {
                name = "Lens \"A\"",
                squeezeFactor = 1.54,
                notes = "two\nlines",
                hasDistortion = true,
                distortion = Model(-0.0153, -0.0153),
                distortionKind = "radial",
                distortionRms = 0.9,
            };
            p.calibration = new LensCalibration { imageWidth = 6000, imageHeight = 4000, factor = 1.54 };
            p.calibration.lines.AddRange(Lines(p.distortion, 6000, 4000, 1.54));
            string json = LensProfileJson.Write(p);
            var back = LensProfileJson.Read(json);
            Assert.AreEqual(json, LensProfileJson.Write(back));
            Assert.IsTrue(back.SameSettings(p));
            Assert.AreEqual(p.name, back.name);
            Assert.AreEqual(p.notes, back.notes);
            Assert.Throws<FormatException>(() => LensProfileJson.Read("{\"format\": \"other\"}"));
            Assert.Throws<FormatException>(() => LensProfileJson.Read("not json"));
        }

        private static PixelImage Pattern(int w, int h)
        {
            var img = new PixelImage(w, h, 3, 8);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < 3; c++)
                        img.Data8[(y * w + x) * 3 + c] = (byte)(128 + 60 * Math.Sin(x * 0.13 + c) * Math.Cos(y * 0.09 - c));
            return img;
        }

        [Test]
        public void Process_NoDistortionIsThePlainDesqueeze()
        {
            var img = Pattern(120, 80);
            var plain = new DesqueezeSettings { Factor = 1.5, WidthShare = 1, Widen = WidenMethod.Kernel };
            var a = Desqueezer.Process(img, plain);
            var zero = plain.Clone();
            zero.Distortion = Model(-0.05, -0.05);
            zero.DistortionAmount = 0;
            CollectionAssert.AreEqual(a.Data8, Desqueezer.Process(img, zero).Data8);
        }

        [Test]
        public void Process_OutputSizeAndCrop()
        {
            var img = Pattern(120, 80);
            var s = new DesqueezeSettings { Factor = 1.5, WidthShare = 1, Widen = WidenMethod.Kernel, Distortion = Model(-0.05, -0.03) };
            foreach (var edges in new[] { DistortionEdges.AutoCrop, DistortionEdges.Uncropped })
            {
                s.DistortionEdges = edges;
                int w, h;
                Desqueezer.OutputSize(img, s, out w, out h);
                var r = Desqueezer.Process(img, s);
                Assert.AreEqual(w, r.Width);
                Assert.AreEqual(h, r.Height);
                int midTop = r.Width / 2 * 3;
                if (edges == DistortionEdges.AutoCrop) Assert.Greater(r.Data8[midTop], 20);
                else Assert.AreEqual(0, r.Data8[midTop]);   // barrel correction leaves the middle of the edges uncovered
            }
        }

        [Test]
        public void Process_PortraitIsCorrectedInTheSensorFrame()
        {
            var sideways = Pattern(90, 60);
            var up = Orientation.Apply(sideways, Orientation.Rotate90Clockwise);
            up.Metadata.SourceOrientation = Orientation.Rotate90Clockwise;
            var s = new DesqueezeSettings { Factor = 1.5, WidthShare = 1, Widen = WidenMethod.Kernel, Distortion = Model(-0.05, -0.02) };
            var viaUp = Desqueezer.Process(up, s);
            var viaStored = Orientation.Apply(Desqueezer.Process(sideways, s), Orientation.Rotate90Clockwise);
            CollectionAssert.AreEqual(viaStored.Data8, viaUp.Data8);
        }

        [Test]
        public void Orientation_MapPointFollowsThePixels()
        {
            for (int o = 1; o <= 8; o++)
            {
                var img = new PixelImage(5, 3, 1, 8);
                img.Data8[1 * 5 + 3] = 255;
                var t = Orientation.Apply(img, o);
                double ox, oy;
                Orientation.MapPoint(o, 5, 3, 3, 1, out ox, out oy);
                Assert.AreEqual(255, t.Data8[(int)oy * t.Width + (int)ox], "orientation " + o);
            }
        }
    }
}
