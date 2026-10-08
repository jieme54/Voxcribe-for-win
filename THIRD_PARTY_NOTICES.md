# Third-party notices

Voxcribe's own source code is MIT-licensed; see [LICENSE](LICENSE).
That license does not replace the licenses of dependencies or model weights.

## Components included in the Windows ZIP

- **NAudio 2.2.1**, copyright 2020 Mark Heath, MIT:
  [upstream license](https://github.com/naudio/NAudio/blob/v2.2.1/license.txt).
  The complete notice is included in `licenses/NAudio-LICENSE.txt`.
- **Microsoft .NET 8 / Windows Desktop runtime**, .NET Foundation and contributors:
  [runtime license](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT).
  Microsoft license and third-party notices are included in
  `licenses/DOTNET-LICENSE.txt` and `licenses/DOTNET-THIRD-PARTY-NOTICES.txt`.

The repository keeps these copies under [docs/licenses](docs/licenses).
The self-contained ZIP includes the corresponding license files.

## Components downloaded separately

Python, PyTorch, Transformers, PyAV/FFmpeg, CrisperWhisper, Fermion, and the other
Python packages are installed separately by the runtime installer or development
setup script. Their license and package metadata remain in those installations.
Model weights are downloaded on demand from the model's original repository.
Check the selected model's license before using or redistributing it.

- **Phonon-2** weights: CC BY 4.0, with Fermion Research / NVIDIA attribution.
  The model downloader preserves the upstream license and attribution files.
- **CrisperWhisper 2.0** public weights:
  [Nyra Health Non-Commercial Research License](https://huggingface.co/nyralabs/CrisperWhisper2.0_large/blob/main/LICENSE.md).
  Commercial use requires a separate Nyra license. The
  [inference package](https://github.com/nyrahealth/CrisperWhisper) is MIT-licensed.

The release ZIP does not redistribute model weights or Python / PyTorch runtimes.
