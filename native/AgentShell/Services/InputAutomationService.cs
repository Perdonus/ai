using System.Text;
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
        var vk = ResolveVirtualKey(key);
        SendVirtualKey(vk, keyUp: false);
        SendVirtualKey(vk, keyUp: true);
    }

    public void PressKeyCombo(IReadOnlyList<string> keys)
    {
        var resolved = keys.Select(ResolveVirtualKey).ToArray();
        foreach (var key in resolved)
        {
            SendVirtualKey(key, keyUp: false);
        }

        for (var index = resolved.Length - 1; index >= 0; index--)
        {
            SendVirtualKey(resolved[index], keyUp: true);
        }
    }

    public void LeftClick(int x, int y)
    {
        _ = SetCursorPos(x, y);
        SendMouseInput(MouseeventfLeftdown);
        SendMouseInput(MouseeventfLeftup);
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

    private static void SendMouseInput(uint flags)
    {
        var input = new Input
        {
            type = InputMouse,
            Anonymous = new InputUnion
            {
                mi = new MouseInput
                {
                    dwFlags = flags,
                    dwExtraInfo = nint.Zero
                }
            }
        };

        SendInput(1, [input], Marshal.SizeOf<Input>());
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
            "CTRL" or "CONTROL" => 0x11,
            "SHIFT" => 0x10,
            "ALT" => 0x12,
            "WIN" or "WINDOWS" => 0x5B,
            "BACKSPACE" => 0x08,
            "DELETE" => 0x2E,
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
