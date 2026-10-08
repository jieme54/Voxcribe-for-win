using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using TranscriptionOverlay.Models;

namespace TranscriptionOverlay.Services;

public sealed class BackendCapabilityService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly AppEnvironment _appEnvironment;
    private readonly PythonRuntimeLocator _pythonRuntimeLocator;
    private readonly object _capabilitiesLock = new();
    private readonly object _gpuDiagnosticsLock = new();
    private Task<BackendCapabilities>? _capabilitiesTask;
    private BackendCapabilities? _cachedCapabilities;
    private Task<GpuDiagnostics>? _gpuDiagnosticsTask;
    private GpuDiagnostics? _cachedGpuDiagnostics;

    public event EventHandler<GpuDiagnostics>? GpuDiagnosticsUpdated;

    public BackendCapabilityService(AppEnvironment appEnvironment, PythonRuntimeLocator pythonRuntimeLocator)
    {
        _appEnvironment = appEnvironment;
        _pythonRuntimeLocator = pythonRuntimeLocator;
    }

    public Task<BackendCapabilities> DetectAsync(
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        lock (_capabilitiesLock)
        {
            if (forceRefresh)
            {
                _cachedCapabilities = null;
            }

            if (_cachedCapabilities is not null)
            {
                return Task.FromResult(_cachedCapabilities);
            }

            if (_capabilitiesTask is not null)
            {
                return _capabilitiesTask.WaitAsync(cancellationToken);
            }

            _capabilitiesTask = DetectCapabilitiesCoreAsync(cancellationToken);
            return _capabilitiesTask.WaitAsync(cancellationToken);
        }
    }

    private async Task<BackendCapabilities> DetectCapabilitiesCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var defaultProbe = await ProbeDefaultRuntimeAsync(cancellationToken);
            var hasQwenRuntime = await ProbeQwenRuntimeAsync(cancellationToken);
            var hasRealtimeRuntime = await ProbeVoxtralRealtimeRuntimeAsync(cancellationToken);
            var capabilities = new BackendCapabilities(
                HasQwenRuntime: hasQwenRuntime,
                HasCrisperWhisperRuntime: defaultProbe.HasCrisperWhisperRuntime,
                HasPhononRuntime: defaultProbe.HasPhononRuntime,
                HasVoxtralRealtimeRuntime: hasRealtimeRuntime,
                PythonVersion: defaultProbe.PythonVersion);
            _cachedCapabilities = capabilities;
            return capabilities;
        }
        finally
        {
            lock (_capabilitiesLock)
            {
                _capabilitiesTask = null;
            }
        }
    }

    public BackendCapabilities? CachedCapabilities => _cachedCapabilities;

    public GpuDiagnostics? CachedGpuDiagnostics => _cachedGpuDiagnostics;

    public Task<GpuDiagnostics> DetectGpuDiagnosticsAsync(
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        lock (_gpuDiagnosticsLock)
        {
            if (forceRefresh)
            {
                _cachedGpuDiagnostics = null;
            }

            if (_cachedGpuDiagnostics is not null)
            {
                return Task.FromResult(_cachedGpuDiagnostics);
            }

            if (_gpuDiagnosticsTask is not null)
            {
                return _gpuDiagnosticsTask.WaitAsync(cancellationToken);
            }

            _gpuDiagnosticsTask = DetectGpuDiagnosticsCoreAsync(cancellationToken);
            return _gpuDiagnosticsTask.WaitAsync(cancellationToken);
        }
    }

    private async Task<GpuDiagnostics> DetectGpuDiagnosticsCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var diagnostics = await RunGpuDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
            _cachedGpuDiagnostics = diagnostics;
            GpuDiagnosticsUpdated?.Invoke(this, diagnostics);
            return diagnostics;
        }
        finally
        {
            lock (_gpuDiagnosticsLock)
            {
                _gpuDiagnosticsTask = null;
            }
        }
    }

    private async Task<GpuDiagnostics> RunGpuDiagnosticsAsync(CancellationToken cancellationToken)
    {
        var pythonPath = _pythonRuntimeLocator.ResolvePythonPath();
        if (!File.Exists(pythonPath))
        {
            return await BuildUnavailableGpuDiagnosticsAsync(
                $"Python introuvable : {pythonPath}",
                cancellationToken).ConfigureAwait(false);
        }

        var scriptPath = Path.Combine(_appEnvironment.BackendDirectory, "model_manager.py");
        if (!File.Exists(scriptPath))
        {
            return await BuildUnavailableGpuDiagnosticsAsync(
                $"Gestionnaire de modeles introuvable : {scriptPath}",
                cancellationToken).ConfigureAwait(false);
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = $"\"{scriptPath}\" gpu-diagnostics",
                WorkingDirectory = _appEnvironment.BackendDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        if (!process.Start())
        {
            return await BuildUnavailableGpuDiagnosticsAsync(
                "Le diagnostic GPU n'a pas pu demarrer.",
                cancellationToken).ConfigureAwait(false);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            return await BuildUnavailableGpuDiagnosticsAsync(
                string.IsNullOrWhiteSpace(stderr) ? "Le diagnostic GPU a echoue." : stderr.Trim(),
                cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(stdout))
        {
            return await BuildUnavailableGpuDiagnosticsAsync(
                "Le diagnostic GPU n'a rien retourne.",
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var diagnostics = JsonSerializer.Deserialize<GpuDiagnostics>(ExtractJsonPayload(stdout), JsonOptions)
                              ?? new GpuDiagnostics { Ok = false, Error = "Diagnostic GPU illisible." };
            await EnrichDisplayControllersAsync(diagnostics, cancellationToken).ConfigureAwait(false);
            return diagnostics;
        }
        catch (Exception ex)
        {
            return await BuildUnavailableGpuDiagnosticsAsync(ex.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<GpuDiagnostics> BuildUnavailableGpuDiagnosticsAsync(
        string error,
        CancellationToken cancellationToken)
    {
        var diagnostics = new GpuDiagnostics
        {
            Ok = false,
            Error = error,
        };

        await EnrichDisplayControllersAsync(diagnostics, cancellationToken).ConfigureAwait(false);
        return diagnostics;
    }

    private async Task EnrichDisplayControllersAsync(
        GpuDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        diagnostics.Devices ??= [];
        diagnostics.DisplayControllers ??= [];

        if (diagnostics.DisplayControllers.Count == 0)
        {
            diagnostics.DisplayControllers = await DetectWindowsDisplayControllersAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        diagnostics.HasIntegratedGpu = diagnostics.HasIntegratedGpu
                                       || diagnostics.DisplayControllers.Any(controller => controller.IsIntegrated);
    }

    private static async Task<List<GpuDeviceInfo>> DetectWindowsDisplayControllersAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var commands = new[]
        {
            (
                "powershell.exe",
                "-NoProfile -Command \"Get-CimInstance Win32_VideoController | Select-Object Name,AdapterCompatibility,PNPDeviceID | ConvertTo-Json -Compress\""
            ),
            (
                "powershell.exe",
                "-NoProfile -Command \"Get-WmiObject Win32_VideoController | Select-Object Name,AdapterCompatibility,PNPDeviceID | ConvertTo-Json -Compress\""
            ),
        };

        foreach (var command in commands)
        {
            var output = await TryCaptureProcessAsync(command.Item1, command.Item2, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(output))
            {
                continue;
            }

            var controllers = ParseWindowsDisplayControllers(output);
            if (controllers.Count > 0)
            {
                return controllers;
            }
        }

        return DetectWindowsDisplayControllersFromRegistry();
    }

    private static List<GpuDeviceInfo> DetectWindowsDisplayControllersFromRegistry()
    {
        var controllers = new List<GpuDeviceInfo>();
        try
        {
            using var pciRoot = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI");
            if (pciRoot is null)
            {
                return controllers;
            }

            foreach (var deviceKeyName in pciRoot.GetSubKeyNames())
            {
                using var deviceKey = pciRoot.OpenSubKey(deviceKeyName);
                if (deviceKey is null)
                {
                    continue;
                }

                foreach (var instanceKeyName in deviceKey.GetSubKeyNames())
                {
                    using var instanceKey = deviceKey.OpenSubKey(instanceKeyName);
                    if (instanceKey is null)
                    {
                        continue;
                    }

                    var className = CleanRegistryText(instanceKey.GetValue("Class"));
                    var classGuid = CleanRegistryText(instanceKey.GetValue("ClassGUID"));
                    var hardwareId = CleanRegistryText(instanceKey.GetValue("HardwareID"));
                    var name = CleanRegistryText(instanceKey.GetValue("FriendlyName"));
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = CleanRegistryText(instanceKey.GetValue("DeviceDesc"));
                    }

                    var manufacturer = CleanRegistryText(instanceKey.GetValue("Mfg"));
                    var pnpDeviceId = $@"PCI\{deviceKeyName}\{instanceKeyName}";
                    if (string.IsNullOrWhiteSpace(name)
                        || !IsRegistryDisplayController(
                            name,
                            manufacturer,
                            pnpDeviceId,
                            hardwareId,
                            className,
                            classGuid))
                    {
                        continue;
                    }

                    var controller = new GpuDeviceInfo
                    {
                        Index = controllers.Count,
                        Name = name,
                        AdapterCompatibility = manufacturer,
                        PnpDeviceId = pnpDeviceId,
                    };
                    controller.IsIntegrated = IsProbablyIntegratedGpu(controller);
                    controllers.Add(controller);
                }
            }
        }
        catch
        {
            return [];
        }

        return DeduplicateDisplayControllers(controllers);
    }

    private static string CleanRegistryText(object? value)
    {
        var text = value switch
        {
            string[] values => string.Join(' ', values.Where(item => !string.IsNullOrWhiteSpace(item))),
            _ => Convert.ToString(value) ?? string.Empty,
        };
        text = text.Trim();
        var separatorIndex = text.LastIndexOf(';');
        return separatorIndex >= 0 && separatorIndex + 1 < text.Length
            ? text[(separatorIndex + 1)..].Trim()
            : text;
    }

    private static bool IsRegistryDisplayController(
        string name,
        string manufacturer,
        string pnpDeviceId,
        string hardwareId,
        string className,
        string classGuid)
    {
        if (string.Equals(className, "Display", StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                classGuid,
                "{4d36e968-e325-11ce-bfc1-08002be10318}",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var text = $"{name} {manufacturer} {pnpDeviceId} {hardwareId}".ToLowerInvariant();
        if (new[] { "audio", "usb", "smbus", "host bridge", "root port" }
            .Any(marker => text.Contains(marker, StringComparison.Ordinal)))
        {
            return false;
        }

        var hasVendorMarker = new[] { "ven_10de", "ven_1002", "ven_8086", "nvidia", "radeon", "amd", "intel" }
            .Any(marker => text.Contains(marker, StringComparison.Ordinal));
        var hasDisplayMarker = new[]
            {
                "display", "graphics", "vga", "3d video", "video controller",
                "geforce", "quadro", "rtx", "gtx",
            }
            .Any(marker => text.Contains(marker, StringComparison.Ordinal));
        return hasVendorMarker && hasDisplayMarker;
    }

    private static List<GpuDeviceInfo> DeduplicateDisplayControllers(IEnumerable<GpuDeviceInfo> controllers)
    {
        var deduplicated = new List<GpuDeviceInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var controller in controllers)
        {
            var key = $"{controller.Name}\n{controller.PnpDeviceId}";
            if (!seen.Add(key))
            {
                continue;
            }

            controller.Index = deduplicated.Count;
            deduplicated.Add(controller);
        }

        return deduplicated;
    }

    private static async Task<string?> TryCaptureProcessAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            if (!process.Start())
            {
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            _ = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            return process.ExitCode == 0 ? stdout.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static List<GpuDeviceInfo> ParseWindowsDisplayControllers(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                return [NormalizeDisplayController(root, 0)];
            }

            if (root.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var controllers = new List<GpuDeviceInfo>();
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                controllers.Add(NormalizeDisplayController(item, controllers.Count));
            }

            return controllers;
        }
        catch
        {
            return [];
        }
    }

    private static GpuDeviceInfo NormalizeDisplayController(JsonElement item, int index)
    {
        var controller = new GpuDeviceInfo
        {
            Index = index,
            Name = ReadString(item, "Name") is { Length: > 0 } name ? name : $"GPU {index + 1}",
            AdapterCompatibility = ReadString(item, "AdapterCompatibility"),
            PnpDeviceId = ReadString(item, "PNPDeviceID") ?? ReadString(item, "PnpDeviceId"),
        };
        controller.IsIntegrated = IsProbablyIntegratedGpu(controller);
        return controller;
    }

    private static string? ReadString(JsonElement item, string propertyName)
    {
        foreach (var property in item.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()?.Trim()
                : property.Value.ToString().Trim();
        }

        return null;
    }

    private static bool IsProbablyIntegratedGpu(GpuDeviceInfo controller)
    {
        var text = $"{controller.Name} {controller.AdapterCompatibility} {controller.PnpDeviceId}".ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.Contains("microsoft basic", StringComparison.Ordinal)
            || text.Contains("remote display", StringComparison.Ordinal)
            || text.Contains("virtual", StringComparison.Ordinal)
            || text.Contains("nvidia", StringComparison.Ordinal))
        {
            return false;
        }

        if (text.Contains("intel", StringComparison.Ordinal)
            && (text.Contains("uhd", StringComparison.Ordinal)
                || text.Contains("iris", StringComparison.Ordinal)
                || text.Contains("graphics", StringComparison.Ordinal)))
        {
            return true;
        }

        var amdIntegratedMarkers = new[]
        {
            "radeon(tm) graphics",
            "radeon graphics",
            "radeon 610m",
            "radeon 660m",
            "radeon 680m",
            "radeon 740m",
            "radeon 760m",
            "radeon 780m",
            "radeon 880m",
            "radeon 890m",
            "radeon 8050s",
            "radeon 8060s",
        };

        return amdIntegratedMarkers.Any(marker => text.Contains(marker, StringComparison.Ordinal));
    }

    private async Task<(bool HasQwenRuntime, bool HasCrisperWhisperRuntime, bool HasPhononRuntime, string? PythonVersion)> ProbeDefaultRuntimeAsync(
        CancellationToken cancellationToken)
    {
        return await ProbeRuntimeAsync(modelKey: null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ProbeQwenRuntimeAsync(CancellationToken cancellationToken)
    {
        var probe = await ProbeRuntimeAsync("qwen3-asr-0.6b", cancellationToken).ConfigureAwait(false);
        return probe.HasQwenRuntime;
    }

    private async Task<(bool HasQwenRuntime, bool HasCrisperWhisperRuntime, bool HasPhononRuntime, string? PythonVersion)> ProbeRuntimeAsync(
        string? modelKey,
        CancellationToken cancellationToken)
    {
        var pythonPath = _pythonRuntimeLocator.ResolvePythonPath(modelKey);
        if (!File.Exists(pythonPath))
        {
            return (false, false, false, null);
        }

        var scriptPath = Path.Combine(_appEnvironment.BackendDirectory, "model_manager.py");
        if (!File.Exists(scriptPath))
        {
            return (false, false, false, null);
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = pythonPath,
                Arguments = $"\"{scriptPath}\" probe",
                WorkingDirectory = _appEnvironment.BackendDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        if (!process.Start())
        {
            return (false, false, false, null);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            _ = await stderrTask;
            return (false, false, false, null);
        }

        var stdout = await stdoutTask;
        if (string.IsNullOrWhiteSpace(stdout))
        {
            return (false, false, false, null);
        }

        try
        {
            var payload = JsonSerializer.Deserialize<ProbePayload>(stdout, JsonOptions);
            return (
                payload?.HasQwenRuntime == true,
                payload?.HasCrisperWhisperRuntime == true,
                payload?.HasPhononRuntime == true,
                payload?.PythonVersion);
        }
        catch
        {
            return (false, false, false, null);
        }
    }

    private async Task<bool> ProbeVoxtralRealtimeRuntimeAsync(CancellationToken cancellationToken)
    {
        var pythonPath = _pythonRuntimeLocator.ResolvePythonPath("voxtral-mini-4b-realtime");
        var overlayPath = _pythonRuntimeLocator.ResolveOverlayPath("voxtral-mini-4b-realtime");
        if (!File.Exists(pythonPath))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            Arguments = "-c \"from transformers import VoxtralRealtimeForConditionalGeneration; print('ok')\"",
            WorkingDirectory = _appEnvironment.BackendDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (!string.IsNullOrWhiteSpace(overlayPath))
        {
            startInfo.Environment["PYTHONPATH"] = overlayPath;
        }

        var process = new Process
        {
            StartInfo = startInfo,
        };

        if (!process.Start())
        {
            return false;
        }

        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode == 0;
    }

    private static string ExtractJsonPayload(string value)
    {
        var startIndex = value.IndexOf('{');
        var endIndex = value.LastIndexOf('}');
        return startIndex >= 0 && endIndex > startIndex
            ? value[startIndex..(endIndex + 1)]
            : value;
    }

    private sealed class ProbePayload
    {
        public bool HasQwenRuntime { get; set; }

        public bool HasCrisperWhisperRuntime { get; set; }

        public bool HasPhononRuntime { get; set; }

        public bool HasVoxtralRealtimeRuntime { get; set; }

        public string? PythonVersion { get; set; }
    }
}
