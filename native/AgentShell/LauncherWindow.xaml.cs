using System.Runtime.InteropServices;
using AgentShell.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Documents;
using WinRT.Interop;

namespace AgentShell;

public sealed partial class LauncherWindow : Window
{
    private static readonly SolidColorBrush PromptBrush = Brush(0xFF, 0xF5, 0xFB, 0xFF);
    private static readonly SolidColorBrush SubtleBrush = Brush(0xFF, 0xB7, 0xC4, 0xD6);
    private static readonly SolidColorBrush TimerBrush = Brush(0xFF, 0x7E, 0x91, 0xA9);
    private static readonly SolidColorBrush TurnBrush = Brush(0xFF, 0x10, 0x18, 0x24);
    private static readonly SolidColorBrush TurnHeaderBrush = Brush(0xFF, 0x15, 0x1F, 0x2D);
    private static readonly SolidColorBrush TurnDetailsBrush = Brush(0xFF, 0x0D, 0x15, 0x20);
    private static readonly SolidColorBrush TurnAnswerBrush = Brush(0xFF, 0x12, 0x1C, 0x29);
    private static readonly SolidColorBrush TurnErrorBrush = Brush(0xFF, 0x36, 0x15, 0x17);
    private static readonly SolidColorBrush TurnErrorTextBrush = Brush(0xFF, 0xFF, 0xAA, 0xAA);

    private readonly AgentLoopService _agentLoop = new();
    private readonly GlobalHotkeyService _hotkey;
    private readonly WindowVisualService _visuals;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly AgentSessionState _session = new();
    private readonly DispatcherTimer _turnTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<ConversationTurnView> _turns = [];
    private CancellationTokenSource? _promptCts;
    private bool _isBusy;
    private bool _isVisible;
    public GlobalHotkeyService HotkeyService => _hotkey;

    public LauncherWindow()
    {
        try
        {
            InitializeComponent();
            StartupLogService.Info("LauncherWindow initialized.");
            _dispatcherQueue = DispatcherQueue;
            _turnTimer.Tick += TurnTimer_Tick;

            _visuals = new WindowVisualService(this, ShellPanel);
            _visuals.InitializeLauncherChrome();
            _visuals.HideImmediately();
            StartupLogService.Info("Launcher visuals initialized.");

            GetAppWindow().Closing += AppWindow_Closing;

            _hotkey = new GlobalHotkeyService(this, 7001, 0xA3);
            _hotkey.HotkeyPressed += Hotkey_HotkeyPressed;
            StartupLogService.Info("Global hotkey registered for Right Ctrl.");

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
            }

            e.Handled = true;
        }
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

        PromptBox.Text = string.Empty;
        var turn = CreateConversationTurn(prompt);
        SetTurnBusy(turn);
        ApplyExpandedState(true);

        var progress = new Progress<AgentLoopProgress>(update =>
        {
            _ = EnqueueOnUiAsync(() =>
            {
                UpdateTurnProgress(turn, update);
            });
        });

        try
        {
            var result = await _agentLoop.RunAsync(App.ConfigService.Current, _session, prompt, progress, _promptCts.Token);
            await EnqueueOnUiAsync(() => CompleteTurn(turn, result));
        }
        catch (OperationCanceledException)
        {
            await EnqueueOnUiAsync(() => CancelTurn(turn));
        }
        catch (Exception ex)
        {
            StartupLogService.Error($"Prompt execution failed: {ex}");
            await EnqueueOnUiAsync(() => FailTurn(turn, ex.Message));
        }
        finally
        {
            _isBusy = false;
            await EnqueueOnUiAsync(StopTurnTimerIfIdle);
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
        ConversationContainer.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (expanded)
        {
            RefreshExpandedLayout();
        }
        else
        {
            _visuals.SetCompact();
        }

        if (_isVisible)
        {
            _visuals.MoveTopRight();
        }
    }

    private void RefreshExpandedLayout()
    {
        ConversationPanel.UpdateLayout();
        ConversationContainer.UpdateLayout();
        ShellPanel.UpdateLayout();

        var maxConversationHeight = _visuals.GetMaxConversationHeight();
        ConversationScrollViewer.MaxHeight = maxConversationHeight;

        ConversationPanel.UpdateLayout();
        ConversationScrollViewer.UpdateLayout();

        var desiredConversationHeight = Math.Min(
            maxConversationHeight,
            Math.Max(0, ConversationPanel.DesiredSize.Height + 16));

        _visuals.SetExpandedToContent(desiredConversationHeight);
    }

    private async Task FocusPromptAsync()
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            await EnqueueOnUiAsync(() =>
            {
                ForceWindowForeground();
                PromptBox.UpdateLayout();
                _ = PromptBox.Focus(FocusState.Programmatic);
                _ = PromptBox.Focus(FocusState.Keyboard);
                if (!string.IsNullOrWhiteSpace(PromptBox.Text))
                {
                    PromptBox.SelectAll();
                }
            });

            await Task.Delay(attempt == 0 ? 40 : 90);
        }
    }

    private void ForceWindowForeground()
    {
        Activate();
        var hwnd = WindowNative.GetWindowHandle(this);
        _ = ShowWindow(hwnd, SwShow);
        _ = BringWindowToTop(hwnd);

        var foreground = GetForegroundWindow();
        var currentThread = GetCurrentThreadId();
        var foregroundThread = foreground == nint.Zero
            ? 0u
            : GetWindowThreadProcessId(foreground, out _);

        if (foregroundThread != 0 && foregroundThread != currentThread)
        {
            _ = AttachThreadInput(foregroundThread, currentThread, true);
            try
            {
                _ = SetForegroundWindow(hwnd);
                _ = SetActiveWindow(hwnd);
                _ = SetFocus(hwnd);
            }
            finally
            {
                _ = AttachThreadInput(foregroundThread, currentThread, false);
            }
        }
        else
        {
            _ = SetForegroundWindow(hwnd);
            _ = SetActiveWindow(hwnd);
            _ = SetFocus(hwnd);
        }
    }

    private bool HasConversationContent()
    {
        return _turns.Count > 0;
    }

    private ConversationTurnView CreateConversationTurn(string prompt)
    {
        ConversationContainer.Visibility = Visibility.Visible;

        var promptText = new TextBlock
        {
            Text = prompt,
            Foreground = PromptBrush,
            FontSize = 14,
            TextWrapping = TextWrapping.WrapWholeWords
        };

        var spinner = new ProgressRing
        {
            Width = 14,
            Height = 14,
            IsActive = true,
            Foreground = (Brush)Application.Current.Resources["ShellAccentBrush"],
            VerticalAlignment = VerticalAlignment.Center
        };

        var summaryText = new TextBlock
        {
            Text = "Думаю...",
            Foreground = SubtleBrush,
            FontSize = 13.5,
            TextWrapping = TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center
        };

        var elapsedText = new TextBlock
        {
            Text = "0с",
            Foreground = TimerBrush,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        ConversationTurnView? turn = null;

        var headerButton = new Button
        {
            Background = new SolidColorBrush(Colors.Transparent),
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        headerButton.Click += (_, _) =>
        {
            if (turn is not null)
            {
                ToggleTurnDetails(turn);
            }
        };

        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        spinner.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetColumn(spinner, 0);
        Grid.SetColumn(summaryText, 1);
        Grid.SetColumn(elapsedText, 2);
        headerGrid.Children.Add(spinner);
        headerGrid.Children.Add(summaryText);
        headerGrid.Children.Add(elapsedText);
        headerButton.Content = headerGrid;

        var detailsText = new RichTextBlock
        {
            Foreground = SubtleBrush,
            FontSize = 13
        };

        var detailsBorder = new Border
        {
            Background = TurnDetailsBrush,
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12),
            Visibility = Visibility.Collapsed,
            Child = detailsText
        };

        var answerText = new TextBlock
        {
            Foreground = PromptBrush,
            FontSize = 14,
            TextWrapping = TextWrapping.WrapWholeWords
        };

        var answerBorder = new Border
        {
            Background = TurnAnswerBrush,
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12),
            Visibility = Visibility.Collapsed,
            Child = answerText
        };

        var errorText = new TextBlock
        {
            Foreground = TurnErrorTextBrush,
            FontSize = 13,
            TextWrapping = TextWrapping.WrapWholeWords
        };

        var errorBorder = new Border
        {
            Background = TurnErrorBrush,
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12),
            Visibility = Visibility.Collapsed,
            Child = errorText
        };

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(promptText);
        stack.Children.Add(headerButton);
        stack.Children.Add(detailsBorder);
        stack.Children.Add(answerBorder);
        stack.Children.Add(errorBorder);

        var root = new Border
        {
            Background = TurnBrush,
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(12),
            Child = stack
        };

        turn = new ConversationTurnView(
            prompt,
            DateTimeOffset.Now,
            root,
            spinner,
            summaryText,
            elapsedText,
            detailsBorder,
            detailsText,
            answerBorder,
            answerText,
            errorBorder,
            errorText);

        _turns.Add(turn);
        ConversationPanel.Children.Add(root);
        AppendTimelineEntry(turn, "Запрос", prompt);
        RenderTimeline(turn);
        UpdateElapsedText(turn);
        RefreshExpandedLayout();
        ScrollConversationToEnd();
        return turn;
    }

    private void SetTurnBusy(ConversationTurnView turn)
    {
        turn.IsBusy = true;
        turn.SummaryText.Text = "Думаю...";
        turn.Spinner.Visibility = Visibility.Visible;
        turn.Spinner.IsActive = true;
        AppendStatusEntry(turn, "Думаю...");
        RenderTimeline(turn);
        UpdateElapsedText(turn);
        _turnTimer.Start();
    }

    private void UpdateTurnProgress(ConversationTurnView turn, AgentLoopProgress update)
    {
        var status = string.IsNullOrWhiteSpace(update.Status) ? "Думаю..." : update.Status.Trim();
        turn.SummaryText.Text = status;
        AppendStatusEntry(turn, status);
        AppendThinkingEntries(turn, update.Thinking);
        RenderTimeline(turn);
        RefreshTurnDetailsVisibility(turn);

        if (!string.IsNullOrWhiteSpace(update.Answer))
        {
            turn.AnswerText.Text = update.Answer;
            turn.AnswerBorder.Visibility = Visibility.Visible;
        }

        UpdateElapsedText(turn);
        ScrollConversationToEnd();
    }

    private void CompleteTurn(ConversationTurnView turn, AgentLoopResult result)
    {
        turn.IsBusy = false;
        turn.Spinner.IsActive = false;
        turn.Spinner.Visibility = Visibility.Collapsed;
        UpdateElapsedText(turn);

        AppendThinkingEntries(turn, result.Thinking);

        if (!string.IsNullOrWhiteSpace(result.Error))
        {
            turn.SummaryText.Text = "Ошибка";
            AppendStatusEntry(turn, "Ошибка");
            AppendTimelineEntry(turn, "Ошибка", result.Error);
            turn.ErrorText.Text = result.Error;
            turn.ErrorBorder.Visibility = Visibility.Visible;
            turn.AnswerBorder.Visibility = Visibility.Collapsed;
        }
        else
        {
            turn.ErrorText.Text = string.Empty;
            turn.ErrorBorder.Visibility = Visibility.Collapsed;
            turn.SummaryText.Text = result.WaitingForUser ? "Жду данные" : "Готово";
            AppendStatusEntry(turn, turn.SummaryText.Text);

            if (!string.IsNullOrWhiteSpace(result.Answer))
            {
                AppendTimelineEntry(turn, "Результат", result.Answer);
                turn.AnswerText.Text = result.Answer;
                turn.AnswerBorder.Visibility = Visibility.Visible;
            }
        }

        RenderTimeline(turn);
        RefreshTurnDetailsVisibility(turn);
        StopTurnTimerIfIdle();
        ScrollConversationToEnd();
    }

    private void CancelTurn(ConversationTurnView turn)
    {
        turn.IsBusy = false;
        turn.Spinner.IsActive = false;
        turn.Spinner.Visibility = Visibility.Collapsed;
        turn.SummaryText.Text = "Остановлено";
        AppendStatusEntry(turn, "Остановлено");
        AppendTimelineEntry(turn, "Остановлено", "Остановилась по запросу.");
        if (string.IsNullOrWhiteSpace(turn.AnswerText.Text))
        {
            turn.AnswerText.Text = "Остановилась.";
            turn.AnswerBorder.Visibility = Visibility.Visible;
        }

        UpdateElapsedText(turn);
        StopTurnTimerIfIdle();
        RenderTimeline(turn);
        RefreshExpandedLayout();
    }

    private void FailTurn(ConversationTurnView turn, string error)
    {
        turn.IsBusy = false;
        turn.Spinner.IsActive = false;
        turn.Spinner.Visibility = Visibility.Collapsed;
        turn.SummaryText.Text = "Ошибка";
        AppendStatusEntry(turn, "Ошибка");
        AppendTimelineEntry(turn, "Ошибка", error);
        turn.ErrorText.Text = error;
        turn.ErrorBorder.Visibility = Visibility.Visible;
        UpdateElapsedText(turn);
        StopTurnTimerIfIdle();
        RenderTimeline(turn);
        RefreshExpandedLayout();
    }

    private void ToggleTurnDetails(ConversationTurnView turn)
    {
        turn.DetailsExpanded = !turn.DetailsExpanded;
        RefreshTurnDetailsVisibility(turn);
    }

    private void RefreshTurnDetailsVisibility(ConversationTurnView turn)
    {
        var hasDetails = turn.DetailEntries.Count > 0;
        turn.DetailsBorder.Visibility = hasDetails && turn.DetailsExpanded
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshExpandedLayout();
    }

    private void TurnTimer_Tick(object? sender, object e)
    {
        foreach (var turn in _turns.Where(item => item.IsBusy))
        {
            UpdateElapsedText(turn);
        }
    }

    private void UpdateElapsedText(ConversationTurnView turn)
    {
        var elapsed = DateTimeOffset.Now - turn.StartedAt;
        var duration = elapsed.TotalMinutes >= 1
            ? $"{Math.Max(1, (int)elapsed.TotalMinutes)}м"
            : $"{Math.Max(1, (int)elapsed.TotalSeconds)}с";
        turn.ElapsedText.Text = turn.IsBusy
            ? $"Думаю · {duration} ▼"
            : $"Думал · {duration} ▼";
    }

    private void StopTurnTimerIfIdle()
    {
        if (_turns.All(item => !item.IsBusy))
        {
            _turnTimer.Stop();
        }
    }

    private void ScrollConversationToEnd()
    {
        ConversationScrollViewer.UpdateLayout();
        ConversationScrollViewer.ChangeView(null, ConversationScrollViewer.ScrollableHeight, null, true);
    }

    private void ResetSessionUi()
    {
        _turnTimer.Stop();
        _turns.Clear();
        ConversationPanel.Children.Clear();
        ConversationContainer.Visibility = Visibility.Collapsed;
        ApplyExpandedState(false);
    }

    private void AppendStatusEntry(ConversationTurnView turn, string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return;
        }

        var normalized = status.Trim();
        if (string.Equals(turn.LastStatus, normalized, StringComparison.Ordinal))
        {
            return;
        }

        turn.LastStatus = normalized;
        AppendTimelineEntry(turn, "Статус", normalized);
    }

    private void AppendThinkingEntries(ConversationTurnView turn, string thinking)
    {
        if (string.IsNullOrWhiteSpace(thinking))
        {
            return;
        }

        var normalized = thinking.Trim();
        string delta;
        if (!string.IsNullOrWhiteSpace(turn.LastThinkingSnapshot) &&
            normalized.StartsWith(turn.LastThinkingSnapshot, StringComparison.Ordinal))
        {
            delta = normalized[turn.LastThinkingSnapshot.Length..].Trim();
        }
        else
        {
            delta = normalized;
        }

        turn.LastThinkingSnapshot = normalized;
        if (string.IsNullOrWhiteSpace(delta))
        {
            return;
        }

        var sections = delta
            .Split($"{Environment.NewLine}{Environment.NewLine}", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var section in sections)
        {
            AppendThoughtSection(turn, section);
        }
    }

    private void AppendThoughtSection(ConversationTurnView turn, string section)
    {
        if (string.IsNullOrWhiteSpace(section))
        {
            return;
        }

        var normalized = section.Trim();
        var heading = "Мысль";
        var body = normalized;
        var colonIndex = normalized.IndexOf(':');
        if (colonIndex > 0 && colonIndex <= 24)
        {
            heading = normalized[..colonIndex].Trim();
            body = normalized[(colonIndex + 1)..].Trim();
        }

        AppendTimelineEntry(turn, heading, body);
    }

    private void AppendTimelineEntry(ConversationTurnView turn, string heading, string body)
    {
        var normalizedHeading = string.IsNullOrWhiteSpace(heading) ? "Событие" : heading.Trim();
        var normalizedBody = string.IsNullOrWhiteSpace(body) ? string.Empty : body.Trim();

        var lastEntry = turn.DetailEntries.LastOrDefault();
        if (lastEntry is not null &&
            string.Equals(lastEntry.Heading, normalizedHeading, StringComparison.Ordinal) &&
            string.Equals(lastEntry.Body, normalizedBody, StringComparison.Ordinal))
        {
            return;
        }

        turn.DetailEntries.Add(new ConversationDetailEntry(normalizedHeading, normalizedBody));
    }

    private static void RenderTimeline(ConversationTurnView turn)
    {
        turn.DetailsText.Blocks.Clear();
        foreach (var entry in turn.DetailEntries)
        {
            var paragraph = new Paragraph();
            var headingSpan = new Span();
            headingSpan.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            headingSpan.FontStyle = Windows.UI.Text.FontStyle.Italic;
            headingSpan.Inlines.Add(new Run { Text = entry.Heading });
            paragraph.Inlines.Add(headingSpan);

            if (!string.IsNullOrWhiteSpace(entry.Body))
            {
                var lines = entry.Body
                    .Split(Environment.NewLine, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

                if (lines.Length > 0)
                {
                    paragraph.Inlines.Add(new Run { Text = " " });
                    paragraph.Inlines.Add(new Run { Text = lines[0] });
                    for (var index = 1; index < lines.Length; index++)
                    {
                        paragraph.Inlines.Add(new LineBreak());
                        paragraph.Inlines.Add(new Run { Text = lines[index] });
                    }
                }
            }

            turn.DetailsText.Blocks.Add(paragraph);
        }
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
        StartupLogService.Info("Launcher close intercepted and ignored.");
    }

    private AppWindow GetAppWindow()
    {
        return AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this)));
    }

    private static SolidColorBrush Brush(byte a, byte r, byte g, byte b)
    {
        return new(ColorHelper.FromArgb(a, r, g, b));
    }

    private const int SwShow = 5;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetActiveWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetFocus(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    private sealed class ConversationTurnView(
        string prompt,
        DateTimeOffset startedAt,
        Border root,
        ProgressRing spinner,
        TextBlock summaryText,
        TextBlock elapsedText,
        Border detailsBorder,
        RichTextBlock detailsText,
        Border answerBorder,
        TextBlock answerText,
        Border errorBorder,
        TextBlock errorText)
    {
        public string Prompt { get; } = prompt;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public Border Root { get; } = root;
        public ProgressRing Spinner { get; } = spinner;
        public TextBlock SummaryText { get; } = summaryText;
        public TextBlock ElapsedText { get; } = elapsedText;
        public Border DetailsBorder { get; } = detailsBorder;
        public RichTextBlock DetailsText { get; } = detailsText;
        public Border AnswerBorder { get; } = answerBorder;
        public TextBlock AnswerText { get; } = answerText;
        public Border ErrorBorder { get; } = errorBorder;
        public TextBlock ErrorText { get; } = errorText;
        public List<ConversationDetailEntry> DetailEntries { get; } = [];
        public string LastStatus { get; set; } = string.Empty;
        public string LastThinkingSnapshot { get; set; } = string.Empty;
        public bool DetailsExpanded { get; set; }
        public bool IsBusy { get; set; }
    }

    private sealed class ConversationDetailEntry(string heading, string body)
    {
        public string Heading { get; } = heading;
        public string Body { get; } = body;
    }
}
