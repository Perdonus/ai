using AgentShell.Services;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;

namespace AgentShell;

public partial class App : Application
{
    public static LauncherWindow? Launcher { get; private set; }

    public static SettingsWindow? Settings { get; private set; }

    public static ShellConfigService ConfigService { get; } = new();

    public static RuntimeCatalogService RuntimeCatalog { get; } = new();

    private TrayIconService? _trayIcon;

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

            try
            {
                _trayIcon = new TrayIconService(Launcher, OpenLauncher, ShowSettings, ExitAgent, Launcher.HotkeyService);
                StartupLogService.Info("Tray icon created.");
            }
            catch (Exception ex)
            {
                StartupLogService.Error($"Tray icon initialization failed: {ex}");
            }
        }
        catch (Exception ex)
        {
            StartupLogService.Error($"OnLaunched failed: {ex}");
            throw;
        }
    }

    public static void ShowSettings()
    {
        if (Launcher is null)
        {
            return;
        }

        Launcher.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                Settings ??= new SettingsWindow();
                StartupLogService.Info("Settings window opened.");
                Settings.BringToFront();
            }
            catch (COMException ex)
            {
                StartupLogService.Warn($"Settings window recreation requested after COM failure: {ex.Message}");
                Settings = new SettingsWindow();
                StartupLogService.Info("Settings window reopened with a fresh instance.");
                Settings.BringToFront();
            }
        });
    }

    public static void OpenLauncher()
    {
        if (Launcher is null)
        {
            return;
        }

        Launcher.DispatcherQueue.TryEnqueue(async () => await Launcher.ShowAnimatedAsync());
    }

    public void ExitAgent()
    {
        try
        {
            StartupLogService.Info("Exit requested from tray.");
            _trayIcon?.Dispose();
            _trayIcon = null;
        }
        catch (Exception ex)
        {
            StartupLogService.Warn($"Tray dispose during exit failed: {ex.Message}");
        }

        Current.Exit();
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        StartupLogService.Error($"Application unhandled exception: {e.Exception}");
    }
}
