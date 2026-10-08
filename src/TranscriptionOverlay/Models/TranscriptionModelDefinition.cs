namespace TranscriptionOverlay.Models;

public sealed record TranscriptionModelDefinition(
    string Key,
    string DisplayName,
    string RepoId,
    string SizeLabel,
    string[] DownloadPatterns,
    ModelRuntimeKind RuntimeKind,
    bool SupportsFileTranscription,
    bool SupportsRealtimeTranscription,
    bool SupportsWindowsRuntime,
    bool SupportsLinuxRuntime,
    bool SupportsMacOsRuntime,
    bool RequiresOptionalRuntime,
    string DetailsFr,
    string DetailsEn,
    string PublisherKey,
    string PublisherDisplayName,
    string ModelFamilyKey,
    string ModelFamilyDisplayName,
    string? VariantDisplayName)
{
    public bool SupportsCurrentOperatingSystem()
    {
        if (OperatingSystem.IsWindows())
        {
            return SupportsWindowsRuntime;
        }

        if (OperatingSystem.IsLinux())
        {
            return SupportsLinuxRuntime;
        }

        if (OperatingSystem.IsMacOS())
        {
            return SupportsMacOsRuntime;
        }

        return false;
    }

    public bool SupportsMode(TranscriptionMode mode)
    {
        return mode switch
        {
            TranscriptionMode.Realtime => SupportsRealtimeTranscription,
            _ => SupportsFileTranscription,
        };
    }
}
