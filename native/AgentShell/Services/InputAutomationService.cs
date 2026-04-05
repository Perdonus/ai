using System.Runtime.InteropServices;

namespace AgentShell.Services;

public sealed class InputAutomationService
{
    public void TypeText(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == '\r')
            {
                continue;
            }

            if (rune.Value == '\n')
            {
                PressKey("ENTER");
                continue;
            }

            SendUnicodeKey((ushort)rune.Value, keyUp: false);
            SendUnicodeKey((ushort)rune.Value, keyUp: true);
        }
    }

    public void PressKey(string key)
    {
        KeyDown(key);
        KeyUp(key);
    }

    public void PressKeyCombo(IReadOnlyList<string> keys)
    {
        var resolved = keys.Select(ResolveVirtualKey).ToArray();
        foreach (var key in resolved)
        {
            SendVirtualKey(key, keyUp: false);
            Thread.Sleep(24);
        }

        Thread.Sleep(32);
        for (var index = resolved.Length - 1; index >= 0; index--)
        {
            SendVirtualKey(resolved[index], keyUp: true);
            Thread.Sleep(20);
        }
    }

    public void KeyDown(string key)
    {
        SendVirtualKey(ResolveVirtualKey(key), keyUp: false);
    }

    public void KeyUp(string key)
    {
        SendVirtualKey(ResolveVirtualKey(key), keyUp: true);
    }

    public async Task HoldKeyAsync(string key, int milliseconds, CancellationToken cancellationToken)
    {
        KeyDown(key);
        try
        {
            await WaitAsync(milliseconds, cancellationToken);
        }
        finally
        {
            KeyUp(key);
        }
    }

    public async Task HoldMouseAsync(string button, int milliseconds, CancellationToken cancellationToken)
    {
        MouseDown(button);
        try
        {
            await WaitAsync(milliseconds, cancellationToken);
        }
        finally
        {
            MouseUp(button);
        }
    }

    public void MoveMouse(int x, int y)
    {
        _ = SetCursorPos(x, y);
    }

    public void LeftClick(int x, int y)
    {
        MoveMouse(x, y);
        Click("left");
    }

    public void RightClick(int x, int y)
    {
        MoveMouse(x, y);
        Click("right");
    }

    public void DoubleClick(int x, int y, string button = "left")
    {
        MoveMouse(x, y);
        Click(button);
        Click(button);
    }

    public void Click(string button = "left")
    {
        MouseDown(button);
        MouseUp(button);
    }

    public void MouseDown(string button = "left")
    {
        SendMouseInput(ResolveMouseDown(button));
    }

    public void MouseUp(string button = "left")
    {
        SendMouseInput(ResolveMouseUp(button));
    }

    public void Scroll(int delta)
    {
        var remaining = delta;
        while (remaining != 0)
        {
            var chunk = Math.Clamp(remaining, -120, 120);
            SendMouseInput(MouseeventfWheel, unchecked((uint)chunk));
            remaining -= chunk;
            if (remaining != 0)
            {
                Thread.Sleep(18);
            }
        }
    }

    public async Task DragAsync(
        int startX,
        int startY,
        int endX,
        int endY,
        int milliseconds,
        string button,
        CancellationToken cancellationToken)
    {
        MoveMouse(startX, startY);
        MouseDown(button);
        try
        {
            await WaitAsync(70, cancellationToken);
            var steps = Math.Clamp(milliseconds / 18, 6, 48);
            for (var step = 1; step <= steps; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var x = startX + ((endX - startX) * step / steps);
                var y = startY + ((endY - startY) * step / steps);
                MoveMouse(x, y);
                await WaitAsync(Math.Max(12, milliseconds / steps), cancellationToken);
            }
        }
        finally
        {
            MouseUp(button);
        }
    }

    public Task WaitAsync(int milliseconds, CancellationToken cancellationToken)
    {
        return Task.Delay(Math.Clamp(milliseconds, 50, 5000), cancellationToken);
    }

    private static void SendUnicodeKey(ushort code, bool keyUp)
    {
        var input = new Input
        {
            type = InputKeyboard,
            Anonymous = new InputUnion
            {
                ki = new KeyboardInput
                {
                    wVk = 0,
                    wScan = code,
                    dwFlags = KeyeventfUnicode | (keyUp ? KeyeventfKeyup : 0),
                    dwExtraInfo = nint.Zero
                }
            }
        };

        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    private static void SendVirtualKey(ushort virtualKey, bool keyUp)
    {
        var input = new Input
        {
            type = InputKeyboard,
            Anonymous = new InputUnion
            {
                ki = new KeyboardInput
                {
                    wVk = virtualKey,
                    wScan = 0,
                    dwFlags = keyUp ? KeyeventfKeyup : 0,
                    dwExtraInfo = nint.Zero
                }
            }
        };

        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    private static void SendMouseInput(uint flags, uint mouseData = 0)
    {
        var input = new Input
        {
            type = InputMouse,
            Anonymous = new InputUnion
            {
                mi = new MouseInput
                {
                    mouseData = mouseData,
                    dwFlags = flags,
                    dwExtraInfo = nint.Zero
                }
            }
        };

        SendInput(1, [input], Marshal.SizeOf<Input>());
    }

    private static uint ResolveMouseDown(string button)
    {
        return button.Trim().ToLowerInvariant() switch
        {
            "left" => MouseeventfLeftdown,
            "right" => MouseeventfRightdown,
            "middle" => MouseeventfMiddledown,
            _ => throw new InvalidOperationException($"Unsupported mouse button: {button}")
        };
    }

    private static uint ResolveMouseUp(string button)
    {
        return button.Trim().ToLowerInvariant() switch
        {
            "left" => MouseeventfLeftup,
            "right" => MouseeventfRightup,
            "middle" => MouseeventfMiddleup,
            _ => throw new InvalidOperationException($"Unsupported mouse button: {button}")
        };
    }

    private static ushort ResolveVirtualKey(string key)
    {
        return key.Trim().ToUpperInvariant() switch
        {
            "ENTER" => 0x0D,
            "TAB" => 0x09,
            "ESC" or "ESCAPE" => 0x1B,
            "SPACE" => 0x20,
            "UP" => 0x26,
            "DOWN" => 0x28,
            "LEFT" => 0x25,
            "RIGHT" => 0x27,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" or "PGUP" => 0x21,
            "PAGEDOWN" or "PGDOWN" or "PGDN" => 0x22,
            "CTRL" or "CONTROL" or "LCTRL" or "RCTRL" => 0x11,
            "SHIFT" => 0x10,
            "ALT" or "LALT" or "RALT" or "ALTGR" => 0x12,
            "WIN" or "WINDOWS" or "META" or "SUPER" or "CMD" or "COMMAND" or "LWIN" or "RWIN" => 0x5B,
            "BACKSPACE" => 0x08,
            "DELETE" => 0x2E,
            "INSERT" => 0x2D,
            "CAPSLOCK" => 0x14,
            "NUMLOCK" => 0x90,
            "SCROLLLOCK" => 0x91,
            "PRINTSCREEN" or "PRTSC" or "PRNTSCR" => 0x2C,
            "PAUSE" => 0x13,
            "APPS" or "MENU" or "CONTEXT" => 0x5D,
            "F1" => 0x70,
            "F2" => 0x71,
            "F3" => 0x72,
            "F4" => 0x73,
            "F5" => 0x74,
            "F6" => 0x75,
            "F7" => 0x76,
            "F8" => 0x77,
            "F9" => 0x78,
            "F10" => 0x79,
            "F11" => 0x7A,
            "F12" => 0x7B,
            var single when single.Length == 1 => (ushort)single[0],
            _ => throw new InvalidOperationException($"Unsupported key: {key}")
        };
    }

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint KeyeventfKeyup = 0x0002;
    private const uint KeyeventfUnicode = 0x0004;
    private const uint MouseeventfLeftdown = 0x0002;
    private const uint MouseeventfLeftup = 0x0004;
    private const uint MouseeventfRightdown = 0x0008;
    private const uint MouseeventfRightup = 0x0010;
    private const uint MouseeventfMiddledown = 0x0020;
    private const uint MouseeventfMiddleup = 0x0040;
    private const uint MouseeventfWheel = 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint type;
        public InputUnion Anonymous;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInput mi;

        [FieldOffset(0)]
        public KeyboardInput ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);
}
