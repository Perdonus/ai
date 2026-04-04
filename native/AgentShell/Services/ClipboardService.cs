using System.Runtime.InteropServices;

namespace AgentShell.Services;

public sealed class ClipboardService
{
    public string GetText()
    {
        if (!OpenClipboardWithRetry())
        {
            return string.Empty;
        }

        try
        {
            var handle = GetClipboardData(CfUnicodeText);
            if (handle == nint.Zero)
            {
                return string.Empty;
            }

            var pointer = GlobalLock(handle);
            if (pointer == nint.Zero)
            {
                return string.Empty;
            }

            try
            {
                return Marshal.PtrToStringUni(pointer) ?? string.Empty;
            }
            finally
            {
                _ = GlobalUnlock(handle);
            }
        }
        finally
        {
            _ = CloseClipboard();
        }
    }

    public string GetPreview(int maxLength = 200)
    {
        var text = GetText().Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(empty)";
        }

        text = text.Replace("\r", " ").Replace("\n", " ");
        return text.Length <= maxLength ? text : $"{text[..maxLength]}...";
    }

    public void SetText(string text)
    {
        var normalized = (text ?? string.Empty).Replace("\r\n", "\n").Replace("\n", "\r\n");
        var bytes = (normalized.Length + 1) * 2;
        var memory = GlobalAlloc(GmemMoveable, (nuint)bytes);
        if (memory == nint.Zero)
        {
            throw new InvalidOperationException("Unable to allocate clipboard memory.");
        }

        try
        {
            var pointer = GlobalLock(memory);
            if (pointer == nint.Zero)
            {
                throw new InvalidOperationException("Unable to lock clipboard memory.");
            }

            try
            {
                var chars = normalized.ToCharArray();
                Marshal.Copy(chars, 0, pointer, chars.Length);
                Marshal.WriteInt16(pointer, chars.Length * 2, 0);
            }
            finally
            {
                _ = GlobalUnlock(memory);
            }

            if (!OpenClipboardWithRetry())
            {
                throw new InvalidOperationException("Unable to open clipboard.");
            }

            try
            {
                if (!EmptyClipboard())
                {
                    throw new InvalidOperationException("Unable to empty clipboard.");
                }

                if (SetClipboardData(CfUnicodeText, memory) == nint.Zero)
                {
                    throw new InvalidOperationException("Unable to set clipboard data.");
                }

                memory = nint.Zero;
            }
            finally
            {
                _ = CloseClipboard();
            }
        }
        finally
        {
            if (memory != nint.Zero)
            {
                _ = GlobalFree(memory);
            }
        }
    }

    private static bool OpenClipboardWithRetry()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (OpenClipboard(nint.Zero))
            {
                return true;
            }

            Thread.Sleep(25);
        }

        return false;
    }

    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint uFormat, nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalFree(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(nint hMem);
}
