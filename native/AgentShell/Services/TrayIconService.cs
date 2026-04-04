using DrawingIcon = System.Drawing.Icon;
using Forms = System.Windows.Forms;

namespace AgentShell.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly Action _openLauncher;
    private readonly Action _openSettings;
    private readonly Action _exitApplication;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly DrawingIcon _trayIcon;

    public TrayIconService(Action openLauncher, Action openSettings, Action exitApplication)
    {
        _openLauncher = openLauncher;
        _openSettings = openSettings;
        _exitApplication = exitApplication;
        _trayIcon = LoadIconForTray();
        _menu = BuildContextMenu();
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "AI Agent",
            Visible = true,
            ContextMenuStrip = _menu,
            Icon = _trayIcon
        };

        _notifyIcon.MouseClick += NotifyIcon_MouseClick;
        _notifyIcon.MouseDoubleClick += NotifyIcon_MouseDoubleClick;
        StartupLogService.Info("Tray icon initialized via WinForms NotifyIcon.");
    }

    public void Dispose()
    {
        _notifyIcon.MouseClick -= NotifyIcon_MouseClick;
        _notifyIcon.MouseDoubleClick -= NotifyIcon_MouseDoubleClick;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _trayIcon.Dispose();
    }

    private void NotifyIcon_MouseClick(object? sender, Forms.MouseEventArgs e)
    {
        StartupLogService.Info($"Tray click received: {e.Button}.");
        if (e.Button == Forms.MouseButtons.Left)
        {
            _openLauncher();
        }
    }

    private void NotifyIcon_MouseDoubleClick(object? sender, Forms.MouseEventArgs e)
    {
        StartupLogService.Info($"Tray double click received: {e.Button}.");
        if (e.Button == Forms.MouseButtons.Left)
        {
            _openLauncher();
        }
    }

    private Forms.ContextMenuStrip BuildContextMenu()
    {
        var menu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false
        };

        menu.Items.Add("Открыть", null, (_, _) =>
        {
            StartupLogService.Info("Tray command: open.");
            _openLauncher();
        });
        menu.Items.Add("Настройки", null, (_, _) =>
        {
            StartupLogService.Info("Tray command: settings.");
            _openSettings();
        });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) =>
        {
            StartupLogService.Info("Tray command: exit.");
            _exitApplication();
        });

        return menu;
    }

    private static DrawingIcon LoadIconForTray()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        return File.Exists(iconPath)
            ? new DrawingIcon(iconPath)
            : (DrawingIcon)System.Drawing.SystemIcons.Application.Clone();
    }
}
