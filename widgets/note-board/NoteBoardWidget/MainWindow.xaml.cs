using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace NoteBoardWidget;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(550) };
    private FileSystemWatcher? _inputWatcher;
    private string _widgetRoot = string.Empty;
    private string _inputPath = string.Empty;
    private string _statePath = string.Empty;
    private bool _closing;
    private bool _suppressSave;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        _saveTimer.Tick += SaveTimer_Tick;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _widgetRoot = ResolveWidgetRoot();
        _inputPath = Path.Combine(_widgetRoot, "widget-input.json");
        _statePath = Path.Combine(_widgetRoot, "widget-state.json");
        LoadState();
        PositionDefault();
        BeginOpenAnimation();
        StartInputWatcher();
        await ProcessPendingInputAsync();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        _inputWatcher?.Dispose();
    }

    private async void SaveTimer_Tick(object? sender, EventArgs e)
    {
        _saveTimer.Stop();
        await SaveStateAsync();
    }

    private void NoteTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_suppressSave)
        {
            return;
        }

        StatusText.Text = "Сохраняю...";
        _saveTimer.Stop();
        _saveTimer.Start();
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
        if (payload.Close)
        {
            TryDelete(_inputPath);
            await CloseAnimatedAsync();
            return;
        }

        _suppressSave = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(payload.Title))
            {
                TitleText.Text = payload.Title.Trim();
            }

            if (!string.IsNullOrWhiteSpace(payload.Text))
            {
                if (payload.Prepend)
                {
                    NoteTextBox.Text = string.Concat(payload.Text.Trim(), Environment.NewLine, NoteTextBox.Text).Trim();
                }
                else if (payload.Append)
                {
                    NoteTextBox.Text = string.Concat(NoteTextBox.Text.TrimEnd(), Environment.NewLine, payload.Text.Trim()).Trim();
                }
                else
                {
                    NoteTextBox.Text = payload.Text;
                }
            }
            else if (!raw.StartsWith("{", StringComparison.Ordinal))
            {
                NoteTextBox.Text = raw;
            }
        }
        finally
        {
            _suppressSave = false;
        }

        StatusText.Text = "Заметка обновлена.";
        TryDelete(_inputPath);
        await SaveStateAsync();

        if (payload.Focus)
        {
            Activate();
            NoteTextBox.Focus();
            NoteTextBox.CaretIndex = NoteTextBox.Text.Length;
        }
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

    private void LoadState()
    {
        if (!File.Exists(_statePath))
        {
            return;
        }

        try
        {
            var state = JsonSerializer.Deserialize<NoteWidgetState>(File.ReadAllText(_statePath));
            if (state is null)
            {
                return;
            }

            TitleText.Text = string.IsNullOrWhiteSpace(state.Title) ? "Заметка" : state.Title;
            NoteTextBox.Text = state.Text ?? string.Empty;
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

        var state = new NoteWidgetState
        {
            Title = TitleText.Text,
            Text = NoteTextBox.Text
        };

        await File.WriteAllTextAsync(
            _statePath,
            JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        StatusText.Text = "Сохранено.";
    }

    private void PositionDefault()
    {
        Left = Math.Max(SystemParameters.WorkArea.Left, SystemParameters.WorkArea.Right - Width - 452);
        Top = SystemParameters.WorkArea.Top + 118;
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

    private static NoteInputPayload ParseInput(string raw)
    {
        if (!raw.StartsWith("{", StringComparison.Ordinal))
        {
            return new NoteInputPayload { Text = raw };
        }

        try
        {
            return JsonSerializer.Deserialize<NoteInputPayload>(raw, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new NoteInputPayload { Text = raw };
        }
        catch
        {
            return new NoteInputPayload { Text = raw };
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

internal sealed class NoteInputPayload
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("append")]
    public bool Append { get; set; }

    [JsonPropertyName("prepend")]
    public bool Prepend { get; set; }

    [JsonPropertyName("focus")]
    public bool Focus { get; set; }

    [JsonPropertyName("close")]
    public bool Close { get; set; }
}

internal sealed class NoteWidgetState
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;
}
