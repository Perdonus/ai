using System.Globalization;
using System.Net.Http;
using System.Text.Json;
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

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        _refreshTimer.Tick += RefreshTimer_Tick;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        PositionTopRight();
        BeginOpenAnimation();
        BeginAmbientAnimation();
        await RefreshWeatherAsync();
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshWeatherAsync();
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshWeatherAsync();
    }

    private async Task RefreshWeatherAsync()
    {
        RefreshButton.IsEnabled = false;
        try
        {
            var snapshot = await LoadWeatherAsync();
            ApplySnapshot(snapshot);
            _refreshTimer.Interval = TimeSpan.FromMinutes(20);
            _refreshTimer.Start();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не получилось обновить погоду.";
            UpdatedText.Text = ex.Message.Length > 80 ? ex.Message[..80] : ex.Message;
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private async Task<WeatherSnapshot> LoadWeatherAsync()
    {
        const double latitude = 55.7558;
        const double longitude = 37.6176;
        var url =
            $"https://api.open-meteo.com/v1/forecast?latitude={latitude.ToString(CultureInfo.InvariantCulture)}" +
            $"&longitude={longitude.ToString(CultureInfo.InvariantCulture)}" +
            "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m,wind_direction_10m,precipitation,is_day" +
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
            TemperatureC = ReadDouble(current, "temperature_2m"),
            FeelsLikeC = ReadDouble(current, "apparent_temperature"),
            Humidity = ReadInt(current, "relative_humidity_2m"),
            WindSpeed = ReadDouble(current, "wind_speed_10m"),
            WindDirection = DescribeWindDirection(ReadInt(current, "wind_direction_10m")),
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
        LocationText.Text = "Москва";
        UpdatedText.Text = $"Обновлено {snapshot.UpdatedAt:HH:mm}";
        TemperatureText.Text = $"{Math.Round(snapshot.TemperatureC):0}°";
        ConditionText.Text = snapshot.Condition;
        FeelsLikeText.Text = $"Ощущается как {Math.Round(snapshot.FeelsLikeC):0}°";
        WindText.Text = $"{snapshot.WindSpeed:0.#} м/с {snapshot.WindDirection}";
        HumidityText.Text = $"{snapshot.Humidity}%";
        PrecipitationText.Text = $"{snapshot.Precipitation:0.#} мм";
        SunTimeText.Text = snapshot.IsDay
            ? $"закат {FormatTime(snapshot.Sunset)}"
            : $"восход {FormatTime(snapshot.Sunrise)}";
        StatusText.Text = "Готово.";

        SunOrb.Visibility = snapshot.IsDay ? Visibility.Visible : Visibility.Collapsed;
        MoonOrb.Visibility = snapshot.IsDay ? Visibility.Collapsed : Visibility.Visible;
        RainAccent.Visibility = snapshot.Precipitation > 0.05 ? Visibility.Visible : Visibility.Collapsed;

        if (snapshot.IsDay)
        {
            GradientA.Color = ColorFromHex("#FF0E2940");
            GradientB.Color = ColorFromHex("#FF235882");
            GradientC.Color = ColorFromHex("#FF11263A");
            SkyGlow.Fill = Brush("#40A7E8FF");
        }
        else
        {
            GradientA.Color = ColorFromHex("#FF0B0F1F");
            GradientB.Color = ColorFromHex("#FF20284B");
            GradientC.Color = ColorFromHex("#FF0F1220");
            SkyGlow.Fill = Brush("#2DADC4FF");
        }

        CloudOne.Background = Brush(snapshot.Precipitation > 0.2 ? "#E3D7E5F7" : "#D8EAF7FF");
        CloudTwo.Background = Brush(snapshot.Precipitation > 0.2 ? "#CFCAE3F4" : "#BFE2F8FF");
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

        var dx = _random.Next(-26, 27);
        var dy = _random.Next(-8, 9);
        AnimateDouble(transform, TranslateTransform.XProperty, dx, 0, 340);
        AnimateDouble(transform, TranslateTransform.YProperty, dy, 0, 340);
        e.Handled = true;
    }

    private void SceneCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var position = e.GetPosition(SceneCanvas);
        SpawnDrop(position);
    }

    private void BeginOpenAnimation()
    {
        Card.Opacity = 0;
        Card.RenderTransform = new TranslateTransform(0, -18);
        AnimateDouble(Card, UIElement.OpacityProperty, 0, 1, 220);
        AnimateDouble((TranslateTransform)Card.RenderTransform, TranslateTransform.YProperty, -18, 0, 220);
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

    private void PositionTopRight()
    {
        Left = SystemParameters.WorkArea.Right - Width - 24;
        Top = SystemParameters.WorkArea.Top + 96;
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

    private static void AnimateAutoReverse(Animatable target, DependencyProperty property, double from, double to, int milliseconds)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        target.BeginAnimation(property, animation);
    }

    private static void AnimateDouble(Animatable target, DependencyProperty property, double from, double to, int milliseconds)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            if (target is DependencyObject dependencyObject)
            {
                dependencyObject.SetValue(property, to);
            }
        };
        target.BeginAnimation(property, animation);
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
}

internal sealed class WeatherSnapshot
{
    public double TemperatureC { get; init; }
    public double FeelsLikeC { get; init; }
    public int Humidity { get; init; }
    public double WindSpeed { get; init; }
    public string WindDirection { get; init; } = string.Empty;
    public double Precipitation { get; init; }
    public string Sunrise { get; init; } = string.Empty;
    public string Sunset { get; init; } = string.Empty;
    public string Condition { get; init; } = string.Empty;
    public bool IsDay { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
