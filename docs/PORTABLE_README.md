# Voxcribe for Windows

## Getting started

1. Extract the complete ZIP archive.
2. Run `Voxcribe.exe` from the `Voxcribe` folder.
3. In settings, install a compatible NVIDIA, AMD, or CPU runtime.
4. Download a model, select your audio source, and start transcribing.

Requires Windows 11 x64. The .NET runtime is included in this folder.
Internet access is required to install the Python runtime and download models.
Transcription then runs locally on your computer. Memory and storage requirements
vary by model, and runtime and model downloads can be large.

Keep every file in this folder alongside `Voxcribe.exe`.
Settings, runtimes, and models are stored under `%LocalAppData%\Voxcribe`.

## Updating

Close Voxcribe and extract the new release into a separate folder.
Your settings and downloaded models are stored outside the application folder
and remain available to the new version.

## Links

- [Source code and documentation](https://github.com/jieme54/Voxcribe-for-win)
- [Downloads and release notes](https://github.com/jieme54/Voxcribe-for-win/releases)

Voxcribe's own source code is MIT-licensed; see `LICENSE`.
Dependencies and model weights retain their own licenses; see
`THIRD_PARTY_NOTICES.md` and the `licenses` folder.
