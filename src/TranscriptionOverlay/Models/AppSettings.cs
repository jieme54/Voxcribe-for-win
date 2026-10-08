namespace TranscriptionOverlay.Models;

public sealed class AppSettings
{
    public string LanguageCode { get; set; } = "fr";

    public string SelectedModelKey { get; set; } = string.Empty;

    public string SelectedModelSourceId { get; set; } = string.Empty;

    public bool PinSelectedModelSource { get; set; }

    public string AudioSourceId { get; set; } = "default-input";

    public string TranscriptionHotkey { get; set; } = "Ctrl+Alt+Shift+V";

    public bool ShowTranscriptInWindow { get; set; }

    public bool AutoOpenTranscriptWindow { get; set; }

    public string PythonRuntimePath { get; set; } = string.Empty;

    public string HardwareAccelerationDevice { get; set; } = HardwareAccelerationPreference.Auto;

    public string AutomaticHardwareAccelerationDevice { get; set; } = string.Empty;

    public TranscriptionMode PreferredTranscriptionMode { get; set; } = TranscriptionMode.File;

    public bool RouteRealtimeTranscriptToWindow { get; set; }

    public Dictionary<string, TranscriptionMode> PreferredModeByModelKey { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public List<LocalModelRegistration> LocalModelRegistrations { get; set; } = [];

    public TranscriptionMode? GetPreferredModeForModel(string? modelKey)
    {
        if (string.IsNullOrWhiteSpace(modelKey)
            || PreferredModeByModelKey.Count == 0
            || !PreferredModeByModelKey.TryGetValue(modelKey, out var mode))
        {
            return null;
        }

        return mode;
    }

    public void SetPreferredModeForModel(string? modelKey, TranscriptionMode mode)
    {
        if (string.IsNullOrWhiteSpace(modelKey))
        {
            return;
        }

        PreferredModeByModelKey[modelKey] = mode;
    }
}
