param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path $PSScriptRoot -Parent
$projectFile = Join-Path $projectRoot "src\TranscriptionOverlay\TranscriptionOverlay.csproj"
[xml]$project = Get-Content -LiteralPath $projectFile -Raw
$version = [string]$project.Project.PropertyGroup.Version

if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "The project file must define a Version in the format 1.0.0."
}

$releaseRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot "dist\release-$version"))
$packageRoot = [System.IO.Path]::GetFullPath((Join-Path $releaseRoot "Voxcribe"))
$allowedRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot "dist")) + [System.IO.Path]::DirectorySeparatorChar
if (-not $packageRoot.StartsWith($allowedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "The package directory must remain inside dist."
}

# This output is isolated from the user's existing Voxcribe application and data.
& (Join-Path $PSScriptRoot "build.ps1") -Configuration $Configuration -OutputDir $packageRoot -ExcludePortablePythonRuntime -ResetPortableState -KeepIntermediateBuildOutput

Copy-Item -LiteralPath (Join-Path $projectRoot "docs\PORTABLE_README.md") -Destination (Join-Path $packageRoot "README.md")
Copy-Item -LiteralPath (Join-Path $projectRoot "LICENSE") -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $projectRoot "THIRD_PARTY_NOTICES.md") -Destination $packageRoot
Copy-Item -LiteralPath (Join-Path $projectRoot "docs\licenses") -Destination (Join-Path $packageRoot "licenses") -Recurse
Set-Content -LiteralPath (Join-Path $packageRoot "VERSION.txt") -Value $version -Encoding utf8

foreach ($required in @(
    "Voxcribe.exe",
    "Voxcribe.dll",
    "Voxcribe.deps.json",
    "Voxcribe.runtimeconfig.json",
    "coreclr.dll",
    "PresentationFramework.dll",
    "backend\voxtral_worker.py",
    "backend\model_manager.py",
    "backend\media_audio.py",
    "backend\phonon_runtime.py",
    "backend\crisperwhisper_compat.py",
    "backend\requirements.txt",
    "README.md",
    "LICENSE",
    "licenses\NAudio-LICENSE.txt",
    "licenses\DOTNET-LICENSE.txt",
    "licenses\DOTNET-THIRD-PARTY-NOTICES.txt"
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageRoot $required) -PathType Leaf)) {
        throw "The package is incomplete: $required"
    }
}

foreach ($forbidden in @("data", "models", "python-runtime", "backend\.venv", "backend\.venv-qwen", "backend\tests")) {
    if (Test-Path -LiteralPath (Join-Path $packageRoot $forbidden)) {
        throw "The public package must not contain: $forbidden"
    }
}

# A stable asset name keeps the README's /releases/latest/download/ URL working.
$archivePath = Join-Path $releaseRoot "Voxcribe-Windows-x64.zip"
Compress-Archive -LiteralPath $packageRoot -DestinationPath $archivePath -Force
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = Join-Path $releaseRoot "SHA256SUMS.txt"
Set-Content -LiteralPath $checksumPath -Value "$hash  Voxcribe-Windows-x64.zip" -Encoding ascii

Write-Host ""
Write-Host "Version : $version"
Write-Host "Archive : $archivePath"
Write-Host "SHA-256 : $hash"
