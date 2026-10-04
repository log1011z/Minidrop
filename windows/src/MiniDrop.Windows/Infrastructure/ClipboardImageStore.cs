using System.IO;
using System.Windows.Media.Imaging;

namespace MiniDrop.Windows.Infrastructure;

public static class ClipboardImageStore
{
    // Keep screenshots outside the OS temp directory: queued uploads must survive a restart.
    public static string Save(BitmapSource image, string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"截图-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png");
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var output = File.Create(path);
            encoder.Save(output);
            return path;
        }
        catch
        {
            try { File.Delete(path); } catch { }
            throw;
        }
    }
}
