using System.IO;
using System.Windows.Media.Imaging;

namespace MiniDrop.Windows.Infrastructure;

public static class ImagePreview
{
    public static bool IsImage(string name, string? mime) => mime?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true
        || new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff" }.Contains(Path.GetExtension(name).ToLowerInvariant());

    public static BitmapSource? Load(string path, int maxPixels)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var width = frame.PixelWidth;
            var height = frame.PixelHeight;
            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            if (Math.Max(width, height) > maxPixels)
            {
                if (width >= height) image.DecodePixelWidth = maxPixels;
                else image.DecodePixelHeight = maxPixels;
            }
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }
}
