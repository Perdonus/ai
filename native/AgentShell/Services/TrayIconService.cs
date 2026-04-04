using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace AgentShell.Services;

public sealed class TrayIconService : IDisposable
{
    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightbutton = 0x0002;
    private const uint TpmReturcmd = 0x0100;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetversion = 0x00000004;
    private const uint NotifyIconVersion4 = 4;
    private const uint WmLbuttonup = 0x0202;
    private const uint WmLbuttondblclk = 0x0203;
    private const uint WmRbuttonup = 0x0205;
    private const uint WmNull = 0x0000;
    private const uint OpenCommandId = 1001;
    private const uint SettingsCommandId = 1002;
    private const uint ExitCommandId = 1003;

    private readonly Action _openLauncher;
    private readonly Action _openSettings;
    private readonly Action _exitApplication;
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly nint _hwnd;
    private NotifyIconData _data;

    public TrayIconService(Window window, Action openLauncher, Action openSettings, Action exitApplication, GlobalHotkeyService hotkeyService)
    {
        _openLauncher = openLauncher;
        _openSettings = openSettings;
        _exitApplication = exitApplication;
        _hotkeyService = hotkeyService;
        _hotkeyService.TrayMessageReceived += HotkeyService_TrayMessageReceived;

        _hwnd = WindowNative.GetWindowHandle(window);
        _data = new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = GlobalHotkeyService.TrayCallbackMessage,
            hIcon = LoadIconForTray(),
            szTip = "AI Agent",
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
            Anonymous = new NotifyIconDataTimeoutUnion
            {
                uVersion = NotifyIconVersion4
            }
        };

        CallShellNotifyIcon(NimAdd);
        CallShellNotifyIcon(NimSetversion);
        CallShellNotifyIcon(NimModify);
    }

    public void Dispose()
    {
        _hotkeyService.TrayMessageReceived -= HotkeyService_TrayMessageReceived;
        CallShellNotifyIcon(NimDelete);
        if (_data.hIcon != nint.Zero)
        {
            _ = DestroyIcon(_data.hIcon);
            _data.hIcon = nint.Zero;
        }
    }

    private void HotkeyService_TrayMessageReceived(object? sender, TrayIconMessageEventArgs e)
    {
        if (e.Message is WmLbuttonup or WmLbuttondblclk)
        {
            _openLauncher();
            return;
        }

        if (e.Message == WmRbuttonup)
        {
            ShowContextMenu();
        }
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == nint.Zero)
        {
            throw new InvalidOperationException("CreatePopupMenu failed.");
        }

        try
        {
            _ = AppendMenu(menu, MfString, OpenCommandId, "Открыть");
            _ = AppendMenu(menu, MfString, SettingsCommandId, "Настройки");
            _ = AppendMenu(menu, MfSeparator, 0, string.Empty);
            _ = AppendMenu(menu, MfString, ExitCommandId, "Выход");

            _ = SetForegroundWindow(_hwnd);
            if (!GetCursorPos(out var point))
            {
                throw new InvalidOperationException("GetCursorPos failed for tray menu.");
            }

            var command = TrackPopupMenu(menu, TpmReturcmd | TpmRightbutton, point.X, point.Y, 0, _hwnd, nint.Zero);
            _ = PostMessage(_hwnd, WmNull, 0, 0);

            switch (command)
            {
                case OpenCommandId:
                    _openLauncher();
                    break;
                case SettingsCommandId:
                    _openSettings();
                    break;
                case ExitCommandId:
                    _exitApplication();
                    break;
            }
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    private void CallShellNotifyIcon(uint message)
    {
        if (!ShellNotifyIcon(message, ref _data))
        {
            throw new InvalidOperationException($"Shell_NotifyIconW failed for message 0x{message:X}.");
        }
    }

    private static nint LoadIconForTray()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        return File.Exists(iconPath) ? ExtractIconW(nint.Zero, iconPath, 0) : nint.Zero;
    }

    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellNotifyIcon(uint dwMessage, ref NotifyIconData lpData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint ExtractIconW(nint hInst, string lpszExeFileName, int nIconIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AppendMenu(nint hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenu(nint hMenu, uint uFlags, int x, int y, int nReserved, nint hWnd, nint prcRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(nint hWnd, uint msg, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public NotifyIconDataTimeoutUnion Anonymous;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct NotifyIconDataTimeoutUnion
    {
        [FieldOffset(0)]
        public uint uTimeout;

        [FieldOffset(0)]
        public uint uVersion;
    }
}
