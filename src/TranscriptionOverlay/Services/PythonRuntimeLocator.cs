using System.IO;

namespace TranscriptionOverlay.Services;

public sealed class PythonRuntimeLocator
{
    private const string AmdRuntimeFolderName = "amd-rocm";
    private const string CpuRuntimeFolderName = "cpu";
    private const string LegacyNvidiaRuntimeFolderName = "nvidia-cuda";
    private const string NvidiaCuda126RuntimeFolderName = "nvidia-cuda-cu126";
    private const string NvidiaCuda130RuntimeFolderName = "nvidia-cuda-cu130";
    private const string LegacyQwenNvidiaRuntimeFolderName = "qwen-nvidia-cuda";
    private const string QwenNvidiaCuda126RuntimeFolderName = "qwen-nvidia-cuda-cu126";
    private const string QwenNvidiaCuda130RuntimeFolderName = "qwen-nvidia-cuda-cu130";
    private const string RuntimeCompleteMarkerFileName = ".voxcribe-runtime-complete";

    private readonly AppEnvironment _appEnvironment;
    private string _customPythonPath = string.Empty;

    public PythonRuntimeLocator(AppEnvironment appEnvironment)
    {
        _appEnvironment = appEnvironment;
    }

    public void ConfigureCustomPythonPath(string? pythonPath)
    {
        _customPythonPath = NormalizeManagedPythonPath(pythonPath);
    }

    public string NormalizeManagedPythonPath(string? pythonPath)
    {
        if (string.IsNullOrWhiteSpace(pythonPath))
        {
            return string.Empty;
        }

        try
        {
            var fullPath = Path.GetFullPath(pythonPath.Trim().Trim('"'));
            var runtimesRoot = Path.GetFullPath(_appEnvironment.RuntimesDirectory);
            if (!runtimesRoot.EndsWith(Path.DirectorySeparatorChar))
            {
                runtimesRoot += Path.DirectorySeparatorChar;
            }

            return File.Exists(fullPath)
                   && fullPath.StartsWith(runtimesRoot, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public string ResolvePythonPath(string? modelKey = null)
    {
        var overridePath = Environment.GetEnvironmentVariable("VOXSCRIBE_PYTHON_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        var legacyOverridePath = Environment.GetEnvironmentVariable("VOXTRAL_PYTHON_PATH");
        if (!string.IsNullOrWhiteSpace(legacyOverridePath))
        {
            return legacyOverridePath;
        }

        if (IsQwenModel(modelKey))
        {
            foreach (var qwenRuntimePath in EnumerateQwenRuntimeCandidates())
            {
                if (IsManagedRuntimeComplete(qwenRuntimePath))
                {
                    return qwenRuntimePath;
                }
            }

            var qwenDevelopmentRuntimePath = Path.Combine(_appEnvironment.BackendDirectory, ".venv-qwen", "Scripts", "python.exe");
            if (File.Exists(qwenDevelopmentRuntimePath))
            {
                return qwenDevelopmentRuntimePath;
            }
        }

        if (!string.IsNullOrWhiteSpace(_customPythonPath))
        {
            return _customPythonPath;
        }

        if (HasPortableRuntime(modelKey))
        {
            return _appEnvironment.PortablePythonExecutablePath;
        }

        var installedRuntimePath = ResolveInstalledManagedRuntimePath();
        if (!string.IsNullOrWhiteSpace(installedRuntimePath))
        {
            return installedRuntimePath;
        }

        return Path.Combine(_appEnvironment.BackendDirectory, ".venv", "Scripts", "python.exe");
    }

    public string ResolveInstalledManagedRuntimePath()
    {
        foreach (var runtimeFolderName in new[]
                 {
                     NvidiaCuda130RuntimeFolderName,
                     NvidiaCuda126RuntimeFolderName,
                     LegacyNvidiaRuntimeFolderName,
                 })
        {
            var nvidiaRuntimePath = GetManagedRuntimePythonPath(runtimeFolderName);
            if (IsManagedRuntimeComplete(nvidiaRuntimePath))
            {
                return nvidiaRuntimePath;
            }
        }

        var amdRuntimePath = GetManagedRuntimePythonPath(AmdRuntimeFolderName);
        if (IsManagedRuntimeComplete(amdRuntimePath))
        {
            return amdRuntimePath;
        }

        var cpuRuntimePath = GetManagedRuntimePythonPath(CpuRuntimeFolderName);
        return IsManagedRuntimeComplete(cpuRuntimePath) ? cpuRuntimePath : string.Empty;
    }

    public static bool IsManagedRuntimeComplete(string pythonPath)
    {
        return File.Exists(pythonPath) && File.Exists(GetManagedRuntimeCompleteMarkerPath(pythonPath));
    }

    public static string GetManagedRuntimeCompleteMarkerPath(string pythonPath)
    {
        var scriptsDirectory = Path.GetDirectoryName(pythonPath) ?? string.Empty;
        var runtimeRoot = Path.GetDirectoryName(scriptsDirectory) ?? string.Empty;
        return Path.Combine(runtimeRoot, RuntimeCompleteMarkerFileName);
    }

    public bool HasPortableRuntime(string? modelKey = null)
    {
        if (IsQwenModel(modelKey))
        {
            return false;
        }

        return File.Exists(_appEnvironment.PortablePythonExecutablePath);
    }

    public string? ResolveOverlayPath(string? modelKey = null)
    {
        if (!IsVoxtralRealtimeModel(modelKey))
        {
            return null;
        }

        var overridePath = Environment.GetEnvironmentVariable("VOXSCRIBE_VOXTRAL_REALTIME_OVERLAY_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        return Directory.Exists(_appEnvironment.PortableVoxtralRealtimeOverlayDirectory)
            ? _appEnvironment.PortableVoxtralRealtimeOverlayDirectory
            : null;
    }

    public bool HasOverlay(string? modelKey = null)
    {
        var overlayPath = ResolveOverlayPath(modelKey);
        return !string.IsNullOrWhiteSpace(overlayPath) && Directory.Exists(overlayPath);
    }

    private static bool IsVoxtralRealtimeModel(string? modelKey)
    {
        return string.Equals(modelKey, "voxtral-mini-4b-realtime", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsQwenModel(string? modelKey)
    {
        return !string.IsNullOrWhiteSpace(modelKey)
               && modelKey.StartsWith("qwen3-asr-", StringComparison.OrdinalIgnoreCase);
    }

    private string GetManagedRuntimePythonPath(string runtimeFolderName)
    {
        return Path.Combine(_appEnvironment.RuntimesDirectory, runtimeFolderName, "Scripts", "python.exe");
    }

    private IEnumerable<string> EnumerateQwenRuntimeCandidates()
    {
        var configuredCudaTag = ResolveConfiguredNvidiaCudaTag();
        if (!string.IsNullOrWhiteSpace(configuredCudaTag))
        {
            yield return GetManagedRuntimePythonPath(
                string.Equals(configuredCudaTag, "cu130", StringComparison.Ordinal)
                    ? QwenNvidiaCuda130RuntimeFolderName
                    : QwenNvidiaCuda126RuntimeFolderName);

            var legacyPath = GetManagedRuntimePythonPath(LegacyQwenNvidiaRuntimeFolderName);
            if (RuntimeMarkerContainsBackendTag(legacyPath, configuredCudaTag))
            {
                yield return legacyPath;
            }

            yield break;
        }

        yield return GetManagedRuntimePythonPath(QwenNvidiaCuda130RuntimeFolderName);
        yield return GetManagedRuntimePythonPath(QwenNvidiaCuda126RuntimeFolderName);
        yield return GetManagedRuntimePythonPath(LegacyQwenNvidiaRuntimeFolderName);
    }

    private string ResolveConfiguredNvidiaCudaTag()
    {
        if (string.IsNullOrWhiteSpace(_customPythonPath))
        {
            return string.Empty;
        }

        var scriptsDirectory = Path.GetDirectoryName(_customPythonPath);
        var runtimeRoot = string.IsNullOrWhiteSpace(scriptsDirectory)
            ? null
            : Path.GetDirectoryName(scriptsDirectory);
        var runtimeFolderName = string.IsNullOrWhiteSpace(runtimeRoot)
            ? string.Empty
            : Path.GetFileName(runtimeRoot);
        if (string.Equals(runtimeFolderName, NvidiaCuda130RuntimeFolderName, StringComparison.OrdinalIgnoreCase))
        {
            return "cu130";
        }

        if (string.Equals(runtimeFolderName, NvidiaCuda126RuntimeFolderName, StringComparison.OrdinalIgnoreCase))
        {
            return "cu126";
        }

        foreach (var cudaTag in new[] { "cu130", "cu126" })
        {
            if (RuntimeMarkerContainsBackendTag(_customPythonPath, cudaTag))
            {
                return cudaTag;
            }
        }

        return string.Empty;
    }

    private static bool RuntimeMarkerContainsBackendTag(string pythonPath, string backendTag)
    {
        try
        {
            var markerPath = GetManagedRuntimeCompleteMarkerPath(pythonPath);
            return File.Exists(markerPath)
                   && File.ReadAllText(markerPath).Contains(
                       $"-{backendTag}-",
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
