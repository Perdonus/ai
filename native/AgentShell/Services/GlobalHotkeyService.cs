using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace AgentShell.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmKeyup = 0x0101;
    private const int WmSyskeydown = 0x0104;
    private const int WmSyskeyup = 0x0105;

    public const uint TrayCallbackMessage = 0x8001;

    private readonly nint _hwnd;
    private readonly LowLevelKeyboardProc _hookProc;
    private readonly uint _virtualKey;
    private readonly IntPtr _previousWndProc;
    private readonly WndProc _wndProcDelegate;
    private nint _hookHandle;
    private bool _pressed;

    public event EventHandler? HotkeyPressed;
    public event EventHandler<TrayIconMessageEventArgs>? TrayMessageReceived;

    public GlobalHotkeyService(Window window, int hotkeyId, uint virtualKey)
    {
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _virtualKey = virtualKey;
        _hookProc = HookCallback;
        _hookHandle = SetHook(_hookProc);
        if (_hookHandle == nint.Zero)
        {
            throw new InvalidOperationException($"Unable to install low-level keyboard hook for 0x{virtualKey:X2}");
        }

        _wndProcDelegate = WndProcHook;
        _previousWndProc = SetWindowLongPtr(_hwnd, -4, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
    }

    public void Dispose()
    {
        if (_hookHandle != nint.Zero)
        {
            _ = UnhookWindowsHookEx(_hookHandle);
            _hookHandle = nint.Zero;
        }

        if (_previousWndProc != IntPtr.Zero)
        {
            _ = SetWindowLongPtr(_hwnd, -4, _previousWndProc);
        }
    }

    private nint HookCallback(int code, nuint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            if (info.vkCode == _virtualKey)
            {
                var message = unchecked((uint)wParam);
                if (message is WmKeydown or WmSyskeydown)
                {
                    _pressed = true;
                }
                else if (message is WmKeyup or WmSyskeyup)
                {
                    if (_pressed)
                    {
                        _pressed = false;
                        HotkeyPressed?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
        }

        return CallNextHookEx(_hookHandle, code, wParam, lParam);
    }

    private nint WndProcHook(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message == TrayCallbackMessage)
        {
            TrayMessageReceived?.Invoke(this, new TrayIconMessageEventArgs(unchecked((uint)lParam.ToInt64())));
            return 0;
        }

        return CallWindowProc(_previousWndProc, hwnd, message, wParam, lParam);
    }

    private static nint SetHook(LowLevelKeyboardProc proc)
    {
        using var currentProcess = Process.GetCurrentProcess();
        using var currentModule = currentProcess.MainModule!;
        return SetWindowsHookEx(WhKeyboardLl, proc, GetModuleHandle(currentModule.ModuleName), 0);
    }

    private delegate nint LowLevelKeyboardProc(int nCode, nuint wParam, nint lParam);
    private delegate nint WndProc(nint hwnd, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nuint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(nint hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(IntPtr lpPrevWndFunc, nint hWnd, uint msg, nuint wParam, nint lParam);
}

public sealed record TrayIconMessageEventArgs(uint Message);
