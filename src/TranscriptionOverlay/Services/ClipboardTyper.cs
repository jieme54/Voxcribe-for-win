namespace TranscriptionOverlay.Services;

public sealed class ClipboardTyper
{
    public async Task<bool> TryTypeTextAsync(
        string text,
        IntPtr targetWindow,
        ForegroundWindowTracker tracker,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text) || targetWindow == IntPtr.Zero)
        {
            return false;
        }

        var focusSucceeded = false;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (tracker.FocusWindow(targetWindow))
            {
                focusSucceeded = true;
                break;
            }

            await Task.Delay(120, cancellationToken);
        }

        if (!focusSucceeded)
        {
            return false;
        }

        await Task.Delay(110, cancellationToken);
        return Win32Native.SendUnicodeText(text);
    }

    public async Task<PasteOutcome> TryPasteTextAsync(
        string text,
        IntPtr targetWindow,
        ForegroundWindowTracker tracker,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new PasteOutcome(false, false);
        }

        System.Windows.IDataObject? originalClipboard = null;
        try
        {
            originalClipboard = System.Windows.Clipboard.GetDataObject();
        }
        catch
        {
        }

        var dataObject = new System.Windows.DataObject();
        dataObject.SetData(System.Windows.DataFormats.UnicodeText, text);
        dataObject.SetData(System.Windows.DataFormats.Text, text);

        var clipboardContainsTranscript = await TrySetClipboardDataAsync(dataObject, cancellationToken);
        if (!clipboardContainsTranscript)
        {
            return new PasteOutcome(false, false);
        }

        if (targetWindow == IntPtr.Zero)
        {
            return new PasteOutcome(false, true);
        }

        var focusSucceeded = false;
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (tracker.FocusWindow(targetWindow))
            {
                focusSucceeded = true;
                break;
            }

            await Task.Delay(120, cancellationToken);
        }

        if (!focusSucceeded)
        {
            return new PasteOutcome(false, true);
        }

        await Task.Delay(160, cancellationToken);
        Win32Native.SendCtrlV();
        await Task.Delay(180, cancellationToken);

        if (originalClipboard is not null)
        {
            await TryRestoreClipboardAsync(originalClipboard, cancellationToken);
        }

        return new PasteOutcome(true, false);
    }

    public Task<bool> TryCopyTextAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Task.FromResult(false);
        }

        var dataObject = new System.Windows.DataObject();
        dataObject.SetData(System.Windows.DataFormats.UnicodeText, text);
        dataObject.SetData(System.Windows.DataFormats.Text, text);
        return TrySetClipboardDataAsync(dataObject, cancellationToken);
    }

    private static async Task<bool> TrySetClipboardDataAsync(System.Windows.IDataObject dataObject, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                System.Windows.Clipboard.SetDataObject(dataObject, true);

                try
                {
                    System.Windows.Clipboard.Flush();
                }
                catch
                {
                }

                return true;
            }
            catch when (attempt < 4)
            {
                await Task.Delay(80, cancellationToken);
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private static async Task TryRestoreClipboardAsync(System.Windows.IDataObject originalClipboard, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                System.Windows.Clipboard.SetDataObject(originalClipboard, true);

                try
                {
                    System.Windows.Clipboard.Flush();
                }
                catch
                {
                }

                return;
            }
            catch when (attempt < 2)
            {
                await Task.Delay(60, cancellationToken);
            }
            catch
            {
                return;
            }
        }
    }
}

public readonly record struct PasteOutcome(bool WasPasted, bool KeptInClipboard);
