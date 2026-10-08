param(
    [string]$ModelId = "mistralai/Voxtral-Mini-3B-2507",
    [string]$TorchIndexUrl = "https://download.pytorch.org/whl/cu126",
    [string]$PythonExe = "",
    [switch]$PreloadModel,
    [switch]$IncludeQwenRuntime,
    [switch]$RecreateVenv
)

$ErrorActionPreference = "Stop"

function Resolve-Python {
    param(
        [string]$ExplicitPythonExe
    )

    if ($ExplicitPythonExe) {
        if (-not (Test-Path $ExplicitPythonExe)) {
            throw "Python executable not found: $ExplicitPythonExe"
        }

        return @{
            Exe = $ExplicitPythonExe
            Args = @()
        }
    }

    $py = Get-Command py -ErrorAction SilentlyContinue
    if ($py) {
        foreach ($tag in @("-3.12", "-3.13", "-3.14", "-3.11")) {
            try {
                & $py.Source $tag -c "import sys; print(sys.version)" | Out-Null
                return @{
                    Exe = $py.Source
                    Args = @($tag)
                }
            }
            catch {
            }
        }
    }

    $python = Get-Command python -ErrorAction SilentlyContinue
    if ($python) {
        return @{
            Exe = $python.Source
            Args = @()
        }
    }

    throw "The Voxtral backend requires Python 3.12 to 3.14, or an explicit -PythonExe path."
}

$projectRoot = Split-Path $PSScriptRoot -Parent
$backendRoot = Join-Path $projectRoot "backend"
$venvRoot = Join-Path $backendRoot ".venv"
$qwenVenvRoot = Join-Path $backendRoot ".venv-qwen"
$pythonInfo = Resolve-Python -ExplicitPythonExe $PythonExe

if ($RecreateVenv -and (Test-Path $venvRoot)) {
    Write-Host "Recreating the Python virtual environment..."
    Remove-Item -Recurse -Force $venvRoot
}

if ($RecreateVenv -and (Test-Path $qwenVenvRoot)) {
    Write-Host "Recreating the Qwen Python virtual environment..."
    Remove-Item -Recurse -Force $qwenVenvRoot
}

if (-not (Test-Path $venvRoot)) {
    Write-Host "Creating the Python virtual environment..."
    & $pythonInfo.Exe @($pythonInfo.Args) -m venv $venvRoot
}

$venvPython = Join-Path $venvRoot "Scripts\python.exe"
$venvPip = Join-Path $venvRoot "Scripts\pip.exe"
$requirements = Join-Path $backendRoot "requirements.txt"

Write-Host "Updating pip..."
& $venvPython -m pip install --upgrade pip setuptools wheel

Write-Host "Installing PyTorch CUDA..."
& $venvPip install --index-url $TorchIndexUrl --extra-index-url "https://pypi.org/simple" torch torchaudio

Write-Host "Installing Voxtral dependencies..."
& $venvPip install -r $requirements

if ($IncludeQwenRuntime) {
    Write-Host "Creating the separate Qwen runtime..."
    if (-not (Test-Path $qwenVenvRoot)) {
        & $pythonInfo.Exe @($pythonInfo.Args) -m venv $qwenVenvRoot
    }

    $qwenPython = Join-Path $qwenVenvRoot "Scripts\python.exe"
    $qwenPip = Join-Path $qwenVenvRoot "Scripts\pip.exe"
    & $qwenPython -m pip install --upgrade pip setuptools wheel
    & $qwenPip install --index-url $TorchIndexUrl --extra-index-url "https://pypi.org/simple" torch torchaudio
    & $qwenPip install --upgrade --upgrade-strategy only-if-needed "av>=19.0.1,<20" soundfile librosa qwen-asr
}

if ($PreloadModel) {
    Write-Host "Preloading model $ModelId ..."
    $code = "from huggingface_hub import snapshot_download; snapshot_download(repo_id=r'$ModelId')"
    & $venvPython -c $code
}

Write-Host ""
Write-Host "Backend ready."
Write-Host "Python : $venvPython"
Write-Host "Model: $ModelId"
if ($IncludeQwenRuntime) {
    Write-Host "Qwen runtime: $(Join-Path $qwenVenvRoot "Scripts\python.exe")"
}
Write-Host ""
Write-Host "You can now launch the application with:"
Write-Host "dotnet run --project .\src\TranscriptionOverlay\TranscriptionOverlay.csproj"
