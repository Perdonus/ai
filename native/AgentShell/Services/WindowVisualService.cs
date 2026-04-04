using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Graphics;
using WinRT.Interop;

namespace AgentShell.Services;

public sealed class WindowVisualService(Window window, FrameworkElement animatedRoot)
{
    private const int CompactWidth = 500;
    private const int CompactHeight = 72;
    private const int ExpandedWidth = 560;
    private const int ExpandedHeight = 430;
    private const int VisibleMargin = 18;
    private const int SwHide = 0;
    private const int SwShow = 5;
    private const uint TransparentKeyColor = 0x00030201;

    private readonly Window _window = window;
    private readonly FrameworkElement _animatedRoot = animatedRoot;
    private readonly DispatcherQueue _dispatcherQueue = window.DispatcherQueue;
    private readonly AppWindow _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(window)));
    private bool _expanded;

    public void InitializeLauncherChrome()
    {
        _appWindow.Title = "AI Agent";
        _appWindow.IsShownInSwitchers = false;

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = false;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        EnsureTaskbarWindowStyle();
        EnableTransparentHost();
        SuppressWindowFrame();
        SetExpanded(false);
        StartupLogService.Info("Launcher chrome initialized.");
    }

    public void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        var width = expanded ? ExpandedWidth : CompactWidth;
        var height = expanded ? ExpandedHeight : CompactHeight;
        _appWindow.Resize(new SizeInt32(width, height));
        StartupLogService.Info($"Launcher size mode set to {(expanded ? "expanded" : "compact")} {width}x{height}.");
    }

    public void HideImmediately()
    {
        var hwnd = WindowNative.GetWindowHandle(_window);
        _ = ShowWindow(hwnd, SwHide);
        StartupLogService.Info("Launcher hidden with SW_HIDE.");
    }

    public async Task AnimateAsync(bool show)
    {
        var compositor = ElementCompositionPreview.GetElementVisual(_animatedRoot).Compositor;
        var visual = ElementCompositionPreview.GetElementVisual(_animatedRoot);

        var offset = compositor.CreateVector3KeyFrameAnimation();
        offset.Duration = TimeSpan.FromMilliseconds(show ? 180 : 140);
        offset.InsertKeyFrame(0f, show ? new System.Numerics.Vector3(72, 0, 0) : new System.Numerics.Vector3(0, 0, 0));
        offset.InsertKeyFrame(1f, show ? new System.Numerics.Vector3(0, 0, 0) : new System.Numerics.Vector3(72, 0, 0));

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Duration = offset.Duration;
        opacity.InsertKeyFrame(0f, show ? 0f : 1f);
        opacity.InsertKeyFrame(1f, show ? 1f : 0f);

        if (show)
        {
            MoveTopRight();
            var hwnd = WindowNative.GetWindowHandle(_window);
            _ = ShowWindow(hwnd, SwShow);
            _window.Activate();
            BringToFront();
        }

        visual.StartAnimation("Offset", offset);
        visual.StartAnimation("Opacity", opacity);

        await Task.Delay(offset.Duration);

        if (!show)
        {
            await EnqueueAsync(HideImmediately);
        }
    }

    public void MoveTopRight()
    {
        var workArea = GetCurrentMonitorWorkArea();
        var width = _appWindow.Size.Width;
        var height = _appWindow.Size.Height;
        var x = Math.Max(workArea.X, workArea.X + workArea.Width - width - VisibleMargin);
        var y = Math.Max(workArea.Y, workArea.Y + VisibleMargin);
        var maxY = workArea.Y + Math.Max(0, workArea.Height - height);
        y = Math.Clamp(y, workArea.Y, maxY);

        _appWindow.Move(new PointInt32(x, y));
        StartupLogService.Info(
            $"Launcher moved. expanded={_expanded}; workArea={workArea.X},{workArea.Y},{workArea.Width},{workArea.Height}; size={width}x{height}; target={x},{y}");
    }

    private RectInt32 GetCurrentMonitorWorkArea()
    {
        if (!GetCursorPos(out var cursor))
        {
            var fallback = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            return new RectInt32(fallback.X, fallback.Y, fallback.Width, fallback.Height);
        }

        var displayArea = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest);
        var workArea = displayArea.WorkArea;
        StartupLogService.Info($"Cursor position captured at {cursor.X},{cursor.Y}.");
        return new RectInt32(workArea.X, workArea.Y, workArea.Width, workArea.Height);
    }

    private Task EnqueueAsync(Action action)
    {
        var tcs = new TaskCompletionSource();
        if (!_dispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }))
        {
            tcs.SetException(new InvalidOperationException("Failed to enqueue work on the UI dispatcher."));
        }

        return tcs.Task;
    }

    private void EnsureTaskbarWindowStyle()
    {
        var hwnd = WindowNative.GetWindowHandle(_window);
        var style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        style &= ~(WsCaption | WsThickframe | WsMinimizebox | WsMaximizebox | WsSysmenu);
        _ = SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(style));

        var exStyle = GetWindowLongPtr(hwnd, GwlExstyle).ToInt64();
        exStyle |= WsExToolwindow | WsExLayered;
        exStyle &= ~WsExAppwindow;
        _ = SetWindowLongPtr(hwnd, GwlExstyle, new IntPtr(exStyle));
        _ = SetWindowPos(hwnd, nint.Zero, 0, 0, 0, 0, SwpNomove | SwpNosize | SwpFramechanged);
        StartupLogService.Info($"Launcher extended window style set to 0x{exStyle:X}.");
    }

    private void EnableTransparentHost()
    {
        var hwnd = WindowNative.GetWindowHandle(_window);
        if (!SetLayeredWindowAttributes(hwnd, TransparentKeyColor, 0, LwaColorkey))
        {
            StartupLogService.Warn($"Failed to enable launcher transparency. win32={Marshal.GetLastWin32Error()}");
            return;
        }

        StartupLogService.Info("Launcher layered transparency enabled.");
    }

    private void SuppressWindowFrame()
    {
        var hwnd = WindowNative.GetWindowHandle(_window);
        var borderColor = DwmColorNone;
        var cornerPreference = DwmWindowCornerPreferenceRound;
        _ = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref borderColor, sizeof(uint));
        _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref cornerPreference, sizeof(uint));
        StartupLogService.Info("Launcher DWM frame suppression applied.");
    }

    private void BringToFront()
    {
        var hwnd = WindowNative.GetWindowHandle(_window);
        _ = ShowWindow(hwnd, SwShow);
        _ = SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNomove | SwpNosize);
        _ = SetWindowPos(hwnd, HwndNotopmost, 0, 0, 0, 0, SwpNomove | SwpNosize);
        _ = BringWindowToTop(hwnd);
        _ = SetForegroundWindow(hwnd);
        _ = SetActiveWindow(hwnd);
        StartupLogService.Info("Launcher bring-to-front sequence executed.");
    }

    private const int GwlStyle = -16;
    private const int GwlExstyle = -20;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNomove = 0x0002;
    private const uint SwpFramechanged = 0x0020;
    private const long WsCaption = 0x00C00000L;
    private const long WsSysmenu = 0x00080000L;
    private const long WsThickframe = 0x00040000L;
    private const long WsMinimizebox = 0x00020000L;
    private const long WsMaximizebox = 0x00010000L;
    private const long WsExToolwindow = 0x00000080L;
    private const long WsExLayered = 0x00080000L;
    private const long WsExAppwindow = 0x00040000L;
    private const uint LwaColorkey = 0x00000001;
    private const uint DwmwaWindowCornerPreference = 33;
    private const uint DwmwaBorderColor = 34;
    private const uint DwmColorNone = 0xFFFFFFFE;
    private const uint DwmWindowCornerPreferenceRound = 2;
    private static readonly nint HwndTopmost = new(-1);
    private static readonly nint HwndNotopmost = new(-2);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(nint hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetActiveWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(nint hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, uint dwAttribute, ref uint pvAttribute, int cbAttribute);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }
}
