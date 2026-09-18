using WlshareViewer.Interop;

namespace WlshareViewer;

/// <summary>
/// The Windows clipboard and the desktop's, kept in step.
///
/// The desktop's arrives when it changes, and goes on the Windows clipboard at
/// once, with Windows' CRLF line endings. The Windows one goes the other way
/// only when the window becomes the one being used and the clipboard has
/// changed since — that is when a paste into the desktop can next happen. The
/// core notifies the desktop and sends the text when it is asked for, so the
/// desktop never learns anything copied while this window was not in front.
/// </summary>
internal sealed class ClipboardSync(Client client, nint window)
{
    /// <summary>The clipboard's sequence number as it was last offered or
    /// written. Both directions record it, so neither sends back what the other
    /// just did.</summary>
    private uint? _seenChange;
    /// <summary>The desktop's clipboard already on the Windows one.</summary>
    private ulong _seenGeneration;

    /// <summary>Give the desktop the Windows clipboard, if it has changed
    /// since.</summary>
    public void Offer()
    {
        var change = Win32.GetClipboardSequenceNumber();
        if (change == _seenChange)
        {
            return;
        }
        var text = Win32.ClipboardText(window);
        // A clipboard with no text, or one another process held on to, is
        // looked at again next time rather than recorded as seen.
        if (text is null)
        {
            return;
        }
        _seenChange = change;
        client.SetClipboard(text);
    }

    /// <summary>Put the desktop's clipboard on the Windows one, if there is a
    /// new one.</summary>
    public void Take()
    {
        if (client.DesktopClipboard(_seenGeneration) is not { } taken)
        {
            return;
        }
        var (generation, text) = taken;
        if (!Win32.SetClipboardText(window, text.ReplaceLineEndings("\r\n")))
        {
            // Left unseen, so the next change the window hears of tries again.
            return;
        }
        _seenGeneration = generation;
        _seenChange = Win32.GetClipboardSequenceNumber();
    }
}
