using System.IO;
using System.Text.Json;
using TranscriptionOverlay.Models;

namespace TranscriptionOverlay.Services;

public sealed class LocalModelSourceCatalog
{
    private const string MarkerFileName = ".voxcribe-model.json";
    private const string ManagedSourcePrefix = "managed:";
    private const string SharedSourcePrefix = "shared:";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly TranscriptionModelCatalog _modelCatalog;
    private readonly ModelPackageManager _modelPackageManager;

    public LocalModelSourceCatalog(TranscriptionModelCatalog modelCatalog, ModelPackageManager modelPackageManager)
    {
        _modelCatalog = modelCatalog;
        _modelPackageManager = modelPackageManager;
    }

    public static string GetManagedSourceId(string modelKey) => $"{ManagedSourcePrefix}{modelKey}";

    public static string GetSharedSourceId(string modelKey) => $"{SharedSourcePrefix}{modelKey}";

    public IReadOnlyList<ModelSourceOption> GetSources(
        TranscriptionModelDefinition definition,
        AppSettings settings,
        string languageCode)
    {
        var sources = new List<ModelSourceOption>();

        var managedPath = _modelPackageManager.ResolveManagedModelPath(definition);
        if (!string.IsNullOrWhiteSpace(managedPath))
        {
            sources.Add(new ModelSourceOption(
                Id: GetManagedSourceId(definition.Key),
                DisplayName: UiText.Translate(languageCode, "settings.model.source.managed"),
                DirectoryPath: managedPath,
                SourceKind: ModelSourceKind.Managed));
        }

        var sharedPath = _modelPackageManager.ResolveSharedCachePath(definition);
        if (!string.IsNullOrWhiteSpace(sharedPath))
        {
            sources.Add(new ModelSourceOption(
                Id: GetSharedSourceId(definition.Key),
                DisplayName: UiText.Translate(languageCode, "settings.model.source.shared_cache"),
                DirectoryPath: sharedPath,
                SourceKind: ModelSourceKind.SharedCache));
        }

        foreach (var registration in settings.LocalModelRegistrations
                     .Where(item =>
                         string.Equals(item.ModelKey, definition.Key, StringComparison.OrdinalIgnoreCase)
                         && !string.IsNullOrWhiteSpace(item.DirectoryPath)
                         && Directory.Exists(item.DirectoryPath)
                         && _modelPackageManager.IsModelDirectoryUsable(definition, item.DirectoryPath))
                     .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            sources.Add(new ModelSourceOption(
                Id: registration.Id,
                DisplayName: UiText.Translate(languageCode, "settings.model.source.custom", registration.DirectoryPath),
                DirectoryPath: registration.DirectoryPath,
                SourceKind: ModelSourceKind.CustomDirectory));
        }

        return sources;
    }

    public ModelSourceOption? ResolveSelectedSource(
        TranscriptionModelDefinition definition,
        AppSettings settings,
        string languageCode)
    {
        var sources = GetSources(definition, settings, languageCode);
        if (sources.Count == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(settings.SelectedModelSourceId))
        {
            return null;
        }

        var selected = sources.FirstOrDefault(source =>
            string.Equals(source.Id, settings.SelectedModelSourceId, StringComparison.OrdinalIgnoreCase));
        if (selected is not null && (settings.PinSelectedModelSource || selected.SourceKind != ModelSourceKind.SharedCache))
        {
            return selected;
        }

        return sources.FirstOrDefault(source => source.SourceKind == ModelSourceKind.Managed)
            ?? sources.FirstOrDefault(source => source.SourceKind == ModelSourceKind.CustomDirectory);
    }

    public bool TryRegisterCustomDirectory(
        string directoryPath,
        AppSettings settings,
        string languageCode,
        out ModelSelectionChange? selectionChange,
        out string? errorMessage)
    {
        selectionChange = null;
        errorMessage = null;

        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            errorMessage = UiText.Translate(languageCode, "settings.status.local_model_invalid");
            return false;
        }

        var normalizedInputPath = Path.GetFullPath(directoryPath.Trim());
        var normalizedPath = ResolveModelDirectory(normalizedInputPath, out var definition);
        if (definition is null)
        {
            errorMessage = UiText.Translate(languageCode, "settings.status.local_model_unsupported");
            return false;
        }

        if (!definition.SupportsCurrentOperatingSystem())
        {
            errorMessage = UiText.Translate(languageCode, "settings.status.local_model_unsupported_os");
            return false;
        }

        var existing = settings.LocalModelRegistrations.FirstOrDefault(item =>
            AreSamePath(item.DirectoryPath, normalizedPath));

        if (existing is null)
        {
            existing = new LocalModelRegistration
            {
                Id = $"custom:{Guid.NewGuid():N}",
                ModelKey = definition.Key,
                DisplayName = Path.GetFileName(normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                DirectoryPath = normalizedPath,
            };

            settings.LocalModelRegistrations.Add(existing);
        }
        else
        {
            existing.ModelKey = definition.Key;
            existing.DirectoryPath = normalizedPath;
            if (string.IsNullOrWhiteSpace(existing.DisplayName))
            {
                existing.DisplayName = Path.GetFileName(normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
        }

        selectionChange = null;
        return true;
    }

    public string? RemoveCustomSource(AppSettings settings, string sourceId)
    {
        var registration = settings.LocalModelRegistrations.FirstOrDefault(item =>
            string.Equals(item.Id, sourceId, StringComparison.OrdinalIgnoreCase));
        if (registration is null)
        {
            return null;
        }

        settings.LocalModelRegistrations.Remove(registration);
        return registration.DisplayName;
    }

    private string ResolveModelDirectory(string directoryPath, out TranscriptionModelDefinition? definition)
    {
        foreach (var candidatePath in EnumerateCandidateModelDirectories(directoryPath)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            definition = DetectDefinition(candidatePath);
            if (definition is not null && _modelPackageManager.IsModelDirectoryUsable(definition, candidatePath))
            {
                return candidatePath;
            }
        }

        definition = null;
        return directoryPath;
    }

    private IEnumerable<string> EnumerateCandidateModelDirectories(string directoryPath)
    {
        yield return directoryPath;

        var snapshotsDirectory = string.Equals(
                Path.GetFileName(directoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                "snapshots",
                StringComparison.OrdinalIgnoreCase)
            ? directoryPath
            : Path.Combine(directoryPath, "snapshots");

        if (Directory.Exists(snapshotsDirectory))
        {
            foreach (var snapshotDirectory in EnumerateDirectoriesSafe(snapshotsDirectory)
                         .OrderByDescending(Directory.GetLastWriteTimeUtc))
            {
                yield return snapshotDirectory;
            }
        }

        var directModelChildren = EnumerateDirectoriesSafe(directoryPath)
            .Where(HasDirectModelMetadata)
            .ToList();
        if (directModelChildren.Count == 1)
        {
            yield return directModelChildren[0];
        }
    }

    private TranscriptionModelDefinition? DetectDefinition(string directoryPath)
    {
        var markerDefinition = TryReadMarkerDefinition(directoryPath);
        if (markerDefinition is not null)
        {
            return markerDefinition;
        }

        if (File.Exists(Path.Combine(directoryPath, "packed_manifest.json"))
            && (File.Exists(Path.Combine(directoryPath, "phonon-2.bps.tar.zst"))
                || File.Exists(Path.Combine(directoryPath, "model.fermion"))))
        {
            return _modelCatalog.GetByKeyOrDefault("phonon-2");
        }

        var modelType = TryReadModelType(directoryPath);
        if (!string.IsNullOrWhiteSpace(modelType))
        {
            return modelType switch
            {
                "voxtral" => _modelCatalog.GetByKeyOrDefault(
                    IsRealtimeVoxtralDirectory(directoryPath)
                        ? "voxtral-mini-4b-realtime"
                        : ResolveVoxtralModelKey(directoryPath)),
                "whisper" => _modelCatalog.GetByKeyOrDefault(
                    IsCrisperWhisperDirectory(directoryPath)
                        ? ResolveCrisperWhisperModelKey(directoryPath)
                        : "whisper-large-v3"),
                "qwen3_asr" => _modelCatalog.GetByKeyOrDefault(ResolveQwenModelKey(directoryPath)),
                "parakeet_tdt_five_value" => _modelCatalog.GetByKeyOrDefault("phonon-2"),
                _ => null,
            };
        }

        if (File.Exists(Path.Combine(directoryPath, "tekken.json")) && File.Exists(Path.Combine(directoryPath, "params.json")))
        {
            return _modelCatalog.GetByKeyOrDefault(
                IsRealtimeVoxtralDirectory(directoryPath)
                    ? "voxtral-mini-4b-realtime"
                    : ResolveVoxtralModelKey(directoryPath));
        }

        if (File.Exists(Path.Combine(directoryPath, "normalizer.json")) && File.Exists(Path.Combine(directoryPath, "model.safetensors")))
        {
            return _modelCatalog.GetByKeyOrDefault(
                IsCrisperWhisperDirectory(directoryPath)
                    ? ResolveCrisperWhisperModelKey(directoryPath)
                    : "whisper-large-v3");
        }

        if (File.Exists(Path.Combine(directoryPath, "chat_template.json")) && File.Exists(Path.Combine(directoryPath, "model.safetensors.index.json")))
        {
            return _modelCatalog.GetByKeyOrDefault(ResolveQwenModelKey(directoryPath));
        }

        return null;
    }

    private TranscriptionModelDefinition? TryReadMarkerDefinition(string directoryPath)
    {
        var markerPath = Path.Combine(directoryPath, MarkerFileName);
        if (!File.Exists(markerPath))
        {
            return null;
        }

        try
        {
            var marker = JsonSerializer.Deserialize<ModelMarker>(File.ReadAllText(markerPath), JsonOptions);
            if (string.IsNullOrWhiteSpace(marker?.RepoId))
            {
                return null;
            }

            return _modelCatalog.GetAll().FirstOrDefault(definition =>
                string.Equals(definition.RepoId, marker.RepoId, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static bool IsRealtimeVoxtralDirectory(string directoryPath)
    {
        var normalizedPath = directoryPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (normalizedPath.Contains("realtime", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var configPath = Path.Combine(directoryPath, "config.json");
        if (!File.Exists(configPath))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            if (!document.RootElement.TryGetProperty("architectures", out var architectures)
                || architectures.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var architecture in architectures.EnumerateArray())
            {
                if (architecture.ValueKind == JsonValueKind.String
                    && architecture.GetString()?.Contains("Realtime", StringComparison.OrdinalIgnoreCase) == true)
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static string ResolveQwenModelKey(string directoryPath)
    {
        var normalizedPath = directoryPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (normalizedPath.Contains("0.6b", StringComparison.OrdinalIgnoreCase))
        {
            return "qwen3-asr-0.6b";
        }

        return "qwen3-asr-1.7b";
    }

    private static bool IsCrisperWhisperDirectory(string directoryPath)
    {
        var normalizedPath = directoryPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (normalizedPath.Contains("crisperwhisper2", StringComparison.OrdinalIgnoreCase)
            || normalizedPath.Contains("crisperwhisper-2", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var fileName in new[] { "added_tokens.json", "tokenizer.json", "vocab.json" })
        {
            var filePath = Path.Combine(directoryPath, fileName);
            try
            {
                if (File.Exists(filePath)
                    && File.ReadAllText(filePath).Contains("[verbatim_1]", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch
            {
            }
        }

        return false;
    }

    private static string ResolveCrisperWhisperModelKey(string directoryPath)
    {
        var normalizedPath = directoryPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        foreach (var size in new[] { "small", "medium", "turbo", "large" })
        {
            if (normalizedPath.Contains(size, StringComparison.OrdinalIgnoreCase))
            {
                return $"crisperwhisper-2-{size}";
            }
        }

        var configPath = Path.Combine(directoryPath, "config.json");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = document.RootElement;
            var dModel = root.TryGetProperty("d_model", out var dModelElement)
                         && dModelElement.TryGetInt32(out var dModelValue)
                ? dModelValue
                : 0;
            var decoderLayers = root.TryGetProperty("decoder_layers", out var decoderLayersElement)
                                && decoderLayersElement.TryGetInt32(out var decoderLayersValue)
                ? decoderLayersValue
                : 0;

            if (dModel > 0 && dModel <= 768)
            {
                return "crisperwhisper-2-small";
            }

            if (dModel > 0 && dModel <= 1024)
            {
                return "crisperwhisper-2-medium";
            }

            if (decoderLayers > 0 && decoderLayers <= 4)
            {
                return "crisperwhisper-2-turbo";
            }
        }
        catch
        {
        }

        return "crisperwhisper-2-large";
    }

    private static string ResolveVoxtralModelKey(string directoryPath)
    {
        var normalizedPath = directoryPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (normalizedPath.Contains("24b", StringComparison.OrdinalIgnoreCase)
            || normalizedPath.Contains("voxtral-small", StringComparison.OrdinalIgnoreCase))
        {
            return "voxtral-small-24b";
        }

        return TranscriptionModelCatalog.DefaultModelKey;
    }

    private static string? TryReadModelType(string directoryPath)
    {
        var configPath = Path.Combine(directoryPath, "config.json");
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            if (document.RootElement.TryGetProperty("model_type", out var modelTypeElement)
                && modelTypeElement.ValueKind == JsonValueKind.String)
            {
                return modelTypeElement.GetString();
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool HasDirectModelMetadata(string directoryPath)
    {
        return File.Exists(Path.Combine(directoryPath, MarkerFileName))
               || File.Exists(Path.Combine(directoryPath, "config.json"));
    }

    private static IEnumerable<string> EnumerateDirectoriesSafe(string directoryPath)
    {
        try
        {
            return Directory.EnumerateDirectories(directoryPath).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static bool AreSamePath(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(left), right, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private sealed class ModelMarker
    {
        public string? RepoId { get; set; }
    }
}
