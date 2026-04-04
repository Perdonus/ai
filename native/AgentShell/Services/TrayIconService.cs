using System.Drawing;
using System.Windows.Forms;

namespace AgentShell.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;

    public TrayIconService(Action openLauncher, Action openSettings, Action exitApp)
    {
        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("Open Agent", null, (_, _) => openLauncher());
        contextMenu.Items.Add("Settings", null, (_, _) => openSettings());
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("Exit", null, (_, _) => exitApp());

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        var icon = File.Exists(iconPath) ? new Icon(iconPath) : SystemIcons.Application;

        _notifyIcon = new NotifyIcon
        {
            Text = "AI Agent",
            Icon = icon,
            Visible = true,
            ContextMenuStrip = contextMenu
        };

        _notifyIcon.DoubleClick += (_, _) => openLauncher();
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
