namespace TranscriptionOverlay.Services;

public sealed class RealtimeTranscriptAssembler
{
    private string _displayText = string.Empty;

    public string CommittedText => _displayText;

    public void Reset()
    {
        _displayText = string.Empty;
    }

    public RealtimeTranscriptUpdate Update(string? rawText, bool isFinal)
    {
        var normalized = Normalize(rawText);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return new RealtimeTranscriptUpdate(_displayText, string.Empty, _displayText, string.Empty);
        }

        var nextDisplay = isFinal
            ? normalized
            : MergeRollingTranscript(_displayText, normalized);

        var committedDelta = nextDisplay.Length > _displayText.Length
            ? nextDisplay[_displayText.Length..]
            : string.Empty;

        _displayText = nextDisplay;
        return new RealtimeTranscriptUpdate(_displayText, committedDelta, _displayText, string.Empty);
    }

    private static string MergeRollingTranscript(string existingText, string latestWindowText)
    {
        var left = Normalize(existingText);
        var right = Normalize(latestWindowText);
        if (string.IsNullOrWhiteSpace(left))
        {
            return right;
        }

        if (string.IsNullOrWhiteSpace(right))
        {
            return left;
        }

        if (left.Contains(right, StringComparison.Ordinal))
        {
            return left;
        }

        var leftWords = left.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rightWords = right.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var maxOverlap = Math.Min(leftWords.Length, rightWords.Length);

        for (var overlapSize = maxOverlap; overlapSize >= 2; overlapSize--)
        {
            if (!WordsMatch(leftWords, rightWords, overlapSize))
            {
                continue;
            }

            var suffix = string.Join(' ', rightWords[overlapSize..]).Trim();
            return string.IsNullOrWhiteSpace(suffix)
                ? left
                : Normalize($"{left} {suffix}");
        }

        return Normalize($"{left} {right}");
    }

    private static bool WordsMatch(string[] leftWords, string[] rightWords, int overlapSize)
    {
        for (var index = 0; index < overlapSize; index++)
        {
            var leftWord = NormalizeWord(leftWords[leftWords.Length - overlapSize + index]);
            var rightWord = NormalizeWord(rightWords[index]);
            if (!string.Equals(leftWord, rightWord, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeWord(string word)
    {
        return new string(word
            .Where(character => char.IsLetterOrDigit(character))
            .ToArray())
            .ToLowerInvariant();
    }

    private static string Normalize(string? text)
    {
        return string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : text.Replace("\r\n", "\n").Trim();
    }
}

public readonly record struct RealtimeTranscriptUpdate(
    string CommittedText,
    string CommittedDelta,
    string DisplayText,
    string PreviewText);
