using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using TranscriptionOverlay.Models;

namespace TranscriptionOverlay.Services;

public sealed class VoxtralWorkerClient : IAsyncDisposable
{
    private readonly SemaphoreSlim _processLock = new(1, 1);
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<WorkerMessage>> _pendingRequests = new();
    private readonly ConcurrentQueue<string> _recentStderr = new();
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly AppEnvironment _appEnvironment;
    private readonly PythonRuntimeLocator _pythonRuntimeLocator;

    private Process? _process;
    private StreamWriter? _stdin;
    private Task? _stdoutPump;
    private Task? _stderrPump;
    private TaskCompletionSource<bool>? _readyTcs;
    private string _modelKey = TranscriptionModelCatalog.DefaultModelKey;
    private string _modelRepoId = "mistralai/Voxtral-Mini-3B-2507";
    private string _modelKind = "voxtral";
    private string? _modelPath;
    private string _hardwareAccelerationDevice = HardwareAccelerationPreference.Auto;

    public VoxtralWorkerClient(AppEnvironment appEnvironment, PythonRuntimeLocator pythonRuntimeLocator)
    {
        _appEnvironment = appEnvironment;
        _pythonRuntimeLocator = pythonRuntimeLocator;
    }

    public event EventHandler<string>? StatusChanged;

    public string ModelKey => _modelKey;

    public void ConfigureModel(TranscriptionModelDefinition definition, string? modelPath)
    {
        _modelKey = definition.Key;
        _modelRepoId = definition.RepoId;
        _modelPath = modelPath;
        _modelKind = definition.RuntimeKind switch
        {
            ModelRuntimeKind.Whisper => "whisper",
            ModelRuntimeKind.CrisperWhisper => "crisperwhisper",
            ModelRuntimeKind.Qwen => "qwen",
            ModelRuntimeKind.Phonon => "phonon",
            _ => "voxtral",
        };
    }

    public void ConfigureHardwareAcceleration(string? hardwareAccelerationDevice)
    {
        _hardwareAccelerationDevice = HardwareAccelerationPreference.Normalize(hardwareAccelerationDevice);
    }

    public Task WarmupAsync(CancellationToken cancellationToken)
    {
        return EnsureProcessAsync(cancellationToken);
    }

    public async Task<string> TranscribeAsync(string audioPath, string? language, CancellationToken cancellationToken)
    {
        await EnsureProcessAsync(cancellationToken).ConfigureAwait(false);
        await _requestLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var requestId = Guid.NewGuid().ToString("N");
            var completion = new TaskCompletionSource<WorkerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingRequests[requestId] = completion;

            using var registration = cancellationToken.Register(
                () => completion.TrySetCanceled(cancellationToken));

            await SendAsync(
                new WorkerRequest("transcribe", requestId, audioPath, language),
                cancellationToken).ConfigureAwait(false);

            var message = await completion.Task.ConfigureAwait(false);
            if (!message.Success)
            {
                throw new InvalidOperationException(message.Error ?? message.Message ?? "Le worker a retourn\u00e9 une erreur.");
            }

            return message.Text ?? string.Empty;
        }
        finally
        {
            _requestLock.Release();
        }
    }

    public async Task ResetAsync()
    {
        await _processLock.WaitAsync().ConfigureAwait(false);

        try
        {
            await StopProcessAsync().ConfigureAwait(false);
        }
        finally
        {
            _processLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ResetAsync().ConfigureAwait(false);
        _processLock.Dispose();
        _requestLock.Dispose();
    }

    public void Abort()
    {
        var process = Interlocked.Exchange(ref _process, null);
        var stdin = Interlocked.Exchange(ref _stdin, null);
        var readyTcs = Interlocked.Exchange(ref _readyTcs, null);

        _stdoutPump = null;
        _stderrPump = null;

        readyTcs?.TrySetCanceled();

        if (stdin is not null)
        {
            try
            {
                stdin.Dispose();
            }
            catch
            {
            }
        }

        if (process is not null)
        {
            try
            {
                process.Exited -= Process_Exited;
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        FailPendingRequests(new OperationCanceledException("Le worker de transcription a \u00e9t\u00e9 interrompu."));
    }

    private async Task EnsureProcessAsync(CancellationToken cancellationToken)
    {
        if (IsReady())
        {
            return;
        }

        await _processLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (IsReady())
            {
                return;
            }

            await StopProcessAsync().ConfigureAwait(false);
            StartProcess();

            var readyTask = _readyTcs?.Task ?? Task.CompletedTask;
            await readyTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _processLock.Release();
        }
    }

    private bool IsReady()
    {
        return _process is { HasExited: false }
            && _stdin is not null
            && _readyTcs?.Task.IsCompletedSuccessfully == true;
    }

    private void StartProcess()
    {
        while (_recentStderr.TryDequeue(out _))
        {
        }

        var workerScript = Path.Combine(_appEnvironment.BackendDirectory, "voxtral_worker.py");
        if (!File.Exists(workerScript))
        {
            throw new FileNotFoundException($"Worker introuvable : {workerScript}");
        }

        var pythonPath = _pythonRuntimeLocator.ResolvePythonPath(_modelKey);
        if (!File.Exists(pythonPath))
        {
            var guidance = _pythonRuntimeLocator.HasPortableRuntime(_modelKey)
                ? "Le runtime Python portable requis par ce mod\u00e8le semble incomplet."
                : "Installez un runtime NVIDIA CUDA, AMD ROCm ou CPU depuis les param\u00e8tres, ou lancez scripts\\setup_backend.ps1 en environnement de developpement.";

            throw new FileNotFoundException(
                $"Python du backend introuvable : {pythonPath}. {guidance}");
        }

        var overlayPath = _pythonRuntimeLocator.ResolveOverlayPath(_modelKey);
        _readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var startInfo = new ProcessStartInfo
        {
            FileName = pythonPath,
            Arguments = $"\"{workerScript}\" --stdio",
            WorkingDirectory = _appEnvironment.BackendDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        startInfo.Environment["PYTHONUNBUFFERED"] = "1";
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
        startInfo.Environment["HF_HUB_DISABLE_PROGRESS_BARS"] = "1";
        startInfo.Environment["VOXSCRIBE_MODEL_KEY"] = _modelKey;
        startInfo.Environment["VOXSCRIBE_MODEL_KIND"] = _modelKind;
        startInfo.Environment["VOXSCRIBE_MODEL_REPO_ID"] = _modelRepoId;
        startInfo.Environment["VOXSCRIBE_HARDWARE_ACCELERATION"] = _hardwareAccelerationDevice;

        if (!string.IsNullOrWhiteSpace(_modelPath))
        {
            startInfo.Environment["VOXSCRIBE_MODEL_PATH"] = _modelPath;
        }

        var configuredLanguage = Environment.GetEnvironmentVariable("VOXSCRIBE_LANGUAGE");
        if (!string.IsNullOrWhiteSpace(configuredLanguage))
        {
            startInfo.Environment["VOXSCRIBE_LANGUAGE"] = configuredLanguage;
        }

        if (!string.IsNullOrWhiteSpace(overlayPath))
        {
            var inheritedPythonPath = Environment.GetEnvironmentVariable("PYTHONPATH");
            startInfo.Environment["PYTHONPATH"] = string.IsNullOrWhiteSpace(inheritedPythonPath)
                ? overlayPath
                : $"{overlayPath}{Path.PathSeparator}{inheritedPythonPath}";
        }

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        process.Exited += Process_Exited;

        if (!process.Start())
        {
            throw new InvalidOperationException("Le worker de transcription n'a pas pu d\u00e9marrer.");
        }

        _process = process;
        _stdin = process.StandardInput;
        _stdoutPump = Task.Run(() => ReadStdoutLoopAsync(process));
        _stderrPump = Task.Run(() => ReadStderrLoopAsync(process));
    }

    private async Task StopProcessAsync()
    {
        var process = _process;
        var stdin = _stdin;

        _process = null;
        _stdin = null;
        _readyTcs = null;
        _stdoutPump = null;
        _stderrPump = null;

        if (stdin is not null)
        {
            try
            {
                await stdin.DisposeAsync();
            }
            catch
            {
            }
        }

        if (process is not null)
        {
            try
            {
                process.Exited -= Process_Exited;

                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        FailPendingRequests(new OperationCanceledException("Le worker de transcription a \u00e9t\u00e9 r\u00e9initialis\u00e9."));
    }

    private async Task SendAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        if (_stdin is null)
        {
            throw new InvalidOperationException("Le worker de transcription n'est pas pr\u00eat.");
        }

        var payload = JsonSerializer.Serialize(request, _jsonOptions);
        await _stdin.WriteLineAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _stdin.FlushAsync().ConfigureAwait(false);
    }

    private async Task ReadStdoutLoopAsync(Process process)
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                WorkerMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<WorkerMessage>(line, _jsonOptions);
                }
                catch
                {
                    continue;
                }

                if (message is null)
                {
                    continue;
                }

                switch (message.Type)
                {
                    case "ready":
                        _readyTcs?.TrySetResult(true);
                        if (!string.IsNullOrWhiteSpace(message.Message))
                        {
                            StatusChanged?.Invoke(this, message.Message);
                        }

                        break;

                    case "status":
                        if (!string.IsNullOrWhiteSpace(message.Message))
                        {
                            StatusChanged?.Invoke(this, message.Message);
                        }

                        break;

                    case "transcription":
                        if (!string.IsNullOrWhiteSpace(message.RequestId)
                            && _pendingRequests.TryRemove(message.RequestId, out var completion))
                        {
                            completion.TrySetResult(message);
                        }

                        break;

                    case "error":
                        var error = new InvalidOperationException(message.Error ?? message.Message ?? "Erreur inconnue du worker.");
                        if (!string.IsNullOrWhiteSpace(message.RequestId)
                            && _pendingRequests.TryRemove(message.RequestId, out var pending))
                        {
                            pending.TrySetException(error);
                        }
                        else
                        {
                            _readyTcs?.TrySetException(error);
                            StatusChanged?.Invoke(this, error.Message);
                        }

                        break;
                }
            }

            var processExit = BuildProcessExitException();
            _readyTcs?.TrySetException(processExit);
            FailPendingRequests(processExit);
        }
        catch (Exception ex)
        {
            _readyTcs?.TrySetException(ex);
            FailPendingRequests(ex);
        }
    }

    private async Task ReadStderrLoopAsync(Process process)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                _recentStderr.Enqueue(line);
                while (_recentStderr.Count > 12 && _recentStderr.TryDequeue(out _))
                {
                }
            }
        }
        catch
        {
        }
    }

    private void Process_Exited(object? sender, EventArgs e)
    {
        var processExit = BuildProcessExitException();
        _readyTcs?.TrySetException(processExit);
        FailPendingRequests(processExit);
    }

    private Exception BuildProcessExitException()
    {
        var stderr = string.Join(Environment.NewLine, _recentStderr);
        if (!string.IsNullOrWhiteSpace(stderr))
        {
            return new InvalidOperationException($"Le worker de transcription s'est arr\u00eat\u00e9.{Environment.NewLine}{stderr}");
        }

        return new InvalidOperationException("Le worker de transcription s'est arr\u00eat\u00e9 de fa\u00e7on inattendue.");
    }

    private void FailPendingRequests(Exception exception)
    {
        foreach (var entry in _pendingRequests.ToArray())
        {
            if (_pendingRequests.TryRemove(entry.Key, out var completion))
            {
                completion.TrySetException(exception);
            }
        }
    }

    private sealed record WorkerRequest(string Type, string? RequestId, string? AudioPath, string? Language);

    private sealed class WorkerMessage
    {
        public string? Type { get; set; }

        public string? RequestId { get; set; }

        public bool Success { get; set; }

        public string? Text { get; set; }

        public string? Error { get; set; }

        public string? Message { get; set; }
    }
}
