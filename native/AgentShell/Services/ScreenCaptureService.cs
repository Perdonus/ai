using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AgentShell.Services;

public sealed class ScreenCaptureService
{
    public ScreenSnapshot Capture()
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
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(left, top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        var base64 = Convert.ToBase64String(stream.ToArray());
        StartupLogService.Info($"Captured screen snapshot {width}x{height} at {left},{top}.");
        return new ScreenSnapshot(left, top, width, height, base64);
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
