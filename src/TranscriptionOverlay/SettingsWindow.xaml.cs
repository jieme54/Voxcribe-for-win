using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TranscriptionOverlay.Models;
using TranscriptionOverlay.Services;
using Forms = System.Windows.Forms;
using MediaBrush = System.Windows.Media.Brush;

namespace TranscriptionOverlay;

public partial class SettingsWindow : Window
{
    private const double HomeWindowHeightSlack = 0d;
    private const double MinimumWindowHeight = 360d;
    private const double MaximumModelBrowserHeight = 648d;
    private const double ModelBrowserHeightSlack = 14d;
    private const double DefaultChromeHeight = 42d;
    private static readonly Duration ModelBrowserSlideDuration = new(TimeSpan.FromMilliseconds(280));

    private readonly AppSettings _settings;
    private readonly AppSettingsService _settingsService;
    private readonly AppEnvironment _appEnvironment;
    private readonly TranscriptionModelCatalog _modelCatalog;
    private readonly ModelPackageManager _modelPackageManager;
    private readonly ModelDownloadCoordinator _modelDownloadCoordinator;
    private readonly LocalModelSourceCatalog _modelSourceCatalog;
    private readonly BackendCapabilityService _backendCapabilityService;
    private readonly BackendRuntimeInstallerService _backendRuntimeInstallerService;
    private readonly AudioSourceCatalog _audioSourceCatalog;
    private readonly PythonRuntimeLocator _pythonRuntimeLocator;
    private readonly ObservableCollection<ModelItemViewModel> _modelItemsPaneA = [];
    private readonly ObservableCollection<ModelItemViewModel> _modelItemsPaneB = [];
    private readonly ObservableCollection<HardwareAccelerationOptionViewModel> _hardwareAccelerationOptions = [];

    private BackendCapabilities _backendCapabilities = new(
        HasQwenRuntime: false,
        HasCrisperWhisperRuntime: false,
        HasPhononRuntime: false,
        HasVoxtralRealtimeRuntime: false,
        PythonVersion: null);
    private GpuDiagnostics? _gpuDiagnostics;
    private bool _isBusy;
    private bool _isCapturingHotkey;
    private bool _isModelBrowserVisible;
    private bool _isModelBrowserPaneAActive = true;
    private bool _isModelBrowserTransitionRunning;
    private bool _isWindowClosed;
    private bool _ignoreLanguageSelectionChange;
    private bool _ignoreAudioSourceSelectionChange;
    private bool _ignoreTranscriptPreferenceChange;
    private bool _ignoreHardwareAccelerationSelectionChange;
    private TaskCompletionSource<bool>? _confirmationCompletionSource;
    private double _windowChromeHeight = DefaultChromeHeight;
    private ModelBrowserLevel _modelBrowserLevel = ModelBrowserLevel.Publishers;
    private string? _selectedPublisherKey;
    private string? _selectedModelFamilyKey;

    public SettingsWindow(
        AppSettings settings,
        AppSettingsService settingsService,
        AppEnvironment appEnvironment,
        TranscriptionModelCatalog modelCatalog,
        ModelPackageManager modelPackageManager,
        ModelDownloadCoordinator modelDownloadCoordinator,
        LocalModelSourceCatalog modelSourceCatalog,
        BackendCapabilityService backendCapabilityService,
        BackendRuntimeInstallerService backendRuntimeInstallerService,
        AudioSourceCatalog audioSourceCatalog,
        PythonRuntimeLocator pythonRuntimeLocator)
    {
        InitializeComponent();

        _settings = settings;
        _settingsService = settingsService;
        _appEnvironment = appEnvironment;
        _modelCatalog = modelCatalog;
        _modelPackageManager = modelPackageManager;
        _modelDownloadCoordinator = modelDownloadCoordinator;
        _modelSourceCatalog = modelSourceCatalog;
        _backendCapabilityService = backendCapabilityService;
        _backendRuntimeInstallerService = backendRuntimeInstallerService;
        _audioSourceCatalog = audioSourceCatalog;
        _pythonRuntimeLocator = pythonRuntimeLocator;
        _backendCapabilities = _backendCapabilityService.CachedCapabilities ?? _backendCapabilities;
        _gpuDiagnostics = _backendCapabilityService.CachedGpuDiagnostics;
        _backendCapabilityService.GpuDiagnosticsUpdated += BackendCapabilityService_GpuDiagnosticsUpdated;
        _backendRuntimeInstallerService.ProgressChanged += BackendRuntimeInstallerService_ProgressChanged;

        ModelsItemsControlA.ItemsSource = _modelItemsPaneA;
        ModelsItemsControlB.ItemsSource = _modelItemsPaneB;
        HardwareAccelerationComboBox.ItemsSource = _hardwareAccelerationOptions;

        Loaded += SettingsWindow_Loaded;
        Closed += SettingsWindow_Closed;
        PreviewKeyDown += SettingsWindow_PreviewKeyDown;
        _modelDownloadCoordinator.DownloadsChanged += ModelDownloadCoordinator_DownloadsChanged;
        _settings.PythonRuntimePath = _pythonRuntimeLocator.NormalizeManagedPythonPath(_settings.PythonRuntimePath);
        _pythonRuntimeLocator.ConfigureCustomPythonPath(_settings.PythonRuntimePath);
        _settings.HardwareAccelerationDevice = HardwareAccelerationPreference.Normalize(_settings.HardwareAccelerationDevice);
        _settings.AutomaticHardwareAccelerationDevice = NormalizeAutomaticHardwareAccelerationDevice(
            _settings.AutomaticHardwareAccelerationDevice);
        InitializeLanguageSelection();
        InitializeHotkeyDisplay();
        InitializeTranscriptOptions();
        RefreshHardwareAccelerationOptions();
        RefreshAudioSources();
        ApplyLocalizedText();
        RefreshModelItems();
        UpdateViewState();
    }

    public event EventHandler<string>? InterfaceLanguageChanged;

    public event EventHandler<ModelSelectionChange>? ModelSelectionChanged;

    public event EventHandler<string>? HotkeyChanged;

    public event EventHandler? HardwareAccelerationChanged;

    public event EventHandler? PythonRuntimeChanged;

    private string SelectedLanguageCode => string.Equals(_settings.LanguageCode, "en", StringComparison.OrdinalIgnoreCase)
        ? "en"
        : "fr";

    private void InitializeLanguageSelection()
    {
        _ignoreLanguageSelectionChange = true;
        LanguageComboBox.SelectedIndex = SelectedLanguageCode == "en" ? 1 : 0;
        _ignoreLanguageSelectionChange = false;
    }

    private void InitializeHotkeyDisplay()
    {
        var hotkey = GlobalHotkeyFormatter.ParseOrDefault(_settings.TranscriptionHotkey);
        _settings.TranscriptionHotkey = GlobalHotkeyFormatter.Format(hotkey);
        HotkeyTextBox.Text = _settings.TranscriptionHotkey;
    }

    private void InitializeTranscriptOptions()
    {
        _ignoreTranscriptPreferenceChange = true;
        TranscriptWindowCheckBox.IsChecked = _settings.ShowTranscriptInWindow;
        AutoOpenTranscriptWindowCheckBox.IsChecked = _settings.AutoOpenTranscriptWindow;
        _ignoreTranscriptPreferenceChange = false;
        UpdateTranscriptWindowOptionsState();
    }

    private async void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyCachedGpuDiagnostics();

        try
        {
            _backendCapabilities = await _backendCapabilityService.DetectAsync(CancellationToken.None);
        }
        catch
        {
            SetStatus("settings.status.probe_failed");
        }

        if (HasNvidiaGpu(_gpuDiagnostics))
        {
            try
            {
                await _backendRuntimeInstallerService.DetectNvidiaRuntimePlanAsync(CancellationToken.None);
            }
            catch
            {
                // The NVIDIA button reports the actionable driver/detection error
                // if the user explicitly requests installation or repair.
            }
        }

        RefreshHardwareAccelerationOptions();
        RefreshModelItems();
        RequestWindowMetricsUpdate();
    }

    private void SettingsWindow_Closed(object? sender, EventArgs e)
    {
        CloseConfirmationOverlay(false);
        _isWindowClosed = true;
        _modelDownloadCoordinator.DownloadsChanged -= ModelDownloadCoordinator_DownloadsChanged;
        _backendCapabilityService.GpuDiagnosticsUpdated -= BackendCapabilityService_GpuDiagnosticsUpdated;
        _backendRuntimeInstallerService.ProgressChanged -= BackendRuntimeInstallerService_ProgressChanged;
    }

    private void BackendCapabilityService_GpuDiagnosticsUpdated(object? sender, GpuDiagnostics diagnostics)
    {
        if (_isWindowClosed)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            _gpuDiagnostics = diagnostics;
            RefreshHardwareAccelerationOptions();
        });
    }

    private void BackendRuntimeInstallerService_ProgressChanged(object? sender, RuntimeInstallProgress progress)
    {
        if (_isWindowClosed)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() =>
        {
            var text = progress.Percent is { } percent
                ? $"{progress.Message} ({percent}%)"
                : progress.Message;
            SetStatusText(text);
        });
    }

    private void ModelDownloadCoordinator_DownloadsChanged(object? sender, EventArgs e)
    {
        if (_isWindowClosed)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(RefreshModelItems);
    }

    private void LanguageComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ignoreLanguageSelectionChange || LanguageComboBox.SelectedItem is not ComboBoxItem { Tag: string languageCode })
        {
            return;
        }

        _settings.LanguageCode = languageCode;
        _settingsService.Save(_settings);
        ApplyLocalizedText();
        RefreshModelItems();
        InterfaceLanguageChanged?.Invoke(this, languageCode);
    }

    private void AudioSourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ignoreAudioSourceSelectionChange || AudioSourceComboBox.SelectedValue is not string sourceId)
        {
            return;
        }

        _settings.AudioSourceId = sourceId;
        _settingsService.Save(_settings);
    }

    private async void HardwareAccelerationComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ignoreHardwareAccelerationSelectionChange
            || HardwareAccelerationComboBox.SelectedValue is not string selectedDevice)
        {
            return;
        }

        var normalizedDevice = HardwareAccelerationPreference.Normalize(selectedDevice);
        if (TrySwitchInstalledNvidiaRuntimeForHardwareSelection(normalizedDevice, out var runtimeChanged))
        {
            if (runtimeChanged)
            {
                await RefreshGpuDiagnosticsAfterRuntimeSwitchAsync();
            }

            return;
        }

        if (string.Equals(_settings.HardwareAccelerationDevice, normalizedDevice, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _settings.HardwareAccelerationDevice = normalizedDevice;
        _settingsService.Save(_settings);
        RefreshHardwareAccelerationOptions();
        HardwareAccelerationChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool TrySwitchInstalledNvidiaRuntimeForHardwareSelection(
        string normalizedDevice,
        out bool runtimeChanged)
    {
        runtimeChanged = false;
        if (!_backendRuntimeInstallerService.IsNvidiaCudaRuntimeInstalled()
            || IsAmdRuntimeActive())
        {
            return false;
        }

        var plan = _backendRuntimeInstallerService.CachedNvidiaRuntimePlan;
        if (plan is null)
        {
            return false;
        }

        var targetGpuIndex = HardwareAccelerationPreference.TryParseGpuIndex(normalizedDevice, out var gpuIndex)
            && plan.Gpus.Any(gpu => gpu.Index == gpuIndex)
                ? gpuIndex
                : string.Equals(normalizedDevice, HardwareAccelerationPreference.Auto, StringComparison.OrdinalIgnoreCase)
                    ? plan.PreferredGpuIndex
                    : -1;
        if (targetGpuIndex < 0)
        {
            return false;
        }

        var targetRuntimePath = _backendRuntimeInstallerService.GetInstalledNvidiaCudaRuntimePathForGpu(targetGpuIndex);
        if (string.IsNullOrWhiteSpace(targetRuntimePath))
        {
            return false;
        }

        runtimeChanged = !string.Equals(
            GetEffectivePythonRuntimePath(),
            targetRuntimePath,
            StringComparison.OrdinalIgnoreCase);

        var automaticDevice = string.Equals(
            normalizedDevice,
            HardwareAccelerationPreference.Auto,
            StringComparison.OrdinalIgnoreCase)
                ? $"{HardwareAccelerationPreference.GpuPrefix}{targetGpuIndex}"
                : _settings.AutomaticHardwareAccelerationDevice;
        ActivatePythonRuntime(
            targetRuntimePath,
            useGpuAcceleration: true,
            hardwareAccelerationDevice: normalizedDevice,
            automaticHardwareAccelerationDevice: automaticDevice);
        return true;
    }

    private async Task RefreshGpuDiagnosticsAfterRuntimeSwitchAsync()
    {
        try
        {
            _gpuDiagnostics = await _backendCapabilityService.DetectGpuDiagnosticsAsync(
                CancellationToken.None,
                forceRefresh: true);
            RefreshHardwareAccelerationOptions();
        }
        catch
        {
            // Runtime activation already succeeded. A later application restart
            // can retry the diagnostic without undoing the selected runtime.
        }
    }

    private async void InstallAmdRuntimeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || !InstallAmdRuntimeButton.IsEnabled)
        {
            return;
        }

        if (_backendRuntimeInstallerService.GetInstalledAmdRocmRuntimePath() is { } installedPythonPath)
        {
            ActivatePythonRuntime(installedPythonPath, useGpuAcceleration: true);
            SetStatus("settings.status.amd_runtime_activated");
            return;
        }

        if (!await ConfirmAsync(UiText.Translate(SelectedLanguageCode, "settings.confirm.install_amd_runtime")))
        {
            return;
        }

        await RunBusyOperationAsync(async () =>
        {
            var pythonPath = await _backendRuntimeInstallerService.InstallAmdRocmRuntimeAsync(CancellationToken.None);
            ActivatePythonRuntime(pythonPath, useGpuAcceleration: true);
            SetStatus("settings.status.amd_runtime_installed");
        }, "settings.status.amd_runtime_failed");
    }

    private async void InstallNvidiaRuntimeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || !InstallNvidiaRuntimeButton.IsEnabled)
        {
            return;
        }

        if (!await ConfirmAsync(UiText.Translate(SelectedLanguageCode, "settings.confirm.install_nvidia_runtime")))
        {
            return;
        }

        await RunBusyOperationAsync(async () =>
        {
            var installResult = await _backendRuntimeInstallerService.InstallNvidiaCudaRuntimeAsync(
                _settings.HardwareAccelerationDevice,
                CancellationToken.None);
            ActivatePythonRuntime(
                installResult.PythonPath,
                useGpuAcceleration: true,
                hardwareAccelerationDevice: HardwareAccelerationPreference.Auto,
                automaticHardwareAccelerationDevice:
                    $"{HardwareAccelerationPreference.GpuPrefix}{installResult.PreferredGpuIndex}");
            await RefreshGpuDiagnosticsAfterRuntimeSwitchAsync();
            SetStatus("settings.status.nvidia_runtime_installed");
        }, "settings.status.nvidia_runtime_failed");
    }

    private async void InstallCpuRuntimeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || !InstallCpuRuntimeButton.IsEnabled)
        {
            return;
        }

        if (_backendRuntimeInstallerService.GetInstalledCpuRuntimePath() is { } installedPythonPath)
        {
            ActivatePythonRuntime(installedPythonPath, useGpuAcceleration: false);
            SetStatus("settings.status.cpu_runtime_activated");
            return;
        }

        if (!await ConfirmAsync(UiText.Translate(SelectedLanguageCode, "settings.confirm.install_cpu_runtime")))
        {
            return;
        }

        await RunBusyOperationAsync(async () =>
        {
            var pythonPath = await _backendRuntimeInstallerService.InstallCpuRuntimeAsync(CancellationToken.None);
            ActivatePythonRuntime(pythonPath, useGpuAcceleration: false);
            SetStatus("settings.status.cpu_runtime_installed");
        }, "settings.status.cpu_runtime_failed");
    }

    private void TranscriptWindowCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_ignoreTranscriptPreferenceChange)
        {
            return;
        }

        _settings.ShowTranscriptInWindow = TranscriptWindowCheckBox.IsChecked == true;
        _settingsService.Save(_settings);
    }

    private void AutoOpenTranscriptWindowCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_ignoreTranscriptPreferenceChange)
        {
            return;
        }

        _settings.AutoOpenTranscriptWindow = AutoOpenTranscriptWindowCheckBox.IsChecked == true;
        _settingsService.Save(_settings);
    }

    private void TraditionalModeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        SetPreferredMode(TranscriptionMode.File);
    }

    private void RealtimeModeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        SetPreferredMode(TranscriptionMode.Realtime);
    }

    private void RealtimeTranscriptWindowCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
    }

    private async void SelectModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not FrameworkElement { Tag: string actionTag })
        {
            return;
        }

        if (TryHandleModelBrowserNavigation(actionTag))
        {
            return;
        }

        var entry = TryGetModelListEntry(actionTag);
        if (entry is null || !entry.IsAvailableLocally)
        {
            return;
        }

        var definition = _modelCatalog.GetByKeyOrDefault(entry.ModelKey);
        if (!definition.SupportsCurrentOperatingSystem())
        {
            return;
        }

        if (definition.RuntimeKind == ModelRuntimeKind.Qwen && !_backendCapabilities.HasQwenRuntime)
        {
            if (!await ConfirmAsync(UiText.Translate(SelectedLanguageCode, "settings.confirm.install_qwen_runtime")))
            {
                return;
            }

            var installed = await InstallQwenRuntimeAsync();
            if (!installed)
            {
                return;
            }
        }

        if (definition.RuntimeKind == ModelRuntimeKind.CrisperWhisper
            && !_backendCapabilities.HasCrisperWhisperRuntime)
        {
            if (!await ConfirmAsync(UiText.Translate(
                    SelectedLanguageCode,
                    "settings.confirm.install_crisperwhisper_runtime")))
            {
                return;
            }

            var installed = await InstallCrisperWhisperRuntimeAsync();
            if (!installed)
            {
                return;
            }
        }

        if (definition.RuntimeKind == ModelRuntimeKind.Phonon && !_backendCapabilities.HasPhononRuntime)
        {
            if (!await ConfirmAsync(UiText.Translate(SelectedLanguageCode, "settings.confirm.install_phonon_runtime")))
            {
                return;
            }

            if (!await InstallPhononRuntimeAsync())
            {
                return;
            }
        }

        var targetLabel = $"{definition.DisplayName} ({entry.SourceText})";
        if (!await ConfirmAsync(UiText.Translate(SelectedLanguageCode, "settings.confirm.change_model", targetLabel)))
        {
            return;
        }

        await RunBusyOperationAsync(() =>
        {
            _settings.SelectedModelKey = definition.Key;
            _settings.SelectedModelSourceId = entry.SourceId;
            _settings.PinSelectedModelSource = true;
            NormalizePreferredMode(definition, persist: false);
            _settingsService.Save(_settings);
            RefreshModelItems();
            SetStatus("settings.status.selected", targetLabel);
            ModelSelectionChanged?.Invoke(this, new ModelSelectionChange(definition.Key, entry.SourceId));
            return Task.CompletedTask;
        });
    }

    private async void ManageModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || sender is not FrameworkElement { Tag: string actionTag })
        {
            return;
        }

        if (TryHandleModelBrowserNavigation(actionTag))
        {
            return;
        }

        var entry = TryGetModelListEntry(actionTag);
        if (entry is null)
        {
            return;
        }

        var definition = _modelCatalog.GetByKeyOrDefault(entry.ModelKey);
        if (IsManagedDownloadBlocked(entry))
        {
            RefreshModelItems();
            SetStatus("settings.status.download_locked");
            return;
        }

        switch (entry.SourceKind)
        {
            case ModelSourceKind.Managed:
                if (definition.RuntimeKind == ModelRuntimeKind.Qwen && entry.IsAvailableLocally && !_backendCapabilities.HasQwenRuntime)
                {
                    await InstallQwenRuntimeAsync();
                    return;
                }

                if (definition.RuntimeKind == ModelRuntimeKind.CrisperWhisper
                    && entry.IsAvailableLocally
                    && !_backendCapabilities.HasCrisperWhisperRuntime)
                {
                    await InstallCrisperWhisperRuntimeAsync();
                    return;
                }

                if (definition.RuntimeKind == ModelRuntimeKind.Phonon
                    && entry.IsAvailableLocally
                    && !_backendCapabilities.HasPhononRuntime)
                {
                    await InstallPhononRuntimeAsync();
                    return;
                }

                if (entry.IsAvailableLocally)
                {
                    await DeleteManagedModelAsync(definition);
                }
                else
                {
                    await DownloadModelAsync(definition);
                }

                break;

            case ModelSourceKind.CustomDirectory:
                await RemoveCustomSourceAsync(entry);
                break;
        }
    }

    private async Task DownloadModelAsync(TranscriptionModelDefinition definition)
    {
        var managedEntry = BuildModelEntries(definition)
            .First(entry => entry.SourceKind == ModelSourceKind.Managed);
        if (IsManagedDownloadBlocked(managedEntry))
        {
            RefreshModelItems();
            SetStatus("settings.status.download_locked");
            return;
        }

        var confirmationKey = definition.RuntimeKind == ModelRuntimeKind.CrisperWhisper
            ? "settings.confirm.download_crisperwhisper"
            : "settings.confirm.download";
        if (!await ConfirmAsync(UiText.Translate(SelectedLanguageCode, confirmationKey, definition.DisplayName)))
        {
            return;
        }

        RefreshModelItems();
        if (!_modelDownloadCoordinator.TryStartDownload(definition, out var downloadTask))
        {
            RefreshModelItems();
            SetStatus("settings.status.download_locked");
            return;
        }

        SetStatus("settings.status.download_started", definition.DisplayName);

        try
        {
            await downloadTask;

            if (!_isWindowClosed)
            {
                RefreshModelItems();
                SetStatus("settings.status.downloaded", definition.DisplayName);
            }
        }
        catch (Exception ex)
        {
            if (!_isWindowClosed)
            {
                SetStatus("settings.status.download_failed", ex.Message);
            }
        }
    }

    private async Task DeleteManagedModelAsync(TranscriptionModelDefinition definition)
    {
        var isCurrentManagedSource =
            string.Equals(_settings.SelectedModelKey, definition.Key, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                _settings.SelectedModelSourceId,
                LocalModelSourceCatalog.GetManagedSourceId(definition.Key),
                StringComparison.OrdinalIgnoreCase);

        var confirmKey = isCurrentManagedSource
            ? "settings.confirm.delete_current_managed_keep_shared"
            : "settings.confirm.delete";

        if (!await ConfirmAsync(UiText.Translate(SelectedLanguageCode, confirmKey, definition.DisplayName)))
        {
            return;
        }

        await RunBusyOperationAsync(async () =>
        {
            await _modelPackageManager.DeleteAsync(definition, CancellationToken.None);

            if (isCurrentManagedSource)
            {
                var nextSource = _modelSourceCatalog.ResolveSelectedSource(definition, _settings, SelectedLanguageCode);
                _settings.SelectedModelSourceId = nextSource?.Id ?? string.Empty;
                _settings.PinSelectedModelSource = nextSource is not null;
                ModelSelectionChanged?.Invoke(this, new ModelSelectionChange(definition.Key, nextSource?.Id));
            }

            _settingsService.Save(_settings);
            RefreshModelItems();
            SetStatus("settings.status.deleted", definition.DisplayName);
        }, "settings.status.delete_failed");
    }

    private async Task RemoveCustomSourceAsync(ModelListEntry entry)
    {
        if (!await ConfirmAsync(UiText.Translate(SelectedLanguageCode, "settings.confirm.remove_local_source")))
        {
            return;
        }

        await RunBusyOperationAsync(() =>
        {
            var removedDisplayName = _modelSourceCatalog.RemoveCustomSource(_settings, entry.SourceId);
            var definition = _modelCatalog.GetByKeyOrDefault(entry.ModelKey);
            var wasCurrentSource =
                string.Equals(_settings.SelectedModelKey, entry.ModelKey, StringComparison.OrdinalIgnoreCase)
                && string.Equals(_settings.SelectedModelSourceId, entry.SourceId, StringComparison.OrdinalIgnoreCase);

            if (wasCurrentSource)
            {
                var nextSource = _modelSourceCatalog.ResolveSelectedSource(definition, _settings, SelectedLanguageCode);
                _settings.SelectedModelSourceId = nextSource?.Id ?? string.Empty;
                _settings.PinSelectedModelSource = nextSource is not null;
                ModelSelectionChanged?.Invoke(this, new ModelSelectionChange(definition.Key, nextSource?.Id));
            }

            _settingsService.Save(_settings);
            RefreshModelItems();
            SetStatus("settings.status.local_model_removed", removedDisplayName ?? entry.DisplayName);
            return Task.CompletedTask;
        }, "settings.status.local_model_remove_failed");
    }

    private async void AddLocalModelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = UiText.Translate(SelectedLanguageCode, "settings.action.add_local_model_hint"),
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK)
        {
            return;
        }

        await RunBusyOperationAsync(() =>
        {
            if (!_modelSourceCatalog.TryRegisterCustomDirectory(
                    dialog.SelectedPath,
                    _settings,
                    SelectedLanguageCode,
                    out var selectionChange,
                    out var errorMessage))
            {
                throw new InvalidOperationException(errorMessage ?? UiText.Translate(SelectedLanguageCode, "settings.status.local_model_unsupported"));
            }

            if (selectionChange is not null)
            {
                var definition = _modelCatalog.GetByKeyOrDefault(selectionChange.ModelKey);
                NormalizePreferredMode(definition, persist: false);
            }

            _settingsService.Save(_settings);
            RefreshModelItems();

            SetStatus("settings.status.local_model_added", Path.GetFullPath(dialog.SelectedPath));
            if (selectionChange is not null)
            {
                ModelSelectionChanged?.Invoke(this, selectionChange);
            }

            return Task.CompletedTask;
        }, "settings.status.local_model_add_failed");
    }

    private void CaptureHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        _isCapturingHotkey = !_isCapturingHotkey;
        UpdateHotkeyCaptureState();

        if (_isCapturingHotkey)
        {
            SetStatus("settings.status.hotkey_capture");
            Activate();
        }
        else
        {
            SetStatus("settings.status.hotkey_cancelled");
        }
    }

    private void SettingsWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_isCapturingHotkey)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            _isCapturingHotkey = false;
            UpdateHotkeyCaptureState();
            SetStatus("settings.status.hotkey_cancelled");
            e.Handled = true;
            return;
        }

        if (GlobalHotkeyFormatter.IsModifierKey(key))
        {
            e.Handled = true;
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if (CountModifiers(modifiers) < 2)
        {
            SetStatus("settings.status.hotkey_invalid");
            e.Handled = true;
            return;
        }

        var hotkey = new GlobalHotkeyDefinition(modifiers, key);
        var hotkeyText = GlobalHotkeyFormatter.Format(hotkey);
        _settings.TranscriptionHotkey = hotkeyText;
        _settingsService.Save(_settings);
        HotkeyTextBox.Text = hotkeyText;
        _isCapturingHotkey = false;
        UpdateHotkeyCaptureState();
        SetStatus("settings.status.hotkey_updated", hotkeyText);
        HotkeyChanged?.Invoke(this, hotkeyText);
        e.Handled = true;
    }

    private void ChooseModelButton_Click(object sender, RoutedEventArgs e)
    {
        ResetModelBrowserNavigation();
        RefreshModelItems();
        _isModelBrowserVisible = true;
        UpdateViewState(animatedTransition: true);
    }

    private void BackToSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isModelBrowserTransitionRunning)
        {
            return;
        }

        _isModelBrowserVisible = false;
        UpdateViewState(animatedTransition: true);
    }

    private void ModelBrowserLevelBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _isModelBrowserTransitionRunning)
        {
            return;
        }

        switch (_modelBrowserLevel)
        {
            case ModelBrowserLevel.Variants:
                NavigateModelBrowser(
                    ModelBrowserLevel.Models,
                    _selectedPublisherKey,
                    modelFamilyKey: null,
                    forward: false);
                break;
            case ModelBrowserLevel.Models:
                NavigateModelBrowser(
                    ModelBrowserLevel.Publishers,
                    publisherKey: null,
                    modelFamilyKey: null,
                    forward: false);
                break;
        }
    }

    private bool TryHandleModelBrowserNavigation(string actionTag)
    {
        if (actionTag.StartsWith("publisher:", StringComparison.Ordinal))
        {
            if (_isModelBrowserTransitionRunning)
            {
                return true;
            }

            var publisherKey = actionTag["publisher:".Length..];
            var publisherExists = BuildModelPublisherGroups().Any(publisher =>
                string.Equals(publisher.Key, publisherKey, StringComparison.OrdinalIgnoreCase));
            if (publisherExists)
            {
                NavigateModelBrowser(
                    ModelBrowserLevel.Models,
                    publisherKey,
                    modelFamilyKey: null,
                    forward: true);
            }

            return true;
        }

        if (!actionTag.StartsWith("family:", StringComparison.Ordinal))
        {
            return false;
        }

        if (_isModelBrowserTransitionRunning)
        {
            return true;
        }

        var parts = actionTag["family:".Length..].Split(
            ['|'],
            count: 2,
            StringSplitOptions.None);
        if (parts.Length != 2)
        {
            return true;
        }

        var familyExists = BuildModelPublisherGroups()
            .FirstOrDefault(publisher => string.Equals(
                publisher.Key,
                parts[0],
                StringComparison.OrdinalIgnoreCase))?
            .Families.Any(family => string.Equals(
                family.Key,
                parts[1],
                StringComparison.OrdinalIgnoreCase)) == true;
        if (familyExists)
        {
            NavigateModelBrowser(
                ModelBrowserLevel.Variants,
                parts[0],
                parts[1],
                forward: true);
        }

        return true;
    }

    private void ResetModelBrowserNavigation()
    {
        StopModelBrowserPaneAnimations();
        _modelBrowserLevel = ModelBrowserLevel.Publishers;
        _selectedPublisherKey = null;
        _selectedModelFamilyKey = null;
        _isModelBrowserPaneAActive = true;
        _isModelBrowserTransitionRunning = false;
        _modelItemsPaneB.Clear();
        ModelBrowserPaneA.Visibility = Visibility.Visible;
        ModelBrowserPaneB.Visibility = Visibility.Collapsed;
        ModelBrowserPaneA.IsHitTestVisible = true;
        ModelBrowserPaneB.IsHitTestVisible = false;
    }

    private void NavigateModelBrowser(
        ModelBrowserLevel targetLevel,
        string? publisherKey,
        string? modelFamilyKey,
        bool forward)
    {
        if (_isModelBrowserTransitionRunning || !_isModelBrowserVisible)
        {
            return;
        }

        var currentPane = _isModelBrowserPaneAActive ? ModelBrowserPaneA : ModelBrowserPaneB;
        var incomingPane = _isModelBrowserPaneAActive ? ModelBrowserPaneB : ModelBrowserPaneA;
        var currentTransform = (TranslateTransform)currentPane.RenderTransform;
        var incomingTransform = (TranslateTransform)incomingPane.RenderTransform;

        _isModelBrowserTransitionRunning = true;
        _modelBrowserLevel = targetLevel;
        _selectedPublisherKey = publisherKey;
        _selectedModelFamilyKey = modelFamilyKey;
        _isModelBrowserPaneAActive = !_isModelBrowserPaneAActive;
        PopulateActiveModelBrowserPane();

        StopModelBrowserPaneAnimations();
        _isModelBrowserTransitionRunning = true;
        currentPane.Visibility = Visibility.Visible;
        incomingPane.Visibility = Visibility.Visible;
        currentPane.IsHitTestVisible = false;
        incomingPane.IsHitTestVisible = false;
        System.Windows.Controls.Panel.SetZIndex(currentPane, 0);
        System.Windows.Controls.Panel.SetZIndex(incomingPane, 1);

        ModelBrowserViewport.UpdateLayout();
        var slideDistance = Math.Max(1d, ModelBrowserViewport.ActualWidth);
        incomingTransform.X = forward ? slideDistance : -slideDistance;
        currentTransform.X = 0d;

        var easing = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
        var currentAnimation = new DoubleAnimation
        {
            From = 0d,
            To = forward ? -slideDistance : slideDistance,
            Duration = ModelBrowserSlideDuration,
            EasingFunction = easing,
        };
        var incomingAnimation = new DoubleAnimation
        {
            From = incomingTransform.X,
            To = 0d,
            Duration = ModelBrowserSlideDuration,
            EasingFunction = easing,
        };

        incomingAnimation.Completed += (_, _) =>
        {
            currentTransform.BeginAnimation(TranslateTransform.XProperty, null);
            incomingTransform.BeginAnimation(TranslateTransform.XProperty, null);
            currentTransform.X = 0d;
            incomingTransform.X = 0d;
            currentPane.Visibility = Visibility.Collapsed;
            incomingPane.Visibility = Visibility.Visible;
            currentPane.IsHitTestVisible = false;
            incomingPane.IsHitTestVisible = true;
            System.Windows.Controls.Panel.SetZIndex(currentPane, 0);
            System.Windows.Controls.Panel.SetZIndex(incomingPane, 0);
            _isModelBrowserTransitionRunning = false;
        };

        currentTransform.BeginAnimation(
            TranslateTransform.XProperty,
            currentAnimation,
            HandoffBehavior.SnapshotAndReplace);
        incomingTransform.BeginAnimation(
            TranslateTransform.XProperty,
            incomingAnimation,
            HandoffBehavior.SnapshotAndReplace);
    }

    private void StopModelBrowserPaneAnimations()
    {
        var paneATransform = (TranslateTransform)ModelBrowserPaneA.RenderTransform;
        var paneBTransform = (TranslateTransform)ModelBrowserPaneB.RenderTransform;
        paneATransform.BeginAnimation(TranslateTransform.XProperty, null);
        paneBTransform.BeginAnimation(TranslateTransform.XProperty, null);
        paneATransform.X = 0d;
        paneBTransform.X = 0d;
    }

    private void UpdateModelBrowserPaneHeader(bool paneA)
    {
        var backButton = paneA ? ModelBrowserPaneABackButton : ModelBrowserPaneBBackButton;
        var titleTextBlock = paneA ? ModelBrowserPaneATitleTextBlock : ModelBrowserPaneBTitleTextBlock;
        backButton.Visibility = _modelBrowserLevel == ModelBrowserLevel.Publishers
            ? Visibility.Collapsed
            : Visibility.Visible;
        backButton.ToolTip = UiText.Translate(SelectedLanguageCode, "settings.action.back");

        var publishers = BuildModelPublisherGroups();
        var publisher = publishers.FirstOrDefault(group => string.Equals(
            group.Key,
            _selectedPublisherKey,
            StringComparison.OrdinalIgnoreCase));
        var family = publisher?.Families.FirstOrDefault(group => string.Equals(
            group.Key,
            _selectedModelFamilyKey,
            StringComparison.OrdinalIgnoreCase));

        titleTextBlock.Text = _modelBrowserLevel switch
        {
            ModelBrowserLevel.Models when publisher is not null => UiText.Translate(
                SelectedLanguageCode,
                "settings.browser.models_for",
                publisher.DisplayName),
            ModelBrowserLevel.Variants when family is not null => UiText.Translate(
                SelectedLanguageCode,
                "settings.browser.variants_for",
                family.DisplayName),
            _ => UiText.Translate(SelectedLanguageCode, "settings.publishers"),
        };
    }

    private void OpenAppDataFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_appEnvironment.LocalAppDataDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = _appEnvironment.LocalAppDataDirectory,
                UseShellExecute = true,
            });
            SetStatus("settings.status.appdata_opened", _appEnvironment.LocalAppDataDirectory);
        }
        catch (Exception ex)
        {
            SetStatus("settings.status.appdata_open_failed", ex.Message);
        }
    }

    private async Task RunBusyOperationAsync(Func<Task> action, string failureStatusKey = "settings.status.select_failed")
    {
        SetBusy(true);

        try
        {
            await action();
        }
        catch (Exception ex)
        {
            SetStatus(failureStatusKey, ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool isBusy)
    {
        _isBusy = isBusy;
        LanguageComboBox.IsEnabled = !isBusy;
        AudioSourceComboBox.IsEnabled = !isBusy;
        HardwareAccelerationComboBox.IsEnabled = !isBusy;
        InstallNvidiaRuntimeButton.IsEnabled = !isBusy;
        InstallAmdRuntimeButton.IsEnabled = !isBusy;
        InstallCpuRuntimeButton.IsEnabled = !isBusy;
        AddLocalModelButton.IsEnabled = !isBusy;
        CaptureHotkeyButton.IsEnabled = !isBusy;
        TranscriptWindowCheckBox.IsEnabled = !isBusy;
        UpdateTranscriptWindowOptionsState();
        UpdateModeSelectionState();
        UpdateRuntimeInstallButtonState();

        RefreshModelItems();
    }

    private void RefreshModelItems()
    {
        var currentDefinition = _modelCatalog.GetByKeyOrDefault(_settings.SelectedModelKey);
        NormalizePreferredMode(currentDefinition, persist: false);
        var currentSource = _modelSourceCatalog.ResolveSelectedSource(currentDefinition, _settings, SelectedLanguageCode);

        if (currentSource is null)
        {
            CurrentModelValueTextBlock.Text = UiText.Translate(SelectedLanguageCode, "settings.model.none_selected");
            CurrentModelSourceTextBlock.Text = UiText.Translate(SelectedLanguageCode, "settings.model.none_selected_hint");
        }
        else
        {
            CurrentModelValueTextBlock.Text = currentDefinition.DisplayName;
            CurrentModelSourceTextBlock.Text = currentSource.DisplayName;
        }

        PopulateActiveModelBrowserPane();
        UpdateModeSelectionState();
    }

    private void PopulateActiveModelBrowserPane()
    {
        var items = _isModelBrowserPaneAActive ? _modelItemsPaneA : _modelItemsPaneB;
        PopulateModelBrowserItems(items);
        UpdateModelBrowserPaneHeader(_isModelBrowserPaneAActive);
    }

    private void PopulateModelBrowserItems(ObservableCollection<ModelItemViewModel> items)
    {
        items.Clear();
        var publishers = BuildModelPublisherGroups();

        var selectedPublisher = publishers.FirstOrDefault(group =>
            string.Equals(group.Key, _selectedPublisherKey, StringComparison.OrdinalIgnoreCase));
        if (_modelBrowserLevel != ModelBrowserLevel.Publishers && selectedPublisher is null)
        {
            _modelBrowserLevel = ModelBrowserLevel.Publishers;
            _selectedPublisherKey = null;
            _selectedModelFamilyKey = null;
        }

        switch (_modelBrowserLevel)
        {
            case ModelBrowserLevel.Publishers:
                foreach (var publisher in publishers)
                {
                    items.Add(CreatePublisherBrowserItem(publisher));
                }
                break;

            case ModelBrowserLevel.Models when selectedPublisher is not null:
                foreach (var family in selectedPublisher.Families)
                {
                    if (family.IsVariantFamily)
                    {
                        items.Add(CreateModelFamilyBrowserItem(selectedPublisher, family));
                        continue;
                    }

                    foreach (var item in BuildDetailedModelItems(family.Definitions))
                    {
                        items.Add(item);
                    }
                }
                break;

            case ModelBrowserLevel.Variants when selectedPublisher is not null:
                var selectedFamily = selectedPublisher.Families.FirstOrDefault(family =>
                    string.Equals(family.Key, _selectedModelFamilyKey, StringComparison.OrdinalIgnoreCase));
                if (selectedFamily is null)
                {
                    _modelBrowserLevel = ModelBrowserLevel.Models;
                    _selectedModelFamilyKey = null;
                    PopulateModelBrowserItems(items);
                    return;
                }

                foreach (var item in BuildDetailedModelItems(selectedFamily.Definitions))
                {
                    items.Add(item);
                }
                break;
        }
    }

    private IEnumerable<ModelItemViewModel> BuildDetailedModelItems(
        IEnumerable<TranscriptionModelDefinition> definitions)
    {
        var hasActiveDownload = _modelDownloadCoordinator.HasActiveDownload();

        foreach (var definition in definitions)
        {
            foreach (var entry in BuildModelEntries(definition))
            {
                var isCurrent =
                    string.Equals(_settings.SelectedModelKey, entry.ModelKey, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(_settings.SelectedModelSourceId, entry.SourceId, StringComparison.OrdinalIgnoreCase);
                var isManagedDownloadBlocked =
                    !entry.IsDownloading
                    && hasActiveDownload
                    && entry.SourceKind == ModelSourceKind.Managed
                    && !entry.IsAvailableLocally;

                yield return new ModelItemViewModel
                {
                    ActionTag = BuildActionTag(entry),
                    DisplayName = entry.DisplayName,
                    SourceText = entry.SourceText,
                    InfoText = BuildInfoText(definition, entry),
                    StatusText = BuildStatusText(definition, entry, isCurrent),
                    FileBadgeText = UiText.Translate(SelectedLanguageCode, "settings.model.badge.file"),
                    FileBadgeToolTip = UiText.Translate(SelectedLanguageCode, "settings.model.badge.file_tooltip"),
                    FileBadgeBackground = CreateBrush(definition.SupportsFileTranscription ? "#DCEBFF" : "#F0F3F7"),
                    FileBadgeBorderBrush = CreateBrush(definition.SupportsFileTranscription ? "#4B78A9" : "#B8C0CC"),
                    FileBadgeForeground = CreateBrush(definition.SupportsFileTranscription ? "#1E4F81" : "#7E8794"),
                    FileBadgeOpacity = definition.SupportsFileTranscription ? 1d : 0.55d,
                    RealtimeBadgeText = UiText.Translate(SelectedLanguageCode, "settings.model.badge.realtime"),
                    RealtimeBadgeToolTip = UiText.Translate(SelectedLanguageCode, "settings.model.badge.realtime_tooltip"),
                    RealtimeBadgeBackground = CreateBrush(definition.SupportsRealtimeTranscription ? "#FFDCD4" : "#F4F0EF"),
                    RealtimeBadgeBorderBrush = CreateBrush(definition.SupportsRealtimeTranscription ? "#B45C46" : "#C8BDBB"),
                    RealtimeBadgeForeground = CreateBrush(definition.SupportsRealtimeTranscription ? "#8A2F20" : "#8D7E7A"),
                    RealtimeBadgeOpacity = definition.SupportsRealtimeTranscription ? 1d : 0.55d,
                    CanSelect = !_isBusy
                        && !entry.IsDownloading
                        && entry.IsAvailableLocally
                        && !isCurrent
                        && definition.SupportsCurrentOperatingSystem()
                        && IsSelectableWithCurrentRuntime(definition),
                    CanManage = !_isBusy && !entry.IsDownloading && entry.CanManage && !isManagedDownloadBlocked,
                    ManageGlyph = ResolveManageGlyph(definition, entry),
                    ManageToolTip = ResolveManageToolTip(definition, entry, isManagedDownloadBlocked),
                    ManageVisibility = entry.CanManage ? Visibility.Visible : Visibility.Collapsed,
                    CapabilityBadgesVisibility = Visibility.Visible,
                    CountBadgesVisibility = Visibility.Collapsed,
                    CardBackground = isCurrent ? CreateBrush("#F7EAD3") : CreateBrush("#FFFDF9F3"),
                    CardBorderBrush = isCurrent ? CreateBrush("#C28B2C") : CreateBrush("#D4C1A3"),
                };
            }
        }
    }

    private ModelItemViewModel CreatePublisherBrowserItem(ModelPublisherGroup publisher)
    {
        var modelCountText = FormatModelCount(publisher.ModelCount);
        var variantCountText = FormatVariantCount(publisher.VariantCount);
        var openText = UiText.Translate(SelectedLanguageCode, "settings.browser.open_publisher");

        return new ModelItemViewModel
        {
            ActionTag = $"publisher:{publisher.Key}",
            DisplayName = publisher.DisplayName,
            SourceText = UiText.Translate(SelectedLanguageCode, "settings.browser.publisher"),
            StatusText = openText,
            InfoText = $"{publisher.DisplayName}\n{modelCountText} · {variantCountText}",
            InfoVisibility = Visibility.Collapsed,
            CanSelect = !_isBusy,
            CanManage = !_isBusy,
            ManageGlyph = "\uE72A",
            ManageToolTip = openText,
            ManageVisibility = Visibility.Visible,
            CapabilityBadgesVisibility = Visibility.Collapsed,
            CountBadgesVisibility = Visibility.Visible,
            ModelCountText = modelCountText,
            VariantCountText = variantCountText,
            ModelCountVisibility = Visibility.Visible,
            VariantCountVisibility = Visibility.Visible,
            CardBackground = CreateBrush("#FFFDF9F3"),
            CardBorderBrush = CreateBrush("#D4C1A3"),
        };
    }

    private ModelItemViewModel CreateModelFamilyBrowserItem(
        ModelPublisherGroup publisher,
        ModelFamilyGroup family)
    {
        var variantCountText = FormatVariantCount(family.Definitions.Count);
        var openText = UiText.Translate(SelectedLanguageCode, "settings.browser.open_variants");
        var variantNames = string.Join(", ", family.Definitions.Select(definition =>
            definition.VariantDisplayName ?? definition.DisplayName));

        return new ModelItemViewModel
        {
            ActionTag = $"family:{publisher.Key}|{family.Key}",
            DisplayName = family.DisplayName,
            SourceText = UiText.Translate(SelectedLanguageCode, "settings.browser.model_family"),
            StatusText = openText,
            InfoText = $"{variantCountText}\n{variantNames}",
            CanSelect = !_isBusy,
            CanManage = !_isBusy,
            ManageGlyph = "\uE72A",
            ManageToolTip = openText,
            ManageVisibility = Visibility.Visible,
            CapabilityBadgesVisibility = Visibility.Collapsed,
            CountBadgesVisibility = Visibility.Visible,
            VariantCountText = variantCountText,
            ModelCountVisibility = Visibility.Collapsed,
            VariantCountVisibility = Visibility.Visible,
            CardBackground = CreateBrush("#FFFDF9F3"),
            CardBorderBrush = CreateBrush("#D4C1A3"),
        };
    }

    private IReadOnlyList<ModelPublisherGroup> BuildModelPublisherGroups()
    {
        return _modelCatalog.GetAvailableForCurrentOperatingSystem()
            .GroupBy(definition => definition.PublisherKey, StringComparer.OrdinalIgnoreCase)
            .Select(publisherDefinitions =>
            {
                var definitions = publisherDefinitions.ToList();
                var families = definitions
                    .GroupBy(definition => definition.ModelFamilyKey, StringComparer.OrdinalIgnoreCase)
                    .Select(familyDefinitions =>
                    {
                        var familyModels = familyDefinitions.ToList();
                        return new ModelFamilyGroup(
                            familyDefinitions.Key,
                            familyModels[0].ModelFamilyDisplayName,
                            familyModels);
                    })
                    .OrderBy(family => family.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                return new ModelPublisherGroup(
                    publisherDefinitions.Key,
                    definitions[0].PublisherDisplayName,
                    families);
            })
            .OrderBy(publisher => publisher.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private string FormatModelCount(int count)
    {
        var key = count == 1 ? "settings.count.model.one" : "settings.count.model.many";
        return UiText.Translate(SelectedLanguageCode, key, count);
    }

    private string FormatVariantCount(int count)
    {
        var key = count == 1 ? "settings.count.variant.one" : "settings.count.variant.many";
        return UiText.Translate(SelectedLanguageCode, key, count);
    }

    private IEnumerable<ModelListEntry> BuildModelEntries(TranscriptionModelDefinition definition)
    {
        var managedPath = _modelPackageManager.ResolveManagedModelPath(definition);
        yield return new ModelListEntry(
            ModelKey: definition.Key,
            SourceId: LocalModelSourceCatalog.GetManagedSourceId(definition.Key),
            SourceKind: ModelSourceKind.Managed,
            DisplayName: definition.DisplayName,
            SourceText: UiText.Translate(SelectedLanguageCode, "settings.model.source.managed"),
            DirectoryPath: managedPath,
            IsAvailableLocally: !string.IsNullOrWhiteSpace(managedPath),
            CanManage: true,
            IsDownloading: _modelDownloadCoordinator.IsDownloading(definition.Key));

        foreach (var source in _modelSourceCatalog
                     .GetSources(definition, _settings, SelectedLanguageCode)
                     .Where(source => source.SourceKind != ModelSourceKind.Managed))
        {
            yield return new ModelListEntry(
                ModelKey: definition.Key,
                SourceId: source.Id,
                SourceKind: source.SourceKind,
                DisplayName: definition.DisplayName,
                SourceText: source.DisplayName,
                DirectoryPath: source.DirectoryPath,
                IsAvailableLocally: true,
                CanManage: source.SourceKind == ModelSourceKind.CustomDirectory,
                IsDownloading: false);
        }
    }

    private string BuildStatusText(TranscriptionModelDefinition definition, ModelListEntry entry, bool isCurrent)
    {
        if (!definition.SupportsCurrentOperatingSystem())
        {
            return UiText.Translate(SelectedLanguageCode, "settings.model.windows_unsupported");
        }

        if (entry.IsDownloading)
        {
            return UiText.Translate(SelectedLanguageCode, "settings.model.downloading");
        }

        if (definition.RuntimeKind == ModelRuntimeKind.Qwen && entry.IsAvailableLocally && !_backendCapabilities.HasQwenRuntime)
        {
            return BuildQwenRuntimeMissingText();
        }

        if (definition.RuntimeKind == ModelRuntimeKind.CrisperWhisper
            && entry.IsAvailableLocally
            && !_backendCapabilities.HasCrisperWhisperRuntime)
        {
            return UiText.Translate(SelectedLanguageCode, "settings.model.runtime_optional_crisperwhisper");
        }

        if (definition.RuntimeKind == ModelRuntimeKind.Phonon
            && entry.IsAvailableLocally
            && !_backendCapabilities.HasPhononRuntime)
        {
            return UiText.Translate(SelectedLanguageCode, "settings.model.runtime_optional_phonon");
        }

        if (string.Equals(definition.Key, "voxtral-mini-4b-realtime", StringComparison.OrdinalIgnoreCase)
            && !_backendCapabilities.HasVoxtralRealtimeRuntime)
        {
            return UiText.Translate(SelectedLanguageCode, "settings.model.runtime_missing_voxtral_realtime");
        }

        if (isCurrent)
        {
            return entry.SourceKind switch
            {
                ModelSourceKind.Managed => UiText.Translate(SelectedLanguageCode, "settings.model.current_managed"),
                ModelSourceKind.SharedCache => UiText.Translate(SelectedLanguageCode, "settings.model.current_shared_cache"),
                ModelSourceKind.CustomDirectory => UiText.Translate(SelectedLanguageCode, "settings.model.current_custom"),
                _ => UiText.Translate(SelectedLanguageCode, "settings.model.current"),
            };
        }

        return entry.SourceKind switch
        {
            ModelSourceKind.Managed when entry.IsAvailableLocally => UiText.Translate(SelectedLanguageCode, "settings.model.downloaded"),
            ModelSourceKind.Managed => UiText.Translate(SelectedLanguageCode, "settings.model.download_first"),
            ModelSourceKind.SharedCache => UiText.Translate(SelectedLanguageCode, "settings.model.shared_cache"),
            ModelSourceKind.CustomDirectory => UiText.Translate(SelectedLanguageCode, "settings.model.custom_source"),
            _ => UiText.Translate(SelectedLanguageCode, "settings.model.not_downloaded"),
        };
    }

    private string BuildInfoText(TranscriptionModelDefinition definition, ModelListEntry entry)
    {
        var sizeLabel = entry.IsAvailableLocally
            ? _modelPackageManager.GetSizeLabel(definition, entry.DirectoryPath)
            : definition.SizeLabel;

        var details = SelectedLanguageCode == "en"
            ? definition.DetailsEn
            : definition.DetailsFr;

        if (SelectedLanguageCode == "en")
        {
            var sizePrefix = entry.IsAvailableLocally ? "Installed size" : "Approx. size";
            return string.IsNullOrWhiteSpace(details)
                ? $"{sizePrefix}: {sizeLabel}"
                : $"{sizePrefix}: {sizeLabel}\n{details}";
        }

        var frenchPrefix = entry.IsAvailableLocally ? "Taille installée" : "Taille estimée";
        frenchPrefix = frenchPrefix.Replace("installÃ©e", "install\u00e9e").Replace("estimÃ©e", "estim\u00e9e");
        frenchPrefix = entry.IsAvailableLocally ? "Taille install\u00e9e" : "Taille estim\u00e9e";
        return string.IsNullOrWhiteSpace(details)
            ? $"{frenchPrefix} : {sizeLabel}"
            : $"{frenchPrefix} : {sizeLabel}\n{details}";
    }

    private string ResolveManageGlyph(TranscriptionModelDefinition definition, ModelListEntry entry)
    {
        if (!entry.CanManage)
        {
            return string.Empty;
        }

        if (entry.IsDownloading)
        {
            return "\uE895";
        }

        if (entry.SourceKind == ModelSourceKind.Managed)
        {
            if (definition.RuntimeKind == ModelRuntimeKind.Qwen && entry.IsAvailableLocally && !_backendCapabilities.HasQwenRuntime)
            {
                return "\uE767";
            }

            if (definition.RuntimeKind == ModelRuntimeKind.CrisperWhisper
                && entry.IsAvailableLocally
                && !_backendCapabilities.HasCrisperWhisperRuntime)
            {
                return "\uE767";
            }

            if (definition.RuntimeKind == ModelRuntimeKind.Phonon
                && entry.IsAvailableLocally
                && !_backendCapabilities.HasPhononRuntime)
            {
                return "\uE767";
            }

            return entry.IsAvailableLocally ? "\uE74D" : "\uE896";
        }

        return "\uE74D";
    }

    private string ResolveManageToolTip(TranscriptionModelDefinition definition, ModelListEntry entry, bool isManagedDownloadBlocked)
    {
        if (!entry.CanManage)
        {
            return string.Empty;
        }

        if (entry.IsDownloading)
        {
            return UiText.Translate(SelectedLanguageCode, "settings.model.downloading");
        }

        if (entry.SourceKind == ModelSourceKind.CustomDirectory)
        {
            return UiText.Translate(SelectedLanguageCode, "settings.action.remove_source");
        }

        if (isManagedDownloadBlocked)
        {
            return UiText.Translate(SelectedLanguageCode, "settings.status.download_locked");
        }

        if (definition.RuntimeKind == ModelRuntimeKind.Qwen && entry.IsAvailableLocally && !_backendCapabilities.HasQwenRuntime)
        {
            return UiText.Translate(SelectedLanguageCode, "settings.model.install_runtime");
        }

        if (definition.RuntimeKind == ModelRuntimeKind.CrisperWhisper
            && entry.IsAvailableLocally
            && !_backendCapabilities.HasCrisperWhisperRuntime)
        {
            return UiText.Translate(SelectedLanguageCode, "settings.model.install_runtime_crisperwhisper");
        }

        if (definition.RuntimeKind == ModelRuntimeKind.Phonon
            && entry.IsAvailableLocally
            && !_backendCapabilities.HasPhononRuntime)
        {
            return UiText.Translate(SelectedLanguageCode, "settings.model.install_runtime_phonon");
        }

        return UiText.Translate(
            SelectedLanguageCode,
            entry.IsAvailableLocally ? "settings.action.delete" : "settings.action.download");
    }

    private void ApplyLocalizedText()
    {
        Title = UiText.Translate(SelectedLanguageCode, "settings.title");
        LanguageSectionTitleTextBlock.Text = UiText.Translate(SelectedLanguageCode, "settings.language");
        AudioSourceSectionTitleTextBlock.Text = UiText.Translate(SelectedLanguageCode, "settings.audio_source");
        HotkeySectionTitleTextBlock.Text = UiText.Translate(SelectedLanguageCode, "settings.hotkey");
        TranscriptOutputSectionTitleTextBlock.Text = UiText.Translate(SelectedLanguageCode, "settings.transcript_window");
        TranscriptWindowCheckBoxTextBlock.Text = UiText.Translate(SelectedLanguageCode, "settings.transcript_window_enabled");
        AutoOpenTranscriptWindowCheckBoxTextBlock.Text = UiText.Translate(SelectedLanguageCode, "settings.transcript_window_auto_open");
        CurrentModelSectionTitleTextBlock.Text = UiText.Translate(SelectedLanguageCode, "settings.current_model");
        HardwareAccelerationSectionTitleTextBlock.Text = UiText.Translate(SelectedLanguageCode, "settings.hardware.title");
        OpenAppDataFolderButtonTextBlock.Text = UiText.Translate(SelectedLanguageCode, "settings.action.open_appdata");
        OpenAppDataFolderButton.ToolTip = UiText.Translate(
            SelectedLanguageCode,
            "settings.action.open_appdata_hint",
            _appEnvironment.LocalAppDataDirectory);
        InstallNvidiaRuntimeButton.Content = UiText.Translate(SelectedLanguageCode, "settings.runtime.install_nvidia");
        InstallNvidiaRuntimeButton.ToolTip = UiText.Translate(SelectedLanguageCode, "settings.runtime.install_nvidia_hint");
        InstallAmdRuntimeButton.Content = UiText.Translate(SelectedLanguageCode, "settings.runtime.install_amd");
        InstallAmdRuntimeButton.ToolTip = UiText.Translate(SelectedLanguageCode, "settings.runtime.install_amd_hint");
        InstallCpuRuntimeButton.Content = UiText.Translate(SelectedLanguageCode, "settings.runtime.install_cpu");
        InstallCpuRuntimeButton.ToolTip = UiText.Translate(SelectedLanguageCode, "settings.runtime.install_cpu_hint");
        ChooseModelButton.Content = UiText.Translate(SelectedLanguageCode, "settings.action.choose_model");
        TraditionalModeToggleButton.Content = UiText.Translate(SelectedLanguageCode, "settings.mode.file");
        RealtimeModeToggleButton.Content = UiText.Translate(SelectedLanguageCode, "settings.mode.realtime");
        ModelsSectionTitleTextBlock.Text = UiText.Translate(SelectedLanguageCode, "settings.models");
        AddLocalModelButton.ToolTip = UiText.Translate(SelectedLanguageCode, "settings.action.add_local_model_hint");
        BackToSettingsButton.ToolTip = UiText.Translate(SelectedLanguageCode, "settings.action.back");
        ConfirmationTitleTextBlock.Text = UiText.Translate(SelectedLanguageCode, "settings.confirm.title");
        ConfirmationConfirmButton.Content = UiText.Translate(SelectedLanguageCode, "settings.confirm.accept");
        ConfirmationCancelButton.Content = UiText.Translate(SelectedLanguageCode, "settings.confirm.reject");
        UpdateHotkeyCaptureState();
        FrenchLanguageComboBoxItem.Content = UiText.Translate(SelectedLanguageCode, "settings.language.fr");
        EnglishLanguageComboBoxItem.Content = UiText.Translate(SelectedLanguageCode, "settings.language.en");
        RefreshHardwareAccelerationOptions();
        UpdateRuntimeInstallButtonState();
        RefreshAudioSources();
        UpdateViewState();
        UpdateTranscriptWindowOptionsState();
    }

    private void SetStatus(string key, params object[] args)
    {
        SetStatusText(UiText.Translate(SelectedLanguageCode, key, args));
    }

    private void SetStatusText(string text)
    {
        SettingsStatusTextBlock.Text = text;
        RequestWindowMetricsUpdate();
    }

    private Task<bool> ConfirmAsync(string message)
    {
        if (_confirmationCompletionSource is not null)
        {
            return Task.FromResult(false);
        }

        ConfirmationTitleTextBlock.Text = UiText.Translate(SelectedLanguageCode, "settings.confirm.title");
        ConfirmationMessageTextBlock.Text = message;
        ConfirmationConfirmButton.Content = UiText.Translate(SelectedLanguageCode, "settings.confirm.accept");
        ConfirmationCancelButton.Content = UiText.Translate(SelectedLanguageCode, "settings.confirm.reject");
        ConfirmationOverlay.Visibility = Visibility.Visible;
        ConfirmationConfirmButton.Focus();

        _confirmationCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _confirmationCompletionSource.Task;
    }

    private void ConfirmationConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        CloseConfirmationOverlay(true);
    }

    private void ConfirmationCancelButton_Click(object sender, RoutedEventArgs e)
    {
        CloseConfirmationOverlay(false);
    }

    private void CloseConfirmationOverlay(bool accepted)
    {
        var completionSource = _confirmationCompletionSource;
        if (completionSource is null)
        {
            ConfirmationOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        _confirmationCompletionSource = null;
        ConfirmationOverlay.Visibility = Visibility.Collapsed;
        completionSource.TrySetResult(accepted);
    }

    private void RefreshAudioSources()
    {
        _ignoreAudioSourceSelectionChange = true;
        AudioSourceComboBox.ItemsSource = _audioSourceCatalog.GetAvailableSources(SelectedLanguageCode);
        AudioSourceComboBox.SelectedValue = _audioSourceCatalog.GetByIdOrDefault(_settings.AudioSourceId, SelectedLanguageCode).Id;
        _ignoreAudioSourceSelectionChange = false;
    }

    private void ActivatePythonRuntime(
        string? pythonPath,
        bool useGpuAcceleration,
        string? hardwareAccelerationDevice = null,
        string? automaticHardwareAccelerationDevice = null)
    {
        var normalizedPath = _pythonRuntimeLocator.NormalizeManagedPythonPath(pythonPath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            throw new FileNotFoundException("Le runtime Python s\u00e9lectionn\u00e9 est incomplet ou introuvable.");
        }

        var normalizedHardwareAccelerationDevice = useGpuAcceleration
            ? HardwareAccelerationPreference.Normalize(
                hardwareAccelerationDevice ?? HardwareAccelerationPreference.Auto)
            : HardwareAccelerationPreference.Cpu;
        var normalizedAutomaticHardwareAccelerationDevice = automaticHardwareAccelerationDevice is null
            ? _settings.AutomaticHardwareAccelerationDevice
            : NormalizeAutomaticHardwareAccelerationDevice(automaticHardwareAccelerationDevice);
        var pythonRuntimeChanged = !string.Equals(
            _settings.PythonRuntimePath,
            normalizedPath,
            StringComparison.OrdinalIgnoreCase);
        var hardwareAccelerationChanged = !string.Equals(
            _settings.HardwareAccelerationDevice,
            normalizedHardwareAccelerationDevice,
            StringComparison.OrdinalIgnoreCase);
        var automaticHardwareAccelerationChanged = !string.Equals(
            _settings.AutomaticHardwareAccelerationDevice,
            normalizedAutomaticHardwareAccelerationDevice,
            StringComparison.OrdinalIgnoreCase);

        _settings.PythonRuntimePath = normalizedPath;
        _settings.HardwareAccelerationDevice = normalizedHardwareAccelerationDevice;
        _settings.AutomaticHardwareAccelerationDevice = normalizedAutomaticHardwareAccelerationDevice;
        _pythonRuntimeLocator.ConfigureCustomPythonPath(normalizedPath);
        _settingsService.Save(_settings);
        RefreshHardwareAccelerationOptions();

        if (pythonRuntimeChanged || hardwareAccelerationChanged || automaticHardwareAccelerationChanged)
        {
            PythonRuntimeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ApplyCachedGpuDiagnostics()
    {
        if (_backendCapabilityService.CachedGpuDiagnostics is { } cachedDiagnostics)
        {
            _gpuDiagnostics = cachedDiagnostics;
        }

        RefreshHardwareAccelerationOptions();
    }

    private void RefreshHardwareAccelerationOptions()
    {
        var selectedDevice = HardwareAccelerationPreference.Normalize(_settings.HardwareAccelerationDevice);
        _settings.HardwareAccelerationDevice = selectedDevice;
        var hardwareAccelerationWasReset = false;
        var displayControllers = _gpuDiagnostics?.DisplayControllers ?? [];
        var torchDevices = BuildRuntimeAwareGpuDevices(
            _gpuDiagnostics?.Devices ?? [],
            displayControllers);

        _ignoreHardwareAccelerationSelectionChange = true;
        _hardwareAccelerationOptions.Clear();
        _hardwareAccelerationOptions.Add(new HardwareAccelerationOptionViewModel(
            HardwareAccelerationPreference.Auto,
            BuildAutomaticHardwareAccelerationLabel(torchDevices, displayControllers),
            CanSelect: true));
        _hardwareAccelerationOptions.Add(new HardwareAccelerationOptionViewModel(
            HardwareAccelerationPreference.Cpu,
            UiText.Translate(SelectedLanguageCode, "settings.hardware.cpu"),
            CanSelect: true));

        var matchedTorchDeviceIndexes = new HashSet<int>();
        var displayGpuNumber = 1;

        if (displayControllers.Count > 0)
        {
            foreach (var controller in displayControllers)
            {
                var torchDevice = torchDevices.FirstOrDefault(device =>
                    !matchedTorchDeviceIndexes.Contains(device.Index)
                    && AreGpuNamesMatching(controller.Name, device.Name));

                if (torchDevice is not null)
                {
                    matchedTorchDeviceIndexes.Add(torchDevice.Index);
                    var isUsable = IsGpuDeviceUsable(torchDevice);
                    _hardwareAccelerationOptions.Add(new HardwareAccelerationOptionViewModel(
                        $"{HardwareAccelerationPreference.GpuPrefix}{torchDevice.Index}",
                        BuildGpuOptionLabel(torchDevice, unavailable: !isUsable, displayGpuNumber, controller.Name),
                        CanSelect: isUsable));
                }
                else
                {
                    _hardwareAccelerationOptions.Add(new HardwareAccelerationOptionViewModel(
                        $"display:{controller.Index}",
                        UiText.Translate(
                            SelectedLanguageCode,
                            "settings.hardware.gpu_windows_unavailable",
                            displayGpuNumber,
                            controller.Name),
                        CanSelect: false));
                }

                displayGpuNumber++;
            }
        }

        foreach (var device in torchDevices.Where(device => !matchedTorchDeviceIndexes.Contains(device.Index)))
        {
            var isUsable = IsGpuDeviceUsable(device);
            _hardwareAccelerationOptions.Add(new HardwareAccelerationOptionViewModel(
                $"{HardwareAccelerationPreference.GpuPrefix}{device.Index}",
                BuildGpuOptionLabel(device, unavailable: !isUsable, displayGpuNumber),
                CanSelect: isUsable));
            displayGpuNumber++;
        }

        if (HardwareAccelerationPreference.TryParseGpuIndex(selectedDevice, out var selectedGpuIndex)
            && !_hardwareAccelerationOptions.Any(option =>
                option.CanSelect
                && string.Equals(option.Id, selectedDevice, StringComparison.OrdinalIgnoreCase)))
        {
            if (_gpuDiagnostics?.Ok == true)
            {
                selectedDevice = HardwareAccelerationPreference.Auto;
                _settings.HardwareAccelerationDevice = selectedDevice;
                hardwareAccelerationWasReset = true;
            }
            else if (!_hardwareAccelerationOptions.Any(option =>
                         string.Equals(option.Id, selectedDevice, StringComparison.OrdinalIgnoreCase)))
            {
                _hardwareAccelerationOptions.Add(new HardwareAccelerationOptionViewModel(
                    selectedDevice,
                    UiText.Translate(SelectedLanguageCode, "settings.hardware.gpu_unavailable", selectedGpuIndex + 1),
                    CanSelect: false));
            }
        }

        HardwareAccelerationComboBox.SelectedValue = selectedDevice;
        _ignoreHardwareAccelerationSelectionChange = false;
        UpdateHardwareDiagnosticText();
        UpdateRuntimeInstallButtonState();
        RequestWindowMetricsUpdate();

        if (hardwareAccelerationWasReset)
        {
            _settingsService.Save(_settings);
            HardwareAccelerationChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private string BuildAutomaticHardwareAccelerationLabel(
        IReadOnlyList<GpuDeviceInfo> torchDevices,
        IReadOnlyList<GpuDeviceInfo> displayControllers)
    {
        if (_gpuDiagnostics is null)
        {
            return UiText.Translate(SelectedLanguageCode, "settings.hardware.auto");
        }

        if (!_gpuDiagnostics.Ok
            && !IsNvidiaRuntimeActive()
            && !IsAmdRuntimeActive()
            && !IsCpuRuntimeActive())
        {
            return UiText.Translate(SelectedLanguageCode, "settings.hardware.auto");
        }

        GpuDeviceInfo? automaticDevice = null;
        if (IsNvidiaRuntimeActive()
            && HardwareAccelerationPreference.TryParseGpuIndex(
                _settings.AutomaticHardwareAccelerationDevice,
                out var automaticGpuIndex))
        {
            automaticDevice = torchDevices.FirstOrDefault(
                device => device.Index == automaticGpuIndex && IsGpuDeviceUsable(device));
        }

        automaticDevice ??= torchDevices.FirstOrDefault(device => device.Index == 0 && IsGpuDeviceUsable(device))
                            ?? torchDevices.FirstOrDefault(IsGpuDeviceUsable);
        if (automaticDevice is not null)
        {
            var displayInfo = ResolveGpuDisplayInfo(automaticDevice, torchDevices, displayControllers);
            return BuildAutomaticGpuOptionLabel(
                automaticDevice,
                displayInfo?.DisplayIndex ?? automaticDevice.Index + 1,
                displayInfo?.DisplayName ?? automaticDevice.Name);
        }

        return UiText.Translate(SelectedLanguageCode, "settings.hardware.auto_cpu");
    }

    private List<GpuDeviceInfo> BuildRuntimeAwareGpuDevices(
        IReadOnlyList<GpuDeviceInfo> detectedTorchDevices,
        IReadOnlyList<GpuDeviceInfo> displayControllers)
    {
        if (IsCpuRuntimeActive())
        {
            return [];
        }

        var nvidiaRuntimeActive = IsNvidiaRuntimeActive();
        var amdRuntimeActive = IsAmdRuntimeActive();
        if (nvidiaRuntimeActive
            && _backendRuntimeInstallerService.CachedNvidiaRuntimePlan is { } nvidiaPlan)
        {
            return BuildNvidiaRuntimeAwareGpuDevices(detectedTorchDevices, nvidiaPlan);
        }

        if (!nvidiaRuntimeActive && !amdRuntimeActive)
        {
            return detectedTorchDevices.ToList();
        }

        var startupDiagnosticMatchesRuntime = nvidiaRuntimeActive
            ? !string.IsNullOrWhiteSpace(_gpuDiagnostics?.CudaVersion)
            : !string.IsNullOrWhiteSpace(_gpuDiagnostics?.HipVersion);
        if (startupDiagnosticMatchesRuntime && detectedTorchDevices.Count > 0)
        {
            return detectedTorchDevices.ToList();
        }

        var matchingControllers = displayControllers
            .Where(controller => nvidiaRuntimeActive ? IsNvidiaGpu(controller) : IsAmdGpu(controller))
            .ToArray();

        return matchingControllers
            .Select((controller, index) => new GpuDeviceInfo
            {
                Index = index,
                Name = controller.Name,
                TotalMemoryBytes = controller.TotalMemoryBytes,
                IsUsable = true,
                AdapterCompatibility = controller.AdapterCompatibility,
                PnpDeviceId = controller.PnpDeviceId,
                IsIntegrated = controller.IsIntegrated,
            })
            .ToList();
    }

    private List<GpuDeviceInfo> BuildNvidiaRuntimeAwareGpuDevices(
        IReadOnlyList<GpuDeviceInfo> detectedTorchDevices,
        NvidiaRuntimePlan plan)
    {
        var devices = detectedTorchDevices
            .Select(CloneGpuDevice)
            .ToDictionary(device => device.Index);

        foreach (var profile in plan.Gpus)
        {
            devices.TryGetValue(profile.Index, out var device);
            device ??= new GpuDeviceInfo
            {
                Index = profile.Index,
            };

            device.Name = string.IsNullOrWhiteSpace(device.Name) ? profile.Name : device.Name;
            device.TotalMemoryBytes ??= profile.TotalMemoryBytes;
            device.ComputeCapability ??= profile.ComputeCapability.ToString(2);

            if (_backendRuntimeInstallerService.GetInstalledNvidiaCudaRuntimePathForGpu(profile.Index) is not null)
            {
                // The compatible runtime may differ from the currently active one.
                // Selecting this entry switches environments before restarting the worker.
                device.IsUsable = true;
                device.Error = null;
            }

            devices[profile.Index] = device;
        }

        return devices.Values.OrderBy(device => device.Index).ToList();
    }

    private static GpuDeviceInfo CloneGpuDevice(GpuDeviceInfo device)
    {
        return new GpuDeviceInfo
        {
            Index = device.Index,
            Name = device.Name,
            TotalMemoryBytes = device.TotalMemoryBytes,
            ComputeCapability = device.ComputeCapability,
            IsUsable = device.IsUsable,
            Error = device.Error,
            AdapterCompatibility = device.AdapterCompatibility,
            PnpDeviceId = device.PnpDeviceId,
            IsIntegrated = device.IsIntegrated,
        };
    }

    private (int DisplayIndex, string DisplayName)? ResolveGpuDisplayInfo(
        GpuDeviceInfo targetDevice,
        IReadOnlyList<GpuDeviceInfo> torchDevices,
        IReadOnlyList<GpuDeviceInfo> displayControllers)
    {
        var matchedTorchDeviceIndexes = new HashSet<int>();
        var displayGpuNumber = 1;

        foreach (var controller in displayControllers)
        {
            var torchDevice = torchDevices.FirstOrDefault(device =>
                !matchedTorchDeviceIndexes.Contains(device.Index)
                && AreGpuNamesMatching(controller.Name, device.Name));

            if (torchDevice is not null)
            {
                matchedTorchDeviceIndexes.Add(torchDevice.Index);
                if (torchDevice.Index == targetDevice.Index)
                {
                    return (displayGpuNumber, controller.Name);
                }
            }

            displayGpuNumber++;
        }

        foreach (var device in torchDevices.Where(device => !matchedTorchDeviceIndexes.Contains(device.Index)))
        {
            if (device.Index == targetDevice.Index)
            {
                return (displayGpuNumber, device.Name);
            }

            displayGpuNumber++;
        }

        return null;
    }

    private string BuildAutomaticGpuOptionLabel(GpuDeviceInfo device, int displayIndex, string displayName)
    {
        var memoryLabel = FormatMemoryLabel(device.TotalMemoryBytes);
        return string.IsNullOrWhiteSpace(memoryLabel)
            ? UiText.Translate(SelectedLanguageCode, "settings.hardware.auto_gpu", displayIndex, displayName)
            : UiText.Translate(SelectedLanguageCode, "settings.hardware.auto_gpu_with_memory", displayIndex, displayName, memoryLabel);
    }

    private string BuildGpuOptionLabel(
        GpuDeviceInfo device,
        bool unavailable,
        int? displayIndexOverride = null,
        string? displayNameOverride = null)
    {
        var displayIndex = displayIndexOverride ?? device.Index + 1;
        var displayName = string.IsNullOrWhiteSpace(displayNameOverride)
            ? device.Name
            : displayNameOverride;
        var memoryLabel = FormatMemoryLabel(device.TotalMemoryBytes);
        if (unavailable)
        {
            return UiText.Translate(
                SelectedLanguageCode,
                "settings.hardware.gpu_runtime_incompatible",
                displayIndex,
                displayName);
        }

        return string.IsNullOrWhiteSpace(memoryLabel)
            ? UiText.Translate(SelectedLanguageCode, "settings.hardware.gpu", displayIndex, displayName)
            : UiText.Translate(SelectedLanguageCode, "settings.hardware.gpu_with_memory", displayIndex, displayName, memoryLabel);
    }

    private static bool AreGpuNamesMatching(string left, string right)
    {
        var normalizedLeft = NormalizeGpuName(left);
        var normalizedRight = NormalizeGpuName(right);
        if (string.IsNullOrWhiteSpace(normalizedLeft) || string.IsNullOrWhiteSpace(normalizedRight))
        {
            return false;
        }

        return normalizedLeft.Contains(normalizedRight, StringComparison.Ordinal)
               || normalizedRight.Contains(normalizedLeft, StringComparison.Ordinal)
               || AreGpuNameTokensMatching(left, right);
    }

    private static bool AreGpuNameTokensMatching(string left, string right)
    {
        var leftVendor = DetectGpuVendor(left);
        var rightVendor = DetectGpuVendor(right);
        if (!string.IsNullOrWhiteSpace(leftVendor)
            && !string.IsNullOrWhiteSpace(rightVendor)
            && !string.Equals(leftVendor, rightVendor, StringComparison.Ordinal))
        {
            return false;
        }

        var commonTokens = TokenizeGpuName(left)
            .Intersect(TokenizeGpuName(right), StringComparer.Ordinal)
            .ToArray();

        return commonTokens.Any(token => token.Length >= 3 && token.Any(char.IsDigit))
               || commonTokens.Length >= 2;
    }

    private static string DetectGpuVendor(string value)
    {
        var normalized = value.ToLowerInvariant();
        if (normalized.Contains("nvidia", StringComparison.Ordinal) || normalized.Contains("ven_10de", StringComparison.Ordinal))
        {
            return "nvidia";
        }

        if (normalized.Contains("amd", StringComparison.Ordinal)
            || normalized.Contains("advanced micro devices", StringComparison.Ordinal)
            || normalized.Contains("radeon", StringComparison.Ordinal)
            || normalized.Contains("ven_1002", StringComparison.Ordinal))
        {
            return "amd";
        }

        if (normalized.Contains("intel", StringComparison.Ordinal) || normalized.Contains("ven_8086", StringComparison.Ordinal))
        {
            return "intel";
        }

        return string.Empty;
    }

    private static HashSet<string> TokenizeGpuName(string value)
    {
        var normalized = new string(
            value
                .ToLowerInvariant()
                .Select(character => char.IsLetterOrDigit(character) ? character : ' ')
                .ToArray());

        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 2 && !IsGpuNameNoiseToken(token))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool IsGpuNameNoiseToken(string token)
    {
        return token is "nvidia"
            or "geforce"
            or "quadro"
            or "rtx"
            or "gtx"
            or "amd"
            or "ati"
            or "radeon"
            or "intel"
            or "iris"
            or "uhd"
            or "graphics"
            or "graphic"
            or "laptop"
            or "mobile"
            or "gpu"
            or "super"
            or "ti";
    }

    private static string NormalizeGpuName(string value)
    {
        var filtered = new string(
            value
                .ToLowerInvariant()
                .Where(char.IsLetterOrDigit)
                .ToArray());

        return filtered
            .Replace("nvidia", string.Empty, StringComparison.Ordinal)
            .Replace("amd", string.Empty, StringComparison.Ordinal)
            .Replace("intel", string.Empty, StringComparison.Ordinal);
    }

    private void UpdateHardwareDiagnosticText()
    {
        if (_gpuDiagnostics is null)
        {
            GpuDiagnosticTextBlock.Text = UiText.Translate(SelectedLanguageCode, "settings.hardware.diagnostic_waiting");
            GpuDiagnosticTextBlock.ToolTip = null;
            return;
        }

        if (IsNvidiaRuntimeActive()
            && (!_gpuDiagnostics.Ok || string.IsNullOrWhiteSpace(_gpuDiagnostics.CudaVersion)))
        {
            GpuDiagnosticTextBlock.Text = UiText.Translate(
                SelectedLanguageCode,
                "settings.hardware.diagnostic_nvidia_runtime_selected");
            GpuDiagnosticTextBlock.ToolTip = null;
            return;
        }

        if (IsAmdRuntimeActive()
            && (!_gpuDiagnostics.Ok || string.IsNullOrWhiteSpace(_gpuDiagnostics.HipVersion)))
        {
            GpuDiagnosticTextBlock.Text = UiText.Translate(
                SelectedLanguageCode,
                "settings.hardware.diagnostic_amd_runtime_selected");
            GpuDiagnosticTextBlock.ToolTip = null;
            return;
        }

        if (IsCpuRuntimeActive()
            && (!_gpuDiagnostics.Ok
                || !string.IsNullOrWhiteSpace(_gpuDiagnostics.CudaVersion)
                || !string.IsNullOrWhiteSpace(_gpuDiagnostics.HipVersion)))
        {
            GpuDiagnosticTextBlock.Text = UiText.Translate(
                SelectedLanguageCode,
                "settings.hardware.diagnostic_cpu_runtime_selected");
            GpuDiagnosticTextBlock.ToolTip = null;
            return;
        }

        if (!_gpuDiagnostics.Ok)
        {
            if (_gpuDiagnostics.DisplayControllers.Count > 0)
            {
                GpuDiagnosticTextBlock.Text = UiText.Translate(
                    SelectedLanguageCode,
                    "settings.hardware.diagnostic_runtime_missing_with_gpu",
                    _gpuDiagnostics.DisplayControllers.Count);
                GpuDiagnosticTextBlock.ToolTip = _gpuDiagnostics.Error
                                                 ?? UiText.Translate(SelectedLanguageCode, "settings.hardware.diagnostic_unknown_error");
                return;
            }

            GpuDiagnosticTextBlock.Text = UiText.Translate(
                SelectedLanguageCode,
                "settings.hardware.diagnostic_unavailable",
                _gpuDiagnostics.Error ?? UiText.Translate(SelectedLanguageCode, "settings.hardware.diagnostic_unknown_error"));
            GpuDiagnosticTextBlock.ToolTip = _gpuDiagnostics.Error;
            return;
        }

        var backendLabel = BuildBackendLabel(_gpuDiagnostics);
        var torchLabel = string.IsNullOrWhiteSpace(_gpuDiagnostics.TorchVersion)
            ? "PyTorch"
            : $"PyTorch {_gpuDiagnostics.TorchVersion}";

        var usableDeviceCount = _gpuDiagnostics.Devices.Count(IsGpuDeviceUsable);
        if (_gpuDiagnostics.CudaAvailable
            && _gpuDiagnostics.DeviceCount > 0
            && usableDeviceCount == 0)
        {
            GpuDiagnosticTextBlock.Text = UiText.Translate(
                SelectedLanguageCode,
                "settings.hardware.diagnostic_gpu_incompatible");
            GpuDiagnosticTextBlock.ToolTip = string.Join(
                Environment.NewLine,
                _gpuDiagnostics.Devices
                    .Where(device => !string.IsNullOrWhiteSpace(device.Error))
                    .Select(device => $"{device.Name}: {device.Error}"));
            return;
        }

        GpuDiagnosticTextBlock.Text = usableDeviceCount > 0
            ? UiText.Translate(
                SelectedLanguageCode,
                "settings.hardware.diagnostic_gpu",
                usableDeviceCount,
                backendLabel,
                torchLabel)
            : UiText.Translate(
                SelectedLanguageCode,
                "settings.hardware.diagnostic_cpu",
                backendLabel,
                torchLabel);
        GpuDiagnosticTextBlock.ToolTip = null;
    }

    private void RequestWindowMetricsUpdate()
    {
        if (!IsLoaded)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(
            () => UpdateWindowMetrics(animated: false),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void UpdateRuntimeInstallButtonState()
    {
        var diagnosticsReady = _gpuDiagnostics is not null
                               && (_gpuDiagnostics.Ok || _gpuDiagnostics.DisplayControllers.Count > 0);
        var hasAmdGpu = diagnosticsReady && HasAmdGpu(_gpuDiagnostics);
        var hasSupportedAmdRocmGpu = diagnosticsReady && HasSupportedAmdRocmGpu(_gpuDiagnostics);
        var hasNvidiaGpu = diagnosticsReady && HasNvidiaGpu(_gpuDiagnostics);
        var amdOperatingSystemSupported = _backendRuntimeInstallerService.IsAmdRocmOperatingSystemSupported();
        var amdRuntimeActive = IsAmdRuntimeActive();
        var amdRuntimeInstalled = _backendRuntimeInstallerService.IsAmdRocmRuntimeInstalled();
        var nvidiaRuntimeActive = IsNvidiaRuntimeActive();
        var nvidiaRuntimeInstalled = _backendRuntimeInstallerService.IsNvidiaCudaRuntimeInstalled();
        var requiredNvidiaRuntimesInstalled = _backendRuntimeInstallerService.AreRequiredNvidiaCudaRuntimesInstalled();
        var nvidiaRuntimeNeedsRepair = nvidiaRuntimeActive
                                       && _gpuDiagnostics?.CudaAvailable == true
                                       && _gpuDiagnostics.DeviceCount > 0
                                       && !_gpuDiagnostics.Devices.Any(IsGpuDeviceUsable);
        var cpuRuntimeActive = IsCpuRuntimeActive();
        var cpuRuntimeInstalled = _backendRuntimeInstallerService.IsCpuRuntimeInstalled();
        var buttonsCanChange = !_isBusy;

        InstallAmdRuntimeButton.IsEnabled = buttonsCanChange
                                            && !amdRuntimeActive
                                            && (amdRuntimeInstalled
                                                || (diagnosticsReady
                                                    && amdOperatingSystemSupported
                                                    && hasSupportedAmdRocmGpu));
        InstallAmdRuntimeButton.ToolTip = BuildAmdRuntimeInstallToolTip(
            diagnosticsReady,
            amdOperatingSystemSupported,
            hasAmdGpu,
            hasSupportedAmdRocmGpu,
            amdRuntimeActive,
            amdRuntimeInstalled);

        InstallNvidiaRuntimeButton.IsEnabled = buttonsCanChange
                                               && (!nvidiaRuntimeActive
                                                   || !requiredNvidiaRuntimesInstalled
                                                   || nvidiaRuntimeNeedsRepair)
                                               && (nvidiaRuntimeInstalled
                                                   || (diagnosticsReady && hasNvidiaGpu));
        InstallNvidiaRuntimeButton.ToolTip = BuildNvidiaRuntimeInstallToolTip(
            diagnosticsReady,
            hasNvidiaGpu,
            nvidiaRuntimeActive,
            nvidiaRuntimeInstalled,
            requiredNvidiaRuntimesInstalled,
            nvidiaRuntimeNeedsRepair);

        InstallCpuRuntimeButton.IsEnabled = buttonsCanChange && !cpuRuntimeActive;
        InstallCpuRuntimeButton.ToolTip = BuildCpuRuntimeInstallToolTip(
            cpuRuntimeActive,
            cpuRuntimeInstalled);
    }

    private string BuildAmdRuntimeInstallToolTip(
        bool diagnosticsReady,
        bool operatingSystemSupported,
        bool hasAmdGpu,
        bool hasSupportedAmdRocmGpu,
        bool amdRuntimeActive,
        bool amdRuntimeInstalled)
    {
        if (amdRuntimeActive)
        {
            return UiText.Translate(SelectedLanguageCode, "settings.runtime.install_amd_ready");
        }

        if (amdRuntimeInstalled)
        {
            return UiText.Translate(SelectedLanguageCode, "settings.runtime.install_amd_activate_hint");
        }

        if (!diagnosticsReady)
        {
            return UiText.Translate(SelectedLanguageCode, "settings.runtime.install_waiting_gpu_diagnostic");
        }

        if (!operatingSystemSupported)
        {
            return UiText.Translate(SelectedLanguageCode, "settings.runtime.install_amd_unsupported_windows");
        }

        if (!hasAmdGpu)
        {
            return UiText.Translate(SelectedLanguageCode, "settings.runtime.install_amd_no_amd");
        }

        if (!hasSupportedAmdRocmGpu)
        {
            return UiText.Translate(SelectedLanguageCode, "settings.runtime.install_amd_unsupported_amd");
        }

        return UiText.Translate(SelectedLanguageCode, "settings.runtime.install_amd_hint");
    }

    private string BuildNvidiaRuntimeInstallToolTip(
        bool diagnosticsReady,
        bool hasNvidiaGpu,
        bool nvidiaRuntimeActive,
        bool nvidiaRuntimeInstalled,
        bool requiredNvidiaRuntimesInstalled,
        bool nvidiaRuntimeNeedsRepair)
    {
        if (nvidiaRuntimeActive && requiredNvidiaRuntimesInstalled && !nvidiaRuntimeNeedsRepair)
        {
            return UiText.Translate(SelectedLanguageCode, "settings.runtime.install_nvidia_ready");
        }

        if (nvidiaRuntimeNeedsRepair || (nvidiaRuntimeActive && !requiredNvidiaRuntimesInstalled))
        {
            return UiText.Translate(SelectedLanguageCode, "settings.runtime.install_nvidia_repair_hint");
        }

        if (nvidiaRuntimeInstalled)
        {
            return UiText.Translate(SelectedLanguageCode, "settings.runtime.install_nvidia_activate_hint");
        }

        if (!diagnosticsReady)
        {
            return UiText.Translate(SelectedLanguageCode, "settings.runtime.install_waiting_gpu_diagnostic");
        }

        if (!hasNvidiaGpu && !nvidiaRuntimeInstalled)
        {
            return UiText.Translate(SelectedLanguageCode, "settings.runtime.install_nvidia_no_nvidia");
        }

        return UiText.Translate(SelectedLanguageCode, "settings.runtime.install_nvidia_hint");
    }

    private string BuildCpuRuntimeInstallToolTip(bool cpuRuntimeActive, bool cpuRuntimeInstalled)
    {
        if (cpuRuntimeActive)
        {
            return UiText.Translate(SelectedLanguageCode, "settings.runtime.install_cpu_ready");
        }

        return cpuRuntimeInstalled
            ? UiText.Translate(SelectedLanguageCode, "settings.runtime.install_cpu_activate_hint")
            : UiText.Translate(SelectedLanguageCode, "settings.runtime.install_cpu_hint");
    }

    private bool IsAmdRuntimeActive()
    {
        return _backendRuntimeInstallerService.IsAmdRocmRuntimePath(GetEffectivePythonRuntimePath())
               && _backendRuntimeInstallerService.IsAmdRocmRuntimeInstalled();
    }

    private static bool IsGpuDeviceUsable(GpuDeviceInfo device)
    {
        // IsUsable is nullable so diagnostics produced by an older bundled
        // model_manager remain compatible.  New diagnostics always set it.
        return device.IsUsable != false && string.IsNullOrWhiteSpace(device.Error);
    }

    private bool IsNvidiaRuntimeActive()
    {
        return _backendRuntimeInstallerService.IsInstalledNvidiaCudaRuntimePath(GetEffectivePythonRuntimePath());
    }

    private bool IsCpuRuntimeActive()
    {
        return _backendRuntimeInstallerService.IsCpuRuntimePath(GetEffectivePythonRuntimePath())
               && _backendRuntimeInstallerService.IsCpuRuntimeInstalled();
    }

    private string GetEffectivePythonRuntimePath()
    {
        return !string.IsNullOrWhiteSpace(_settings.PythonRuntimePath)
            ? _settings.PythonRuntimePath
            : _pythonRuntimeLocator.ResolvePythonPath();
    }

    private static bool HasAmdGpu(GpuDiagnostics? diagnostics)
    {
        if (diagnostics is null)
        {
            return false;
        }

        return diagnostics.DisplayControllers.Any(IsAmdGpu)
               || diagnostics.Devices.Any(IsAmdGpu);
    }

    private static bool HasNvidiaGpu(GpuDiagnostics? diagnostics)
    {
        if (diagnostics is null)
        {
            return false;
        }

        return diagnostics.DisplayControllers.Any(IsNvidiaGpu)
               || diagnostics.Devices.Any(IsNvidiaGpu);
    }

    private static bool HasSupportedAmdRocmGpu(GpuDiagnostics? diagnostics)
    {
        if (diagnostics is null)
        {
            return false;
        }

        return diagnostics.DisplayControllers.Any(IsSupportedAmdRocmGpu)
               || diagnostics.Devices.Any(IsSupportedAmdRocmGpu);
    }

    private static bool IsAmdGpu(GpuDeviceInfo device)
    {
        return ContainsAmdGpuMarker(device.Name)
               || ContainsAmdGpuMarker(device.AdapterCompatibility)
               || ContainsAmdGpuMarker(device.PnpDeviceId);
    }

    private static bool IsNvidiaGpu(GpuDeviceInfo device)
    {
        return ContainsNvidiaGpuMarker(device.Name)
               || ContainsNvidiaGpuMarker(device.AdapterCompatibility)
               || ContainsNvidiaGpuMarker(device.PnpDeviceId);
    }

    private static bool IsSupportedAmdRocmGpu(GpuDeviceInfo device)
    {
        if (!IsAmdGpu(device))
        {
            return false;
        }

        var normalized = NormalizeGpuSupportName($"{device.Name} {device.AdapterCompatibility} {device.PnpDeviceId}");
        return normalized.Contains("radeonrx9070xt", StringComparison.Ordinal)
               || normalized.Contains("radeonrx9070", StringComparison.Ordinal)
               || normalized.Contains("radeonrx9060xt", StringComparison.Ordinal)
               || normalized.Contains("radeonaipror9700", StringComparison.Ordinal)
               || normalized.Contains("radeonrx7900xtx", StringComparison.Ordinal)
               || normalized.Contains("radeonprow7900dualslot", StringComparison.Ordinal)
               || normalized.Contains("radeonprow7900", StringComparison.Ordinal)
               || normalized.Contains("radeonrx7700", StringComparison.Ordinal)
               || normalized.Contains("radeon8060s", StringComparison.Ordinal)
               || normalized.Contains("radeon8050s", StringComparison.Ordinal)
               || normalized.Contains("radeon890m", StringComparison.Ordinal)
               || normalized.Contains("radeon880m", StringComparison.Ordinal)
               || normalized.Contains("ryzenaimax395", StringComparison.Ordinal)
               || normalized.Contains("ryzenaimax390", StringComparison.Ordinal)
               || normalized.Contains("ryzenaimax385", StringComparison.Ordinal)
               || normalized.Contains("ryzenai9hx375", StringComparison.Ordinal)
               || normalized.Contains("ryzenai9hx370", StringComparison.Ordinal)
               || normalized.Contains("ryzenai9365", StringComparison.Ordinal)
               || normalized.Contains("ryzenai9hx475", StringComparison.Ordinal)
               || normalized.Contains("ryzenai9hx470", StringComparison.Ordinal)
               || normalized.Contains("ryzenai9465", StringComparison.Ordinal);
    }

    private static string NormalizeGpuSupportName(string value)
    {
        return new string(
            value
                .ToLowerInvariant()
                .Where(char.IsLetterOrDigit)
                .ToArray());
    }

    private static string NormalizeAutomaticHardwareAccelerationDevice(string? value)
    {
        var normalized = HardwareAccelerationPreference.Normalize(value);
        return HardwareAccelerationPreference.TryParseGpuIndex(normalized, out _)
            ? normalized
            : string.Empty;
    }

    private static bool ContainsAmdGpuMarker(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.ToLowerInvariant();
        return normalized.Contains("amd", StringComparison.Ordinal)
               || normalized.Contains("advanced micro devices", StringComparison.Ordinal)
               || normalized.Contains("radeon", StringComparison.Ordinal)
               || normalized.Contains("ati technologies", StringComparison.Ordinal)
               || normalized.Contains("ven_1002", StringComparison.Ordinal);
    }

    private static bool ContainsNvidiaGpuMarker(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.ToLowerInvariant();
        return normalized.Contains("nvidia", StringComparison.Ordinal)
               || normalized.Contains("geforce", StringComparison.Ordinal)
               || normalized.Contains("quadro", StringComparison.Ordinal)
               || normalized.Contains("rtx", StringComparison.Ordinal)
               || normalized.Contains("gtx", StringComparison.Ordinal)
               || normalized.Contains("ven_10de", StringComparison.Ordinal);
    }

    private static string BuildBackendLabel(GpuDiagnostics diagnostics)
    {
        if (!string.IsNullOrWhiteSpace(diagnostics.HipVersion))
        {
            return $"ROCm/HIP {diagnostics.HipVersion}";
        }

        if (!string.IsNullOrWhiteSpace(diagnostics.CudaVersion))
        {
            return $"CUDA {diagnostics.CudaVersion}";
        }

        return "CPU";
    }

    private static string FormatMemoryLabel(long? bytes)
    {
        if (bytes is null or <= 0)
        {
            return string.Empty;
        }

        var gib = bytes.Value / 1024d / 1024d / 1024d;
        return $"{gib:0.#} GB";
    }

    private async Task<bool> InstallQwenRuntimeAsync()
    {
        var succeeded = false;
        await RunBusyOperationAsync(async () =>
        {
            await _backendRuntimeInstallerService.InstallQwenRuntimeAsync(
                _settings.HardwareAccelerationDevice,
                CancellationToken.None);
            _backendCapabilities = await _backendCapabilityService.DetectAsync(
                CancellationToken.None,
                forceRefresh: true);
            RefreshModelItems();
            SetStatus("settings.status.qwen_runtime_installed");
            succeeded = _backendCapabilities.HasQwenRuntime;
        }, "settings.status.qwen_runtime_failed");

        return succeeded;
    }

    private async Task<bool> InstallCrisperWhisperRuntimeAsync()
    {
        var succeeded = false;
        await RunBusyOperationAsync(async () =>
        {
            await _backendRuntimeInstallerService.InstallCrisperWhisperRuntimeAsync(
                GetEffectivePythonRuntimePath(),
                CancellationToken.None);
            _backendCapabilities = await _backendCapabilityService.DetectAsync(
                CancellationToken.None,
                forceRefresh: true);
            RefreshModelItems();
            SetStatus("settings.status.crisperwhisper_runtime_installed");
            succeeded = _backendCapabilities.HasCrisperWhisperRuntime;
        }, "settings.status.crisperwhisper_runtime_failed");

        return succeeded;
    }

    private async Task<bool> InstallPhononRuntimeAsync()
    {
        var succeeded = false;
        await RunBusyOperationAsync(async () =>
        {
            await _backendRuntimeInstallerService.InstallPhononRuntimeAsync(
                GetEffectivePythonRuntimePath(),
                CancellationToken.None);
            _backendCapabilities = await _backendCapabilityService.DetectAsync(
                CancellationToken.None,
                forceRefresh: true);
            RefreshModelItems();
            SetStatus("settings.status.phonon_runtime_installed");
            succeeded = _backendCapabilities.HasPhononRuntime;
        }, "settings.status.phonon_runtime_failed");

        return succeeded;
    }

    private string BuildQwenRuntimeMissingText()
    {
        if (!string.IsNullOrWhiteSpace(_backendCapabilities.PythonVersion))
        {
            return UiText.Translate(
                SelectedLanguageCode,
                "settings.model.runtime_optional_qwen_python",
                _backendCapabilities.PythonVersion!);
        }

        return UiText.Translate(SelectedLanguageCode, "settings.model.runtime_optional_qwen");
    }

    private bool IsSelectableWithCurrentRuntime(TranscriptionModelDefinition definition)
    {
        if (string.Equals(definition.Key, "voxtral-mini-4b-realtime", StringComparison.OrdinalIgnoreCase))
        {
            return _backendCapabilities.HasVoxtralRealtimeRuntime;
        }

        return true;
    }

    private bool IsManagedDownloadBlocked(ModelListEntry entry)
    {
        return entry.SourceKind == ModelSourceKind.Managed
               && !entry.IsAvailableLocally
               && _modelDownloadCoordinator.HasActiveDownload()
               && !_modelDownloadCoordinator.IsDownloading(entry.ModelKey);
    }

    private static string BuildActionTag(ModelListEntry entry)
    {
        return $"{entry.ModelKey}|{entry.SourceId}|{(int)entry.SourceKind}";
    }

    private ModelListEntry? TryGetModelListEntry(string actionTag)
    {
        var parts = actionTag.Split('|');
        if (parts.Length != 3 || !int.TryParse(parts[2], out var kindValue))
        {
            return null;
        }

        var definition = _modelCatalog.GetByKeyOrDefault(parts[0]);
        return BuildModelEntries(definition).FirstOrDefault(entry =>
            string.Equals(entry.SourceId, parts[1], StringComparison.OrdinalIgnoreCase)
            && (int)entry.SourceKind == kindValue);
    }

    private static MediaBrush CreateBrush(string hex)
    {
        return (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
    }

    private void UpdateViewState(bool animatedTransition = false)
    {
        SettingsHomeView.Visibility = _isModelBrowserVisible ? Visibility.Collapsed : Visibility.Visible;
        ModelBrowserView.Visibility = _isModelBrowserVisible ? Visibility.Visible : Visibility.Collapsed;
        SettingsTitleTextBlock.Text = UiText.Translate(
            SelectedLanguageCode,
            _isModelBrowserVisible ? "settings.models.title" : "settings.title");
        Title = SettingsTitleTextBlock.Text;

        if (IsLoaded)
        {
            _ = Dispatcher.InvokeAsync(
                () => UpdateWindowMetrics(animated: animatedTransition),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
        else
        {
            UpdateWindowMetrics(animated: false);
        }
    }

    private void UpdateWindowMetrics(bool animated)
    {
        RootLayoutGrid.UpdateLayout();

        if (IsLoaded && RootLayoutGrid.ActualHeight > 0d && ActualHeight > 0d)
        {
            _windowChromeHeight = Math.Max(DefaultChromeHeight, ActualHeight - RootLayoutGrid.ActualHeight);
        }

        var targetHeight = CalculateTargetWindowHeight();
        var currentHeight = ActualHeight > 0d ? ActualHeight : Height;

        if (!animated || Math.Abs(currentHeight - targetHeight) < 0.5d)
        {
            BeginAnimation(HeightProperty, null);
            Height = targetHeight;
            MinHeight = targetHeight;
            MaxHeight = targetHeight;
            return;
        }

        MinHeight = Math.Min(currentHeight, targetHeight);
        MaxHeight = Math.Max(currentHeight, targetHeight);

        var animation = new DoubleAnimation
        {
            To = targetHeight,
            Duration = TimeSpan.FromSeconds(1),
            EasingFunction = new QuadraticEase
            {
                EasingMode = EasingMode.EaseInOut,
            },
        };

        animation.Completed += (_, _) =>
        {
            BeginAnimation(HeightProperty, null);
            Height = targetHeight;
            MinHeight = targetHeight;
            MaxHeight = targetHeight;
        };

        BeginAnimation(HeightProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private double CalculateTargetWindowHeight()
    {
        var fallbackWidth = Math.Max(0d, Width - 28d);
        RootLayoutGrid.Measure(new System.Windows.Size(fallbackWidth, double.PositiveInfinity));

        var contentHeight = RootLayoutGrid.DesiredSize.Height;
        var heightSlack = _isModelBrowserVisible ? ModelBrowserHeightSlack : HomeWindowHeightSlack;
        var rawTargetHeight = Math.Ceiling(contentHeight + _windowChromeHeight + heightSlack);
        var boundedTargetHeight = Math.Max(MinimumWindowHeight, rawTargetHeight);

        return _isModelBrowserVisible
            ? Math.Min(MaximumModelBrowserHeight, boundedTargetHeight)
            : boundedTargetHeight;
    }

    private void UpdateHotkeyCaptureState()
    {
        CaptureHotkeyButton.Content = UiText.Translate(
            SelectedLanguageCode,
            _isCapturingHotkey ? "settings.hotkey.cancel_capture" : "settings.hotkey.capture");
    }

    private void UpdateTranscriptWindowOptionsState()
    {
        TranscriptWindowCheckBox.IsEnabled = !_isBusy;
        AutoOpenTranscriptWindowCheckBox.IsEnabled = !_isBusy;
        UpdateModeSelectionState();
    }

    private void SetPreferredMode(TranscriptionMode mode)
    {
        var currentDefinition = _modelCatalog.GetByKeyOrDefault(_settings.SelectedModelKey);
        var currentSource = _modelSourceCatalog.ResolveSelectedSource(currentDefinition, _settings, SelectedLanguageCode);
        if (currentSource is null || !currentDefinition.SupportsMode(mode))
        {
            UpdateModeSelectionState();
            return;
        }

        _settings.PreferredTranscriptionMode = mode;
        _settings.SetPreferredModeForModel(currentDefinition.Key, mode);
        _settingsService.Save(_settings);
        UpdateModeSelectionState();
    }

    private TranscriptionMode NormalizePreferredMode(TranscriptionModelDefinition definition, bool persist)
    {
        var normalized = _settings.GetPreferredModeForModel(definition.Key) ?? _settings.PreferredTranscriptionMode;
        if (!definition.SupportsMode(normalized))
        {
            normalized = definition.SupportsRealtimeTranscription
                ? TranscriptionMode.Realtime
                : TranscriptionMode.File;
        }

        var globalModeChanged = _settings.PreferredTranscriptionMode != normalized;
        var modelModeChanged = _settings.GetPreferredModeForModel(definition.Key) != normalized;

        if (!globalModeChanged && !modelModeChanged)
        {
            return normalized;
        }

        _settings.PreferredTranscriptionMode = normalized;
        _settings.SetPreferredModeForModel(definition.Key, normalized);
        if (persist)
        {
            _settingsService.Save(_settings);
        }

        return normalized;
    }

    private void UpdateModeSelectionState()
    {
        var currentDefinition = _modelCatalog.GetByKeyOrDefault(_settings.SelectedModelKey);
        var currentSource = _modelSourceCatalog.ResolveSelectedSource(currentDefinition, _settings, SelectedLanguageCode);
        var hasSelectedSource = currentSource is not null;
        var effectiveMode = hasSelectedSource
            ? NormalizePreferredMode(currentDefinition, persist: false)
            : _settings.PreferredTranscriptionMode;

        TraditionalModeToggleButton.IsChecked = hasSelectedSource && effectiveMode == TranscriptionMode.File;
        RealtimeModeToggleButton.IsChecked = hasSelectedSource && effectiveMode == TranscriptionMode.Realtime;
        TraditionalModeToggleButton.IsEnabled = !_isBusy && hasSelectedSource && currentDefinition.SupportsFileTranscription;
        RealtimeModeToggleButton.IsEnabled = !_isBusy && hasSelectedSource && currentDefinition.SupportsRealtimeTranscription;

        _ignoreTranscriptPreferenceChange = true;
        RealtimeTranscriptWindowCheckBox.IsChecked = true;
        RealtimeTranscriptWindowCheckBox.Visibility = Visibility.Collapsed;
        RealtimeTranscriptWindowCheckBox.IsEnabled = false;
        _ignoreTranscriptPreferenceChange = false;
    }

    private static int CountModifiers(ModifierKeys modifiers)
    {
        var count = 0;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            count++;
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            count++;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            count++;
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            count++;
        }

        return count;
    }

    private sealed record ModelListEntry(
        string ModelKey,
        string SourceId,
        ModelSourceKind SourceKind,
        string DisplayName,
        string SourceText,
        string? DirectoryPath,
        bool IsAvailableLocally,
        bool CanManage,
        bool IsDownloading);

    private sealed class ModelItemViewModel
    {
        public string ActionTag { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;

        public string SourceText { get; init; } = string.Empty;

        public string InfoText { get; init; } = string.Empty;

        public Visibility InfoVisibility { get; init; } = Visibility.Visible;

        public string StatusText { get; init; } = string.Empty;

        public string FileBadgeText { get; init; } = string.Empty;

        public string FileBadgeToolTip { get; init; } = string.Empty;

        public MediaBrush FileBadgeBackground { get; init; } = System.Windows.Media.Brushes.Transparent;

        public MediaBrush FileBadgeBorderBrush { get; init; } = System.Windows.Media.Brushes.Transparent;

        public MediaBrush FileBadgeForeground { get; init; } = System.Windows.Media.Brushes.Transparent;

        public double FileBadgeOpacity { get; init; } = 1d;

        public string RealtimeBadgeText { get; init; } = string.Empty;

        public string RealtimeBadgeToolTip { get; init; } = string.Empty;

        public MediaBrush RealtimeBadgeBackground { get; init; } = System.Windows.Media.Brushes.Transparent;

        public MediaBrush RealtimeBadgeBorderBrush { get; init; } = System.Windows.Media.Brushes.Transparent;

        public MediaBrush RealtimeBadgeForeground { get; init; } = System.Windows.Media.Brushes.Transparent;

        public double RealtimeBadgeOpacity { get; init; } = 1d;

        public bool CanSelect { get; init; }

        public bool CanManage { get; init; }

        public string ManageGlyph { get; init; } = string.Empty;

        public string ManageToolTip { get; init; } = string.Empty;

        public Visibility ManageVisibility { get; init; } = Visibility.Collapsed;

        public Visibility CapabilityBadgesVisibility { get; init; } = Visibility.Collapsed;

        public Visibility CountBadgesVisibility { get; init; } = Visibility.Collapsed;

        public string ModelCountText { get; init; } = string.Empty;

        public string VariantCountText { get; init; } = string.Empty;

        public Visibility ModelCountVisibility { get; init; } = Visibility.Collapsed;

        public Visibility VariantCountVisibility { get; init; } = Visibility.Collapsed;

        public MediaBrush CardBackground { get; init; } = System.Windows.Media.Brushes.Transparent;

        public MediaBrush CardBorderBrush { get; init; } = System.Windows.Media.Brushes.Transparent;
    }

    private sealed record ModelPublisherGroup(
        string Key,
        string DisplayName,
        IReadOnlyList<ModelFamilyGroup> Families)
    {
        public int ModelCount => Families.Count;

        public int VariantCount => Families.Sum(family => family.Definitions.Count);
    }

    private sealed record ModelFamilyGroup(
        string Key,
        string DisplayName,
        IReadOnlyList<TranscriptionModelDefinition> Definitions)
    {
        public bool IsVariantFamily => Definitions.Count > 1;
    }

    private enum ModelBrowserLevel
    {
        Publishers,
        Models,
        Variants,
    }

    private sealed record HardwareAccelerationOptionViewModel(
        string Id,
        string DisplayName,
        bool CanSelect);
}
