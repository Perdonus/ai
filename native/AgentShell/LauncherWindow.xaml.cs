using AgentShell.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;

namespace AgentShell;

public sealed partial class LauncherWindow : Window
{
    private readonly AgentChatService _chatService = new();
    private readonly GlobalHotkeyService _hotkey;
    private readonly WindowVisualService _visuals;
    private readonly DispatcherQueue _dispatcherQueue;
    private CancellationTokenSource? _promptCts;
    private bool _isBusy;
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

            GetAppWindow().Closing += AppWindow_Closing;

            _hotkey = new GlobalHotkeyService(this, 7001, 0xA3);
            _hotkey.HotkeyPressed += Hotkey_HotkeyPressed;
            StartupLogService.Info("Global hotkey registered for Right Ctrl.");

            Activated += LauncherWindow_Activated;
            ResetOutput();
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
        await Task.Delay(60);
        await EnqueueOnUiAsync(() =>
        {
            _isVisible = true;
            PromptBox.Focus(FocusState.Programmatic);
            PromptBox.SelectAll();
        });
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

    private async Task HideInternalAsync(bool immediate)
    {
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
            if (!_isBusy)
            {
                HideAnimated();
            }
        }
    }

    private async void PromptBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.RightControl)
        {
            HideAnimated();
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            await SubmitPromptAsync();
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            if (_isBusy)
            {
                _promptCts?.Cancel();
                SetStatus("Cancelled");
            }
            else
            {
                HideAnimated();
            }

            e.Handled = true;
        }
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        await SubmitPromptAsync();
    }

    private async Task SubmitPromptAsync()
    {
        var prompt = PromptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt) || _isBusy)
        {
            return;
        }

        if (prompt.Contains("настрой", StringComparison.OrdinalIgnoreCase) ||
            prompt.Contains("settings", StringComparison.OrdinalIgnoreCase))
        {
            App.ShowSettings();
            return;
        }

        StartupLogService.Info($"Prompt submitted: {prompt}");
        _promptCts?.Cancel();
        _promptCts = new CancellationTokenSource();
        _isBusy = true;

        await EnqueueOnUiAsync(() =>
        {
            SendButton.IsEnabled = false;
            PromptBox.IsEnabled = false;
            SetStatus("Thinking...");
            ThinkingPanel.Visibility = Visibility.Collapsed;
            AnswerPanel.Visibility = Visibility.Collapsed;
            ErrorText.Visibility = Visibility.Collapsed;
            ErrorText.Text = string.Empty;
            ThinkingText.Text = string.Empty;
            AnswerText.Text = string.Empty;
        });

        try
        {
            var result = await _chatService.RunAsync(App.ConfigService.Current, prompt, _promptCts.Token);
            await EnqueueOnUiAsync(() =>
            {
                if (!string.IsNullOrWhiteSpace(result.Error))
                {
                    ErrorText.Text = result.Error;
                    ErrorText.Visibility = Visibility.Visible;
                    SetStatus("Request failed");
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(result.Thinking))
                    {
                        ThinkingText.Text = result.Thinking;
                        ThinkingPanel.Visibility = Visibility.Visible;
                    }

                    AnswerText.Text = result.Answer;
                    AnswerPanel.Visibility = Visibility.Visible;
                    SetStatus("Ready");
                }
            });
        }
        catch (OperationCanceledException)
        {
            await EnqueueOnUiAsync(() =>
            {
                SetStatus("Cancelled");
            });
        }
        catch (Exception ex)
        {
            StartupLogService.Error($"Prompt execution failed: {ex}");
            await EnqueueOnUiAsync(() =>
            {
                ErrorText.Text = ex.Message;
                ErrorText.Visibility = Visibility.Visible;
                SetStatus("Request failed");
            });
        }
        finally
        {
            _isBusy = false;
            await EnqueueOnUiAsync(() =>
            {
                SendButton.IsEnabled = true;
                PromptBox.IsEnabled = true;
                PromptBox.Focus(FocusState.Programmatic);
                PromptBox.SelectAll();
            });
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        App.ShowSettings();
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

    private void SetStatus(string text)
    {
        StatusText.Text = text;
    }

    private void ResetOutput()
    {
        SetStatus("Ready");
        ThinkingText.Text = string.Empty;
        AnswerText.Text = string.Empty;
        ErrorText.Text = string.Empty;
        ThinkingPanel.Visibility = Visibility.Collapsed;
        AnswerPanel.Visibility = Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private Task EnqueueOnUiAsync(Action action)
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

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        StartupLogService.Info("Launcher close intercepted, hiding instead.");
        HideAnimated();
    }

    private AppWindow GetAppWindow()
    {
        return AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this)));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);
}
