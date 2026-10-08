using System.IO;
using System.Text.Json;
using TranscriptionOverlay.Models;

namespace TranscriptionOverlay.Services;

public sealed class AppSettingsService
{
    private const string LegacySettingsFolderName = "TranscriptionOverlay";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _settingsPath;

    public AppSettingsService(AppEnvironment appEnvironment)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var legacySettingsPath = Path.Combine(localAppData, LegacySettingsFolderName, "settings.json");
        _settingsPath = appEnvironment.SettingsFilePath;

        if (File.Exists(_settingsPath) || !File.Exists(legacySettingsPath))
        {
            return;
        }

        try
        {
            File.Copy(legacySettingsPath, _settingsPath);
        }
        catch
        {
        }
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(_settingsPath, json);
        }
        catch
        {
        }
    }
}
