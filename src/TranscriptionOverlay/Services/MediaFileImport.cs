using System.IO;

namespace TranscriptionOverlay.Services;

public static class MediaFileImport
{
    private const string AudioPatterns =
        "*.wav;*.wave;*.mp3;*.mp2;*.flac;*.ogg;*.oga;*.opus;*.m4a;*.m4b;*.m4r;" +
        "*.aac;*.adts;*.wma;*.aif;*.aiff;*.aifc;*.caf;*.ac3;*.eac3;*.dts;*.amr;" +
        "*.ape;*.au;*.snd;*.mka;*.weba;*.wv;*.w64;*.rf64;*.mpga;*.spx;*.ra;*.tta;*.3ga";

    private const string VideoPatterns =
        "*.mp4;*.m4v;*.mkv;*.webm;*.mov;*.avi;*.wmv;*.asf;*.mpg;*.mpeg;*.mpe;" +
        "*.m2v;*.m2ts;*.mts;*.ts;*.m2t;*.vob;*.flv;*.f4v;*.ogv;*.3gp;*.3g2;" +
        "*.divx;*.rm;*.rmvb;*.mxf";

    public static string GetDialogFilter(string languageCode)
    {
        var allPatterns = $"{AudioPatterns};{VideoPatterns}";
        return languageCode == "en"
            ? $"Audio and video files|{allPatterns}|Audio files|{AudioPatterns}|Video files|{VideoPatterns}|All files|*.*"
            : $"Fichiers audio et vidéo|{allPatterns}|Fichiers audio|{AudioPatterns}|Fichiers vidéo|{VideoPatterns}|Tous les fichiers|*.*";
    }

    public static bool CanImport(string? filePath)
    {
        // The decoder checks the actual content, not a fixed extension allowlist.
        return !string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath);
    }
}
