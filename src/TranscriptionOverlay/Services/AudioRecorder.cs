using NAudio.Wave;
using System.IO;
using TranscriptionOverlay.Models;

namespace TranscriptionOverlay.Services;

public sealed class AudioRecorder : IDisposable
{
    private readonly object _syncRoot = new();
    private readonly AudioSourceCatalog _audioSourceCatalog = new();

    private IWaveIn? _capture;
    private WaveFileWriter? _writer;
    private MemoryStream? _bufferedAudio;
    private WaveFormat? _captureWaveFormat;
    private TaskCompletionSource<string?>? _stopTcs;
    private bool _keepFileAfterStop;
    private string? _currentFilePath;

    public Task<string> StartAsync(AudioSourceOption audioSource)
    {
        lock (_syncRoot)
        {
            if (_capture is not null)
            {
                throw new InvalidOperationException("Un enregistrement est déjà en cours.");
            }

            var tempRoot = Path.Combine(Path.GetTempPath(), "Voxcribe");
            Directory.CreateDirectory(tempRoot);

            _currentFilePath = Path.Combine(
                tempRoot,
                $"clip-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.wav");

            _capture = _audioSourceCatalog.CreateCapture(audioSource);
            _captureWaveFormat = _capture.WaveFormat;
            _writer = new WaveFileWriter(_currentFilePath, _capture.WaveFormat);
            _bufferedAudio = new MemoryStream(capacity: Math.Max(_capture.WaveFormat.AverageBytesPerSecond * 8, 4096));
            _stopTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _keepFileAfterStop = false;

            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;

            try
            {
                _capture.StartRecording();
            }
            catch
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
                _capture.Dispose();
                _writer.Dispose();
                _bufferedAudio?.Dispose();
                _capture = null;
                _captureWaveFormat = null;
                _writer = null;
                _bufferedAudio = null;
                _stopTcs = null;

                if (!string.IsNullOrWhiteSpace(_currentFilePath) && File.Exists(_currentFilePath))
                {
                    File.Delete(_currentFilePath);
                }

                _currentFilePath = null;
                throw;
            }

            return Task.FromResult(_currentFilePath);
        }
    }

    public Task<string?> StopAsync(bool keepFile)
    {
        IWaveIn? capture;
        Task<string?> stopTask;

        lock (_syncRoot)
        {
            if (_capture is null || _stopTcs is null)
            {
                return Task.FromResult<string?>(keepFile ? _currentFilePath : null);
            }

            _keepFileAfterStop = keepFile;
            capture = _capture;
            stopTask = _stopTcs.Task;
        }

        capture.StopRecording();
        return stopTask;
    }

    public RecordingSnapshot? CreateSnapshot(TimeSpan? trailingWindow = null)
    {
        lock (_syncRoot)
        {
            if (_capture is null || _captureWaveFormat is null || _bufferedAudio is null || _bufferedAudio.Length == 0)
            {
                return null;
            }

            var tempRoot = Path.Combine(Path.GetTempPath(), "Voxcribe");
            Directory.CreateDirectory(tempRoot);

            var snapshotPath = Path.Combine(
                tempRoot,
                $"live-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.wav");

            var bytes = _bufferedAudio.ToArray();
            if (trailingWindow is not null && trailingWindow.Value > TimeSpan.Zero && _captureWaveFormat.AverageBytesPerSecond > 0)
            {
                var desiredByteCount = (int)Math.Min(
                    bytes.Length,
                    trailingWindow.Value.TotalSeconds * _captureWaveFormat.AverageBytesPerSecond);
                var blockAlign = Math.Max(_captureWaveFormat.BlockAlign, 1);
                desiredByteCount -= desiredByteCount % blockAlign;

                if (desiredByteCount > 0 && bytes.Length > desiredByteCount)
                {
                    var startIndex = bytes.Length - desiredByteCount;
                    startIndex -= startIndex % blockAlign;

                    var trailingBytes = new byte[bytes.Length - startIndex];
                    Buffer.BlockCopy(bytes, startIndex, trailingBytes, 0, trailingBytes.Length);
                    bytes = trailingBytes;
                }
            }

            using var writer = new WaveFileWriter(snapshotPath, _captureWaveFormat);
            writer.Write(bytes, 0, bytes.Length);
            writer.Flush();

            var duration = _captureWaveFormat.AverageBytesPerSecond > 0
                ? TimeSpan.FromSeconds(bytes.Length / (double)_captureWaveFormat.AverageBytesPerSecond)
                : TimeSpan.Zero;

            return new RecordingSnapshot(snapshotPath, duration);
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_syncRoot)
        {
            _writer?.Write(e.Buffer, 0, e.BytesRecorded);
            _writer?.Flush();
            _bufferedAudio?.Write(e.Buffer, 0, e.BytesRecorded);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        string? filePath;
        TaskCompletionSource<string?>? completion;
        bool keepFile;

        lock (_syncRoot)
        {
            filePath = _currentFilePath;
            completion = _stopTcs;
            keepFile = _keepFileAfterStop;

            if (_capture is not null)
            {
                _capture.DataAvailable -= OnDataAvailable;
                _capture.RecordingStopped -= OnRecordingStopped;
                _capture.Dispose();
                _capture = null;
            }

            _writer?.Dispose();
            _bufferedAudio?.Dispose();
            _writer = null;
            _bufferedAudio = null;
            _captureWaveFormat = null;
            _stopTcs = null;
            _currentFilePath = keepFile ? filePath : null;
        }

        if (!keepFile && !string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
        {
            try
            {
                File.Delete(filePath);
            }
            catch
            {
            }
        }

        if (e.Exception is not null)
        {
            completion?.TrySetException(e.Exception);
            return;
        }

        completion?.TrySetResult(keepFile ? filePath : null);
    }

    public void Dispose()
    {
        try
        {
            StopAsync(keepFile: false).GetAwaiter().GetResult();
        }
        catch
        {
        }
    }
}

public readonly record struct RecordingSnapshot(string FilePath, TimeSpan Duration);
