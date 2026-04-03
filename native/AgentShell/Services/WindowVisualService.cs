using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Graphics;
using WinRT.Interop;

namespace AgentShell.Services;

public sealed class WindowVisualService(Window window, FrameworkElement animatedRoot)
{
    private readonly Window _window = window;
    private readonly FrameworkElement _animatedRoot = animatedRoot;
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

        _appWindow.Resize(new SizeInt32(468, 108));
    }

    public void MoveTopRight(bool visible)
    {
        var displayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        var x = visible ? workArea.X + workArea.Width - 488 : workArea.X + workArea.Width + 36;
        var y = workArea.Y + 16;
        _appWindow.Move(new PointInt32(x, y));
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
            MoveTopRight(false);
        }
    }
}
