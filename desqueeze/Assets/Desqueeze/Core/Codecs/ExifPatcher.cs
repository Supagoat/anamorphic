using System;

namespace Desqueeze.Core.Codecs
{
    /// <summary>
    /// Updates the pixel-dimension tags inside an EXIF (TIFF) block so they match the de-squeezed
    /// image. Everything else - camera, lens, exposure, orientation, GPS - is left untouched.
    /// The block is patched in place on a copy; if it cannot be parsed it is returned unchanged.
    /// </summary>
    public static class ExifPatcher
    {
        private const int TagImageWidth = 0x0100;
        private const int TagImageLength = 0x0101;
        private const int TagExifIfd = 0x8769;
        private const int TagPixelXDimension = 0xA002;
        private const int TagPixelYDimension = 0xA003;

        public static byte[] WithDimensions(byte[] exif, int width, int height)
        {
            if (exif == null || exif.Length < 8) return exif;
            var t = (byte[])exif.Clone();
            try
            {
                bool le;
                if (t[0] == 'I' && t[1] == 'I') le = true;
                else if (t[0] == 'M' && t[1] == 'M') le = false;
                else return exif;
                if (U16(t, 2, le) != 42) return exif;
                int ifd0 = (int)U32(t, 4, le);
                int exifIfd = -1;
                PatchIfd(t, ifd0, le, width, height, TagImageWidth, TagImageLength, ref exifIfd);
                if (exifIfd > 0)
                {
                    int dummy = -1;
                    PatchIfd(t, exifIfd, le, width, height, TagPixelXDimension, TagPixelYDimension, ref dummy);
                }
                return t;
            }
            catch (Exception)
            {
                return exif;
            }
        }

        private static void PatchIfd(byte[] t, int ifd, bool le, int width, int height, int tagW, int tagH, ref int exifIfd)
        {
            if (ifd < 8 || ifd + 2 > t.Length) return;
            int count = U16(t, ifd, le);
            for (int i = 0; i < count; i++)
            {
                int e = ifd + 2 + i * 12;
                if (e + 12 > t.Length) return;
                int tag = U16(t, e, le);
                int type = U16(t, e + 2, le);
                uint n = U32(t, e + 4, le);
                if (tag == TagExifIfd && (type == 4 || type == 13) && n == 1)
                {
                    exifIfd = (int)U32(t, e + 8, le);
                }
                else if ((tag == tagW || tag == tagH) && n == 1)
                {
                    int v = tag == tagW ? width : height;
                    if (type == 3 && v <= 0xFFFF)
                    {
                        W16(t, e + 8, le, v);
                    }
                    else if (type == 4)
                    {
                        W32(t, e + 8, le, (uint)v);
                    }
                }
            }
        }

        private static int U16(byte[] b, int p, bool le)
        {
            return le ? b[p] | (b[p + 1] << 8) : (b[p] << 8) | b[p + 1];
        }

        private static uint U32(byte[] b, int p, bool le)
        {
            return le
                ? (uint)(b[p] | (b[p + 1] << 8) | (b[p + 2] << 16) | (b[p + 3] << 24))
                : (uint)((b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3]);
        }

        private static void W16(byte[] b, int p, bool le, int v)
        {
            if (le)
            {
                b[p] = (byte)v;
                b[p + 1] = (byte)(v >> 8);
            }
            else
            {
                b[p] = (byte)(v >> 8);
                b[p + 1] = (byte)v;
            }
        }

        private static void W32(byte[] b, int p, bool le, uint v)
        {
            if (le)
            {
                b[p] = (byte)v;
                b[p + 1] = (byte)(v >> 8);
                b[p + 2] = (byte)(v >> 16);
                b[p + 3] = (byte)(v >> 24);
            }
            else
            {
                b[p] = (byte)(v >> 24);
                b[p + 1] = (byte)(v >> 16);
                b[p + 2] = (byte)(v >> 8);
                b[p + 3] = (byte)v;
            }
        }
    }
}
