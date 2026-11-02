// Command-line harness for the Unity-independent core (Desqueeze.Core).
// Used to test codecs and to benchmark resampling quality outside Unity.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Desqueeze.Core;
using Desqueeze.Core.Codecs;
using Desqueeze.Core.Imaging;

namespace DesqueezeBench
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            if (args.Length == 0)
            {
                Console.WriteLine("commands: selftest | resize | batch <manifest.tsv> | desqueeze | png-roundtrip | png-dump | jpeg-encode | jpeg-info | speed");
                return 1;
            }
            try
            {
                switch (args[0])
                {
                    case "selftest": return SelfTest();
                    case "resize": return Resize(args);
                    case "batch": return Batch(args[1]);
                    case "desqueeze": return DesqueezeCmd(args);
                    case "png-roundtrip": return PngRoundTrip(args[1], args[2]);
                    case "png-dump": return PngDump(args[1], args[2]);
                    case "jpeg-encode": return JpegEncode(args);
                    case "jpeg-info": return JpegInfoCmd(args[1]);
                    case "speed": return Speed(args);
                    case "axissr": return AxisSRCmd(args);
                    default:
                        Console.WriteLine("unknown command " + args[0]);
                        return 1;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("EXCEPTION: " + e);
                return 2;
            }
        }

        private static DesqueezeSettings ParseSettings(string algo, string reduce, string enlarge, string ar)
        {
            return new DesqueezeSettings
            {
                Algorithm = (ResampleAlgorithm)Enum.Parse(typeof(ResampleAlgorithm), algo),
                ReduceLight = (LightSpace)Enum.Parse(typeof(LightSpace), reduce),
                EnlargeLight = (LightSpace)Enum.Parse(typeof(LightSpace), enlarge),
                AntiRinging = float.Parse(ar, CultureInfo.InvariantCulture),
            };
        }

        private static int Resize(string[] a)
        {
            var img = PngCodec.Decode(File.ReadAllBytes(a[1]));
            var s = ParseSettings(a[5], a[6], a[7], a[8]);
            var r = Desqueezer.Resize(img, int.Parse(a[3]), int.Parse(a[4]), s);
            File.WriteAllBytes(a[2], PngCodec.Encode(r));
            return 0;
        }

        private static int Batch(string manifest)
        {
            var cache = new Dictionary<string, PixelImage>();
            int n = 0;
            var sw = Stopwatch.StartNew();
            foreach (var line in File.ReadAllLines(manifest))
            {
                if (line.Trim().Length == 0 || line.StartsWith("#")) continue;
                var f = line.Split('\t');
                if (!cache.TryGetValue(f[0], out var img))
                {
                    img = PngCodec.Decode(File.ReadAllBytes(f[0]));
                    cache[f[0]] = img;
                }
                var s = ParseSettings(f[4], f[5], f[6], f[7]);
                var r = Desqueezer.Resize(img, int.Parse(f[2]), int.Parse(f[3]), s);
                File.WriteAllBytes(f[1], PngCodec.Encode(r, new PngEncodeOptions { Compression = System.IO.Compression.CompressionLevel.Fastest }));
                n++;
            }
            Console.WriteLine("batch: " + n + " jobs in " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");
            return 0;
        }

        private static int DesqueezeCmd(string[] a)
        {
            // desqueeze in out factor share algo linear sigmoid ar
            var bytes = File.ReadAllBytes(a[1]);
            var img = PngCodec.Decode(bytes);
            var s = ParseSettings(a[5], a[6], a[7], a[8]);
            s.Factor = double.Parse(a[3]);
            s.WidthShare = double.Parse(a[4]);
            var p = new ProgressTracker();
            var sw = Stopwatch.StartNew();
            var r = Desqueezer.Process(img, s, p);
            Console.WriteLine("geometry " + DesqueezeGeometry.Compute(img.Width, img.Height, s.Factor, s.WidthShare) +
                              " in " + sw.ElapsedMilliseconds + " ms, progress " + p.Value);
            var fmt = ImageIO.FormatFromExtension(a[2]);
            File.WriteAllBytes(a[2], ImageIO.Encode(r, fmt));
            return 0;
        }

        private static int PngRoundTrip(string input, string output)
        {
            var img = PngCodec.Decode(File.ReadAllBytes(input));
            File.WriteAllBytes(output, PngCodec.Encode(img));
            Console.WriteLine(img.Describe() + " icc=" + (img.Metadata.IccProfile?.Length ?? 0) +
                              " exif=" + (img.Metadata.Exif?.Length ?? 0) + " chunks=" + img.Metadata.PngChunks.Count);
            return 0;
        }

        private static int PngDump(string input, string output)
        {
            var img = PngCodec.Decode(File.ReadAllBytes(input));
            using (var fs = File.Create(output))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(img.Width);
                bw.Write(img.Height);
                bw.Write(img.Channels);
                bw.Write(img.BitDepth);
                if (img.BitDepth == 8) bw.Write(img.Data8);
                else foreach (var v in img.Data16) bw.Write(v);
            }
            return 0;
        }

        private static int JpegEncode(string[] a)
        {
            var img = PngCodec.Decode(File.ReadAllBytes(a[1]));
            int q = a.Length > 3 ? int.Parse(a[3]) : 100;
            var sw = Stopwatch.StartNew();
            var bytes = JpegEncoder.Encode(img, new JpegEncodeOptions { Quality = q });
            Console.WriteLine("jpeg " + img.Describe() + " -> " + bytes.Length + " bytes in " + sw.ElapsedMilliseconds + " ms");
            File.WriteAllBytes(a[2], bytes);
            return 0;
        }

        private static int JpegInfoCmd(string path)
        {
            var info = JpegInfo.Read(File.ReadAllBytes(path));
            Console.WriteLine(info.Width + "x" + info.Height + " comps=" + info.Components + " prog=" + info.Progressive +
                              " exif=" + (info.Exif?.Length ?? 0) + " icc=" + (info.IccProfile?.Length ?? 0));
            return 0;
        }

        // axissr <in.png> <out.png> <outWidth> <model.bin> [net 0/1] [flatSkip] [axis X/Y]
        private static int AxisSRCmd(string[] a)
        {
            var img = PngCodec.Decode(File.ReadAllBytes(a[1]));
            var model = Desqueeze.Core.Resampling.AxisSRModel.Load(a[4]);
            var opts = new Desqueeze.Core.Resampling.AxisSROptions
            {
                UseNetwork = a.Length <= 5 || a[5] == "1",
                FlatSkip = a.Length > 6 ? float.Parse(a[6], CultureInfo.InvariantCulture) : 0f,
            };
            var axis = a.Length > 7 && a[7] == "Y" ? SqueezeAxis.Vertical : SqueezeAxis.Horizontal;
            var sw = Stopwatch.StartNew();
            var r = Desqueeze.Core.Resampling.AxisSR.Upscale(img, int.Parse(a[3]), axis, model, opts);
            Console.WriteLine("axissr " + img.Width + "x" + img.Height + " -> " + r.Width + "x" + r.Height + " in " + sw.ElapsedMilliseconds +
                              " ms (model " + model.Name + ", net " + opts.UseNetwork + ", cores " + Environment.ProcessorCount + ")");
            File.WriteAllBytes(a[2], PngCodec.Encode(r, new PngEncodeOptions { Compression = System.IO.Compression.CompressionLevel.Fastest }));
            return 0;
        }

        private static int Speed(string[] a)
        {
            // speed in.png factor share algo [repeats]
            var img = PngCodec.Decode(File.ReadAllBytes(a[1]));
            var s = new DesqueezeSettings
            {
                Factor = double.Parse(a[2]),
                WidthShare = double.Parse(a[3]),
                Algorithm = (ResampleAlgorithm)Enum.Parse(typeof(ResampleAlgorithm), a[4]),
            };
            int reps = a.Length > 5 ? int.Parse(a[5]) : 3;
            Desqueezer.Process(img, s); // warm-up
            var sw = Stopwatch.StartNew();
            PixelImage r = null;
            for (int i = 0; i < reps; i++) r = Desqueezer.Process(img, s);
            double ms = sw.Elapsed.TotalMilliseconds / reps;
            sw.Restart();
            var png = PngCodec.Encode(r);
            double pngMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var jpg = JpegEncoder.Encode(r);
            double jpgMs = sw.Elapsed.TotalMilliseconds;
            Console.WriteLine(s.Algorithm + ": " + img.Width + "x" + img.Height + " -> " + r.Width + "x" + r.Height +
                              " resample " + ms.ToString("0") + " ms, png " + pngMs.ToString("0") + " ms (" + png.Length / 1024 + " KB), jpeg " +
                              jpgMs.ToString("0") + " ms (" + jpg.Length / 1024 + " KB), cores " + Environment.ProcessorCount);
            return 0;
        }

        // -------------------------------------------------------------------------------------------
        private static int s_fail;

        private static void Check(bool ok, string what)
        {
            Console.WriteLine((ok ? "  pass  " : "  FAIL  ") + what);
            if (!ok) s_fail++;
        }

        private static int SelfTest()
        {
            Console.WriteLine("Geometry");
            var g = DesqueezeGeometry.Compute(6000, 4000, 1.33, 0.0);
            Check(g.OutputWidth == 6000 && g.OutputHeight == 3008, "1.33x, share 0 -> width x1, height /1.33: " + g);
            g = DesqueezeGeometry.Compute(6000, 4000, 1.5, 1.0);
            Check(g.OutputWidth == 9000 && g.OutputHeight == 4000, "1.5x, share 1 -> width x1.5: " + g);
            g = DesqueezeGeometry.Compute(6000, 4000, 1.5, 0.5);
            Check(Math.Abs(g.PixelCountRatio - 1) < 0.001, "share 0.5 keeps pixel count: " + g.PixelCountRatio.ToString("0.0000"));
            bool aspectOk = true;
            for (double t = 0; t <= 1.0001; t += 0.05)
            {
                var gg = DesqueezeGeometry.Compute(6000, 4000, 1.6, t);
                if (Math.Abs(gg.AspectErrorPercent) > 0.05) aspectOk = false;
                if (Math.Abs(gg.WidthScale / gg.HeightScale - 1.6) > 1e-9) aspectOk = false;
            }
            Check(aspectOk, "aspect locked to factor across the slider (error < 0.05%)");
            Check(Math.Abs(DesqueezeGeometry.WidthShareForWidthScale(1.5, DesqueezeGeometry.WidthScaleFor(1.5, 0.37)) - 0.37) < 1e-12,
                "slider position <-> width scale round trip");

            Console.WriteLine("Resampling invariants");
            var rnd = new Random(1);
            foreach (var ch in new[] { 1, 2, 3, 4 })
            {
                foreach (var depth in new[] { 8, 16 })
                {
                    var img = new PixelImage(37, 23, ch, depth);
                    if (depth == 8) rnd.NextBytes(img.Data8);
                    else for (int i = 0; i < img.Data16.Length; i++) img.Data16[i] = (ushort)rnd.Next(65536);
                    foreach (ResampleAlgorithm alg in Enum.GetValues(typeof(ResampleAlgorithm)))
                    {
                        if (alg == ResampleAlgorithm.EwaLanczosSharp) continue; // EWA is not interpolating at 1:1
                        var s = new DesqueezeSettings { Algorithm = alg, AntiRinging = 0.5f };
                        // force a pass even at 1:1 by resizing to same size through Resize (identity shortcut) and through Process
                        var r = Desqueezer.Resize(img, 37, 23, s);
                        bool same = depth == 8 ? Equal(r.Data8, img.Data8) : Equal(r.Data16, img.Data16);
                        if (!same) Check(false, "identity size is lossless for " + alg + " ch=" + ch + " depth=" + depth);
                    }
                }
            }
            Check(true, "identity size is lossless (all kernels, 1-4 channels, 8/16-bit)");

            // Width-only de-squeeze must leave every row's content untouched vertically:
            // an image whose rows are each a single random colour must come back with the same rows.
            {
                var img = new PixelImage(40, 30, 3, 8);
                for (int y = 0; y < 30; y++)
                {
                    byte r0 = (byte)rnd.Next(256), g0 = (byte)rnd.Next(256), b0 = (byte)rnd.Next(256);
                    for (int x = 0; x < 40; x++)
                    {
                        int o = (y * 40 + x) * 3;
                        img.Data8[o] = r0; img.Data8[o + 1] = g0; img.Data8[o + 2] = b0;
                    }
                }
                bool rowsOk = true;
                foreach (ResampleAlgorithm alg in Enum.GetValues(typeof(ResampleAlgorithm)))
                {
                    if (alg == ResampleAlgorithm.EwaLanczosSharp) continue; // EWA filters both axes by design
                    var r = Desqueezer.Process(img, new DesqueezeSettings { Algorithm = alg, Factor = 1.5, WidthShare = 1.0 });
                    for (int y = 0; y < 30 && rowsOk; y++)
                        for (int x = 0; x < r.Width; x++)
                            for (int c = 0; c < 3; c++)
                                if (r.Data8[(y * r.Width + x) * 3 + c] != img.Data8[y * 40 * 3 + c])
                                {
                                    rowsOk = false;
                                    Console.WriteLine("    row changed with " + alg);
                                    x = r.Width; c = 3;
                                }
                }
                Check(rowsOk, "width-only de-squeeze (share 1) leaves rows bit-exact for separable kernels");
            }
            {
                var flat = new PixelImage(50, 40, 4, 16);
                for (int i = 0; i < flat.Data16.Length; i += 4)
                {
                    flat.Data16[i] = 12345; flat.Data16[i + 1] = 54321; flat.Data16[i + 2] = 777; flat.Data16[i + 3] = 40000;
                }
                bool flatOk = true;
                foreach (ResampleAlgorithm alg in Enum.GetValues(typeof(ResampleAlgorithm)))
                {
                    foreach (var share in new[] { 0.0, 0.5, 1.0 })
                    foreach (LightSpace red in Enum.GetValues(typeof(LightSpace)))
                    foreach (LightSpace enl in Enum.GetValues(typeof(LightSpace)))
                    {
                        var s = new DesqueezeSettings { Algorithm = alg, Factor = 1.6, WidthShare = share, AntiRinging = 0.3f, ReduceLight = red, EnlargeLight = enl };
                        var r = Desqueezer.Process(flat, s);
                        for (int i = 0; i < r.Data16.Length; i += 4)
                        {
                            if (Math.Abs(r.Data16[i] - 12345) > 1 || Math.Abs(r.Data16[i + 1] - 54321) > 1 ||
                                Math.Abs(r.Data16[i + 2] - 777) > 1 || Math.Abs(r.Data16[i + 3] - 40000) > 1)
                            {
                                flatOk = false;
                                Console.WriteLine("    flat field changed: " + alg + " share " + share + " -> " +
                                                  r.Data16[i] + "," + r.Data16[i + 1] + "," + r.Data16[i + 2] + "," + r.Data16[i + 3]);
                                break;
                            }
                        }
                    }
                }
                Check(flatOk, "flat RGBA16 field stays flat for every algorithm, split and light-space pairing (within 1/65535)");
                // and opaque RGB16 through the sigmoidal paths
                var flat3 = new PixelImage(50, 40, 3, 16);
                for (int i = 0; i < flat3.Data16.Length; i += 3) { flat3.Data16[i] = 1000; flat3.Data16[i + 1] = 30000; flat3.Data16[i + 2] = 64000; }
                bool flat3Ok = true;
                foreach (ResampleAlgorithm alg in Enum.GetValues(typeof(ResampleAlgorithm)))
                    foreach (var share in new[] { 0.0, 0.5, 1.0 })
                        foreach (LightSpace red in Enum.GetValues(typeof(LightSpace)))
                            foreach (LightSpace enl in Enum.GetValues(typeof(LightSpace)))
                            {
                                var r = Desqueezer.Process(flat3, new DesqueezeSettings { Algorithm = alg, Factor = 1.33, WidthShare = share, ReduceLight = red, EnlargeLight = enl });
                                for (int i = 0; i < r.Data16.Length; i += 3)
                                    if (Math.Abs(r.Data16[i] - 1000) > 2 || Math.Abs(r.Data16[i + 1] - 30000) > 2 || Math.Abs(r.Data16[i + 2] - 64000) > 2) { flat3Ok = false; Console.WriteLine("    RGB16 flat changed: " + alg + " " + share + " " + red + "/" + enl + " -> " + r.Data16[i] + "," + r.Data16[i + 1] + "," + r.Data16[i + 2]); break; }
                            }
                Check(flat3Ok, "flat RGB16 field stays flat through every light-space pairing (within 2/65535)");
            }

            // Fully transparent pixels must not bleed colour.
            {
                var img = new PixelImage(20, 10, 4, 8);
                for (int y = 0; y < 10; y++)
                    for (int x = 0; x < 20; x++)
                    {
                        int o = (y * 20 + x) * 4;
                        if (x < 10) { img.Data8[o] = 255; img.Data8[o + 1] = 0; img.Data8[o + 2] = 0; img.Data8[o + 3] = 255; }
                        else { img.Data8[o] = 0; img.Data8[o + 1] = 255; img.Data8[o + 2] = 0; img.Data8[o + 3] = 0; }
                    }
                var r = Desqueezer.Process(img, new DesqueezeSettings { Factor = 1.5, WidthShare = 1, Algorithm = ResampleAlgorithm.Lanczos3 });
                bool noGreen = true;
                for (int i = 0; i < r.Data8.Length; i += 4)
                    if (r.Data8[i + 3] > 0 && r.Data8[i + 1] > 2) noGreen = false;
                Check(noGreen, "premultiplied alpha: colour of transparent pixels does not bleed");
            }

            Console.WriteLine("Codecs");
            foreach (var ch in new[] { 1, 2, 3, 4 })
                foreach (var depth in new[] { 8, 16 })
                {
                    var img = new PixelImage(53, 31, ch, depth);
                    if (depth == 8) rnd.NextBytes(img.Data8);
                    else for (int i = 0; i < img.Data16.Length; i++) img.Data16[i] = (ushort)rnd.Next(65536);
                    img.Metadata.Exif = new byte[] { (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, 0, 0 };
                    img.Metadata.IccProfile = new byte[300];
                    rnd.NextBytes(img.Metadata.IccProfile);
                    img.Metadata.PngChunks.Add(new PngChunk("tEXt", System.Text.Encoding.ASCII.GetBytes("Comment\0hello")));
                    var back = PngCodec.Decode(PngCodec.Encode(img));
                    bool same = back.Channels == ch && back.BitDepth == depth &&
                                (depth == 8 ? Equal(back.Data8, img.Data8) : Equal(back.Data16, img.Data16)) &&
                                Equal(back.Metadata.IccProfile, img.Metadata.IccProfile) &&
                                Equal(back.Metadata.Exif, img.Metadata.Exif) && back.Metadata.PngChunks.Count == 1;
                    Check(same, "PNG round trip ch=" + ch + " depth=" + depth + " (pixels + ICC + EXIF + text)");
                }

            {
                // EXIF patch: little-endian TIFF with IFD0 -> ExifIFD containing PixelX/YDimension (LONG and SHORT).
                var t = new List<byte>();
                t.AddRange(new byte[] { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0 });
                t.AddRange(new byte[] { 1, 0 }); // 1 entry
                t.AddRange(new byte[] { 0x69, 0x87, 4, 0, 1, 0, 0, 0, 26, 0, 0, 0 }); // ExifIFD at 26
                t.AddRange(new byte[] { 0, 0, 0, 0 }); // next IFD
                t.AddRange(new byte[] { 2, 0 });
                t.AddRange(new byte[] { 0x02, 0xA0, 4, 0, 1, 0, 0, 0, 0x10, 0x27, 0, 0 }); // PixelX LONG 10000
                t.AddRange(new byte[] { 0x03, 0xA0, 3, 0, 1, 0, 0, 0, 0x88, 0x13, 0, 0 }); // PixelY SHORT 5000
                t.AddRange(new byte[] { 0, 0, 0, 0 });
                var p = ExifPatcher.WithDimensions(t.ToArray(), 9000, 4000);
                int px = p[26 + 2 + 8] | (p[26 + 2 + 9] << 8) | (p[26 + 2 + 10] << 16);
                int py = p[26 + 2 + 12 + 8] | (p[26 + 2 + 12 + 9] << 8);
                Check(px == 9000 && py == 4000, "EXIF PixelXDimension/PixelYDimension patched (" + px + " x " + py + ")");
            }

            string modelPath = Path.Combine("Assets", "StreamingAssets", "desqueeze-axissr.bin");
            if (File.Exists(modelPath)) AxisSRTests(modelPath);
            else Console.WriteLine("(skipping cross-axis detail tests: run from the project root so " + modelPath + " is found)");

            Console.WriteLine(s_fail == 0 ? "ALL PASSED" : s_fail + " FAILED");
            return s_fail == 0 ? 0 : 1;
        }

        private static void AxisSRTests(string modelPath)
        {
            Console.WriteLine("Cross-axis detail reconstruction");
            var model = Desqueeze.Core.Resampling.AxisSRModel.Load(modelPath);
            Check(model.HasNetwork, "model loads (" + model.Name + ")");
            var rnd = new Random(7);

            // flat fields stay flat, 8 and 16 bit, with and without the network
            bool flat = true;
            foreach (var depth in new[] { 8, 16 })
                foreach (var net in new[] { false, true })
                {
                    var img = new PixelImage(60, 40, 3, depth);
                    for (int i = 0; i < img.Data8?.Length; i += 3) { img.Data8[i] = 200; img.Data8[i + 1] = 90; img.Data8[i + 2] = 17; }
                    if (depth == 16) for (int i = 0; i < img.Data16.Length; i += 3) { img.Data16[i] = 51400; img.Data16[i + 1] = 23130; img.Data16[i + 2] = 4369; }
                    var r = Desqueeze.Core.Resampling.AxisSR.UpscaleX(img, 93, model, new Desqueeze.Core.Resampling.AxisSROptions { UseNetwork = net });
                    for (int i = 0; i < r.Width * r.Height * 3 && flat; i++)
                    {
                        int v = depth == 8 ? r.Data8[i] : r.Data16[i];
                        int want = depth == 8 ? img.Data8[i % 3] : img.Data16[i % 3];
                        if (Math.Abs(v - want) > (depth == 8 ? 1 : 2)) { flat = false; Console.WriteLine("    flat changed: depth " + depth + " net " + net + " " + v + " vs " + want); }
                    }
                }
            Check(flat, "flat RGB field stays flat (8/16-bit, filters and network)");

            // vertical axis == transposed horizontal
            var tex = new PixelImage(45, 37, 3, 8);
            rnd.NextBytes(tex.Data8);
            var opts = new Desqueeze.Core.Resampling.AxisSROptions();
            var hx = Desqueeze.Core.Resampling.AxisSR.UpscaleX(Transforms.Transpose(tex), 61, model, opts);
            var vy = Desqueeze.Core.Resampling.AxisSR.Upscale(tex, 61, SqueezeAxis.Vertical, model, opts);
            Check(vy.Width == 45 && vy.Height == 61 && Equal(Transforms.Transpose(hx).Data8, vy.Data8), "vertical squeeze = transposed horizontal (45x37 -> 45x61)");

            // Desqueezer routing and geometry
            var prev = Desqueeze.Core.Resampling.AxisSRModel.Default;
            Desqueeze.Core.Resampling.AxisSRModel.Default = model;
            try
            {
                var src = new PixelImage(120, 80, 3, 8);
                rnd.NextBytes(src.Data8);
                foreach (var share in new[] { 0.0, 0.5, 1.0 })
                    foreach (var axis in new[] { SqueezeAxis.Horizontal, SqueezeAxis.Vertical })
                    {
                        var s = new DesqueezeSettings { Factor = 1.5, WidthShare = share, Widen = WidenMethod.Detail, Axis = axis };
                        var g = DesqueezeGeometry.Compute(120, 80, 1.5, share, axis);
                        var r = Desqueezer.Process(src, s);
                        bool learned = Desqueezer.UsesLearned(120, 80, g.OutputWidth, g.OutputHeight, s);
                        Check(r.Width == g.OutputWidth && r.Height == g.OutputHeight && learned == (share > 0),
                            "Process " + axis + " share " + share + " -> " + r.Width + "x" + r.Height + (learned ? " (learned)" : " (kernel)"));
                    }
                Desqueeze.Core.Resampling.AxisSRModel.Default = null;
                var k = Desqueezer.Process(src, new DesqueezeSettings { Factor = 1.5, WidthShare = 1, Widen = WidenMethod.Detail });
                Check(k.Width == 180 && k.Height == 80, "no model loaded -> kernel fallback");
            }
            finally
            {
                Desqueeze.Core.Resampling.AxisSRModel.Default = prev;
            }
        }

        private static bool Equal(byte[] a, byte[] b)
        {
            if (a == null || b == null) return a == b;
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static bool Equal(ushort[] a, ushort[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
