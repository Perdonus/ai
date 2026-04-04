using System.Runtime.InteropServices;
using AgentShell.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;

namespace AgentShell;

public sealed partial class LauncherWindow : Window
{
    private readonly AgentLoopService _agentLoop = new();
    private readonly GlobalHotkeyService _hotkey;
    private readonly WindowVisualService _visuals;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly AgentSessionState _session = new();
    private CancellationTokenSource? _promptCts;
    private bool _isBusy;
    private bool _isVisible;
    private bool _ignoreNextDeactivation;

    public GlobalHotkeyService HotkeyService => _hotkey;

    public LauncherWindow()
    {
        try
        {
            InitializeComponent();
            StartupLogService.Info("LauncherWindow initialized.");
            _dispatcherQueue = DispatcherQueue;

            _visuals = new WindowVisualService(this, ShellPanel);
            _visuals.InitializeLauncherChrome();
            _visuals.HideImmediately();
            StartupLogService.Info("Launcher visuals initialized.");

            GetAppWindow().Closing += AppWindow_Closing;

            _hotkey = new GlobalHotkeyService(this, 7001, 0xA3);
            _hotkey.HotkeyPressed += Hotkey_HotkeyPressed;
            StartupLogService.Info("Global hotkey registered for Right Ctrl.");

            Activated += LauncherWindow_Activated;
            ResetSessionUi();
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
        if (_isVisible)
        {
            await FocusPromptAsync();
            return;
        }

        ApplyExpandedState(HasConversationContent());
        StartupLogService.Info("Showing launcher.");
        _ignoreNextDeactivation = true;
        await _visuals.AnimateAsync(show: true);
        _isVisible = true;
        await FocusPromptAsync();
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
        _promptCts?.Cancel();
        _isBusy = false;
        _isVisible = false;
        _session.Reset();

        await EnqueueOnUiAsync(() =>
        {
            PromptBox.Text = string.Empty;
            ResetSessionUi();
        });

        if (immediate)
        {
            _visuals.HideImmediately();
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

        if (args.WindowActivationState == WindowActivationState.Deactivated && !_isBusy && !HasConversationContent())
        {
            await Task.Delay(40);
            HideAnimated();
        }
    }

    private void PromptBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            _ = SubmitPromptAsync();
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

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        StartupLogService.Info("Launcher requested settings.");
        App.ShowSettings();
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
            BusyRing.IsActive = true;
            BusyRing.Visibility = Visibility.Visible;
            OutputContainer.Visibility = Visibility.Visible;
            ThinkingPanel.Visibility = Visibility.Visible;
            ThinkingText.Text = "Думаю...";
            AnswerPanel.Visibility = Visibility.Collapsed;
            ErrorText.Visibility = Visibility.Collapsed;
            ErrorText.Text = string.Empty;
            ApplyExpandedState(true);
            SetStatus("Агент работает...");
        });

        var progress = new Progress<AgentLoopProgress>(update =>
        {
            _ = EnqueueOnUiAsync(() =>
            {
                OutputContainer.Visibility = Visibility.Visible;
                ApplyExpandedState(true);
                SetStatus(update.Status);

                if (!string.IsNullOrWhiteSpace(update.Thinking))
                {
                    ThinkingText.Text = update.Thinking;
                    ThinkingPanel.Visibility = Visibility.Visible;
                }

                if (!string.IsNullOrWhiteSpace(update.Answer))
                {
                    AnswerText.Text = update.Answer;
                    AnswerPanel.Visibility = Visibility.Visible;
                }
            });
        });

        try
        {
            var result = await _agentLoop.RunAsync(App.ConfigService.Current, _session, prompt, progress, _promptCts.Token);
            await EnqueueOnUiAsync(() =>
            {
                if (!string.IsNullOrWhiteSpace(result.Error))
                {
                    ErrorText.Text = result.Error;
                    ErrorText.Visibility = Visibility.Visible;
                    SetStatus("Ошибка");
                    return;
                }

                ThinkingText.Text = result.Thinking;
                ThinkingPanel.Visibility = string.IsNullOrWhiteSpace(result.Thinking) ? Visibility.Collapsed : Visibility.Visible;
                AnswerText.Text = result.Answer;
                AnswerPanel.Visibility = string.IsNullOrWhiteSpace(result.Answer) ? Visibility.Collapsed : Visibility.Visible;
                SetStatus("Готово");
            });
        }
        catch (OperationCanceledException)
        {
            await EnqueueOnUiAsync(() => SetStatus("Cancelled"));
        }
        catch (Exception ex)
        {
            StartupLogService.Error($"Prompt execution failed: {ex}");
            await EnqueueOnUiAsync(() =>
            {
                ErrorText.Text = ex.Message;
                ErrorText.Visibility = Visibility.Visible;
                SetStatus("Ошибка");
            });
        }
        finally
        {
            _isBusy = false;
            await EnqueueOnUiAsync(() =>
            {
                BusyRing.IsActive = false;
                BusyRing.Visibility = Visibility.Collapsed;
            });

            await FocusPromptAsync();
        }
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

    private void ApplyExpandedState(bool expanded)
    {
        _visuals.SetExpanded(expanded);
        OutputContainer.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (_isVisible)
        {
            _visuals.MoveTopRight();
        }
    }

    private async Task FocusPromptAsync()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await EnqueueOnUiAsync(() =>
            {
                Activate();
                var hwnd = WindowNative.GetWindowHandle(this);
                _ = ShowWindow(hwnd, SwShow);
                _ = SetForegroundWindow(hwnd);
                PromptBox.Focus(FocusState.Programmatic);
                if (!string.IsNullOrWhiteSpace(PromptBox.Text))
                {
                    PromptBox.SelectAll();
                }
            });

            await Task.Delay(70);
        }
    }

    private bool HasConversationContent()
    {
        return !string.IsNullOrWhiteSpace(ThinkingText.Text) ||
               !string.IsNullOrWhiteSpace(AnswerText.Text) ||
               !string.IsNullOrWhiteSpace(ErrorText.Text);
    }

    private void ResetSessionUi()
    {
        BusyRing.IsActive = false;
        BusyRing.Visibility = Visibility.Collapsed;
        SetStatus(string.Empty);
        ThinkingText.Text = string.Empty;
        AnswerText.Text = string.Empty;
        ErrorText.Text = string.Empty;
        ThinkingPanel.Visibility = Visibility.Collapsed;
        AnswerPanel.Visibility = Visibility.Collapsed;
        ErrorText.Visibility = Visibility.Collapsed;
        ApplyExpandedState(false);
    }

    private void SetStatus(string text)
    {
        StatusText.Text = text;
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

    private const int SwShow = 5;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(nint hWnd);
}
