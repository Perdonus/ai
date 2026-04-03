using System.Runtime.InteropServices;
using System.Threading;
using AgentShell.Services;
using Microsoft.UI.Xaml;

namespace AgentShell;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        StartupLogService.Initialize();
        StartupLogService.Info("Program.Main entered.");

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            StartupLogService.Error($"Unhandled exception: {eventArgs.ExceptionObject}");
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            StartupLogService.Warn("ProcessExit raised.");
        };

        XamlCheckProcessRequirements();
        WinRT.ComWrappersSupport.InitializeComWrappers();
        StartupLogService.Info("XAML requirements checked.");

        Application.Start(_ =>
        {
            StartupLogService.Info("Application.Start callback entered.");
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });

        StartupLogService.Warn("Application.Start returned.");
    }

    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();
}
