using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentShell.Services;

public sealed class DesktopContextService
{
    public DesktopContextSnapshot Capture()
    {
        _ = GetCursorPos(out var point);
        var virtualScreen = GetVirtualScreen();
        var currentMonitor = GetCurrentMonitor(point, virtualScreen);
        var visibleWindows = EnumerateVisibleWindows();
        var foreground = NormalizeForegroundWindow(TryGetWindowSummary(GetForegroundWindow()), visibleWindows);

        return new DesktopContextSnapshot(
            point.X,
            point.Y,
            virtualScreen,
            currentMonitor,
            foreground,
            visibleWindows);
    }

    private static WindowSummary? NormalizeForegroundWindow(WindowSummary? foreground, IReadOnlyList<WindowSummary> visibleWindows)
    {
        if (foreground is null)
        {
            return visibleWindows.FirstOrDefault();
        }

        if (!string.Equals(foreground.ProcessName, "AgentShell", StringComparison.OrdinalIgnoreCase))
        {
            return foreground;
        }

        return visibleWindows.FirstOrDefault() ?? foreground;
    }

    private static IReadOnlyList<WindowSummary> EnumerateVisibleWindows()
    {
        List<WindowSummary> windows = [];

        _ = EnumWindows((hWnd, _) =>
        {
            var summary = TryGetWindowSummary(hWnd);
            if (summary is not null &&
                !string.Equals(summary.ProcessName, "AgentShell", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(summary.Title, "MSCTFIME UI", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(summary.Title, "Default IME", StringComparison.OrdinalIgnoreCase))
            {
                windows.Add(summary);
            }

            return windows.Count < 8;
        }, nint.Zero);

        return windows;
    }

    private static WindowSummary? TryGetWindowSummary(nint hWnd)
    {
        if (hWnd == nint.Zero || !IsWindowVisible(hWnd))
        {
            return null;
        }

        var titleLength = GetWindowTextLengthW(hWnd);
        if (titleLength <= 0)
        {
            return null;
        }

        var titleBuilder = new StringBuilder(titleLength + 1);
        _ = GetWindowTextW(hWnd, titleBuilder, titleBuilder.Capacity);
        var title = titleBuilder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        _ = GetWindowRect(hWnd, out var rect);
        var width = Math.Max(0, rect.Right - rect.Left);
        var height = Math.Max(0, rect.Bottom - rect.Top);
        if (width == 0 || height == 0)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(hWnd, out var processId);
        return new WindowSummary(
            title,
            TryGetProcessName(processId),
            rect.Left,
            rect.Top,
            width,
            height);
    }

    private static RectSummary GetVirtualScreen()
    {
        return new RectSummary(
            GetSystemMetrics(SmXvirtualscreen),
            GetSystemMetrics(SmYvirtualscreen),
            GetSystemMetrics(SmCxvirtualscreen),
            GetSystemMetrics(SmCyvirtualscreen));
    }

    private static MonitorSnapshot GetCurrentMonitor(Point point, RectSummary fallback)
    {
        var handle = MonitorFromPoint(point, MonitorDefaulttonearest);
        var monitorInfo = new MonitorInfo
        {
            cbSize = (uint)Marshal.SizeOf<MonitorInfo>()
        };

        if (handle == nint.Zero || !GetMonitorInfoW(handle, ref monitorInfo))
        {
            return new MonitorSnapshot(fallback, fallback);
        }

        return new MonitorSnapshot(
            new RectSummary(
                monitorInfo.rcMonitor.Left,
                monitorInfo.rcMonitor.Top,
                Math.Max(0, monitorInfo.rcMonitor.Right - monitorInfo.rcMonitor.Left),
                Math.Max(0, monitorInfo.rcMonitor.Bottom - monitorInfo.rcMonitor.Top)),
            new RectSummary(
                monitorInfo.rcWork.Left,
                monitorInfo.rcWork.Top,
                Math.Max(0, monitorInfo.rcWork.Right - monitorInfo.rcWork.Left),
                Math.Max(0, monitorInfo.rcWork.Bottom - monitorInfo.rcWork.Top)));
    }

    private static string TryGetProcessName(uint processId)
    {
        try
        {
            return Process.GetProcessById((int)processId).ProcessName;
        }
        catch
        {
            return "unknown";
        }
    }

    private const int SmXvirtualscreen = 76;
    private const int SmYvirtualscreen = 77;
    private const int SmCxvirtualscreen = 78;
    private const int SmCyvirtualscreen = 79;
    private const uint MonitorDefaulttonearest = 2;

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowTextLengthW(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowTextW(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(nint hWnd, out Rect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(Point pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfoW(nint hMonitor, ref MonitorInfo lpmi);

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

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
}

public sealed record DesktopContextSnapshot(
    int CursorX,
    int CursorY,
    RectSummary VirtualScreen,
    MonitorSnapshot CurrentMonitor,
    WindowSummary? ForegroundWindow,
    IReadOnlyList<WindowSummary> VisibleWindows)
{
    public string ToPromptString(string clipboardPreview, RectSummary? screenshotBounds = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Cursor: {CursorX},{CursorY}");
        builder.AppendLine($"Virtual screen: {VirtualScreen.Left},{VirtualScreen.Top} {VirtualScreen.Width}x{VirtualScreen.Height}");
        builder.AppendLine(
            $"Current monitor: {CurrentMonitor.Bounds.Left},{CurrentMonitor.Bounds.Top} {CurrentMonitor.Bounds.Width}x{CurrentMonitor.Bounds.Height}");
        builder.AppendLine(
            $"Current work area: {CurrentMonitor.WorkArea.Left},{CurrentMonitor.WorkArea.Top} {CurrentMonitor.WorkArea.Width}x{CurrentMonitor.WorkArea.Height}");
        builder.AppendLine(
            $"Current monitor center: {CurrentMonitor.Bounds.Left + (CurrentMonitor.Bounds.Width / 2)},{CurrentMonitor.Bounds.Top + (CurrentMonitor.Bounds.Height / 2)}");
        builder.AppendLine(
            $"Cursor in current monitor: {CursorX - CurrentMonitor.Bounds.Left},{CursorY - CurrentMonitor.Bounds.Top}");
        builder.AppendLine(
            $"Cursor in current monitor percent: {FormatPercent(CursorX - CurrentMonitor.Bounds.Left, CurrentMonitor.Bounds.Width)}, {FormatPercent(CursorY - CurrentMonitor.Bounds.Top, CurrentMonitor.Bounds.Height)}");
        builder.AppendLine(
            $"Cursor in current work area: {CursorX - CurrentMonitor.WorkArea.Left},{CursorY - CurrentMonitor.WorkArea.Top}");
        builder.AppendLine(
            $"Cursor in current work area percent: {FormatPercent(CursorX - CurrentMonitor.WorkArea.Left, CurrentMonitor.WorkArea.Width)}, {FormatPercent(CursorY - CurrentMonitor.WorkArea.Top, CurrentMonitor.WorkArea.Height)}");

        if (screenshotBounds is not null)
        {
            builder.AppendLine(
                $"Screenshot bounds on desktop: {screenshotBounds.Left},{screenshotBounds.Top} {screenshotBounds.Width}x{screenshotBounds.Height}");
            if (ContainsPoint(screenshotBounds, CursorX, CursorY))
            {
                builder.AppendLine(
                    $"Cursor in screenshot: {CursorX - screenshotBounds.Left},{CursorY - screenshotBounds.Top}");
                builder.AppendLine(
                    $"Cursor in screenshot percent: {FormatPercent(CursorX - screenshotBounds.Left, screenshotBounds.Width)}, {FormatPercent(CursorY - screenshotBounds.Top, screenshotBounds.Height)}");
            }
        }

        builder.AppendLine($"Clipboard: {clipboardPreview}");

        if (ForegroundWindow is not null)
        {
            builder.AppendLine(
                $"Foreground window: {ForegroundWindow.ProcessName} | \"{ForegroundWindow.Title}\" | {ForegroundWindow.Left},{ForegroundWindow.Top} {ForegroundWindow.Width}x{ForegroundWindow.Height}");

            var relX = CursorX - ForegroundWindow.Left;
            var relY = CursorY - ForegroundWindow.Top;
            if (relX >= 0 && relY >= 0 && relX <= ForegroundWindow.Width && relY <= ForegroundWindow.Height)
            {
                var percentX = ForegroundWindow.Width == 0
                    ? 0
                    : (int)Math.Round(relX * 100.0 / ForegroundWindow.Width);
                var percentY = ForegroundWindow.Height == 0
                    ? 0
                    : (int)Math.Round(relY * 100.0 / ForegroundWindow.Height);
                builder.AppendLine(
                    $"Cursor in foreground window: {relX},{relY} ({percentX}% x, {percentY}% y)");
            }
        }
        else
        {
            builder.AppendLine("Foreground window: unknown");
        }

        if (VisibleWindows.Count == 0)
        {
            builder.Append("Visible windows: none");
            return builder.ToString();
        }

        builder.AppendLine("Visible windows:");
        for (var index = 0; index < VisibleWindows.Count; index++)
        {
            var window = VisibleWindows[index];
            var tags = new List<string>();
            if (ForegroundWindow is not null && SameWindow(window, ForegroundWindow))
            {
                tags.Add("foreground");
            }

            if (Intersects(window, CurrentMonitor.Bounds))
            {
                tags.Add("on_current_monitor");
            }

            if (screenshotBounds is not null && Intersects(window, screenshotBounds))
            {
                tags.Add("on_screenshot");
            }

            builder.Append(index + 1)
                .Append(". ")
                .Append(window.ProcessName)
                .Append(" | \"")
                .Append(window.Title)
                .Append("\" | ")
                .Append(window.Left)
                .Append(',')
                .Append(window.Top)
                .Append(' ')
                .Append(window.Width)
                .Append('x')
                .Append(window.Height)
                .Append(" | center ")
                .Append(window.Left + (window.Width / 2))
                .Append(',')
                .Append(window.Top + (window.Height / 2))
                .Append(" | current-monitor-center ")
                .Append(window.Left + (window.Width / 2) - CurrentMonitor.Bounds.Left)
                .Append(',')
                .Append(window.Top + (window.Height / 2) - CurrentMonitor.Bounds.Top);

            if (screenshotBounds is not null)
            {
                builder.Append(" | screenshot-center ")
                    .Append(window.Left + (window.Width / 2) - screenshotBounds.Left)
                    .Append(',')
                    .Append(window.Top + (window.Height / 2) - screenshotBounds.Top);
            }

            if (tags.Count > 0)
            {
                builder.Append(" | [")
                    .Append(string.Join(", ", tags))
                    .Append(']');
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static bool SameWindow(WindowSummary left, WindowSummary right)
    {
        return string.Equals(left.Title, right.Title, StringComparison.Ordinal) &&
               string.Equals(left.ProcessName, right.ProcessName, StringComparison.OrdinalIgnoreCase) &&
               left.Left == right.Left &&
               left.Top == right.Top &&
               left.Width == right.Width &&
               left.Height == right.Height;
    }

    private static bool ContainsPoint(RectSummary rect, int x, int y)
    {
        return x >= rect.Left &&
               y >= rect.Top &&
               x <= rect.Left + rect.Width &&
               y <= rect.Top + rect.Height;
    }

    private static bool Intersects(WindowSummary window, RectSummary rect)
    {
        return window.Left < rect.Left + rect.Width &&
               window.Left + window.Width > rect.Left &&
               window.Top < rect.Top + rect.Height &&
               window.Top + window.Height > rect.Top;
    }

    private static string FormatPercent(int offset, int length)
    {
        if (length <= 0)
        {
            return "0%";
        }

        var percent = (int)Math.Round(offset * 100.0 / length);
        return $"{Math.Clamp(percent, -999, 999)}%";
    }
}

public sealed record RectSummary(
    int Left,
    int Top,
    int Width,
    int Height);

public sealed record MonitorSnapshot(
    RectSummary Bounds,
    RectSummary WorkArea);

public sealed record WindowSummary(
    string Title,
    string ProcessName,
    int Left,
    int Top,
    int Width,
    int Height);
