namespace TranscriptionOverlay.Models;

public static class HardwareAccelerationPreference
{
    public const string Auto = "auto";
    public const string Cpu = "cpu";
    public const string GpuPrefix = "gpu:";

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Auto;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized == Cpu)
        {
            return Cpu;
        }

        return TryParseGpuIndex(normalized, out var gpuIndex)
            ? $"{GpuPrefix}{gpuIndex}"
            : Auto;
    }

    public static bool TryParseGpuIndex(string? value, out int gpuIndex)
    {
        gpuIndex = -1;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (!normalized.StartsWith(GpuPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        return int.TryParse(normalized[GpuPrefix.Length..], out gpuIndex) && gpuIndex >= 0;
    }
}
