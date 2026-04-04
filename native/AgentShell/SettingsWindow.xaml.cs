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
    private readonly IReadOnlyList<ProviderDescriptor> _providers = ProviderCatalog.All;
    private readonly DispatcherQueue _dispatcherQueue;

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
        appWindow.IsShownInSwitchers = false;
        appWindow.Resize(new Windows.Graphics.SizeInt32(980, 760));
        ApplyToolWindowStyle();
    }

    private void HookCloseBehavior()
    {
        GetAppWindow().Closing += SettingsWindow_Closing;
    }

    private async Task LoadSafeAsync()
    {
        try
        {
            await EnqueueOnUiAsync(() =>
            {
                ProvidersList.ItemsSource = _providers;

                BindRouteSelector(PrimaryProviderCombo, _config.Current.Models.Primary);
                BindRouteSelector(AnalysisProviderCombo, _config.Current.Models.Analysis);
                BindRouteSelector(VisionProviderCombo, _config.Current.Models.Vision);
                BindRouteSelector(OcrProviderCombo, _config.Current.Models.Ocr);

                PrimaryThinkingToggle.IsChecked = _config.Current.Models.PrimaryThinking;
                AnalysisThinkingToggle.IsChecked = _config.Current.Models.AnalysisThinking;
                SeparateAnalysisToggle.IsChecked = _config.Current.Models.UseSeparateAnalysis;
                SeparateVisionToggle.IsChecked = _config.Current.Models.UseSeparateVision;
                SeparateOcrToggle.IsChecked = _config.Current.Models.UseSeparateOcr;
                ApplySeparateRouteVisibility();
                OperationStatusText.Text = string.Empty;
            });

            await LoadModelChoicesAsync(PrimaryProviderCombo, PrimaryModelCombo, _config.Current.Models.Primary.Model);
            await LoadModelChoicesAsync(AnalysisProviderCombo, AnalysisModelCombo, _config.Current.Models.Analysis.Model);
            await LoadModelChoicesAsync(VisionProviderCombo, VisionModelCombo, _config.Current.Models.Vision.Model);
            await LoadModelChoicesAsync(OcrProviderCombo, OcrModelCombo, _config.Current.Models.Ocr.Model);

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
    }

    private void BindRouteSelector(ComboBox comboBox, ModelRoute route)
    {
        comboBox.ItemsSource = _providers;
        comboBox.DisplayMemberPath = nameof(ProviderDescriptor.Name);
        comboBox.SelectedItem = _providers.FirstOrDefault(provider => provider.Id == route.Provider) ?? _providers[0];
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
        IReadOnlyList<string> models;
        var placeholder = "Выберите модель";

        try
        {
            models = await _modelDiscovery.LoadModelsAsync(provider, apiKey);
            if (string.IsNullOrWhiteSpace(apiKey))
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
            modelCombo.SelectedItem = models.FirstOrDefault(model => model == selectedModel) ?? models.FirstOrDefault();
            modelCombo.PlaceholderText = placeholder;
        });
    }

    private void ShowTab(FrameworkElement view)
    {
        ProvidersView.Visibility = Visibility.Collapsed;
        ModelsView.Visibility = Visibility.Collapsed;
        ToolsView.Visibility = Visibility.Collapsed;
        WidgetsView.Visibility = Visibility.Collapsed;
        view.Visibility = Visibility.Visible;
    }

    private void ProvidersTabButton_Click(object sender, RoutedEventArgs e) => ShowTab(ProvidersView);

    private void ModelsTabButton_Click(object sender, RoutedEventArgs e) => ShowTab(ModelsView);

    private void ToolsTabButton_Click(object sender, RoutedEventArgs e) => ShowTab(ToolsView);

    private void WidgetsTabButton_Click(object sender, RoutedEventArgs e) => ShowTab(WidgetsView);

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SyncModelSettings();
        await _config.SaveAsync();
        OperationStatusText.Text = $"Сохранено: {_config.NativeConfigPath}";
    }

    private async void BackupButton_Click(object sender, RoutedEventArgs e)
    {
        SyncModelSettings();
        var path = await _config.CreateBackupSnapshotAsync();
        OperationStatusText.Text = $"Бэкап создан: {path}";
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        SyncModelSettings();
        var path = await _config.ExportAsync();
        OperationStatusText.Text = $"Экспорт создан: {path}";
    }

    private async void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        var path = await _config.RestoreLatestBackupAsync();
        if (string.IsNullOrWhiteSpace(path))
        {
            OperationStatusText.Text = "Бэкапы не найдены.";
            return;
        }

        OperationStatusText.Text = $"Восстановлено: {path}";
        await LoadSafeAsync();
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

        await RefreshProvidersUsingKeyAsync(providerId);
    }

    private async Task RefreshProvidersUsingKeyAsync(string providerId)
    {
        if ((PrimaryProviderCombo.SelectedItem as ProviderDescriptor)?.Id == providerId)
        {
            await LoadModelChoicesAsync(PrimaryProviderCombo, PrimaryModelCombo, string.Empty);
        }

        if ((AnalysisProviderCombo.SelectedItem as ProviderDescriptor)?.Id == providerId)
        {
            await LoadModelChoicesAsync(AnalysisProviderCombo, AnalysisModelCombo, string.Empty);
        }

        if ((VisionProviderCombo.SelectedItem as ProviderDescriptor)?.Id == providerId)
        {
            await LoadModelChoicesAsync(VisionProviderCombo, VisionModelCombo, string.Empty);
        }

        if ((OcrProviderCombo.SelectedItem as ProviderDescriptor)?.Id == providerId)
        {
            await LoadModelChoicesAsync(OcrProviderCombo, OcrModelCombo, string.Empty);
        }
    }

    private async void PrimaryProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => await LoadModelChoicesAsync(PrimaryProviderCombo, PrimaryModelCombo, string.Empty);

    private async void AnalysisProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => await LoadModelChoicesAsync(AnalysisProviderCombo, AnalysisModelCombo, string.Empty);

    private async void VisionProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => await LoadModelChoicesAsync(VisionProviderCombo, VisionModelCombo, string.Empty);

    private async void OcrProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => await LoadModelChoicesAsync(OcrProviderCombo, OcrModelCombo, string.Empty);

    private void SeparateToggle_Changed(object sender, RoutedEventArgs e)
    {
        ApplySeparateRouteVisibility();
    }

    private void ApplySeparateRouteVisibility()
    {
        AnalysisPanel.Visibility = SeparateAnalysisToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        VisionPanel.Visibility = SeparateVisionToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        OcrPanel.Visibility = SeparateOcrToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RemoveTool_Click(object sender, RoutedEventArgs e)
    {
        await RemoveRuntimeItemAsync(sender, isWidget: false);
    }

    private async void RemoveWidget_Click(object sender, RoutedEventArgs e)
    {
        await RemoveRuntimeItemAsync(sender, isWidget: true);
    }

    private void TestWidget_Click(object sender, RoutedEventArgs e)
    {
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
            WidgetsList.ItemsSource = await _runtimeCatalog.LoadWidgetsAsync();
        }
        else
        {
            ToolsList.ItemsSource = await _runtimeCatalog.LoadToolsAsync();
        }
    }

    private void SyncModelSettings()
    {
        _config.Current.Models.Primary = CaptureRoute(PrimaryProviderCombo, PrimaryModelCombo);
        _config.Current.Models.PrimaryThinking = PrimaryThinkingToggle.IsChecked == true;
        _config.Current.Models.UseSeparateAnalysis = SeparateAnalysisToggle.IsChecked == true;
        _config.Current.Models.Analysis = CaptureRoute(AnalysisProviderCombo, AnalysisModelCombo);
        _config.Current.Models.AnalysisThinking = AnalysisThinkingToggle.IsChecked == true;
        _config.Current.Models.UseSeparateVision = SeparateVisionToggle.IsChecked == true;
        _config.Current.Models.Vision = CaptureRoute(VisionProviderCombo, VisionModelCombo);
        _config.Current.Models.UseSeparateOcr = SeparateOcrToggle.IsChecked == true;
        _config.Current.Models.Ocr = CaptureRoute(OcrProviderCombo, OcrModelCombo);
    }

    private static ModelRoute CaptureRoute(ComboBox providerCombo, ComboBox modelCombo)
    {
        var provider = (providerCombo.SelectedItem as ProviderDescriptor)?.Id ?? "sosiskibot";
        var model = modelCombo.SelectedItem as string ?? string.Empty;
        return new ModelRoute(provider, model);
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

    private void SettingsWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        StartupLogService.Info("Settings close intercepted, hiding instead.");
        ShowWindow(WindowNative.GetWindowHandle(this), SwHide);
    }

    private AppWindow GetAppWindow()
    {
        return AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this)));
    }

    private void ApplyToolWindowStyle()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var exStyle = GetWindowLongPtr(hwnd, GwlExstyle).ToInt64();
        exStyle |= WsExToolwindow;
        exStyle &= ~WsExAppwindow;
        _ = SetWindowLongPtr(hwnd, GwlExstyle, new IntPtr(exStyle));
    }

    private const int GwlExstyle = -20;
    private const long WsExToolwindow = 0x00000080L;
    private const long WsExAppwindow = 0x00040000L;
    private const int SwHide = 0;
    private const int SwShow = 5;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(nint hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);
}
