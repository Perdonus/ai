using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace TimerBloomWidget;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private FileSystemWatcher? _inputWatcher;
    private string _widgetRoot = string.Empty;
    private string _inputPath = string.Empty;
    private string _statePath = string.Empty;
    private bool _running;
    private bool _closing;
    private DateTimeOffset _endAt = DateTimeOffset.UtcNow.AddMinutes(5);
    private TimeSpan _remaining = TimeSpan.FromMinutes(5);
    private TimeSpan _initial = TimeSpan.FromMinutes(5);

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        _tick.Tick += Tick_Tick;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _widgetRoot = ResolveWidgetRoot();
        _inputPath = Path.Combine(_widgetRoot, "widget-input.json");
        _statePath = Path.Combine(_widgetRoot, "widget-state.json");
        LoadState();
        PositionDefault();
        BeginOpenAnimation();
        BeginAmbientAnimation();
        UpdateUi();
        StartInputWatcher();
        await ProcessPendingInputAsync();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _tick.Stop();
        _inputWatcher?.Dispose();
    }

    private void Tick_Tick(object? sender, EventArgs e)
    {
        if (!_running)
        {
            return;
        }

        _remaining = _endAt - DateTimeOffset.UtcNow;
        if (_remaining <= TimeSpan.Zero)
        {
            _remaining = TimeSpan.Zero;
            _running = false;
            _tick.Stop();
            StateText.Text = "Время вышло";
            PulseRing.Opacity = 0.38;
        }

        UpdateUi();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        StartTimer();
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        PauseTimer();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        ResetTimer();
    }

    private void PlusMinuteButton_Click(object sender, RoutedEventArgs e)
    {
        _remaining += TimeSpan.FromMinutes(1);
        _initial = _remaining > _initial ? _remaining : _initial;
        if (_running)
        {
            _endAt = DateTimeOffset.UtcNow + _remaining;
        }

        StateText.Text = "Добавила минуту";
        UpdateUi();
        _ = SaveStateAsync();
    }

    private async void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        await CloseAnimatedAsync();
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Button)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
        }
    }

    private async Task ProcessPendingInputAsync()
    {
        if (string.IsNullOrWhiteSpace(_inputPath) || !File.Exists(_inputPath))
        {
            return;
        }

        await Task.Delay(90);
        if (!File.Exists(_inputPath))
        {
            return;
        }

        var raw = (await File.ReadAllTextAsync(_inputPath)).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            TryDelete(_inputPath);
            return;
        }

        var payload = ParseInput(raw);
        if (payload.Close || string.Equals(payload.Command, "close", StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(_inputPath);
            await CloseAnimatedAsync();
            return;
        }

        if (!string.IsNullOrWhiteSpace(payload.Label))
        {
            LabelText.Text = payload.Label.Trim();
        }

        if (payload.DurationSeconds is not null)
        {
            _initial = TimeSpan.FromSeconds(Math.Max(1, payload.DurationSeconds.Value));
            _remaining = _initial;
        }
        else if (!raw.StartsWith("{", StringComparison.Ordinal) && TryParseDuration(raw, out var parsed))
        {
            _initial = parsed;
            _remaining = parsed;
        }

        switch ((payload.Command ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "start":
                StartTimer();
                break;
            case "pause":
                PauseTimer();
                break;
            case "reset":
                ResetTimer();
                break;
        }

        TryDelete(_inputPath);
        UpdateUi();
        await SaveStateAsync();
    }

    private void StartInputWatcher()
    {
        var directory = Path.GetDirectoryName(_inputPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        _inputWatcher = new FileSystemWatcher(directory, Path.GetFileName(_inputPath))
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _inputWatcher.Created += InputWatcher_Changed;
        _inputWatcher.Changed += InputWatcher_Changed;
        _inputWatcher.Renamed += InputWatcher_Renamed;
    }

    private void InputWatcher_Changed(object sender, FileSystemEventArgs e)
    {
        _ = Dispatcher.InvokeAsync(async () => await ProcessPendingInputAsync());
    }

    private void InputWatcher_Renamed(object sender, RenamedEventArgs e)
    {
        _ = Dispatcher.InvokeAsync(async () => await ProcessPendingInputAsync());
    }

    private void StartTimer()
    {
        if (_remaining <= TimeSpan.Zero)
        {
            _remaining = _initial;
        }

        _endAt = DateTimeOffset.UtcNow + _remaining;
        _running = true;
        _tick.Start();
        StateText.Text = "Таймер идёт";
        _ = SaveStateAsync();
    }

    private void PauseTimer()
    {
        if (_running)
        {
            _remaining = _endAt - DateTimeOffset.UtcNow;
        }

        _running = false;
        _tick.Stop();
        StateText.Text = "На паузе";
        UpdateUi();
        _ = SaveStateAsync();
    }

    private void ResetTimer()
    {
        _running = false;
        _tick.Stop();
        _remaining = _initial;
        StateText.Text = "Сброшен";
        UpdateUi();
        _ = SaveStateAsync();
    }

    private void UpdateUi()
    {
        var remaining = _running ? _endAt - DateTimeOffset.UtcNow : _remaining;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        _remaining = remaining;
        TimeText.Text = $"{(int)remaining.TotalMinutes:00}:{remaining.Seconds:00}";
        var denominator = Math.Max(1, _initial.TotalSeconds);
        ProgressBar.Value = Math.Clamp(1.0 - (remaining.TotalSeconds / denominator), 0, 1);
        PulseRing.Opacity = _running ? 0.28 : 0.16;
    }

    private void LoadState()
    {
        if (!File.Exists(_statePath))
        {
            return;
        }

        try
        {
            var state = JsonSerializer.Deserialize<TimerWidgetState>(File.ReadAllText(_statePath));
            if (state is null)
            {
                return;
            }

            LabelText.Text = string.IsNullOrWhiteSpace(state.Label) ? "Таймер" : state.Label;
            _initial = TimeSpan.FromSeconds(Math.Max(1, state.InitialSeconds));
            _remaining = TimeSpan.FromSeconds(Math.Max(0, state.RemainingSeconds));
            _running = state.IsRunning;
            _endAt = DateTimeOffset.UtcNow + _remaining;
            if (_running)
            {
                _tick.Start();
            }
        }
        catch
        {
        }
    }

    private async Task SaveStateAsync()
    {
        if (string.IsNullOrWhiteSpace(_statePath))
        {
            return;
        }

        var state = new TimerWidgetState
        {
            Label = LabelText.Text,
            InitialSeconds = (int)Math.Max(1, _initial.TotalSeconds),
            RemainingSeconds = (int)Math.Max(0, _remaining.TotalSeconds),
            IsRunning = _running
        };

        await File.WriteAllTextAsync(
            _statePath,
            JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void PositionDefault()
    {
        Left = SystemParameters.WorkArea.Right - Width - 24;
        Top = SystemParameters.WorkArea.Top + 520;
    }

    private void BeginOpenAnimation()
    {
        Card.Opacity = 0;
        Card.RenderTransform = new TranslateTransform(0, -16);
        AnimateDouble(Card, UIElement.OpacityProperty, 0, 1, 210);
        AnimateDouble((TranslateTransform)Card.RenderTransform, TranslateTransform.YProperty, -16, 0, 210);
    }

    private void BeginAmbientAnimation()
    {
        AnimatePulse((ScaleTransform)PulseRing.RenderTransform, 1, 1.08, 2800);
    }

    private async Task CloseAnimatedAsync()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        var transform = Card.RenderTransform as TranslateTransform ?? new TranslateTransform();
        Card.RenderTransform = transform;
        AnimateDouble(Card, UIElement.OpacityProperty, 1, 0, 160);
        AnimateDouble(transform, TranslateTransform.YProperty, 0, -10, 160);
        await Task.Delay(170);
        Close();
    }

    private static TimerInputPayload ParseInput(string raw)
    {
        if (!raw.StartsWith("{", StringComparison.Ordinal))
        {
            return new TimerInputPayload();
        }

        try
        {
            return JsonSerializer.Deserialize<TimerInputPayload>(raw, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new TimerInputPayload();
        }
        catch
        {
            return new TimerInputPayload();
        }
    }

    private static bool TryParseDuration(string raw, out TimeSpan duration)
    {
        var trimmed = raw.Trim().ToLowerInvariant();
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var secondsOnly))
        {
            duration = TimeSpan.FromSeconds(Math.Max(1, secondsOnly));
            return true;
        }

        var match = Regex.Match(trimmed, @"^(?<value>\d+)\s*(?<unit>h|hr|hrs|m|min|mins|s|sec|secs)$");
        if (match.Success && int.TryParse(match.Groups["value"].Value, out var value))
        {
            duration = match.Groups["unit"].Value switch
            {
                "h" or "hr" or "hrs" => TimeSpan.FromHours(value),
                "m" or "min" or "mins" => TimeSpan.FromMinutes(value),
                _ => TimeSpan.FromSeconds(value)
            };
            return true;
        }

        duration = TimeSpan.Zero;
        return false;
    }

    private static string ResolveWidgetRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 4 && current is not null; depth++)
        {
            if (File.Exists(Path.Combine(current.FullName, "widget.json")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return AppContext.BaseDirectory;
    }

    private static void AnimateDouble(DependencyObject target, DependencyProperty property, double from, double to, int milliseconds)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            FillBehavior = FillBehavior.Stop
        };
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        animation.Completed += (_, _) => target.SetValue(property, to);
        storyboard.Begin();
    }

    private static void AnimatePulse(ScaleTransform transform, double from, double to, int milliseconds)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}

internal sealed class TimerInputPayload
{
    [JsonPropertyName("duration_seconds")]
    public int? DurationSeconds { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("close")]
    public bool Close { get; set; }
}

internal sealed class TimerWidgetState
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("initial_seconds")]
    public int InitialSeconds { get; set; } = 300;

    [JsonPropertyName("remaining_seconds")]
    public int RemainingSeconds { get; set; } = 300;

    [JsonPropertyName("is_running")]
    public bool IsRunning { get; set; }
}
