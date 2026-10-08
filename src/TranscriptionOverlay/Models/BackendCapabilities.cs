namespace TranscriptionOverlay.Models;

public sealed record BackendCapabilities(
    bool HasQwenRuntime,
    bool HasCrisperWhisperRuntime,
    bool HasPhononRuntime,
    bool HasVoxtralRealtimeRuntime,
    string? PythonVersion);
