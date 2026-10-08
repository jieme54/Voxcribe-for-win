using NAudio.CoreAudioApi;
using NAudio.Wave;
using TranscriptionOverlay.Models;

namespace TranscriptionOverlay.Services;

public sealed class AudioSourceCatalog
{
    public const string DefaultInputId = "default-input";
    public const string SystemOutputId = "system-output";

    public IReadOnlyList<AudioSourceOption> GetAvailableSources(string languageCode)
    {
        using var enumerator = new MMDeviceEnumerator();
        var defaultCapture = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
        var defaultRender = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var options = new List<AudioSourceOption>
        {
            new(
                Id: DefaultInputId,
                DisplayName: $"{UiText.Translate(languageCode, "settings.audio.default_input")} ({defaultCapture.FriendlyName})",
                Kind: AudioSourceKind.DefaultInput,
                DeviceId: null),
        };

        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            options.Add(new AudioSourceOption(
                Id: $"capture:{device.ID}",
                DisplayName: device.FriendlyName,
                Kind: AudioSourceKind.InputDevice,
                DeviceId: device.ID));
        }

        options.Add(new AudioSourceOption(
            Id: SystemOutputId,
            DisplayName: $"{UiText.Translate(languageCode, "settings.audio.system_output")} ({defaultRender.FriendlyName})",
            Kind: AudioSourceKind.SystemOutput,
            DeviceId: null));

        return options;
    }

    public AudioSourceOption GetByIdOrDefault(string? sourceId, string languageCode)
    {
        var options = GetAvailableSources(languageCode);
        return options.FirstOrDefault(option => string.Equals(option.Id, sourceId, StringComparison.OrdinalIgnoreCase))
            ?? options.First();
    }

    public IWaveIn CreateCapture(AudioSourceOption source)
    {
        using var enumerator = new MMDeviceEnumerator();

        return source.Kind switch
        {
            AudioSourceKind.SystemOutput => new WasapiLoopbackCapture(
                enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)),
            AudioSourceKind.InputDevice when !string.IsNullOrWhiteSpace(source.DeviceId) => new WasapiCapture(
                enumerator.GetDevice(source.DeviceId)),
            _ => new WasapiCapture(enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia)),
        };
    }
}
