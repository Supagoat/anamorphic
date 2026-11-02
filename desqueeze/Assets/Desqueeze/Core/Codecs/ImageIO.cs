using System;
using System.IO;
using Desqueeze.Core.Imaging;

namespace Desqueeze.Core.Codecs
{
    /// <summary>Format detection and encoding to the output formats (PNG / max-quality JPEG).</summary>
    public static class ImageIO
    {
        public static readonly string[] SupportedExtensions = { ".jpg", ".jpeg", ".png" };

        public static ImageFileFormat DetectFormat(byte[] data)
        {
            if (PngCodec.IsPng(data)) return ImageFileFormat.Png;
            if (JpegInfo.IsJpeg(data)) return ImageFileFormat.Jpeg;
            return ImageFileFormat.Unknown;
        }

        public static ImageFileFormat FormatFromExtension(string path)
        {
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            if (ext == ".png") return ImageFileFormat.Png;
            if (ext == ".jpg" || ext == ".jpeg") return ImageFileFormat.Jpeg;
            return ImageFileFormat.Unknown;
        }

        public static bool IsSupportedPath(string path)
        {
            return FormatFromExtension(path) != ImageFileFormat.Unknown;
        }

        public static string DefaultExtension(ImageFileFormat f)
        {
            return f == ImageFileFormat.Png ? ".png" : ".jpg";
        }

        /// <summary>
        /// Encodes an image. JPEG is always written at maximum quality (Q100, 4:4:4, optimised Huffman).
        /// EXIF pixel-dimension tags are updated to the image's size; ICC/EXIF/ancillary data is preserved.
        /// </summary>
        public static byte[] Encode(PixelImage img, ImageFileFormat format)
        {
            if (img == null) throw new ArgumentNullException(nameof(img));
            var original = img.Metadata;
            if (original != null && original.Exif != null)
            {
                // Encode from a shallow wrapper so the caller's metadata is not modified.
                var meta = original.Clone();
                meta.Exif = ExifPatcher.WithDimensions(meta.Exif, img.Width, img.Height);
                img.Metadata = meta;
                try
                {
                    return EncodeRaw(img, format);
                }
                finally
                {
                    img.Metadata = original;
                }
            }
            return EncodeRaw(img, format);
        }

        private static byte[] EncodeRaw(PixelImage img, ImageFileFormat format)
        {
            switch (format)
            {
                case ImageFileFormat.Png:
                    return PngCodec.Encode(img);
                case ImageFileFormat.Jpeg:
                    return JpegEncoder.Encode(img, new JpegEncodeOptions { Quality = 100 });
                default:
                    throw new ArgumentException("Unsupported output format " + format + ".");
            }
        }

        /// <summary>
        /// Completes a JPEG decoded by an external decoder (which returns RGB): attaches EXIF/ICC from
        /// the file and collapses grayscale JPEGs back to one channel so they are saved as grayscale.
        /// </summary>
        public static PixelImage AttachJpegMetadata(PixelImage decodedRgb, JpegInfo info)
        {
            if (decodedRgb == null) throw new ArgumentNullException(nameof(decodedRgb));
            PixelImage img = decodedRgb;
            if (info != null && info.IsGrayscale && decodedRgb.Channels >= 3 && decodedRgb.BitDepth == 8)
            {
                int n = decodedRgb.Width * decodedRgb.Height;
                var g = new byte[n];
                var s = decodedRgb.Data8;
                int ch = decodedRgb.Channels;
                for (int i = 0; i < n; i++) g[i] = s[i * ch];
                img = new PixelImage(decodedRgb.Width, decodedRgb.Height, 1, g);
            }
            img.SourceFormat = ImageFileFormat.Jpeg;
            img.Metadata = new ImageMetadata();
            if (info != null)
            {
                img.Metadata.Exif = info.Exif;
                img.Metadata.IccProfile = info.IccProfile;
            }
            return img;
        }
    }
}
