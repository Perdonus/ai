using AgentShell.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;

namespace AgentShell;

public sealed partial class LauncherWindow : Window
{
    private readonly GlobalHotkeyService _hotkey;
    private readonly WindowVisualService _visuals;
    private readonly DispatcherQueue _dispatcherQueue;
    private bool _isVisible;
    private bool _ignoreNextDeactivation;

    public LauncherWindow()
    {
        try
        {
            InitializeComponent();
            StartupLogService.Info("LauncherWindow initialized.");
            _dispatcherQueue = DispatcherQueue;

            _visuals = new WindowVisualService(this, ShellPanel);
            _visuals.InitializeLauncherChrome();
            _visuals.MoveTopRight(false);
            StartupLogService.Info("Launcher visuals initialized.");

            _hotkey = new GlobalHotkeyService(this, 7001, 0xA3);
            _hotkey.HotkeyPressed += Hotkey_HotkeyPressed;
            StartupLogService.Info("Global hotkey registered for Right Ctrl.");

            Activated += LauncherWindow_Activated;
        }
        catch (Exception ex)
        {
            StartupLogService.Error($"LauncherWindow constructor failed: {ex}");
            throw;
        }
    }

    public async Task ToggleAsync()
    {
        if (_isVisible)
        {
            HideAnimated();
        }
        else
        {
            await ShowAnimatedAsync();
        }
    }

    public async Task ShowAnimatedAsync()
    {
        StartupLogService.Info("Showing launcher.");
        _ignoreNextDeactivation = true;
        ShowWindow(WindowNative.GetWindowHandle(this), 5);
        await _visuals.AnimateAsync(show: true);
        _isVisible = true;
        PromptBox.Focus(FocusState.Programmatic);
        PromptBox.SelectAll();
    }

    public void HideAnimated(bool immediate = false)
    {
        StartupLogService.Info(immediate ? "Hiding launcher immediately." : "Hiding launcher.");
        if (!_isVisible && !immediate)
        {
            return;
        }

        _ = HideInternalAsync(immediate);
    }

    public void BringToFront()
    {
        ShowWindow(WindowNative.GetWindowHandle(this), 5);
        SetForegroundWindow(WindowNative.GetWindowHandle(this));
    }

    private async Task HideInternalAsync(bool immediate)
    {
        PromptBox.Text = string.Empty;
        _isVisible = false;

        if (immediate)
        {
            _visuals.MoveTopRight(false);
            return;
        }

        await _visuals.AnimateAsync(show: false);
    }

    private async void LauncherWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (_ignoreNextDeactivation)
        {
            _ignoreNextDeactivation = false;
            return;
        }

        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            await Task.Delay(40);
            HideAnimated();
        }
    }

    private void PromptBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            HandlePrompt(PromptBox.Text.Trim());
            e.Handled = true;
        }

        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            HideAnimated();
            e.Handled = true;
        }
    }

    private void HandlePrompt(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        StartupLogService.Info($"Prompt submitted: {prompt}");
        if (prompt.Contains("\u043d\u0430\u0441\u0442\u0440\u043e\u0439", StringComparison.OrdinalIgnoreCase) ||
            prompt.Contains("settings", StringComparison.OrdinalIgnoreCase))
        {
            App.ShowSettings();
            HideAnimated();
            return;
        }

        HideAnimated();
    }

    private void Hotkey_HotkeyPressed(object? sender, EventArgs e)
    {
        StartupLogService.Info("Global hotkey pressed.");
        _dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await ToggleAsync();
            }
            catch (Exception ex)
            {
                StartupLogService.Error($"Hotkey toggle failed: {ex}");
            }
        });
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);
}
