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
    private readonly ModelDiscoveryService _modelDiscovery = new();
    private readonly ShellConfigService _config = App.ConfigService;
    private readonly RuntimeCatalogService _runtimeCatalog = App.RuntimeCatalog;
    private readonly RuntimeWidgetService _runtimeWidgets = new();
    private readonly IReadOnlyList<ProviderDescriptor> _remoteProviders = ProviderCatalog.RemoteOnly;
    private readonly IReadOnlyList<ProviderDescriptor> _modelProviders = ProviderCatalog.All;
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
        appWindow.Resize(new Windows.Graphics.SizeInt32(980, 760));
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
            await EnqueueOnUiAsync(() =>
            {
                ProvidersList.ItemsSource = _remoteProviders;
                LocalIdleSecondsBox.Text = Math.Max(10, _config.Current.LocalAi.IdleUnloadSeconds).ToString();
                LocalModelsList.ItemsSource = _config.Current.LocalAi.Models.OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase).ToList();

                BindRouteSelector(PrimaryProviderCombo, _config.Current.Models.Primary);
                BindRouteSelector(AnalysisProviderCombo, _config.Current.Models.Analysis);
                BindRouteSelector(VisionProviderCombo, _config.Current.Models.Vision);

                PrimaryThinkingToggle.IsChecked = _config.Current.Models.PrimaryThinking;
                PrimaryMcpThinkingToggle.IsChecked = _config.Current.Models.PrimaryMcpThinking;
                AnalysisThinkingToggle.IsChecked = _config.Current.Models.AnalysisThinking;
                AnalysisMcpThinkingToggle.IsChecked = _config.Current.Models.AnalysisMcpThinking;
                SeparateAnalysisToggle.IsChecked = _config.Current.Models.UseSeparateAnalysis;
                SeparateVisionToggle.IsChecked = _config.Current.Models.UseSeparateVision;
                ApplySeparateRouteVisibility();
                OperationStatusText.Text = string.Empty;
            });

            await LoadModelChoicesAsync(PrimaryProviderCombo, PrimaryModelCombo, _config.Current.Models.Primary.Model);
            await LoadModelChoicesAsync(AnalysisProviderCombo, AnalysisModelCombo, _config.Current.Models.Analysis.Model);
            await LoadModelChoicesAsync(VisionProviderCombo, VisionModelCombo, _config.Current.Models.Vision.Model);

            var tools = await _runtimeCatalog.LoadToolsAsync();
            var widgets = await _runtimeCatalog.LoadWidgetsAsync();

            await EnqueueOnUiAsync(() =>
            {
                ToolsList.ItemsSource = tools;
                WidgetsList.ItemsSource = widgets;
                ApplyThinkingAvailability();
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

    private void BindRouteSelector(ComboBox comboBox, ModelRoute route)
    {
        comboBox.ItemsSource = _modelProviders;
        comboBox.DisplayMemberPath = nameof(ProviderDescriptor.Name);
        comboBox.SelectedItem = _modelProviders.FirstOrDefault(provider => provider.Id == route.Provider) ?? _modelProviders[0];
    }

    private async Task LoadModelChoicesAsync(ComboBox providerCombo, ComboBox modelCombo, string selectedModel)
    {
        ProviderDescriptor? provider = null;
        await EnqueueOnUiAsync(() =>
        {
            provider = providerCombo.SelectedItem as ProviderDescriptor;
            modelCombo.PlaceholderText = "Загрузка моделей...";
            modelCombo.ItemsSource = null;
            modelCombo.SelectedItem = null;
        });

        if (provider is null)
        {
            return;
        }

        var apiKey = _config.Current.Providers.GetValueOrDefault(provider.Id)?.ApiKey ?? string.Empty;
        IReadOnlyList<ModelChoice> models;
        var placeholder = "Выберите модель";

        try
        {
            models = await _modelDiscovery.LoadModelsAsync(provider, apiKey, _config.Current);
            if (provider.Id == "local")
            {
                placeholder = models.Count == 0
                    ? "Добавь GGUF модель в разделе Локальные ИИ"
                    : "Выберите локальную модель";
            }
            else if (string.IsNullOrWhiteSpace(apiKey))
            {
                placeholder = "Введите API key";
            }
            else if (models.Count == 0)
            {
                placeholder = "Модели не найдены";
            }
        }
        catch (Exception ex)
        {
            StartupLogService.Error($"Model load failed for provider {provider.Id}: {ex}");
            models = [];
            placeholder = "Ошибка загрузки моделей";
        }

        await EnqueueOnUiAsync(() =>
        {
            modelCombo.ItemsSource = models;
            modelCombo.SelectedItem = models.FirstOrDefault(model => model.Id == selectedModel) ?? models.FirstOrDefault();
            modelCombo.PlaceholderText = placeholder;
        });

        await EnqueueOnUiAsync(ApplyThinkingAvailability);

        if (!_isLoading)
        {
            await SyncModelSettingsAsync();
        }
    }

    private void ShowTab(FrameworkElement view)
    {
        ProvidersView.Visibility = Visibility.Collapsed;
        LocalAiView.Visibility = Visibility.Collapsed;
        ModelsView.Visibility = Visibility.Collapsed;
        ToolsView.Visibility = Visibility.Collapsed;
        WidgetsView.Visibility = Visibility.Collapsed;
        view.Visibility = Visibility.Visible;
    }

    private void ProvidersTabButton_Click(object sender, RoutedEventArgs e) => ShowTab(ProvidersView);

    private void LocalAiTabButton_Click(object sender, RoutedEventArgs e) => ShowTab(LocalAiView);

    private void ModelsTabButton_Click(object sender, RoutedEventArgs e) => ShowTab(ModelsView);

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
            await SyncModelSettingsAsync();
            var path = await _config.CreateBackupSnapshotAsync();
            await EnqueueOnUiAsync(() => OperationStatusText.Text = $"Бэкап создан: {path}");
        }, "backup settings");
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        await RunUiSafeAsync(async () =>
        {
            await SyncModelSettingsAsync();
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

    private void ProviderApiKeyBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.Tag is not string providerId)
        {
            return;
        }

        textBox.Text = _config.Current.Providers.GetValueOrDefault(providerId)?.ApiKey ?? string.Empty;
    }

    private void ProviderApiKeyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.Tag is not string providerId)
        {
            return;
        }

        if (!_config.Current.Providers.TryGetValue(providerId, out var provider))
        {
            provider = new ProviderConfig();
            _config.Current.Providers[providerId] = provider;
        }

        provider.ApiKey = textBox.Text.Trim();
    }

    private async void ProviderApiKeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.Tag is not string providerId)
        {
            return;
        }

        await RunUiSafeAsync(async () =>
        {
            await RefreshProvidersUsingKeyAsync(providerId);
            await SaveCurrentConfigAsync("API key updated.");
        }, $"provider key update {providerId}");
    }

    private async void LocalIdleSecondsBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        await RunUiSafeAsync(
            () => SaveCurrentConfigAsync("Параметры локальных ИИ обновлены."),
            "local idle change");
    }

    private async void AddLocalModel_Click(object sender, RoutedEventArgs e)
    {
        await RunUiSafeAsync(async () =>
        {
            var path = LocalModelPathBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException("Укажи путь к .gguf файлу.");
            }

            var name = string.IsNullOrWhiteSpace(LocalModelNameBox.Text)
                ? Path.GetFileNameWithoutExtension(path)
                : LocalModelNameBox.Text.Trim();

            _config.Current.LocalAi.Models.Add(new LocalModelConfig
            {
                Id = Guid.NewGuid().ToString("N"),
                Name = name,
                ModelPath = path,
                ContextSize = ParseInt(LocalModelContextBox.Text, 4096, 512, 131072),
                GpuLayers = ParseInt(LocalModelGpuLayersBox.Text, 0, 0, 256),
                SupportsThinking = LocalModelSupportsThinkingToggle.IsChecked == true
            });

            LocalModelNameBox.Text = string.Empty;
            LocalModelPathBox.Text = string.Empty;
            LocalModelContextBox.Text = string.Empty;
            LocalModelGpuLayersBox.Text = string.Empty;
            LocalModelSupportsThinkingToggle.IsChecked = false;

            await SaveCurrentConfigAsync("Локальная модель добавлена.");
            await LoadSafeAsync();
        }, "add local model");
    }

    private async void RemoveLocalModel_Click(object sender, RoutedEventArgs e)
    {
        await RunUiSafeAsync(async () =>
        {
            if (sender is not Button button || button.Tag is not string modelId)
            {
                return;
            }

            _config.Current.LocalAi.Models.RemoveAll(model => string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase));
            await SaveCurrentConfigAsync("Локальная модель удалена.");
            await LoadSafeAsync();
        }, "remove local model");
    }

    private async Task RefreshProvidersUsingKeyAsync(string providerId)
    {
        if ((PrimaryProviderCombo.SelectedItem as ProviderDescriptor)?.Id == providerId)
        {
            await LoadModelChoicesAsync(PrimaryProviderCombo, PrimaryModelCombo, CaptureRoute(PrimaryProviderCombo, PrimaryModelCombo).Model);
        }

        if ((AnalysisProviderCombo.SelectedItem as ProviderDescriptor)?.Id == providerId)
        {
            await LoadModelChoicesAsync(AnalysisProviderCombo, AnalysisModelCombo, CaptureRoute(AnalysisProviderCombo, AnalysisModelCombo).Model);
        }

        if ((VisionProviderCombo.SelectedItem as ProviderDescriptor)?.Id == providerId)
        {
            await LoadModelChoicesAsync(VisionProviderCombo, VisionModelCombo, CaptureRoute(VisionProviderCombo, VisionModelCombo).Model);
        }
    }

    private async void PrimaryProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => await RunUiSafeAsync(
            () => ProviderSelectionChangedAsync(PrimaryProviderCombo, PrimaryModelCombo),
            "primary provider selection");

    private async void AnalysisProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => await RunUiSafeAsync(
            () => ProviderSelectionChangedAsync(AnalysisProviderCombo, AnalysisModelCombo),
            "analysis provider selection");

    private async void VisionProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => await RunUiSafeAsync(
            () => ProviderSelectionChangedAsync(VisionProviderCombo, VisionModelCombo),
            "vision provider selection");

    private async Task ProviderSelectionChangedAsync(ComboBox providerCombo, ComboBox modelCombo)
    {
        if (!_isLoading)
        {
            SyncModelSettingsOnUi();
        }

        await LoadModelChoicesAsync(providerCombo, modelCombo, string.Empty);

        if (!_isLoading)
        {
            await SaveCurrentConfigAsync("Модельный провайдер обновлен.");
        }
    }

    private async void ModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        await RunUiSafeAsync(async () =>
        {
            ApplyThinkingAvailability();
            await SaveCurrentConfigAsync("Модель обновлена.");
        }, "model selection");
    }

    private async void ModelSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        await RunUiSafeAsync(
            () => SaveCurrentConfigAsync("Параметр модели обновлен."),
            "model toggle");
    }

    private async void SeparateToggle_Changed(object sender, RoutedEventArgs e)
    {
        ApplySeparateRouteVisibility();
        if (_isLoading)
        {
            return;
        }

        await RunUiSafeAsync(
            () => SaveCurrentConfigAsync("Маршрутизация моделей обновлена."),
            "separate route toggle");
    }

    private void ApplySeparateRouteVisibility()
    {
        AnalysisPanel.Visibility = SeparateAnalysisToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        VisionPanel.Visibility = SeparateVisionToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyThinkingAvailability()
    {
        ApplyThinkingAvailability(PrimaryModelCombo, PrimaryThinkingToggle);
        ApplyThinkingAvailability(AnalysisModelCombo, AnalysisThinkingToggle);
    }

    private static void ApplyThinkingAvailability(ComboBox modelCombo, CheckBox thinkingToggle)
    {
        var selected = modelCombo.SelectedItem as ModelChoice;
        var enabled = selected?.SupportsThinking == true;
        thinkingToggle.IsEnabled = enabled;
        if (!enabled)
        {
            thinkingToggle.IsChecked = false;
        }
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
            await SyncModelSettingsAsync();
            await _config.SaveAsync();
            await EnqueueOnUiAsync(() => OperationStatusText.Text = statusText);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private Task SyncModelSettingsAsync()
        => EnqueueOnUiAsync(SyncModelSettingsOnUi);

    private void SyncModelSettingsOnUi()
    {
        _config.Current.LocalAi.IdleUnloadSeconds = ParseInt(LocalIdleSecondsBox.Text, 60, 10, 3600);
        _config.Current.Models.Primary = CaptureRoute(PrimaryProviderCombo, PrimaryModelCombo);
        _config.Current.Models.PrimaryThinking = PrimaryThinkingToggle.IsChecked == true;
        _config.Current.Models.PrimaryMcpThinking = PrimaryMcpThinkingToggle.IsChecked == true;
        _config.Current.Models.UseSeparateAnalysis = SeparateAnalysisToggle.IsChecked == true;
        _config.Current.Models.Analysis = CaptureRoute(AnalysisProviderCombo, AnalysisModelCombo);
        _config.Current.Models.AnalysisThinking = AnalysisThinkingToggle.IsChecked == true;
        _config.Current.Models.AnalysisMcpThinking = AnalysisMcpThinkingToggle.IsChecked == true;
        _config.Current.Models.UseSeparateVision = SeparateVisionToggle.IsChecked == true;
        _config.Current.Models.Vision = CaptureRoute(VisionProviderCombo, VisionModelCombo);
    }

    private static ModelRoute CaptureRoute(ComboBox providerCombo, ComboBox modelCombo)
    {
        var provider = (providerCombo.SelectedItem as ProviderDescriptor)?.Id ?? "sosiskibot";
        var model = (modelCombo.SelectedItem as ModelChoice)?.Id ?? string.Empty;
        return new ModelRoute(provider, model);
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
            SyncModelSettingsOnUi();
            _config.Save();
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
