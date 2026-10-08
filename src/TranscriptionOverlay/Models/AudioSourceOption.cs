namespace TranscriptionOverlay.Models;

public sealed record AudioSourceOption(
    string Id,
    string DisplayName,
    AudioSourceKind Kind,
    string? DeviceId);
