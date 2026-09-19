using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WlshareViewer.Interop;

namespace WlshareViewer;

/// <summary>
/// The form, the desktop behind it, and the session between them. Everything
/// that is about the desktop on the screen is in <see cref="DesktopView"/>;
/// everything about the wire is in the Rust core.
/// </summary>
internal sealed partial class MainWindow : Window
{
    private Client? _client;
    private DesktopView? _desktop;
    private ClipboardSync? _clipboard;
    private AudioOutput? _audio;
    /// <summary>The destination of the session in the window, for its
    /// title.</summary>
    private Destination? _last;

    public MainWindow(Destination? fromArguments)
    {
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "wlshare.ico"));
        Place();

        Form.Chosen += Open;
        // Ends the session and joins its thread while there is still a window
        // for its callbacks to have reached.
        Closed += (_, _) => EndSession();
        // Not while the form holds something that cannot be saved: it stays up
        // with the reason on it.
        AppWindow.Closing += (_, e) =>
        {
            if (Form.Visibility == Visibility.Visible && !Form.Save())
            {
                e.Cancel = true;
            }
        };
        // The moments the window becomes the one in use, which is when the
        // Windows clipboard is offered to the desktop — and when a desktop
        // clipboard that could not be written, the Windows one being held by
        // another process, is tried again without waiting for the next wake.
        // Offered first, so what was copied here reaches the desktop before
        // anything older from it lands on top.
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
            {
                _clipboard?.Offer();
                _clipboard?.Take();
            }
        };

        // A launch from a shell says where to go; a launch from the Start menu
        // asks.
        if (fromArguments is not null)
        {
            // The password saved for the same place and user, if any. One that
            // will not open is not tried as none: the form says why, and is
            // where it is typed.
            var profile = Form.Profiles.Matching(fromArguments);
            try
            {
                fromArguments = fromArguments with { Password = profile?.Password() ?? "" };
            }
            catch (SafeStorageException e)
            {
                Form.Load(fromArguments, profile?.Id);
                Ask(e.Message);
                return;
            }
            Form.Load(fromArguments, profile?.Id);
            Open(fromArguments);
        }
        else
        {
            Ask(null);
        }
    }

    /// <summary>Three quarters of the screen the window opens on, in the
    /// middle of it: a desktop is asked to be the window's size, so a small
    /// window is a small desktop.</summary>
    private void Place()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = area.Width * 3 / 4;
        var height = area.Height * 3 / 4;
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
    }

    // ── A session ───────────────────────────────────────────────────────────

    private void Open(Destination destination)
    {
        EndSession();
        _last = destination;

        var desktop = new DesktopView();
        _desktop = desktop;
        DesktopHost.Child = desktop;
        SessionTitle.Text = destination.Label;
        Title = $"{destination.Label} — wlshare";
        Banner.Text = $"Connecting to {destination.Label}…";
        Banner.Visibility = Visibility.Visible;
        Form.Visibility = Visibility.Collapsed;
        Session.Visibility = Visibility.Visible;

        // The first surface is the view's own size, so the desktop is asked to
        // match before the first frame rather than after it — which it cannot
        // be until the view has been laid out.
        desktop.Loaded += (_, _) =>
        {
            if (_desktop != desktop || _client is not null)
            {
                return;
            }
            _client = new Client(destination, desktop.Surface, DispatcherQueue, Changed);
            // Offered at once: the window is the one in use, the form having
            // just been filled in. The core keeps it until the desktop can be
            // told.
            _clipboard = new ClipboardSync(_client, WinRT.Interop.WindowNative.GetWindowHandle(this));
            _clipboard.Offer();
            desktop.Attach(_client);
            desktop.Focus(FocusState.Programmatic);
            Changed();
        };
    }

    /// <summary>Put the session and its view away.</summary>
    private void EndSession()
    {
        _desktop?.Detach();
        _desktop = null;
        DesktopHost.Child = null;
        _clipboard = null;
        // Before the client: the audio thread reads from it until it has
        // stopped.
        _audio?.Dispose();
        _audio = null;
        // Joins the session's thread: nothing calls back after this.
        _client?.Dispose();
        _client = null;
    }

    /// <summary>The session has something new: a frame, a size, a state, the
    /// desktop's clipboard. Called on the window's thread.</summary>
    private void Changed()
    {
        if (_client is null || _desktop is null)
        {
            return;
        }
        var status = _client.Read();
        switch (status.State)
        {
            case Client.State.Connecting:
                break;
            case Client.State.Ready:
                Banner.Visibility = Visibility.Collapsed;
                var name = status.Name.Length == 0 ? _last?.Label : status.Name;
                // A VP9 session is VP9 or nothing: a server without it ends it.
                var encoding = _last?.Encoding == PixelEncoding.Vp9 ? " · VP9" : "";
                SessionTitle.Text = $"{name} — {status.Width}×{status.Height} @ {Scale(status.Scale)}{encoding}";
                Title = $"{name} — wlshare";
                // Not before the server has said it has sound: a session
                // without it leaves the Windows audio device alone.
                if (status.Audio && _audio is null)
                {
                    _audio = new AudioOutput(_client);
                }
                break;
            case Client.State.Closed:
                // Back to the form with the reason on it.
                EndSession();
                Ask(status.Error ?? "The connection closed.");
                return;
        }
        _clipboard?.Take();
        _desktop.Refresh();
    }

    private static string Scale(double scale) =>
        scale == Math.Round(scale) ? $"{scale:0}×" : $"{scale:0.00}×";

    private void Ask(string? error)
    {
        Session.Visibility = Visibility.Collapsed;
        Form.Visibility = Visibility.Visible;
        Title = "wlshare";
        Form.Show(error);
    }

    private void OnDisconnect(object sender, RoutedEventArgs e)
    {
        EndSession();
        Ask(null);
    }
}
