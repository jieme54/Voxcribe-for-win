using System.IO;

namespace TranscriptionOverlay.Services;

public sealed class AppEnvironment
{
    private const string ApplicationFolderName = "Voxcribe";

    public AppEnvironment(string appBaseDirectory)
    {
        RootDirectory = ResolveRootDirectory(appBaseDirectory);

        LocalAppDataDirectory = EnsureDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ApplicationFolderName));

        DataDirectory = EnsureDirectory(Path.Combine(LocalAppDataDirectory, "data"));
        ModelsDirectory = EnsureDirectory(Path.Combine(LocalAppDataDirectory, "models"));
        RuntimesDirectory = EnsureDirectory(Path.Combine(LocalAppDataDirectory, "runtimes"));
        MigrateLegacyPortableState();
    }

    public string RootDirectory { get; }

    public string LocalAppDataDirectory { get; }

    public string BackendDirectory => Path.Combine(RootDirectory, "backend");

    public string PortablePythonDirectory => Path.Combine(RootDirectory, "python-runtime");

    public string RuntimeOverlaysDirectory => Path.Combine(RootDirectory, "runtime-overlays");

    public string PortableVoxtralRealtimeOverlayDirectory => Path.Combine(RuntimeOverlaysDirectory, "voxtral-realtime");

    public string DataDirectory { get; }

    public string ModelsDirectory { get; }

    public string RuntimesDirectory { get; }

    public string SettingsFilePath => Path.Combine(DataDirectory, "settings.json");

    public string PortablePythonExecutablePath => ResolvePortablePythonExecutablePath(PortablePythonDirectory);

    public string GetManagedModelDirectory(string repoId)
    {
        return Path.Combine(ModelsDirectory, SanitizeRepoId(repoId));
    }

    private static string ResolveRootDirectory(string appBaseDirectory)
    {
        var current = new DirectoryInfo(appBaseDirectory);
        while (current is not null)
        {
            var workerPath = Path.Combine(current.FullName, "backend", "voxtral_worker.py");
            if (File.Exists(workerPath))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return appBaseDirectory;
    }

    private void MigrateLegacyPortableState()
    {
        MoveLegacyDirectoryIfNeeded(Path.Combine(RootDirectory, "data"), DataDirectory);
        MoveLegacyDirectoryIfNeeded(Path.Combine(RootDirectory, "models"), ModelsDirectory);
    }

    private static void MoveLegacyDirectoryIfNeeded(string sourceDirectory, string destinationDirectory)
    {
        if (!Directory.Exists(sourceDirectory) || AreSamePath(sourceDirectory, destinationDirectory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(destinationDirectory);
            foreach (var sourceItem in Directory.EnumerateFileSystemEntries(sourceDirectory))
            {
                var destinationPath = Path.Combine(destinationDirectory, Path.GetFileName(sourceItem));
                if (File.Exists(sourceItem))
                {
                    if (!File.Exists(destinationPath))
                    {
                        File.Move(sourceItem, destinationPath);
                    }
                }
                else if (Directory.Exists(sourceItem) && !Directory.Exists(destinationPath))
                {
                    Directory.Move(sourceItem, destinationPath);
                }
            }

            if (!Directory.EnumerateFileSystemEntries(sourceDirectory).Any())
            {
                Directory.Delete(sourceDirectory);
            }
        }
        catch
        {
        }
    }

    private static bool AreSamePath(string left, string right)
    {
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string EnsureDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string SanitizeRepoId(string repoId)
    {
        return repoId
            .Replace("/", "__", StringComparison.Ordinal)
            .Replace("\\", "__", StringComparison.Ordinal)
            .Replace(":", "_", StringComparison.Ordinal);
    }

    private static string ResolvePortablePythonExecutablePath(string pythonDirectory)
    {
        var candidates = new[]
        {
            Path.Combine(pythonDirectory, "python.exe"),
            Path.Combine(pythonDirectory, "python3.exe"),
            Path.Combine(pythonDirectory, "python3.14.exe"),
            Path.Combine(pythonDirectory, "python3.13.exe"),
            Path.Combine(pythonDirectory, "python3.12.exe"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return candidates[0];
    }
}
