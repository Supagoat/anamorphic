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
