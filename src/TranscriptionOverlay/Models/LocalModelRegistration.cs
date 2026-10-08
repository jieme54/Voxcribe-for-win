namespace TranscriptionOverlay.Models;

public sealed class LocalModelRegistration
{
    public string Id { get; set; } = $"custom:{Guid.NewGuid():N}";

    public string ModelKey { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string DirectoryPath { get; set; } = string.Empty;
}
