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
            throw "Python introuvable : $ExplicitPythonExe"
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

    throw "Python 3.12 a 3.14 est requis pour le backend Voxtral, ou fournissez -PythonExe."
}

$projectRoot = Split-Path $PSScriptRoot -Parent
$backendRoot = Join-Path $projectRoot "backend"
$venvRoot = Join-Path $backendRoot ".venv"
$qwenVenvRoot = Join-Path $backendRoot ".venv-qwen"
$pythonInfo = Resolve-Python -ExplicitPythonExe $PythonExe

if ($RecreateVenv -and (Test-Path $venvRoot)) {
    Write-Host "Recreation du venv Python..."
    Remove-Item -Recurse -Force $venvRoot
}

if ($RecreateVenv -and (Test-Path $qwenVenvRoot)) {
    Write-Host "Recreation du venv Python Qwen..."
    Remove-Item -Recurse -Force $qwenVenvRoot
}

if (-not (Test-Path $venvRoot)) {
    Write-Host "Creation du venv Python..."
    & $pythonInfo.Exe @($pythonInfo.Args) -m venv $venvRoot
}

$venvPython = Join-Path $venvRoot "Scripts\python.exe"
$venvPip = Join-Path $venvRoot "Scripts\pip.exe"
$requirements = Join-Path $backendRoot "requirements.txt"

Write-Host "Mise a jour de pip..."
& $venvPython -m pip install --upgrade pip setuptools wheel

Write-Host "Installation de PyTorch CUDA..."
& $venvPip install --index-url $TorchIndexUrl --extra-index-url "https://pypi.org/simple" torch torchaudio

Write-Host "Installation des dependances Voxtral..."
& $venvPip install -r $requirements

if ($IncludeQwenRuntime) {
    Write-Host "Creation du runtime Qwen separe..."
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
    Write-Host "Prechargement du modele $ModelId ..."
    $code = "from huggingface_hub import snapshot_download; snapshot_download(repo_id=r'$ModelId')"
    & $venvPython -c $code
}

Write-Host ""
Write-Host "Backend pret."
Write-Host "Python : $venvPython"
Write-Host "Modele : $ModelId"
if ($IncludeQwenRuntime) {
    Write-Host "Runtime Qwen : $(Join-Path $qwenVenvRoot "Scripts\python.exe")"
}
Write-Host ""
Write-Host "Vous pouvez maintenant lancer l'app avec :"
Write-Host "dotnet run --project .\src\TranscriptionOverlay\TranscriptionOverlay.csproj"
