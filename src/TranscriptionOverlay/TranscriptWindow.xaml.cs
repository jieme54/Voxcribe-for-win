using System.Windows;
using System.Windows.Input;
using TranscriptionOverlay.Services;

namespace TranscriptionOverlay;

public partial class TranscriptWindow : Window
{
    private string _languageCode = "fr";
    public long TranscriptRevision { get; private set; }

    public bool IsLatestTranscript { get; private set; } = true;

    public TranscriptWindow()
    {
        InitializeComponent();
        PreviewKeyDown += TranscriptWindow_PreviewKeyDown;
        ApplyLanguage(_languageCode);
        UpdateEditHistoryButtons();
    }

    public void ApplyLanguage(string languageCode)
    {
        _languageCode = string.Equals(languageCode, "en", StringComparison.OrdinalIgnoreCase)
            ? "en"
            : "fr";

        var title = UiText.Translate(
            _languageCode,
            IsLatestTranscript ? "transcript.title.latest" : "transcript.title.previous");
        Title = title;
        WindowTitleTextBlock.Text = title;
        CopyButton.Content = UiText.Translate(_languageCode, "transcript.copy");
        CopyButton.ToolTip = UiText.Translate(_languageCode, "transcript.copy");
        UndoButton.ToolTip = $"{UiText.Translate(_languageCode, "transcript.undo")} (Ctrl+Z)";
        RedoButton.ToolTip = $"{UiText.Translate(_languageCode, "transcript.redo")} (Ctrl+Y)";
    }

    public void SetTranscript(string transcript, long transcriptRevision, bool isLatestTranscript)
    {
        TranscriptRevision = transcriptRevision;
        IsLatestTranscript = isLatestTranscript;
        TranscriptTextBox.IsUndoEnabled = false;
        TranscriptTextBox.Text = transcript ?? string.Empty;
        TranscriptTextBox.IsUndoEnabled = true;
        TranscriptTextBox.CaretIndex = 0;
        TranscriptTextBox.ScrollToHome();
        UpdateEditHistoryButtons();
        ApplyLanguage(_languageCode);
    }

    public void SetLatestState(bool isLatestTranscript)
    {
        IsLatestTranscript = isLatestTranscript;
        ApplyLanguage(_languageCode);
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TranscriptTextBox.Text))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(TranscriptTextBox.Text);
        }
        catch
        {
        }
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        UndoTextEdit();
    }

    private void RedoButton_Click(object sender, RoutedEventArgs e)
    {
        RedoTextEdit();
    }

    private void TranscriptTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        UpdateEditHistoryButtons();
    }

    private void TranscriptWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        if (e.Key == Key.Z)
        {
            UndoTextEdit();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Y)
        {
            RedoTextEdit();
            e.Handled = true;
        }
    }

    private void UndoTextEdit()
    {
        if (!TranscriptTextBox.CanUndo)
        {
            UpdateEditHistoryButtons();
            return;
        }

        TranscriptTextBox.Undo();
        UpdateEditHistoryButtons();
    }

    private void RedoTextEdit()
    {
        if (!TranscriptTextBox.CanRedo)
        {
            UpdateEditHistoryButtons();
            return;
        }

        TranscriptTextBox.Redo();
        UpdateEditHistoryButtons();
    }

    private void UpdateEditHistoryButtons()
    {
        UndoButton.IsEnabled = TranscriptTextBox.CanUndo;
        RedoButton.IsEnabled = TranscriptTextBox.CanRedo;
    }
}
