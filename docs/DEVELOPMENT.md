# Voxcribe

Voxcribe is a compact Windows overlay for fully local speech transcription.

It can:

- stay always on top
- record from a microphone or another Windows audio source
- transcribe locally with downloadable ASR models
- work in standard mode or real-time mode, depending on the selected model
- open transcripts in a dedicated text window
- import existing audio and video files
- run as a portable package that can be shared with other Windows users

## Current capabilities

- Compact WPF overlay with always-on-top behavior
- Start / stop transcription from the overlay
- Minimize to the Windows notification area
- Global hotkey support
- Interface language selection: French or English
- Audio source selection
- Transcript window with selectable text and copy button
- Optional automatic transcript window opening
- Audio/video file import from button or drag and drop, with automatic audio-track extraction
- Local model management from the settings window
- Per-model mode memory:
  - if a model supports both `Standard` and `Real-time`, Voxcribe remembers the last selected mode for that model

## Transcription modes

### Standard

Used for classic record-then-transcribe workflows and for imported audio/video files.

File import supports the audio and video containers/codecs readable by the local
PyAV/FFmpeg decoder, including WAV, MP3, FLAC, OGG/Opus, AAC/M4A, WMA, AIFF,
MP4, MKV, MOV, AVI, WebM and MPEG/TS. Video images are not processed; Voxcribe
transcribes the first audio track. Stereo and multichannel tracks are converted
to mono. No separate FFmpeg installation or online conversion is required.
The file picker offers audio/video, audio-only, video-only and all-file filters.
Unusual or missing file extensions are accepted and checked by the decoder.

No application can guarantee every proprietary, protected or damaged format.
Files without an audio track or with an unsupported codec produce an explicit
error. On an older runtime, rerun the appropriate runtime installer in settings
to add the PyAV decoder; new runtime installations include it automatically.

Behavior depends on the transcript window setting:

- if automatic transcript-window opening is disabled, Voxcribe inserts the text at the current cursor position when possible
- if automatic transcript-window opening is enabled, Voxcribe opens the transcript window instead of copying or pasting automatically

### Real-time

Available only for models that support it.

- the transcript is updated live during recording
- Voxcribe uses the transcript window for live output
- if automatic transcript-window opening is enabled, the window opens once at the start of the run
- after that, the user can close or reopen it manually without Voxcribe forcing it open again

## Models currently defined in the project

The application filters downloadable models by operating system at runtime.

Models currently defined in the catalog:

- `Phonon-2`
- `Qwen3-ASR-1.7B`
- `Qwen3-ASR-0.6B`
- `CrisperWhisper 2.0 Small`
- `CrisperWhisper 2.0 Medium`
- `CrisperWhisper 2.0 Turbo`
- `CrisperWhisper 2.0 Large`
- `Whisper large-v3`
- `Voxtral Mini 3B`
- `Voxtral Mini 4B Realtime`
- `Voxtral Small 24B`
- `Canary-1B-v2`
- `Parakeet-TDT-0.6B-v3`

On Windows, the UI only shows models that are marked usable on Windows.

Phonon-2 transcribes **English only**, in standard mode, using Fermion's local
CPU engine on Windows. It downloads approximately 164 MB and does not require a
GPU. Existing Python runtimes can install its small additional component from
the model settings. Its byte-plane archive is verified against the release
SHA-256 and restored with Fermion's own unpacker on first use. The weights use
[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/); the downloaded package
retains the Fermion Research / NVIDIA attribution notice and both license files.

The public CrisperWhisper 2.0 checkpoints run locally through the portable
PyTorch/Transformers backend and use their native verbatim transcription and
long-form processing. They do not support Voxcribe's real-time mode. Their
weights use the [Nyra Health Non-Commercial Research License](https://huggingface.co/nyralabs/CrisperWhisper2.0_large/blob/main/LICENSE.md):
research and non-commercial use are allowed, while commercial use requires a
separate license from Nyra. The [`crisperwhisper` inference package](https://github.com/nyrahealth/CrisperWhisper)
itself is MIT-licensed.

## Project layout

### Runtime / distribution

- `Voxcribe/`
  - the portable application to run or share

### Source code

- [`src/TranscriptionOverlay`](../src/TranscriptionOverlay)
  - WPF application source
- [`backend`](../backend)
  - Python worker source and model download helper

### Build / setup

- [`scripts/build.ps1`](../scripts/build.ps1)
  - builds and publishes the portable package
- [`scripts/setup_backend.ps1`](../scripts/setup_backend.ps1)
  - prepares the development Python backend

### Development-only files

- [`TranscriptionOverlay.sln`](../TranscriptionOverlay.sln)
  - Visual Studio solution
- [`NuGet.Config`](../NuGet.Config)
  - NuGet restore configuration
- `.appdata/`
  - temporary build-time application data used during publishing
- `src/TranscriptionOverlay/bin`
  - intermediate build output
- `src/TranscriptionOverlay/obj`
  - intermediate build output
- `backend/.venv`
  - development Python virtual environment
- `backend/__pycache__`
  - Python cache files

## Portable package

The published package is generated in:

- `Voxcribe/`

It includes:

- the self-contained .NET application
- the embedded Python worker source
- no machine-specific Python / PyTorch runtime by default
- the in-app NVIDIA, AMD, and CPU runtime installer

The default lightweight package does not include a multi-GB Python / PyTorch runtime. Users install the appropriate runtime from the settings window, and Voxcribe stores it under `%LocalAppData%\\Voxcribe\\runtimes`. Use `-IncludePortablePythonRuntime` only when intentionally creating a much larger package tied to the locally prepared Python runtime.

Runtime downloads are resumable. Large Python, PyTorch, CUDA, and ROCm packages are cached under `%LocalAppData%\\Voxcribe\\runtimes\\downloads`, so a network interruption or a later repair continues from the data already downloaded instead of starting again from zero. A runtime is marked complete only after its Python dependencies pass `pip check` and a real GPU kernel succeeds.

The PyTorch GPU diagnostic is cached and reruns only when the active runtime changes. The NVIDIA installer separately queries `nvidia-smi` when the NVIDIA button is pressed so a stale runtime can be repaired after the application or the hardware changes. Exact, already validated runtime profiles are reused without downloading their packages again. Switching among compatible installed runtimes restarts only the transcription worker.

By default, managed downloadable models are not bundled into the published folder. This keeps the shared package much smaller. Users can then download models on demand from inside Voxcribe.

Runtime settings and downloaded models are stored outside the application folder:

- `%LocalAppData%\\Voxcribe\\models`
- `%LocalAppData%\\Voxcribe\\data`
- `%LocalAppData%\\Voxcribe\\runtimes`

This keeps the published `Voxcribe` folder shareable without your local settings or downloaded model copies.

## End-user usage

For most users, no development setup is required. The default lightweight build needs a one-time runtime installation from the settings window.

To share Voxcribe:

1. take the full `Voxcribe/` folder
2. zip it if needed
3. extract it on another Windows PC
4. launch `Voxcribe.exe`

Models can then be downloaded from the settings window.

## Development prerequisites

- Windows 11
- .NET 8 SDK
- Python 3.12 to 3.14
- enough disk space for the portable runtime and downloaded models
- NVIDIA drivers if you want CUDA-backed local inference

## Development setup

Prepare the development backend:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\setup_backend.ps1
```

Or with an explicit Python executable:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\setup_backend.ps1 -PythonExe "C:\Users\YourUser\AppData\Local\Python\bin\python3.14-64.exe"
```

Qwen uses a separate dependency set. Add `-IncludeQwenRuntime` to the setup
command when you also want its development runtime.

Run the application from source:

```powershell
dotnet run --project .\src\TranscriptionOverlay\TranscriptionOverlay.csproj
```

## Build and publish

Publish the portable application:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

Output:

- `Voxcribe/`

This is the only distributable build directory. Each build replaces it, removes the old `Voxcribe-fixed` / `dist` outputs, and deletes the intermediate `bin` and `obj` compilations. Use `-KeepIntermediateBuildOutput` only when those intermediate files are explicitly needed for development.

If you explicitly want to bundle the currently managed local model copies into the published folder, use:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1 -IncludeManagedModels
```

To explicitly request the same lightweight package in an automated build, use:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1 -ExcludePortablePythonRuntime
```

To intentionally bundle the local Python / PyTorch runtime instead, use:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1 -IncludePortablePythonRuntime
```

Users can then install the NVIDIA CUDA, AMD ROCm, or universal CPU runtime from the settings window. Voxcribe downloads Python 3.12, PyTorch, and the backend requirements into `%LocalAppData%\\Voxcribe\\runtimes`. The CPU option is slower, but it provides a working fallback for AMD GPUs outside the native-Windows ROCm support matrix.

The NVIDIA button queries every installed NVIDIA adapter and selects the smallest compatible runtime set:

- systems without Blackwell use CUDA 12.6, preserving support for older GPUs and drivers
- Blackwell/RTX 50 systems whose other adapters are Turing or newer use CUDA 13.0 alone, because that build supports every GPU present
- systems combining Blackwell with a Maxwell, Pascal, or Volta adapter receive separate CUDA 12.6 and CUDA 13.0 Python environments; selecting a GPU switches to its compatible environment

CUDA 13.0 on Windows requires NVIDIA driver 580.88 or newer. Voxcribe refuses to guess when `nvidia-smi` cannot report the compute capability, and it launches a real kernel before marking each runtime complete. Existing legacy `nvidia-cuda` environments and cached wheel downloads are reused when their profile matches.

Qwen keeps its required Python environment separate from the main Voxcribe runtime, but follows the same CUDA 12.6 / 13.0 planning and per-GPU selection rules.

The AMD installer uses the official native-Windows ROCm 7.2.1 packages. It supports Windows 11 with the Radeon RX 9070/9060/7900/7700 families listed by AMD, Radeon PRO W7900 / AI PRO R9700, and the supported Ryzen AI systems exposed as Radeon 8060S, 8050S, 890M, or 880M. Other AMD GPUs cannot be promised GPU acceleration on native Windows because AMD does not provide a compatible PyTorch ROCm wheel for them.

If you also accept requiring the target PC to already have the .NET Desktop Runtime, combine:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1 -FrameworkDependent
```

## Notes

- The first model load is slower because the model must be loaded into memory.
- For packages intended for several different PCs, keep the default lightweight build: a single bundled PyTorch wheel cannot cover NVIDIA, native-Windows AMD ROCm, and CPU fallback at once. The per-PC installer selects the appropriate wheel.
- Automatic insertion depends on Windows focus and can be blocked by elevated applications.
- If you want to paste into an application running as administrator, Voxcribe should also run as administrator.
- Real-time mode depends on model support and available hardware.
- Some large models may be defined in the catalog but still be impractical on smaller GPUs.
