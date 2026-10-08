param(
    [string]$Configuration = "Release",
    [string]$OutputDir = "",
    [switch]$FrameworkDependent,
    [string]$PortablePythonHome = "",
    [switch]$IncludePortablePythonRuntime,
    [switch]$ExcludePortablePythonRuntime,
    [switch]$IncludeManagedModels,
    [switch]$ResetPortableState,
    [switch]$KeepIntermediateBuildOutput
)

$ErrorActionPreference = "Stop"

if ($IncludePortablePythonRuntime -and $ExcludePortablePythonRuntime) {
    throw "Utilisez soit -IncludePortablePythonRuntime, soit -ExcludePortablePythonRuntime, pas les deux."
}

$includePortablePythonRuntime = -not $ExcludePortablePythonRuntime `
    -and ($IncludePortablePythonRuntime -or -not [string]::IsNullOrWhiteSpace($PortablePythonHome))

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory,
        [Parameter(Mandatory = $true)]
        [string]$DestinationDirectory,
        [string[]]$ExcludeNames = @()
    )

    if (-not (Test-Path $SourceDirectory)) {
        throw "Source introuvable : $SourceDirectory"
    }

    New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null

    Get-ChildItem $SourceDirectory -Force |
        Where-Object { $ExcludeNames -notcontains $_.Name } |
        ForEach-Object {
            Copy-Item $_.FullName (Join-Path $DestinationDirectory $_.Name) -Recurse -Force
        }
}

function Resolve-PortablePythonHome {
    param(
        [string]$ExplicitHome,
        [string]$VenvRoot,
        [string]$FallbackHome
    )

    if ($ExplicitHome) {
        if (-not (Test-Path $ExplicitHome)) {
            throw "Runtime Python portable introuvable : $ExplicitHome"
        }

        return $ExplicitHome
    }

    $pyvenvPath = Join-Path $VenvRoot "pyvenv.cfg"
    if (-not (Test-Path $pyvenvPath)) {
        if ($FallbackHome -and (Test-Path $FallbackHome)) {
            return $FallbackHome
        }

        throw "Impossible de retrouver le Python source. pyvenv.cfg introuvable : $pyvenvPath"
    }

    $homeLine = Get-Content $pyvenvPath |
        Where-Object { $_ -like "home = *" } |
        Select-Object -First 1

    if (-not $homeLine) {
        throw "La ligne 'home =' est introuvable dans $pyvenvPath"
    }

    $resolvedHome = $homeLine.Substring("home = ".Length).Trim()
    if (-not (Test-Path $resolvedHome)) {
        throw "Le runtime Python indique dans pyvenv.cfg est introuvable : $resolvedHome"
    }

    return $resolvedHome
}

function Resolve-SitePackagesSource {
    param(
        [string]$VenvSitePackages,
        [string]$PortablePythonSource
    )

    if (Test-Path $VenvSitePackages) {
        return $VenvSitePackages
    }

    $runtimeSitePackages = Join-Path $PortablePythonSource "Lib\site-packages"
    if (Test-Path $runtimeSitePackages) {
        return $runtimeSitePackages
    }

    throw "Les dépendances Python du backend sont introuvables. Ni $VenvSitePackages ni $runtimeSitePackages n'existent."
}

function Copy-OverlayPackages {
    param(
        [string]$SourceSitePackages,
        [string]$DestinationDirectory
    )

    if (-not (Test-Path $SourceSitePackages)) {
        throw "Source overlay introuvable : $SourceSitePackages"
    }

    if (Test-Path $DestinationDirectory) {
        Remove-Item $DestinationDirectory -Recurse -Force
    }

    New-Item -ItemType Directory -Path $DestinationDirectory -Force | Out-Null

    $patterns = @(
        "transformers",
        "transformers-*.dist-info",
        "huggingface_hub",
        "huggingface_hub-*.dist-info"
    )

    foreach ($pattern in $patterns) {
        Get-ChildItem $SourceSitePackages -Filter $pattern -Force -ErrorAction SilentlyContinue |
            ForEach-Object {
                Copy-Item $_.FullName (Join-Path $DestinationDirectory $_.Name) -Recurse -Force
            }
    }
}

function Remove-DirectoryIfExists {
    param(
        [string]$Path
    )

    if (Test-Path $Path) {
        Remove-Item $Path -Recurse -Force
    }
}

function Remove-FileIfExists {
    param(
        [string]$Path
    )

    if (Test-Path $Path) {
        Remove-Item $Path -Force
    }
}

function Remove-DirectoriesByName {
    param(
        [string]$RootDirectory,
        [string[]]$DirectoryNames
    )

    if (-not (Test-Path $RootDirectory)) {
        return
    }

    Get-ChildItem $RootDirectory -Directory -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object { $DirectoryNames -contains $_.Name } |
        Sort-Object FullName -Descending |
        ForEach-Object {
            Remove-DirectoryIfExists -Path $_.FullName
        }
}

function Remove-PatternMatches {
    param(
        [string]$RootDirectory,
        [string[]]$Patterns
    )

    if (-not (Test-Path $RootDirectory)) {
        return
    }

    foreach ($pattern in $Patterns) {
        Get-ChildItem $RootDirectory -Force -Filter $pattern -ErrorAction SilentlyContinue |
            ForEach-Object {
                if ($_.PSIsContainer) {
                    Remove-DirectoryIfExists -Path $_.FullName
                }
                else {
                    Remove-FileIfExists -Path $_.FullName
                }
            }
    }
}

function Prune-PortablePythonRuntime {
    param(
        [string]$RuntimeRoot
    )

    if (-not (Test-Path $RuntimeRoot)) {
        return
    }

    foreach ($relativePath in @(
            "Doc",
            "include",
            "libs",
            "tcl",
            "Lib\\ensurepip",
            "Lib\\idlelib",
            "Lib\\venv",
            "Lib\\pydoc_data",
            "Lib\\tkinter")) {
        Remove-DirectoryIfExists -Path (Join-Path $RuntimeRoot $relativePath)
    }

    Remove-DirectoriesByName -RootDirectory $RuntimeRoot -DirectoryNames @("__pycache__", ".pytest_cache")

    $sitePackagesRoot = Join-Path $RuntimeRoot "Lib\\site-packages"
    Remove-PatternMatches -RootDirectory $sitePackagesRoot -Patterns @(
        "gradio",
        "gradio-*.dist-info",
        "gradio_client",
        "gradio_client-*.dist-info",
        "flask",
        "flask-*.dist-info",
        "qwen_asr",
        "qwen_asr-*.dist-info"
    )
}

function Prune-PythonOverlay {
    param(
        [string]$OverlayRoot
    )

    if (-not (Test-Path $OverlayRoot)) {
        return
    }

    Remove-DirectoriesByName -RootDirectory $OverlayRoot -DirectoryNames @("__pycache__", ".pytest_cache")
}

function Save-DirectoryToTemp {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory
    )

    $tempDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("voxcribe-overlay-" + [guid]::NewGuid().ToString("N"))
    Copy-DirectoryContents -SourceDirectory $SourceDirectory -DestinationDirectory $tempDirectory
    return $tempDirectory
}

function Move-PortableStateToAppData {
    param(
        [string]$SourceRoot,
        [string]$DestinationRoot
    )

    if (-not $SourceRoot -or -not (Test-Path $SourceRoot)) {
        return
    }

    foreach ($stateFolderName in @("data", "models")) {
        $sourceDirectory = Join-Path $SourceRoot $stateFolderName
        if (-not (Test-Path $sourceDirectory)) {
            continue
        }

        $destinationDirectory = Join-Path $DestinationRoot $stateFolderName
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null

        Get-ChildItem -LiteralPath $sourceDirectory -Force |
            ForEach-Object {
                $destinationPath = Join-Path $destinationDirectory $_.Name
                if (-not (Test-Path $destinationPath)) {
                    Move-Item -LiteralPath $_.FullName -Destination $destinationPath
                }
            }

        if (-not (Get-ChildItem -LiteralPath $sourceDirectory -Force -ErrorAction SilentlyContinue)) {
            Remove-DirectoryIfExists -Path $sourceDirectory
        }
    }
}

$projectRoot = Split-Path $PSScriptRoot -Parent
$projectFile = Join-Path $projectRoot "src\TranscriptionOverlay\TranscriptionOverlay.csproj"
$publishRoot = if ($OutputDir) { $OutputDir } else { Join-Path $projectRoot "Voxcribe" }
$legacyPublishRoots = @(
    (Join-Path $projectRoot "Voxcribe-fixed"),
    (Join-Path $projectRoot "dist\Voxcribe"),
    (Join-Path $projectRoot "dist\TranscriptionOverlay")
)
$intermediateBuildRoots = @(
    (Join-Path $projectRoot "src\TranscriptionOverlay\bin"),
    (Join-Path $projectRoot "src\TranscriptionOverlay\obj")
)
$selfContainedValue = if ($FrameworkDependent) { "false" } else { "true" }
$backendSource = Join-Path $projectRoot "backend"
$backendPublish = Join-Path $publishRoot "backend"
$venvRoot = Join-Path $backendSource ".venv"
$venvSitePackages = Join-Path $venvRoot "Lib\site-packages"
$pythonRuntimePublish = Join-Path $publishRoot "python-runtime"
$runtimeOverlaysPublish = Join-Path $publishRoot "runtime-overlays"
$voxtralRealtimeOverlayPublish = Join-Path $runtimeOverlaysPublish "voxtral-realtime"
$pythonRuntimeSitePackages = Join-Path $pythonRuntimePublish "Lib\site-packages"
$modelsSource = Join-Path $projectRoot "models"
$modelsPublish = Join-Path $publishRoot "models"
$scriptsPublish = Join-Path $publishRoot "scripts"
$huggingFaceHubRequirement = "huggingface-hub>=1.3.0,<2.0"
$existingPortablePythonHome = Join-Path $publishRoot "python-runtime"
$legacyPortablePythonHome = Join-Path $projectRoot "dist\Voxcribe\python-runtime"
$stagedPortablePythonHome = ""
$fallbackPortablePythonHome = ""
$existingOverlayPackageSource = Join-Path $publishRoot "runtime-overlays\voxtral-realtime"
$fallbackOverlayPackageSource = Join-Path $projectRoot "dist\Voxcribe\runtime-overlays\voxtral-realtime"
$stagedOverlayPackageSource = ""
$overlayPackageSource = ""

if ($includePortablePythonRuntime) {
    if (-not $PortablePythonHome -and -not (Test-Path (Join-Path $venvRoot "pyvenv.cfg"))) {
        if (Test-Path $existingPortablePythonHome) {
            $stagedPortablePythonHome = Save-DirectoryToTemp -SourceDirectory $existingPortablePythonHome
            $fallbackPortablePythonHome = $stagedPortablePythonHome
        }
        elseif ($existingPortablePythonHome -ne $legacyPortablePythonHome -and (Test-Path $legacyPortablePythonHome)) {
            $stagedPortablePythonHome = Save-DirectoryToTemp -SourceDirectory $legacyPortablePythonHome
            $fallbackPortablePythonHome = $stagedPortablePythonHome
        }
    }

    if (Test-Path $existingOverlayPackageSource) {
        $stagedOverlayPackageSource = Save-DirectoryToTemp -SourceDirectory $existingOverlayPackageSource
        $overlayPackageSource = $stagedOverlayPackageSource
    }
    elseif ($existingOverlayPackageSource -ne $fallbackOverlayPackageSource -and (Test-Path $fallbackOverlayPackageSource)) {
        $stagedOverlayPackageSource = Save-DirectoryToTemp -SourceDirectory $fallbackOverlayPackageSource
        $overlayPackageSource = $stagedOverlayPackageSource
    }

    $portablePythonSource = Resolve-PortablePythonHome -ExplicitHome $PortablePythonHome -VenvRoot $venvRoot -FallbackHome $fallbackPortablePythonHome
    $pythonSitePackagesSource = Resolve-SitePackagesSource -VenvSitePackages $venvSitePackages -PortablePythonSource $portablePythonSource
}
$nugetConfigPath = Join-Path $projectRoot "NuGet.Config"
$env:APPDATA = Join-Path $projectRoot ".appdata"
New-Item -ItemType Directory -Path $env:APPDATA -Force | Out-Null

if (-not $ResetPortableState) {
    $portableStateSourceRoot = ""
    if (Test-Path $publishRoot) {
        $portableStateSourceRoot = $publishRoot
    }
    else {
        foreach ($legacyPublishRoot in $legacyPublishRoots) {
            if (Test-Path $legacyPublishRoot) {
                $portableStateSourceRoot = $legacyPublishRoot
                break
            }
        }
    }

    $localAppDataRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) "Voxcribe"
    Move-PortableStateToAppData -SourceRoot $portableStateSourceRoot -DestinationRoot $localAppDataRoot
}

if (Test-Path $publishRoot) {
    Remove-Item $publishRoot -Recurse -Force
}

if (-not $OutputDir) {
    foreach ($legacyPublishRoot in $legacyPublishRoots) {
        if ($legacyPublishRoot -ne $publishRoot -and (Test-Path $legacyPublishRoot)) {
            Remove-Item $legacyPublishRoot -Recurse -Force
        }
    }

    $legacyDistRoot = Join-Path $projectRoot "dist"
    if ((Test-Path $legacyDistRoot) -and -not (Get-ChildItem $legacyDistRoot -Force -ErrorAction SilentlyContinue)) {
        Remove-Item $legacyDistRoot -Force
    }
}

if (Test-Path $nugetConfigPath) {
    & "C:\Program Files\dotnet\dotnet.exe" restore $projectFile `
        -r win-x64 `
        --configfile $nugetConfigPath
}
else {
    & "C:\Program Files\dotnet\dotnet.exe" restore $projectFile `
        -r win-x64
}

if ($LASTEXITCODE -ne 0) {
    throw "La restauration des dependances .NET a echoue."
}

& "C:\Program Files\dotnet\dotnet.exe" publish $projectFile `
    -c $Configuration `
    -r win-x64 `
    --self-contained $selfContainedValue `
    --no-restore `
    -o $publishRoot

if ($LASTEXITCODE -ne 0) {
    throw "La compilation de Voxcribe a echoue. Aucun package ne doit etre publie."
}

if (Test-Path $backendSource) {
    Copy-DirectoryContents -SourceDirectory $backendSource -DestinationDirectory $backendPublish -ExcludeNames @(".venv", ".venv-qwen", "__pycache__", "tests")
}

if ($IncludeManagedModels -and (Test-Path $modelsSource)) {
    Copy-Item $modelsSource $modelsPublish -Recurse -Force
}

if ($includePortablePythonRuntime) {
    Copy-DirectoryContents -SourceDirectory $portablePythonSource -DestinationDirectory $pythonRuntimePublish

    if (Test-Path $pythonRuntimeSitePackages) {
        Remove-Item $pythonRuntimeSitePackages -Recurse -Force
    }

    Copy-DirectoryContents -SourceDirectory $pythonSitePackagesSource -DestinationDirectory $pythonRuntimeSitePackages -ExcludeNames @("__pycache__")

    & (Join-Path $pythonRuntimePublish "python.exe") -m pip install --upgrade --upgrade-strategy only-if-needed $huggingFaceHubRequirement "av>=19.0.1,<20"
    if ($LASTEXITCODE -ne 0) {
        throw "Impossible d'installer huggingface-hub et le decodeur audio/video PyAV dans le Python portable."
    }

    if ($overlayPackageSource -and (Test-Path $overlayPackageSource)) {
        Copy-OverlayPackages -SourceSitePackages $overlayPackageSource -DestinationDirectory $voxtralRealtimeOverlayPublish
    }
    else {
        New-Item -ItemType Directory -Path $voxtralRealtimeOverlayPublish -Force | Out-Null
        & (Join-Path $pythonRuntimePublish "python.exe") -m pip install --upgrade --no-deps --target $voxtralRealtimeOverlayPublish transformers==5.3.0 $huggingFaceHubRequirement
        if ($LASTEXITCODE -ne 0) {
            throw "Impossible de reconstruire la surcouche Voxtral Realtime."
        }
    }

    & (Join-Path $pythonRuntimePublish "python.exe") -m pip install --upgrade --no-deps --target $voxtralRealtimeOverlayPublish $huggingFaceHubRequirement
    if ($LASTEXITCODE -ne 0) {
        throw "Impossible d'installer une version compatible de huggingface-hub dans la surcouche Voxtral Realtime."
    }

    Prune-PortablePythonRuntime -RuntimeRoot $pythonRuntimePublish
    Prune-PythonOverlay -OverlayRoot $voxtralRealtimeOverlayPublish
}

if (-not $IncludeManagedModels) {
    Remove-DirectoryIfExists -Path $modelsPublish
}

if ($stagedOverlayPackageSource -and (Test-Path $stagedOverlayPackageSource)) {
    Remove-Item $stagedOverlayPackageSource -Recurse -Force
}

if ($stagedPortablePythonHome -and (Test-Path $stagedPortablePythonHome)) {
    Remove-Item $stagedPortablePythonHome -Recurse -Force
}

$setupScriptSource = Join-Path $projectRoot "scripts\setup_backend.ps1"
if (Test-Path $setupScriptSource) {
    New-Item -ItemType Directory -Path $scriptsPublish -Force | Out-Null
    Copy-Item $setupScriptSource (Join-Path $scriptsPublish "setup_backend.ps1") -Force
}

if (-not $KeepIntermediateBuildOutput) {
    foreach ($intermediateBuildRoot in $intermediateBuildRoots) {
        Remove-DirectoryIfExists -Path $intermediateBuildRoot
    }
}

Write-Host ""
Write-Host "Publish pret : $publishRoot"
