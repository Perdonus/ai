using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AgentShell.Services;

public sealed class ScreenCaptureService
{
    /// <summary>
    /// Captures the monitor under the cursor.
    /// <paramref name="maxPixels"/> &gt; 0 downscales the encoded PNG to a multiple of 32 while the
    /// snapshot keeps the real monitor geometry, so action coordinates stay in screen space.
    /// EvoCUA answers in a relative 0..999 grid, so a smaller image does not shift the mapping.
    /// </summary>
    public ScreenSnapshot Capture(int maxPixels = 0)
    {
        var area = GetCaptureArea();
        var left = area.Left;
        var top = area.Top;
        var width = area.Width;
        var height = area.Height;
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("Failed to determine capture bounds.");
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(left, top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
        }

        var sentWidth = width;
        var sentHeight = height;
        Bitmap? scaled = null;
        try
        {
            if (maxPixels > 0 && (long)width * height > maxPixels)
            {
                (sentWidth, sentHeight) = QwenImageProcessor.SmartResize(width, height, maxPixels: maxPixels);
                scaled = new Bitmap(sentWidth, sentHeight, PixelFormat.Format32bppArgb);
                using var scaledGraphics = Graphics.FromImage(scaled);
                scaledGraphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                scaledGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                scaledGraphics.DrawImage(bitmap, 0, 0, sentWidth, sentHeight);
            }

            using var stream = new MemoryStream();
            (scaled ?? bitmap).Save(stream, ImageFormat.Png);
            var base64 = Convert.ToBase64String(stream.ToArray());
            StartupLogService.Info(
                $"Captured screen snapshot {width}x{height} at {left},{top} (sent {sentWidth}x{sentHeight}, png={stream.Length / 1024} KB).");
            return new ScreenSnapshot(left, top, width, height, base64);
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    /// <summary>
    /// Writes the captured PNG to disk so the agent can attach it as a picture and
    /// type its path into a file dialog.
    /// </summary>
    public static string SaveSnapshot(ScreenSnapshot snapshot, string directory, string fileNameWithoutExtension)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{fileNameWithoutExtension}.png");
        File.WriteAllBytes(path, Convert.FromBase64String(snapshot.PngBase64));
        return path;
    }

    /// <summary>Keeps only the newest screenshot files so the folder cannot grow without bound.</summary>
    public static void PruneOldSnapshots(string directory, int keep)
    {
        try
        {
            if (!Directory.Exists(directory) || keep <= 0)
            {
                return;
            }

            var stale = new DirectoryInfo(directory)
                .GetFiles("step-*.png")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(keep);

            foreach (var file in stale)
            {
                file.Delete();
            }
        }
        catch (Exception ex)
        {
            StartupLogService.Warn($"Failed to prune screenshots: {ex.Message}");
        }
    }

    private static CaptureArea GetCaptureArea()
    {
        if (GetCursorPos(out var cursor))
        {
            var monitor = MonitorFromPoint(cursor, MonitorDefaulttonearest);
            var info = new MonitorInfo
            {
                cbSize = (uint)Marshal.SizeOf<MonitorInfo>()
            };

            if (monitor != nint.Zero && GetMonitorInfoW(monitor, ref info))
            {
                return new CaptureArea(
                    info.rcMonitor.Left,
                    info.rcMonitor.Top,
                    Math.Max(1, info.rcMonitor.Right - info.rcMonitor.Left),
                    Math.Max(1, info.rcMonitor.Bottom - info.rcMonitor.Top));
            }
        }

        return new CaptureArea(
            GetSystemMetrics(SmXvirtualscreen),
            GetSystemMetrics(SmYvirtualscreen),
            GetSystemMetrics(SmCxvirtualscreen),
            GetSystemMetrics(SmCyvirtualscreen));
    }

    private const int SmXvirtualscreen = 76;
    private const int SmYvirtualscreen = 77;
    private const int SmCxvirtualscreen = 78;
    private const int SmCyvirtualscreen = 79;
    private const uint MonitorDefaulttonearest = 2;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(Point pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfoW(nint hMonitor, ref MonitorInfo lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public uint cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    private readonly record struct CaptureArea(int Left, int Top, int Width, int Height);
}

public sealed record ScreenSnapshot(int Left, int Top, int Width, int Height, string PngBase64);
