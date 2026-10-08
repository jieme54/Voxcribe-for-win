namespace TranscriptionOverlay.Models;

public sealed class GpuDiagnostics
{
    public bool Ok { get; set; }

    public string? Error { get; set; }

    public string? PythonVersion { get; set; }

    public string? TorchVersion { get; set; }

    public string? CudaVersion { get; set; }

    public string? HipVersion { get; set; }

    public bool CudaAvailable { get; set; }

    public bool CudaUsable { get; set; }

    public bool HasIntegratedGpu { get; set; }

    public int DeviceCount { get; set; }

    public List<string> SupportedArchitectures { get; set; } = [];

    public List<GpuDeviceInfo> Devices { get; set; } = [];

    public List<GpuDeviceInfo> DisplayControllers { get; set; } = [];
}

public sealed class GpuDeviceInfo
{
    public int Index { get; set; }

    public string Name { get; set; } = string.Empty;

    public long? TotalMemoryBytes { get; set; }

    public string? ComputeCapability { get; set; }

    public bool? IsUsable { get; set; }

    public string? Error { get; set; }

    public string? AdapterCompatibility { get; set; }

    public string? PnpDeviceId { get; set; }

    public bool IsIntegrated { get; set; }
}
