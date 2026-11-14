// Command-line harness for the Unity-independent core (Desqueeze.Core).
// Used to test codecs and to benchmark resampling quality outside Unity.
using System;
using System.Linq;
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
                    case "viewtest": ViewTests(); return s_fail == 0 ? 0 : 1;
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
                    case "circle-list": return CircleListCmd(args);
                    case "distortion-fit": return DistortionFitCmd(args);
                    case "fit-exp": return FitExperimentCmd(args);
                    case "lens-exp": return LensExperimentCmd(args);
                    case "lens-profile": return LensProfileCmd(args);
                    case "lens-apply": return LensApplyCmd(args);
                    case "warp2": return Warp2Cmd(args);
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

        // lens-exp <list.txt> <model.bin>: each line "sensor.png outPrefix outW outH kx1 ky1 kx2 ky2".
        // Corrects the squeezed capture onto the full frame with every pipeline and resampler variant, writing outPrefix_<name>.png.
        private static int LensExperimentCmd(string[] a)
        {
            Desqueeze.Core.Resampling.AxisSRModel.Default = Desqueeze.Core.Resampling.AxisSRModel.Load(a[2]);
            foreach (var line in File.ReadAllLines(a[1]))
            {
                var f = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 8) continue;
                var sensor = PngCodec.Decode(File.ReadAllBytes(f[0]));
                string prefix = f[1];
                int ow = int.Parse(f[2]), oh = int.Parse(f[3]);
                var model = new Desqueeze.Core.Lens.DistortionModel();
                model.Kx[0] = double.Parse(f[4], CultureInfo.InvariantCulture); model.Ky[0] = double.Parse(f[5], CultureInfo.InvariantCulture);
                model.Kx[1] = double.Parse(f[6], CultureInfo.InvariantCulture); model.Ky[1] = double.Parse(f[7], CultureInfo.InvariantCulture);
                double sq = (double)ow / sensor.Width;
                var frame = new Desqueeze.Core.Lens.CanvasRect(-1, -(double)oh / ow, 1, (double)oh / ow);
                var s16 = To16(sensor);
                Action<string, PixelImage> save = (name, img) =>
                    File.WriteAllBytes(prefix + "_" + name + ".png", PngCodec.Encode(img, new PngEncodeOptions { Compression = System.IO.Compression.CompressionLevel.Fastest }));
                var detail = new DesqueezeSettings { Factor = sq, WidthShare = 1, Widen = WidenMethod.Detail };
                var kernel = new DesqueezeSettings { Factor = sq, WidthShare = 1, Widen = WidenMethod.Kernel };
                Func<LightSpace, Desqueeze.Core.Resampling.WarpKernel, Desqueeze.Core.Resampling.WarpOptions> wo = (l, k) =>
                    new Desqueeze.Core.Resampling.WarpOptions { Light = l, Kernel = k, OutputBitDepth = 8 };
                var radial = wo(LightSpace.Gamma, Desqueeze.Core.Resampling.WarpKernel.RadialLanczos);
                var times = new List<string>();
                var sw = Stopwatch.StartNew();

                var only = Environment.GetEnvironmentVariable("LENS_METHODS");
                Func<string, bool> want = n => string.IsNullOrEmpty(only) || Array.IndexOf(only.Split(','), n) >= 0;
                var tensor = wo(LightSpace.Sigmoidal, Desqueeze.Core.Resampling.WarpKernel.TensorLanczos);
                var axm = Desqueeze.Core.Resampling.AxisSRModel.Default;
                string shiftPath = Environment.GetEnvironmentVariable("LENS_SHIFT_MODEL");
                var shm = !string.IsNullOrEmpty(shiftPath) ? Desqueeze.Core.Resampling.AxisSRModel.Load(shiftPath) : null;
                string tag = Environment.GetEnvironmentVariable("LENS_TAG") ?? "";
                Func<Desqueeze.Core.Lens.WarpPassMethod, Desqueeze.Core.Lens.WarpPassMethod, PixelImage> two = (h, v) =>
                    Desqueeze.Core.Lens.TwoPassWarp.Correct(s16, sq, model, frame, ow, oh, h, v, axm, null, 8);
                Func<Desqueeze.Core.Lens.WarpPassMethod, Desqueeze.Core.Lens.WarpPassMethod, PixelImage> twoShift = (h, v) =>
                    Desqueeze.Core.Lens.TwoPassWarp.Correct(s16, sq, model, frame, ow, oh, h, v, axm, shm, 8);
                var D = Desqueeze.Core.Lens.WarpPassMethod.Detail;
                var DF = Desqueeze.Core.Lens.WarpPassMethod.DetailFast;
                var K = Desqueeze.Core.Lens.WarpPassMethod.Kernel;
                PixelImage dqD = null;
                if (want("B_detail_tensor"))
                {
                    dqD = Desqueezer.ResizeTo(s16, ow, oh, detail, 16);
                    times.Add("desqueeze " + sw.ElapsedMilliseconds); sw.Restart();
                    save("B_detail_tensor", Desqueeze.Core.Lens.LensCorrection.Correct(dqD, 1, model, frame, ow, oh, tensor));
                    times.Add("warp " + sw.ElapsedMilliseconds); sw.Restart();
                }
                if (want("F_detail_kernel")) { save("F_detail_kernel" + tag, Desqueeze.Core.Lens.TwoPassWarp.Correct(s16, sq, model, frame, ow, oh, VOpts(axm), null)); times.Add("F_dk " + sw.ElapsedMilliseconds); sw.Restart(); }
                if (want("F_detail_detail")) { save("F_detail_detail", two(D, D)); times.Add("F_dd " + sw.ElapsedMilliseconds); sw.Restart(); }
                if (want("F_detail_fast")) save("F_detail_fast", two(D, DF));
                if (want("F_fast_kernel")) save("F_fast_kernel", two(DF, K));
                if (want("F_kernel_kernel")) save("F_kernel_kernel", two(K, K));
                if (shm != null && want("F_detail_shift")) { save("F_detail_shift" + tag, twoShift(D, D)); times.Add("F_ds " + sw.ElapsedMilliseconds); sw.Restart(); }
                if (shm != null && want("F_detail_shiftfast")) save("F_detail_shiftfast" + tag, twoShift(D, DF));
                if (want("B05") || want("F05") || want("none05"))
                {
                    var g05 = DesqueezeGeometry.Compute(sensor.Width, sensor.Height, sq, 0.5);
                    int ow2 = g05.OutputWidth, oh2 = g05.OutputHeight;
                    var detail05 = new DesqueezeSettings { Factor = sq, WidthShare = 0.5, Widen = WidenMethod.Detail };
                    if (want("B05") || want("none05"))
                    {
                        var dq05 = Desqueezer.ResizeTo(s16, ow2, oh2, detail05, 16);
                        if (want("none05")) save("none05", To8(dq05));
                        if (want("B05")) save("B05", Desqueeze.Core.Lens.LensCorrection.Correct(dq05, 1, model, frame, ow2, oh2, tensor));
                    }
                    if (want("F05")) save("F05" + tag, Desqueeze.Core.Lens.TwoPassWarp.Correct(s16, sq, model, frame, ow2, oh2, VOpts(axm), null));
                }
                if (want("Pnew") || want("Pold"))
                {
                    // the app's pipeline for a given split and widening (LENS_SHARE, LENS_WIDEN): new (two-pass) and old (de-squeeze + 2-D warp)
                    double share = double.Parse(Environment.GetEnvironmentVariable("LENS_SHARE") ?? "1", CultureInfo.InvariantCulture);
                    var wm = (WidenMethod)Enum.Parse(typeof(WidenMethod), Environment.GetEnvironmentVariable("LENS_WIDEN") ?? "Detail");
                    var ps = new DesqueezeSettings { Factor = sq, WidthShare = share, Widen = wm, Distortion = model, DistortionEdges = Desqueeze.Core.Lens.DistortionEdges.Uncropped };
                    var gs = DesqueezeGeometry.Compute(sensor.Width, sensor.Height, sq, share);
                    if (want("Pnew"))
                    {
                        var po = Desqueezer.TwoPassOptionsForTest(s16, ps, gs);
                        string hk = Environment.GetEnvironmentVariable("LENS_HKERNEL"), hl = Environment.GetEnvironmentVariable("LENS_HLIGHT");
                        if (hk == "lanczos3") po.HorizontalKernel = Desqueeze.Core.Resampling.Kernels.Lanczos(3);
                        if (hk == "catrom") po.HorizontalKernel = Desqueeze.Core.Resampling.Kernels.CatmullRom;
                        if (!string.IsNullOrEmpty(hl)) po.HorizontalLight = (LightSpace)Enum.Parse(typeof(LightSpace), hl);
                        var r = model.IsIdentity ? Desqueezer.Resize(s16, gs.OutputWidth, gs.OutputHeight, ps)
                            : Desqueeze.Core.Lens.TwoPassWarp.Correct(s16, sq, model, frame, gs.OutputWidth, gs.OutputHeight, po, null);
                        save("Pnew" + tag, To8(r));
                    }
                    if (want("Pold"))
                    {
                        var dq = Desqueezer.ResizeTo(s16, gs.OutputWidth, gs.OutputHeight, ps, 16);
                        save("Pold" + tag, model.IsIdentity ? To8(dq) : Desqueeze.Core.Lens.LensCorrection.Correct(dq, 1, model, frame, gs.OutputWidth, gs.OutputHeight, tensor));
                    }
                }
                if (want("P3"))
                {
                    // share LENS_SHARE without enough widening for the learned method: widen rows (learned, at the corrected
                    // positions) to an intermediate width, correct the columns, then reduce the width with the kernel.
                    double share = double.Parse(Environment.GetEnvironmentVariable("LENS_SHARE") ?? "0", CultureInfo.InvariantCulture);
                    double iscale = double.Parse(Environment.GetEnvironmentVariable("LENS_ISCALE") ?? "0", CultureInfo.InvariantCulture);
                    var ps = new DesqueezeSettings { Factor = sq, WidthShare = share, Widen = WidenMethod.Detail };
                    var gs = DesqueezeGeometry.Compute(sensor.Width, sensor.Height, sq, share);
                    int wi = iscale > 0 ? (int)Math.Round(sensor.Width * iscale) : DesqueezeGeometry.Compute(sensor.Width, sensor.Height, sq, 1).OutputWidth;
                    var o3 = Desqueezer.TwoPassOptionsForTest(s16, ps, gs);
                    o3.Horizontal = Desqueeze.Core.Lens.WarpPassMethod.Detail;
                    o3.OutputBitDepth = 16;
                    var mid = Desqueeze.Core.Lens.TwoPassWarp.Correct(s16, sq, model, frame, wi, gs.OutputHeight, o3, null);
                    var red = Desqueezer.ResizeTo(mid, gs.OutputWidth, gs.OutputHeight, new DesqueezeSettings { Factor = sq, Widen = WidenMethod.Kernel }, 8);
                    save("P3" + tag, red);
                }
                if (want("C_tensor")) save("C_tensor", Desqueeze.Core.Lens.LensCorrection.Correct(s16, sq, model, frame, ow, oh, tensor));
                Console.WriteLine(Path.GetFileName(prefix) + ": " + string.Join(", ", times) + " ms");
            }
            return 0;
        }

        /// <summary>Two-pass options for experiments: LENS_VKERNEL (lanczos3|mks|spline36|catrom|lanczos4), LENS_VLIGHT (Gamma|Sigmoidal|Linear).</summary>
        private static Desqueeze.Core.Lens.TwoPassOptions VOpts(Desqueeze.Core.Resampling.AxisSRModel axm)
        {
            string k = Environment.GetEnvironmentVariable("LENS_VKERNEL") ?? "lanczos3";
            string l = Environment.GetEnvironmentVariable("LENS_VLIGHT") ?? "Sigmoidal";
            var kern = k == "mks" ? Desqueeze.Core.Resampling.Kernels.MagicKernelSharp2021 : k == "spline36" ? Desqueeze.Core.Resampling.Kernels.Spline36
                : k == "catrom" ? Desqueeze.Core.Resampling.Kernels.CatmullRom : k == "lanczos4" ? Desqueeze.Core.Resampling.Kernels.Lanczos(4)
                : Desqueeze.Core.Resampling.Kernels.Lanczos(3);
            return new Desqueeze.Core.Lens.TwoPassOptions
            {
                Horizontal = Desqueeze.Core.Lens.WarpPassMethod.Detail, Vertical = Desqueeze.Core.Lens.WarpPassMethod.Kernel,
                VerticalKernel = kern, VerticalLight = (LightSpace)Enum.Parse(typeof(LightSpace), l), DetailModel = axm, OutputBitDepth = 8,
            };
        }

        private static PixelImage To16(PixelImage img)
        {
            if (img.BitDepth == 16) return img;
            var o = new PixelImage(img.Width, img.Height, img.Channels, 16) { Metadata = img.Metadata, SourceFormat = img.SourceFormat };
            for (int i = 0; i < img.Data8.Length; i++) o.Data16[i] = (ushort)(img.Data8[i] * 257);
            return o;
        }

        private static PixelImage To8(PixelImage img)
        {
            if (img.BitDepth == 8) return img;
            var o = new PixelImage(img.Width, img.Height, img.Channels, 8) { Metadata = img.Metadata, SourceFormat = img.SourceFormat };
            for (int i = 0; i < img.Data16.Length; i++) o.Data8[i] = (byte)((img.Data16[i] * 255 + 32767) / 65535);
            return o;
        }

        // lens-profile <lines.json> <name> <out.json>: fits the lines and writes a lens profile.
        private static int LensProfileCmd(string[] a)
        {
            using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(a[1])))
            {
                var root = doc.RootElement;
                var cal = new Desqueeze.Core.Lens.LensCalibration
                {
                    imageWidth = root.GetProperty("width").GetInt32(),
                    imageHeight = root.GetProperty("height").GetInt32(),
                    factor = root.GetProperty("factor").GetDouble(),
                    sourceImage = root.TryGetProperty("source", out var src) ? src.GetString() : "",
                };
                foreach (var l in root.GetProperty("lines").EnumerateArray())
                {
                    var cl = new Desqueeze.Core.Lens.CalibrationLine { orientation = l.GetProperty("orientation").GetString() };
                    foreach (var p in l.GetProperty("points").EnumerateArray())
                        cl.points.Add(new Desqueeze.Core.Lens.LinePoint(p[0].GetDouble(), p[1].GetDouble()));
                    cal.lines.Add(cl);
                }
                var prof = new Desqueeze.Core.Lens.LensProfile
                {
                    name = a[2], squeezeFactor = cal.factor, calibration = cal,
                    created = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture),
                };
                prof.modified = prof.created;
                var r = prof.Refit();
                File.WriteAllText(a[3], Desqueeze.Core.Lens.LensProfileJson.Write(prof));
                Console.WriteLine(prof.name + ": " + r.Kind + ", " + prof.distortion + ", " + r.RmsBefore.ToString("0.00") + " -> " + r.RmsAfter.ToString("0.00") +
                                  " px, " + Desqueeze.Core.Lens.DistortionSummary.Describe(prof.distortion, (double)cal.imageHeight / (cal.imageWidth * cal.factor)) +
                                  (r.Warning != null ? " (" + r.Warning + ")" : ""));
            }
            return 0;
        }

        // warp2 <in.png> <out.png> <kx1> <ky1> <kx2> <ky2> <H:Kernel|Detail> <V:Kernel|Detail|Shift> <detail.bin> [shift.bin]
        // Corrects an already de-squeezed image (squeeze 1) on the full frame with the two-pass warp.
        private static int Warp2Cmd(string[] a)
        {
            var img = PngCodec.Decode(File.ReadAllBytes(a[1]));
            var m = new Desqueeze.Core.Lens.DistortionModel();
            m.Kx[0] = double.Parse(a[3], CultureInfo.InvariantCulture); m.Ky[0] = double.Parse(a[4], CultureInfo.InvariantCulture);
            m.Kx[1] = double.Parse(a[5], CultureInfo.InvariantCulture); m.Ky[1] = double.Parse(a[6], CultureInfo.InvariantCulture);
            var detail = Desqueeze.Core.Resampling.AxisSRModel.Load(a[9]);
            var shift = a.Length > 10 ? Desqueeze.Core.Resampling.AxisSRModel.Load(a[10]) : null;
            Func<string, Desqueeze.Core.Lens.WarpPassMethod> pm = t => t == "Kernel" ? Desqueeze.Core.Lens.WarpPassMethod.Kernel
                : t == "Fast" || t == "ShiftFast" ? Desqueeze.Core.Lens.WarpPassMethod.DetailFast : Desqueeze.Core.Lens.WarpPassMethod.Detail;
            double hh = (double)img.Height / img.Width;
            var frame = new Desqueeze.Core.Lens.CanvasRect(-1, -hh, 1, hh);
            PixelImage r;
            if (a[7] == "2D")
                r = Desqueeze.Core.Lens.LensCorrection.Correct(img, 1, m, frame, img.Width, img.Height, Desqueezer.DistortionWarpOptions(img, 8));
            else
                r = Desqueeze.Core.Lens.TwoPassWarp.Correct(img, 1, m, frame, img.Width, img.Height, pm(a[7]), pm(a[8]), detail,
                    a[8] == "Shift" || a[8] == "ShiftFast" ? shift : null, 8);
            File.WriteAllBytes(a[2], PngCodec.Encode(r));
            return 0;
        }

        // lens-apply <in.png> <out.png|jpg> <profile.json> <model.bin> [amount] [AutoCrop|Uncropped] [After|Before|Single]: the app's pipeline with a profile.
        private static int LensApplyCmd(string[] a)
        {
            Desqueeze.Core.Resampling.AxisSRModel.Default = Desqueeze.Core.Resampling.AxisSRModel.Load(a[4]);
            var img = ImageIO.MakeUpright(PngCodec.Decode(File.ReadAllBytes(a[1])));
            var prof = Desqueeze.Core.Lens.LensProfileJson.Read(File.ReadAllText(a[3]));
            var s = new DesqueezeSettings { Factor = prof.squeezeFactor, Distortion = prof.Distortion,
                WidthShare = double.Parse(Environment.GetEnvironmentVariable("LENS_SHARE") ?? "1", CultureInfo.InvariantCulture) };
            if (a.Length > 5) s.DistortionAmount = double.Parse(a[5], CultureInfo.InvariantCulture);
            if (a.Length > 6) s.DistortionEdges = (Desqueeze.Core.Lens.DistortionEdges)Enum.Parse(typeof(Desqueeze.Core.Lens.DistortionEdges), a[6], true);
            if (a.Length > 7) s.DistortionOrder = a[7].StartsWith("B", StringComparison.OrdinalIgnoreCase) ? Desqueeze.Core.Lens.DistortionOrder.BeforeDesqueeze
                : a[7].StartsWith("S", StringComparison.OrdinalIgnoreCase) ? Desqueeze.Core.Lens.DistortionOrder.SinglePass : Desqueeze.Core.Lens.DistortionOrder.AfterDesqueeze;
            var p = new ProgressTracker();
            long mem0 = GC.GetTotalMemory(true);
            var sw = Stopwatch.StartNew();
            var r = Desqueezer.Process(img, s, p);
            long ms = sw.ElapsedMilliseconds;
            var g = Desqueezer.Geometry(img, s);
            Console.WriteLine(img.Width + " x " + img.Height + " -> " + r.Width + " x " + r.Height + " (de-squeeze alone " + g.OutputWidth + " x " + g.OutputHeight + ") in " +
                              ms + " ms, " + s + ", peak managed ~" + ((GC.GetTotalMemory(false) - mem0) / 1048576) + " MB");
            File.WriteAllBytes(a[2], ImageIO.Encode(r, ImageIO.FormatFromExtension(a[2])));
            return 0;
        }

        // ------------------------------------------------------------------------------------------- fit experiment

        private delegate void Lens(double xu, double yu, out double xd, out double yd);

        private sealed class FitScenario
        {
            public string Name;
            public Lens Lens;
        }

        private static List<FitScenario> FitScenarios(double hh)
        {
            var list = new List<FitScenario>();
            Func<string, Desqueeze.Core.Lens.DistortionModel, FitScenario> fromModel = (n, m) => new FitScenario { Name = n, Lens = (double x, double y, out double xd, out double yd) => m.Distort(x, y, out xd, out yd) };
            list.Add(fromModel("radial barrel", Model(-0.03, -0.03)));
            list.Add(fromModel("anamorphic barrel", Model(-0.04, -0.012)));
            list.Add(fromModel("radial moustache", Model(-0.06, -0.06, 0.05, 0.05)));
            list.Add(fromModel("H barrel, V pincushion", Model(0.02, -0.03)));
            list.Add(new FitScenario { Name = "barrel centre, pincushion sides", Lens = (double x, double y, out double xd, out double yd) =>
            {
                xd = x * (1 - 0.02 * x * x - 0.02 * y * y);
                yd = y * (1 - 0.05 * x * x + 0.07 * x * x * x * x - 0.01 * y * y);
            } });
            list.Add(new FitScenario { Name = "decentred barrel", Lens = (double x, double y, out double xd, out double yd) =>
            {
                double ax = x - 0.10, ay = y + 0.06, r2 = ax * ax + ay * ay;
                xd = x - 0.04 * ax * r2;
                yd = y - 0.04 * ay * r2;
            } });
            list.Add(new FitScenario { Name = "anamorphic + smile", Lens = (double x, double y, out double xd, out double yd) =>
            {
                double r2 = x * x + y * y;
                xd = x * (1 - 0.04 * r2);
                yd = y * (1 - 0.012 * r2) + 0.006 * x * x;
            } });
            list.Add(new FitScenario { Name = "not a polynomial", Lens = (double x, double y, out double xd, out double yd) =>
            {
                double r2 = x * x + y * y;
                xd = x / (1 + 0.05 * r2) + 0.003 * Math.Sin(2.5 * y / hh) * x;
                yd = y / (1 + 0.02 * r2) + 0.004 * Math.Sin(3 * x) * y / hh;
            } });
            return list;
        }

        /// <summary>Points along straight world lines as the lens draws them, in pixels, with marking noise.</summary>
        private static List<Desqueeze.Core.Lens.CalibrationLine> MarkedLines(Lens lens, string layout, int w, int h, double f, double noise, int seed)
        {
            var frame = new Desqueeze.Core.Lens.LensFrame(w, h, f);
            double hh = frame.HalfHeight;
            var rnd = new Random(seed);
            Func<double> gauss = () => Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble());
            var lines = new List<Desqueeze.Core.Lens.CalibrationLine>();
            double[] offsets;
            int pts;
            double reach = 1.15, tilt = 4;
            bool wantH = true, wantV = true;
            switch (layout)
            {
                case "grid": offsets = new[] { -0.9, -0.6, -0.3, 0, 0.3, 0.6, 0.9 }; pts = 13; break;
                case "sparse": offsets = new[] { -0.8, 0, 0.8 }; pts = 9; break;
                case "h-only": offsets = new[] { -0.9, -0.45, 0, 0.45, 0.9 }; pts = 11; wantV = false; break;
                case "central": offsets = new[] { -0.45, -0.15, 0.15, 0.45 }; pts = 9; reach = 0.55; break;
                case "level": offsets = new[] { -0.9, -0.6, -0.3, 0, 0.3, 0.6, 0.9 }; pts = 13; tilt = 0; break;
                case "diag": offsets = new[] { -0.9, -0.6, -0.3, 0, 0.3, 0.6, 0.9 }; pts = 13; break;
                case "few": offsets = new[] { -0.75, 0.75 }; pts = 11; break;
                case "two-h": offsets = new[] { -0.8, 0.7 }; pts = 12; wantV = false; break;
                default: throw new ArgumentException(layout);
            }
            foreach (bool horiz in new[] { true, false })
            {
                if (horiz && !wantH || !horiz && !wantV) continue;
                foreach (double c in offsets)
                {
                    double slope = Math.Tan((rnd.NextDouble() * 2 - 1) * tilt * Math.PI / 180);
                    var l = new Desqueeze.Core.Lens.CalibrationLine { orientation = horiz ? "horizontal" : "vertical" };
                    for (int i = 0; i < pts; i++)
                    {
                        double t = (-1 + 2.0 * i / (pts - 1)) * reach;
                        double xu = horiz ? t : c + slope * t * hh, yu = horiz ? c * hh + slope * t : t * hh;
                        double xd, yd, px, py;
                        lens(xu, yu, out xd, out yd);
                        if (Math.Abs(xd) > 0.985 || Math.Abs(yd) > hh * 0.985) continue;
                        frame.ToPixel(xd, yd, out px, out py);
                        l.points.Add(new Desqueeze.Core.Lens.LinePoint(px + noise * gauss(), py + noise * gauss()));
                    }
                    if (l.points.Count >= 3) lines.Add(l);
                }
            }
            if (layout == "diag")
            {
                // six lines across the frame at steep angles, marked like horizontal ones
                foreach (double deg in new[] { 33.0, -33, 24, -24 })
                    foreach (double off in new[] { -0.45, 0.45 })
                    {
                        if (Math.Abs(deg) < 30 && off > 0) continue;
                        double dx = Math.Cos(deg * Math.PI / 180), dy = Math.Sin(deg * Math.PI / 180);
                        var l = new Desqueeze.Core.Lens.CalibrationLine { orientation = "horizontal" };
                        for (int i = 0; i < 15; i++)
                        {
                            double t = (-1 + 2.0 * i / 14) * 1.3, xu = -dy * off * hh + t * dx, yu = dx * off * hh + t * dy, xd, yd, px, py;
                            lens(xu, yu, out xd, out yd);
                            if (Math.Abs(xd) > 0.985 || Math.Abs(yd) > hh * 0.985) continue;
                            frame.ToPixel(xd, yd, out px, out py);
                            l.points.Add(new Desqueeze.Core.Lens.LinePoint(px + noise * gauss(), py + noise * gauss()));
                        }
                        if (l.points.Count >= 3) lines.Add(l);
                    }
            }
            return lines;
        }

        /// <summary>
        /// How straight a correction makes the world: straight lines at many positions and angles are drawn through the
        /// lens, corrected with <paramref name="m"/>, and measured against the straight line through them. Returns the RMS and
        /// the largest of the lines' greatest deviations, in de-squeezed pixels.
        /// </summary>
        private static void Straightness(Lens lens, Desqueeze.Core.Lens.DistortionModel m, double hh, double pxPerUnit, out double rms, out double worst, int which = 0)
        {
            double ss = 0;
            int n = 0;
            worst = 0;
            foreach (double deg in which == 1 ? new[] { 0.0, 90 } : which == 2 ? new[] { 30.0, -30, 60, -60, 45, -45 } : new[] { 0.0, 90, 30, -30, 60, -60, 12, -78 })
            {
                double dx = Math.Cos(deg * Math.PI / 180), dy = Math.Sin(deg * Math.PI / 180);
                for (int k = -6; k <= 6; k++)
                {
                    // offset along the normal, spanning the frame
                    double span = Math.Abs(-dy) * 1 + Math.Abs(dx) * hh;
                    double off = k / 6.5 * span;
                    double ox = -dy * off, oy = dx * off;
                    var pts = new List<double[]>();
                    for (int i = -60; i <= 60; i++)
                    {
                        double t = i / 60.0 * 1.6, xu = ox + t * dx, yu = oy + t * dy, xd, yd, cx, cy;
                        lens(xu, yu, out xd, out yd);
                        if (Math.Abs(xd) > 1 || Math.Abs(yd) > hh) continue;
                        if (!m.Undistort(xd, yd, out cx, out cy)) { cx = xd; cy = yd; }
                        pts.Add(new[] { cx, cy });
                    }
                    if (pts.Count < 12) continue;
                    double mx = 0, my = 0;
                    foreach (var q in pts) { mx += q[0]; my += q[1]; }
                    mx /= pts.Count; my /= pts.Count;
                    double sxx = 0, sxy = 0, syy = 0;
                    foreach (var q in pts) { sxx += (q[0] - mx) * (q[0] - mx); sxy += (q[0] - mx) * (q[1] - my); syy += (q[1] - my) * (q[1] - my); }
                    double ang = 0.5 * Math.Atan2(2 * sxy, sxx - syy), ux = Math.Cos(ang), uy = Math.Sin(ang);
                    double far = 0;
                    foreach (var q in pts) far = Math.Max(far, Math.Abs(-(q[0] - mx) * uy + (q[1] - my) * ux));
                    far *= pxPerUnit;
                    ss += far * far;
                    n++;
                    worst = Math.Max(worst, far);
                }
            }
            rms = Math.Sqrt(ss / Math.Max(1, n));
        }

        // fit-exp [noisePx] [seeds] [freeRidge] [growth] [layouts,comma] [maxRung]: the old (radial) and the new fit on lenses of increasing complexity.
        private static int FitExperimentCmd(string[] a)
        {
            double noise = a.Length > 1 ? double.Parse(a[1], CultureInfo.InvariantCulture) : 0.4;
            int seeds = a.Length > 2 ? int.Parse(a[2]) : 3;
            var opt = new Desqueeze.Core.Lens.DistortionFitOptions();
            if (a.Length > 3) opt.FreeRidge = double.Parse(a[3], CultureInfo.InvariantCulture);
            if (a.Length > 4) opt.FreeRidgeGrowth = double.Parse(a[4], CultureInfo.InvariantCulture);
            string[] layouts = a.Length > 5 ? a[5].Split(',') : new[] { "grid", "level", "diag", "sparse", "h-only", "central" };
            if (a.Length > 6) opt.MaxRung = int.Parse(a[6]);
            if (a.Length > 7) opt.SecondOrderRidge = double.Parse(a[7], CultureInfo.InvariantCulture);
            if (a.Length > 8) opt.SplitRidge = double.Parse(a[8], CultureInfo.InvariantCulture);
            if (a.Length > 9) opt.SplitRidge2 = double.Parse(a[9], CultureInfo.InvariantCulture);
            if (a.Length > 10) opt.HiddenRidge = double.Parse(a[10], CultureInfo.InvariantCulture);
            opt.Trace = Environment.GetEnvironmentVariable("FITTRACE") != null;
            if (Environment.GetEnvironmentVariable("FIT_PPT") != null) opt.PointsPerTerm = double.Parse(Environment.GetEnvironmentVariable("FIT_PPT"), CultureInfo.InvariantCulture);
            if (Environment.GetEnvironmentVariable("FIT_MINLINES") != null) opt.MinLinesForGeneral = int.Parse(Environment.GetEnvironmentVariable("FIT_MINLINES"));
            if (Environment.GetEnvironmentVariable("FIT_PREC") != null) opt.PointPrecision = double.Parse(Environment.GetEnvironmentVariable("FIT_PREC"), CultureInfo.InvariantCulture);
            const int w = 6000, h = 4000;
            const double f = 1.5;
            double hh = h / (w * f), ppu = w * f / 2;
            var scenarios = FitScenarios(hh);
            Console.WriteLine(string.Format("noise {0} px, {1} seed(s), ridge {2} x {3}^deg; straightness of world lines after correction, px (rms / worst), 9000 px wide", noise, seeds, opt.FreeRidge, opt.FreeRidgeGrowth));
            double totalOld = 0, totalNew = 0, ms = 0, totalOldD = 0, totalNewD = 0;
            int cells = 0;
            Console.WriteLine("   (hv = level and upright lines, dg = diagonals; rms/worst)");
            foreach (var layout in layouts)
            {
                Console.WriteLine("-- lines: " + layout);
                foreach (var sc in scenarios)
                {
                    var acc = new double[3, 4];   // none/old/new x hv rms, hv worst, dg rms, dg worst
                    string kinds = "";
                    for (int seed = 1; seed <= seeds; seed++)
                    {
                        var lines = MarkedLines(sc.Lens, layout, w, h, f, noise, seed * 17 + 3);
                        var old = LegacyDistortionFit.Fit(lines, w, h, f);
                        var sw = Stopwatch.StartNew();
                        var neu = Desqueeze.Core.Lens.DistortionFit.Fit(lines, w, h, f, opt);
                        ms = Math.Max(ms, sw.Elapsed.TotalMilliseconds);
                        var models = new[] { new Desqueeze.Core.Lens.DistortionModel(), old.Model, neu.Model };
                        for (int k = 0; k < 3; k++)
                            for (int which = 1; which <= 2; which++)
                            {
                                double r, wo;
                                Straightness(sc.Lens, models[k], hh, ppu, out r, out wo, which);
                                acc[k, (which - 1) * 2] += r / seeds;
                                acc[k, (which - 1) * 2 + 1] = Math.Max(acc[k, (which - 1) * 2 + 1], wo);
                            }
                        kinds += (seed > 1 ? "," : "") + neu.Rung;
                        if (Environment.GetEnvironmentVariable("FITDEBUG") != null && seed == 1)
                            Console.WriteLine("      " + neu.Model + "\n      rms on marked lines " + neu.RmsAfter.ToString("0.00") + " px (old " + old.RmsAfter.ToString("0.00") + ")");
                    }
                    totalOld += acc[1, 0]; totalNew += acc[2, 0]; totalOldD += acc[1, 2]; totalNewD += acc[2, 2]; cells++;
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "  {0,-32} none hv {1,5:0.0}/{2,5:0.0} dg {3,5:0.0}/{4,5:0.0} | old hv {5,5:0.00}/{6,5:0.0} dg {7,5:0.00}/{8,5:0.0} | new hv {9,5:0.00}/{10,5:0.0} dg {11,5:0.00}/{12,5:0.0}  rung {13}",
                        sc.Name, acc[0, 0], acc[0, 1], acc[0, 2], acc[0, 3], acc[1, 0], acc[1, 1], acc[1, 2], acc[1, 3], acc[2, 0], acc[2, 1], acc[2, 2], acc[2, 3], kinds));
                }
            }
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "mean rms: level/upright old {0:0.00}, new {1:0.00} px; diagonals old {2:0.00}, new {3:0.00} px; slowest fit {4:0} ms",
                totalOld / cells, totalNew / cells, totalOldD / cells, totalNewD / cells, ms));
            return 0;
        }

        // distortion-fit <lines.json>: {"width", "height", "factor", "lines": [{"orientation", "points": [[x, y], ...]}]}
        private static int DistortionFitCmd(string[] a)
        {
            using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(a[1])))
            {
                var root = doc.RootElement;
                int w = root.GetProperty("width").GetInt32(), h = root.GetProperty("height").GetInt32();
                double f = root.GetProperty("factor").GetDouble();
                var lines = new List<Desqueeze.Core.Lens.CalibrationLine>();
                var names = new List<string>();
                foreach (var l in root.GetProperty("lines").EnumerateArray())
                {
                    var cl = new Desqueeze.Core.Lens.CalibrationLine { orientation = l.GetProperty("orientation").GetString() };
                    foreach (var p in l.GetProperty("points").EnumerateArray())
                        cl.points.Add(new Desqueeze.Core.Lens.LinePoint(p[0].GetDouble(), p[1].GetDouble()));
                    lines.Add(cl);
                    names.Add(l.TryGetProperty("name", out var n) ? n.GetString() : "");
                }
                var sw = Stopwatch.StartNew();
                var r = Desqueeze.Core.Lens.DistortionFit.Fit(lines, w, h, f, new Desqueeze.Core.Lens.DistortionFitOptions { Trace = Environment.GetEnvironmentVariable("FITTRACE") != null });
                Console.WriteLine("fit in " + sw.ElapsedMilliseconds + " ms: " + r.Kind + ", " + r.Lines + " lines, " + r.Points + " points");
                Console.WriteLine("model " + r.Model.ToString());
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "straightness RMS {0:0.00} px before, {1:0.00} px after", r.RmsBefore, r.RmsAfter));
                double hh = h / (w * f);
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "largest displacement in frame: {0:0.0} px (de-squeezed, {1} px wide)",
                    r.Model.MaxDisplacement(hh) * w * f / 2, (int)(w * f)));
                for (int i = 0; i < lines.Count; i++)
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "  {0,-18} {1,-10} {2,3} pts  rms after {3:0.00} px",
                        names[i], lines[i].orientation, lines[i].points.Count, r.LineRms[i]));
                if (r.Warning != null) Console.WriteLine("warning: " + r.Warning);
                if (a.Length > 2) File.WriteAllText(a[2], r.Model.ToString());
            }
            return 0;
        }

        // circle-list <list.txt>: each line "image.png clickX clickY" -> one result line per image:
        // "ok cx cy rx ry points rays rms ms" or "fail <reason>".
        private static int CircleListCmd(string[] a)
        {
            if (Environment.GetEnvironmentVariable("CIRCLE_LOG") == "1") Desqueeze.Core.Calibration.CircleFinder.Log = Console.WriteLine;
            foreach (var line in File.ReadAllLines(a[1]))
            {
                var f = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 3) continue;
                var img = PngCodec.Decode(File.ReadAllBytes(f[0]));
                var sw = Stopwatch.StartNew();
                string err;
                var fit = Desqueeze.Core.Calibration.CircleFinder.Find(img, double.Parse(f[1], CultureInfo.InvariantCulture),
                    double.Parse(f[2], CultureInfo.InvariantCulture), out err);
                if (fit == null) Console.WriteLine("fail " + err);
                else
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "ok {0:R} {1:R} {2:R} {3:R} {4} {5} {6:R} {7} {8} {9}",
                        fit.CenterX, fit.CenterY, fit.RadiusX, fit.RadiusY, fit.EdgePoints, fit.Rays, fit.RmsError, sw.ElapsedMilliseconds,
                        fit.ThinOutline ? 1 : 0, fit.Warning != null ? 1 : 0));
            }
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
            CalibrationTests();
            LensTests();
            ViewTests();

            string modelPath = Path.Combine("Assets", "StreamingAssets", "desqueeze-axissr.bin");
            if (File.Exists(modelPath)) AxisSRTests(modelPath);
            if (File.Exists(modelPath)) TwoPassTests(modelPath);
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

        /// <summary>
        /// An axis-aligned ellipse (a squeezed circle) rendered with 8x8 supersampling in linear light, plus noise.
        /// ringWidth > 0 draws only an outline of that width (inside the ellipse). Pixel centres are at whole numbers.
        /// </summary>
        internal static PixelImage RenderEllipse(int w, int h, double cx, double cy, double a, double b, double inside, double outside,
            double ringWidth, double noise, int seed)
        {
            var img = new PixelImage(w, h, 3, 8);
            var rnd = new Random(seed);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double sum = 0;
                    for (int j = 0; j < 8; j++)
                        for (int i = 0; i < 8; i++)
                        {
                            double u = (x - 0.5 + (i + 0.5) / 8 - cx) / a, v = (y - 0.5 + (j + 0.5) / 8 - cy) / b;
                            bool inOuter = u * u + v * v <= 1;
                            bool inInner = ringWidth > 0 &&
                                           Math.Pow((x - 0.5 + (i + 0.5) / 8 - cx) / (a - ringWidth), 2) + Math.Pow((y - 0.5 + (j + 0.5) / 8 - cy) / (b - ringWidth), 2) <= 1;
                            sum += inOuter && !inInner ? inside : outside;
                        }
                    double lin = sum / 64;
                    double enc = Desqueeze.Core.Color.Srgb.FromLinear(lin);
                    double g1 = rnd.NextDouble(), g2 = rnd.NextDouble();
                    enc += noise * Math.Sqrt(-2 * Math.Log(1 - g1)) * Math.Cos(2 * Math.PI * g2);
                    byte q = (byte)Math.Max(0, Math.Min(255, Math.Round(enc * 255)));
                    int o = (y * w + x) * 3;
                    img.Data8[o] = img.Data8[o + 1] = img.Data8[o + 2] = q;
                }
            return img;
        }

        private static void CalibrationTests()
        {
            Console.WriteLine("Calibration (circle finder)");
            const double cx = 311.37, cy = 240.81, a = 120.4, b = 180.6;    // a circle of radius 180.6 squeezed 1.5x
            var disk = RenderEllipse(640, 480, cx, cy, a, b, 0.02, 0.8, 0, 0.01, 1);
            string err;
            var fit = Desqueeze.Core.Calibration.CircleFinder.Find(disk, cx + 25, cy - 40, out err);
            bool ok = fit != null && Math.Abs(fit.CenterX - cx) < 0.05 && Math.Abs(fit.CenterY - cy) < 0.05 &&
                      Math.Abs(fit.RadiusX / a - 1) < 0.0005 && Math.Abs(fit.RadiusY / b - 1) < 0.0005;
            Check(ok, "dark disk, click off-centre: " + (fit != null ? fit + ", factor " + fit.MeasuredFactor(SqueezeAxis.Horizontal).ToString("0.0000") : err));

            var light = RenderEllipse(640, 480, cx, cy, b, a, 0.9, 0.05, 0, 0.01, 2);   // vertical squeeze, light on dark
            fit = Desqueeze.Core.Calibration.CircleFinder.Find(light, cx - 60, cy + 10, out err);
            ok = fit != null && Math.Abs(fit.MeasuredFactor(SqueezeAxis.Vertical) / 1.5 - 1) < 0.001 && !fit.DarkInside;
            Check(ok, "light disk, vertical squeeze: " + (fit != null ? "factor " + fit.MeasuredFactor(SqueezeAxis.Vertical).ToString("0.0000") : err));

            var ring = RenderEllipse(640, 480, cx, cy, a, b, 0.02, 0.8, 7, 0.01, 3);    // drawn outline 7 px wide
            fit = Desqueeze.Core.Calibration.CircleFinder.Find(ring, cx, cy, out err);
            ok = fit != null && Math.Abs(fit.RadiusX / a - 1) < 0.001 && Math.Abs(fit.RadiusY / b - 1) < 0.001;
            Check(ok, "drawn ring outline: the outer edge is the circle (" + (fit != null ? fit.RadiusX.ToString("0.00") + " x " + fit.RadiusY.ToString("0.00") : err) + ")");

            fit = Desqueeze.Core.Calibration.CircleFinder.Find(disk, 20, 20, out err);
            Check(fit == null && err != null, "click outside any circle -> clear message (" + err + ")");

            // The preview puts the ring exactly on the circle when the factor is right, and off it when it is not.
            fit = Desqueeze.Core.Calibration.CircleFinder.Find(disk, cx, cy, out err);
            bool preview = fit != null;
            foreach (var share in new[] { 0.0, 0.5, 1.0 })
            {
                if (!preview) break;
                var s = new DesqueezeSettings { Factor = 1.5, WidthShare = share, Algorithm = ResampleAlgorithm.Lanczos3 };
                var p = Desqueeze.Core.Calibration.CalibrationPreview.Render(disk, fit, s, SqueezeAxis.Horizontal);
                double ex = p.ImageRadiusX / p.RingRadius - 1, ey = p.ImageRadiusY / p.RingRadius - 1;
                var refit = Desqueeze.Core.Calibration.CircleFinder.Find(p.Image, p.CenterX, p.CenterY, out err);
                bool round = refit != null && Math.Abs(refit.RadiusX / p.RingRadius - 1) < 0.002 && Math.Abs(refit.RadiusY / p.RingRadius - 1) < 0.002 &&
                             Math.Abs(refit.CenterX - p.CenterX) < 0.1 && Math.Abs(refit.CenterY - p.CenterY) < 0.1;
                if (Math.Abs(ex) > 0.0006 || Math.Abs(ey) > 0.0006 || !round)
                {
                    preview = false;
                    Console.WriteLine("    share " + share + ": ring " + p.RingRadius.ToString("0.00") + ", image " + p.ImageRadiusX.ToString("0.00") + " x " +
                                      p.ImageRadiusY.ToString("0.00") + (refit != null ? ", found in preview " + refit : ", " + err));
                }
            }
            Check(preview, "preview at 1.5x (3 splits): the de-squeezed circle is round and fills the ring");
            if (fit != null)
            {
                var off = Desqueeze.Core.Calibration.CalibrationPreview.Render(disk, fit, new DesqueezeSettings { Factor = 1.4, WidthShare = 1 }, SqueezeAxis.Horizontal);
                Check(off.ImageRadiusX < off.RingRadius * 0.95, "preview at a wrong factor (1.4x): the circle stays narrower than the ring (" +
                      off.ImageRadiusX.ToString("0.0") + " vs " + off.RingRadius.ToString("0.0") + ")");
            }
        }

        // ------------------------------------------------------------------------------------------- lens

        private static Desqueeze.Core.Lens.DistortionModel Model(double kx1, double ky1, double kx2 = 0, double ky2 = 0)
        {
            var m = new Desqueeze.Core.Lens.DistortionModel();
            m.Kx[0] = kx1; m.Ky[0] = ky1; m.Kx[1] = kx2; m.Ky[1] = ky2;
            return m;
        }

        /// <summary>Straight lines in the undistorted plane, seen through the lens, as pixel points of a W x H capture.</summary>
        private static List<Desqueeze.Core.Lens.CalibrationLine> SyntheticLines(Desqueeze.Core.Lens.DistortionModel m, int w, int h, double f,
            double noise, int seed, bool horizontal = true, bool vertical = true)
        {
            var frame = new Desqueeze.Core.Lens.LensFrame(w, h, f);
            var rnd = new Random(seed);
            var lines = new List<Desqueeze.Core.Lens.CalibrationLine>();
            double hh = frame.HalfHeight;
            Func<double> gauss = () => Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble());
            for (int k = 0; k < 7; k++)
            {
                double c = -0.92 + 1.84 * k / 6;
                foreach (bool horiz in new[] { true, false })
                {
                    if (horiz && !horizontal || !horiz && !vertical) continue;
                    var l = new Desqueeze.Core.Lens.CalibrationLine { orientation = horiz ? "horizontal" : "vertical" };
                    for (int i = 0; i <= 24; i++)
                    {
                        double t = -1.2 + 2.4 * i / 24;
                        double xu = horiz ? t : c, yu = horiz ? c * hh : t * hh;
                        double xd, yd, px, py;
                        m.Distort(xu, yu, out xd, out yd);
                        if (Math.Abs(xd) > 0.99 || Math.Abs(yd) > hh * 0.99) continue;
                        frame.ToPixel(xd, yd, out px, out py);
                        l.points.Add(new Desqueeze.Core.Lens.LinePoint(px + noise * gauss(), py + noise * gauss()));
                    }
                    if (l.points.Count >= 3) lines.Add(l);
                }
            }
            return lines;
        }

        /// <summary>Largest difference between two models' corrections over the frame, in de-squeezed pixels.</summary>
        private static double ModelDifference(Desqueeze.Core.Lens.DistortionModel a, Desqueeze.Core.Lens.DistortionModel b, double hh, double pxPerUnit)
        {
            double worst = 0;
            for (int i = 0; i <= 40; i++)
                for (int j = 0; j <= 40; j++)
                {
                    double x = -1 + 2.0 * i / 40, y = (-1 + 2.0 * j / 40) * hh, ax, ay, bx, by;
                    a.Undistort(x, y, out ax, out ay);
                    b.Undistort(x, y, out bx, out by);
                    worst = Math.Max(worst, Math.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by)) * pxPerUnit);
                }
            return worst;
        }

        private static PixelImage Pattern(int w, int h, int ch, int depth, int seed)
        {
            var img = new PixelImage(w, h, ch, depth);
            var rnd = new Random(seed);
            // smooth-ish random field: sum of a few sinusoids per channel
            var ph = new double[ch * 6];
            for (int i = 0; i < ph.Length; i++) ph[i] = rnd.NextDouble() * 6.283;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < ch; c++)
                    {
                        double v = 0.5 + 0.2 * Math.Sin(x * 0.11 + ph[c * 6]) * Math.Cos(y * 0.07 + ph[c * 6 + 1]) + 0.15 * Math.Sin((x + 2 * y) * 0.031 + ph[c * 6 + 2]);
                        if (img.HasAlpha && c == ch - 1) v = 1;
                        int o = (y * w + x) * ch + c;
                        if (depth == 8) img.Data8[o] = (byte)Math.Round(v * 255);
                        else img.Data16[o] = (ushort)Math.Round(v * 65535);
                    }
            return img;
        }

        private static double Psnr8(PixelImage a, PixelImage b, int margin)
        {
            double se = 0;
            long n = 0;
            int ch = a.Channels;
            for (int y = margin; y < a.Height - margin; y++)
                for (int x = margin; x < a.Width - margin; x++)
                    for (int c = 0; c < ch; c++)
                    {
                        int o = (y * a.Width + x) * ch + c;
                        double d = a.BitDepth == 8 ? a.Data8[o] - b.Data8[o] : (a.Data16[o] - b.Data16[o]) / 257.0;
                        se += d * d;
                        n++;
                    }
            return se == 0 ? 99 : 10 * Math.Log10(255.0 * 255.0 * n / se);
        }

        private static void LensTests()
        {
            Console.WriteLine("Lens distortion");
            var models = new[]
            {
                Model(-0.03, -0.03), Model(0.04, 0.04), Model(-0.06, -0.02, 0.01, 0.004), Model(-0.08, -0.08, 0.05, 0.05), Model(0.02, -0.03),
            };
            bool inv = true, jac = true;
            var rnd = new Random(3);
            foreach (var m in models)
                for (int i = 0; i < 400; i++)
                {
                    double xu = rnd.NextDouble() * 2 - 1, yu = (rnd.NextDouble() * 2 - 1) * 0.7, xd, yd, bx, by, a, b, c, d;
                    m.Distort(xu, yu, out xd, out yd, out a, out b, out c, out d);
                    if (!m.Undistort(xd, yd, out bx, out by) || Math.Abs(bx - xu) > 1e-9 || Math.Abs(by - yu) > 1e-9) inv = false;
                    const double h = 1e-6;
                    double x1, y1, x2, y2;
                    m.Distort(xu + h, yu, out x1, out y1);
                    m.Distort(xu - h, yu, out x2, out y2);
                    if (Math.Abs((x1 - x2) / (2 * h) - a) > 1e-6 || Math.Abs((y1 - y2) / (2 * h) - c) > 1e-6) jac = false;
                    m.Distort(xu, yu + h, out x1, out y1);
                    m.Distort(xu, yu - h, out x2, out y2);
                    if (Math.Abs((x1 - x2) / (2 * h) - b) > 1e-6 || Math.Abs((y1 - y2) / (2 * h) - d) > 1e-6) jac = false;
                }
            Check(inv, "undistort inverts distort (5 models incl. anamorphic and moustache, 2000 points, < 1e-9)");
            Check(jac, "analytic Jacobian matches finite differences");
            // general terms
            var general = Model(-0.03, -0.012, 0.004, 0);
            general.Terms = new[]
            {
                new Desqueeze.Core.Lens.DistortionTerm(1, 2, -0.02, 0), new Desqueeze.Core.Lens.DistortionTerm(2, 1, 0, -0.05), new Desqueeze.Core.Lens.DistortionTerm(4, 1, 0, 0.07),
                new Desqueeze.Core.Lens.DistortionTerm(2, 0, 0.004, 0.006), new Desqueeze.Core.Lens.DistortionTerm(1, 1, -0.003, 0.002), new Desqueeze.Core.Lens.DistortionTerm(0, 7, 0.3, 0.2),
            };
            bool ginv = true, gjac = true, gsame = true;
            for (int i = 0; i < 2000; i++)
            {
                double xu = rnd.NextDouble() * 2.2 - 1.1, yu = (rnd.NextDouble() * 2 - 1) * 0.5, xd, yd, bx, by, a, b, c, d, xs, ys;
                general.Distort(xu, yu, out xd, out yd, out a, out b, out c, out d);
                general.Distort(xu, yu, out xs, out ys);
                if (xs != xd || ys != yd) gsame = false;
                if (!general.Undistort(xd, yd, out bx, out by) || Math.Abs(bx - xu) > 1e-9 || Math.Abs(by - yu) > 1e-9) ginv = false;
                const double h = 1e-6;
                double x1, y1, x2, y2;
                general.Distort(xu + h, yu, out x1, out y1);
                general.Distort(xu - h, yu, out x2, out y2);
                if (Math.Abs((x1 - x2) / (2 * h) - a) > 1e-6 || Math.Abs((y1 - y2) / (2 * h) - c) > 1e-6) gjac = false;
                general.Distort(xu, yu + h, out x1, out y1);
                general.Distort(xu, yu - h, out x2, out y2);
                if (Math.Abs((x1 - x2) / (2 * h) - b) > 1e-6 || Math.Abs((y1 - y2) / (2 * h) - d) > 1e-6) gjac = false;
            }
            Check(ginv && gjac && gsame, "general terms (asymmetric, up to 7th degree): undistort inverts distort, Jacobian matches finite differences");
            {
                var half = general.Scaled(0.5);
                var twice = half.Scaled(2);
                var radialOnly = general.Clone();
                radialOnly.Terms = new Desqueeze.Core.Lens.DistortionTerm[0];
                Check(Math.Abs(half.Terms[2].Y - 0.035) < 1e-15 && twice.SameAs(general, 1e-15) && !half.SameAs(general) && !radialOnly.SameAs(general) &&
                      !general.IsIdentity && general.Scaled(0).IsIdentity && general.ToString() != radialOnly.ToString(),
                    "the correction strength scales the general terms too; models with different terms differ");
            }
            var scaled = Model(-0.04, -0.02, 0.01, 0).Scaled(0.5);
            Check(Math.Abs(scaled.Kx[0] + 0.02) < 1e-15 && Math.Abs(scaled.Ky[0] + 0.01) < 1e-15 && Math.Abs(scaled.Kx[1] - 0.005) < 1e-15,
                "amount scales the coefficients");

            // plumb-line fit
            const int W = 6000, H = 4000;
            const double F = 1.5;
            double hh = (double)H / (W * F), ppu = W * F / 2;
            foreach (var truth in new[] { Model(-0.03, -0.03), Model(0.025, 0.025), Model(-0.04, -0.02) })
            {
                var lines = SyntheticLines(truth, W, H, F, 0.25, 11);
                var sw = Stopwatch.StartNew();
                var r = Desqueeze.Core.Lens.DistortionFit.Fit(lines, W, H, F);
                double diff = ModelDifference(truth, r.Model, hh, ppu);
                Check(diff < 1.5 && r.RmsAfter < 0.5 && r.RmsBefore > 3 * r.RmsAfter,
                    "fit recovers " + truth + ": max error " + diff.ToString("0.00") + " px, straightness " + r.RmsBefore.ToString("0.00") + " -> " +
                    r.RmsAfter.ToString("0.00") + " px (" + r.Kind + ", " + r.Lines + " lines, " + r.Points + " points, " + sw.ElapsedMilliseconds + " ms)");
            }
            {
                var truth = Model(-0.03, -0.03);
                var r = Desqueeze.Core.Lens.DistortionFit.Fit(SyntheticLines(truth, W, H, F, 0.25, 12, true, false), W, H, F);
                Check(ModelDifference(truth, r.Model, hh, ppu) < 2.5, "horizontal lines alone measure a radial lens (" + r.Kind + ")");
                var clean = SyntheticLines(truth, W, H, F, 0, 13);
                clean[2].points[clean[2].points.Count / 2] = new Desqueeze.Core.Lens.LinePoint(clean[2].points[clean[2].points.Count / 2].x + 25, clean[2].points[clean[2].points.Count / 2].y + 25);
                var rr = Desqueeze.Core.Lens.DistortionFit.Fit(clean, W, H, F);
                Check(ModelDifference(truth, rr.Model, hh, ppu) < 1.0, "one misplaced point (25 px off) barely moves the fit (robust weighting)");
                var none = Desqueeze.Core.Lens.DistortionFit.Fit(new List<Desqueeze.Core.Lens.CalibrationLine>(), W, H, F);
                Check(none.Lines == 0 && none.Model.IsIdentity && none.Warning != null, "no lines: identity and a hint");
            }

            // canvas
            {
                var barrel = Model(-0.05, -0.05);
                var crop = Desqueeze.Core.Lens.LensCorrection.Canvas(barrel, hh, Desqueeze.Core.Lens.DistortionEdges.AutoCrop);
                var full = Desqueeze.Core.Lens.LensCorrection.Canvas(barrel, hh, Desqueeze.Core.Lens.DistortionEdges.Uncropped);
                bool inside = true, touches = false;
                for (int i = 0; i <= 200; i++)
                {
                    double t = i / 200.0;
                    foreach (var p in new[] { new[] { crop.X0 + t * crop.Width, crop.Y0 }, new[] { crop.X0 + t * crop.Width, crop.Y1 }, new[] { crop.X0, crop.Y0 + t * crop.Height }, new[] { crop.X1, crop.Y0 + t * crop.Height } })
                    {
                        double xd, yd;
                        barrel.Distort(p[0], p[1], out xd, out yd);
                        if (Math.Abs(xd) > 1 + 1e-9 || Math.Abs(yd) > hh + 1e-9) inside = false;
                        if (Math.Abs(xd) > 1 - 1e-6 || Math.Abs(yd) > hh - 1e-6) touches = true;
                    }
                }
                Check(inside && touches && Math.Abs(crop.Width / crop.Height - 1 / hh) < 1e-9,
                    "auto-crop: the largest rectangle of the frame's shape inside the source " + crop);
                Check(full.X0 < -1 && full.X1 > 1 && full.Y0 < -hh && full.Y1 > hh, "uncropped (barrel): everything the photo shows, beyond the frame " + full);
                var pin = Desqueeze.Core.Lens.LensCorrection.Canvas(Model(0.05, 0.05), hh, Desqueeze.Core.Lens.DistortionEdges.AutoCrop);
                Check(pin.X1 < 1 && pin.Y1 < hh, "auto-crop (pincushion) crops inside the frame " + pin);
            }

            // warp invariants
            bool ident = true;
            foreach (var ch in new[] { 1, 2, 3, 4 })
                foreach (var depth in new[] { 8, 16 })
                {
                    var img = Pattern(61, 37, ch, depth, ch * 10 + depth);
                    var frame = new Desqueeze.Core.Lens.CanvasRect(-1, -37.0 / 61, 1, 37.0 / 61);
                    var o = Desqueeze.Core.Lens.LensCorrection.Correct(img, 1, new Desqueeze.Core.Lens.DistortionModel(), frame, 61, 37,
                        Desqueezer.DistortionWarpOptions(img, depth));
                    for (int i = 0; i < img.Width * img.Height * ch; i++)
                    {
                        int dv = depth == 8 ? Math.Abs(img.Data8[i] - o.Data8[i]) : Math.Abs(img.Data16[i] - o.Data16[i]);
                        if (dv > (depth == 8 ? 0 : 2)) { ident = false; break; }
                    }
                }
            Check(ident, "warp with the identity reproduces the image (1-4 channels, 8/16-bit, sigmoidal light)");
            {
                var flat = new PixelImage(300, 200, 3, 16);
                for (int i = 0; i < flat.Data16.Length; i++) flat.Data16[i] = 30000;
                var m = Model(-0.06, -0.03, 0.01, 0.005);
                double fh = 200.0 / 300;
                var crop = Desqueeze.Core.Lens.LensCorrection.Canvas(m, fh, Desqueeze.Core.Lens.DistortionEdges.AutoCrop);
                var o = Desqueeze.Core.Lens.LensCorrection.Correct(flat, 1, m, crop, 300, 200, Desqueezer.DistortionWarpOptions(flat, 16));
                bool ok = true;
                for (int i = 0; i < o.Data16.Length; i++) if (Math.Abs(o.Data16[i] - 30000) > 3) { ok = false; break; }
                Check(ok, "flat field stays flat through an auto-cropped correction (no dark edges)");
            }

            // full pipeline
            {
                var img = Pattern(240, 160, 3, 8, 5);
                var plain = new DesqueezeSettings { Factor = 1.5, WidthShare = 1, Widen = WidenMethod.Kernel };
                var r0 = Desqueezer.Process(img, plain);
                var zero = plain.Clone(); zero.Distortion = Model(-0.05, -0.05); zero.DistortionAmount = 0;
                var idm = plain.Clone(); idm.Distortion = new Desqueeze.Core.Lens.DistortionModel();
                Check(Equal(r0.Data8, Desqueezer.Process(img, zero).Data8) && Equal(r0.Data8, Desqueezer.Process(img, idm).Data8),
                    "no distortion, an identity model or amount 0% give exactly the plain de-squeeze");

                var s = plain.Clone();
                s.Distortion = Model(-0.05, -0.03, 0.005, 0);
                foreach (var edges in new[] { Desqueeze.Core.Lens.DistortionEdges.AutoCrop, Desqueeze.Core.Lens.DistortionEdges.Uncropped })
                {
                    s.DistortionEdges = edges;
                    var outs = new List<PixelImage>();
                    foreach (var order in new[] { Desqueeze.Core.Lens.DistortionOrder.AfterDesqueeze, Desqueeze.Core.Lens.DistortionOrder.BeforeDesqueeze, Desqueeze.Core.Lens.DistortionOrder.SinglePass })
                    {
                        s.DistortionOrder = order;
                        int ew, eh;
                        Desqueezer.OutputSize(img, s, out ew, out eh);
                        var r = Desqueezer.Process(img, s);
                        if (r.Width != ew || r.Height != eh) Check(false, "output size matches OutputSize for " + order + " " + edges);
                        outs.Add(r);
                    }
                    double p1 = Psnr8(outs[0], outs[1], 8), p2 = Psnr8(outs[0], outs[2], 8);
                    Check(p1 > 33 && p2 > 33, edges + ": after, before and one-pass agree (" + p1.ToString("0.0") + " / " + p2.ToString("0.0") +
                          " dB) at " + outs[0].Width + " x " + outs[0].Height);
                    // Barrel correction pulls the corners out furthest, so uncropped the gaps are at the middle of each edge.
                    int corner = 0, midTop = (outs[0].Width / 2) * 3;
                    if (edges == Desqueeze.Core.Lens.DistortionEdges.Uncropped)
                        Check(outs[0].Data8[midTop] == 0 && outs[0].Data8[midTop + 1] == 0 && outs[0].Data8[corner] > 40,
                            "uncropped barrel correction: black where the photo doesn't reach (middle of the edges), picture in the corners");
                    else
                        Check(outs[0].Data8[corner] > 40 && outs[0].Data8[midTop] > 40, "auto-crop: corners and edges are filled");
                }

                // a portrait shot (camera turned, EXIF 6) is corrected in the sensor's frame
                var sideways = Pattern(150, 100, 3, 8, 9);
                var up = Orientation.Apply(sideways, 6);
                up.Metadata.SourceOrientation = 6;
                var sv = plain.Clone();
                sv.Distortion = Model(-0.05, -0.02);
                var viaUp = Desqueezer.Process(up, sv);
                var viaStored = Orientation.Apply(Desqueezer.Process(sideways, sv), 6);
                Check(viaUp.Width == viaStored.Width && viaUp.Height == viaStored.Height && Equal(viaUp.Data8, viaStored.Data8),
                    "portrait (EXIF 6): correcting upright = correcting the stored pixels, then turning");
            }

            // orientation point mapping matches the pixel transform
            {
                bool okMap = true;
                for (int o = 1; o <= 8; o++)
                {
                    var img = new PixelImage(5, 3, 1, 8);
                    img.Data8[1 * 5 + 3] = 255;
                    var t = Orientation.Apply(img, o);
                    double ox, oy;
                    Orientation.MapPoint(o, 5, 3, 3, 1, out ox, out oy);
                    if (t.Data8[(int)oy * t.Width + (int)ox] != 255) okMap = false;
                }
                Check(okMap, "orientation point mapping follows the pixels (all 8 orientations)");
            }

            // edge snapping
            {
                var img = new PixelImage(80, 60, 1, 8);
                const double edge = 30.3;
                for (int y = 0; y < 60; y++)
                    for (int x = 0; x < 80; x++)
                    {
                        double cover = Math.Max(0, Math.Min(1, y + 0.5 - edge));
                        img.Data8[y * 80 + x] = (byte)Math.Round(Srgb8(0.1 + 0.7 * cover));
                    }
                double sx, sy;
                int pol;
                bool found = Desqueeze.Core.Lens.EdgeSnap.Snap(img, 40, 34, true, 8, 0, out sx, out sy, out pol);
                Check(found && Math.Abs(sy - edge) < 0.1 && pol == 1 && sx == 40,
                    "snap finds a horizontal edge to a fraction of a pixel (" + sy.ToString("0.00") + " vs " + edge.ToString("0.00") + ")");
                Check(!Desqueeze.Core.Lens.EdgeSnap.Snap(img, 40, 34, true, 8, -1, out sx, out sy, out pol), "snap keeps the line's polarity (no light-to-dark edge here)");
            }

            // edge snapping never moves a point further than the click's uncertainty
            {
                // a faint edge at 30.3 (dark to a little lighter) and a strong one at 34.6 (to bright), a mortar-joint-like pair
                var img = new PixelImage(80, 60, 1, 8);
                const double faint = 30.3, strong = 34.6;
                for (int y = 0; y < 60; y++)
                    for (int x = 0; x < 80; x++)
                    {
                        double c1 = Math.Max(0, Math.Min(1, y + 0.5 - faint)), c2 = Math.Max(0, Math.Min(1, y + 0.5 - strong));
                        img.Data8[y * 80 + x] = (byte)Math.Round(Srgb8(0.1 + 0.08 * c1 + 0.7 * c2));
                    }
                double sx, sy;
                int pol;
                bool wide = Desqueeze.Core.Lens.EdgeSnap.Snap(img, 40, 31, true, 8, 0, out sx, out sy, out pol);
                Check(wide && Math.Abs(sy - strong) < 0.2, "a wide search (zoomed out) still finds the strong edge nearby (" + sy.ToString("0.00") + ")");
                bool near = Desqueeze.Core.Lens.EdgeSnap.Snap(img, 40, 30.6, true, 0.75, 0, out sx, out sy, out pol);
                Check(near && Math.Abs(sy - faint) < 0.2, "zoomed in, a point on the faint edge stays on it (" + sy.ToString("0.00") + ", strong edge " + strong + ")");
                bool worst = true;
                foreach (var r in new[] { 0.5, 0.75, 1.5, 3.0 })
                    for (double c = 26; c <= 40; c += 0.37)
                        if (Desqueeze.Core.Lens.EdgeSnap.Snap(img, 40, c, true, r, 0, out sx, out sy, out pol) && Math.Abs(sy - c) > r + 1e-9) worst = false;
                Check(worst, "a snapped point never moves further than the search radius (radii 0.5-3 px, 38 clicks each)");
                Check(!Desqueeze.Core.Lens.EdgeSnap.Snap(img, 40, 34.4, true, 0.75, -1, out sx, out sy, out pol),
                    "zoomed in, an edge of the wrong polarity leaves the point where it was put");
            }

            // profiles
            {
                var p = new Desqueeze.Core.Lens.LensProfile
                {
                    name = "Test \"lens\" \\ 1.54x", squeezeFactor = 1.54, notes = "line 1\nline 2\tend",
                    hasDistortion = true, distortion = Model(-0.0153, -0.0153, 0.001, 0), distortionKind = "radial", distortionRms = 0.95,
                    created = "2026-11-10T21:00:00Z", modified = "2026-11-10T21:05:00Z",
                };
                p.calibration = new Desqueeze.Core.Lens.LensCalibration { imageWidth = W, imageHeight = H, factor = 1.54, sourceImage = "wall.jpg" };
                p.calibration.lines.AddRange(SyntheticLines(p.distortion, W, H, 1.54, 0.1, 21));
                string json = Desqueeze.Core.Lens.LensProfileJson.Write(p);
                var back = Desqueeze.Core.Lens.LensProfileJson.Read(json);
                Check(Desqueeze.Core.Lens.LensProfileJson.Write(back) == json && back.SameSettings(p) && back.name == p.name && back.notes == p.notes &&
                      back.calibration.lines.Count == p.calibration.lines.Count, "lens profile JSON round trip (" + json.Length + " bytes)");
                bool rejected = false;
                try { Desqueeze.Core.Lens.LensProfileJson.Read("{\"format\": \"something-else\"}"); } catch (FormatException) { rejected = true; }
                bool rejected2 = false;
                try { Desqueeze.Core.Lens.LensProfileJson.Read("{\"format\": \"desqueeze-lens-profile\", \"squeezeFactor\": 1.5,"); } catch (FormatException) { rejected2 = true; }
                Check(rejected && rejected2, "files that aren't lens profiles are refused with a message");

                var refit = p.Clone();
                refit.squeezeFactor = 1.5;
                var rr = refit.Refit();
                Check(rr != null && rr.Lines > 0 && Math.Abs(refit.calibration.factor - 1.5) < 1e-12 && rr.RmsAfter < 0.5,
                    "refit for a new factor keeps the lines straight (" + rr.RmsAfter.ToString("0.00") + " px)");

                string text = Desqueeze.Core.Lens.DistortionSummary.Describe(Model(-0.03, -0.03), hh);
                Check(text.StartsWith("Lines bend by up to") && Desqueeze.Core.Lens.DistortionSummary.Describe(new Desqueeze.Core.Lens.DistortionModel(), hh) == "No distortion" &&
                      Math.Abs(Model(-0.03, -0.03).MaxBend(hh) - Model(0.03, 0.03).MaxBend(hh)) < 0.3 * Model(0.03, 0.03).MaxBend(hh) && general.MaxBend(hh) > 0,
                    "distortion described by how far lines bend, whatever its shape: " + text);

                // general terms in a profile
                var gp = p.Clone();
                gp.distortion = general.Clone();
                string gj = Desqueeze.Core.Lens.LensProfileJson.Write(gp);
                var gb = Desqueeze.Core.Lens.LensProfileJson.Read(gj);
                Check(gj.Contains("\"formatVersion\": 2") && json.Contains("\"formatVersion\": 1") && gb.distortion.SameAs(general) && !gb.distortion.SameAs(p.distortion) &&
                      Desqueeze.Core.Lens.LensProfileJson.Write(gb) == gj && gb.SameSettings(gp) && !gb.SameSettings(p),
                    "general terms round-trip in a profile (format 2); a radial profile is still written as format 1");
                bool badTerm = false;
                try { Desqueeze.Core.Lens.LensProfileJson.Read(gj.Replace("\"i\": 1, \"j\": 2", "\"i\": 1, \"j\": 20")); } catch (FormatException) { badTerm = true; }
                Check(badTerm, "a term with powers out of range is refused");
            }

            // complex lenses: the general model
            {
                var scenarios = FitScenarios(hh);
                Func<string, FitScenario> find = n => scenarios.Find(x => x.Name == n);
                foreach (string name in new[] { "barrel centre, pincushion sides", "anamorphic + smile", "decentred barrel" })
                {
                    var sc = find(name);
                    var lines = MarkedLines(sc.Lens, "level", W, H, F, 0.3, 5);
                    var old = LegacyDistortionFit.Fit(lines, W, H, F);
                    var sw = Stopwatch.StartNew();
                    var r = Desqueeze.Core.Lens.DistortionFit.Fit(lines, W, H, F);
                    long ms = sw.ElapsedMilliseconds;
                    double rms, worst, orms, oworst;
                    Straightness(sc.Lens, r.Model, hh, ppu, out rms, out worst, 1);
                    Straightness(sc.Lens, old.Model, hh, ppu, out orms, out oworst, 1);
                    Check(r.Rung >= 4 && r.RmsAfter < 0.6 && worst < 3.5 && worst < 0.4 * oworst,
                        "\"" + name + "\": marked lines straight to " + r.RmsAfter.ToString("0.00") + " px (radial-only fit: " + old.RmsAfter.ToString("0.00") +
                        "); level and upright lines anywhere bend at most " + worst.ToString("0.0") + " px (was " + oworst.ToString("0.0") + ")  [" + r.Kind + ", " + ms + " ms]");
                }
                {
                    // what level lines can't show follows the radial pattern: x^3 in the horizontal displacement = its x y^2 term
                    var r = Desqueeze.Core.Lens.DistortionFit.Fit(MarkedLines(find("barrel centre, pincushion sides").Lens, "level", W, H, F, 0.3, 5), W, H, F);
                    double x3 = 0, xy2 = 0, y3 = 0, x2y = 0;
                    foreach (var t in r.Model.Terms)
                    {
                        if (t.I == 3 && t.J == 0) x3 = t.X;
                        if (t.I == 1 && t.J == 2) xy2 = t.X;
                        if (t.I == 0 && t.J == 3) y3 = t.Y;
                        if (t.I == 2 && t.J == 1) x2y = t.Y;
                    }
                    Check(Math.Abs(xy2 + 0.02) < 0.003 && Math.Abs(x3 - xy2) < 0.004 && Math.Abs(x2y + 0.05) < 0.004 && Math.Abs(y3 - x2y) < 0.01,
                        "terms the lines can't see follow the radial pattern (x^3 " + x3.ToString("0.0000") + " ~ x y^2 " + xy2.ToString("0.0000") + ", y^3 " +
                        y3.ToString("0.0000") + " ~ x^2 y " + x2y.ToString("0.0000") + ")");
                }
                {
                    // lines at steep angles reveal them
                    var sc = find("barrel centre, pincushion sides");
                    var a1 = Desqueeze.Core.Lens.DistortionFit.Fit(MarkedLines(sc.Lens, "level", W, H, F, 0.3, 5), W, H, F);
                    var a2 = Desqueeze.Core.Lens.DistortionFit.Fit(MarkedLines(sc.Lens, "diag", W, H, F, 0.3, 5), W, H, F);
                    double r1, w1, r2, w2;
                    Straightness(sc.Lens, a1.Model, hh, ppu, out r1, out w1, 2);
                    Straightness(sc.Lens, a2.Model, hh, ppu, out r2, out w2, 2);
                    Check(r2 < 0.6 * r1 && r2 < 2.5, "a few diagonal lines straighten the diagonals too (" + r1.ToString("0.0") + " -> " + r2.ToString("0.0") + " px rms)");
                }
                {
                    // simple lenses keep the simple model, with or without noise
                    bool simple = true;
                    string kinds = "";
                    foreach (double noise in new[] { 0.0, 0.3, 1.0 })
                        foreach (string name in new[] { "radial barrel", "anamorphic barrel", "radial moustache" })
                        {
                            var r = Desqueeze.Core.Lens.DistortionFit.Fit(MarkedLines(find(name).Lens, "grid", W, H, F, noise, 9), W, H, F);
                            if (r.Rung >= 4) { simple = false; kinds += " " + name + "@" + noise + ":" + r.Rung; }
                        }
                    Check(simple, "radial and anamorphic lenses are still fitted with the radial models (no general terms without need)" + kinds);
                }
                {
                    // one direction only
                    var sc = find("barrel centre, pincushion sides");
                    var lines = MarkedLines(sc.Lens, "h-only", W, H, F, 0.3, 5);
                    var r = Desqueeze.Core.Lens.DistortionFit.Fit(lines, W, H, F);
                    var old = LegacyDistortionFit.Fit(lines, W, H, F);
                    Check(r.RmsAfter < 0.7 && old.RmsAfter > 3 * r.RmsAfter && r.Warning != null,
                        "horizontal lines alone are straightened whatever their shape (" + old.RmsAfter.ToString("0.00") + " -> " + r.RmsAfter.ToString("0.00") + " px), with a hint to add vertical ones");
                }
            }
        }

        private static void ViewTests()
        {
            Console.WriteLine("Viewer coordinates");
            var rnd = new Random(8);
            bool round = true, edges = true, pan = true, zoomOk = true, hover = true;
            foreach (int zoom in new[] { -8, -3, -2, 1, 2, 5, 16 })
                for (int k = 0; k < 200; k++)
                {
                    var t = new ViewTransform(zoom, rnd.NextDouble() * 6000, rnd.NextDouble() * 4000, 900 + rnd.Next(400), 500 + rnd.Next(300));
                    double x = rnd.NextDouble() * 6000 - 0.5, y = rnd.NextDouble() * 4000 - 0.5, vx, vy, bx, by;
                    t.ImageToView(x, y, out vx, out vy);
                    t.ViewToImage(vx, vy, out bx, out by);
                    if (Math.Abs(bx - x) > 1e-9 || Math.Abs(by - y) > 1e-9) round = false;
                    // a pixel's centre lies midway between the edges its tile is drawn with
                    int px = (int)Math.Round(x), py = (int)Math.Round(y);
                    t.ImageToView(px, py, out vx, out vy);
                    if (Math.Abs(vx - 0.5 * (t.EdgeToViewX(px) + t.EdgeToViewX(px + 1))) > 1e-9 || Math.Abs(vy - 0.5 * (t.EdgeToViewY(py) + t.EdgeToViewY(py + 1))) > 1e-9) edges = false;
                    if (t.OriginX != Math.Floor(t.OriginX) || t.OriginY != Math.Floor(t.OriginY)) edges = false;
                    // the pixel under a view position is the one whose tile covers it
                    int hx, hy;
                    t.PixelAt(vx, vy, out hx, out hy);
                    if (hx != px || hy != py) hover = false;
                    // dragging by (dx, dy) moves every image point by exactly (dx, dy) on screen
                    int dx = rnd.Next(-300, 300), dy = rnd.Next(-300, 300);
                    var p2 = t.Panned(dx, dy);
                    double wx, wy;
                    t.ImageToView(x, y, out vx, out vy);
                    p2.ImageToView(x, y, out wx, out wy);
                    if (Math.Abs(wx - vx - dx) > 1e-6 || Math.Abs(wy - vy - dy) > 1e-6) pan = false;
                    // zooming keeps the image point under the cursor within a pixel
                    double ax = rnd.NextDouble() * t.ViewWidth, ay = rnd.NextDouble() * t.ViewHeight, ix, iy, jx, jy;
                    t.ViewToImage(ax, ay, out ix, out iy);
                    var z = t.Zoomed(zoom == 16 ? 8 : zoom == -8 ? -6 : zoom + 1 == 0 ? 1 : zoom + 1 == -1 ? 1 : zoom + 1, ax, ay);
                    z.ImageToView(ix, iy, out jx, out jy);
                    if (Math.Abs(jx - ax) > 1.0 + 1e-9 || Math.Abs(jy - ay) > 1.0 + 1e-9) zoomOk = false;
                }
            Check(round, "view -> image -> view round trip at every zoom");
            Check(edges, "marks use the tiles' grid: a pixel's centre is midway between its edges, the origin a whole pixel");
            Check(hover, "the pixel under a position is the one drawn there");
            Check(pan, "panning moves marks exactly as far as the image");
            Check(zoomOk, "zooming at the cursor keeps the point under it (within a pixel)");

            // line runs
            bool runsOk = true;
            int most = 0;
            for (int k = 0; k < 2000; k++)
            {
                double ax = rnd.NextDouble() * 800, ay = rnd.NextDouble() * 600, len = 5 + rnd.NextDouble() * 700, ang = (rnd.NextDouble() - 0.5) * (k % 2 == 0 ? 0.2 : 6.283);
                double bx = ax + len * Math.Cos(ang), by = ay + len * Math.Sin(ang), w = 1 + rnd.Next(3);
                double covered = 0, prevTo = double.NaN, prevRow = double.NaN;
                bool major = Math.Abs(bx - ax) >= Math.Abs(by - ay), ok = true;
                int n = LineRaster.Runs(ax, ay, bx, by, w, (h, row, from, to) =>
                {
                    if (h != major || to <= from || row != Math.Floor(row)) ok = false;
                    if (!double.IsNaN(prevTo) && (Math.Abs(from - prevTo) > 1e-9 || Math.Abs(row - prevRow) != 1)) ok = false;   // contiguous, one row apart
                    // the run sits on the line: its centre across is within half a pixel of the line at both ends
                    foreach (double s in new[] { from, to })
                    {
                        double tt = major ? (s - ax) / (bx - ax) : (s - ay) / (by - ay);
                        double c = major ? ay + tt * (by - ay) : ax + tt * (bx - ax);
                        if (Math.Abs(row + w * 0.5 - c) > 0.5 + 1e-6) ok = false;
                    }
                    covered += to - from;
                    prevTo = to;
                    prevRow = row;
                });
                double extent = major ? Math.Abs(bx - ax) : Math.Abs(by - ay), cross = major ? Math.Abs(by - ay) : Math.Abs(bx - ax);
                if (!ok || Math.Abs(covered - extent) > 1e-6 || n > cross + 2 || n < 1) runsOk = false;
                most = Math.Max(most, n);
            }
            Check(runsOk, "lines split into contiguous runs that stay within half a pixel of the line, one per row crossed (at most " + most + " here)");
        }

        private static void TwoPassTests(string modelPath)
        {
            Console.WriteLine("Two-pass lens correction");
            var model = Desqueeze.Core.Resampling.AxisSRModel.Load(modelPath);
            var img = Pattern(90, 60, 3, 8, 21);
            // the learned widener at per-row positions equal to the uniform de-squeeze geometry = the plain widener
            var a = Desqueeze.Core.Resampling.AxisSR.UpscaleX(img, 135, model, new Desqueeze.Core.Resampling.AxisSROptions { OutputBitDepth = 16 });
            var b = Desqueeze.Core.Resampling.AxisSR.ResampleRows(img, 135, (row, pos) => { for (int x = 0; x < pos.Length; x++) pos[x] = (x + 0.5) * 90.0 / 135 - 0.5; },
                135.0 / 90, model, new Desqueeze.Core.Resampling.AxisSROptions { OutputBitDepth = 16 });
            Check(Equal(a.Data16, b.Data16), "widener at explicit per-row positions = the plain widener (bit-exact)");

            // flat field through an auto-cropped two-pass correction, 8 and 16 bit, with and without alpha
            bool flat = true;
            foreach (var ch in new[] { 3, 4 })
                foreach (var depth in new[] { 8, 16 })
                {
                    var f = new PixelImage(120, 80, ch, depth);
                    for (int i = 0; i < f.Width * f.Height * ch; i++)
                    {
                        bool al = ch == 4 && i % 4 == 3;
                        if (depth == 8) f.Data8[i] = al ? (byte)255 : (byte)118; else f.Data16[i] = al ? (ushort)65535 : (ushort)30000;
                    }
                    var m = Model(-0.06, -0.03, 0.01, 0.005);
                    double hh = 80.0 / (120 * 1.5);
                    var crop = Desqueeze.Core.Lens.LensCorrection.Canvas(m, hh, Desqueeze.Core.Lens.DistortionEdges.AutoCrop);
                    int ow, oh;
                    Desqueeze.Core.Lens.LensCorrection.CanvasSize(crop, 90, 90, out ow, out oh);
                    foreach (var hm in new[] { Desqueeze.Core.Lens.WarpPassMethod.Kernel, Desqueeze.Core.Lens.WarpPassMethod.Detail })
                    {
                        var o = Desqueeze.Core.Lens.TwoPassWarp.Correct(f, 1.5, m, crop, ow, oh, new Desqueeze.Core.Lens.TwoPassOptions { Horizontal = hm, DetailModel = model });
                        for (int i = 0; i < o.Width * o.Height * ch; i++)
                        {
                            bool al = ch == 4 && i % 4 == 3;
                            int v = depth == 8 ? o.Data8[i] : o.Data16[i], want = depth == 8 ? (al ? 255 : 118) : (al ? 65535 : 30000);
                            // the pixel where the crop touches the photo's edge is 99.99% covered: allow 0.01%
                            if (Math.Abs(v - want) > (depth == 8 ? 1 : 8)) { flat = false; break; }
                        }
                    }
                }
            Check(flat, "flat field stays flat through the two-pass correction (RGB/RGBA, 8/16-bit, learned and kernel rows)");

            // two-pass and 2-D warp agree on a smooth image, and both straighten what the lens bent
            var smooth = Pattern(300, 200, 3, 16, 4);
            var lm = Model(-0.05, -0.03);
            double h2 = 200.0 / 300;
            var fr = new Desqueeze.Core.Lens.CanvasRect(-1, -h2, 1, h2);
            var w2 = Desqueeze.Core.Lens.LensCorrection.Correct(smooth, 1, lm, fr, 300, 200, Desqueezer.DistortionWarpOptions(smooth, 16));
            var tp = Desqueeze.Core.Lens.TwoPassWarp.Correct(smooth, 1, lm, fr, 300, 200, new Desqueeze.Core.Lens.TwoPassOptions { Horizontal = Desqueeze.Core.Lens.WarpPassMethod.Kernel, OutputBitDepth = 16 });
            double p = Psnr8(w2, tp, 12);
            Check(p > 50, "two-pass and 2-D warp agree on a smooth image (" + p.ToString("0.0") + " dB)");

            // the same with a general model (asymmetric, 5th degree): flat stays flat, the two warps agree
            {
                var gm = Model(-0.03, -0.012);
                gm.Terms = new[]
                {
                    new Desqueeze.Core.Lens.DistortionTerm(1, 2, -0.02, 0), new Desqueeze.Core.Lens.DistortionTerm(2, 1, 0, -0.05), new Desqueeze.Core.Lens.DistortionTerm(4, 1, 0, 0.07),
                    new Desqueeze.Core.Lens.DistortionTerm(2, 0, 0.004, 0.006), new Desqueeze.Core.Lens.DistortionTerm(0, 2, -0.01, 0.003), new Desqueeze.Core.Lens.DistortionTerm(1, 1, 0.004, -0.004),
                };
                var w3 = Desqueeze.Core.Lens.LensCorrection.Correct(smooth, 1, gm, fr, 300, 200, Desqueezer.DistortionWarpOptions(smooth, 16));
                var t3 = Desqueeze.Core.Lens.TwoPassWarp.Correct(smooth, 1, gm, fr, 300, 200, new Desqueeze.Core.Lens.TwoPassOptions { Horizontal = Desqueeze.Core.Lens.WarpPassMethod.Kernel, OutputBitDepth = 16 });
                double pg = Psnr8(w3, t3, 12);
                var f8 = new PixelImage(120, 80, 3, 8);
                for (int i = 0; i < f8.Data8.Length; i++) f8.Data8[i] = 118;
                double hh = 80.0 / (120 * 1.5);
                var crop = Desqueeze.Core.Lens.LensCorrection.Canvas(gm, hh, Desqueeze.Core.Lens.DistortionEdges.AutoCrop);
                int ow, oh;
                Desqueeze.Core.Lens.LensCorrection.CanvasSize(crop, 90, 90, out ow, out oh);
                var o8 = Desqueeze.Core.Lens.TwoPassWarp.Correct(f8, 1.5, gm, crop, ow, oh, new Desqueeze.Core.Lens.TwoPassOptions { Horizontal = Desqueeze.Core.Lens.WarpPassMethod.Detail, DetailModel = model });
                bool gflat = true;
                foreach (byte v in o8.Data8) if (Math.Abs(v - 118) > 1) gflat = false;
                // the settings pipeline accepts it and reports the size it makes
                var gs = new DesqueezeSettings { Factor = 1.5, Distortion = gm };
                int ew, eh;
                Desqueezer.OutputSize(img, gs, out ew, out eh);
                var gr = Desqueezer.Process(img, gs);
                Check(pg > 50 && gflat && gr.Width == ew && gr.Height == eh,
                    "general model: two-pass and 2-D warp agree (" + pg.ToString("0.0") + " dB), flat stays flat when auto-cropped, the pipeline makes " + ew + " x " + eh);
            }
        }

        private static double Srgb8(double linear)
        {
            double v = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
            return v * 255;
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
