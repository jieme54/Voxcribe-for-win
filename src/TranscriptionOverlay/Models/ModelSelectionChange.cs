namespace TranscriptionOverlay.Models;

public sealed record ModelSelectionChange(
    string ModelKey,
    string? SourceId);
