using AgentShell.Models;
using AgentShell.Services;
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

    public SettingsWindow()
    {
        InitializeComponent();
        ConfigureWindow();
        _ = LoadAsync();
    }

    public void BringToFront()
    {
        Activate();
    }

    private void ConfigureWindow()
    {
        var appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowNative.GetWindowHandle(this)));
        appWindow.Resize(new Windows.Graphics.SizeInt32(980, 760));
    }

    private async Task LoadAsync()
    {
        ProvidersList.ItemsSource = _providers;

        BindRouteSelector(PrimaryProviderCombo, _config.Current.Models.Primary);
        BindRouteSelector(AnalysisProviderCombo, _config.Current.Models.Analysis);
        BindRouteSelector(VisionProviderCombo, _config.Current.Models.Vision);
        BindRouteSelector(OcrProviderCombo, _config.Current.Models.Ocr);

        SeparateAnalysisToggle.IsChecked = _config.Current.Models.UseSeparateAnalysis;
        SeparateVisionToggle.IsChecked = _config.Current.Models.UseSeparateVision;
        SeparateOcrToggle.IsChecked = _config.Current.Models.UseSeparateOcr;
        ApplySeparateRouteVisibility();

        await LoadModelChoicesAsync(PrimaryProviderCombo, PrimaryModelCombo, _config.Current.Models.Primary.Model);
        await LoadModelChoicesAsync(AnalysisProviderCombo, AnalysisModelCombo, _config.Current.Models.Analysis.Model);
        await LoadModelChoicesAsync(VisionProviderCombo, VisionModelCombo, _config.Current.Models.Vision.Model);
        await LoadModelChoicesAsync(OcrProviderCombo, OcrModelCombo, _config.Current.Models.Ocr.Model);

        ToolsList.ItemsSource = await _runtimeCatalog.LoadToolsAsync();
        WidgetsList.ItemsSource = await _runtimeCatalog.LoadWidgetsAsync();
    }

    private void BindRouteSelector(ComboBox comboBox, ModelRoute route)
    {
        comboBox.ItemsSource = _providers;
        comboBox.DisplayMemberPath = nameof(ProviderDescriptor.Name);
        comboBox.SelectedItem = _providers.FirstOrDefault(provider => provider.Id == route.Provider) ?? _providers[0];
    }

    private async Task LoadModelChoicesAsync(ComboBox providerCombo, ComboBox modelCombo, string selectedModel)
    {
        if (providerCombo.SelectedItem is not ProviderDescriptor provider)
        {
            return;
        }

        modelCombo.PlaceholderText = "Loading models...";
        var apiKey = _config.Current.Providers.GetValueOrDefault(provider.Id)?.ApiKey ?? string.Empty;
        var models = await _modelDiscovery.LoadModelsAsync(provider, apiKey);
        modelCombo.ItemsSource = models;
        modelCombo.SelectedItem = models.FirstOrDefault(model => model == selectedModel) ?? models.FirstOrDefault();
        modelCombo.PlaceholderText = models.Count == 0 ? "Enter provider API key first" : "Choose model";
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
    }

    private void ProviderApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox passwordBox || passwordBox.Tag is not string providerId)
        {
            return;
        }

        if (!_config.Current.Providers.TryGetValue(providerId, out var provider))
        {
            provider = new ProviderConfig();
            _config.Current.Providers[providerId] = provider;
        }

        provider.ApiKey = passwordBox.Password;
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
        _config.Current.Models.UseSeparateAnalysis = SeparateAnalysisToggle.IsChecked == true;
        _config.Current.Models.Analysis = CaptureRoute(AnalysisProviderCombo, AnalysisModelCombo);
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
}
