using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using TranscriptionOverlay.Models;

namespace TranscriptionOverlay.Services;

public sealed class BackendRuntimeInstallerService
{
    private const string AmdRuntimeFolderName = "amd-rocm";
    private const string CpuRuntimeFolderName = "cpu";
    private const string LegacyNvidiaRuntimeFolderName = "nvidia-cuda";
    private const string NvidiaCuda126RuntimeFolderName = "nvidia-cuda-cu126";
    private const string NvidiaCuda130RuntimeFolderName = "nvidia-cuda-cu130";
    private const string LegacyQwenNvidiaRuntimeFolderName = "qwen-nvidia-cuda";
    private const string QwenNvidiaCuda126RuntimeFolderName = "qwen-nvidia-cuda-cu126";
    private const string QwenNvidiaCuda130RuntimeFolderName = "qwen-nvidia-cuda-cu130";
    private const string DownloadsFolderName = "downloads";
    private const string PackagesFolderName = "packages";
    private const string PipCacheFolderName = "pip-cache";
    private const string Python312FolderName = "python-3.12";
    private const string Python312Version = "3.12.10";
    private const string Python312InstallerUrl = "https://www.python.org/ftp/python/3.12.10/python-3.12.10-amd64.exe";
    private const string PytorchVersion = "2.11.0";
    private const string PytorchCuda126IndexUrl = "https://download.pytorch.org/whl/cu126";
    private const string PytorchCuda130IndexUrl = "https://download.pytorch.org/whl/cu130";
    private const string PytorchCpuIndexUrl = "https://download.pytorch.org/whl/cpu";
    private const string AmdRocmVersion = "7.2.1";
    private const string AmdTorchVersion = "2.9.1";
    private const string RuntimeProfileVersion = "3";
    private const string CrisperWhisperRequirement = "crisperwhisper==2.0.2";
    private const string PhononRequirement = "fermion-research==0.2.9";
    private const int DownloadAttemptCount = 6;

    private static readonly Version MinimumCuda13WindowsDriverVersion = new(580, 88);
    private static readonly TimeSpan DownloadConnectionTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DownloadIdleTimeout = TimeSpan.FromMinutes(2);

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private static readonly string[] AmdRocmBootstrapPackages =
    [
        "https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm_sdk_core-7.2.1-py3-none-win_amd64.whl",
        "https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm_sdk_devel-7.2.1-py3-none-win_amd64.whl",
        "https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm_sdk_libraries_custom-7.2.1-py3-none-win_amd64.whl",
        "https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/rocm-7.2.1.tar.gz",
    ];

    private static readonly string[] AmdTorchPackages =
    [
        "https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/torch-2.9.1%2Brocm7.2.1-cp312-cp312-win_amd64.whl",
        "https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/torchaudio-2.9.1%2Brocm7.2.1-cp312-cp312-win_amd64.whl",
        "https://repo.radeon.com/rocm/windows/rocm-rel-7.2.1/torchvision-0.24.1%2Brocm7.2.1-cp312-cp312-win_amd64.whl",
    ];

    private static readonly string[] QwenRuntimePackages =
    [
        "av>=19.0.1,<20",
        "soundfile>=0.13.1",
        "librosa>=0.11.0",
        "qwen-asr",
    ];

    private readonly AppEnvironment _appEnvironment;
    private readonly object _nvidiaProfilesLock = new();
    private NvidiaRuntimePlan? _cachedNvidiaRuntimePlan;

    public event EventHandler<RuntimeInstallProgress>? ProgressChanged;

    public BackendRuntimeInstallerService(AppEnvironment appEnvironment)
    {
        _appEnvironment = appEnvironment;
    }

    public async Task InstallCrisperWhisperRuntimeAsync(
        string runtimePythonPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(runtimePythonPath))
        {
            throw new FileNotFoundException(
                $"Runtime Python introuvable pour CrisperWhisper : {runtimePythonPath}");
        }

        ReportProgress("Installation du composant CrisperWhisper...", null);
        await RunProcessAsync(
            runtimePythonPath,
            BuildPipInstallArguments($"--upgrade {Quote(CrisperWhisperRequirement)}"),
            _appEnvironment.RootDirectory,
            cancellationToken).ConfigureAwait(false);
        await RunProcessAsync(
            runtimePythonPath,
            $"-c {Quote("from crisperwhisper import CrisperWhisperModel; print('ok')")}",
            _appEnvironment.RootDirectory,
            cancellationToken).ConfigureAwait(false);
        ReportProgress("Composant CrisperWhisper pret.", 100);
    }

    public async Task InstallPhononRuntimeAsync(
        string runtimePythonPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(runtimePythonPath))
        {
            throw new FileNotFoundException($"Runtime Python introuvable pour Phonon-2 : {runtimePythonPath}");
        }

        ReportProgress("Installation du composant Phonon-2...", null);
        await RunProcessAsync(
            runtimePythonPath,
            BuildPipInstallArguments(
                $"--upgrade-strategy only-if-needed {Quote(PhononRequirement)} {Quote("scipy>=1.11.0")} {Quote("zstandard>=0.23.0")}"),
            _appEnvironment.BackendDirectory,
            cancellationToken).ConfigureAwait(false);
        await RunProcessAsync(
            runtimePythonPath,
            $"-c {Quote("from phonon_runtime import phonon_runtime_is_available; assert phonon_runtime_is_available(), 'Runtime Phonon-2 incomplet'; print('ok')")}",
            _appEnvironment.BackendDirectory,
            cancellationToken).ConfigureAwait(false);
        ReportProgress("Composant Phonon-2 pret.", 100);
    }

    public Task<string> InstallQwenRuntimeAsync(CancellationToken cancellationToken)
    {
        return InstallQwenRuntimeAsync(HardwareAccelerationPreference.Auto, cancellationToken);
    }

    public async Task<string> InstallQwenRuntimeAsync(
        string? hardwareAccelerationDevice,
        CancellationToken cancellationToken)
    {
        ReportProgress("Detection des runtimes CUDA requis pour Qwen...", null);
        var plan = await DetectNvidiaRuntimePlanAsync(cancellationToken).ConfigureAwait(false);
        foreach (var runtimeKind in plan.RequiredRuntimes)
        {
            await InstallQwenRuntimeAsync(runtimeKind, cancellationToken).ConfigureAwait(false);
        }

        var preferredGpuIndex = HardwareAccelerationPreference.TryParseGpuIndex(
            hardwareAccelerationDevice,
            out var selectedGpuIndex)
            && plan.Gpus.Any(gpu => gpu.Index == selectedGpuIndex)
                ? selectedGpuIndex
                : plan.PreferredGpuIndex;
        var preferredRuntime = plan.ResolveRuntimeForGpu(preferredGpuIndex);
        var preferredPythonPath = GetInstalledQwenNvidiaRuntimePath(preferredRuntime)
                                  ?? throw new FileNotFoundException(
                                      $"Le runtime Qwen {BuildCudaDisplayName(preferredRuntime)} vient d'etre installe, " +
                                      "mais son environnement Python est introuvable.");
        ReportProgress("Runtime Qwen pret.", 100);
        return preferredPythonPath;
    }

    private async Task InstallQwenRuntimeAsync(
        NvidiaCudaRuntimeKind runtimeKind,
        CancellationToken cancellationToken)
    {
        var cudaPackage = BuildPytorchPackageProfile(runtimeKind);
        var runtimeProfile = BuildRuntimeProfile("qwen", cudaPackage.BuildTag, PytorchVersion);
        var runtimePythonPath = GetInstalledQwenNvidiaRuntimePath(runtimeKind)
                                ?? GetQwenNvidiaRuntimePythonPath(runtimeKind);
        var runtimeRoot = Path.GetDirectoryName(Path.GetDirectoryName(runtimePythonPath))
            ?? throw new DirectoryNotFoundException(
                $"Dossier du runtime Qwen {cudaPackage.DisplayName} introuvable.");

        if (await TryUseExistingRuntimeAsync(
                runtimePythonPath,
                runtimeProfile,
                token => ValidateQwenRuntimeAsync(runtimePythonPath, token),
                $"Le runtime Qwen {cudaPackage.DisplayName} est deja installe. Aucun telechargement requis.",
                cancellationToken,
                readyPercent: null).ConfigureAwait(false))
        {
            return;
        }

        if (!File.Exists(runtimePythonPath))
        {
            ReportProgress($"Installation de Python 3.12 pour Qwen {cudaPackage.DisplayName}...", null);
            var python312Path = await InstallPython312RuntimeAsync(cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(runtimeRoot);
            ReportProgress($"Creation du runtime Python Qwen {cudaPackage.DisplayName}...", null);
            await RunProcessAsync(
                python312Path,
                $"-m venv {Quote(runtimeRoot)}",
                _appEnvironment.RootDirectory,
                cancellationToken).ConfigureAwait(false);
        }

        if (!File.Exists(runtimePythonPath))
        {
            throw new FileNotFoundException(
                $"Runtime Python Qwen {cudaPackage.DisplayName} introuvable apres installation : {runtimePythonPath}");
        }

        MarkRuntimeIncomplete(runtimePythonPath);

        ReportProgress($"Mise a jour de pip pour Qwen {cudaPackage.DisplayName}...", null);
        await RunProcessAsync(
            runtimePythonPath,
            BuildPipInstallArguments("--upgrade pip setuptools wheel"),
            _appEnvironment.RootDirectory,
            cancellationToken).ConfigureAwait(false);

        var pytorchPackages = await DownloadPytorchPackagesAsync(cudaPackage, cancellationToken).ConfigureAwait(false);
        ReportProgress($"Installation de PyTorch {cudaPackage.DisplayName} pour Qwen...", null);
        await RunProcessAsync(
            runtimePythonPath,
            BuildPipInstallArguments(
                "--upgrade " + string.Join(' ', pytorchPackages.Select(packagePath => Quote(packagePath)))),
            _appEnvironment.RootDirectory,
            cancellationToken).ConfigureAwait(false);

        ReportProgress($"Installation de qwen-asr et des dependances audio ({cudaPackage.DisplayName})...", null);
        await RunProcessAsync(
            runtimePythonPath,
            BuildPipInstallArguments(
                "--upgrade --upgrade-strategy only-if-needed "
                + string.Join(' ', QwenRuntimePackages.Select(Quote))),
            _appEnvironment.RootDirectory,
            cancellationToken).ConfigureAwait(false);

        await ValidateQwenRuntimeAsync(runtimePythonPath, cancellationToken).ConfigureAwait(false);
        MarkRuntimeComplete(runtimePythonPath, runtimeProfile);
        ReportProgress($"Runtime Qwen {cudaPackage.DisplayName} pret.", null);
    }

    public bool IsAmdRocmRuntimeInstalled()
    {
        return GetInstalledAmdRocmRuntimePath() is not null;
    }

    public string? GetInstalledAmdRocmRuntimePath()
    {
        var pythonPath = GetAmdRocmRuntimePythonPath();
        return PythonRuntimeLocator.IsManagedRuntimeComplete(pythonPath) ? pythonPath : null;
    }

    public bool IsAmdRocmOperatingSystemSupported()
    {
        return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
    }

    public bool IsAmdRocmRuntimePath(string? pythonPath)
    {
        return AreSamePath(GetAmdRocmRuntimePythonPath(), pythonPath);
    }

    public bool IsCpuRuntimeInstalled()
    {
        return GetInstalledCpuRuntimePath() is not null;
    }

    public string? GetInstalledCpuRuntimePath()
    {
        var pythonPath = GetCpuRuntimePythonPath();
        return PythonRuntimeLocator.IsManagedRuntimeComplete(pythonPath) ? pythonPath : null;
    }

    public bool IsCpuRuntimePath(string? pythonPath)
    {
        return AreSamePath(GetCpuRuntimePythonPath(), pythonPath);
    }

    public bool IsNvidiaCudaRuntimeInstalled()
    {
        return GetInstalledNvidiaCudaRuntimePath(NvidiaCudaRuntimeKind.Cuda126) is not null
               || GetInstalledNvidiaCudaRuntimePath(NvidiaCudaRuntimeKind.Cuda130) is not null;
    }

    public string? GetInstalledNvidiaCudaRuntimePath()
    {
        return GetInstalledNvidiaCudaRuntimePath(NvidiaCudaRuntimeKind.Cuda130)
               ?? GetInstalledNvidiaCudaRuntimePath(NvidiaCudaRuntimeKind.Cuda126);
    }

    public bool IsNvidiaCudaRuntimePath(string? pythonPath)
    {
        return EnumerateNvidiaRuntimePythonPaths().Any(candidate => AreSamePath(candidate, pythonPath));
    }

    public bool IsInstalledNvidiaCudaRuntimePath(string? pythonPath)
    {
        var cuda126Path = GetInstalledNvidiaCudaRuntimePath(NvidiaCudaRuntimeKind.Cuda126);
        var cuda130Path = GetInstalledNvidiaCudaRuntimePath(NvidiaCudaRuntimeKind.Cuda130);
        return (cuda126Path is not null && AreSamePath(cuda126Path, pythonPath))
               || (cuda130Path is not null && AreSamePath(cuda130Path, pythonPath));
    }

    public NvidiaRuntimePlan? CachedNvidiaRuntimePlan
    {
        get
        {
            lock (_nvidiaProfilesLock)
            {
                return _cachedNvidiaRuntimePlan;
            }
        }
    }

    public string? GetInstalledNvidiaCudaRuntimePathForGpu(int gpuIndex)
    {
        var plan = CachedNvidiaRuntimePlan;
        if (plan is null)
        {
            return null;
        }

        return GetInstalledNvidiaCudaRuntimePath(plan.ResolveRuntimeForGpu(gpuIndex));
    }

    public bool AreRequiredNvidiaCudaRuntimesInstalled()
    {
        var plan = CachedNvidiaRuntimePlan;
        return plan is not null
               && plan.RequiredRuntimes.All(
                   runtimeKind => GetInstalledNvidiaCudaRuntimePath(runtimeKind) is not null);
    }

    public async Task<string> InstallPython312RuntimeAsync(CancellationToken cancellationToken)
    {
        var pythonPath = GetLocalPython312Path();
        if (File.Exists(pythonPath))
        {
            ReportProgress("Python 3.12 est deja installe.", 100);
            return pythonPath;
        }

        var runtimeRoot = Path.GetDirectoryName(pythonPath)
            ?? throw new DirectoryNotFoundException("Dossier Python 3.12 local introuvable.");
        Directory.CreateDirectory(runtimeRoot);

        var downloadsDirectory = Path.Combine(_appEnvironment.RuntimesDirectory, DownloadsFolderName);
        Directory.CreateDirectory(downloadsDirectory);
        var installerPath = Path.Combine(downloadsDirectory, $"python-{Python312Version}-amd64.exe");

        await DownloadFileAsync(
            new PackageDownloadSource(
                new Uri(Python312InstallerUrl),
                Path.GetFileName(installerPath),
                null),
            installerPath,
            "Telechargement de Python 3.12",
            cancellationToken).ConfigureAwait(false);

        ReportProgress("Installation locale de Python 3.12...", null);
        await RunProcessAsync(
            installerPath,
            $"/quiet InstallAllUsers=0 TargetDir={Quote(runtimeRoot)} Include_pip=1 Include_launcher=0 Shortcuts=0 AssociateFiles=0 PrependPath=0 Include_test=0",
            _appEnvironment.RootDirectory,
            cancellationToken,
            0,
            3010).ConfigureAwait(false);
        await WaitForFileAsync(pythonPath, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);

        if (!File.Exists(pythonPath))
        {
            ReportProgress("Reparation de l'installation locale Python 3.12...", null);
            await RunProcessAsync(
                installerPath,
                $"/quiet /repair TargetDir={Quote(runtimeRoot)} Include_pip=1 Include_launcher=0 Shortcuts=0 AssociateFiles=0 PrependPath=0 Include_test=0",
                _appEnvironment.RootDirectory,
                cancellationToken,
                0,
                3010).ConfigureAwait(false);
            await WaitForFileAsync(pythonPath, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }

        if (!File.Exists(pythonPath))
        {
            throw new FileNotFoundException($"Python 3.12 local introuvable apres installation : {pythonPath}");
        }

        ReportProgress("Python 3.12 installe.", 100);
        return pythonPath;
    }

    public async Task<string> InstallCpuRuntimeAsync(CancellationToken cancellationToken)
    {
        ReportProgress("Preparation du runtime CPU compatible...", null);
        var runtimePythonPath = GetCpuRuntimePythonPath();
        var runtimeRoot = Path.GetDirectoryName(Path.GetDirectoryName(runtimePythonPath))
            ?? Path.Combine(_appEnvironment.RuntimesDirectory, CpuRuntimeFolderName);
        var cpuPackage = new PytorchPackageProfile(PytorchCpuIndexUrl, "cpu", "CPU");
        var runtimeProfile = BuildRuntimeProfile("cpu", cpuPackage.BuildTag, PytorchVersion);

        if (await TryUseExistingRuntimeAsync(
                runtimePythonPath,
                runtimeProfile,
                token => ValidateCpuRuntimeAsync(runtimePythonPath, token),
                "Le runtime CPU est deja installe. Activation sans nouveau telechargement.",
                cancellationToken).ConfigureAwait(false))
        {
            return runtimePythonPath;
        }

        if (!File.Exists(runtimePythonPath))
        {
            ReportProgress("Installation de Python 3.12 pour le runtime CPU...", null);
            var python312Path = await InstallPython312RuntimeAsync(cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(runtimeRoot);
            ReportProgress("Creation du runtime Python CPU...", null);
            await RunProcessAsync(
                python312Path,
                $"-m venv {Quote(runtimeRoot)}",
                _appEnvironment.RootDirectory,
                cancellationToken).ConfigureAwait(false);
        }

        if (!File.Exists(runtimePythonPath))
        {
            throw new FileNotFoundException($"Runtime Python CPU introuvable apres installation : {runtimePythonPath}");
        }

        MarkRuntimeIncomplete(runtimePythonPath);
        ReportProgress("Mise a jour de pip pour le runtime CPU...", null);
        await RunProcessAsync(
            runtimePythonPath,
            BuildPipInstallArguments("--upgrade pip setuptools wheel"),
            _appEnvironment.RootDirectory,
            cancellationToken).ConfigureAwait(false);

        var pytorchPackages = await DownloadPytorchPackagesAsync(cpuPackage, cancellationToken).ConfigureAwait(false);
        ReportProgress("Installation de PyTorch CPU...", null);
        await RunProcessAsync(
            runtimePythonPath,
            BuildPipInstallArguments(
                "--upgrade " + string.Join(' ', pytorchPackages.Select(packagePath => Quote(packagePath)))),
            _appEnvironment.RootDirectory,
            cancellationToken).ConfigureAwait(false);

        var requirementsPath = Path.Combine(_appEnvironment.BackendDirectory, "requirements.txt");
        if (File.Exists(requirementsPath))
        {
            ReportProgress("Installation des dependances Voxcribe pour le CPU...", null);
            await RunProcessAsync(
                runtimePythonPath,
                BuildPipInstallArguments(
                    $"--upgrade-strategy only-if-needed -r {Quote(requirementsPath)}"),
                _appEnvironment.RootDirectory,
                cancellationToken).ConfigureAwait(false);
        }

        await ValidateCpuRuntimeAsync(runtimePythonPath, cancellationToken).ConfigureAwait(false);
        MarkRuntimeComplete(runtimePythonPath, runtimeProfile);
        ReportProgress("Runtime CPU pret.", 100);
        return runtimePythonPath;
    }

    public async Task<string> InstallAmdRocmRuntimeAsync(CancellationToken cancellationToken)
    {
        ReportProgress("Preparation du runtime AMD ROCm...", null);
        if (!IsAmdRocmOperatingSystemSupported())
        {
            throw new PlatformNotSupportedException(
                "Le runtime AMD ROCm 7.2.1 de Voxcribe necessite Windows 11.");
        }

        var runtimePythonPath = GetAmdRocmRuntimePythonPath();
        var runtimeRoot = Path.GetDirectoryName(Path.GetDirectoryName(runtimePythonPath))
            ?? Path.Combine(_appEnvironment.RuntimesDirectory, AmdRuntimeFolderName);
        var runtimeProfile = BuildRuntimeProfile("amd", $"rocm{AmdRocmVersion}", AmdTorchVersion);

        if (await TryUseExistingRuntimeAsync(
                runtimePythonPath,
                runtimeProfile,
                token => ValidateAmdRuntimeAsync(runtimePythonPath, token),
                "Le runtime AMD ROCm est deja installe. Activation sans nouveau telechargement.",
                cancellationToken).ConfigureAwait(false))
        {
            return runtimePythonPath;
        }

        if (!File.Exists(runtimePythonPath))
        {
            ReportProgress("Installation de Python 3.12 pour AMD...", null);
            var python312Path = await InstallPython312RuntimeAsync(cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(runtimeRoot);
            ReportProgress("Creation du runtime Python AMD...", null);
            await RunProcessAsync(
                python312Path,
                $"-m venv {Quote(runtimeRoot)}",
                _appEnvironment.RootDirectory,
                cancellationToken).ConfigureAwait(false);
        }

        if (!File.Exists(runtimePythonPath))
        {
            throw new FileNotFoundException($"Runtime Python AMD introuvable apres installation : {runtimePythonPath}");
        }

        MarkRuntimeIncomplete(runtimePythonPath);

        ReportProgress("Mise a jour de pip pour AMD...", null);
        await RunProcessAsync(
            runtimePythonPath,
            BuildPipInstallArguments("--upgrade pip setuptools wheel"),
            _appEnvironment.RootDirectory,
            cancellationToken).ConfigureAwait(false);

        var bootstrapPackages = await DownloadPackagesAsync(
                AmdRocmBootstrapPackages,
                $"amd-rocm-{AmdRocmVersion}",
                "ROCm AMD",
                cancellationToken)
            .ConfigureAwait(false);
        ReportProgress("Installation des composants ROCm AMD...", null);
        await RunProcessAsync(
            runtimePythonPath,
            BuildPipInstallArguments(
                "--upgrade " + string.Join(' ', bootstrapPackages.Select(packagePath => Quote(packagePath)))),
            _appEnvironment.RootDirectory,
            cancellationToken).ConfigureAwait(false);

        var torchPackages = await DownloadPackagesAsync(
                AmdTorchPackages,
                $"amd-rocm-{AmdRocmVersion}",
                "PyTorch ROCm AMD",
                cancellationToken)
            .ConfigureAwait(false);
        ReportProgress("Installation de PyTorch ROCm AMD...", null);
        await RunProcessAsync(
            runtimePythonPath,
            BuildPipInstallArguments(
                "--upgrade " + string.Join(' ', torchPackages.Select(packagePath => Quote(packagePath)))),
            _appEnvironment.RootDirectory,
            cancellationToken).ConfigureAwait(false);

        var requirementsPath = Path.Combine(_appEnvironment.BackendDirectory, "requirements.txt");
        if (File.Exists(requirementsPath))
        {
            ReportProgress("Installation des dependances Voxcribe pour AMD...", null);
            await RunProcessAsync(
                runtimePythonPath,
                BuildPipInstallArguments(
                    $"--upgrade-strategy only-if-needed -r {Quote(requirementsPath)}"),
                _appEnvironment.RootDirectory,
                cancellationToken).ConfigureAwait(false);
        }

        await ValidateAmdRuntimeAsync(runtimePythonPath, cancellationToken).ConfigureAwait(false);
        MarkRuntimeComplete(runtimePythonPath, runtimeProfile);
        ReportProgress("Runtime AMD ROCm pret.", 100);

        return runtimePythonPath;
    }

    public Task<NvidiaRuntimeInstallResult> InstallNvidiaCudaRuntimeAsync(CancellationToken cancellationToken)
    {
        return InstallNvidiaCudaRuntimeAsync(HardwareAccelerationPreference.Auto, cancellationToken);
    }

    public async Task<NvidiaRuntimeInstallResult> InstallNvidiaCudaRuntimeAsync(
        string? hardwareAccelerationDevice,
        CancellationToken cancellationToken)
    {
        ReportProgress("Detection des GPU NVIDIA et des runtimes CUDA requis...", null);
        var plan = await DetectNvidiaRuntimePlanAsync(cancellationToken, forceRefresh: true).ConfigureAwait(false);

        foreach (var runtimeKind in plan.RequiredRuntimes)
        {
            await InstallNvidiaCudaRuntimeAsync(runtimeKind, cancellationToken).ConfigureAwait(false);
        }

        var preferredGpuIndex = HardwareAccelerationPreference.TryParseGpuIndex(
            hardwareAccelerationDevice,
            out var selectedGpuIndex)
            && plan.Gpus.Any(gpu => gpu.Index == selectedGpuIndex)
                ? selectedGpuIndex
                : plan.PreferredGpuIndex;
        var preferredRuntime = plan.ResolveRuntimeForGpu(preferredGpuIndex);
        var preferredPythonPath = GetInstalledNvidiaCudaRuntimePath(preferredRuntime)
                                  ?? throw new FileNotFoundException(
                                      $"Le runtime NVIDIA {BuildCudaDisplayName(preferredRuntime)} vient d'etre installe, " +
                                      "mais son environnement Python est introuvable.");

        var installedLabels = string.Join(
            " et ",
            plan.RequiredRuntimes.Select(BuildCudaDisplayName));
        ReportProgress($"Runtime NVIDIA {installedLabels} pret.", 100);

        return new NvidiaRuntimeInstallResult(preferredPythonPath, preferredGpuIndex, plan);
    }

    public async Task<NvidiaRuntimePlan> DetectNvidiaRuntimePlanAsync(
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        lock (_nvidiaProfilesLock)
        {
            if (!forceRefresh && _cachedNvidiaRuntimePlan is not null)
            {
                return _cachedNvidiaRuntimePlan;
            }
        }

        List<NvidiaGpuProfile> profiles;
        try
        {
            var nvidiaSmiPath = ResolveNvidiaSmiPath();
            var (stdout, _) = await RunProcessCaptureAsync(
                nvidiaSmiPath,
                "--query-gpu=index,name,compute_cap,driver_version,memory.total --format=csv,noheader,nounits",
                _appEnvironment.RootDirectory,
                cancellationToken).ConfigureAwait(false);
            profiles = ParseNvidiaGpuProfiles(stdout);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Voxcribe n'a pas pu determiner l'architecture CUDA avec nvidia-smi. " +
                "Le runtime NVIDIA n'a pas ete modifie afin d'eviter d'installer une build PyTorch incompatible. " +
                $"Detail : {ex.Message}",
                ex);
        }

        if (profiles.Count == 0)
        {
            throw new InvalidOperationException(
                "nvidia-smi n'a retourne aucun GPU avec une capacite de calcul exploitable. " +
                "Mettez le pilote NVIDIA a jour, puis relancez l'installation.");
        }

        var plan = NvidiaRuntimePlanner.Build(profiles);
        if (plan.RequiredRuntimes.Contains(NvidiaCudaRuntimeKind.Cuda130))
        {
            var outdatedDriverProfile = plan.Gpus.FirstOrDefault(
                profile => profile.DriverVersion < MinimumCuda13WindowsDriverVersion);
            if (outdatedDriverProfile is not null)
            {
                throw new InvalidOperationException(
                    $"Le GPU NVIDIA {outdatedDriverProfile.Name} (capacite de calcul " +
                    $"{outdatedDriverProfile.ComputeCapability}) requiert le pilote NVIDIA " +
                    $"{MinimumCuda13WindowsDriverVersion} ou plus recent pour PyTorch CUDA 13.0. " +
                    $"Pilote detecte : {outdatedDriverProfile.DriverVersion}.");
            }
        }

        lock (_nvidiaProfilesLock)
        {
            _cachedNvidiaRuntimePlan = plan;
        }

        return plan;
    }

    private async Task InstallNvidiaCudaRuntimeAsync(
        NvidiaCudaRuntimeKind runtimeKind,
        CancellationToken cancellationToken)
    {
        var cudaPackage = BuildPytorchPackageProfile(runtimeKind);
        var runtimeProfile = BuildRuntimeProfile("nvidia", cudaPackage.BuildTag, PytorchVersion);
        var runtimePythonPath = GetInstalledNvidiaCudaRuntimePath(runtimeKind)
                                ?? GetNvidiaCudaRuntimePythonPath(runtimeKind);
        var runtimeRoot = Path.GetDirectoryName(Path.GetDirectoryName(runtimePythonPath))
            ?? throw new DirectoryNotFoundException(
                $"Dossier du runtime NVIDIA {cudaPackage.DisplayName} introuvable.");

        if (await TryUseExistingRuntimeAsync(
                runtimePythonPath,
                runtimeProfile,
                token => ValidateNvidiaRuntimeAsync(runtimePythonPath, token),
                $"Le runtime NVIDIA {cudaPackage.DisplayName} est deja installe. Aucun telechargement requis.",
                cancellationToken,
                readyPercent: null).ConfigureAwait(false))
        {
            return;
        }

        if (!File.Exists(runtimePythonPath))
        {
            ReportProgress($"Installation de Python 3.12 pour NVIDIA {cudaPackage.DisplayName}...", null);
            var python312Path = await InstallPython312RuntimeAsync(cancellationToken).ConfigureAwait(false);
            Directory.CreateDirectory(runtimeRoot);
            ReportProgress($"Creation du runtime Python NVIDIA {cudaPackage.DisplayName}...", null);
            await RunProcessAsync(
                python312Path,
                $"-m venv {Quote(runtimeRoot)}",
                _appEnvironment.RootDirectory,
                cancellationToken).ConfigureAwait(false);
        }

        if (!File.Exists(runtimePythonPath))
        {
            throw new FileNotFoundException(
                $"Runtime Python NVIDIA {cudaPackage.DisplayName} introuvable apres installation : {runtimePythonPath}");
        }

        MarkRuntimeIncomplete(runtimePythonPath);

        ReportProgress($"Mise a jour de pip pour NVIDIA {cudaPackage.DisplayName}...", null);
        await RunProcessAsync(
            runtimePythonPath,
            BuildPipInstallArguments("--upgrade pip setuptools wheel"),
            _appEnvironment.RootDirectory,
            cancellationToken).ConfigureAwait(false);

        var pytorchPackages = await DownloadPytorchPackagesAsync(cudaPackage, cancellationToken).ConfigureAwait(false);
        ReportProgress($"Installation de PyTorch {cudaPackage.DisplayName} NVIDIA...", null);
        await RunProcessAsync(
            runtimePythonPath,
            BuildPipInstallArguments(
                "--upgrade " + string.Join(' ', pytorchPackages.Select(packagePath => Quote(packagePath)))),
            _appEnvironment.RootDirectory,
            cancellationToken).ConfigureAwait(false);

        var requirementsPath = Path.Combine(_appEnvironment.BackendDirectory, "requirements.txt");
        if (File.Exists(requirementsPath))
        {
            ReportProgress($"Installation des dependances Voxcribe pour NVIDIA {cudaPackage.DisplayName}...", null);
            await RunProcessAsync(
                runtimePythonPath,
                BuildPipInstallArguments(
                    $"--upgrade-strategy only-if-needed -r {Quote(requirementsPath)}"),
                _appEnvironment.RootDirectory,
                cancellationToken).ConfigureAwait(false);
        }

        await ValidateNvidiaRuntimeAsync(runtimePythonPath, cancellationToken).ConfigureAwait(false);
        MarkRuntimeComplete(runtimePythonPath, runtimeProfile);
        ReportProgress($"Runtime NVIDIA {cudaPackage.DisplayName} pret.", null);
    }

    private string BuildPipInstallArguments(string installArguments)
    {
        var pipCacheDirectory = Path.Combine(
            _appEnvironment.RuntimesDirectory,
            DownloadsFolderName,
            PipCacheFolderName);
        Directory.CreateDirectory(pipCacheDirectory);
        return "-m pip install --disable-pip-version-check --progress-bar off "
               + "--retries 12 --timeout 120 "
               + $"--cache-dir {Quote(pipCacheDirectory)} "
               + installArguments;
    }

    private async Task ValidateQwenRuntimeAsync(
        string runtimePythonPath,
        CancellationToken cancellationToken)
    {
        ReportProgress("Validation des dependances Qwen...", null);
        await ValidateRuntimeDependenciesAsync(
            runtimePythonPath,
            includeQwen: true,
            cancellationToken).ConfigureAwait(false);
        ReportProgress("Validation des kernels CUDA pour Qwen...", null);
        await ValidateGpuRuntimeAsync(
            runtimePythonPath,
            runtimeLabel: "Qwen",
            requireCudaBuild: true,
            requireHipBuild: false,
            allowCpuOnly: true,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ValidateNvidiaRuntimeAsync(
        string runtimePythonPath,
        CancellationToken cancellationToken)
    {
        ReportProgress("Validation des dependances NVIDIA...", null);
        await ValidateRuntimeDependenciesAsync(
            runtimePythonPath,
            includeQwen: false,
            cancellationToken).ConfigureAwait(false);
        ReportProgress("Validation des kernels CUDA NVIDIA...", null);
        await ValidateGpuRuntimeAsync(
            runtimePythonPath,
            runtimeLabel: "NVIDIA CUDA",
            requireCudaBuild: true,
            requireHipBuild: false,
            allowCpuOnly: false,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ValidateCpuRuntimeAsync(
        string runtimePythonPath,
        CancellationToken cancellationToken)
    {
        ReportProgress("Validation des dependances CPU...", null);
        await ValidateRuntimeDependenciesAsync(
            runtimePythonPath,
            includeQwen: false,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ValidateAmdRuntimeAsync(
        string runtimePythonPath,
        CancellationToken cancellationToken)
    {
        try
        {
            ReportProgress("Validation des dependances AMD...", null);
            await ValidateRuntimeDependenciesAsync(
                runtimePythonPath,
                includeQwen: false,
                cancellationToken).ConfigureAwait(false);
            ReportProgress("Validation des kernels ROCm AMD...", null);
            await ValidateGpuRuntimeAsync(
                runtimePythonPath,
                runtimeLabel: "AMD ROCm",
                requireCudaBuild: false,
                requireHipBuild: true,
                allowCpuOnly: false,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Le runtime AMD a ete telecharge, mais sa validation a echoue. " +
                "Verifiez Windows 11, le pilote AMD 26.2.2 ou plus recent et le redistribuable Visual C++ 2015-2022. " +
                $"Detail : {SummarizeError(ex.Message)}",
                ex);
        }
    }

    private async Task ValidateRuntimeDependenciesAsync(
        string runtimePythonPath,
        bool includeQwen,
        CancellationToken cancellationToken)
    {
        var modules = new List<string>
        {
            "av",
            "huggingface_hub",
            "librosa",
            "numpy",
            "soundfile",
            "torch",
            "torchaudio",
            "transformers",
        };
        if (includeQwen)
        {
            modules.Add("qwen_asr");
        }
        else
        {
            modules.AddRange(["accelerate", "crisperwhisper", "fermion", "mistral_common", "safetensors", "scipy", "zstandard"]);
        }

        var importCode = $"import {string.Join(", ", modules)}; " +
                         "assert tuple(map(int, av.__version__.split('.')[:3])) >= (19, 0, 1), 'PyAV 19.0.1 requis'; " +
                         "print('ok')";
        await RunProcessAsync(
            runtimePythonPath,
            $"-c {Quote(importCode)}",
            _appEnvironment.RootDirectory,
            cancellationToken).ConfigureAwait(false);
        await RunProcessAsync(
            runtimePythonPath,
            "-m pip check --disable-pip-version-check",
            _appEnvironment.RootDirectory,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ValidateGpuRuntimeAsync(
        string runtimePythonPath,
        string runtimeLabel,
        bool requireCudaBuild,
        bool requireHipBuild,
        bool allowCpuOnly,
        CancellationToken cancellationToken)
    {
        var diagnosticsScriptPath = Path.Combine(_appEnvironment.BackendDirectory, "model_manager.py");
        if (!File.Exists(diagnosticsScriptPath))
        {
            throw new FileNotFoundException($"Diagnostic GPU introuvable : {diagnosticsScriptPath}");
        }

        var (stdout, _) = await RunProcessCaptureAsync(
            runtimePythonPath,
            $"{Quote(diagnosticsScriptPath)} gpu-diagnostics",
            _appEnvironment.RootDirectory,
            cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.Deserialize<CudaValidationPayload>(
            ExtractJsonPayload(stdout),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Le diagnostic CUDA installe est illisible.");

        if (requireCudaBuild && string.IsNullOrWhiteSpace(payload.CudaVersion))
        {
            throw new InvalidOperationException(
                $"Le runtime {runtimeLabel} a installe PyTorch {payload.TorchVersion ?? "inconnu"}, " +
                "mais cette build ne contient pas CUDA.");
        }

        if (requireHipBuild && string.IsNullOrWhiteSpace(payload.HipVersion))
        {
            throw new InvalidOperationException(
                $"Le runtime {runtimeLabel} a installe PyTorch {payload.TorchVersion ?? "inconnu"}, " +
                "mais cette build ne contient pas ROCm/HIP.");
        }

        if ((payload.CudaAvailable && payload.CudaUsable)
            || (allowCpuOnly && !payload.CudaAvailable))
        {
            return;
        }

        var deviceDetails = string.Join(
            " | ",
            payload.Devices.Select(device =>
            {
                var capability = string.IsNullOrWhiteSpace(device.ComputeCapability)
                    ? string.Empty
                    : $" (sm_{device.ComputeCapability.Replace(".", string.Empty, StringComparison.Ordinal)})";
                var error = string.IsNullOrWhiteSpace(device.Error) ? "kernel GPU inutilisable" : device.Error.Trim();
                return $"{device.Name}{capability}: {error}";
            }));
        var details = string.IsNullOrWhiteSpace(deviceDetails)
            ? "Aucun GPU utilisable n'a ete expose par PyTorch."
            : deviceDetails;

        throw new InvalidOperationException(
            $"PyTorch {payload.TorchVersion ?? "inconnu"} ({BuildGpuBackendLabel(payload)}) a ete installe, " +
            $"mais le test reel d'un kernel GPU a echoue. {details}");
    }

    private static string BuildGpuBackendLabel(CudaValidationPayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.HipVersion))
        {
            return $"ROCm/HIP {payload.HipVersion}";
        }

        return $"CUDA {payload.CudaVersion ?? "inconnue"}";
    }

    private static string ResolveNvidiaSmiPath()
    {
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programFilesDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new[]
        {
            Path.Combine(windowsDirectory, "System32", "nvidia-smi.exe"),
            Path.Combine(programFilesDirectory, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
        };

        return candidates.FirstOrDefault(File.Exists) ?? "nvidia-smi.exe";
    }

    private static List<NvidiaGpuProfile> ParseNvidiaGpuProfiles(string stdout)
    {
        var profiles = new List<NvidiaGpuProfile>();
        foreach (var line in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length < 5
                || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                || !Version.TryParse(fields[2], out var computeCapability)
                || !Version.TryParse(fields[3], out var driverVersion))
            {
                continue;
            }

            long? totalMemoryBytes = null;
            if (long.TryParse(
                    fields[4],
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var totalMemoryMib)
                && totalMemoryMib > 0)
            {
                totalMemoryBytes = totalMemoryMib * 1024L * 1024L;
            }

            profiles.Add(new NvidiaGpuProfile(
                index,
                string.IsNullOrWhiteSpace(fields[1]) ? $"GPU NVIDIA {index}" : fields[1],
                computeCapability,
                driverVersion,
                totalMemoryBytes));
        }

        return profiles;
    }

    private static string ExtractJsonPayload(string output)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            throw new JsonException("Aucun objet JSON n'a ete retourne par le diagnostic CUDA.");
        }

        return output[start..(end + 1)];
    }

    private string GetLocalPython312Path()
    {
        return Path.Combine(_appEnvironment.RuntimesDirectory, Python312FolderName, "python.exe");
    }

    private string GetAmdRocmRuntimePythonPath()
    {
        return Path.Combine(_appEnvironment.RuntimesDirectory, AmdRuntimeFolderName, "Scripts", "python.exe");
    }

    private string GetCpuRuntimePythonPath()
    {
        return Path.Combine(_appEnvironment.RuntimesDirectory, CpuRuntimeFolderName, "Scripts", "python.exe");
    }

    private string? GetInstalledNvidiaCudaRuntimePath(NvidiaCudaRuntimeKind runtimeKind)
    {
        var packageProfile = BuildPytorchPackageProfile(runtimeKind);
        var expectedRuntimeProfile = BuildRuntimeProfile("nvidia", packageProfile.BuildTag, PytorchVersion);
        var versionedPath = GetNvidiaCudaRuntimePythonPath(runtimeKind);
        if (IsRuntimeProfileComplete(versionedPath, expectedRuntimeProfile))
        {
            return versionedPath;
        }

        var legacyPath = GetManagedRuntimePythonPath(LegacyNvidiaRuntimeFolderName);
        return IsRuntimeProfileComplete(legacyPath, expectedRuntimeProfile) ? legacyPath : null;
    }

    private string GetNvidiaCudaRuntimePythonPath(NvidiaCudaRuntimeKind runtimeKind)
    {
        var runtimeFolderName = runtimeKind switch
        {
            NvidiaCudaRuntimeKind.Cuda126 => NvidiaCuda126RuntimeFolderName,
            NvidiaCudaRuntimeKind.Cuda130 => NvidiaCuda130RuntimeFolderName,
            _ => throw new ArgumentOutOfRangeException(nameof(runtimeKind), runtimeKind, null),
        };

        return GetManagedRuntimePythonPath(runtimeFolderName);
    }

    private IEnumerable<string> EnumerateNvidiaRuntimePythonPaths()
    {
        yield return GetNvidiaCudaRuntimePythonPath(NvidiaCudaRuntimeKind.Cuda126);
        yield return GetNvidiaCudaRuntimePythonPath(NvidiaCudaRuntimeKind.Cuda130);
        yield return GetManagedRuntimePythonPath(LegacyNvidiaRuntimeFolderName);
    }

    private string GetManagedRuntimePythonPath(string runtimeFolderName)
    {
        return Path.Combine(_appEnvironment.RuntimesDirectory, runtimeFolderName, "Scripts", "python.exe");
    }

    private static PytorchPackageProfile BuildPytorchPackageProfile(NvidiaCudaRuntimeKind runtimeKind)
    {
        return runtimeKind switch
        {
            NvidiaCudaRuntimeKind.Cuda126 => new PytorchPackageProfile(
                PytorchCuda126IndexUrl,
                "cu126",
                "CUDA 12.6"),
            NvidiaCudaRuntimeKind.Cuda130 => new PytorchPackageProfile(
                PytorchCuda130IndexUrl,
                "cu130",
                "CUDA 13.0"),
            _ => throw new ArgumentOutOfRangeException(nameof(runtimeKind), runtimeKind, null),
        };
    }

    private static string BuildCudaDisplayName(NvidiaCudaRuntimeKind runtimeKind)
    {
        return BuildPytorchPackageProfile(runtimeKind).DisplayName;
    }

    private string? GetInstalledQwenNvidiaRuntimePath(NvidiaCudaRuntimeKind runtimeKind)
    {
        var packageProfile = BuildPytorchPackageProfile(runtimeKind);
        var expectedRuntimeProfile = BuildRuntimeProfile("qwen", packageProfile.BuildTag, PytorchVersion);
        var versionedPath = GetQwenNvidiaRuntimePythonPath(runtimeKind);
        if (IsRuntimeProfileComplete(versionedPath, expectedRuntimeProfile))
        {
            return versionedPath;
        }

        var legacyPath = GetManagedRuntimePythonPath(LegacyQwenNvidiaRuntimeFolderName);
        return IsRuntimeProfileComplete(legacyPath, expectedRuntimeProfile) ? legacyPath : null;
    }

    private string GetQwenNvidiaRuntimePythonPath(NvidiaCudaRuntimeKind runtimeKind)
    {
        var runtimeFolderName = runtimeKind switch
        {
            NvidiaCudaRuntimeKind.Cuda126 => QwenNvidiaCuda126RuntimeFolderName,
            NvidiaCudaRuntimeKind.Cuda130 => QwenNvidiaCuda130RuntimeFolderName,
            _ => throw new ArgumentOutOfRangeException(nameof(runtimeKind), runtimeKind, null),
        };

        return GetManagedRuntimePythonPath(runtimeFolderName);
    }

    private async Task<bool> TryUseExistingRuntimeAsync(
        string runtimePythonPath,
        string runtimeProfile,
        Func<CancellationToken, Task> validateRuntime,
        string readyMessage,
        CancellationToken cancellationToken,
        int? readyPercent = 100)
    {
        if (!IsRuntimeProfileComplete(runtimePythonPath, runtimeProfile))
        {
            return false;
        }

        try
        {
            await validateRuntime(cancellationToken).ConfigureAwait(false);
            ReportProgress(readyMessage, readyPercent);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            MarkRuntimeIncomplete(runtimePythonPath);
            ReportProgress(
                $"Le runtime existant doit etre repare ({SummarizeError(ex.Message)}). Reprise de l'installation...",
                null);
            return false;
        }
    }

    private static string BuildRuntimeProfile(string runtimeKind, string backendTag, string torchVersion)
    {
        return $"{runtimeKind}-{backendTag}-torch-{torchVersion}-installer-{RuntimeProfileVersion}";
    }

    private static bool IsRuntimeProfileComplete(string runtimePythonPath, string expectedProfile)
    {
        if (!PythonRuntimeLocator.IsManagedRuntimeComplete(runtimePythonPath))
        {
            return false;
        }

        try
        {
            var markerPath = PythonRuntimeLocator.GetManagedRuntimeCompleteMarkerPath(runtimePythonPath);
            return string.Equals(
                File.ReadAllText(markerPath).Trim(),
                expectedProfile,
                StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static void MarkRuntimeComplete(string runtimePythonPath, string runtimeProfile)
    {
        var markerPath = PythonRuntimeLocator.GetManagedRuntimeCompleteMarkerPath(runtimePythonPath);
        Directory.CreateDirectory(Path.GetDirectoryName(markerPath) ?? string.Empty);
        File.WriteAllText(markerPath, runtimeProfile);
    }

    private static void MarkRuntimeIncomplete(string runtimePythonPath)
    {
        var markerPath = PythonRuntimeLocator.GetManagedRuntimeCompleteMarkerPath(runtimePythonPath);
        if (File.Exists(markerPath))
        {
            File.Delete(markerPath);
        }
    }

    private static bool AreSamePath(string left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right.Trim().Trim('"')), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<string>> DownloadPytorchPackagesAsync(
        PytorchPackageProfile packageProfile,
        CancellationToken cancellationToken)
    {
        var packagePaths = new List<string>();
        var cacheDirectory = GetPackageDownloadDirectory($"pytorch-{packageProfile.BuildTag}-{PytorchVersion}");

        foreach (var packageName in new[] { "torch", "torchaudio" })
        {
            var source = await ResolvePytorchPackageSourceAsync(
                    packageProfile,
                    packageName,
                    cancellationToken)
                .ConfigureAwait(false);
            var destinationPath = Path.Combine(cacheDirectory, source.FileName);
            await DownloadFileAsync(
                    source,
                    destinationPath,
                    $"Telechargement de {packageName} {packageProfile.DisplayName}",
                    cancellationToken)
                .ConfigureAwait(false);
            packagePaths.Add(destinationPath);
        }

        return packagePaths;
    }

    private async Task<IReadOnlyList<string>> DownloadPackagesAsync(
        IEnumerable<string> urls,
        string cacheKey,
        string labelPrefix,
        CancellationToken cancellationToken)
    {
        var packagePaths = new List<string>();
        var cacheDirectory = GetPackageDownloadDirectory(cacheKey);
        var packageNumber = 0;

        foreach (var url in urls)
        {
            packageNumber++;
            var sourceUri = new Uri(url);
            var fileName = Path.GetFileName(Uri.UnescapeDataString(sourceUri.AbsolutePath));
            if (string.IsNullOrWhiteSpace(fileName))
            {
                throw new InvalidOperationException($"Nom de paquet introuvable dans l'URL : {url}");
            }

            var destinationPath = Path.Combine(cacheDirectory, fileName);
            await DownloadFileAsync(
                    new PackageDownloadSource(sourceUri, fileName, null),
                    destinationPath,
                    $"{labelPrefix} - paquet {packageNumber}",
                    cancellationToken)
                .ConfigureAwait(false);
            packagePaths.Add(destinationPath);
        }

        return packagePaths;
    }

    private async Task<PackageDownloadSource> ResolvePytorchPackageSourceAsync(
        PytorchPackageProfile packageProfile,
        string packageName,
        CancellationToken cancellationToken)
    {
        var fileName =
            $"{packageName}-{PytorchVersion}+{packageProfile.BuildTag}-cp312-cp312-win_amd64.whl";
        var indexUri = new Uri($"{packageProfile.IndexUrl.TrimEnd('/')}/{packageName}/");

        try
        {
            var indexHtml = await GetTextWithRetriesAsync(indexUri, cancellationToken).ConfigureAwait(false);
            foreach (Match match in Regex.Matches(
                         indexHtml,
                         "href\\s*=\\s*[\"'](?<href>[^\"']+)[\"']",
                         RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                var href = WebUtility.HtmlDecode(match.Groups["href"].Value);
                if (!Uri.TryCreate(indexUri, href, out var candidateUri))
                {
                    continue;
                }

                var candidateFileName = Path.GetFileName(
                    Uri.UnescapeDataString(candidateUri.AbsolutePath));
                if (!string.Equals(candidateFileName, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return new PackageDownloadSource(
                    new Uri(candidateUri.GetLeftPart(UriPartial.Path)),
                    fileName,
                    ExtractSha256(candidateUri.Fragment));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReportProgress(
                $"Index PyTorch temporairement indisponible ({SummarizeError(ex.Message)}). Utilisation de l'URL directe...",
                null);
        }

        var escapedFileName = Uri.EscapeDataString(fileName);
        return new PackageDownloadSource(
            new Uri($"{packageProfile.IndexUrl.TrimEnd('/')}/{escapedFileName}"),
            fileName,
            null);
    }

    private static string? ExtractSha256(string fragment)
    {
        foreach (var part in fragment.TrimStart('#').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = part.Split('=', 2);
            if (fields.Length == 2
                && string.Equals(fields[0], "sha256", StringComparison.OrdinalIgnoreCase)
                && fields[1].Length == 64
                && fields[1].All(Uri.IsHexDigit))
            {
                return fields[1].ToUpperInvariant();
            }
        }

        return null;
    }

    private string GetPackageDownloadDirectory(string cacheKey)
    {
        var sanitizedCacheKey = new string(
            cacheKey.Select(character =>
                    char.IsLetterOrDigit(character) || character is '-' or '_' or '.'
                        ? character
                        : '_')
                .ToArray());
        var directory = Path.Combine(
            _appEnvironment.RuntimesDirectory,
            DownloadsFolderName,
            PackagesFolderName,
            sanitizedCacheKey);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private async Task<string> GetTextWithRetriesAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= DownloadAttemptCount; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await SharedHttpClient
                    .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                    .WaitAsync(DownloadConnectionTimeout, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt < DownloadAttemptCount)
                {
                    await Task.Delay(GetRetryDelay(attempt), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw new InvalidOperationException(
            $"Impossible de lire {uri} apres {DownloadAttemptCount} tentatives.",
            lastError);
    }

    private async Task DownloadFileAsync(
        PackageDownloadSource source,
        string destinationPath,
        string label,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)
                                  ?? throw new DirectoryNotFoundException(destinationPath));

        if (File.Exists(destinationPath))
        {
            if (await IsDownloadedPackageValidAsync(destinationPath, source.Sha256, cancellationToken)
                    .ConfigureAwait(false))
            {
                ReportProgress($"{label} deja present dans le cache.", 100);
                return;
            }

            File.Delete(destinationPath);
        }

        var temporaryPath = destinationPath + ".download";
        Exception? lastError = null;
        for (var attempt = 1; attempt <= DownloadAttemptCount; attempt++)
        {
            try
            {
                await DownloadFileAttemptAsync(
                        source.DownloadUri,
                        temporaryPath,
                        label,
                        cancellationToken)
                    .ConfigureAwait(false);
                File.Move(temporaryPath, destinationPath, overwrite: true);

                if (!await IsDownloadedPackageValidAsync(destinationPath, source.Sha256, cancellationToken)
                        .ConfigureAwait(false))
                {
                    File.Delete(destinationPath);
                    throw new InvalidDataException(
                        $"Le fichier telecharge est incomplet ou corrompu : {source.FileName}");
                }

                ReportProgress($"{label} termine.", 100);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt >= DownloadAttemptCount)
                {
                    break;
                }

                var partialSize = File.Exists(temporaryPath)
                    ? new FileInfo(temporaryPath).Length
                    : 0;
                var resumeText = partialSize > 0
                    ? $" Reprise a {FormatByteSize(partialSize)}."
                    : string.Empty;
                ReportProgress(
                    $"{label} interrompu ({SummarizeError(ex.Message)}). Nouvelle tentative {attempt + 1}/{DownloadAttemptCount}.{resumeText}",
                    null);
                await Task.Delay(GetRetryDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException(
            $"{label} a echoue apres {DownloadAttemptCount} tentatives. " +
            "La partie deja recue est conservee et sera reprise au prochain essai.",
            lastError);
    }

    private async Task DownloadFileAttemptAsync(
        Uri uri,
        string temporaryPath,
        string label,
        CancellationToken cancellationToken)
    {
        var existingLength = File.Exists(temporaryPath)
            ? new FileInfo(temporaryPath).Length
            : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (existingLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingLength, null);
        }

        using var response = await SharedHttpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .WaitAsync(DownloadConnectionTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable
            && response.Content.Headers.ContentRange?.Length == existingLength
            && existingLength > 0)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
        var isResuming = existingLength > 0
                         && response.StatusCode == HttpStatusCode.PartialContent;
        var initialLength = isResuming ? existingLength : 0;
        var totalLength = response.Content.Headers.ContentRange?.Length;
        if (totalLength is null && response.Content.Headers.ContentLength is { } responseLength)
        {
            totalLength = initialLength + responseLength;
        }

        var fileMode = isResuming ? FileMode.Append : FileMode.Create;
        await using var output = new FileStream(
            temporaryPath,
            fileMode,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 128,
            useAsync: true);
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[1024 * 128];
        var totalRead = initialLength;
        var lastPercent = -1;
        ReportProgress(label, totalLength is > 0
            ? (int)Math.Clamp(totalRead * 100 / totalLength.Value, 0, 100)
            : 0);

        while (true)
        {
            var bytesRead = await input
                .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .AsTask()
                .WaitAsync(DownloadIdleTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            totalRead += bytesRead;

            if (totalLength is > 0)
            {
                var percent = (int)Math.Clamp(totalRead * 100 / totalLength.Value, 0, 100);
                if (percent != lastPercent)
                {
                    ReportProgress(label, percent);
                    lastPercent = percent;
                }
            }
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (totalLength is > 0 && output.Length != totalLength.Value)
        {
            throw new EndOfStreamException(
                $"Le serveur annonçait {totalLength.Value} octets, mais {output.Length} ont ete reçus.");
        }
    }

    private static async Task<bool> IsDownloadedPackageValidAsync(
        string path,
        string? expectedSha256,
        CancellationToken cancellationToken)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists || fileInfo.Length <= 0)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                await using var hashStream = File.OpenRead(path);
                var actualHash = Convert.ToHexString(
                    await SHA256.HashDataAsync(hashStream, cancellationToken).ConfigureAwait(false));
                if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            if (path.EndsWith(".whl", StringComparison.OrdinalIgnoreCase))
            {
                using var archive = ZipFile.OpenRead(path);
                return archive.Entries.Count > 0;
            }

            if (path.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                await using var archiveStream = File.OpenRead(path);
                await using var gzipStream = new GZipStream(archiveStream, CompressionMode.Decompress);
                var probe = new byte[1];
                return await gzipStream.ReadAsync(probe, cancellationToken).ConfigureAwait(false) > 0;
            }

            if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                if (fileInfo.Length < 1024 * 1024)
                {
                    return false;
                }

                await using var executable = File.OpenRead(path);
                var signature = new byte[2];
                return await executable.ReadAsync(signature, cancellationToken).ConfigureAwait(false) == 2
                       && signature[0] == (byte)'M'
                       && signature[1] == (byte)'Z';
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static TimeSpan GetRetryDelay(int attempt)
    {
        return TimeSpan.FromSeconds(Math.Min(Math.Pow(2, Math.Max(attempt - 1, 0)), 15));
    }

    private static string FormatByteSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024 / 1024:0.##} Go";
        }

        if (bytes >= 1024L * 1024)
        {
            return $"{bytes / 1024d / 1024:0.##} Mo";
        }

        return $"{bytes / 1024d:0.##} Ko";
    }

    private static string SummarizeError(string value)
    {
        var singleLine = Regex.Replace(value ?? string.Empty, "\\s+", " ").Trim();
        if (singleLine.Length <= 240)
        {
            return singleLine;
        }

        return singleLine[..237] + "...";
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Voxcribe-Runtime-Installer/1.0");
        return client;
    }

    private void ReportProgress(string message, int? percent)
    {
        ProgressChanged?.Invoke(this, new RuntimeInstallProgress(message, percent));
    }

    private static async Task RunProcessAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken,
        params int[] successExitCodes)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"La commande {fileName} n'a pas pu demarrer.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode == 0 || successExitCodes.Contains(process.ExitCode))
        {
            return;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        var details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(details) ? "Erreur inconnue." : details.Trim());
    }

    private static async Task<(string Stdout, string Stderr)> RunProcessCaptureAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"La commande {fileName} n'a pas pu demarrer.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(details) ? "Erreur inconnue." : details.Trim());
        }

        return (stdout, stderr);
    }

    private static string Quote(string value)
    {
        return $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private static async Task WaitForFileAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                return;
            }

            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record PytorchPackageProfile(string IndexUrl, string BuildTag, string DisplayName);

    private sealed record PackageDownloadSource(Uri DownloadUri, string FileName, string? Sha256);

    private sealed class CudaValidationPayload
    {
        public string? TorchVersion { get; set; }

        public string? CudaVersion { get; set; }

        public string? HipVersion { get; set; }

        public bool CudaAvailable { get; set; }

        public bool CudaUsable { get; set; }

        public List<CudaValidationDevice> Devices { get; set; } = [];
    }

    private sealed class CudaValidationDevice
    {
        public string Name { get; set; } = "GPU";

        public string? ComputeCapability { get; set; }

        public string? Error { get; set; }
    }
}

public sealed record RuntimeInstallProgress(string Message, int? Percent);
