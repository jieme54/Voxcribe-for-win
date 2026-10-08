using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace TranscriptionOverlay.Services;

internal static class Win32Native
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const int SW_RESTORE = 9;

    private const int InputKeyboard = 1;
    private const uint KeyEventFKeyUp = 0x0002;
    private const uint KeyEventFUnicode = 0x0004;
    private const ushort VkMenu = 0x12;
    private const ushort VkControl = 0x11;
    private const ushort VkV = 0x56;

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    internal static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    public static void SendCtrlV()
    {
        var inputs = new[]
        {
            CreateKeyInput(VkControl, 0),
            CreateKeyInput(VkV, 0),
            CreateKeyInput(VkV, KeyEventFKeyUp),
            CreateKeyInput(VkControl, KeyEventFKeyUp),
        };

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static bool SendUnicodeText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\n', '\r');
        var inputs = new List<INPUT>(normalized.Length * 2);

        foreach (var character in normalized)
        {
            inputs.Add(CreateUnicodeInput(character, KeyEventFUnicode));
            inputs.Add(CreateUnicodeInput(character, KeyEventFUnicode | KeyEventFKeyUp));
        }

        if (inputs.Count == 0)
        {
            return false;
        }

        var sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        return sent == (uint)inputs.Count;
    }

    public static void SendAltTap()
    {
        var inputs = new[]
        {
            CreateKeyInput(VkMenu, 0),
            CreateKeyInput(VkMenu, KeyEventFKeyUp),
        };

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static string? GetWindowClassName(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var builder = new StringBuilder(256);
        return GetClassName(handle, builder, builder.Capacity) > 0
            ? builder.ToString()
            : null;
    }

    private static INPUT CreateKeyInput(ushort virtualKey, uint flags)
    {
        return new INPUT
        {
            type = InputKeyboard,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey,
                    dwFlags = flags,
                },
            },
        };
    }

    private static INPUT CreateUnicodeInput(char character, uint flags)
    {
        return new INPUT
        {
            type = InputKeyboard,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0,
                    wScan = character,
                    dwFlags = flags,
                },
            },
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
