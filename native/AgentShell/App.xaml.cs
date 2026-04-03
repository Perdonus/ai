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
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        await ConfigService.EnsureLoadedAsync();

        Launcher = new LauncherWindow();
        Launcher.Activate();
        Launcher.HideAnimated(immediate: true);
    }

    public static void ShowSettings()
    {
        Settings ??= new SettingsWindow();
        Settings.Activate();
        Settings.BringToFront();
    }
}
