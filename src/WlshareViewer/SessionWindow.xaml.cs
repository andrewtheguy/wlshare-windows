using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WlshareViewer.Interop;

namespace WlshareViewer;

/// <summary>
/// One connection, in a window of its own: the desktop on the screen, the
/// session behind it, the Windows clipboard and the desktop's sound.
///
/// A window is a session and a session is a window — several stand side by
/// side, each with its own socket, its own decoders and its own sound, and
/// none of them knows about the others. What is shared between them is the
/// list of saved desktops and the form over it, which is
/// <see cref="ConnectWindow"/>'s. Everything that is about the desktop on the
/// screen is in <see cref="DesktopView"/>; everything about the wire is in the
/// Rust core.
/// </summary>
internal sealed partial class SessionWindow : Window
{
    /// <summary>The form, please: <b>New connection</b>, which leaves this
    /// desktop where it is, and <b>Disconnect</b>, which closes it after.</summary>
    public event Action? FormWanted;
    /// <summary>The connection ended by itself — refused, or dropped — with the
    /// reason. The window is still there when this is called; closing it is the
    /// app's, which has somewhere to put the reason first.</summary>
    public event Action<SessionWindow, string>? Dropped;

    /// <summary>Where this one went, for its title and for saying which desktop
    /// a reason belongs to.</summary>
    public Destination Destination { get; }

    private Client? _client;
    private DesktopView? _desktop;
    private ClipboardSync? _clipboard;
    private AudioOutput? _audio;

    /// <summary>How far each window is offset from the one before it, so that
    /// two desktops opened at once do not land exactly on top of each
    /// other.</summary>
    private const int CascadeStep = 32;
    private const int CascadeWrap = 8;

    public SessionWindow(Destination destination, int cascade)
    {
        Destination = destination;
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "wlshare.ico"));
        Place(cascade);

        // Ends the session and joins its thread while there is still a window
        // for its callbacks to have reached.
        Closed += (_, _) => EndSession();
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

        var desktop = new DesktopView();
        _desktop = desktop;
        DesktopHost.Child = desktop;
        SessionTitle.Text = destination.Label;
        Title = $"{destination.Label} — wlshare";
        Banner.Text = $"Connecting to {destination.Label}…";
        Banner.Visibility = Visibility.Visible;

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

    /// <summary>Three quarters of the screen the window opens on, in the middle
    /// of it and a step down and right of the desktop opened before it, as far
    /// as the screen allows: a desktop is asked to be the window's size, so a
    /// small window is a small desktop.</summary>
    private void Place(int cascade)
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = area.Width * 3 / 4;
        var height = area.Height * 3 / 4;
        var step = CascadeStep * (cascade % CascadeWrap);
        // A quarter of a narrow screen is fewer than a whole cascade of steps,
        // so the last of them would otherwise be off the edge of it.
        var x = Math.Clamp(area.X + (area.Width - width) / 2 + step, area.X, area.X + area.Width - width);
        var y = Math.Clamp(area.Y + (area.Height - height) / 2 + step, area.Y, area.Y + area.Height - height);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
    }

    // ── The session ─────────────────────────────────────────────────────────

    /// <summary>Put the session and its view away. The window is left to the
    /// caller; closing it comes back here, and everything below is safe
    /// twice.</summary>
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
                var name = status.Name.Length == 0 ? Destination.Label : status.Name;
                // A VP9 session is VP9 or nothing: a server without it ends it.
                var encoding = Destination.Encoding == PixelEncoding.Vp9 ? " · VP9" : "";
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
                EndSession();
                Dropped?.Invoke(this, status.Error ?? "The connection closed.");
                return;
        }
        _clipboard?.Take();
        _desktop.Refresh();
    }

    private static string Scale(double scale) =>
        scale == Math.Round(scale) ? $"{scale:0}×" : $"{scale:0.00}×";

    private void OnNewConnection(object sender, RoutedEventArgs e) => FormWanted?.Invoke();

    /// <summary>The form, and this desktop away — in that order, so the app is
    /// never for a moment down to no windows at all.</summary>
    private void OnDisconnect(object sender, RoutedEventArgs e)
    {
        FormWanted?.Invoke();
        Close();
    }
}
