using System.Windows.Input;
using TranscriptionOverlay.Models;

namespace TranscriptionOverlay.Services;

public static class GlobalHotkeyFormatter
{
    private static readonly GlobalHotkeyDefinition DefaultHotkeyDefinition =
        new(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, Key.V);

    public static GlobalHotkeyDefinition ParseOrDefault(string? value)
    {
        return TryParse(value, out var definition)
            ? definition
            : DefaultHotkeyDefinition;
    }

    public static bool TryParse(string? value, out GlobalHotkeyDefinition definition)
    {
        definition = DefaultHotkeyDefinition;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        var modifiers = ModifierKeys.None;
        for (var index = 0; index < parts.Length - 1; index++)
        {
            modifiers |= parts[index].ToLowerInvariant() switch
            {
                "ctrl" => ModifierKeys.Control,
                "alt" => ModifierKeys.Alt,
                "shift" => ModifierKeys.Shift,
                "win" => ModifierKeys.Windows,
                _ => ModifierKeys.None,
            };
        }

        if (modifiers == ModifierKeys.None)
        {
            return false;
        }

        var converter = new KeyConverter();
        if (converter.ConvertFromInvariantString(parts[^1]) is not Key key || IsModifierKey(key))
        {
            return false;
        }

        definition = new GlobalHotkeyDefinition(modifiers, key);
        return true;
    }

    public static string Format(GlobalHotkeyDefinition definition)
    {
        return Format(definition.Modifiers, definition.Key);
    }

    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    public static uint ToNativeModifiers(ModifierKeys modifiers)
    {
        var native = 0u;
        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            native |= Win32Native.ModAlt;
        }

        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            native |= Win32Native.ModControl;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            native |= Win32Native.ModShift;
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            native |= Win32Native.ModWin;
        }

        return native;
    }

    public static int ToVirtualKey(Key key)
    {
        return KeyInterop.VirtualKeyFromKey(key);
    }

    public static bool IsModifierKey(Key key)
    {
        return key is Key.LeftAlt or Key.RightAlt
            or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin;
    }
}
