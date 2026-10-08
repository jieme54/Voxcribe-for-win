using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using TranscriptionOverlay.Models;
using TranscriptionOverlay.Services;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using Win32OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace TranscriptionOverlay;

public partial class MainWindow : Window
{
    private const string ApplicationName = "Voxcribe";
    private const string DefaultLanguageCode = "fr";
    private const string ReadyStateHex = "#16794B";
    private const string BusyStateHex = "#B88A1A";
    private const string ErrorStateHex = "#C53D16";
    private const int WmHotKey = 0x0312;
    private const int TranscriptionHotkeyId = 0x5342;
    private const double DefaultOverlayWidth = 452d;
    private const double DefaultOverlayHeight = 184d;
    private const double MinimumOverlayWidth = 224d;
    private const double MinimumOverlayHeight = 96d;
    private const double CompactTitleThreshold = 334d;
    private const double HideLanguageButtonThreshold = 242d;
    private const double HideImportButtonThreshold = 214d;
    private const double CompactButtonsThreshold = 332d;
    private const double SymbolButtonsThreshold = 286d;
    private const double HideStatusThreshold = 124d;
    private const double RealtimeSnapshotMinimumSeconds = 1.15d;
    private const double RealtimeSnapshotWindowSeconds = 45d;
    private const int RealtimePollingDelayMilliseconds = 950;
    private const int RealtimeInitialDelayMilliseconds = 700;

    private static readonly Drawing.Color ReadyStateColor = Drawing.ColorTranslator.FromHtml(ReadyStateHex);
    private static readonly Drawing.Color BusyStateColor = Drawing.ColorTranslator.FromHtml(BusyStateHex);
    private static readonly Drawing.Color ErrorStateColor = Drawing.ColorTranslator.FromHtml(ErrorStateHex);
    private readonly AudioRecorder _audioRecorder = new();
    private readonly AudioSourceCatalog _audioSourceCatalog = new();
    private readonly ForegroundWindowTracker _foregroundWindowTracker = new();
    private readonly ClipboardTyper _clipboardTyper = new();
    private readonly TrayIconBadgeRenderer _trayBadgeRenderer = new();
    private readonly AppEnvironment _appEnvironment;
    private readonly PythonRuntimeLocator _pythonRuntimeLocator;
    private readonly TranscriptionModelCatalog _modelCatalog;
    private readonly ModelPackageManager _modelPackageManager;
    private readonly ModelDownloadCoordinator _modelDownloadCoordinator;
    private readonly LocalModelSourceCatalog _modelSourceCatalog;
    private readonly BackendCapabilityService _backendCapabilityService;
    private readonly BackendRuntimeInstallerService _backendRuntimeInstallerService;
    private readonly VoxtralWorkerClient _workerClient;
    private readonly AppSettingsService _settingsService;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly RealtimeTranscriptAssembler _realtimeAssembler = new();

    private OverlayState _state = OverlayState.Idle;
    private CancellationTokenSource? _transcriptionCts;
    private CancellationTokenSource? _preloadCts;
    private string? _currentAudioPath;
    private bool _currentAudioPathIsTemporary;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Forms.ToolStripMenuItem? _trayOpenMenuItem;
    private Forms.ToolStripMenuItem? _trayCloseMenuItem;
    private Forms.ToolStripMenuItem? _trayPrimaryActionMenuItem;
    private Drawing.Icon? _trayBaseIcon;
    private Drawing.Icon? _trayDisplayIcon;
    private HwndSource? _hwndSource;
    private string _statusKey = "status.ready";
    private object[] _statusArguments = [];
    private AppSettings _settings = new();
    private bool _isModelReady;
    private bool _isPreloadingModel;
    private bool _suppressLanguagePickerClick;
    private IntPtr _trayInteractionWindow;
    private IntPtr _transcriptionTargetWindow;
    private string? _lastTranscriptText;
    private long _lastTranscriptRevision;
    private TranscriptWindow? _transcriptWindow;
    private readonly List<TranscriptWindow> _transcriptWindows = [];
    private TranscriptWindow? _activeRealtimeTranscriptWindow;
    private CancellationTokenSource? _realtimePollingCts;
    private Task? _realtimePollingTask;
    private string _realtimeInsertedText = string.Empty;
    private bool _currentTranscriptSessionInitialized;
    private bool _isMediaDropActive;
    private string _selectedLanguageCode = DefaultLanguageCode;
    private TranscriptionModelDefinition _selectedModelDefinition = new(
        TranscriptionModelCatalog.DefaultModelKey,
        "Voxtral Mini 3B",
        "mistralai/Voxtral-Mini-3B-2507",
        "9.35 GB",
        [],
        ModelRuntimeKind.Voxtral,
        true,
        false,
        true,
        true,
        true,
        false,
        string.Empty,
        string.Empty,
        "mistral",
        "Mistral",
        "voxtral-mini",
        "Voxtral Mini",
        "3B");

    public MainWindow()
    {
        InitializeComponent();

        _appEnvironment = new AppEnvironment(AppContext.BaseDirectory);
        _pythonRuntimeLocator = new PythonRuntimeLocator(_appEnvironment);
        _modelCatalog = new TranscriptionModelCatalog();
        _modelPackageManager = new ModelPackageManager(_appEnvironment, _pythonRuntimeLocator);
        _modelDownloadCoordinator = new ModelDownloadCoordinator(_modelPackageManager);
        _modelSourceCatalog = new LocalModelSourceCatalog(_modelCatalog, _modelPackageManager);
        _backendCapabilityService = new BackendCapabilityService(_appEnvironment, _pythonRuntimeLocator);
        _backendRuntimeInstallerService = new BackendRuntimeInstallerService(_appEnvironment);
        _settingsService = new AppSettingsService(_appEnvironment);
        _workerClient = new VoxtralWorkerClient(_appEnvironment, _pythonRuntimeLocator);
        _workerClient.StatusChanged += WorkerClient_StatusChanged;

        _settings = _settingsService.Load();
        _settings.PythonRuntimePath = _pythonRuntimeLocator.NormalizeManagedPythonPath(_settings.PythonRuntimePath);
        _pythonRuntimeLocator.ConfigureCustomPythonPath(_settings.PythonRuntimePath);
        _settings.HardwareAccelerationDevice = HardwareAccelerationPreference.Normalize(_settings.HardwareAccelerationDevice);
        _settings.AutomaticHardwareAccelerationDevice = NormalizeAutomaticHardwareAccelerationDevice(
            _settings.AutomaticHardwareAccelerationDevice);
        InitializeLanguagePicker();
        InitializeSelectedModel();
        _settingsService.Save(_settings);
        ConfigureWorkerModel();
        InitializeTrayIcon();

        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        SizeChanged += MainWindow_SizeChanged;

        MaxWidth = DefaultOverlayWidth;
        MaxHeight = DefaultOverlayHeight;
        MinWidth = MinimumOverlayWidth;
        MinHeight = MinimumOverlayHeight;

        _isPreloadingModel = true;
        SetState(OverlayState.Idle, "status.preloading");
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _hwndSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _hwndSource?.AddHook(WndProc);
        RegisterGlobalHotkey();
        _foregroundWindowTracker.Start(this);
        PositionWindow();
        ApplyResponsiveLayout();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _ = WarmupBackendStateAsync();
        await PreloadModelAsync();
    }

    private async Task WarmupBackendStateAsync()
    {
        var gpuDiagnosticsTask = _backendCapabilityService.DetectGpuDiagnosticsAsync(_lifetimeCts.Token);
        var capabilitiesTask = _backendCapabilityService.DetectAsync(_lifetimeCts.Token);

        try
        {
            await Task.WhenAll(gpuDiagnosticsTask, capabilitiesTask);
        }
        catch
        {
        }
    }

    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout();
    }

    private void WorkerClient_StatusChanged(object? sender, string message)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (_state == OverlayState.Transcribing)
            {
                SetStatus("status.transcribing_progress");
            }
        });
    }

    private void PositionWindow()
    {
        Left = SystemParameters.WorkArea.Right - Width - 28;
        Top = SystemParameters.WorkArea.Top + 28;
    }

    private async void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecutePrimaryActionAsync(restoreFromTray: false, invokedFromTray: false);
    }

    private async Task ExecutePrimaryActionAsync(bool restoreFromTray, bool invokedFromTray)
    {
        if (restoreFromTray)
        {
            RestoreFromTray();
        }

        switch (_state)
        {
            case OverlayState.Idle:
                if (_isModelReady)
                {
                    await StartRecordingAsync(invokedFromTray);
                }
                else if (!_isPreloadingModel)
                {
                    await PreloadModelAsync();
                }

                break;
            case OverlayState.Recording:
                await StopAndTranscribeAsync();
                break;
        }
    }

    private async void SecondaryButton_Click(object sender, RoutedEventArgs e)
    {
        switch (_state)
        {
            case OverlayState.Idle:
                Close();
                break;
            case OverlayState.Recording:
                await CancelRecordingAsync();
                break;
            case OverlayState.Transcribing:
                await CancelTranscriptionAsync();
                break;
        }
    }

    private async Task StartRecordingAsync(bool invokedFromTray)
    {
        try
        {
            var initialTargetWindow = ResolveInitialTargetWindow(invokedFromTray);
            if (initialTargetWindow != IntPtr.Zero)
            {
                _transcriptionTargetWindow = initialTargetWindow;
            }

            var audioSource = _audioSourceCatalog.GetByIdOrDefault(_settings.AudioSourceId, SelectedLanguageCode);
            _currentAudioPath = await _audioRecorder.StartAsync(audioSource);
            _currentAudioPathIsTemporary = true;
            BeginRealtimeSession();
            SetState(OverlayState.Recording, "status.recording");

            if (IsRealtimeModeSelected)
            {
                StartRealtimePolling();
            }
        }
        catch (Exception ex)
        {
            ResetRealtimeSession(discardHiddenWindow: true);
            SetState(OverlayState.Idle, "status.start_failed", ex.Message);
        }
    }

    private async Task StopAndTranscribeAsync()
    {
        try
        {
            _currentAudioPath = await _audioRecorder.StopAsync(keepFile: true);
            _currentAudioPathIsTemporary = true;
            await StopRealtimePollingAsync();

            if (string.IsNullOrWhiteSpace(_currentAudioPath))
            {
                SetState(OverlayState.Idle, "status.no_audio");
                return;
            }

            await TranscribeCurrentAudioAsync("status.preparing");
        }
        catch (OperationCanceledException)
        {
            SetState(OverlayState.Idle, "status.cancelled");
        }
        catch (Exception ex)
        {
            ResetRealtimeSession(discardHiddenWindow: false);
            SetState(OverlayState.Idle, "status.transcription_failed", ex.Message);
        }
        finally
        {
            CleanupAudioFile();
            DisposeTranscriptionCts();
            ClearTranscriptionTargetWindow();
        }
    }

    private async Task ImportMediaFileAsync(string mediaPath)
    {
        if (_state != OverlayState.Idle)
        {
            return;
        }

        if (!MediaFileImport.CanImport(mediaPath))
        {
            SetState(OverlayState.Idle, "status.import_media_unavailable");
            return;
        }

        if (!_isModelReady)
        {
            if (!_isPreloadingModel)
            {
                await PreloadModelAsync();
            }

            if (!_isModelReady)
            {
                return;
            }
        }

        _transcriptionTargetWindow = _foregroundWindowTracker.LastExternalWindow;
        _currentAudioPath = mediaPath;
        _currentAudioPathIsTemporary = false;

        try
        {
            await TranscribeCurrentAudioAsync("status.importing_media");
        }
        catch (OperationCanceledException)
        {
            SetState(OverlayState.Idle, "status.cancelled");
        }
        catch (Exception ex)
        {
            SetState(OverlayState.Idle, "status.import_media_failed", ex.Message);
        }
        finally
        {
            CleanupAudioFile();
            DisposeTranscriptionCts();
            ClearTranscriptionTargetWindow();
        }
    }

    private async Task TranscribeCurrentAudioAsync(string initialStatusKey)
    {
        _transcriptionCts = new CancellationTokenSource();
        var transcriptionToken = _transcriptionCts.Token;
        SetState(OverlayState.Transcribing, initialStatusKey);

        var transcript = await _workerClient.TranscribeAsync(
            _currentAudioPath!,
            language: null,
            transcriptionToken);

        if (string.IsNullOrWhiteSpace(transcript))
        {
            SetState(OverlayState.Idle, "status.no_text");
            return;
        }

        await HandleCompletedTranscriptAsync(transcript, transcriptionToken);
    }

    private async Task CancelRecordingAsync()
    {
        try
        {
            await _audioRecorder.StopAsync(keepFile: false);
        }
        catch
        {
        }

        CleanupAudioFile();
        await StopRealtimePollingAsync();
        ResetRealtimeSession(discardHiddenWindow: true);
        ClearTranscriptionTargetWindow();
        SetState(OverlayState.Idle, "status.recording_cancelled");
    }

    private async Task CancelTranscriptionAsync()
    {
        _transcriptionCts?.Cancel();
        CancelCurrentPreload();
        await StopRealtimePollingAsync();

        try
        {
            await _workerClient.ResetAsync();
        }
        catch
        {
        }

        CleanupAudioFile();
        DisposeTranscriptionCts();
        ClearTranscriptionTargetWindow();
        ResetRealtimeSession(discardHiddenWindow: false);
        _isModelReady = false;
        ConfigureWorkerModel();
        await PreloadModelAsync(statusKeyOnSuccess: "status.transcription_cancelled");
    }

    private void SetState(OverlayState state, string statusKey, params object[] args)
    {
        _state = state;
        SetStatus(statusKey, args);
        LanguagePickerButton.IsEnabled = state != OverlayState.Transcribing;
        SettingsButton.IsEnabled = state == OverlayState.Idle;

        if (!LanguagePickerButton.IsEnabled)
        {
            LanguagePickerButton.IsChecked = false;
        }

        switch (state)
        {
            case OverlayState.Idle:
                ApplyIdleVisualState();
                SecondaryButton.IsEnabled = true;
                break;

            case OverlayState.Recording:
                StateDot.Fill = CreateBrush(ErrorStateHex);
                PrimaryButton.IsEnabled = true;
                SecondaryButton.IsEnabled = true;
                break;

            case OverlayState.Transcribing:
                StateDot.Fill = CreateBrush(BusyStateHex);
                PrimaryButton.IsEnabled = false;
                SecondaryButton.IsEnabled = true;
                break;
        }

        UpdateActionButtonPresentation();
        UpdateTrayMenuState();
        UpdateTrayIconAppearance();
        UpdateAuxiliaryButtons();
        UpdateDropHintVisibility();
    }

    private async Task PreloadModelAsync(string statusKeyOnSuccess = "status.model_ready")
    {
        if (_state == OverlayState.Recording)
        {
            return;
        }

        var selectedSource = _modelSourceCatalog.ResolveSelectedSource(_selectedModelDefinition, _settings, SelectedLanguageCode);
        if (selectedSource is null)
        {
            CancelCurrentPreload();
            _isPreloadingModel = false;
            _isModelReady = false;
            SetState(OverlayState.Idle, "status.model_source_required");
            return;
        }

        var preloadCts = ReplacePreloadCancellationSource();
        _isPreloadingModel = true;
        _isModelReady = false;
        SetState(OverlayState.Idle, "status.preloading");

        try
        {
            ConfigureWorkerModel();
            await _workerClient.WarmupAsync(preloadCts.Token);

            if (!ReferenceEquals(_preloadCts, preloadCts))
            {
                return;
            }

            _isPreloadingModel = false;
            _isModelReady = true;
            SetState(OverlayState.Idle, statusKeyOnSuccess);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested || preloadCts.IsCancellationRequested)
        {
            if (ReferenceEquals(_preloadCts, preloadCts))
            {
                _isPreloadingModel = false;
            }
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_preloadCts, preloadCts))
            {
                return;
            }

            _isPreloadingModel = false;
            _isModelReady = false;
            SetState(OverlayState.Idle, "status.preload_failed", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_preloadCts, preloadCts))
            {
                _preloadCts = null;
            }

            preloadCts.Dispose();
        }
    }

    private CancellationTokenSource ReplacePreloadCancellationSource()
    {
        var next = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        var previous = Interlocked.Exchange(ref _preloadCts, next);

        if (previous is not null)
        {
            try
            {
                previous.Cancel();
            }
            catch
            {
            }

            previous.Dispose();
        }

        return next;
    }

    private void CancelCurrentPreload()
    {
        var current = Interlocked.Exchange(ref _preloadCts, null);
        if (current is null)
        {
            return;
        }

        try
        {
            current.Cancel();
        }
        catch
        {
        }

        current.Dispose();
        _isPreloadingModel = false;
    }

    private static System.Windows.Media.Brush CreateBrush(string hex)
    {
        return (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
    }

    private void CleanupAudioFile()
    {
        if (_currentAudioPathIsTemporary && !string.IsNullOrWhiteSpace(_currentAudioPath) && File.Exists(_currentAudioPath))
        {
            try
            {
                File.Delete(_currentAudioPath);
            }
            catch
            {
            }
        }

        _currentAudioPath = null;
        _currentAudioPathIsTemporary = false;
    }

    private string SelectedLanguageCode => _selectedLanguageCode;

    private bool IsRealtimeModeSelected =>
        _settings.PreferredTranscriptionMode == TranscriptionMode.Realtime
        && _selectedModelDefinition.SupportsRealtimeTranscription;

    private bool RouteRealtimeTranscriptToWindow => IsRealtimeModeSelected;

    private void InitializeLanguagePicker()
    {
        _selectedLanguageCode = NormalizeLanguageCode(_settings.LanguageCode);
        _settings.LanguageCode = _selectedLanguageCode;
        UpdateLanguagePickerSelection();
        UpdateLanguagePickerToolTip();
    }

    private void InitializeSelectedModel()
    {
        _selectedModelDefinition = _modelCatalog.GetByKeyOrDefault(_settings.SelectedModelKey);
        if (!_selectedModelDefinition.SupportsCurrentOperatingSystem())
        {
            _selectedModelDefinition = _modelCatalog.GetDefaultForCurrentOperatingSystem();
        }

        _settings.SelectedModelKey = _selectedModelDefinition.Key;
        if (!_settings.PinSelectedModelSource
            && _settings.SelectedModelSourceId.StartsWith("shared:", StringComparison.OrdinalIgnoreCase))
        {
            _settings.SelectedModelSourceId = string.Empty;
        }

        _settings.SelectedModelSourceId = _modelSourceCatalog
            .ResolveSelectedSource(_selectedModelDefinition, _settings, SelectedLanguageCode)
            ?.Id
            ?? string.Empty;
        NormalizePreferredTranscriptionMode();
    }

    private void ConfigureWorkerModel()
    {
        NormalizePreferredTranscriptionMode();
        var source = _modelSourceCatalog.ResolveSelectedSource(_selectedModelDefinition, _settings, SelectedLanguageCode);
        _settings.SelectedModelSourceId = source?.Id ?? string.Empty;
        var workerHardwareAccelerationDevice = _settings.HardwareAccelerationDevice;
        if (string.Equals(
                workerHardwareAccelerationDevice,
                HardwareAccelerationPreference.Auto,
                StringComparison.OrdinalIgnoreCase)
            && _backendRuntimeInstallerService.IsInstalledNvidiaCudaRuntimePath(_settings.PythonRuntimePath)
            && HardwareAccelerationPreference.TryParseGpuIndex(
                _settings.AutomaticHardwareAccelerationDevice,
                out _))
        {
            workerHardwareAccelerationDevice = _settings.AutomaticHardwareAccelerationDevice;
        }

        _workerClient.ConfigureHardwareAcceleration(workerHardwareAccelerationDevice);
        _workerClient.ConfigureModel(
            _selectedModelDefinition,
            source?.DirectoryPath);
    }

    private void NormalizePreferredTranscriptionMode()
    {
        var normalized = _settings.GetPreferredModeForModel(_selectedModelDefinition.Key) ?? _settings.PreferredTranscriptionMode;
        if (!_selectedModelDefinition.SupportsMode(normalized))
        {
            normalized = _selectedModelDefinition.SupportsRealtimeTranscription
            ? TranscriptionMode.Realtime
            : TranscriptionMode.File;
        }

        _settings.PreferredTranscriptionMode = normalized;
        _settings.SetPreferredModeForModel(_selectedModelDefinition.Key, normalized);
    }

    private static string NormalizeAutomaticHardwareAccelerationDevice(string? value)
    {
        var normalized = HardwareAccelerationPreference.Normalize(value);
        return HardwareAccelerationPreference.TryParseGpuIndex(normalized, out _)
            ? normalized
            : string.Empty;
    }

    private static string NormalizeLanguageCode(string? languageCode)
    {
        return string.Equals(languageCode, "en", StringComparison.OrdinalIgnoreCase)
            ? "en"
            : DefaultLanguageCode;
    }

    private void ApplyInterfaceLanguage(string languageCode, bool persist)
    {
        var normalizedLanguage = NormalizeLanguageCode(languageCode);
        _selectedLanguageCode = normalizedLanguage;
        _settings.LanguageCode = normalizedLanguage;

        if (persist)
        {
            _settingsService.Save(_settings);
        }

        UpdateLanguagePickerSelection();
        ApplyLocalizedText();
    }

    private void UpdateLanguagePickerSelection()
    {
        var isEnglish = SelectedLanguageCode == "en";
        SelectedFrenchFlag.Visibility = isEnglish ? Visibility.Collapsed : Visibility.Visible;
        SelectedEnglishFlag.Visibility = isEnglish ? Visibility.Visible : Visibility.Collapsed;
        FrenchLanguageButton.IsEnabled = isEnglish;
        EnglishLanguageButton.IsEnabled = !isEnglish;
    }

    private void LanguageOptionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string languageCode })
        {
            return;
        }

        ApplyInterfaceLanguage(languageCode, persist: true);
        LanguagePickerButton.IsChecked = false;
        LanguagePickerPopup.IsOpen = false;
    }

    private void LanguagePickerPopup_Closed(object? sender, EventArgs e)
    {
        LanguagePickerButton.IsChecked = false;
    }

    private void LanguagePickerButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!LanguagePickerPopup.IsOpen)
        {
            return;
        }

        _suppressLanguagePickerClick = true;
        LanguagePickerPopup.IsOpen = false;
        LanguagePickerButton.IsChecked = false;
        e.Handled = true;
    }

    private void LanguagePickerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressLanguagePickerClick)
        {
            _suppressLanguagePickerClick = false;
            return;
        }

        if (!LanguagePickerButton.IsEnabled)
        {
            LanguagePickerButton.IsChecked = false;
            LanguagePickerPopup.IsOpen = false;
            return;
        }

        var shouldOpen = LanguagePickerButton.IsChecked == true;
        LanguagePickerPopup.IsOpen = shouldOpen;
    }

    private void UpdateLanguagePickerToolTip()
    {
        LanguagePickerButton.ToolTip = SelectedLanguageCode switch
        {
            "en" => Translate("tooltip.lang.en"),
            _ => Translate("tooltip.lang.fr"),
        };
    }

    private void InitializeTrayIcon()
    {
        _trayMenu = new Forms.ContextMenuStrip();
        _trayOpenMenuItem = new Forms.ToolStripMenuItem();
        _trayOpenMenuItem.Click += (_, _) => Dispatcher.Invoke(() =>
        {
            _trayMenu?.Close();
            RestoreFromTray();
        });
        _trayCloseMenuItem = new Forms.ToolStripMenuItem();
        _trayCloseMenuItem.Click += (_, _) => Dispatcher.Invoke(() =>
        {
            _trayMenu?.Close();
            ExitApplication();
        });
        _trayPrimaryActionMenuItem = new Forms.ToolStripMenuItem();
        _trayPrimaryActionMenuItem.Click += (_, _) =>
            Dispatcher.InvokeAsync(() =>
            {
                _trayMenu?.Close();
                _ = ExecutePrimaryActionAsync(restoreFromTray: false, invokedFromTray: true);
            });

        _trayMenu.Items.Add(_trayOpenMenuItem);
        _trayMenu.Items.Add(_trayCloseMenuItem);
        _trayMenu.Items.Add(new Forms.ToolStripSeparator());
        _trayMenu.Items.Add(_trayPrimaryActionMenuItem);

        _trayBaseIcon = ResolveTrayBaseIcon();

        _trayIcon = new Forms.NotifyIcon
        {
            Text = ApplicationName,
            Icon = _trayBaseIcon,
            Visible = false,
            ContextMenuStrip = _trayMenu,
        };

        _trayIcon.MouseUp += TrayIcon_MouseUp;
        ApplyLocalizedText();
    }

    private void TrayIcon_MouseUp(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button != Forms.MouseButtons.Left || _trayMenu is null || _trayIcon?.Visible != true)
        {
            return;
        }

        if (_trayMenu.Visible)
        {
            _trayMenu.Close();
            return;
        }

        _trayInteractionWindow = _foregroundWindowTracker.LastExternalWindow;
        ShowTrayMenu();
    }

    private void ShowTrayMenu()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            Win32Native.SetForegroundWindow(handle);
        }

        _trayMenu?.Show(Forms.Control.MousePosition);
    }

    private static Drawing.Icon ResolveTrayBaseIcon()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            using var extractedIcon = Drawing.Icon.ExtractAssociatedIcon(processPath);
            if (extractedIcon is not null)
            {
                return (Drawing.Icon)extractedIcon.Clone();
            }
        }

        return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state != OverlayState.Idle)
        {
            return;
        }

        var settingsWindow = new SettingsWindow(
            _settings,
            _settingsService,
            _appEnvironment,
            _modelCatalog,
            _modelPackageManager,
            _modelDownloadCoordinator,
            _modelSourceCatalog,
            _backendCapabilityService,
            _backendRuntimeInstallerService,
            _audioSourceCatalog,
            _pythonRuntimeLocator)
        {
            Owner = this,
        };

        settingsWindow.InterfaceLanguageChanged += SettingsWindow_InterfaceLanguageChanged;
        settingsWindow.ModelSelectionChanged += SettingsWindow_ModelSelectionChanged;
        settingsWindow.HotkeyChanged += SettingsWindow_HotkeyChanged;
        settingsWindow.HardwareAccelerationChanged += SettingsWindow_HardwareAccelerationChanged;
        settingsWindow.PythonRuntimeChanged += SettingsWindow_PythonRuntimeChanged;
        settingsWindow.ShowDialog();
        settingsWindow.InterfaceLanguageChanged -= SettingsWindow_InterfaceLanguageChanged;
        settingsWindow.ModelSelectionChanged -= SettingsWindow_ModelSelectionChanged;
        settingsWindow.HotkeyChanged -= SettingsWindow_HotkeyChanged;
        settingsWindow.HardwareAccelerationChanged -= SettingsWindow_HardwareAccelerationChanged;
        settingsWindow.PythonRuntimeChanged -= SettingsWindow_PythonRuntimeChanged;
        _transcriptWindow?.ApplyLanguage(SelectedLanguageCode);
        UpdateAuxiliaryButtons();
    }

    private void TranscriptButton_Click(object sender, RoutedEventArgs e)
    {
        ShowLatestTranscriptWindow(activate: true);
    }

    private async void ImportMediaButton_Click(object sender, RoutedEventArgs e)
    {
        if (_state != OverlayState.Idle)
        {
            return;
        }

        var targetWindow = _foregroundWindowTracker.LastExternalWindow;
        var dialog = new Win32OpenFileDialog
        {
            Title = Translate("button.import_media"),
            Filter = MediaFileImport.GetDialogFilter(SelectedLanguageCode),
            Multiselect = false,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _transcriptionTargetWindow = targetWindow;
        await ImportMediaFileAsync(dialog.FileName);
    }

    private void SettingsWindow_InterfaceLanguageChanged(object? sender, string languageCode)
    {
        ApplyInterfaceLanguage(languageCode, persist: false);
        ConfigureWorkerModel();

        if (_state == OverlayState.Idle)
        {
            ApplyIdleVisualState();
            UpdateTrayMenuState();
            UpdateTrayIconAppearance();
        }
    }

    private async void SettingsWindow_ModelSelectionChanged(object? sender, ModelSelectionChange change)
    {
        await SwitchModelAsync(change);
    }

    private void SettingsWindow_HotkeyChanged(object? sender, string hotkey)
    {
        _settings.TranscriptionHotkey = hotkey;
        _settingsService.Save(_settings);
        RegisterGlobalHotkey();
    }

    private async void SettingsWindow_HardwareAccelerationChanged(object? sender, EventArgs e)
    {
        await SwitchBackendRuntimeAsync("status.hardware_acceleration_switching");
    }

    private async void SettingsWindow_PythonRuntimeChanged(object? sender, EventArgs e)
    {
        await SwitchBackendRuntimeAsync("status.runtime_switching");
    }

    private async Task SwitchModelAsync(ModelSelectionChange change)
    {
        var definition = _modelCatalog.GetByKeyOrDefault(change.ModelKey);
        _selectedModelDefinition = definition;
        _settings.SelectedModelKey = definition.Key;
        _settings.SelectedModelSourceId = change.SourceId ?? string.Empty;
        _settings.SelectedModelSourceId = _modelSourceCatalog
            .ResolveSelectedSource(definition, _settings, SelectedLanguageCode)
            ?.Id
            ?? string.Empty;
        NormalizePreferredTranscriptionMode();
        _settingsService.Save(_settings);
        _isModelReady = false;
        SetState(OverlayState.Idle, "status.model_switching");
        CancelCurrentPreload();

        try
        {
            await _workerClient.ResetAsync();
        }
        catch
        {
        }

        ConfigureWorkerModel();
        await PreloadModelAsync();
    }

    private async Task SwitchBackendRuntimeAsync(string statusKey)
    {
        _settings.HardwareAccelerationDevice = HardwareAccelerationPreference.Normalize(_settings.HardwareAccelerationDevice);
        _settingsService.Save(_settings);
        _isModelReady = false;
        SetState(OverlayState.Idle, statusKey);
        CancelCurrentPreload();

        try
        {
            await _workerClient.ResetAsync();
        }
        catch
        {
        }

        ConfigureWorkerModel();
        await PreloadModelAsync();
    }

    private void TrayButton_Click(object sender, RoutedEventArgs e)
    {
        MinimizeToTray();
    }

    private void MinimizeToTray()
    {
        if (_trayIcon is null)
        {
            return;
        }

        _trayIcon.Visible = true;
        ShowInTaskbar = false;
        Hide();
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        Show();
        Activate();

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
        }
    }

    private void ExitApplication()
    {
        Close();
    }

    private void DisposeTranscriptionCts()
    {
        _transcriptionCts?.Dispose();
        _transcriptionCts = null;
    }

    private void SetStatus(string statusKey, params object[] args)
    {
        _statusKey = statusKey;
        _statusArguments = args;
        StatusTextBlock.Text = Translate(statusKey, args);
    }

    private void ApplyLocalizedText()
    {
        Title = Translate("title");
        TrayButton.ToolTip = Translate("tooltip.tray");
        SettingsButton.ToolTip = Translate("tooltip.settings");
        TranscriptButton.ToolTip = Translate("tooltip.show_transcript");
        ImportMediaButton.ToolTip = Translate("tooltip.import_media");
        DropHintTextBlock.Text = Translate("overlay.drop_media");
        UpdateLanguagePickerSelection();
        UpdateLanguagePickerToolTip();
        _transcriptWindow?.ApplyLanguage(SelectedLanguageCode);
        foreach (var transcriptWindow in _transcriptWindows.ToArray())
        {
            transcriptWindow.ApplyLanguage(SelectedLanguageCode);
        }

        if (_trayOpenMenuItem is not null)
        {
            _trayOpenMenuItem.Text = Translate("tray.open");
        }

        if (_trayCloseMenuItem is not null)
        {
            _trayCloseMenuItem.Text = Translate("tray.close");
        }

        StatusTextBlock.Text = Translate(_statusKey, _statusArguments);
        SetState(_state, _statusKey, _statusArguments);
        ApplyResponsiveLayout();
    }

    private void UpdateAuxiliaryButtons()
    {
        TranscriptButton.IsEnabled = !string.IsNullOrWhiteSpace(_lastTranscriptText);
        ImportMediaButton.IsEnabled = _state == OverlayState.Idle && !_isPreloadingModel;
    }

    private void UpdateTrayMenuState()
    {
        if (_trayPrimaryActionMenuItem is null)
        {
            return;
        }

        _trayPrimaryActionMenuItem.Text = GetResponsivePrimaryButtonContent(useSymbolButtons: false);
        _trayPrimaryActionMenuItem.Enabled = PrimaryButton.IsEnabled;
    }

    private void UpdateTrayIconAppearance()
    {
        if (_trayIcon is null || _trayBaseIcon is null)
        {
            return;
        }

        var nextIcon = _trayBadgeRenderer.CreateIcon(_trayBaseIcon, GetTrayBadgeColor());
        var previousIcon = _trayDisplayIcon;

        _trayDisplayIcon = nextIcon;
        _trayIcon.Icon = nextIcon;
        _trayIcon.Text = BuildTrayToolTipText();

        previousIcon?.Dispose();
    }

    private Drawing.Color GetTrayBadgeColor()
    {
        if (_state == OverlayState.Recording)
        {
            return ErrorStateColor;
        }

        if (_state == OverlayState.Transcribing || _isPreloadingModel)
        {
            return BusyStateColor;
        }

        return _isModelReady
            ? ReadyStateColor
            : ErrorStateColor;
    }

    private void ApplyIdleVisualState()
    {
        if (_isPreloadingModel)
        {
            StateDot.Fill = CreateBrush(BusyStateHex);
            PrimaryButton.IsEnabled = false;
            return;
        }

        if (_isModelReady)
        {
            StateDot.Fill = CreateBrush(ReadyStateHex);
            PrimaryButton.IsEnabled = true;
            return;
        }

        StateDot.Fill = CreateBrush(ErrorStateHex);
        PrimaryButton.IsEnabled = true;
    }

    private void ApplyResponsiveLayout()
    {
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;

        var useCompactTitle = width < CompactTitleThreshold;
        TitleTextBlock.Text = Translate("title");
        TitleTextBlock.Visibility = useCompactTitle ? Visibility.Collapsed : Visibility.Visible;
        TitleLogoBorder.Visibility = useCompactTitle ? Visibility.Visible : Visibility.Collapsed;
        TitleTextBlock.FontSize = 24d;
        ImportMediaButton.Visibility = width < HideImportButtonThreshold ? Visibility.Collapsed : Visibility.Visible;
        LanguagePickerButton.Visibility = width < HideLanguageButtonThreshold ? Visibility.Collapsed : Visibility.Visible;
        StatusTextBlock.Visibility = height < HideStatusThreshold ? Visibility.Collapsed : Visibility.Visible;
        StatusTextBlock.FontSize = 14d;
        StatusTextBlock.Margin = new Thickness(0, 10, 0, 12);
        DropHintIconTextBlock.Visibility = height < 118d
            ? Visibility.Collapsed
            : Visibility.Visible;
        DropHintTextBlock.FontSize = width < 300d || height < 124d ? 12d : 14d;

        if (LanguagePickerButton.Visibility != Visibility.Visible)
        {
            LanguagePickerButton.IsChecked = false;
            LanguagePickerPopup.IsOpen = false;
        }

        UpdateActionButtonPresentation();
    }

    private void UpdateActionButtonPresentation()
    {
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var useSymbolButtons = width < SymbolButtonsThreshold;
        PrimaryButton.FontSize = 16d;
        SecondaryButton.FontSize = 16d;
        PrimaryButton.Padding = new Thickness(10);
        SecondaryButton.Padding = new Thickness(10);
        PrimaryButton.Content = GetResponsivePrimaryButtonContent(useSymbolButtons);
        SecondaryButton.Content = GetResponsiveSecondaryButtonContent(useSymbolButtons);
    }

    private string GetResponsivePrimaryButtonContent(bool useSymbolButtons)
    {
        if (_state == OverlayState.Recording)
        {
            return useSymbolButtons ? "\u25A0" : Translate("button.finish");
        }

        if (_state == OverlayState.Transcribing)
        {
            return useSymbolButtons ? "\u2026" : Translate("button.transcribing");
        }

        if (_isPreloadingModel)
        {
            return useSymbolButtons ? "\u2026" : Translate("button.loading");
        }

        if (_isModelReady)
        {
            return useSymbolButtons ? "\u25B6" : Translate("button.start");
        }

        return useSymbolButtons ? "\u21BB" : Translate("button.retry");
    }

    private string GetResponsiveSecondaryButtonContent(bool useSymbolButtons)
    {
        if (_state == OverlayState.Idle)
        {
            return useSymbolButtons ? "\u2715" : Translate("button.close");
        }

        return useSymbolButtons ? "\u2715" : Translate("button.cancel");
    }

    private string GetPrimaryButtonContent(bool useSymbolButtons)
    {
        if (_state == OverlayState.Recording)
        {
            return useSymbolButtons ? "■" : Translate("button.finish");
        }

        if (_state == OverlayState.Transcribing)
        {
            return useSymbolButtons ? "…" : Translate("button.transcribing");
        }

        if (_isPreloadingModel)
        {
            return useSymbolButtons ? "…" : Translate("button.loading");
        }

        if (_isModelReady)
        {
            return useSymbolButtons ? "▶" : Translate("button.start");
        }

        return useSymbolButtons ? "↻" : Translate("button.retry");
    }

    private string GetSecondaryButtonContent(bool useSymbolButtons)
    {
        if (_state == OverlayState.Idle)
        {
            return useSymbolButtons ? "✕" : Translate("button.close");
        }

        return useSymbolButtons ? "✕" : Translate("button.cancel");
    }

    private string BuildTrayToolTipText()
    {
        var status = Translate(_statusKey, _statusArguments).Replace(Environment.NewLine, " ").Trim();
        var text = $"{ApplicationName} - {status}";

        if (text.Length <= 63)
        {
            return text;
        }

        return $"{text[..60]}...";
    }

    private IntPtr ResolveInitialTargetWindow(bool invokedFromTray)
    {
        return invokedFromTray ? _trayInteractionWindow : _foregroundWindowTracker.LastExternalWindow;
    }

    private void BeginRealtimeSession()
    {
        _realtimeAssembler.Reset();
        _realtimeInsertedText = string.Empty;
        _activeRealtimeTranscriptWindow = null;
        _currentTranscriptSessionInitialized = false;

        if (!IsRealtimeModeSelected)
        {
            return;
        }

        EnsureTranscriptSessionInitialized();
        _lastTranscriptText = string.Empty;
        EnsureCurrentTranscriptWindow(show: _settings.AutoOpenTranscriptWindow, activate: false);
        UpdateCurrentTranscriptWindows(string.Empty);
        UpdateAuxiliaryButtons();
    }

    private void ResetRealtimeSession(bool discardHiddenWindow)
    {
        if (discardHiddenWindow
            && _activeRealtimeTranscriptWindow is not null
            && !_activeRealtimeTranscriptWindow.IsVisible)
        {
            _activeRealtimeTranscriptWindow.Close();
        }

        _realtimeAssembler.Reset();
        _realtimeInsertedText = string.Empty;
        _activeRealtimeTranscriptWindow = null;
        _currentTranscriptSessionInitialized = false;
    }

    private void StartRealtimePolling()
    {
        _realtimePollingCts?.Cancel();
        _realtimePollingCts?.Dispose();

        _realtimePollingCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _realtimePollingTask = RunRealtimePollingLoopAsync(_realtimePollingCts.Token);
    }

    private async Task StopRealtimePollingAsync()
    {
        var cts = _realtimePollingCts;
        var task = _realtimePollingTask;
        _realtimePollingCts = null;
        _realtimePollingTask = null;

        if (cts is not null)
        {
            try
            {
                cts.Cancel();
            }
            catch
            {
            }

            cts.Dispose();
        }

        if (task is null)
        {
            return;
        }

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private async Task RunRealtimePollingLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(RealtimeInitialDelayMilliseconds, cancellationToken);

            while (!cancellationToken.IsCancellationRequested && _state == OverlayState.Recording)
            {
                var snapshot = _audioRecorder.CreateSnapshot(TimeSpan.FromSeconds(RealtimeSnapshotWindowSeconds));
                if (snapshot is null || snapshot.Value.Duration.TotalSeconds < RealtimeSnapshotMinimumSeconds)
                {
                    await Task.Delay(RealtimePollingDelayMilliseconds, cancellationToken);
                    continue;
                }

                try
                {
                    var partialTranscript = await _workerClient.TranscribeAsync(
                        snapshot.Value.FilePath,
                        language: null,
                        cancellationToken);

                    if (!string.IsNullOrWhiteSpace(partialTranscript))
                    {
                        var operation = Dispatcher.InvokeAsync(
                            () => ApplyRealtimeTranscriptUpdateAsync(partialTranscript, cancellationToken));
                        await operation.Task.Unwrap();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                }
                finally
                {
                    try
                    {
                        File.Delete(snapshot.Value.FilePath);
                    }
                    catch
                    {
                    }
                }

                await Task.Delay(RealtimePollingDelayMilliseconds, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private Task ApplyRealtimeTranscriptUpdateAsync(string partialTranscript, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        EnsureTranscriptSessionInitialized();

        var update = _realtimeAssembler.Update(partialTranscript, isFinal: false);
        if (string.IsNullOrWhiteSpace(update.DisplayText) && string.IsNullOrWhiteSpace(update.CommittedText))
        {
            return Task.CompletedTask;
        }

        _lastTranscriptText = update.DisplayText;
        UpdateAuxiliaryButtons();

        if (RouteRealtimeTranscriptToWindow)
        {
            EnsureCurrentTranscriptWindow(show: false, activate: false);
        }

        UpdateCurrentTranscriptWindows(update.DisplayText);

        return Task.CompletedTask;
    }

    private void EnsureTranscriptSessionInitialized()
    {
        if (_currentTranscriptSessionInitialized)
        {
            return;
        }

        _lastTranscriptRevision++;
        _currentTranscriptSessionInitialized = true;
        MarkTranscriptWindowsAsPrevious();
    }

    private TranscriptWindow EnsureCurrentTranscriptWindow(bool show, bool activate)
    {
        TranscriptWindow window;

        if (_settings.ShowTranscriptInWindow)
        {
            window = _activeRealtimeTranscriptWindow
                ?? _transcriptWindows.FirstOrDefault(item =>
                    item.IsLatestTranscript && item.TranscriptRevision == _lastTranscriptRevision)
                ?? CreateTranscriptWindow();
        }
        else
        {
            if (_transcriptWindow is null)
            {
                _transcriptWindow = CreateTranscriptWindow();
            }

            window = _transcriptWindow;
        }

        _activeRealtimeTranscriptWindow = window;
        window.ApplyLanguage(SelectedLanguageCode);
        window.SetTranscript(_lastTranscriptText ?? string.Empty, _lastTranscriptRevision, isLatestTranscript: true);

        if (show && !window.IsVisible)
        {
            window.Show();
        }

        if (activate && window.IsVisible)
        {
            window.Activate();
        }

        return window;
    }

    private TranscriptWindow CreateTranscriptWindow()
    {
        var window = new TranscriptWindow();
        window.Closed += TranscriptWindow_Closed;

        if (_settings.ShowTranscriptInWindow)
        {
            _transcriptWindows.Add(window);
        }

        return window;
    }

    private void UpdateCurrentTranscriptWindows(string transcript)
    {
        if (!_currentTranscriptSessionInitialized)
        {
            return;
        }

        if (_transcriptWindow is not null)
        {
            _transcriptWindow.SetTranscript(transcript, _lastTranscriptRevision, isLatestTranscript: true);
        }

        foreach (var transcriptWindow in _transcriptWindows
                     .Where(window => window.TranscriptRevision == _lastTranscriptRevision || window.IsLatestTranscript)
                     .ToArray())
        {
            transcriptWindow.SetTranscript(transcript, _lastTranscriptRevision, isLatestTranscript: true);
        }
    }

    private async Task HandleCompletedTranscriptAsync(string transcript, CancellationToken cancellationToken)
    {
        if (IsRealtimeModeSelected)
        {
            await HandleCompletedRealtimeTranscriptAsync(transcript, cancellationToken);
            return;
        }

        await HandleCompletedTraditionalTranscriptAsync(transcript, cancellationToken);
    }

    private async Task HandleCompletedTraditionalTranscriptAsync(string transcript, CancellationToken cancellationToken)
    {
        EnsureTranscriptSessionInitialized();
        _lastTranscriptText = transcript;
        UpdateCurrentTranscriptWindows(transcript);
        UpdateAuxiliaryButtons();

        if (_settings.AutoOpenTranscriptWindow)
        {
            ShowLatestTranscriptWindow(activate: true);
            SetState(OverlayState.Idle, "status.text_window_opened");
            _currentTranscriptSessionInitialized = false;
            return;
        }

        PasteOutcome pasteOutcome;
        try
        {
            pasteOutcome = await _clipboardTyper.TryPasteTextAsync(
                transcript,
                ResolveTranscriptionTargetWindow(),
                _foregroundWindowTracker,
                cancellationToken);
        }
        catch (Exception)
        {
            var copied = await _clipboardTyper.TryCopyTextAsync(transcript, cancellationToken);
            SetState(
                OverlayState.Idle,
                copied ? "status.text_copied" : "status.transcription_failed",
                copied ? Array.Empty<object>() : [Translate("status.paste_failed_detail")]);

            if (_settings.AutoOpenTranscriptWindow)
            {
                ShowLatestTranscriptWindow(activate: true);
            }

            _currentTranscriptSessionInitialized = false;
            return;
        }

        SetState(
            OverlayState.Idle,
            pasteOutcome.WasPasted
                ? "status.text_inserted"
                : "status.text_copied");

        if (_settings.AutoOpenTranscriptWindow)
        {
            ShowLatestTranscriptWindow(activate: true);
        }

        _currentTranscriptSessionInitialized = false;
    }

    private Task HandleCompletedRealtimeTranscriptAsync(string transcript, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        EnsureTranscriptSessionInitialized();
        _realtimeAssembler.Update(transcript, isFinal: true);
        _lastTranscriptText = transcript;

        EnsureCurrentTranscriptWindow(show: false, activate: false);
        UpdateCurrentTranscriptWindows(transcript);
        UpdateAuxiliaryButtons();
        SetState(
            OverlayState.Idle,
            HasVisibleLatestTranscriptWindow() ? "status.text_window_opened" : "status.text_window_ready");

        ResetRealtimeSession(discardHiddenWindow: false);
        return Task.CompletedTask;
    }

    private void ShowLatestTranscriptWindow(bool activate)
    {
        if (string.IsNullOrWhiteSpace(_lastTranscriptText))
        {
            SetStatus("status.no_saved_transcript");
            return;
        }

        var useSeparateWindow = _settings.ShowTranscriptInWindow;
        TranscriptWindow window;

        if (useSeparateWindow)
        {
            var existingLatestWindow = _transcriptWindows.FirstOrDefault(window =>
                window.IsLatestTranscript && window.TranscriptRevision == _lastTranscriptRevision);
            if (existingLatestWindow is not null)
            {
                if (!existingLatestWindow.IsVisible)
                {
                    existingLatestWindow.Show();
                }

                if (activate)
                {
                    existingLatestWindow.Activate();
                }

                return;
            }

            window = CreateTranscriptWindow();
        }
        else
        {
            if (_transcriptWindow is null)
            {
                _transcriptWindow = CreateTranscriptWindow();
            }

            window = _transcriptWindow;
        }

        window.ApplyLanguage(SelectedLanguageCode);
        window.SetTranscript(_lastTranscriptText, _lastTranscriptRevision, isLatestTranscript: true);

        if (!window.IsVisible)
        {
            window.Show();
        }

        if (activate)
        {
            window.Activate();
        }
    }

    private void MarkTranscriptWindowsAsPrevious()
    {
        _transcriptWindow?.SetLatestState(false);

        foreach (var transcriptWindow in _transcriptWindows.ToArray())
        {
            transcriptWindow.SetLatestState(false);
        }
    }

    private bool HasVisibleLatestTranscriptWindow()
    {
        if (_transcriptWindow is not null
            && _transcriptWindow.IsVisible
            && _transcriptWindow.IsLatestTranscript
            && _transcriptWindow.TranscriptRevision == _lastTranscriptRevision)
        {
            return true;
        }

        return _transcriptWindows.Any(window =>
            window.IsVisible
            && window.IsLatestTranscript
            && window.TranscriptRevision == _lastTranscriptRevision);
    }

    private void TranscriptWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is not TranscriptWindow window)
        {
            return;
        }

        window.Closed -= TranscriptWindow_Closed;

        if (ReferenceEquals(_transcriptWindow, window))
        {
            _transcriptWindow = null;
        }

        if (ReferenceEquals(_activeRealtimeTranscriptWindow, window))
        {
            _activeRealtimeTranscriptWindow = null;
        }

        _transcriptWindows.Remove(window);
    }

    private void ClearTranscriptionTargetWindow()
    {
        _transcriptionTargetWindow = IntPtr.Zero;
    }

    private IntPtr ResolveTranscriptionTargetWindow()
    {
        if (_transcriptionTargetWindow != IntPtr.Zero)
        {
            return _transcriptionTargetWindow;
        }

        return _foregroundWindowTracker.LastExternalWindow;
    }

    private string Translate(string key, params object[] args)
    {
        return UiText.Translate(SelectedLanguageCode, key, args);
    }

    private void UpdateDropHintVisibility()
    {
        DropHintOverlay.Visibility = _isMediaDropActive ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        var mediaPath = TryGetDraggedMediaPath(e.Data);
        _isMediaDropActive = false;
        UpdateDropHintVisibility();

        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            return;
        }

        e.Handled = true;
        _transcriptionTargetWindow = _foregroundWindowTracker.LastExternalWindow;
        await ImportMediaFileAsync(mediaPath);
    }

    private void Window_DragEnter(object sender, System.Windows.DragEventArgs e)
    {
        HandleMediaDrag(e);
    }

    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        HandleMediaDrag(e);
    }

    private void Window_DragLeave(object sender, System.Windows.DragEventArgs e)
    {
        _isMediaDropActive = false;
        UpdateDropHintVisibility();
    }

    private void HandleMediaDrag(System.Windows.DragEventArgs e)
    {
        var mediaPath = TryGetDraggedMediaPath(e.Data);
        var canDrop = _state == OverlayState.Idle && !_isPreloadingModel && !string.IsNullOrWhiteSpace(mediaPath);
        e.Effects = canDrop ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None;
        e.Handled = true;
        _isMediaDropActive = canDrop;
        UpdateDropHintVisibility();
    }

    private static string? TryGetDraggedMediaPath(System.Windows.IDataObject data)
    {
        if (!data.GetDataPresent(System.Windows.DataFormats.FileDrop))
        {
            return null;
        }

        if (data.GetData(System.Windows.DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return null;
        }

        return files.FirstOrDefault(MediaFileImport.CanImport);
    }

    private void TopLeftResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeOverlay(-e.HorizontalChange, -e.VerticalChange, resizeFromLeft: true, resizeFromTop: true);
    }

    private void TopRightResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeOverlay(e.HorizontalChange, -e.VerticalChange, resizeFromLeft: false, resizeFromTop: true);
    }

    private void BottomLeftResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeOverlay(-e.HorizontalChange, e.VerticalChange, resizeFromLeft: true, resizeFromTop: false);
    }

    private void BottomRightResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        ResizeOverlay(e.HorizontalChange, e.VerticalChange, resizeFromLeft: false, resizeFromTop: false);
    }

    private void ResizeOverlay(double horizontalDelta, double verticalDelta, bool resizeFromLeft, bool resizeFromTop)
    {
        var currentWidth = Width;
        var currentHeight = Height;

        var nextWidth = Math.Clamp(currentWidth + horizontalDelta, MinimumOverlayWidth, DefaultOverlayWidth);
        var nextHeight = Math.Clamp(currentHeight + verticalDelta, MinimumOverlayHeight, DefaultOverlayHeight);

        if (resizeFromLeft)
        {
            Left += currentWidth - nextWidth;
        }

        if (resizeFromTop)
        {
            Top += currentHeight - nextHeight;
        }

        Width = nextWidth;
        Height = nextHeight;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        CancelCurrentPreload();
        _realtimePollingCts?.Cancel();
        _lifetimeCts.Cancel();
        _workerClient.StatusChanged -= WorkerClient_StatusChanged;
        UnregisterGlobalHotkey();

        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }

        _workerClient.Abort();
        _foregroundWindowTracker.Dispose();
        _audioRecorder.Dispose();
        DisposeTranscriptionCts();

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.MouseUp -= TrayIcon_MouseUp;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _trayDisplayIcon?.Dispose();
        _trayDisplayIcon = null;
        _trayBaseIcon?.Dispose();
        _trayBaseIcon = null;
        _trayMenu?.Dispose();
        _trayMenu = null;

        if (_transcriptWindow is not null)
        {
            _transcriptWindow.Closed -= TranscriptWindow_Closed;
            _transcriptWindow.Close();
            _transcriptWindow = null;
        }

        foreach (var transcriptWindow in _transcriptWindows.ToArray())
        {
            transcriptWindow.Closed -= TranscriptWindow_Closed;
            transcriptWindow.Close();
        }

        _transcriptWindows.Clear();

        _lifetimeCts.Dispose();
    }

    private async Task HandleGlobalHotkeyAsync()
    {
        if (_state == OverlayState.Transcribing)
        {
            return;
        }

        _trayInteractionWindow = _foregroundWindowTracker.LastExternalWindow;
        await ExecutePrimaryActionAsync(restoreFromTray: false, invokedFromTray: true);
    }

    private void RegisterGlobalHotkey()
    {
        if (_hwndSource is null)
        {
            return;
        }

        UnregisterGlobalHotkey();

        var hotkey = GlobalHotkeyFormatter.ParseOrDefault(_settings.TranscriptionHotkey);
        _settings.TranscriptionHotkey = GlobalHotkeyFormatter.Format(hotkey);

        if (!Win32Native.RegisterHotKey(
                _hwndSource.Handle,
                TranscriptionHotkeyId,
                GlobalHotkeyFormatter.ToNativeModifiers(hotkey.Modifiers),
                (uint)GlobalHotkeyFormatter.ToVirtualKey(hotkey.Key)))
        {
            SetStatus("status.hotkey_register_failed", _settings.TranscriptionHotkey);
        }
    }

    private void UnregisterGlobalHotkey()
    {
        if (_hwndSource is null)
        {
            return;
        }

        Win32Native.UnregisterHotKey(_hwndSource.Handle, TranscriptionHotkeyId);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && wParam.ToInt32() == TranscriptionHotkeyId)
        {
            handled = true;
            _ = Dispatcher.InvokeAsync(() => _ = HandleGlobalHotkeyAsync());
        }

        return IntPtr.Zero;
    }
}
