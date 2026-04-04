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
    private const int LauncherWidth = 468;
    private const int LauncherHeight = 108;
    private const int VisibleMargin = 16;
    private const int HiddenOffset = 36;

    private readonly Window _window = window;
    private readonly FrameworkElement _animatedRoot = animatedRoot;
    private readonly DispatcherQueue _dispatcherQueue = window.DispatcherQueue;
    private readonly AppWindow _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(window)));

    public void InitializeLauncherChrome()
    {
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        _appWindow.Resize(new SizeInt32(LauncherWidth, LauncherHeight));
        StartupLogService.Info($"Launcher chrome initialized with size {LauncherWidth}x{LauncherHeight}.");
    }

    public void MoveTopRight(bool visible)
    {
        var displayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        var width = Math.Max(_appWindow.Size.Width, LauncherWidth);
        var height = Math.Max(_appWindow.Size.Height, LauncherHeight);
        var visibleX = workArea.X + workArea.Width - width - VisibleMargin;
        var hiddenX = workArea.X + workArea.Width + HiddenOffset;
        var maxVisibleX = workArea.X + Math.Max(0, workArea.Width - width);
        var x = visible
            ? Math.Clamp(visibleX, workArea.X, maxVisibleX)
            : hiddenX;
        var maxY = workArea.Y + Math.Max(0, workArea.Height - height);
        var y = Math.Clamp(workArea.Y + VisibleMargin, workArea.Y, maxY);

        _appWindow.Move(new PointInt32(x, y));
        StartupLogService.Info(
            $"Launcher moved. visible={visible}; workArea={workArea.X},{workArea.Y},{workArea.Width},{workArea.Height}; size={width}x{height}; target={x},{y}");
    }

    public async Task AnimateAsync(bool show)
    {
        var compositor = ElementCompositionPreview.GetElementVisual(_animatedRoot).Compositor;
        var visual = ElementCompositionPreview.GetElementVisual(_animatedRoot);

        var offset = compositor.CreateVector3KeyFrameAnimation();
        offset.Duration = TimeSpan.FromMilliseconds(show ? 180 : 140);
        offset.InsertKeyFrame(0f, show ? new System.Numerics.Vector3(84, 0, 0) : new System.Numerics.Vector3(0, 0, 0));
        offset.InsertKeyFrame(1f, show ? new System.Numerics.Vector3(0, 0, 0) : new System.Numerics.Vector3(84, 0, 0));

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.Duration = offset.Duration;
        opacity.InsertKeyFrame(0f, show ? 0f : 1f);
        opacity.InsertKeyFrame(1f, show ? 1f : 0f);

        if (show)
        {
            MoveTopRight(true);
            _window.Activate();
        }

        visual.StartAnimation("Offset", offset);
        visual.StartAnimation("Opacity", opacity);

        await Task.Delay(offset.Duration);

        if (!show)
        {
            await EnqueueAsync(() => MoveTopRight(false));
        }
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
}
