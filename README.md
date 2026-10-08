# Voxcribe for Windows

[![Windows release](https://github.com/jieme54/Voxcribe-for-win/actions/workflows/release.yml/badge.svg)](https://github.com/jieme54/Voxcribe-for-win/actions/workflows/release.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Voxcribe est une application Windows de transcription vocale locale, avec une
petite fenêtre toujours visible. Elle transcrit le microphone, le son d'une
application ou des fichiers audio et vidéo. Interface en français et en anglais.

## Télécharger l'application

**[Télécharger Voxcribe pour Windows x64 (ZIP)](https://github.com/jieme54/Voxcribe-for-win/releases/latest/download/Voxcribe-Windows-x64.zip)**

[Voir toutes les versions et les notes de publication](https://github.com/jieme54/Voxcribe-for-win/releases)
 · [Somme de contrôle SHA-256](https://github.com/jieme54/Voxcribe-for-win/releases/latest/download/SHA256SUMS.txt)

1. Téléchargez le ZIP et extrayez-le entièrement dans un dossier.
2. Ouvrez le dossier `Voxcribe`, puis lancez `Voxcribe.exe`.
3. Dans les paramètres, installez le moteur adapté à votre PC : NVIDIA, AMD
   compatible ou CPU. L'option CPU fonctionne aussi sans carte graphique compatible.
4. Téléchargez un modèle proposé dans les paramètres, puis sélectionnez votre
   source audio et démarrez la transcription.

Le package inclut .NET : aucun SDK ni installation séparée de .NET n'est
nécessaire. Une connexion Internet est nécessaire pour installer le moteur et
télécharger les modèles ; la transcription s'effectue ensuite sur votre ordinateur.
Le moteur et les modèles ne sont pas inclus dans le ZIP et peuvent occuper
plusieurs gigaoctets.

**Configuration :** Windows 11, processeur x64. Les besoins en RAM, VRAM et
stockage dépendent du modèle. Les modèles et les modes disponibles sont filtrés
dans l'application ; tous les modèles ne fonctionnent pas sur tous les PC.
Le ZIP est une version portable de l'application. Les paramètres, moteurs et
modèles sont conservés dans `%LocalAppData%\Voxcribe`.

## Code source open source

**[Télécharger le code source (ZIP)](https://github.com/jieme54/Voxcribe-for-win/archive/refs/heads/main.zip)**

```powershell
git clone https://github.com/jieme54/Voxcribe-for-win.git
cd Voxcribe-for-win
```

Le code de Voxcribe est publié sous [licence MIT](LICENSE).
Les dépendances et les modèles conservent leurs propres licences :
voir [les notices](THIRD_PARTY_NOTICES.md). Les poids CrisperWhisper 2.0,
notamment, sont réservés à la recherche et aux usages non commerciaux sans
licence distincte de Nyra.

## Fonctions principales

- Fenêtre compacte toujours au premier plan et réduction dans la zone de notification.
- Microphone et autres sources audio Windows.
- Transcription standard et temps réel selon le modèle.
- Import de fichiers audio ou vidéo, y compris par glisser-déposer.
- Fenêtre de transcription avec sélection et copie du texte.
- Insertion du texte dans l'application active lorsque le mode le permet.
- Raccourci clavier global et interface français / anglais.
- Installation et téléchargement des moteurs et modèles depuis les paramètres.

## Compiler depuis les sources

Sur Windows, avec le **SDK .NET 8** :

```powershell
dotnet build .\src\TranscriptionOverlay\TranscriptionOverlay.csproj -c Release
powershell -ExecutionPolicy Bypass -File .\scripts\package-release.ps1
```

Le second appel crée le ZIP dans `dist\release-1.0.0\`, avec son fichier
`SHA256SUMS.txt`. Python n'est pas nécessaire pour compiler le package léger.
Pour exécuter le backend en développement, consultez le
[guide de développement](docs/DEVELOPMENT.md).

Les sources de l'interface sont dans [src/TranscriptionOverlay](src/TranscriptionOverlay),
le moteur Python dans [backend](backend), et les scripts de compilation dans
[scripts](scripts).

GitHub Actions compile le projet à chaque publication sur `main` et publie une
release lorsqu'une nouvelle version est définie dans le fichier projet.
Une version déjà publiée est conservée : augmentez `<Version>` et mettez à jour
`docs/RELEASE_NOTES.md` pour publier la suivante. Le workflow peut aussi être
relancé manuellement.

## English quick start

**[Download Voxcribe for Windows x64](https://github.com/jieme54/Voxcribe-for-win/releases/latest/download/Voxcribe-Windows-x64.zip)**

Extract the complete ZIP, open the `Voxcribe` folder, and run `Voxcribe.exe`.
In settings, install the NVIDIA, supported AMD, or CPU runtime, then download a
model. Internet is needed for these downloads; transcription runs locally.
The ZIP includes .NET, but Python runtimes and model weights are installed
separately. Requires Windows 11 x64. Hardware requirements depend on the model.

**[Download source code](https://github.com/jieme54/Voxcribe-for-win/archive/refs/heads/main.zip)** ·
[Development guide](docs/DEVELOPMENT.md) · [MIT license](LICENSE)
