using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using WlshareViewer.Interop;

namespace WlshareViewer;

/// <summary>
/// One connection, in a window of its own: the desktop on the screen, the
/// session behind it, the Windows clipboard and the desktop's sound — and,
/// floating over the top edge of the desktop, the connection bar that says
/// which desktop it is and holds <b>Library</b> and <b>Disconnect</b>.
///
/// A window is a session and a session is a window — several stand side by
/// side, each with its own socket, its own decoders and its own sound, and
/// none of them knows about the others. What is shared between them is the
/// library they were opened from, <see cref="ConnectWindow"/>, which stays
/// behind them. Everything that is about the desktop on the
/// screen is in <see cref="DesktopView"/>; everything about the wire is in the
/// Rust core.
///
/// A connection refused or dropped ends the session but not the window: the
/// desktop goes, the reason stays in its place and the bar stays up, with
/// <b>Disconnect</b> become <b>Close</b>, until the window is closed. The
/// window is only ever this desktop's — never the library, and never another
/// connection.
/// </summary>
internal sealed partial class SessionWindow : Window
{
    /// <summary>The library forward, please: <b>Library</b>, which leaves this
    /// desktop where it is.</summary>
    public event Action? LibraryWanted;
    /// <summary><b>Disconnect</b>, or <b>Close</b> once the connection has
    /// ended: this desktop closed, please. The app does it, with the library
    /// up first when this is the last one, so the app is never for a moment
    /// down to no windows at all.</summary>
    public event Action<SessionWindow>? Leaving;

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

    // ── The bar ─────────────────────────────────────────────────────────────

    /// <summary>How close to the top edge of the window the pointer has to
    /// be for a hidden bar to come back, in the view's own units.</summary>
    private const double BarEdge = 3;
    /// <summary>How long the pointer is gone from the bar before it goes:
    /// long enough to come back to it after overshooting, and out of the way
    /// soon after that.</summary>
    private static readonly TimeSpan BarLinger = TimeSpan.FromMilliseconds(1500);
    /// <summary>How long it stays when it is shown on connect, long enough to
    /// be noticed.</summary>
    private static readonly TimeSpan BarIntro = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BarSlide = TimeSpan.FromMilliseconds(180);
    /// <summary>How much of the bar shows while the pointer is not on it —
    /// the desktop under it shows through, as in the other remote desktop
    /// clients; under the pointer it is solid.</summary>
    private const double BarFaded = 0.6;
    private static readonly TimeSpan BarFade = TimeSpan.FromMilliseconds(120);

    private readonly DispatcherTimer _barTimer = new() { Interval = BarIntro };
    /// <summary>Whether the bar is on the screen, as opposed to slid up out
    /// of the window.</summary>
    private bool _barShown = true;
    /// <summary>Set once the connection has ended by itself, which keeps the
    /// bar up: with the desktop gone there is no top edge to bring it back
    /// by, and <b>Close</b> is on it.</summary>
    private bool _ended;
    /// <summary>Set while the pointer is over the bar, which is what keeps it
    /// from going.</summary>
    private bool _overBar;
    /// <summary>Where the bar was dragged to along the edge, from the middle,
    /// and where a drag in progress started.</summary>
    private double _barOffset;
    private double? _dragFrom;

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
        // The bar hides completely, so the top edge of the desktop is what
        // brings it back — seen after the view has handled the move, which
        // the desktop must still get.
        desktop.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnDesktopPointerMoved), handledEventsToo: true);
        SessionTitle.Text = destination.Label;
        Title = $"{destination.Label} — wlshare";
        Banner.Text = $"Connecting to {destination.Label}…";
        Banner.Visibility = Visibility.Visible;

        // Up to begin with, so it is seen once, and gone after a moment
        // unless it is pinned or the pointer is on it.
        _barTimer.Tick += (_, _) => HideBar();
        _barTimer.Start();
        Root.SizeChanged += (_, _) => PlaceBar();

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
        _barTimer.Stop();
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
                Ended(status.Error ?? "The connection closed.");
                return;
        }
        _clipboard?.Take();
        _desktop.Refresh();
    }

    /// <summary>The reason where the desktop was, and the bar up for good
    /// with the way out on it. Nothing closes the window but the person using
    /// it.</summary>
    private void Ended(string reason)
    {
        _ended = true;
        Banner.Text = $"Disconnected from {Destination.Label}\n{reason}";
        Banner.Visibility = Visibility.Visible;
        SessionTitle.Text = $"{Destination.Label} — disconnected";
        Title = $"{Destination.Label} — disconnected — wlshare";
        DisconnectButton.Content = "Close";
        PinButton.Visibility = Visibility.Collapsed;
        ShowBar();
        Fade(1);
    }

    private static string Scale(double scale) =>
        scale == Math.Round(scale) ? $"{scale:0}×" : $"{scale:0.00}×";

    /// <summary>The library forward — and the keyboard back on the desktop
    /// for when this window is next in front, rather than left on the
    /// button.</summary>
    private void OnLibrary(object sender, RoutedEventArgs e)
    {
        _desktop?.Focus(FocusState.Programmatic);
        LibraryWanted?.Invoke();
    }

    private void OnDisconnect(object sender, RoutedEventArgs e) => Leaving?.Invoke(this);

    // ── The bar ─────────────────────────────────────────────────────────────

    /// <summary>Pinned, the bar stays; unpinned, it goes once the pointer has
    /// left it. Either way the keyboard goes back to the desktop: a click on
    /// the pin is not a reason for the desktop to lose it.</summary>
    private void OnPinClicked(object sender, RoutedEventArgs e)
    {
        if (PinButton.IsChecked == true)
        {
            _barTimer.Stop();
            ShowBar();
        }
        else if (!_overBar)
        {
            _barTimer.Start();
        }
        _desktop?.Focus(FocusState.Programmatic);
    }

    /// <summary>The pointer at the top edge of the desktop is how a hidden
    /// bar is asked back, as it is in the other remote desktop clients. It
    /// slides down under the pointer, which may not move again, so the timer
    /// runs until the pointer is on the bar.</summary>
    private void OnDesktopPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(Root).Position.Y > BarEdge || PinButton.IsChecked == true || _overBar)
        {
            return;
        }
        ShowBar();
        _barTimer.Stop();
        _barTimer.Start();
    }

    /// <summary>The pointer on the bar keeps it.</summary>
    private void OnBarEntered(object sender, PointerRoutedEventArgs e)
    {
        _overBar = true;
        _barTimer.Stop();
        // Seen, so no longer the introduction.
        _barTimer.Interval = BarLinger;
        ShowBar();
        Fade(1);
    }

    private void OnBarExited(object sender, PointerRoutedEventArgs e)
    {
        _overBar = false;
        if (_dragFrom is null)
        {
            Fade(BarFaded);
            if (PinButton.IsChecked != true)
            {
                _barTimer.Start();
            }
        }
    }

    /// <summary>A bar of a new size is hidden by a new amount, and may need
    /// to come in from the edge.</summary>
    private void OnBarSized(object sender, SizeChangedEventArgs e)
    {
        PlaceBar();
        if (!_barShown)
        {
            // Slid, not set: a storyboard that has run holds its value over
            // one written to the property.
            Slide(-Bar.ActualHeight);
        }
    }

    private void ShowBar()
    {
        if (_barShown)
        {
            return;
        }
        _barShown = true;
        Slide(0);
    }

    private void HideBar()
    {
        _barTimer.Stop();
        _barTimer.Interval = BarLinger;
        if (!_barShown || _ended || PinButton.IsChecked == true || _overBar)
        {
            return;
        }
        _barShown = false;
        Slide(-Bar.ActualHeight);
        // Brought back by the edge, it is faded until the pointer is on it;
        // the connect is the one time it is shown solid without.
        Fade(BarFaded);
    }

    private void Fade(double to)
    {
        var fade = new DoubleAnimation { To = to, Duration = new Duration(BarFade) };
        Storyboard.SetTarget(fade, Bar);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var story = new Storyboard();
        story.Children.Add(fade);
        story.Begin();
    }

    private void Slide(double y)
    {
        var slide = new DoubleAnimation
        {
            To = y,
            Duration = new Duration(BarSlide),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(slide, BarShift);
        Storyboard.SetTargetProperty(slide, "Y");
        var story = new Storyboard();
        story.Children.Add(slide);
        story.Begin();
    }

    // A drag on the handle moves the bar along the top edge, and no further
    // than the edge goes: what it was dragged to is kept as an offset from
    // the middle, so the bar stays on the window when the window changes
    // size.

    private void OnHandlePressed(object sender, PointerRoutedEventArgs e)
    {
        _dragFrom = e.GetCurrentPoint(Root).Position.X - _barOffset;
        Handle.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnHandleMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragFrom is not { } from)
        {
            return;
        }
        _barOffset = e.GetCurrentPoint(Root).Position.X - from;
        PlaceBar();
        e.Handled = true;
    }

    private void OnHandleReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_dragFrom is null)
        {
            return;
        }
        _dragFrom = null;
        Handle.ReleasePointerCaptures();
        if (!_overBar)
        {
            Fade(BarFaded);
            if (PinButton.IsChecked != true)
            {
                _barTimer.Start();
            }
        }
    }

    private void PlaceBar()
    {
        var room = Math.Max(0, (Root.ActualWidth - Bar.ActualWidth) / 2);
        _barOffset = Math.Clamp(_barOffset, -room, room);
        BarShift.X = _barOffset;
    }
}
