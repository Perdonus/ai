using AgentShell.Services;
using Microsoft.UI.Xaml;

namespace AgentShell;

public partial class App : Application
{
    public static LauncherWindow? Launcher { get; private set; }

    public static SettingsWindow? Settings { get; private set; }

    public static ShellConfigService ConfigService { get; } = new();

    public static RuntimeCatalogService RuntimeCatalog { get; } = new();

    public App()
    {
        StartupLogService.Initialize();
        StartupLogService.Info("App constructor entered.");
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupLogService.Info("OnLaunched started.");
        await ConfigService.EnsureLoadedAsync();
        StartupLogService.Info("Configuration loaded.");

        Launcher = new LauncherWindow();
        StartupLogService.Info("Launcher window created.");
        Launcher.Activate();
        Launcher.HideAnimated(immediate: true);
        StartupLogService.Info("Launcher hidden immediately after activation.");
    }

    public static void ShowSettings()
    {
        Settings ??= new SettingsWindow();
        StartupLogService.Info("Settings window opened.");
        Settings.Activate();
        Settings.BringToFront();
    }
}
