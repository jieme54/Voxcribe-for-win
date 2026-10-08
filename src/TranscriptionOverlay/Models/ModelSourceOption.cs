namespace TranscriptionOverlay.Models;

public sealed record ModelSourceOption(
    string Id,
    string DisplayName,
    string DirectoryPath,
    ModelSourceKind SourceKind);
