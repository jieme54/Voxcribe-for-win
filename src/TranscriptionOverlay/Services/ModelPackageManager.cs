using System.Diagnostics;
using System.IO;
using System.IO.Enumeration;
using System.Text.Json;
using TranscriptionOverlay.Models;

namespace TranscriptionOverlay.Services;

public sealed class ModelPackageManager
{
    private const string MarkerFileName = ".voxcribe-model.json";
    private const int CurrentMarkerVersion = 3;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly AppEnvironment _appEnvironment;
    private readonly PythonRuntimeLocator _pythonRuntimeLocator;

    public ModelPackageManager(AppEnvironment appEnvironment, PythonRuntimeLocator pythonRuntimeLocator)
    {
        _appEnvironment = appEnvironment;
        _pythonRuntimeLocator = pythonRuntimeLocator;
    }

    public bool IsModelDownloaded(TranscriptionModelDefinition definition)
    {
        TryRecoverManagedModelDirectory(definition);
        var modelDirectory = GetManagedModelDirectory(definition);
        var markerPath = Path.Combine(modelDirectory, MarkerFileName);
        if (!File.Exists(markerPath))
        {
            return false;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<ModelMarker>(File.ReadAllText(markerPath), JsonOptions);
            return payload?.Version == CurrentMarkerVersion
                   && HasRequiredModelFiles(definition, modelDirectory);
        }
        catch
        {
            return false;
        }
    }

    public ModelSourceKind GetModelSourceKind(TranscriptionModelDefinition definition)
    {
        if (IsModelDownloaded(definition))
        {
            return ModelSourceKind.Managed;
        }

        return ResolveSharedCachePath(definition) is not null
            ? ModelSourceKind.SharedCache
            : ModelSourceKind.None;
    }

    public bool IsModelAvailableLocally(TranscriptionModelDefinition definition)
    {
        return GetModelSourceKind(definition) != ModelSourceKind.None;
    }

    public bool IsModelDirectoryUsable(TranscriptionModelDefinition definition, string directoryPath)
    {
        return Directory.Exists(directoryPath) && HasRequiredModelFiles(definition, directoryPath);
    }

    public string? ResolveManagedModelPath(TranscriptionModelDefinition definition)
    {
        TryRecoverManagedModelDirectory(definition);
        var modelDirectory = GetManagedModelDirectory(definition);
        return IsModelDownloaded(definition) ? modelDirectory : null;
    }

    public string? ResolveSharedCachePath(TranscriptionModelDefinition definition)
    {
        return ResolveSharedCacheSnapshotPath(definition);
    }

    public string? ResolvePreferredModelPath(TranscriptionModelDefinition definition)
    {
        return ResolveManagedModelPath(definition)
            ?? ResolveSharedCachePath(definition);
    }

    public async Task DownloadAsync(TranscriptionModelDefinition definition, CancellationToken cancellationToken)
    {
        if (IsModelDownloaded(definition))
        {
            return;
        }

        var pythonPath = _pythonRuntimeLocator.ResolvePythonPath();
        if (!File.Exists(pythonPath))
        {
            throw new FileNotFoundException($"Python introuvable : {pythonPath}");
        }

        var scriptPath = Path.Combine(_appEnvironment.BackendDirectory, "model_manager.py");
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"Script de gestion des mod\u00e8les introuvable : {scriptPath}");
        }

        Directory.CreateDirectory(_appEnvironment.ModelsDirectory);

        var targetDirectory = GetManagedModelDirectory(definition);
        var stagingDirectory = GetManagedStagingDirectory(definition);

        if (TryRecoverManagedModelDirectory(definition))
        {
            return;
        }

        var patternArguments = string.Join(
            " ",
            definition.DownloadPatterns.Select(pattern => $"--pattern \"{pattern}\""));

        Exception? lastError = null;
        const int maxAttempts = 2;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryDeleteDirectory(stagingDirectory);

            try
            {
                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = pythonPath,
                        Arguments = $"\"{scriptPath}\" download --repo-id \"{definition.RepoId}\" --target-dir \"{stagingDirectory}\" {patternArguments}",
                        WorkingDirectory = _appEnvironment.BackendDirectory,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    },
                };

                if (!process.Start())
                {
                    throw new InvalidOperationException("Le t\u00e9l\u00e9chargement du mod\u00e8le n'a pas pu d\u00e9marrer.");
                }

                var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);

                var stdout = await outputTask;
                var stderr = await errorTask;

                if (process.ExitCode != 0)
                {
                    var details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(details) ? "Erreur inconnue." : details.Trim());
                }

                TryDeleteDirectory(targetDirectory);
                Directory.Move(stagingDirectory, targetDirectory);
                TryWriteSizeMetadata(targetDirectory);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && ex is not OperationCanceledException)
            {
                lastError = ex;
                TryDeleteDirectory(stagingDirectory);
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch
            {
                TryDeleteDirectory(stagingDirectory);
                throw;
            }
        }

        throw lastError ?? new InvalidOperationException("Erreur inconnue.");
    }

    public Task DeleteAsync(TranscriptionModelDefinition definition, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var modelDirectory = GetManagedModelDirectory(definition);
        var stagingDirectory = GetManagedStagingDirectory(definition);
        if (Directory.Exists(modelDirectory))
        {
            Directory.Delete(modelDirectory, recursive: true);
        }

        if (Directory.Exists(stagingDirectory))
        {
            Directory.Delete(stagingDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    public Task DeleteSharedCacheAsync(TranscriptionModelDefinition definition, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var repoDirectory = ResolveSharedCacheRepositoryDirectory(definition);
        if (!string.IsNullOrWhiteSpace(repoDirectory) && Directory.Exists(repoDirectory))
        {
            Directory.Delete(repoDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    public string GetSizeLabel(TranscriptionModelDefinition definition)
    {
        return GetSizeLabel(definition, null);
    }

    public string GetSizeLabel(TranscriptionModelDefinition definition, string? directoryPath)
    {
        if (!string.IsNullOrWhiteSpace(directoryPath) && Directory.Exists(directoryPath))
        {
            try
            {
                return FormatSize(GetDirectorySizeBytes(directoryPath));
            }
            catch
            {
            }
        }

        if (IsModelDownloaded(definition))
        {
            var modelDirectory = GetManagedModelDirectory(definition);
            var markerPath = Path.Combine(modelDirectory, MarkerFileName);
            try
            {
                if (File.Exists(markerPath))
                {
                    var payload = JsonSerializer.Deserialize<ModelMarker>(File.ReadAllText(markerPath), JsonOptions);
                    if (payload?.SizeBytes is > 0)
                    {
                        return FormatSize(payload.SizeBytes.Value);
                    }
                }
            }
            catch
            {
            }

            return FormatSize(GetDirectorySizeBytes(modelDirectory));
        }

        var sharedCachePath = ResolveSharedCachePath(definition);
        if (!string.IsNullOrWhiteSpace(sharedCachePath) && Directory.Exists(sharedCachePath))
        {
            try
            {
                return FormatSize(GetDirectorySizeBytes(sharedCachePath));
            }
            catch
            {
            }
        }

        return definition.SizeLabel;
    }

    private string GetManagedModelDirectory(TranscriptionModelDefinition definition)
    {
        return _appEnvironment.GetManagedModelDirectory(definition.RepoId);
    }

    private string GetManagedStagingDirectory(TranscriptionModelDefinition definition)
    {
        return $"{GetManagedModelDirectory(definition)}.downloading";
    }

    private bool TryRecoverManagedModelDirectory(TranscriptionModelDefinition definition)
    {
        var targetDirectory = GetManagedModelDirectory(definition);
        if (HasValidMarker(definition, targetDirectory)
            || TryRepairCompleteModelDirectory(definition, targetDirectory))
        {
            return true;
        }

        var stagingDirectory = GetManagedStagingDirectory(definition);
        if (!HasValidMarker(definition, stagingDirectory)
            && !TryRepairCompleteModelDirectory(definition, stagingDirectory))
        {
            return false;
        }

        try
        {
            TryDeleteDirectory(targetDirectory);
            Directory.Move(stagingDirectory, targetDirectory);
            TryWriteSizeMetadata(targetDirectory);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryRepairCompleteModelDirectory(TranscriptionModelDefinition definition, string directoryPath)
    {
        if (!Directory.Exists(directoryPath) || !HasRequiredModelFiles(definition, directoryPath))
        {
            return false;
        }

        try
        {
            WriteMarker(directoryPath, definition.RepoId);
            TryWriteSizeMetadata(directoryPath);
            return HasValidMarker(definition, directoryPath);
        }
        catch
        {
            return false;
        }
    }

    private string? ResolveSharedCacheSnapshotPath(TranscriptionModelDefinition definition)
    {
        foreach (var repoDirectory in EnumerateSharedCacheRepositoryDirectories(definition))
        {
            var snapshotsDirectory = Path.Combine(repoDirectory, "snapshots");
            if (!Directory.Exists(snapshotsDirectory))
            {
                continue;
            }

            var candidate = Directory
                .EnumerateDirectories(snapshotsDirectory)
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault(path => IsModelDirectoryUsable(definition, path));

            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private string? ResolveSharedCacheRepositoryDirectory(TranscriptionModelDefinition definition)
    {
        return EnumerateSharedCacheRepositoryDirectories(definition)
            .FirstOrDefault(Directory.Exists);
    }

    private IEnumerable<string> EnumerateSharedCacheRepositoryDirectories(TranscriptionModelDefinition definition)
    {
        var repoDirectoryName = GetHubCacheDirectoryName(definition.RepoId);
        foreach (var hubDirectory in GetHubCacheDirectories())
        {
            yield return Path.Combine(hubDirectory, repoDirectoryName);
        }
    }

    private static IEnumerable<string> GetHubCacheDirectories()
    {
        var directHubCache = Environment.GetEnvironmentVariable("HF_HUB_CACHE");
        if (!string.IsNullOrWhiteSpace(directHubCache))
        {
            yield return directHubCache;
        }

        var legacyHubCache = Environment.GetEnvironmentVariable("HUGGINGFACE_HUB_CACHE");
        if (!string.IsNullOrWhiteSpace(legacyHubCache))
        {
            yield return legacyHubCache;
        }

        var hfHome = Environment.GetEnvironmentVariable("HF_HOME");
        if (!string.IsNullOrWhiteSpace(hfHome))
        {
            yield return Path.Combine(hfHome, "hub");
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            yield return Path.Combine(userProfile, ".cache", "huggingface", "hub");
        }
    }

    private static string GetHubCacheDirectoryName(string repoId)
    {
        return "models--" + repoId.Replace("/", "--", StringComparison.Ordinal);
    }

    private void TryWriteSizeMetadata(string modelDirectory)
    {
        var markerPath = Path.Combine(modelDirectory, MarkerFileName);
        if (!File.Exists(markerPath))
        {
            return;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<ModelMarker>(File.ReadAllText(markerPath), JsonOptions) ?? new ModelMarker();
            payload.SizeBytes = GetDirectorySizeBytes(modelDirectory);
            payload.Version = CurrentMarkerVersion;
            File.WriteAllText(markerPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }

    private static void WriteMarker(string modelDirectory, string repoId)
    {
        var payload = new ModelMarker
        {
            RepoId = repoId,
            SizeBytes = GetDirectorySizeBytes(modelDirectory),
            Version = CurrentMarkerVersion,
        };
        var markerPath = Path.Combine(modelDirectory, MarkerFileName);
        File.WriteAllText(markerPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private bool HasValidMarker(TranscriptionModelDefinition definition, string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return false;
        }

        var markerPath = Path.Combine(directoryPath, MarkerFileName);
        if (!File.Exists(markerPath))
        {
            return false;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<ModelMarker>(File.ReadAllText(markerPath), JsonOptions);
            return payload?.Version == CurrentMarkerVersion
                   && HasRequiredModelFiles(definition, directoryPath);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasRequiredModelFiles(TranscriptionModelDefinition definition, string directoryPath)
    {
        return definition.Key switch
        {
            "phonon-2" => HasPhonon2ModelFiles(directoryPath),
            "qwen3-asr-1.7b" or "qwen3-asr-0.6b" => HasAllMatches(
                    directoryPath,
                    "config.json",
                    "generation_config.json",
                    "preprocessor_config.json",
                    "tokenizer_config.json",
                    "merges.txt",
                    "vocab.json")
                && HasCompleteSafetensors(directoryPath, allowConsolidated: false),
            "whisper-large-v3" => HasAllMatches(
                    directoryPath,
                    "config.json",
                    "preprocessor_config.json",
                    "tokenizer_config.json")
                && HasCompleteSafetensors(directoryPath, allowConsolidated: false),
            var key when key.StartsWith("crisperwhisper-2-", StringComparison.OrdinalIgnoreCase) => HasAllMatches(
                    directoryPath,
                    "config.json",
                    "generation_config.json",
                    "preprocessor_config.json",
                    "tokenizer_config.json",
                    "added_tokens.json")
                && HasCompleteSafetensors(directoryPath, allowConsolidated: false),
            TranscriptionModelCatalog.DefaultModelKey or "voxtral-small-24b" => HasAllMatches(
                    directoryPath,
                    "config.json",
                    "generation_config.json",
                    "params.json",
                    "tekken.json")
                && HasCompleteSafetensors(directoryPath, allowConsolidated: true),
            "voxtral-mini-4b-realtime" => HasAllMatches(
                    directoryPath,
                    "config.json",
                    "generation_config.json",
                    "processor_config.json",
                    "params.json",
                    "tekken.json")
                && HasCompleteSafetensors(directoryPath, allowConsolidated: true),
            _ => true,
        };
    }

    private static bool HasPhonon2ModelFiles(string directoryPath)
    {
        if (!HasAllMatches(directoryPath, "config.json", "packed_manifest.json"))
        {
            return false;
        }

        // Sizes from the pinned release distinguish complete artifacts from
        // interrupted downloads, including models imported from another cache.
        const long archiveBytes = 163515201;
        const long containerBytes = 177438361;
        static bool HasSize(string path, long expectedBytes) =>
            File.Exists(path) && new FileInfo(path).Length == expectedBytes;

        if (HasSize(Path.Combine(directoryPath, "phonon-2.bps.tar.zst"), archiveBytes)
            || HasSize(Path.Combine(directoryPath, "model.fermion"), containerBytes))
        {
            return true;
        }

        var unpackedDirectory = Path.Combine(directoryPath, "model_phonon2_c4c_int6");
        return Directory.Exists(unpackedDirectory)
               && HasAllMatches(unpackedDirectory, "config.json", "packed_manifest.json")
               && HasSize(Path.Combine(unpackedDirectory, "model.fermion"), containerBytes);
    }

    private static bool HasCompleteSafetensors(string directoryPath, bool allowConsolidated)
    {
        if (HasMatchingFile(directoryPath, "model.safetensors"))
        {
            return true;
        }

        if (allowConsolidated && HasMatchingFile(directoryPath, "consolidated.safetensors"))
        {
            return true;
        }

        return HasCompleteIndexedSafetensors(directoryPath);
    }

    private static bool HasCompleteIndexedSafetensors(string directoryPath)
    {
        foreach (var indexPath in Directory.EnumerateFiles(directoryPath, "model.safetensors.index.json", SearchOption.AllDirectories))
        {
            if (!TryReadIndexedSafetensorFiles(indexPath, out var requiredFiles) || requiredFiles.Count == 0)
            {
                continue;
            }

            var indexDirectory = Path.GetDirectoryName(indexPath);
            if (string.IsNullOrWhiteSpace(indexDirectory))
            {
                continue;
            }

            if (requiredFiles.All(relativePath =>
                    File.Exists(Path.Combine(indexDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)))))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadIndexedSafetensorFiles(string indexPath, out IReadOnlyCollection<string> requiredFiles)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        requiredFiles = files;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(indexPath));
            if (!document.RootElement.TryGetProperty("weight_map", out var weightMap)
                || weightMap.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var item in weightMap.EnumerateObject())
            {
                if (item.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var fileName = item.Value.GetString();
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    files.Add(fileName);
                }
            }

            return files.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasAllMatches(string directoryPath, params string[] patterns)
    {
        return patterns.All(pattern => HasMatchingFile(directoryPath, pattern));
    }

    private static bool HasMatchingFile(string directoryPath, string pattern)
    {
        foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(directoryPath, filePath).Replace('\\', '/');
            if (FileSystemName.MatchesSimpleExpression(pattern, relativePath, ignoreCase: true))
            {
                return true;
            }
        }

        return false;
    }

    private static long GetDirectorySizeBytes(string directoryPath)
    {
        return Directory
            .EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)
            .Sum(filePath => new FileInfo(filePath).Length);
    }

    private static void TryDeleteDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        Directory.Delete(directoryPath, recursive: true);
    }

    private static string FormatSize(long sizeBytes)
    {
        const double gib = 1024d * 1024d * 1024d;
        return $"{sizeBytes / gib:0.0} GB";
    }

    private sealed class ModelMarker
    {
        public string? RepoId { get; set; }

        public long? SizeBytes { get; set; }

        public int? Version { get; set; }
    }
}
