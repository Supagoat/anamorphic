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
                    case "axissr-list": return AxisSRList(args);
                    case "upright": return UprightCmd(args);
                    case "exif-patch": return ExifPatchCmd(args);
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
            // desqueeze in out factor share algo reduce enlarge ar [Auto|Horizontal|Vertical]
            // Like the app: the PNG is turned upright from its eXIf orientation, then the direction decides the axis.
            var bytes = File.ReadAllBytes(a[1]);
            var img = ImageIO.MakeUpright(PngCodec.Decode(bytes));
            var s = ParseSettings(a[5], a[6], a[7], a[8]);
            s.Factor = double.Parse(a[3]);
            s.WidthShare = double.Parse(a[4]);
            if (a.Length > 9) s.Direction = (SqueezeDirection)Enum.Parse(typeof(SqueezeDirection), a[9], true);
            var p = new ProgressTracker();
            var sw = Stopwatch.StartNew();
            var r = Desqueezer.Process(img, s, p);
            Console.WriteLine("geometry " + Desqueezer.Geometry(img, s) + ", " + AxisChooser.Choose(s.Direction, img) +
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

        // axissr-list <list.txt> <model.bin>: each line "in.png out.png outLength X|Y none|cw|ccw".
        // With cw/ccw the image is turned a quarter turn first, enlarged along X, and turned back
        // (the alternative to transposing for a vertical squeeze).
        private static int AxisSRList(string[] a)
        {
            var model = Desqueeze.Core.Resampling.AxisSRModel.Load(a[2]);
            var opts = new Desqueeze.Core.Resampling.AxisSROptions();
            int n = 0;
            var sw = Stopwatch.StartNew();
            foreach (var line in File.ReadAllLines(a[1]))
            {
                var f = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 5) continue;
                var img = PngCodec.Decode(File.ReadAllBytes(f[0]));
                int len = int.Parse(f[2]);
                var axis = f[3] == "Y" ? SqueezeAxis.Vertical : SqueezeAxis.Horizontal;
                int rot = f[4] == "cw" ? Orientation.Rotate90Clockwise : f[4] == "ccw" ? Orientation.Rotate90CounterClockwise : 0;
                PixelImage r;
                if (rot == 0)
                {
                    r = Desqueeze.Core.Resampling.AxisSR.Upscale(img, len, axis, model, opts);
                }
                else
                {
                    var turned = Orientation.Apply(img, rot);
                    var up = Desqueeze.Core.Resampling.AxisSR.Upscale(turned, len, SqueezeAxis.Horizontal, model, opts);
                    r = Orientation.Apply(up, Orientation.Inverse(rot));
                }
                File.WriteAllBytes(f[1], PngCodec.Encode(r, new PngEncodeOptions { Compression = System.IO.Compression.CompressionLevel.Fastest }));
                n++;
            }
            Console.WriteLine("axissr-list: " + n + " images in " + sw.Elapsed.TotalSeconds.ToString("0.0") + " s");
            return 0;
        }

        // upright <in.png> <out.png>: turn a PNG upright from its eXIf orientation (as the app's loader does).
        private static int UprightCmd(string[] a)
        {
            var img = ImageIO.MakeUpright(PngCodec.Decode(File.ReadAllBytes(a[1])));
            File.WriteAllBytes(a[2], PngCodec.Encode(img));
            Console.WriteLine(Path.GetFileName(a[1]) + " orientation " + img.Metadata.SourceOrientation + " -> " + img.Width + "x" + img.Height +
                              ", tag now " + ExifPatcher.ReadOrientation(img.Metadata.Exif));
            return 0;
        }

        // exif-patch <in.jpg> <outPrefix>: orientation of a JPEG, plus its EXIF as read, with orientation 1, and as saved.
        private static int ExifPatchCmd(string[] a)
        {
            var info = JpegInfo.Read(File.ReadAllBytes(a[1]));
            if (info.Exif == null)
            {
                Console.WriteLine("no-exif");
                return 0;
            }
            File.WriteAllBytes(a[2] + ".orig.tif", info.Exif);
            File.WriteAllBytes(a[2] + ".o1.tif", ExifPatcher.WithOrientation(info.Exif, 1));
            File.WriteAllBytes(a[2] + ".saved.tif",
                ExifPatcher.WithoutThumbnail(ExifPatcher.WithDimensions(ExifPatcher.WithOrientation(info.Exif, 1), 1234, 567)));
            Console.WriteLine("orientation " + ExifPatcher.ReadOrientation(info.Exif));
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

            OrientationTests(rnd);

            string modelPath = Path.Combine("Assets", "StreamingAssets", "desqueeze-axissr.bin");
            if (File.Exists(modelPath)) AxisSRTests(modelPath);
            else Console.WriteLine("(skipping cross-axis detail tests: run from the project root so " + modelPath + " is found)");

            Console.WriteLine(s_fail == 0 ? "ALL PASSED" : s_fail + " FAILED");
            return s_fail == 0 ? 0 : 1;
        }

        /// <summary>Minimal EXIF (TIFF) block: IFD0 with an Orientation SHORT, optionally linked to an empty IFD1.</summary>
        internal static byte[] TestExif(bool littleEndian, int orientation, bool withThumbnailIfd)
        {
            if (littleEndian)
                return new byte[]
                {
                    (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0,
                    1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0,
                    (byte)(withThumbnailIfd ? 26 : 0), 0, 0, 0,
                    0, 0, 0, 0, 0, 0,
                };
            return new byte[]
            {
                (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8,
                0, 1, 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, (byte)orientation, 0, 0,
                0, 0, 0, (byte)(withThumbnailIfd ? 26 : 0),
                0, 0, 0, 0, 0, 0,
            };
        }

        private static void OrientationTests(Random rnd)
        {
            Console.WriteLine("Camera orientation");

            // A 3x2 image with distinct pixels: rows "a b c" / "d e f".
            var abc = new PixelImage(3, 2, 1, new byte[] { 1, 2, 3, 4, 5, 6 });
            var cw = Orientation.Apply(abc, Orientation.Rotate90Clockwise);
            Check(cw.Width == 2 && cw.Height == 3 && Equal(cw.Data8, new byte[] { 4, 1, 5, 2, 6, 3 }),
                "orientation 6 turns the stored pixels 90° clockwise (d a / e b / f c)");
            var ccw = Orientation.Apply(abc, Orientation.Rotate90CounterClockwise);
            Check(ccw.Width == 2 && ccw.Height == 3 && Equal(ccw.Data8, new byte[] { 3, 6, 2, 5, 1, 4 }),
                "orientation 8 turns them 90° counter-clockwise (c f / b e / a d)");
            var expected = new Dictionary<int, byte[]>
            {
                { 2, new byte[] { 3, 2, 1, 6, 5, 4 } },
                { 3, new byte[] { 6, 5, 4, 3, 2, 1 } },
                { 4, new byte[] { 4, 5, 6, 1, 2, 3 } },
                { 5, new byte[] { 1, 4, 2, 5, 3, 6 } },
                { 7, new byte[] { 6, 3, 5, 2, 4, 1 } },
            };
            bool known = true;
            foreach (var kv in expected)
                if (!Equal(Orientation.Apply(abc, kv.Key).Data8, kv.Value)) { known = false; Console.WriteLine("    orientation " + kv.Key + " wrong"); }
            Check(known, "orientations 2, 3, 4, 5, 7 match the EXIF definitions on a 3x2 image");

            bool inverse = true;
            foreach (var ch in new[] { 1, 2, 3, 4 })
                foreach (var depth in new[] { 8, 16 })
                {
                    var img = new PixelImage(17, 11, ch, depth);
                    if (depth == 8) rnd.NextBytes(img.Data8);
                    else for (int i = 0; i < img.Data16.Length; i++) img.Data16[i] = (ushort)rnd.Next(65536);
                    for (int o = 1; o <= 8; o++)
                    {
                        var t = Orientation.Apply(img, o);
                        bool dims = Orientation.SwapsAxes(o) ? t.Width == 11 && t.Height == 17 : t.Width == 17 && t.Height == 11;
                        var back = Orientation.Apply(t, Orientation.Inverse(o));
                        bool same = depth == 8 ? Equal(back.Data8, img.Data8) : Equal(back.Data16, img.Data16);
                        if (!dims || !same) { inverse = false; Console.WriteLine("    round trip failed: o=" + o + " ch=" + ch + " depth=" + depth); }
                    }
                }
            Check(inverse, "every orientation is undone by its inverse (1-4 channels, 8/16-bit)");

            bool exifOk = true;
            foreach (var le in new[] { true, false })
                for (int o = 1; o <= 8; o++)
                {
                    var e = TestExif(le, o, true);
                    if (ExifPatcher.ReadOrientation(e) != o) exifOk = false;
                    if (ExifPatcher.ReadOrientation(ExifPatcher.WithOrientation(e, 1)) != 1) exifOk = false;
                    var nt = ExifPatcher.WithoutThumbnail(e);
                    if (nt[22] != 0 || nt[23] != 0 || nt[24] != 0 || nt[25] != 0 || ExifPatcher.ReadOrientation(nt) != o) exifOk = false;
                }
            Check(exifOk, "EXIF orientation read and reset, thumbnail link removed (Intel and Motorola byte order)");
            Check(ExifPatcher.ReadOrientation(null) == 0 && ExifPatcher.ReadOrientation(new byte[] { 1, 2, 3 }) == 0 &&
                  ExifPatcher.ReadOrientation(new byte[] { (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, 0, 0, 0, 0, 0, 0 }) == 0,
                "missing or malformed EXIF reads as 'no orientation tag'");

            // The camera wrote orientation 6 into a JPEG; the app's loader turns it upright and resets the tag.
            {
                var stored = new PixelImage(60, 40, 3, 8);
                rnd.NextBytes(stored.Data8);
                stored.Metadata.Exif = TestExif(false, 6, true);
                var jpg = JpegEncoder.Encode(stored);
                var info = JpegInfo.Read(jpg);
                Check(ExifPatcher.ReadOrientation(info.Exif) == 6, "orientation tag survives a JPEG written and read back");
                var up = ImageIO.MakeUpright(stored);
                Check(up.Width == 40 && up.Height == 60 && up.Metadata.SourceOrientation == 6 &&
                      ExifPatcher.ReadOrientation(up.Metadata.Exif) == 1 && ExifPatcher.ReadOrientation(stored.Metadata.Exif) == 6,
                    "MakeUpright: 60x40 tagged 6 -> 40x60 upright, tag reset to 1, original remembered, source untouched");
                var turnedByDecoder = Orientation.Apply(stored, 6);      // pixels upright, tag still 6
                turnedByDecoder.Metadata.Exif = TestExif(true, 6, false);
                var already = ImageIO.MakeUpright(turnedByDecoder, true);
                Check(already == turnedByDecoder && already.Width == 40 && already.Height == 60 && already.Metadata.SourceOrientation == 6 && ExifPatcher.ReadOrientation(already.Metadata.Exif) == 1,
                    "MakeUpright with a decoder that already turned the pixels: only the tag is reset");
                var plain = new PixelImage(60, 40, 3, 8);
                Check(ImageIO.MakeUpright(plain) == plain && plain.Metadata.SourceOrientation == 0, "no EXIF: image unchanged, orientation 0");
                var outJpg = ImageIO.Encode(up, ImageFileFormat.Jpeg);
                var outInfo = JpegInfo.Read(outJpg);
                bool linkGone = outInfo.Exif != null && outInfo.Exif.Length >= 26 && outInfo.Exif[22] == 0 && outInfo.Exif[25] == 0;
                Check(outInfo.Width == 40 && outInfo.Height == 60 && ExifPatcher.ReadOrientation(outInfo.Exif) == 1 && linkGone,
                    "saved JPEG is upright with orientation 1 and no stale thumbnail link");

                // PNG eXIf and an XMP packet with tiff:Orientation
                var p = new PixelImage(30, 20, 3, 16);
                for (int i = 0; i < p.Data16.Length; i++) p.Data16[i] = (ushort)rnd.Next(65536);
                p.Metadata.Exif = TestExif(true, 8, false);
                var xmp = System.Text.Encoding.ASCII.GetBytes("XML:com.adobe.xmp\0\0\0\0\0<x:xmpmeta><rdf:Description tiff:Orientation=\"8\"/></x:xmpmeta>");
                p.Metadata.PngChunks.Add(new PngChunk("iTXt", xmp));
                var pu = ImageIO.MakeUpright(PngCodec.Decode(PngCodec.Encode(p)));
                string x = "";
                foreach (var c in pu.Metadata.PngChunks)
                    if (c.Type == "iTXt") x = System.Text.Encoding.ASCII.GetString(c.Data);
                Check(pu.Width == 20 && pu.Height == 30 && pu.Metadata.SourceOrientation == 8 && x.Contains("tiff:Orientation=\"1\""),
                    "PNG with eXIf orientation 8: turned upright, XMP orientation reset too");
            }

            // Auto direction
            Check(AxisChooser.Choose(SqueezeDirection.Auto, 4000, 6000, 6).IsVertical &&
                  AxisChooser.Choose(SqueezeDirection.Auto, 4000, 6000, 8).IsVertical &&
                  !AxisChooser.Choose(SqueezeDirection.Auto, 6000, 4000, 1).IsVertical &&
                  !AxisChooser.Choose(SqueezeDirection.Auto, 6000, 4000, 3).IsVertical &&
                  !AxisChooser.Choose(SqueezeDirection.Auto, 6000, 4000, 0).IsVertical &&
                  AxisChooser.Choose(SqueezeDirection.Auto, 4000, 6000, 0).IsVertical &&
                  AxisChooser.Choose(SqueezeDirection.Auto, 4000, 6000, 1).IsVertical &&
                  !AxisChooser.Choose(SqueezeDirection.Auto, 5000, 5000, 0).IsVertical,
                "Auto: tag 5-8 or portrait shape -> vertical; level camera or landscape -> horizontal");
            Check(!AxisChooser.Choose(SqueezeDirection.Horizontal, 4000, 6000, 6).IsVertical &&
                  AxisChooser.Choose(SqueezeDirection.Vertical, 6000, 4000, 1).IsVertical,
                "Horizontal / Vertical override the orientation");

            // End to end with a kernel: de-squeezing the upright image vertically == de-squeezing the stored sideways
            // image horizontally and then turning it upright.
            {
                var stored = new PixelImage(90, 60, 3, 8);
                rnd.NextBytes(stored.Data8);
                stored.Metadata.Exif = TestExif(true, 6, true);
                var up = ImageIO.MakeUpright(stored);
                int worst = 0;
                bool dims = true;
                foreach (var share in new[] { 0.0, 0.5, 1.0 })
                {
                    var k = new DesqueezeSettings { Factor = 1.5, WidthShare = share, Widen = WidenMethod.Kernel, Algorithm = ResampleAlgorithm.Lanczos3 };
                    var viaUpright = Desqueezer.Process(up, k);                         // Auto -> vertical
                    k.Direction = SqueezeDirection.Horizontal;
                    stored.Metadata.SourceOrientation = 0;
                    var viaStored = Orientation.Apply(Desqueezer.Process(stored, k), 6);
                    if (viaUpright.Width != viaStored.Width || viaUpright.Height != viaStored.Height) { dims = false; continue; }
                    for (int i = 0; i < viaUpright.Data8.Length; i++) worst = Math.Max(worst, Math.Abs(viaUpright.Data8[i] - viaStored.Data8[i]));
                }
                Check(dims && worst == 0, "kernel, 3 splits: vertical de-squeeze of the upright shot = horizontal de-squeeze of the sideways file, turned (max diff " + worst + ")");

                // Without a tag (portrait-shaped, or forced Vertical) the image is turned clockwise and back.
                var portrait = new PixelImage(60, 90, 3, 8);
                rnd.NextBytes(portrait.Data8);
                var k2 = new DesqueezeSettings { Factor = 1.5, WidthShare = 0.5, Widen = WidenMethod.Kernel, Algorithm = ResampleAlgorithm.Lanczos3 };
                var a1 = Desqueezer.Process(portrait, k2);
                k2.Direction = SqueezeDirection.Horizontal;
                var a2 = Orientation.Apply(Desqueezer.Process(Orientation.Apply(portrait, 6), k2), 8);
                Check(a1.Width == 49 && a1.Height == 110 && Equal(a1.Data8, a2.Data8),
                    "untagged portrait: turned, de-squeezed horizontally, turned back (60x90 -> " + a1.Width + "x" + a1.Height + ")");
            }
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
                        var s = new DesqueezeSettings
                        {
                            Factor = 1.5, WidthShare = share, Widen = WidenMethod.Detail,
                            Direction = axis == SqueezeAxis.Horizontal ? SqueezeDirection.Horizontal : SqueezeDirection.Vertical,
                        };
                        var g = DesqueezeGeometry.Compute(120, 80, 1.5, share, axis);
                        var r = Desqueezer.Process(src, s);
                        bool learned = Desqueezer.UsesLearned(120, 80, g.OutputWidth, g.OutputHeight, s);
                        Check(r.Width == g.OutputWidth && r.Height == g.OutputHeight && learned == (share > 0),
                            "Process " + axis + " share " + share + " -> " + r.Width + "x" + r.Height + (learned ? " (learned)" : " (kernel)"));
                    }
                // A shot taken with the camera turned (orientation 6): Detail on the upright image == Detail on the
                // sideways file, rotated afterwards.
                var stored = new PixelImage(96, 64, 3, 8);
                rnd.NextBytes(stored.Data8);
                stored.Metadata.Exif = TestExif(true, 6, true);
                var up = ImageIO.MakeUpright(stored);
                bool same = true;
                foreach (var share in new[] { 0.5, 1.0 })
                {
                    var sd = new DesqueezeSettings { Factor = 1.6, WidthShare = share, Widen = WidenMethod.Detail };
                    var viaUp = Desqueezer.Process(up, sd);
                    sd.Direction = SqueezeDirection.Horizontal;
                    var viaStored = Orientation.Apply(Desqueezer.Process(stored, sd), 6);
                    same &= viaUp.Width == viaStored.Width && viaUp.Height == viaStored.Height && Equal(viaUp.Data8, viaStored.Data8);
                }
                Check(same, "Detail: camera-turned shot (tag 6) = sideways file de-squeezed horizontally, then rotated (bit-exact)");

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
