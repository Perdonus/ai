using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AgentShell.Services;

public sealed class ScreenCaptureService
{
    public ScreenSnapshot Capture()
    {
        var left = GetSystemMetrics(SmXvirtualscreen);
        var top = GetSystemMetrics(SmYvirtualscreen);
        var width = GetSystemMetrics(SmCxvirtualscreen);
        var height = GetSystemMetrics(SmCyvirtualscreen);
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("Failed to determine virtual screen bounds.");
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(left, top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        var base64 = Convert.ToBase64String(stream.ToArray());
        StartupLogService.Info($"Captured screen snapshot {width}x{height} at {left},{top}.");
        return new ScreenSnapshot(left, top, width, height, base64);
    }

    private const int SmXvirtualscreen = 76;
    private const int SmYvirtualscreen = 77;
    private const int SmCxvirtualscreen = 78;
    private const int SmCyvirtualscreen = 79;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}

public sealed record ScreenSnapshot(int Left, int Top, int Width, int Height, string PngBase64);
