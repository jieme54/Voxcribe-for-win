using TranscriptionOverlay.Models;

namespace TranscriptionOverlay.Services;

public sealed class ModelDownloadCoordinator
{
    private readonly ModelPackageManager _modelPackageManager;
    private readonly object _syncRoot = new();
    private string? _activeModelKey;
    private Task? _activeDownloadTask;

    public ModelDownloadCoordinator(ModelPackageManager modelPackageManager)
    {
        _modelPackageManager = modelPackageManager;
    }

    public event EventHandler? DownloadsChanged;

    public bool IsDownloading(string modelKey)
    {
        lock (_syncRoot)
        {
            return _activeDownloadTask is { IsCompleted: false }
                   && string.Equals(_activeModelKey, modelKey, StringComparison.OrdinalIgnoreCase);
        }
    }

    public bool HasActiveDownload()
    {
        lock (_syncRoot)
        {
            return _activeDownloadTask is { IsCompleted: false };
        }
    }

    public bool TryStartDownload(TranscriptionModelDefinition definition, out Task downloadTask)
    {
        if (_modelPackageManager.IsModelDownloaded(definition))
        {
            downloadTask = Task.CompletedTask;
            return true;
        }

        lock (_syncRoot)
        {
            if (_activeDownloadTask is { IsCompleted: false })
            {
                if (string.Equals(_activeModelKey, definition.Key, StringComparison.OrdinalIgnoreCase))
                {
                    downloadTask = _activeDownloadTask;
                    return true;
                }

                downloadTask = Task.CompletedTask;
                return false;
            }

            _activeModelKey = definition.Key;
            _activeDownloadTask = RunDownloadAsync(definition);
            downloadTask = _activeDownloadTask;
        }

        OnDownloadsChanged();
        return true;
    }

    private async Task RunDownloadAsync(TranscriptionModelDefinition definition)
    {
        try
        {
            await _modelPackageManager.DownloadAsync(definition, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_syncRoot)
            {
                if (string.Equals(_activeModelKey, definition.Key, StringComparison.OrdinalIgnoreCase))
                {
                    _activeModelKey = null;
                    _activeDownloadTask = null;
                }
            }

            OnDownloadsChanged();
        }
    }

    private void OnDownloadsChanged()
    {
        DownloadsChanged?.Invoke(this, EventArgs.Empty);
    }
}
