using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentShell.Services;

public sealed class DesktopContextService
{
    public DesktopContextSnapshot Capture()
    {
        _ = GetCursorPos(out var point);
        var foreground = TryGetWindowSummary(GetForegroundWindow());
        var visibleWindows = EnumerateVisibleWindows();

        return new DesktopContextSnapshot(
            point.X,
            point.Y,
            foreground,
            visibleWindows);
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
}

public sealed record DesktopContextSnapshot(
    int CursorX,
    int CursorY,
    WindowSummary? ForegroundWindow,
    IReadOnlyList<WindowSummary> VisibleWindows)
{
    public string ToPromptString(string clipboardPreview)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Cursor: {CursorX},{CursorY}");
        builder.AppendLine($"Clipboard: {clipboardPreview}");

        if (ForegroundWindow is not null)
        {
            builder.AppendLine(
                $"Foreground window: {ForegroundWindow.ProcessName} | \"{ForegroundWindow.Title}\" | {ForegroundWindow.Left},{ForegroundWindow.Top} {ForegroundWindow.Width}x{ForegroundWindow.Height}");
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
                .AppendLine();
        }

        return builder.ToString().TrimEnd();
    }
}

public sealed record WindowSummary(
    string Title,
    string ProcessName,
    int Left,
    int Top,
    int Width,
    int Height);
