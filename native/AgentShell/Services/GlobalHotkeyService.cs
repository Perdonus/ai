using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace AgentShell.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private readonly nint _hwnd;
    private readonly int _hotkeyId;
    private readonly IntPtr _previousWndProc;
    private readonly WndProc _wndProcDelegate;

    public event EventHandler? HotkeyPressed;

    public GlobalHotkeyService(Window window, int hotkeyId, uint virtualKey)
    {
        _hwnd = WindowNative.GetWindowHandle(window);
        _hotkeyId = hotkeyId;
        _wndProcDelegate = WndProcHook;
        _previousWndProc = SetWindowLongPtr(_hwnd, -4, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));

        if (!RegisterHotKey(_hwnd, _hotkeyId, 0, virtualKey))
        {
            throw new InvalidOperationException($"Unable to register hotkey 0x{virtualKey:X2}");
        }
    }

    public void Dispose()
    {
        UnregisterHotKey(_hwnd, _hotkeyId);
        _ = SetWindowLongPtr(_hwnd, -4, _previousWndProc);
    }

    private nint WndProcHook(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message == 0x0312 && (int)wParam == _hotkeyId)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
            return 0;
        }

        return CallWindowProc(_previousWndProc, hwnd, message, wParam, lParam);
    }

    private delegate nint WndProc(nint hwnd, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(nint hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(IntPtr lpPrevWndFunc, nint hWnd, uint msg, nuint wParam, nint lParam);
}
