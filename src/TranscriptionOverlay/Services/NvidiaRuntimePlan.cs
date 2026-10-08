namespace TranscriptionOverlay.Services;

public enum NvidiaCudaRuntimeKind
{
    Cuda126,
    Cuda130,
}

public sealed record NvidiaGpuProfile(
    int Index,
    string Name,
    Version ComputeCapability,
    Version DriverVersion,
    long? TotalMemoryBytes = null);

public sealed record NvidiaRuntimePlan(
    IReadOnlyList<NvidiaGpuProfile> Gpus,
    IReadOnlyList<NvidiaCudaRuntimeKind> RequiredRuntimes,
    NvidiaCudaRuntimeKind PreferredRuntime,
    int PreferredGpuIndex)
{
    private static readonly Version Cuda13MinimumComputeCapability = new(7, 5);
    private static readonly Version BlackwellMinimumComputeCapability = new(10, 0);

    public NvidiaCudaRuntimeKind ResolveRuntimeForGpu(int gpuIndex)
    {
        var profile = Gpus.FirstOrDefault(gpu => gpu.Index == gpuIndex);
        if (profile is null)
        {
            return PreferredRuntime;
        }

        if (profile.ComputeCapability < Cuda13MinimumComputeCapability)
        {
            return NvidiaCudaRuntimeKind.Cuda126;
        }

        return Gpus.Any(gpu => gpu.ComputeCapability >= BlackwellMinimumComputeCapability)
            ? NvidiaCudaRuntimeKind.Cuda130
            : NvidiaCudaRuntimeKind.Cuda126;
    }
}

public sealed record NvidiaRuntimeInstallResult(
    string PythonPath,
    int PreferredGpuIndex,
    NvidiaRuntimePlan Plan);

public static class NvidiaRuntimePlanner
{
    private static readonly Version MinimumSupportedComputeCapability = new(5, 0);
    private static readonly Version Cuda13MinimumComputeCapability = new(7, 5);
    private static readonly Version BlackwellMinimumComputeCapability = new(10, 0);

    public static NvidiaRuntimePlan Build(IReadOnlyList<NvidiaGpuProfile> detectedGpus)
    {
        var supportedGpus = detectedGpus
            .Where(gpu => gpu.ComputeCapability >= MinimumSupportedComputeCapability)
            .OrderBy(gpu => gpu.Index)
            .ToArray();

        if (supportedGpus.Length == 0)
        {
            throw new PlatformNotSupportedException(
                "Aucun GPU NVIDIA pris en charge par les runtimes PyTorch integres n'a ete detecte. " +
                "Une capacite de calcul CUDA 5.0 ou plus recente est requise.");
        }

        var hasBlackwell = supportedGpus.Any(gpu => gpu.ComputeCapability >= BlackwellMinimumComputeCapability);
        var hasCuda126OnlyGpu = supportedGpus.Any(gpu => gpu.ComputeCapability < Cuda13MinimumComputeCapability);

        IReadOnlyList<NvidiaCudaRuntimeKind> requiredRuntimes = hasBlackwell && hasCuda126OnlyGpu
            ? [NvidiaCudaRuntimeKind.Cuda126, NvidiaCudaRuntimeKind.Cuda130]
            : hasBlackwell
                ? [NvidiaCudaRuntimeKind.Cuda130]
                : [NvidiaCudaRuntimeKind.Cuda126];

        var preferredGpu = supportedGpus
            .OrderByDescending(gpu => gpu.ComputeCapability)
            .ThenByDescending(gpu => gpu.TotalMemoryBytes ?? 0)
            .ThenBy(gpu => gpu.Index)
            .First();
        var preferredRuntime = hasBlackwell
            ? NvidiaCudaRuntimeKind.Cuda130
            : NvidiaCudaRuntimeKind.Cuda126;

        return new NvidiaRuntimePlan(
            supportedGpus,
            requiredRuntimes,
            preferredRuntime,
            preferredGpu.Index);
    }
}
