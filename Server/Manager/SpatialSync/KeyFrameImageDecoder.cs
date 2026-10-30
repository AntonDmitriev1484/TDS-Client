using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Server.Manager.SpatialSync
{
    /// <summary>Decodes the JPEG/PNG image of a key frame into 8-bit luma for ORB feature extraction.</summary>
    internal static class KeyFrameImageDecoder
    {
        /// <summary>
        /// Decode to Width x Height luma bytes, row-major with row 0 at the top of the decoded image. Returns false if the
        /// bytes are not a decodable image.
        /// </summary>
        public static bool TryDecodeGrayscale(byte[] imageData, out byte[] gray, out int width, out int height)
        {
            gray = Array.Empty<byte>();
            width = height = 0;

            try
            {
                using var stream = new MemoryStream(imageData);
                using var bitmap = new Bitmap(stream);
                width = bitmap.Width;
                height = bitmap.Height;
                if (width <= 0 || height <= 0)
                    return false;

                // Asking for 24bpp makes GDI+ convert whatever the file used (8-bit grey JPEG, RGB PNG, ...).
                BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    gray = new byte[checked(width * height)];
                    var row = new byte[width * 3];
                    for (int y = 0; y < height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                        int o = y * width;
                        for (int x = 0; x < width; x++)
                        {
                            int b = row[3 * x], g = row[3 * x + 1], r = row[3 * x + 2];
                            gray[o + x] = (byte)((r * 77 + g * 150 + b * 29) >> 8);
                        }
                    }
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }

                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or ExternalException or OverflowException)
            {
                return false;
            }
        }
    }
}
