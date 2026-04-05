using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace StopwatchWaveWidget;

public partial class MainWindow : Window
{
    private readonly Stopwatch _stopwatch = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private FileSystemWatcher? _inputWatcher;
    private string _widgetRoot = string.Empty;
    private string _inputPath = string.Empty;
    private string _statePath = string.Empty;
    private bool _closing;

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
        UpdateUi();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        StartStopwatch();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopStopwatch();
    }

    private void LapButton_Click(object sender, RoutedEventArgs e)
    {
        AddLap();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        ResetStopwatch();
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
        var command = !string.IsNullOrWhiteSpace(payload.Command) ? payload.Command : raw;
        if (payload.Close || string.Equals(command, "close", StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(_inputPath);
            await CloseAnimatedAsync();
            return;
        }

        if (!string.IsNullOrWhiteSpace(payload.Label))
        {
            LabelText.Text = payload.Label.Trim();
        }

        switch ((command ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "start":
                StartStopwatch();
                break;
            case "stop":
            case "pause":
                StopStopwatch();
                break;
            case "lap":
                AddLap();
                break;
            case "reset":
                ResetStopwatch();
                break;
        }

        TryDelete(_inputPath);
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

    private void StartStopwatch()
    {
        if (_stopwatch.IsRunning)
        {
            return;
        }

        _stopwatch.Start();
        _tick.Start();
        _ = SaveStateAsync();
    }

    private void StopStopwatch()
    {
        _stopwatch.Stop();
        _tick.Stop();
        _ = SaveStateAsync();
    }

    private void ResetStopwatch()
    {
        _stopwatch.Reset();
        _tick.Stop();
        LapList.Items.Clear();
        UpdateUi();
        _ = SaveStateAsync();
    }

    private void AddLap()
    {
        if (_stopwatch.Elapsed <= TimeSpan.Zero)
        {
            return;
        }

        LapList.Items.Insert(0, $"{LapList.Items.Count + 1}. {FormatElapsed(_stopwatch.Elapsed)}");
        while (LapList.Items.Count > 8)
        {
            LapList.Items.RemoveAt(LapList.Items.Count - 1);
        }

        _ = SaveStateAsync();
    }

    private void UpdateUi()
    {
        TimeText.Text = FormatElapsed(_stopwatch.Elapsed);
    }

    private void LoadState()
    {
        if (!File.Exists(_statePath))
        {
            return;
        }

        try
        {
            var state = JsonSerializer.Deserialize<StopwatchWidgetState>(File.ReadAllText(_statePath));
            if (state is null)
            {
                return;
            }

            LabelText.Text = string.IsNullOrWhiteSpace(state.Label) ? "Секундомер" : state.Label;
            foreach (var lap in state.Laps)
            {
                LapList.Items.Add(lap);
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

        var state = new StopwatchWidgetState
        {
            Label = LabelText.Text,
            Laps = LapList.Items.Cast<object>().Select(item => item?.ToString() ?? string.Empty).ToList()
        };

        await File.WriteAllTextAsync(
            _statePath,
            JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void PositionDefault()
    {
        Left = Math.Max(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Right - Width - 468);
        Top = SystemParameters.WorkArea.Top + 486;
    }

    private void BeginOpenAnimation()
    {
        Card.Opacity = 0;
        Card.RenderTransform = new TranslateTransform(0, -16);
        AnimateDouble(Card, UIElement.OpacityProperty, 0, 1, 210);
        AnimateDouble((TranslateTransform)Card.RenderTransform, TranslateTransform.YProperty, -16, 0, 210);
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

    private static StopwatchInputPayload ParseInput(string raw)
    {
        if (!raw.StartsWith("{", StringComparison.Ordinal))
        {
            return new StopwatchInputPayload { Command = raw };
        }

        try
        {
            return JsonSerializer.Deserialize<StopwatchInputPayload>(raw, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new StopwatchInputPayload();
        }
        catch
        {
            return new StopwatchInputPayload { Command = raw };
        }
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

    private static string FormatElapsed(TimeSpan elapsed)
    {
        return $"{(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}.{elapsed.Milliseconds / 100}";
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

internal sealed class StopwatchInputPayload
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("close")]
    public bool Close { get; set; }
}

internal sealed class StopwatchWidgetState
{
    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("laps")]
    public List<string> Laps { get; set; } = [];
}
