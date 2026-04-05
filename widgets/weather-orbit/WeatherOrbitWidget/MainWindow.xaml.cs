using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WeatherOrbitWidget;

public partial class MainWindow : Window
{
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private readonly DispatcherTimer _refreshTimer = new();
    private readonly Random _random = new();
    private readonly SemaphoreSlim _inputLock = new(1, 1);
    private FileSystemWatcher? _inputWatcher;
    private string _widgetRoot = string.Empty;
    private string _inputPath = string.Empty;
    private string _statePath = string.Empty;
    private WeatherLocation _selectedLocation = new("Москва", 55.7558, 37.6176);
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        _refreshTimer.Tick += RefreshTimer_Tick;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _widgetRoot = ResolveWidgetRoot();
        _inputPath = Path.Combine(_widgetRoot, "widget-input.json");
        _statePath = Path.Combine(_widgetRoot, "widget-state.json");
        LoadState();
        PositionTopRight();
        BeginOpenAnimation();
        BeginAmbientAnimation();
        StartInputWatcher();

        if (!await ProcessPendingInputAsync())
        {
            await RefreshWeatherAsync();
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _inputWatcher?.Dispose();
        _httpClient.Dispose();
        _inputLock.Dispose();
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshWeatherAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshWeatherAsync();
    }

    private async void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        await CloseAnimatedAsync();
    }

    private async Task<bool> ProcessPendingInputAsync()
    {
        if (string.IsNullOrWhiteSpace(_inputPath) || !File.Exists(_inputPath))
        {
            return false;
        }

        await _inputLock.WaitAsync();
        try
        {
            if (!File.Exists(_inputPath))
            {
                return false;
            }

            await Task.Delay(90);
            var raw = (await File.ReadAllTextAsync(_inputPath)).Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                TryDelete(_inputPath);
                return false;
            }

            var isJson = raw.StartsWith("{", StringComparison.Ordinal);
            var payload = ParseInput(raw);
            if (payload.Close || string.Equals(payload.Command, "close", StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(_inputPath);
                await CloseAnimatedAsync();
                return true;
            }

            if (!string.IsNullOrWhiteSpace(payload.Location))
            {
                _selectedLocation = new WeatherLocation(payload.Location.Trim(), payload.Latitude, payload.Longitude);
                StatusText.Text = $"Локация обновлена: {_selectedLocation.Name}";
            }
            else if (payload.Latitude is not null && payload.Longitude is not null)
            {
                var label = string.IsNullOrWhiteSpace(payload.Label) ? "Пользовательская точка" : payload.Label.Trim();
                _selectedLocation = new WeatherLocation(label, payload.Latitude, payload.Longitude);
                StatusText.Text = $"Координаты обновлены: {label}";
            }
            else if (!isJson)
            {
                _selectedLocation = new WeatherLocation(raw, null, null);
                StatusText.Text = $"Локация обновлена: {_selectedLocation.Name}";
            }

            TryDelete(_inputPath);
            await RefreshWeatherAsync();
            return true;
        }
        finally
        {
            _inputLock.Release();
        }
    }

    private async Task RefreshWeatherAsync()
    {
        RefreshButton.IsEnabled = false;
        try
        {
            StatusText.Text = "Обновляю погоду...";
            var resolvedLocation = await ResolveLocationAsync(_selectedLocation);
            var snapshot = await LoadWeatherAsync(resolvedLocation);
            _selectedLocation = resolvedLocation;
            ApplySnapshot(snapshot);
            await SaveStateAsync();
            _refreshTimer.Interval = TimeSpan.FromMinutes(20);
            _refreshTimer.Start();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не получилось обновить погоду.";
            UpdatedText.Text = ex.Message.Length > 110 ? ex.Message[..110] : ex.Message;
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private async Task<WeatherLocation> ResolveLocationAsync(WeatherLocation location)
    {
        if (location.Latitude is not null && location.Longitude is not null)
        {
            return location;
        }

        var query = Uri.EscapeDataString(location.Name);
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={query}&count=1&language=ru&format=json";
        using var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!document.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array ||
            results.GetArrayLength() == 0)
        {
            throw new InvalidOperationException($"Не нашла локацию: {location.Name}");
        }

        var first = results[0];
        var name = ReadString(first, "name");
        var admin = ReadString(first, "admin1");
        var country = ReadString(first, "country");
        var label = string.Join(", ", new[] { name, admin, country }.Where(value => !string.IsNullOrWhiteSpace(value)));

        return new WeatherLocation(
            string.IsNullOrWhiteSpace(label) ? location.Name : label,
            ReadDouble(first, "latitude"),
            ReadDouble(first, "longitude"));
    }

    private async Task<WeatherSnapshot> LoadWeatherAsync(WeatherLocation location)
    {
        var latitude = location.Latitude ?? throw new InvalidOperationException("Latitude is required.");
        var longitude = location.Longitude ?? throw new InvalidOperationException("Longitude is required.");
        var url =
            $"https://api.open-meteo.com/v1/forecast?latitude={latitude.Value.ToString(CultureInfo.InvariantCulture)}" +
            $"&longitude={longitude.Value.ToString(CultureInfo.InvariantCulture)}" +
            "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m,wind_direction_10m,precipitation,is_day,pressure_msl,cloud_cover" +
            "&daily=sunrise,sunset" +
            "&forecast_days=1" +
            "&timezone=auto";

        using var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        var current = root.GetProperty("current");
        var daily = root.GetProperty("daily");
        var code = ReadInt(current, "weather_code");
        var isDay = ReadInt(current, "is_day") == 1;

        return new WeatherSnapshot
        {
            Location = location.Name,
            Latitude = latitude.Value,
            Longitude = longitude.Value,
            TemperatureC = ReadDouble(current, "temperature_2m"),
            FeelsLikeC = ReadDouble(current, "apparent_temperature"),
            Humidity = ReadInt(current, "relative_humidity_2m"),
            WindSpeed = ReadDouble(current, "wind_speed_10m"),
            WindDirection = DescribeWindDirection(ReadInt(current, "wind_direction_10m")),
            Pressure = ReadDouble(current, "pressure_msl"),
            CloudCover = ReadInt(current, "cloud_cover"),
            Precipitation = ReadDouble(current, "precipitation"),
            Sunrise = ReadFirstString(daily, "sunrise"),
            Sunset = ReadFirstString(daily, "sunset"),
            Condition = DescribeWeather(code, isDay),
            IsDay = isDay,
            UpdatedAt = DateTimeOffset.Now
        };
    }

    private void ApplySnapshot(WeatherSnapshot snapshot)
    {
        LocationText.Text = snapshot.Location;
        UpdatedText.Text = $"Обновлено {snapshot.UpdatedAt:HH:mm}";
        TemperatureText.Text = $"{Math.Round(snapshot.TemperatureC):0}°";
        ConditionText.Text = snapshot.Condition;
        FeelsLikeText.Text = $"Ощущается как {Math.Round(snapshot.FeelsLikeC):0}°";
        LocationMetaText.Text = $"Координаты {snapshot.Latitude:0.###}, {snapshot.Longitude:0.###}";
        WindText.Text = $"{snapshot.WindSpeed:0.#} м/с {snapshot.WindDirection}";
        HumidityText.Text = $"{snapshot.Humidity}%";
        PressureText.Text = $"{snapshot.Pressure:0} гПа";
        CloudText.Text = $"{snapshot.CloudCover}%";
        PrecipitationText.Text = $"{snapshot.Precipitation:0.#} мм";
        FeelsLikeTileText.Text = $"{Math.Round(snapshot.FeelsLikeC):0}°";
        SunriseText.Text = FormatTime(snapshot.Sunrise);
        SunsetText.Text = FormatTime(snapshot.Sunset);
        StatusText.Text = "Готово.";

        SunOrb.Visibility = snapshot.IsDay ? Visibility.Visible : Visibility.Collapsed;
        MoonOrb.Visibility = snapshot.IsDay ? Visibility.Collapsed : Visibility.Visible;
        RainAccent.Visibility = snapshot.Precipitation > 0.05 ? Visibility.Visible : Visibility.Collapsed;

        if (snapshot.IsDay)
        {
            GradientA.Color = ColorFromHex("#FF0E2940");
            GradientB.Color = ColorFromHex("#FF235882");
            GradientC.Color = ColorFromHex("#FF101F32");
            SkyGlow.Fill = Brush("#40A7E8FF");
        }
        else
        {
            GradientA.Color = ColorFromHex("#FF090E1B");
            GradientB.Color = ColorFromHex("#FF1B274A");
            GradientC.Color = ColorFromHex("#FF0C1224");
            SkyGlow.Fill = Brush("#2F9AC8FF");
        }

        CloudOne.Background = Brush(snapshot.Precipitation > 0.2 ? "#E1DCEE" : "#D8EAF7FF");
        CloudTwo.Background = Brush(snapshot.Precipitation > 0.2 ? "#D3D3EE" : "#BFE2F8FF");
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is Button)
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

    private void SunOrb_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        PulseAndSpin(SunOrb, 1.18, 280);
        e.Handled = true;
    }

    private void MoonOrb_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        PulseAndSpin(MoonOrb, 1.14, -180);
        e.Handled = true;
    }

    private void Cloud_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.RenderTransform is not TranslateTransform transform)
        {
            return;
        }

        var dx = _random.Next(-28, 29);
        var dy = _random.Next(-8, 9);
        AnimateDouble(transform, TranslateTransform.XProperty, dx, 0, 340);
        AnimateDouble(transform, TranslateTransform.YProperty, dy, 0, 340);
        e.Handled = true;
    }

    private void SceneCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        SpawnDrop(e.GetPosition(SceneCanvas));
    }

    private void PositionTopRight()
    {
        Left = SystemParameters.WorkArea.Right - Width - 24;
        Top = SystemParameters.WorkArea.Top + 116;
    }

    private void BeginOpenAnimation()
    {
        Card.Opacity = 0;
        Card.RenderTransform = new TranslateTransform(0, -18);
        AnimateDouble(Card, UIElement.OpacityProperty, 0, 1, 220);
        AnimateDouble((TranslateTransform)Card.RenderTransform, TranslateTransform.YProperty, -18, 0, 220);
    }

    private async Task CloseAnimatedAsync()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        var translate = Card.RenderTransform as TranslateTransform ?? new TranslateTransform();
        Card.RenderTransform = translate;
        AnimateDouble(Card, UIElement.OpacityProperty, 1, 0, 170);
        AnimateDouble(translate, TranslateTransform.YProperty, 0, -12, 170);
        await Task.Delay(180);
        Close();
    }

    private void BeginAmbientAnimation()
    {
        AnimateAutoReverse((ScaleTransform)SkyGlow.RenderTransform, ScaleTransform.ScaleXProperty, 1, 1.07, 3200);
        AnimateAutoReverse((ScaleTransform)SkyGlow.RenderTransform, ScaleTransform.ScaleYProperty, 1, 1.07, 3200);
        AnimateAutoReverse((TranslateTransform)CloudOne.RenderTransform, TranslateTransform.XProperty, 0, 8, 3600);
        AnimateAutoReverse((TranslateTransform)CloudTwo.RenderTransform, TranslateTransform.XProperty, 0, -12, 4200);
    }

    private void SpawnDrop(Point point)
    {
        var drop = new Ellipse
        {
            Width = 9,
            Height = 14,
            Fill = Brush("#B0B8E2FF"),
            Opacity = 0.95
        };

        Canvas.SetLeft(drop, Math.Clamp(point.X - 4, 0, SceneCanvas.Width - 10));
        Canvas.SetTop(drop, Math.Clamp(point.Y - 7, 0, SceneCanvas.Height - 14));
        SceneCanvas.Children.Add(drop);

        var fall = new DoubleAnimation(Canvas.GetTop(drop), Canvas.GetTop(drop) + 58 + _random.Next(12, 36), TimeSpan.FromMilliseconds(700))
        {
            FillBehavior = FillBehavior.Stop
        };
        var fade = new DoubleAnimation(0.95, 0, TimeSpan.FromMilliseconds(700))
        {
            FillBehavior = FillBehavior.Stop
        };

        fall.Completed += (_, _) => SceneCanvas.Children.Remove(drop);
        drop.BeginAnimation(Canvas.TopProperty, fall);
        drop.BeginAnimation(UIElement.OpacityProperty, fade);
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
            var state = JsonSerializer.Deserialize<WeatherWidgetState>(File.ReadAllText(_statePath));
            if (state is null || string.IsNullOrWhiteSpace(state.Location))
            {
                return;
            }

            _selectedLocation = new WeatherLocation(state.Location, state.Latitude, state.Longitude);
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

        var state = new WeatherWidgetState
        {
            Location = _selectedLocation.Name,
            Latitude = _selectedLocation.Latitude,
            Longitude = _selectedLocation.Longitude
        };

        await File.WriteAllTextAsync(
            _statePath,
            JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static WeatherInputPayload ParseInput(string raw)
    {
        if (!raw.StartsWith("{", StringComparison.Ordinal))
        {
            return new WeatherInputPayload
            {
                Location = raw
            };
        }

        try
        {
            return JsonSerializer.Deserialize<WeatherInputPayload>(raw, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new WeatherInputPayload { Location = raw };
        }
        catch
        {
            return new WeatherInputPayload { Location = raw };
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

    private static double ReadDouble(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.TryGetDouble(out var number)
            ? number
            : 0;
    }

    private static int ReadInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var number)
            ? number
            : 0;
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static string ReadFirstString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        return value[0].GetString() ?? string.Empty;
    }

    private static string DescribeWeather(int code, bool isDay)
    {
        return code switch
        {
            0 => isDay ? "Ясно" : "Ясная ночь",
            1 => isDay ? "Почти ясно" : "Почти ясная ночь",
            2 => "Переменная облачность",
            3 => "Пасмурно",
            45 or 48 => "Туман",
            51 or 53 or 55 => "Морось",
            61 or 63 or 65 => "Дождь",
            66 or 67 => "Ледяной дождь",
            71 or 73 or 75 => "Снег",
            80 or 81 or 82 => "Ливень",
            95 or 96 or 99 => "Гроза",
            _ => "Непростая погода"
        };
    }

    private static string DescribeWindDirection(int degrees)
    {
        return degrees switch
        {
            >= 337 or < 23 => "С",
            >= 23 and < 68 => "СВ",
            >= 68 and < 113 => "В",
            >= 113 and < 158 => "ЮВ",
            >= 158 and < 203 => "Ю",
            >= 203 and < 248 => "ЮЗ",
            >= 248 and < 293 => "З",
            _ => "СЗ"
        };
    }

    private static string FormatTime(string iso)
    {
        return DateTimeOffset.TryParse(iso, out var parsed)
            ? parsed.ToString("HH:mm")
            : "--:--";
    }

    private static void AnimateAutoReverse(DependencyObject target, DependencyProperty property, double from, double to, int milliseconds)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        storyboard.Begin();
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
        animation.Completed += (_, _) =>
        {
            target.SetValue(property, to);
        };
        storyboard.Begin();
    }

    private static void PulseAndSpin(FrameworkElement element, double scale, double angle)
    {
        if (element.RenderTransform is not TransformGroup group ||
            group.Children[0] is not ScaleTransform scaleTransform ||
            group.Children[1] is not RotateTransform rotateTransform)
        {
            return;
        }

        AnimateDouble(scaleTransform, ScaleTransform.ScaleXProperty, 1, scale, 260);
        AnimateDouble(scaleTransform, ScaleTransform.ScaleYProperty, 1, scale, 260);
        AnimateDouble(rotateTransform, RotateTransform.AngleProperty, 0, angle, 360);
    }

    private static SolidColorBrush Brush(string hex) => new(ColorFromHex(hex));

    private static Color ColorFromHex(string hex)
    {
        return Color.FromArgb(
            Convert.ToByte(hex[1..3], 16),
            Convert.ToByte(hex[3..5], 16),
            Convert.ToByte(hex[5..7], 16),
            Convert.ToByte(hex[7..9], 16));
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

internal sealed record WeatherLocation(string Name, double? Latitude, double? Longitude);

internal sealed class WeatherSnapshot
{
    public string Location { get; init; } = string.Empty;
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public double TemperatureC { get; init; }
    public double FeelsLikeC { get; init; }
    public int Humidity { get; init; }
    public double WindSpeed { get; init; }
    public string WindDirection { get; init; } = string.Empty;
    public double Pressure { get; init; }
    public int CloudCover { get; init; }
    public double Precipitation { get; init; }
    public string Sunrise { get; init; } = string.Empty;
    public string Sunset { get; init; } = string.Empty;
    public string Condition { get; init; } = string.Empty;
    public bool IsDay { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

internal sealed class WeatherInputPayload
{
    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("latitude")]
    public double? Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double? Longitude { get; set; }

    [JsonPropertyName("refresh")]
    public bool Refresh { get; set; }

    [JsonPropertyName("close")]
    public bool Close { get; set; }

    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;
}

internal sealed class WeatherWidgetState
{
    [JsonPropertyName("location")]
    public string Location { get; set; } = string.Empty;

    [JsonPropertyName("latitude")]
    public double? Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double? Longitude { get; set; }
}
