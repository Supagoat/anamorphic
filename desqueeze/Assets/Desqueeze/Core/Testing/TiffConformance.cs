using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Desqueeze.Core.Codecs;
using Desqueeze.Core.Imaging;

namespace Desqueeze.Core.Testing
{
    /// <summary>
    /// The TIFF conformance suite. <see cref="TiffCodec"/> is held to the files in Tools/TestData/tiff, which
    /// make_tests.py makes there with Pillow (libtiff) and its own writer, and checks against an independent Python
    /// implementation, libtiff, tifffile and ImageMagick:
    /// <list type="bullet">
    /// <item>reading: every layout (grey, grey + alpha, RGB, RGBA, palette; 8 and 16 bits; both byte orders; strips and
    /// tiles; interleaved and planar; none, LZW, ZIP and PackBits, with and without the predictor; pre-multiplied alpha,
    /// WhiteIsZero, a thumbnail first, an orientation tag with EXIF, GPS, IPTC and an ICC profile) gives the expected
    /// picture, upright, with its metadata;</item>
    /// <item>writing: uncompressed output is byte for byte the reference writer's; ZIP output reads back to the
    /// same file;</item>
    /// <item>round trips for every channel count and depth, conversions to and from PNG and JPEG with the metadata, files
    /// that must be refused, and damaged files (which must fail with a message, never with a crash).</item>
    /// </list>
    /// Run by the EditMode test TiffTests.
    /// </summary>
    public static class TiffConformance
    {
        public sealed class Report
        {
            public int Passed;
            public int Failed;
            public readonly StringBuilder Text = new StringBuilder();

            internal void Check(bool ok, string what)
            {
                Text.AppendLine((ok ? "PASS  " : "FAIL  ") + what);
                if (ok) Passed++;
                else Failed++;
            }
        }

        /// <param name="folder">Tools/TestData/tiff.</param>
        /// <param name="writeTo">When given, the ZIP-compressed TIFFs written here are saved in this folder too, to be
        /// checked with other readers.</param>
        public static Report Run(string folder, string writeTo = null)
        {
            var r = new Report();
            string manifest = Path.Combine(folder, "manifest.txt");
            if (!File.Exists(manifest))
            {
                r.Check(false, "the test files: " + manifest + " is missing");
                return r;
            }
            if (writeTo != null) Directory.CreateDirectory(writeTo);
            foreach (var line in File.ReadAllLines(manifest))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var f = line.Split('\t');
                try
                {
                    if (f[0] == "decode" && f.Length >= 8) DecodeCase(r, folder, f);
                    else if (f[0] == "encode" && f.Length >= 4) EncodeCase(r, folder, f, writeTo);
                    else if (f[0] == "reject" && f.Length >= 3) RejectCase(r, folder, f);
                }
                catch (Exception e)
                {
                    r.Check(false, f[0] + " " + (f.Length > 1 ? f[1] : "") + ": " + e.GetType().Name + ": " + e.Message);
                }
            }
            Guarded(r, "round trips", () => RoundTrips(r, writeTo));
            Guarded(r, "formats", () => Formats(r, folder));
            Guarded(r, "damaged files", () => Damaged(r, folder));
            return r;
        }

        private static void Guarded(Report r, string what, Action a)
        {
            try
            {
                a();
            }
            catch (Exception e)
            {
                r.Check(false, what + ": " + e.GetType().Name + ": " + e.Message);
            }
        }

        // ---------------------------------------------------------------------------- reading

        private static void DecodeCase(Report r, string folder, string[] f)
        {
            string name = f[1];
            byte[] data = File.ReadAllBytes(Path.Combine(folder, f[2]));
            var expected = PngCodec.Decode(File.ReadAllBytes(Path.Combine(folder, f[3])));
            var img = ImageIO.MakeUpright(TiffCodec.Decode(data));
            var meta = img.Metadata;
            var problems = new List<string>();
            if (ImageIO.DetectFormat(data) != ImageFileFormat.Tiff) problems.Add("not recognised as a TIFF");
            if (img.SourceFormat != ImageFileFormat.Tiff) problems.Add("source format " + img.SourceFormat);
            string diff = Compare(img, expected);
            if (diff != null) problems.Add(diff);
            if (f[4] != "-")
            {
                if (meta.SourceOrientation != int.Parse(f[4])) problems.Add("orientation " + meta.SourceOrientation + ", expected " + f[4]);
                if (ExifPatcher.ReadOrientation(meta.Exif) != Orientation.Normal) problems.Add("the EXIF orientation wasn't reset to 1");
            }
            int icc = meta.IccProfile != null ? meta.IccProfile.Length : 0;
            if (f[5] != "-" && icc != int.Parse(f[5])) problems.Add("ICC profile of " + icc + " bytes, expected " + f[5]);
            if (f[6] != "-")
                foreach (var s in f[6].Split('|'))
                    if (!Contains(meta.Exif, s)) problems.Add("the EXIF lacks \"" + s + "\"");
            if (f[7] != "-" && meta.Exif != null && meta.Exif.Length > int.Parse(f[7])) problems.Add("the EXIF is " + meta.Exif.Length + " bytes");
            r.Check(problems.Count == 0, "read " + name + " (" + Describe(img) + ")" + (problems.Count > 0 ? ": " + string.Join("; ", problems) : ""));
        }

        private static void RejectCase(Report r, string folder, string[] f)
        {
            byte[] data = File.ReadAllBytes(Path.Combine(folder, f[2]));
            try
            {
                var img = TiffCodec.Decode(data);
                r.Check(false, "refuse " + f[1] + ": it was read (" + Describe(img) + ")");
            }
            catch (Exception e) when (e is NotSupportedException || e is InvalidDataException)
            {
                r.Check(e.Message.Length > 0, "refuse " + f[1] + ": \"" + e.Message + "\"");
            }
        }

        // ---------------------------------------------------------------------------- writing

        private static void EncodeCase(Report r, string folder, string[] f, string writeTo)
        {
            string name = f[1];
            var img = TiffCodec.Decode(File.ReadAllBytes(Path.Combine(folder, f[2])));
            byte[] z = File.ReadAllBytes(Path.Combine(folder, f[3]));
            byte[] reference = Zlib.Decompress(z, 0, z.Length, -1);

            byte[] plain = TiffCodec.Encode(img, new TiffEncodeOptions { Compression = TiffCompression.None });
            int at = FirstDifference(plain, reference);
            r.Check(at < 0, "write " + name + " uncompressed: " + (at < 0
                ? "identical to the reference (" + plain.Length + " bytes)"
                : (plain.Length != reference.Length ? plain.Length + " bytes, expected " + reference.Length + "; " : "") + "the first difference is at byte " + at));

            byte[] zip = TiffCodec.Encode(img);
            if (writeTo != null) File.WriteAllBytes(Path.Combine(writeTo, "written-" + name + ".tif"), zip);
            var back = TiffCodec.Decode(zip);
            string diff = Compare(back, img);
            bool iccSame = SameBytes(back.Metadata.IccProfile, img.Metadata.IccProfile);
            // the ZIP file holds what the uncompressed one does: written again uncompressed, it gives the same bytes
            int again = FirstDifference(TiffCodec.Encode(back, new TiffEncodeOptions { Compression = TiffCompression.None }), plain);
            string problem = diff ?? (!iccSame ? "the ICC profile changed" : again >= 0 ? "it reads back to a different file (byte " + again + ")" : null);
            r.Check(problem == null, "write " + name + " with ZIP: " + (problem ?? zip.Length + " bytes (" + (100L * zip.Length / Math.Max(1, plain.Length)) + "% of uncompressed), reads back exactly"));
        }

        private static void RoundTrips(Report r, string writeTo)
        {
            foreach (int depth in new[] { 8, 16 })
                for (int ch = 1; ch <= 4; ch++)
                {
                    // 1031 x 300: several strips in each layout but one
                    var img = Pattern(1031, 300, ch, depth, 11 * ch + depth);
                    foreach (var c in new[] { TiffCompression.Deflate, TiffCompression.None })
                    {
                        byte[] data = TiffCodec.Encode(img, new TiffEncodeOptions { Compression = c });
                        var back = TiffCodec.Decode(data);
                        string diff = Compare(back, img);
                        r.Check(diff == null, "round trip, " + Describe(img) + ", " + (c == TiffCompression.Deflate ? "ZIP" : "uncompressed") + ": " + (diff ?? data.Length + " bytes"));
                        if (writeTo != null && c == TiffCompression.Deflate)
                            File.WriteAllBytes(Path.Combine(writeTo, "written-pattern-" + ch + "ch-" + depth + "bit.tif"), data);
                    }
                }
        }

        private static void Formats(Report r, string folder)
        {
            // names and extensions
            bool names = ImageIO.FormatFromExtension("a.tif") == ImageFileFormat.Tiff && ImageIO.FormatFromExtension("B.TIFF") == ImageFileFormat.Tiff &&
                         ImageIO.IsSupportedPath("c.Tif") && ImageIO.DefaultExtension(ImageFileFormat.Tiff) == ".tif" &&
                         Array.IndexOf(ImageIO.SupportedExtensions, ".tiff") >= 0;
            r.Check(names, ".tif and .tiff are recognised");

            // a portrait with an orientation tag, EXIF, GPS and an ICC profile, opened as the apps open it
            var src = ImageIO.MakeUpright(TiffCodec.Decode(File.ReadAllBytes(Path.Combine(folder, "test-rgb16-orient6-exif-icc-le.tif"))));
            var icc = src.Metadata.IccProfile;

            // TIFF -> TIFF, as Save As writes it: upright pixels, orientation 1, the rest of the metadata kept
            var t = ImageIO.MakeUpright(TiffCodec.Decode(ImageIO.Encode(src, ImageFileFormat.Tiff)));
            r.Check(Compare(t, src) == null && t.Metadata.SourceOrientation <= Orientation.Normal && SameBytes(t.Metadata.IccProfile, icc) &&
                    Contains(t.Metadata.Exif, "TestCam") && Contains(t.Metadata.Exif, "R98") && Contains(t.Metadata.Exif, "IPTC-caption"),
                "TIFF to TIFF: " + Describe(t) + ", upright, EXIF + GPS + IPTC + ICC kept" + Problem(Compare(t, src)));

            // TIFF -> PNG: 16 bits and the metadata kept
            var p = ImageIO.MakeUpright(PngCodec.Decode(ImageIO.Encode(src, ImageFileFormat.Png)));
            r.Check(Compare(p, src) == null && SameBytes(p.Metadata.IccProfile, icc) && Contains(p.Metadata.Exif, "TestCam"),
                "TIFF to PNG: " + Describe(p) + ", EXIF + ICC kept" + Problem(Compare(p, src)));

            // PNG -> TIFF: the PNG's EXIF becomes the TIFF's tags
            var t2 = ImageIO.MakeUpright(TiffCodec.Decode(ImageIO.Encode(p, ImageFileFormat.Tiff)));
            r.Check(Compare(t2, src) == null && SameBytes(t2.Metadata.IccProfile, icc) && Contains(t2.Metadata.Exif, "T-1 Mark II") &&
                    Contains(t2.Metadata.Exif, "2026:11:26 12:00:00"),
                "PNG to TIFF: " + Describe(t2) + ", EXIF + ICC kept" + Problem(Compare(t2, src)));

            // TIFF -> JPEG: the camera tags (orientation 1) and the ICC profile go along
            var info = JpegInfo.Read(ImageIO.Encode(src, ImageFileFormat.Jpeg));
            r.Check(info != null && info.Width == src.Width && info.Height == src.Height && Contains(info.Exif, "TestCam") &&
                    ExifPatcher.ReadOrientation(info.Exif) == Orientation.Normal && SameBytes(info.IccProfile, icc),
                "TIFF to JPEG: " + (info != null ? info.Width + " x " + info.Height : "unreadable") + ", EXIF + ICC kept");

            // an 8-bit RGBA TIFF to TIFF through ImageIO keeps 8 bits and the alpha
            var rgba = TiffCodec.Decode(File.ReadAllBytes(Path.Combine(folder, "test-pillow-rgba8-lzw.tif")));
            var rgbaBack = TiffCodec.Decode(ImageIO.Encode(rgba, ImageFileFormat.Tiff));
            r.Check(Compare(rgbaBack, rgba) == null, "8-bit RGBA TIFF to TIFF: " + Describe(rgbaBack) + Problem(Compare(rgbaBack, rgba)));
        }

        /// <summary>Cut short and changed files must fail with a message (InvalidDataException or NotSupportedException).</summary>
        private static void Damaged(Report r, string folder)
        {
            var rnd = new Random(7);
            int clean = 0, read = 0, bad = 0;
            var examples = new List<string>();
            foreach (var name in new[]
                     {
                         "test-rgb16-le-lzw-pred.tif", "test-pillow-palette8-lzw.tif", "test-rgba16-tiled-planar-lzw-pred.tif",
                         "test-rgb16-orient6-exif-icc-be.tif", "test-la16-le-packbits.tif", "test-rgba16-be-deflate-pred.tif",
                     })
            {
                string path = Path.Combine(folder, name);
                if (!File.Exists(path)) continue;
                byte[] data = File.ReadAllBytes(path);
                for (int k = 0; k < 60; k++)
                {
                    byte[] v;
                    if (k < 20)
                    {
                        v = new byte[(long)data.Length * k / 20];
                        Buffer.BlockCopy(data, 0, v, 0, v.Length);
                    }
                    else
                    {
                        v = (byte[])data.Clone();
                        int changes = 1 + rnd.Next(4);
                        for (int i = 0; i < changes; i++) v[rnd.Next(v.Length)] = (byte)rnd.Next(256);
                    }
                    try
                    {
                        TiffCodec.Decode(v);
                        read++;
                    }
                    catch (Exception e) when (e is NotSupportedException || e is InvalidDataException)
                    {
                        clean++;
                    }
                    catch (Exception e)
                    {
                        bad++;
                        if (examples.Count < 5)
                            examples.Add(name + " (" + (k < 20 ? "cut to " + v.Length + " bytes" : "bytes changed") + "): " + e.GetType().Name + ": " + e.Message);
                    }
                }
            }
            r.Check(bad == 0, "damaged files: " + clean + " refused with a message, " + read + " read anyway" +
                              (bad > 0 ? ", " + bad + " failed otherwise: " + string.Join("; ", examples) : ""));
        }

        // ---------------------------------------------------------------------------- helpers

        /// <summary>A deterministic picture: gradients, edges and noise, with an alpha that has fully clear and opaque parts.</summary>
        private static PixelImage Pattern(int w, int h, int ch, int depth, int seed)
        {
            var img = new PixelImage(w, h, ch, depth);
            uint s = (uint)seed * 2654435761u + 1;
            int max = depth == 16 ? 65535 : 255;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < ch; c++)
                    {
                        s = s * 1664525u + 1013904223u;
                        int v;
                        if (img.HasAlpha && c == ch - 1) v = y % 7 == 0 ? 0 : y % 7 == 1 ? max : x * max / (w - 1);
                        else if ((x + y + c) % 5 == 0) v = (int)(s >> 16) % (max + 1);
                        else v = (x * (c + 3) * 37 + y * 53) % (max + 1);
                        int i = (y * w + x) * ch + c;
                        if (depth == 8) img.Data8[i] = (byte)v;
                        else img.Data16[i] = (ushort)v;
                    }
            return img;
        }

        private static string Compare(PixelImage a, PixelImage e)
        {
            if (a.Width != e.Width || a.Height != e.Height || a.Channels != e.Channels || a.BitDepth != e.BitDepth)
                return "got " + Describe(a) + ", expected " + Describe(e);
            int n = a.Width * a.Height * a.Channels, bad = 0, first = -1;
            for (int i = 0; i < n; i++)
            {
                if (Sample(a, i) == Sample(e, i)) continue;
                if (bad++ == 0) first = i;
            }
            if (bad == 0) return null;
            int px = first / a.Channels;
            return bad + " samples differ, the first at x " + px % a.Width + ", y " + px / a.Width + ", channel " + first % a.Channels + ": " +
                   Sample(a, first) + " instead of " + Sample(e, first);
        }

        private static int Sample(PixelImage img, int i)
        {
            return img.BitDepth == 8 ? img.Data8[i] : img.Data16[i];
        }

        private static string Problem(string diff)
        {
            return diff != null ? ": " + diff : "";
        }

        private static string Describe(PixelImage img)
        {
            string model = img.Channels == 1 ? "grey" : img.Channels == 2 ? "grey + alpha" : img.Channels == 3 ? "RGB" : "RGBA";
            return img.Width + " x " + img.Height + " " + model + " " + img.BitDepth + "-bit";
        }

        private static bool Contains(byte[] hay, string text)
        {
            if (hay == null) return false;
            byte[] needle = Encoding.ASCII.GetBytes(text);
            for (int i = 0; i + needle.Length <= hay.Length; i++)
            {
                int k = 0;
                while (k < needle.Length && hay[i + k] == needle[k]) k++;
                if (k == needle.Length) return true;
            }
            return false;
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null) return a == b;
            return FirstDifference(a, b) < 0;
        }

        /// <summary>The first byte where the two differ (or the shorter one's length), or -1 when they're the same.</summary>
        private static int FirstDifference(byte[] a, byte[] b)
        {
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
                if (a[i] != b[i]) return i;
            return a.Length == b.Length ? -1 : n;
        }
    }
}
