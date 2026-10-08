using System.Windows.Input;

namespace TranscriptionOverlay.Models;

public sealed record GlobalHotkeyDefinition(
    ModifierKeys Modifiers,
    Key Key);
