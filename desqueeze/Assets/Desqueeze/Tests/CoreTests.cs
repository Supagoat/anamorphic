using System;
using Desqueeze.Core;
using Desqueeze.Core.Codecs;
using Desqueeze.Core.Imaging;
using NUnit.Framework;
using UnityEngine;

namespace Desqueeze.Tests
{
    /// <summary>EditMode tests for the core library (Window > General > Test Runner).</summary>
    public class CoreTests
    {
        [Test]
        public void Geometry_SplitAlwaysProducesTheAnamorphicAspect()
        {
            foreach (var factor in new[] { 1.33, 1.5, 1.6, 2.0 })
                for (double t = 0; t <= 1.0001; t += 0.1)
                {
                    var g = DesqueezeGeometry.Compute(6000, 4000, factor, t);
                    Assert.AreEqual(factor, g.WidthScale / g.HeightScale, 1e-9);
                    Assert.Less(Math.Abs(g.AspectErrorPercent), 0.05);
                }
        }

        [Test]
        public void Geometry_Endpoints()
        {
            var heightOnly = DesqueezeGeometry.Compute(6000, 4000, 1.33, 0);
            Assert.AreEqual(6000, heightOnly.OutputWidth);
            Assert.AreEqual(3008, heightOnly.OutputHeight);
            var widthOnly = DesqueezeGeometry.Compute(6000, 4000, 1.5, 1);
            Assert.AreEqual(9000, widthOnly.OutputWidth);
            Assert.AreEqual(4000, widthOnly.OutputHeight);
        }

        [Test]
        public void Png_RoundTripsAllLayouts()
        {
            var rnd = new System.Random(3);
            foreach (var ch in new[] { 1, 2, 3, 4 })
                foreach (var depth in new[] { 8, 16 })
                {
                    var img = new PixelImage(31, 17, ch, depth);
                    if (depth == 8) rnd.NextBytes(img.Data8);
                    else for (int i = 0; i < img.Data16.Length; i++) img.Data16[i] = (ushort)rnd.Next(65536);
                    var back = PngCodec.Decode(PngCodec.Encode(img));
                    Assert.AreEqual(ch, back.Channels);
                    Assert.AreEqual(depth, back.BitDepth);
                    if (depth == 8) CollectionAssert.AreEqual(img.Data8, back.Data8);
                    else CollectionAssert.AreEqual(img.Data16, back.Data16);
                }
        }

        [Test]
        public void Resampling_FlatFieldStaysFlat()
        {
            var img = new PixelImage(40, 30, 3, 8);
            for (int i = 0; i < img.Data8.Length; i += 3)
            {
                img.Data8[i] = 200;
                img.Data8[i + 1] = 90;
                img.Data8[i + 2] = 17;
            }
            foreach (ResampleAlgorithm a in Enum.GetValues(typeof(ResampleAlgorithm)))
            {
                var r = Desqueezer.Process(img, new DesqueezeSettings { Algorithm = a, Factor = 1.6, WidthShare = 0.5 });
                for (int i = 0; i < r.Data8.Length; i += 3)
                {
                    Assert.AreEqual(200, r.Data8[i], 1, a.ToString());
                    Assert.AreEqual(90, r.Data8[i + 1], 1, a.ToString());
                    Assert.AreEqual(17, r.Data8[i + 2], 1, a.ToString());
                }
            }
        }

        private static Desqueeze.Core.Resampling.AxisSRModel LoadModel()
        {
            string path = System.IO.Path.Combine(Application.streamingAssetsPath, "desqueeze-axissr.bin");
            Assume.That(System.IO.File.Exists(path), "detail model not present in StreamingAssets");
            return Desqueeze.Core.Resampling.AxisSRModel.Load(path);
        }

        [Test]
        public void Detail_FlatFieldStaysFlat()
        {
            var model = LoadModel();
            var img = new PixelImage(50, 30, 3, 8);
            for (int i = 0; i < img.Data8.Length; i += 3) { img.Data8[i] = 30; img.Data8[i + 1] = 140; img.Data8[i + 2] = 250; }
            var r = Desqueeze.Core.Resampling.AxisSR.UpscaleX(img, 80, model);
            for (int i = 0; i < r.Data8.Length; i++) Assert.AreEqual(img.Data8[i % 3], r.Data8[i], 1);
        }

        [Test]
        public void Detail_VerticalAxisIsTransposedHorizontal()
        {
            var model = LoadModel();
            var img = new PixelImage(33, 21, 3, 8);
            new System.Random(5).NextBytes(img.Data8);
            var h = Desqueeze.Core.Resampling.AxisSR.UpscaleX(Transforms.Transpose(img), 40, model);
            var v = Desqueeze.Core.Resampling.AxisSR.Upscale(img, 40, SqueezeAxis.Vertical, model);
            CollectionAssert.AreEqual(Transforms.Transpose(h).Data8, v.Data8);
        }

        /// <summary>Minimal EXIF block: IFD0 holding one Orientation SHORT, linked to an empty IFD1 (a "thumbnail").</summary>
        private static byte[] Exif(int orientation)
        {
            return new byte[]
            {
                (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0,
                1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0,
                26, 0, 0, 0,
                0, 0, 0, 0, 0, 0,
            };
        }

        [Test]
        public void Orientation_MatchesExifDefinitions()
        {
            // rows "1 2 3" / "4 5 6"
            var img = new PixelImage(3, 2, 1, new byte[] { 1, 2, 3, 4, 5, 6 });
            CollectionAssert.AreEqual(new byte[] { 3, 2, 1, 6, 5, 4 }, Orientation.Apply(img, 2).Data8);
            CollectionAssert.AreEqual(new byte[] { 6, 5, 4, 3, 2, 1 }, Orientation.Apply(img, 3).Data8);
            CollectionAssert.AreEqual(new byte[] { 4, 5, 6, 1, 2, 3 }, Orientation.Apply(img, 4).Data8);
            CollectionAssert.AreEqual(new byte[] { 1, 4, 2, 5, 3, 6 }, Orientation.Apply(img, 5).Data8);
            CollectionAssert.AreEqual(new byte[] { 4, 1, 5, 2, 6, 3 }, Orientation.Apply(img, 6).Data8); // 90° clockwise
            CollectionAssert.AreEqual(new byte[] { 6, 3, 5, 2, 4, 1 }, Orientation.Apply(img, 7).Data8);
            CollectionAssert.AreEqual(new byte[] { 3, 6, 2, 5, 1, 4 }, Orientation.Apply(img, 8).Data8); // 90° counter-clockwise
            for (int o = 1; o <= 8; o++)
                CollectionAssert.AreEqual(img.Data8, Orientation.Apply(Orientation.Apply(img, o), Orientation.Inverse(o)).Data8, "orientation " + o);
        }

        [Test]
        public void Orientation_LoaderTurnsUprightAndResetsTag()
        {
            var stored = new PixelImage(60, 40, 3, 8);
            new System.Random(9).NextBytes(stored.Data8);
            stored.Metadata.Exif = Exif(6);
            Assert.AreEqual(6, ExifPatcher.ReadOrientation(JpegInfo.Read(JpegEncoder.Encode(stored)).Exif));
            var up = ImageIO.MakeUpright(stored);
            Assert.AreEqual(40, up.Width);
            Assert.AreEqual(60, up.Height);
            Assert.AreEqual(6, up.Metadata.SourceOrientation);
            Assert.AreEqual(1, ExifPatcher.ReadOrientation(up.Metadata.Exif));
            var saved = JpegInfo.Read(ImageIO.Encode(up, ImageFileFormat.Jpeg));
            Assert.AreEqual(1, ExifPatcher.ReadOrientation(saved.Exif));
            Assert.AreEqual(0, saved.Exif[22] | saved.Exif[23] | saved.Exif[24] | saved.Exif[25], "thumbnail link removed");
        }

        [Test]
        public void Orientation_AutoDirection()
        {
            Assert.IsTrue(AxisChooser.Choose(SqueezeDirection.Auto, 4000, 6000, 6).IsVertical);
            Assert.IsTrue(AxisChooser.Choose(SqueezeDirection.Auto, 4000, 6000, 8).IsVertical);
            Assert.IsTrue(AxisChooser.Choose(SqueezeDirection.Auto, 4000, 6000, 0).IsVertical);
            Assert.IsFalse(AxisChooser.Choose(SqueezeDirection.Auto, 6000, 4000, 1).IsVertical);
            Assert.IsFalse(AxisChooser.Choose(SqueezeDirection.Auto, 6000, 4000, 0).IsVertical);
            Assert.IsFalse(AxisChooser.Choose(SqueezeDirection.Horizontal, 4000, 6000, 6).IsVertical);
            Assert.IsTrue(AxisChooser.Choose(SqueezeDirection.Vertical, 6000, 4000, 1).IsVertical);
        }

        [Test]
        public void Orientation_TurnedShotDesqueezesLikeTheStoredOne()
        {
            var stored = new PixelImage(90, 60, 3, 8);
            new System.Random(11).NextBytes(stored.Data8);
            stored.Metadata.Exif = Exif(6);
            var up = ImageIO.MakeUpright(stored);
            var s = new DesqueezeSettings { Factor = 1.5, WidthShare = 1, Widen = WidenMethod.Kernel, Algorithm = ResampleAlgorithm.Lanczos3 };
            var viaUpright = Desqueezer.Process(up, s);                      // Auto: camera was turned -> vertical
            Assert.AreEqual(60, viaUpright.Width);
            Assert.AreEqual(135, viaUpright.Height);
            s.Direction = SqueezeDirection.Horizontal;
            var viaStored = Orientation.Apply(Desqueezer.Process(stored, s), 6);
            // The internal turn undoes the camera's turn, so the resampler sees exactly the sideways pixels.
            CollectionAssert.AreEqual(viaStored.Data8, viaUpright.Data8);
        }

        [Test]
        public void Jpeg_EncoderOutputDecodesInUnity()
        {
            var img = new PixelImage(67, 45, 3, 8);
            for (int y = 0; y < 45; y++)
                for (int x = 0; x < 67; x++)
                {
                    int o = (y * 67 + x) * 3;
                    img.Data8[o] = (byte)(x * 255 / 66);
                    img.Data8[o + 1] = (byte)(y * 255 / 44);
                    img.Data8[o + 2] = 128;
                }
            byte[] jpg = JpegEncoder.Encode(img);
            var tex = new Texture2D(2, 2);
            try
            {
                Assert.IsTrue(ImageConversion.LoadImage(tex, jpg, false));
                Assert.AreEqual(67, tex.width);
                Assert.AreEqual(45, tex.height);
                Color32[] px = tex.GetPixels32();
                double maxErr = 0;
                for (int y = 0; y < 45; y++)
                    for (int x = 0; x < 67; x++)
                    {
                        Color32 c = px[(44 - y) * 67 + x]; // Unity rows are bottom-up
                        int o = (y * 67 + x) * 3;
                        maxErr = Math.Max(maxErr, Math.Abs(c.r - img.Data8[o]));
                        maxErr = Math.Max(maxErr, Math.Abs(c.g - img.Data8[o + 1]));
                        maxErr = Math.Max(maxErr, Math.Abs(c.b - img.Data8[o + 2]));
                    }
                Assert.LessOrEqual(maxErr, 3, "Q100 4:4:4 should be near-lossless");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tex);
            }
        }
    }
}
