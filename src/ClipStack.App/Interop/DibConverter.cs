using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipStack.Core.Images;

namespace ClipStack.App.Interop;

/// <summary>Decodes a stored PNG into packed CF_DIB pixels for the clipboard writer.</summary>
public static class DibConverter
{
    public static byte[]? FromPng(byte[] png)
    {
        try
        {
            var decoder = BitmapDecoder.Create(
                new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames.Count > 0 ? decoder.Frames[0] : null;
            if (frame is null
                || frame.PixelWidth == 0
                || frame.PixelHeight == 0
                || (long)frame.PixelWidth * frame.PixelHeight > DibImage.MaxPixels)
            {
                return null;
            }

            var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            var stride = bgra.PixelWidth * 4;
            var pixels = new byte[stride * (long)bgra.PixelHeight];
            bgra.CopyPixels(pixels, stride, 0);
            return DibImage.EncodeBgra32(bgra.PixelWidth, bgra.PixelHeight, pixels);
        }
        catch (Exception)
        {
            // The PNG format still goes out even when the DIB side fails.
            return null;
        }
    }
}
