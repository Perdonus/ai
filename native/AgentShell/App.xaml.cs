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
        UnhandledException += App_UnhandledException;
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            StartupLogService.Info("OnLaunched started.");
            ConfigService.EnsureLoaded();
            StartupLogService.Info("Configuration loaded.");

            Launcher = new LauncherWindow();
            StartupLogService.Info("Launcher window created.");
            Launcher.Activate();
            Launcher.HideAnimated(immediate: true);
            StartupLogService.Info("Launcher hidden immediately after activation.");
        }
        catch (Exception ex)
        {
            StartupLogService.Error($"OnLaunched failed: {ex}");
            throw;
        }
    }

    public static void ShowSettings()
    {
        Settings ??= new SettingsWindow();
        StartupLogService.Info("Settings window opened.");
        Settings.Activate();
        Settings.BringToFront();
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        StartupLogService.Error($"Application unhandled exception: {e.Exception}");
    }
}
