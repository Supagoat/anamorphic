using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Desqueeze.Core.Imaging;

namespace Desqueeze.Core.Codecs
{
    public enum TiffCompression
    {
        /// <summary>Lossless ZIP (Adobe Deflate) with the horizontal predictor: about the size of a PNG, read by every current editor.</summary>
        Deflate,
        /// <summary>No compression: the largest files, readable by anything.</summary>
        None,
    }

    public sealed class TiffEncodeOptions
    {
        public TiffCompression Compression = TiffCompression.Deflate;
        public CompressionLevel Level = CompressionLevel.Optimal;
    }

    /// <summary>
    /// TIFF reading and writing (baseline TIFF 6.0).
    ///
    /// Reads 8- and 16-bit grey, grey + alpha, RGB, RGBA and 8-bit palette images; uncompressed, LZW, ZIP (Deflate)
    /// or PackBits, with or without the horizontal predictor; in strips or tiles, interleaved or planar, in either byte
    /// order. The first full-size image of the file is read (thumbnails and further pages are skipped). Pre-multiplied
    /// (associated) alpha is converted to the straight alpha the apps work in, and WhiteIsZero grey is inverted.
    ///
    /// The camera metadata travels as it does for JPEG: the descriptive tags of the image (make, model, date, artist,
    /// copyright, resolution, orientation) and its EXIF and GPS directories become <see cref="ImageMetadata.Exif"/>, and
    /// the ICC profile becomes <see cref="ImageMetadata.IccProfile"/>. Writing does the reverse.
    ///
    /// Writes little-endian, interleaved, 8 or 16 bits as the image is, in strips of about 1 MB, ZIP-compressed with the
    /// predictor (or uncompressed), with straight (unassociated) alpha.
    /// </summary>
    public static class TiffCodec
    {
        // ---- tags
        private const int TagNewSubfileType = 254;
        private const int TagImageWidth = 256;
        private const int TagImageLength = 257;
        private const int TagBitsPerSample = 258;
        private const int TagCompression = 259;
        private const int TagPhotometric = 262;
        private const int TagStripOffsets = 273;
        private const int TagOrientation = 274;
        private const int TagSamplesPerPixel = 277;
        private const int TagRowsPerStrip = 278;
        private const int TagStripByteCounts = 279;
        private const int TagXResolution = 282;
        private const int TagYResolution = 283;
        private const int TagPlanarConfig = 284;
        private const int TagResolutionUnit = 296;
        private const int TagPredictor = 317;
        private const int TagColorMap = 320;
        private const int TagTileWidth = 322;
        private const int TagTileLength = 323;
        private const int TagTileOffsets = 324;
        private const int TagTileByteCounts = 325;
        private const int TagExtraSamples = 338;
        private const int TagSampleFormat = 339;
        private const int TagIptc = 33723;
        private const int TagExifIfd = 34665;
        private const int TagIccProfile = 34675;
        private const int TagGpsIfd = 34853;
        private const int TagInteropIfd = 40965;

        // ---- field types
        private const int TypeByte = 1, TypeAscii = 2, TypeShort = 3, TypeLong = 4, TypeRational = 5, TypeSByte = 6,
            TypeUndefined = 7, TypeSShort = 8, TypeSLong = 9, TypeSRational = 10, TypeFloat = 11, TypeDouble = 12, TypeIfd = 13;

        /// <summary>
        /// The image's descriptive tags that travel with it as EXIF IFD0 (everything else in the image's directory
        /// describes the pixel layout of that particular file).
        /// </summary>
        private static readonly HashSet<int> DescriptiveTags = new HashSet<int>
        {
            269,            // DocumentName
            270,            // ImageDescription
            271,            // Make
            272,            // Model
            TagOrientation,
            TagXResolution,
            TagYResolution,
            TagResolutionUnit,
            305,            // Software
            306,            // DateTime
            315,            // Artist
            316,            // HostComputer
            33432,          // Copyright
            TagIptc,
            TagExifIfd,
            TagGpsIfd,
        };

        public static bool IsTiff(byte[] d)
        {
            if (d == null || d.Length < 8) return false;
            if (d[0] == 'I' && d[1] == 'I') return (d[2] == 42 || d[2] == 43) && d[3] == 0;
            if (d[0] == 'M' && d[1] == 'M') return d[2] == 0 && (d[3] == 42 || d[3] == 43);
            return false;
        }

        // ============================================================================ directories

        private sealed class Entry
        {
            public int Type;
            public long Count;
            /// <summary>The value's bytes, in the byte order of the file it came from.</summary>
            public byte[] Value;
            public bool Le;
        }

        private sealed class Ifd
        {
            public readonly Dictionary<int, Entry> Entries = new Dictionary<int, Entry>();
            public long Next;
        }

        private static int TypeSize(int type)
        {
            switch (type)
            {
                case TypeByte:
                case TypeAscii:
                case TypeSByte:
                case TypeUndefined:
                    return 1;
                case TypeShort:
                case TypeSShort:
                    return 2;
                case TypeLong:
                case TypeSLong:
                case TypeFloat:
                case TypeIfd:
                    return 4;
                case TypeRational:
                case TypeSRational:
                case TypeDouble:
                    return 8;
                default:
                    return 0;
            }
        }

        /// <summary>The size of the units that are byte-swapped between byte orders (a rational is two longs).</summary>
        private static int SwapUnit(int type)
        {
            switch (type)
            {
                case TypeShort:
                case TypeSShort:
                    return 2;
                case TypeLong:
                case TypeSLong:
                case TypeFloat:
                case TypeIfd:
                case TypeRational:
                case TypeSRational:
                    return 4;
                case TypeDouble:
                    return 8;
                default:
                    return 1;
            }
        }

        private static Ifd ReadIfd(byte[] d, long offset, bool le)
        {
            if (offset < 8 || offset + 2 > d.Length) throw new InvalidDataException("The TIFF's directory is outside the file.");
            int p = (int)offset;
            int n = U16(d, p, le);
            if (p + 2 + 12L * n > d.Length) throw new InvalidDataException("The TIFF's directory is truncated.");
            var ifd = new Ifd();
            for (int i = 0; i < n; i++)
            {
                int e = p + 2 + 12 * i;
                int tag = U16(d, e, le), type = U16(d, e + 2, le);
                long count = U32(d, e + 4, le);
                int size = TypeSize(type);
                if (size == 0) continue;                                // a type this reader doesn't know: skipped
                long bytes = count * size;
                if (bytes < 0 || bytes > int.MaxValue) continue;
                byte[] v;
                if (bytes <= 4)
                {
                    v = new byte[bytes];
                    Buffer.BlockCopy(d, e + 8, v, 0, (int)bytes);
                }
                else
                {
                    long at = U32(d, e + 8, le);
                    if (at + bytes > d.Length) continue;               // points outside the file: skipped
                    v = new byte[bytes];
                    Buffer.BlockCopy(d, (int)at, v, 0, (int)bytes);
                }
                ifd.Entries[tag] = new Entry { Type = type, Count = count, Value = v, Le = le };
            }
            int nextAt = p + 2 + 12 * n;
            ifd.Next = nextAt + 4 <= d.Length ? U32(d, nextAt, le) : 0;
            return ifd;
        }

        /// <summary>Value <paramref name="i"/> of an integer-valued entry.</summary>
        private static long Int(Entry e, int i)
        {
            switch (e.Type)
            {
                case TypeByte:
                case TypeUndefined:
                    return e.Value[i];
                case TypeSByte:
                    return (sbyte)e.Value[i];
                case TypeShort:
                    return U16(e.Value, 2 * i, e.Le);
                case TypeSShort:
                    return (short)U16(e.Value, 2 * i, e.Le);
                case TypeLong:
                case TypeIfd:
                    return U32(e.Value, 4 * i, e.Le);
                case TypeSLong:
                    return (int)U32(e.Value, 4 * i, e.Le);
                default:
                    throw new InvalidDataException("A TIFF field that should hold whole numbers holds type " + e.Type + ".");
            }
        }

        private static long[] Ints(Ifd ifd, int tag)
        {
            Entry e;
            if (!ifd.Entries.TryGetValue(tag, out e) || e.Count <= 0) return null;
            var r = new long[e.Count];
            for (int i = 0; i < r.Length; i++) r[i] = Int(e, i);
            return r;
        }

        private static long Int(Ifd ifd, int tag, long fallback)
        {
            Entry e;
            if (!ifd.Entries.TryGetValue(tag, out e) || e.Count <= 0) return fallback;
            return Int(e, 0);
        }

        private static long Required(Ifd ifd, int tag, string name)
        {
            Entry e;
            if (!ifd.Entries.TryGetValue(tag, out e) || e.Count <= 0) throw new InvalidDataException("The TIFF has no " + name + ".");
            return Int(e, 0);
        }

        // ============================================================================ reading

        public static PixelImage Decode(byte[] data)
        {
            try
            {
                return DecodeFile(data);
            }
            catch (Exception e) when (e is IndexOutOfRangeException || e is ArgumentException || e is OverflowException || e is NullReferenceException)
            {
                // a damaged file whose numbers point somewhere impossible
                throw new InvalidDataException("The TIFF file is damaged or incomplete.", e);
            }
        }

        private static PixelImage DecodeFile(byte[] data)
        {
            if (!IsTiff(data)) throw new InvalidDataException("Not a TIFF file.");
            bool le = data[0] == 'I';
            if (U16(data, 2, le) == 43) throw new NotSupportedException("BigTIFF files (over 4 GB) aren't supported.");

            // the first full-size image (skipping reduced-resolution copies such as thumbnails)
            Ifd main = null, first = null;
            long off = U32(data, 4, le);
            var seen = new HashSet<long>();
            while (off != 0 && seen.Add(off) && seen.Count <= 64)
            {
                var ifd = ReadIfd(data, off, le);
                if (first == null) first = ifd;
                if ((Int(ifd, TagNewSubfileType, 0) & 1) == 0)
                {
                    main = ifd;
                    break;
                }
                off = ifd.Next;
            }
            var m = main ?? first;
            if (m == null) throw new InvalidDataException("The TIFF has no image.");

            long w = Required(m, TagImageWidth, "width"), h = Required(m, TagImageLength, "height");
            if (w <= 0 || h <= 0 || w > 1 << 20 || h > 1 << 20) throw new InvalidDataException("The TIFF's size (" + w + " x " + h + ") isn't valid.");
            int width = (int)w, height = (int)h;
            int spp = (int)Int(m, TagSamplesPerPixel, 1);
            if (spp < 1 || spp > 32) throw new InvalidDataException("The TIFF has " + spp + " samples per pixel.");
            var bpsAll = Ints(m, TagBitsPerSample);
            int bits = bpsAll != null ? (int)bpsAll[0] : 1;
            if (bpsAll != null)
                for (int i = 1; i < bpsAll.Length && i < spp; i++)
                    if (bpsAll[i] != bits) throw new NotSupportedException("TIFFs with different bit depths per channel aren't supported.");
            if (bits != 8 && bits != 16)
                throw new NotSupportedException("This TIFF has " + bits + "-bit samples; 8- and 16-bit TIFFs are supported.");
            var formats = Ints(m, TagSampleFormat);
            if (formats != null && formats[0] != 1)
                throw new NotSupportedException(formats[0] == 3 ? "Floating-point TIFFs aren't supported; save the TIFF as 8 or 16 bits per channel."
                    : "TIFFs with signed samples aren't supported.");
            int compression = (int)Int(m, TagCompression, 1);
            if (compression != 1 && compression != 5 && compression != 8 && compression != 32946 && compression != 32773)
                throw new NotSupportedException(compression == 6 || compression == 7
                    ? "JPEG-compressed TIFFs aren't supported; save the TIFF uncompressed or with LZW or ZIP compression."
                    : "This TIFF's compression (type " + compression + ") isn't supported: uncompressed, LZW, ZIP and PackBits are.");
            int photometric = (int)Int(m, TagPhotometric, spp >= 3 ? 2 : 1);
            int colorSamples;
            switch (photometric)
            {
                case 0:     // WhiteIsZero
                case 1:     // BlackIsZero
                case 3:     // palette
                    colorSamples = 1;
                    break;
                case 2:
                    colorSamples = 3;
                    break;
                case 5:
                    throw new NotSupportedException("CMYK TIFFs aren't supported; convert the file to RGB first.");
                case 6:
                    throw new NotSupportedException("YCbCr TIFFs aren't supported; save the TIFF as RGB.");
                case 8:
                case 9:
                case 10:
                    throw new NotSupportedException("Lab TIFFs aren't supported; convert the file to RGB first.");
                default:
                    throw new NotSupportedException("This TIFF's colour model (type " + photometric + ") isn't supported.");
            }
            if (spp < colorSamples) throw new InvalidDataException("The TIFF has fewer samples than its colour model needs.");
            bool palette = photometric == 3;
            if (palette && bits != 8) throw new NotSupportedException("Only 8-bit palette TIFFs are supported.");
            int planar = (int)Int(m, TagPlanarConfig, 1);
            int predictor = (int)Int(m, TagPredictor, 1);
            if (predictor != 1 && predictor != 2)
                throw new NotSupportedException("This TIFF's predictor (type " + predictor + ") isn't supported.");

            // alpha: the first extra sample (straight, pre-multiplied, or unspecified - which is taken as straight alpha)
            bool hasAlpha = spp > colorSamples;
            var extras = Ints(m, TagExtraSamples);
            bool associated = hasAlpha && extras != null && extras[0] == 1;
            int outCh = (palette ? 3 : colorSamples) + (hasAlpha ? 1 : 0);
            if ((long)width * height * outCh > int.MaxValue) throw new NotSupportedException("This TIFF (" + width + " x " + height + ") is too large.");

            byte[] palR = null, palG = null, palB = null;
            if (palette)
            {
                var cm = Ints(m, TagColorMap);
                if (cm == null || cm.Length < 3 * 256) throw new InvalidDataException("The palette TIFF has no colour map.");
                palR = new byte[256];
                palG = new byte[256];
                palB = new byte[256];
                for (int i = 0; i < 256; i++)
                {
                    palR[i] = (byte)(cm[i] >> 8);
                    palG[i] = (byte)(cm[256 + i] >> 8);
                    palB[i] = (byte)(cm[512 + i] >> 8);
                }
            }

            // where the pixels are: tiles, or strips (strips are tiles as wide as the image)
            bool tiled = m.Entries.ContainsKey(TagTileWidth);
            int tw, th;
            long[] offsets, counts;
            if (tiled)
            {
                tw = (int)Required(m, TagTileWidth, "tile width");
                th = (int)Required(m, TagTileLength, "tile height");
                offsets = Ints(m, TagTileOffsets);
                counts = Ints(m, TagTileByteCounts);
            }
            else
            {
                tw = width;
                th = (int)Math.Min(Int(m, TagRowsPerStrip, uint.MaxValue), height);
                offsets = Ints(m, TagStripOffsets);
                counts = Ints(m, TagStripByteCounts);
            }
            if (tw <= 0 || th <= 0 || tw > 1 << 20 || th > 1 << 20) throw new InvalidDataException("The TIFF's strips or tiles have an impossible size.");
            if (offsets == null) throw new InvalidDataException("The TIFF has no image data.");
            int across = (width + tw - 1) / tw, down = (height + th - 1) / th;
            int planes = planar == 2 ? spp : 1;
            int blockSpp = planar == 2 ? 1 : spp;
            long blocks = (long)across * down * planes;
            if (offsets.Length < blocks) throw new InvalidDataException("The TIFF lists fewer strips or tiles than its image needs.");
            int bytesPerSample = bits / 8;

            var img = new PixelImage(width, height, outCh, bits == 16 && !palette ? 16 : 8)
            {
                SourceFormat = ImageFileFormat.Tiff,
                Metadata = new ImageMetadata(),
            };

            Parallel.For(0L, blocks, b =>
            {
                int plane = (int)(b / ((long)across * down));
                int r = (int)(b % ((long)across * down));
                int ty = r / across, tx = r % across;
                int x0 = tx * tw, y0 = ty * th;
                int rows = tiled ? th : Math.Min(th, height - y0);
                int cols = tw;
                int rowSamples = cols * blockSpp;
                long expected = (long)rows * rowSamples * bytesPerSample;
                long start = offsets[b];
                long avail = counts != null && b < counts.Length ? counts[b] : expected;
                if (start < 0 || start > data.Length) throw new InvalidDataException("A TIFF strip or tile is outside the file.");
                if (start + avail > data.Length) avail = data.Length - start;
                if (avail < 0) avail = 0;
                byte[] raw = Unpack(data, (int)start, (int)avail, expected, compression);

                // which output channel each sample of the block goes to: -1 dropped, -2 palette index
                var map = new int[blockSpp];
                for (int s = 0; s < blockSpp; s++)
                {
                    int fs = planar == 2 ? plane : s;
                    if (palette) map[s] = fs == 0 ? -2 : (hasAlpha && fs == 1 ? 3 : -1);
                    else if (fs < colorSamples) map[s] = fs;
                    else map[s] = hasAlpha && fs == colorSamples ? colorSamples : -1;
                }

                if (bits == 8)
                {
                    if (predictor == 2)
                        for (int y = 0; y < rows; y++)
                        {
                            int o = y * rowSamples;
                            for (int i = blockSpp; i < rowSamples; i++) raw[o + i] = (byte)(raw[o + i] + raw[o + i - blockSpp]);
                        }
                    var dst = img.Data8;
                    for (int y = 0; y < rows; y++)
                    {
                        int iy = y0 + y;
                        if (iy >= height) break;
                        int srcRow = y * rowSamples;
                        for (int x = 0; x < cols; x++)
                        {
                            int ix = x0 + x;
                            if (ix >= width) break;
                            int si = srcRow + x * blockSpp, di = (iy * width + ix) * outCh;
                            for (int s = 0; s < blockSpp; s++)
                            {
                                int c = map[s];
                                byte v = raw[si + s];
                                if (c >= 0) dst[di + c] = v;
                                else if (c == -2)
                                {
                                    dst[di] = palR[v];
                                    dst[di + 1] = palG[v];
                                    dst[di + 2] = palB[v];
                                }
                            }
                        }
                    }
                }
                else
                {
                    int n = rows * rowSamples;
                    var vals = new ushort[n];
                    for (int i = 0; i < n; i++)
                        vals[i] = le ? (ushort)(raw[2 * i] | (raw[2 * i + 1] << 8)) : (ushort)((raw[2 * i] << 8) | raw[2 * i + 1]);
                    if (predictor == 2)
                        for (int y = 0; y < rows; y++)
                        {
                            int o = y * rowSamples;
                            for (int i = blockSpp; i < rowSamples; i++) vals[o + i] = (ushort)(vals[o + i] + vals[o + i - blockSpp]);
                        }
                    var dst = img.Data16;
                    for (int y = 0; y < rows; y++)
                    {
                        int iy = y0 + y;
                        if (iy >= height) break;
                        int srcRow = y * rowSamples;
                        for (int x = 0; x < cols; x++)
                        {
                            int ix = x0 + x;
                            if (ix >= width) break;
                            int si = srcRow + x * blockSpp, di = (iy * width + ix) * outCh;
                            for (int s = 0; s < blockSpp; s++)
                            {
                                int c = map[s];
                                if (c >= 0) dst[di + c] = vals[si + s];
                            }
                        }
                    }
                }
            }, "Reading the TIFF");

            if (photometric == 0) InvertGrey(img);
            if (associated) Unpremultiply(img);

            Entry icc;
            if (m.Entries.TryGetValue(TagIccProfile, out icc) && icc.Value.Length > 0) img.Metadata.IccProfile = (byte[])icc.Value.Clone();
            try
            {
                img.Metadata.Exif = BuildExif(data, le, m);
            }
            catch (Exception)
            {
                img.Metadata.Exif = null;          // damaged metadata never stops the picture from opening
            }
            return img;
        }

        private static void InvertGrey(PixelImage img)
        {
            int n = img.Width * img.Height, ch = img.Channels;
            if (img.BitDepth == 8)
                for (int i = 0; i < n; i++) img.Data8[i * ch] = (byte)(255 - img.Data8[i * ch]);
            else
                for (int i = 0; i < n; i++) img.Data16[i * ch] = (ushort)(65535 - img.Data16[i * ch]);
        }

        /// <summary>Pre-multiplied colour to straight colour.</summary>
        private static void Unpremultiply(PixelImage img)
        {
            int n = img.Width * img.Height, ch = img.Channels, cc = ch - 1;
            long max = img.MaxValue;
            for (int i = 0; i < n; i++)
            {
                int o = i * ch;
                long a = img.BitDepth == 8 ? img.Data8[o + cc] : img.Data16[o + cc];
                for (int c = 0; c < cc; c++)
                {
                    long v = img.BitDepth == 8 ? img.Data8[o + c] : img.Data16[o + c];
                    long s = a == 0 ? 0 : Math.Min(max, (v * max + a / 2) / a);
                    if (img.BitDepth == 8) img.Data8[o + c] = (byte)s;
                    else img.Data16[o + c] = (ushort)s;
                }
            }
        }

        /// <summary>One strip or tile, decompressed to <paramref name="expected"/> bytes (short data is padded with zeros).</summary>
        private static byte[] Unpack(byte[] d, int start, int count, long expected, int compression)
        {
            if (expected > int.MaxValue) throw new NotSupportedException("A TIFF strip or tile is too large.");
            var r = new byte[expected];
            switch (compression)
            {
                case 1:
                    Buffer.BlockCopy(d, start, r, 0, (int)Math.Min(count, expected));
                    break;
                case 5:
                    LzwDecode(d, start, count, r);
                    break;
                case 8:
                case 32946:
                {
                    byte[] z = Zlib.Decompress(d, start, count, -1);
                    Buffer.BlockCopy(z, 0, r, 0, (int)Math.Min(z.Length, expected));
                    break;
                }
                case 32773:
                    PackBitsDecode(d, start, count, r);
                    break;
            }
            return r;
        }

        /// <summary>TIFF LZW: codes of 9 to 12 bits, most significant bit first, widening one code early.</summary>
        private static void LzwDecode(byte[] src, int off, int len, byte[] dst)
        {
            const int Clear = 256, Eoi = 257;
            if (len >= 2 && src[off] == 0 && (src[off + 1] & 1) != 0)
                throw new NotSupportedException("This TIFF uses the old, pre-1992 form of LZW compression, which isn't supported.");
            var prefix = new short[4096];
            var suffix = new byte[4096];
            var firstByte = new byte[4096];
            var length = new int[4096];
            for (int i = 0; i < 256; i++)
            {
                prefix[i] = -1;
                suffix[i] = (byte)i;
                firstByte[i] = (byte)i;
                length[i] = 1;
            }
            int width = 9, next = 258, prev = -1, outPos = 0;
            int bitBuf = 0, bitCount = 0, p = off, end = off + len;
            while (outPos < dst.Length)
            {
                while (bitCount < width)
                {
                    if (p >= end) return;                               // the data ended without an end code
                    bitBuf = (bitBuf << 8) | src[p++];
                    bitCount += 8;
                }
                int code = (bitBuf >> (bitCount - width)) & ((1 << width) - 1);
                bitCount -= width;
                bitBuf &= (1 << bitCount) - 1;
                if (code == Eoi) return;
                if (code == Clear)
                {
                    width = 9;
                    next = 258;
                    prev = -1;
                    continue;
                }
                if (prev < 0)
                {
                    if (code > 255) throw new InvalidDataException("The TIFF's LZW data is damaged.");
                    dst[outPos++] = (byte)code;
                    prev = code;
                    continue;
                }
                int str;
                if (code < next) str = code;
                else if (code == next) str = -1;                        // the string being defined (KwKwK)
                else throw new InvalidDataException("The TIFF's LZW data is damaged.");
                if (next < 4096)
                {
                    prefix[next] = (short)prev;
                    suffix[next] = str >= 0 ? firstByte[str] : firstByte[prev];
                    firstByte[next] = firstByte[prev];
                    length[next] = length[prev] + 1;
                    next++;
                    if (next >= (1 << width) - 1 && width < 12) width++;
                }
                if (str < 0) str = next - 1;
                int l = length[str];
                int pos = outPos + l - 1;
                for (int c = str; c >= 0; c = prefix[c], pos--)
                    if (pos < dst.Length) dst[pos] = suffix[c];
                outPos += l;
                prev = code;
            }
        }

        private static void PackBitsDecode(byte[] src, int off, int len, byte[] dst)
        {
            int p = off, end = off + len, o = 0;
            while (o < dst.Length && p < end)
            {
                int n = (sbyte)src[p++];
                if (n >= 0)
                {
                    int c = Math.Min(n + 1, Math.Min(end - p, dst.Length - o));
                    Buffer.BlockCopy(src, p, dst, o, c);
                    p += n + 1;
                    o += c;
                }
                else if (n != -128)
                {
                    if (p >= end) break;
                    byte v = src[p++];
                    int c = Math.Min(1 - n, dst.Length - o);
                    for (int i = 0; i < c; i++) dst[o + i] = v;
                    o += c;
                }
            }
        }

        // ============================================================================ metadata

        /// <summary>A directory to write: entries (little-endian values) and sub-directories, by tag.</summary>
        private sealed class OutIfd
        {
            public readonly SortedDictionary<int, Entry> Entries = new SortedDictionary<int, Entry>();
            public readonly SortedDictionary<int, OutIfd> Children = new SortedDictionary<int, OutIfd>();
            public bool Empty => Entries.Count == 0 && Children.Count == 0;
        }

        private static bool IsSubIfd(int tag)
        {
            return tag == TagExifIfd || tag == TagGpsIfd || tag == TagInteropIfd;
        }

        /// <summary>A copy of <paramref name="src"/> for writing: the tags <paramref name="keep"/> allows, values made little-endian, sub-directories copied.</summary>
        private static OutIfd Copy(byte[] data, bool le, Ifd src, HashSet<int> keep, int depth)
        {
            var o = new OutIfd();
            foreach (var kv in src.Entries)
            {
                int tag = kv.Key;
                var e = kv.Value;
                if (keep != null && !keep.Contains(tag)) continue;
                if (IsSubIfd(tag))
                {
                    if (depth >= 3 || e.Count < 1 || (e.Type != TypeLong && e.Type != TypeIfd)) continue;
                    try
                    {
                        var child = Copy(data, le, ReadIfd(data, Int(e, 0), le), null, depth + 1);
                        if (!child.Empty) o.Children[tag] = child;
                    }
                    catch (Exception)
                    {
                        // an unreadable sub-directory is left out
                    }
                    continue;
                }
                o.Entries[tag] = LittleEndian(tag, e);
            }
            return o;
        }

        private static Entry LittleEndian(int tag, Entry e)
        {
            if (tag == TagIptc)          // IPTC is a byte stream, whatever type it is filed under: never swapped
                return new Entry { Type = TypeUndefined, Count = e.Value.Length, Value = (byte[])e.Value.Clone(), Le = true };
            var v = (byte[])e.Value.Clone();
            int unit = SwapUnit(e.Type);
            if (!e.Le && unit > 1)
                for (int i = 0; i + unit <= v.Length; i += unit) Array.Reverse(v, i, unit);
            return new Entry { Type = e.Type, Count = e.Count, Value = v, Le = true };
        }

        /// <summary>The most EXIF a JPEG can carry (one APP1 segment); a larger block is written without the IPTC record.</summary>
        private const int MaxExifBytes = 65000;

        /// <summary>The image's descriptive tags and its EXIF and GPS directories as an EXIF block (a little-endian TIFF structure).</summary>
        private static byte[] BuildExif(byte[] data, bool le, Ifd image)
        {
            var root = Copy(data, le, image, DescriptiveTags, 0);
            if (root.Empty) return null;
            byte[] exif = ExifBlock(root);
            if (exif.Length > MaxExifBytes && root.Entries.Remove(TagIptc))
                exif = root.Empty ? null : ExifBlock(root);
            return exif;
        }

        private static byte[] ExifBlock(OutIfd root)
        {
            using (var ms = new MemoryStream())
            {
                ms.Write(new byte[] { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0 }, 0, 8);
                WriteIfd(ms, root);
                return ms.ToArray();
            }
        }

        /// <summary>The descriptive tags and sub-directories of an EXIF block, for a TIFF being written (null when there are none or it can't be read).</summary>
        private static OutIfd FromExif(byte[] exif)
        {
            try
            {
                if (exif == null || exif.Length < 8) return null;
                bool le;
                if (exif[0] == 'I' && exif[1] == 'I') le = true;
                else if (exif[0] == 'M' && exif[1] == 'M') le = false;
                else return null;
                if (U16(exif, 2, le) != 42) return null;
                return Copy(exif, le, ReadIfd(exif, U32(exif, 4, le), le), DescriptiveTags, 0);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Writes a directory (and its sub-directories after it); returns where it starts.</summary>
        private static long WriteIfd(MemoryStream ms, OutIfd ifd)
        {
            if ((ms.Position & 1) != 0) ms.WriteByte(0);
            var tags = new List<int>(ifd.Entries.Keys);
            foreach (var k in ifd.Children.Keys)
                if (!ifd.Entries.ContainsKey(k)) tags.Add(k);
            tags.Sort();
            int n = tags.Count;
            long at = ms.Position;
            var dir = new byte[2 + 12 * n + 4];
            W16(dir, 0, n);
            ms.Write(dir, 0, dir.Length);                              // room for it; filled in below
            var childSlots = new List<KeyValuePair<int, int>>();
            for (int i = 0; i < n; i++)
            {
                int tag = tags[i], e = 2 + 12 * i;
                W16(dir, e, tag);
                if (ifd.Children.ContainsKey(tag))
                {
                    W16(dir, e + 2, TypeLong);
                    W32(dir, e + 4, 1);
                    childSlots.Add(new KeyValuePair<int, int>(tag, e + 8));
                    continue;
                }
                var en = ifd.Entries[tag];
                W16(dir, e + 2, en.Type);
                W32(dir, e + 4, (uint)en.Count);
                if (en.Value.Length <= 4)
                {
                    Buffer.BlockCopy(en.Value, 0, dir, e + 8, en.Value.Length);
                }
                else
                {
                    if ((ms.Position & 1) != 0) ms.WriteByte(0);
                    W32(dir, e + 8, (uint)ms.Position);
                    ms.Write(en.Value, 0, en.Value.Length);
                }
            }
            foreach (var slot in childSlots)
            {
                long c = WriteIfd(ms, ifd.Children[slot.Key]);
                W32(dir, slot.Value, (uint)c);
            }
            long endPos = ms.Position;
            ms.Position = at;
            ms.Write(dir, 0, dir.Length);
            ms.Position = endPos;
            return at;
        }

        private static Entry Shorts(params int[] v)
        {
            var b = new byte[2 * v.Length];
            for (int i = 0; i < v.Length; i++) W16(b, 2 * i, v[i]);
            return new Entry { Type = TypeShort, Count = v.Length, Value = b, Le = true };
        }

        private static Entry Longs(params long[] v)
        {
            var b = new byte[4 * v.Length];
            for (int i = 0; i < v.Length; i++) W32(b, 4 * i, (uint)v[i]);
            return new Entry { Type = TypeLong, Count = v.Length, Value = b, Le = true };
        }

        private static Entry Rational(uint num, uint den)
        {
            var b = new byte[8];
            W32(b, 0, num);
            W32(b, 4, den);
            return new Entry { Type = TypeRational, Count = 1, Value = b, Le = true };
        }

        // ============================================================================ writing

        public static byte[] Encode(PixelImage img, TiffEncodeOptions options = null)
        {
            if (img == null) throw new ArgumentNullException(nameof(img));
            options = options ?? new TiffEncodeOptions();
            int w = img.Width, h = img.Height, ch = img.Channels, bits = img.BitDepth;
            bool deflate = options.Compression == TiffCompression.Deflate;
            int rowSamples = w * ch;
            long rowBytes = (long)rowSamples * (bits / 8);
            int rowsPerStrip = (int)Math.Max(1, Math.Min(h, (1L << 20) / Math.Max(1, rowBytes)));
            int strips = (h + rowsPerStrip - 1) / rowsPerStrip;
            var stripData = new byte[strips][];
            var level = options.Level;

            Parallel.For(0L, strips, sl =>
            {
                int s = (int)sl;
                int y0 = s * rowsPerStrip, rows = Math.Min(rowsPerStrip, h - y0);
                var raw = new byte[rows * rowBytes];
                if (bits == 8)
                {
                    Buffer.BlockCopy(img.Data8, y0 * rowSamples, raw, 0, rows * rowSamples);
                    if (deflate)
                        for (int y = 0; y < rows; y++)
                        {
                            int o = y * rowSamples;
                            for (int i = rowSamples - 1; i >= ch; i--) raw[o + i] = (byte)(raw[o + i] - raw[o + i - ch]);
                        }
                }
                else
                {
                    var src = img.Data16;
                    for (int y = 0; y < rows; y++)
                    {
                        int si = (y0 + y) * rowSamples, o = y * rowSamples * 2;
                        for (int i = 0; i < rowSamples; i++)
                        {
                            int v = src[si + i];
                            if (deflate && i >= ch) v = (v - src[si + i - ch]) & 0xFFFF;
                            raw[o + 2 * i] = (byte)v;
                            raw[o + 2 * i + 1] = (byte)(v >> 8);
                        }
                    }
                }
                stripData[s] = deflate ? Zlib.Compress(raw, 0, raw.Length, level) : raw;
            }, "Writing the TIFF");

            long total = 8;
            foreach (var sd in stripData) total += sd.Length + 1;
            if (total + (1 << 20) > int.MaxValue)
                throw new NotSupportedException("This image is too large to save as TIFF (over 2 GB); save it as PNG instead.");

            using (var ms = new MemoryStream((int)Math.Min(int.MaxValue, total + 65536)))
            {
                ms.Write(new byte[] { (byte)'I', (byte)'I', 42, 0, 0, 0, 0, 0 }, 0, 8);
                var stripOffsets = new long[strips];
                var stripCounts = new long[strips];
                for (int s = 0; s < strips; s++)
                {
                    if ((ms.Position & 1) != 0) ms.WriteByte(0);
                    stripOffsets[s] = ms.Position;
                    stripCounts[s] = stripData[s].Length;
                    ms.Write(stripData[s], 0, stripData[s].Length);
                    stripData[s] = null;
                }

                // the image's directory: its description from the metadata, then its own layout (which wins)
                var meta = img.Metadata;
                var root = (meta != null ? FromExif(meta.Exif) : null) ?? new OutIfd();
                root.Entries[TagNewSubfileType] = Longs(0);
                root.Entries[TagImageWidth] = Longs(w);
                root.Entries[TagImageLength] = Longs(h);
                var bps = new int[ch];
                for (int c = 0; c < ch; c++) bps[c] = bits;
                root.Entries[TagBitsPerSample] = Shorts(bps);
                root.Entries[TagCompression] = Shorts(deflate ? 8 : 1);
                root.Entries[TagPhotometric] = Shorts(ch <= 2 ? 1 : 2);
                root.Entries[TagStripOffsets] = Longs(stripOffsets);
                root.Entries[TagOrientation] = Shorts(1);              // the pixels are upright
                root.Entries[TagSamplesPerPixel] = Shorts(ch);
                root.Entries[TagRowsPerStrip] = Longs(rowsPerStrip);
                root.Entries[TagStripByteCounts] = Longs(stripCounts);
                if (!root.Entries.ContainsKey(TagXResolution) || !root.Entries.ContainsKey(TagYResolution))
                {
                    root.Entries[TagXResolution] = Rational(72, 1);
                    root.Entries[TagYResolution] = Rational(72, 1);
                    root.Entries[TagResolutionUnit] = Shorts(2);
                }
                root.Entries[TagPlanarConfig] = Shorts(1);
                if (deflate) root.Entries[TagPredictor] = Shorts(2);
                if (img.HasAlpha) root.Entries[TagExtraSamples] = Shorts(2);   // straight (unassociated) alpha
                var fmt = new int[ch];
                for (int c = 0; c < ch; c++) fmt[c] = 1;
                root.Entries[TagSampleFormat] = Shorts(fmt);
                if (meta != null && meta.IccProfile != null && meta.IccProfile.Length > 0)
                    root.Entries[TagIccProfile] = new Entry { Type = TypeUndefined, Count = meta.IccProfile.Length, Value = meta.IccProfile, Le = true };

                long ifd = WriteIfd(ms, root);
                ms.Position = 4;
                var p = new byte[4];
                W32(p, 0, (uint)ifd);
                ms.Write(p, 0, 4);
                return ms.ToArray();
            }
        }

        // ============================================================================ helpers

        /// <summary>Parallel.For that rethrows the first failure itself (not an AggregateException), so its message reaches the user.</summary>
        private static class Parallel
        {
            public static void For(long from, long to, Action<long> body, string what)
            {
                try
                {
                    System.Threading.Tasks.Parallel.For(from, to, body);
                }
                catch (AggregateException ae)
                {
                    var inner = ae.Flatten().InnerExceptions;
                    if (inner.Count > 0) ExceptionDispatchInfo.Capture(inner[0]).Throw();
                    throw new InvalidDataException(what + " failed.");
                }
            }
        }

        private static int U16(byte[] b, int p, bool le)
        {
            return le ? b[p] | (b[p + 1] << 8) : (b[p] << 8) | b[p + 1];
        }

        private static long U32(byte[] b, int p, bool le)
        {
            return le
                ? (uint)(b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24))
                : (uint)((b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3]);
        }

        private static void W16(byte[] b, int p, int v)
        {
            b[p] = (byte)v;
            b[p + 1] = (byte)(v >> 8);
        }

        private static void W32(byte[] b, int p, uint v)
        {
            b[p] = (byte)v;
            b[p + 1] = (byte)(v >> 8);
            b[p + 2] = (byte)(v >> 16);
            b[p + 3] = (byte)(v >> 24);
        }
    }
}
