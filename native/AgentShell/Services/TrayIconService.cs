using System.Runtime.InteropServices;
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
    private const uint WmNull = 0x0000;
    private const uint WmCommand = 0x0111;
    private const uint WmLbuttonup = 0x0202;
    private const uint WmLbuttondblclk = 0x0203;
    private const uint WmRbuttonup = 0x0205;
    private const uint WmContextmenu = 0x007B;
    private const uint NinSelect = 0x0400;
    private const uint NinKeyselect = 0x0401;
    private const int GwlpUserdata = -21;
    private const uint OpenCommandId = 1001;
    private const uint SettingsCommandId = 1002;
    private const uint ExitCommandId = 1003;
    private const uint TrayCallbackMessage = 0x8002;
    private static readonly nint HwndMessage = new(-3);

    private readonly Action _openLauncher;
    private readonly Action _openSettings;
    private readonly Action _exitApplication;
    private readonly WndProc _wndProc;
    private readonly string _windowClassName = $"AgentShell.Tray.{Guid.NewGuid():N}";
    private readonly uint _taskbarCreatedMessage;
    private GCHandle _selfHandle;
    private ushort _classAtom;
    private nint _messageWindow;
    private NotifyIconData _data;

    public TrayIconService(Action openLauncher, Action openSettings, Action exitApplication)
    {
        _openLauncher = openLauncher;
        _openSettings = openSettings;
        _exitApplication = exitApplication;
        _wndProc = WindowProc;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");

        RegisterMessageWindow();
        CreateTrayIcon();
        StartupLogService.Info("Tray icon initialized via hidden native window.");
    }

    public void Dispose()
    {
        RemoveTrayIcon();

        if (_messageWindow != nint.Zero)
        {
            _ = DestroyWindow(_messageWindow);
            _messageWindow = nint.Zero;
        }

        if (_classAtom != 0)
        {
            _ = UnregisterClass(_windowClassName, GetModuleHandle(null));
            _classAtom = 0;
        }

        if (_selfHandle.IsAllocated)
        {
            _selfHandle.Free();
        }
    }

    private void RegisterMessageWindow()
    {
        _selfHandle = GCHandle.Alloc(this);

        var windowClass = new WndClass
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = _windowClassName
        };

        _classAtom = RegisterClass(ref windowClass);
        if (_classAtom == 0)
        {
            throw new InvalidOperationException($"RegisterClassW failed. win32={Marshal.GetLastWin32Error()}");
        }

        _messageWindow = CreateWindowEx(
            0,
            _windowClassName,
            "AI Agent Tray",
            0,
            0,
            0,
            0,
            0,
            HwndMessage,
            nint.Zero,
            windowClass.hInstance,
            nint.Zero);

        if (_messageWindow == nint.Zero)
        {
            throw new InvalidOperationException($"CreateWindowExW failed. win32={Marshal.GetLastWin32Error()}");
        }

        _ = SetWindowLongPtr(_messageWindow, GwlpUserdata, GCHandle.ToIntPtr(_selfHandle));
    }

    private void CreateTrayIcon()
    {
        _data = new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _messageWindow,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = TrayCallbackMessage,
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

    private void RemoveTrayIcon()
    {
        if (_data.hWnd != nint.Zero)
        {
            try
            {
                CallShellNotifyIcon(NimDelete);
            }
            catch (Exception ex)
            {
                StartupLogService.Warn($"Tray icon deletion failed: {ex.Message}");
            }
        }

        if (_data.hIcon != nint.Zero)
        {
            _ = DestroyIcon(_data.hIcon);
            _data.hIcon = nint.Zero;
        }

        _data = default;
    }

    private nint WindowProc(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message == _taskbarCreatedMessage)
        {
            StartupLogService.Info("Explorer restarted, recreating tray icon.");
            RemoveTrayIcon();
            CreateTrayIcon();
            return 0;
        }

        if (message == TrayCallbackMessage)
        {
            var trayMessage = ExtractTrayMessage(lParam);
            StartupLogService.Info($"Tray callback message received: 0x{trayMessage:X4}");

            if (trayMessage is WmLbuttonup or WmLbuttondblclk or NinSelect or NinKeyselect)
            {
                _openLauncher();
                return 0;
            }

            if (trayMessage is WmRbuttonup or WmContextmenu)
            {
                ShowContextMenu();
                return 0;
            }
        }

        if (message == WmCommand)
        {
            switch ((uint)(wParam.ToUInt64() & 0xFFFF))
            {
                case OpenCommandId:
                    StartupLogService.Info("Tray command: open.");
                    _openLauncher();
                    return 0;
                case SettingsCommandId:
                    StartupLogService.Info("Tray command: settings.");
                    _openSettings();
                    return 0;
                case ExitCommandId:
                    StartupLogService.Info("Tray command: exit.");
                    _exitApplication();
                    return 0;
            }
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == nint.Zero)
        {
            StartupLogService.Warn($"CreatePopupMenu failed. win32={Marshal.GetLastWin32Error()}");
            return;
        }

        try
        {
            _ = AppendMenu(menu, MfString, OpenCommandId, "Открыть");
            _ = AppendMenu(menu, MfString, SettingsCommandId, "Настройки");
            _ = AppendMenu(menu, MfSeparator, 0, string.Empty);
            _ = AppendMenu(menu, MfString, ExitCommandId, "Выход");

            _ = SetForegroundWindow(_messageWindow);
            if (!GetCursorPos(out var point))
            {
                StartupLogService.Warn($"GetCursorPos failed for tray menu. win32={Marshal.GetLastWin32Error()}");
                return;
            }

            var command = TrackPopupMenu(menu, TpmReturcmd | TpmRightbutton, point.X, point.Y, 0, _messageWindow, nint.Zero);
            _ = PostMessage(_messageWindow, WmNull, 0, 0);
            HandleCommand(command);
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    private static uint ExtractTrayMessage(nint lParam)
    {
        var lowWord = unchecked((uint)(lParam.ToInt64() & 0xFFFF));
        return lowWord == 0 ? unchecked((uint)lParam.ToInt64()) : lowWord;
    }

    private void HandleCommand(uint command)
    {
        switch (command)
        {
            case OpenCommandId:
                StartupLogService.Info("Tray command: open.");
                _openLauncher();
                break;
            case SettingsCommandId:
                StartupLogService.Info("Tray command: settings.");
                _openSettings();
                break;
            case ExitCommandId:
                StartupLogService.Info("Tray command: exit.");
                _exitApplication();
                break;
        }
    }

    private void CallShellNotifyIcon(uint message)
    {
        if (!ShellNotifyIcon(message, ref _data))
        {
            throw new InvalidOperationException($"Shell_NotifyIconW failed for message 0x{message:X}. win32={Marshal.GetLastWin32Error()}");
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

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WndClass lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string lpClassName, nint hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        nint hWndParent,
        nint hMenu,
        nint hInstance,
        nint lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint DefWindowProc(nint hWnd, uint msg, nuint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? lpModuleName);

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    private delegate nint WndProc(nint hWnd, uint msg, nuint wParam, nint lParam);
}
