using System.Runtime.InteropServices;
using AgentShell.Models;
using AgentShell.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;

namespace AgentShell;

public sealed partial class SettingsWindow : Window
{
    private readonly ShellConfigService _config = App.ConfigService;
    private readonly RuntimeCatalogService _runtimeCatalog = App.RuntimeCatalog;
    private readonly RuntimeWidgetService _runtimeWidgets = new();
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private bool _isLoading;

    public SettingsWindow()
    {
        InitializeComponent();
        _dispatcherQueue = DispatcherQueue;
        ConfigureWindow();
        HookCloseBehavior();
        _ = LoadSafeAsync();
    }

    public void BringToFront()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        ShowWindow(hwnd, SwShow);
        Activate();
        SetForegroundWindow(hwnd);
    }

    private void ConfigureWindow()
    {
        var appWindow = GetAppWindow();
        appWindow.Title = "AI Agent Settings";
        appWindow.Resize(new Windows.Graphics.SizeInt32(980, 780));
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, true);
            presenter.IsResizable = true;
            presenter.IsMaximizable = true;
            presenter.IsMinimizable = true;
        }
    }

    private void HookCloseBehavior()
    {
        GetAppWindow().Closing += SettingsWindow_Closing;
    }

    private async Task LoadSafeAsync()
    {
        _isLoading = true;
        try
        {
            var settings = _config.Current.LocalAi;
            var agent = _config.Current.Agent;

            await EnqueueOnUiAsync(() =>
            {
                KoboldPathBox.Text = settings.KoboldCppPath;
                ModelPathBox.Text = settings.ModelPath;
                MmprojPathBox.Text = settings.MmprojPath;
                ContextBox.Text = settings.ContextSize.ToString();
                GpuLayersBox.Text = settings.GpuLayers.ToString();
                QuantKvBox.Text = settings.QuantKv;
                PortBox.Text = settings.Port.ToString();
                ThreadsBox.Text = settings.Threads.ToString();
                ImageMaxPixelsBox.Text = settings.ImageMaxPixels.ToString();
                MaxStepsBox.Text = agent.MaxSteps.ToString();
                IdleUnloadBox.Text = Math.Max(60, settings.IdleUnloadSeconds).ToString();
                ExtraArgsBox.Text = settings.ExtraArgs;
                MmprojOnCpuToggle.IsChecked = settings.MmprojOnCpu;
                OcrHintsToggle.IsChecked = agent.UseOcrHints;
                TrayRecoveryToggle.IsChecked = agent.TrayRecovery;
                OperationStatusText.Text = string.Empty;
            });

            await RefreshDetectedPathsAsync();

            var tools = await _runtimeCatalog.LoadToolsAsync();
            var widgets = await _runtimeCatalog.LoadWidgetsAsync();

            await EnqueueOnUiAsync(() =>
            {
                ToolsList.ItemsSource = tools;
                WidgetsList.ItemsSource = widgets;
            });
        }
        catch (Exception ex)
        {
            StartupLogService.Error($"Settings load failed: {ex}");
            await EnqueueOnUiAsync(() => OperationStatusText.Text = $"Ошибка загрузки настроек: {ex.Message}");
        }
        finally
        {
            _isLoading = false;
        }
    }

    private async Task RefreshDetectedPathsAsync()
    {
        var description = await Task.Run(() => LocalRuntimeLocator.Describe(_config.Current.LocalAi));
        await EnqueueOnUiAsync(() => DetectedPathsText.Text = description);
    }

    private void ShowTab(FrameworkElement view)
    {
        LocalAiView.Visibility = Visibility.Collapsed;
        ToolsView.Visibility = Visibility.Collapsed;
        WidgetsView.Visibility = Visibility.Collapsed;
        view.Visibility = Visibility.Visible;
    }

    private void LocalAiTabButton_Click(object sender, RoutedEventArgs e) => ShowTab(LocalAiView);

    private void ToolsTabButton_Click(object sender, RoutedEventArgs e) => ShowTab(ToolsView);

    private void WidgetsTabButton_Click(object sender, RoutedEventArgs e) => ShowTab(WidgetsView);

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiSafeAsync(
            () => SaveCurrentConfigAsync($"Сохранено: {_config.NativeConfigPath}"),
            "save settings");
    }

    private async void BackupButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiSafeAsync(async () =>
        {
            await SyncSettingsAsync();
            var path = await _config.CreateBackupSnapshotAsync();
            await EnqueueOnUiAsync(() => OperationStatusText.Text = $"Бэкап создан: {path}");
        }, "backup settings");
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiSafeAsync(async () =>
        {
            await SyncSettingsAsync();
            var path = await _config.ExportAsync();
            await EnqueueOnUiAsync(() => OperationStatusText.Text = $"Экспорт создан: {path}");
        }, "export settings");
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiSafeAsync(async () =>
        {
            var path = await _config.RestoreLatestBackupAsync();
            if (string.IsNullOrWhiteSpace(path))
            {
                await EnqueueOnUiAsync(() => OperationStatusText.Text = "Бэкапы не найдены.");
                return;
            }

            await EnqueueOnUiAsync(() => OperationStatusText.Text = $"Восстановлено: {path}");
            await LoadSafeAsync();
        }, "restore settings");
    }

    private async void TestServerButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiSafeAsync(async () =>
        {
            await SyncSettingsAsync();
            await EnqueueOnUiAsync(() => OperationStatusText.Text = "Запускаю koboldcpp, первая загрузка модели может занять до минуты...");

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            var baseUrl = await App.LocalKobold.EnsureServerAsync(_config.Current, timeout.Token);

            var message = App.LocalKobold.IsRunning
                ? $"koboldcpp работает: {baseUrl}"
                : "koboldcpp не запущен.";

            if (!string.IsNullOrWhiteSpace(App.LocalKobold.LastError))
            {
                message += $" Внимание: {App.LocalKobold.LastError}";
            }

            await EnqueueOnUiAsync(() => OperationStatusText.Text = message);
        }, "test local server");
    }

    private async void StopServerButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiSafeAsync(async () =>
        {
            App.LocalKobold.StopServer();
            await EnqueueOnUiAsync(() => OperationStatusText.Text = "koboldcpp остановлен, VRAM освобождена.");
        }, "stop local server");
    }

    private async void RemoveTool_Click(object sender, RoutedEventArgs e)
    {
        await RunUiSafeAsync(
            () => RemoveRuntimeItemAsync(sender, isWidget: false),
            "remove tool");
    }

    private async void RemoveWidget_Click(object sender, RoutedEventArgs e)
    {
        await RunUiSafeAsync(
            () => RemoveRuntimeItemAsync(sender, isWidget: true),
            "remove widget");
    }

    private async void TestWidget_Click(object sender, RoutedEventArgs e)
    {
        await RunUiSafeAsync(
            () => TestWidgetAsync(sender),
            "test widget");
    }

    private async Task TestWidgetAsync(object sender)
    {
        if (sender is not Button button || button.Tag is not string path || !Directory.Exists(path))
        {
            return;
        }

        var result = await _runtimeWidgets.TestLaunchAsync(path, CancellationToken.None);
        await EnqueueOnUiAsync(() => OperationStatusText.Text = result);
    }

    private async Task RemoveRuntimeItemAsync(object sender, bool isWidget)
    {
        if (sender is not Button button || button.Tag is not string path || !Directory.Exists(path))
        {
            return;
        }

        Directory.Delete(path, true);
        if (isWidget)
        {
            var widgets = await _runtimeCatalog.LoadWidgetsAsync();
            await EnqueueOnUiAsync(() =>
            {
                WidgetsList.ItemsSource = widgets;
                OperationStatusText.Text = "Виджет удален.";
            });
        }
        else
        {
            var tools = await _runtimeCatalog.LoadToolsAsync();
            await EnqueueOnUiAsync(() =>
            {
                ToolsList.ItemsSource = tools;
                OperationStatusText.Text = "Тулз удален.";
            });
        }
    }

    private async Task SaveCurrentConfigAsync(string statusText)
    {
        await _saveLock.WaitAsync();
        try
        {
            await SyncSettingsAsync();
            await _config.SaveAsync();
            await EnqueueOnUiAsync(() => OperationStatusText.Text = statusText);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private Task SyncSettingsAsync() => EnqueueOnUiAsync(SyncSettingsOnUi);

    private void SyncSettingsOnUi()
    {
        var settings = _config.Current.LocalAi;
        settings.KoboldCppPath = KoboldPathBox.Text.Trim();
        settings.ModelPath = ModelPathBox.Text.Trim();
        settings.MmprojPath = MmprojPathBox.Text.Trim();
        settings.ContextSize = ParseInt(ContextBox.Text, 8192, 2048, 131072);
        settings.GpuLayers = ParseInt(GpuLayersBox.Text, 999, 0, 999);
        settings.QuantKv = string.IsNullOrWhiteSpace(QuantKvBox.Text) ? "q8_0" : QuantKvBox.Text.Trim();
        settings.Port = ParseInt(PortBox.Text, 5002, 1024, 65535);
        settings.Threads = ParseInt(ThreadsBox.Text, 6, 1, 64);
        settings.ImageMaxPixels = ParseInt(ImageMaxPixelsBox.Text, 1310720, 262144, 13107200);
        settings.IdleUnloadSeconds = ParseInt(IdleUnloadBox.Text, 1800, 60, 86400);
        settings.ExtraArgs = ExtraArgsBox.Text.Trim();
        settings.MmprojOnCpu = MmprojOnCpuToggle.IsChecked == true;

        _config.Current.Agent.MaxSteps = ParseInt(MaxStepsBox.Text, 40, 1, 200);
        _config.Current.Agent.UseOcrHints = OcrHintsToggle.IsChecked == true;
        _config.Current.Agent.TrayRecovery = TrayRecoveryToggle.IsChecked == true;
    }

    private static int ParseInt(string? raw, int fallback, int min, int max)
    {
        return int.TryParse(raw?.Trim(), out var value)
            ? Math.Clamp(value, min, max)
            : fallback;
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

    private async Task RunUiSafeAsync(Func<Task> action, string context)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            StartupLogService.Error($"{context} failed: {ex}");
            await EnqueueOnUiAsync(() => OperationStatusText.Text = $"Ошибка: {ex.Message}");
        }
    }

    private void SettingsWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        try
        {
            SyncSettingsOnUi();
            _config.Save();
            _ = RefreshDetectedPathsAsync();
        }
        catch (Exception ex)
        {
            StartupLogService.Error($"Settings close save failed: {ex}");
        }

        StartupLogService.Info("Settings close intercepted, hiding instead.");
        ShowWindow(WindowNative.GetWindowHandle(this), SwHide);
    }

    private AppWindow GetAppWindow()
    {
        return AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this)));
    }

    private const int SwHide = 0;
    private const int SwShow = 5;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);
}
