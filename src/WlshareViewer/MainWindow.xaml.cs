using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
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
    /// <summary>The last destination tried, which is what the form comes back
    /// filled with — including a password that was typed, so a connection that
    /// failed for some other reason can be retried as it is.</summary>
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

        // A launch from a shell says where to go; a launch from the Start menu
        // asks.
        if (fromArguments is not null)
        {
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
        destination.Save();

        var desktop = new DesktopView { DesktopScale = destination.Scale };
        _desktop = desktop;
        DesktopHost.Child = desktop;
        ShowScale(destination.Scale);
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
        // Joins the session's thread: nothing calls back after this.
        _client?.Dispose();
        _client = null;
    }

    /// <summary>The session has something new: a frame, a size, a state.
    /// Called on the window's thread.</summary>
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
                SessionTitle.Text = $"{name} — {status.Width}×{status.Height} @ {Scale(status.Scale)}";
                Title = $"{name} — wlshare";
                break;
            case Client.State.Closed:
                // Back to the form with the reason on it.
                EndSession();
                Ask(status.Error ?? "The connection closed.");
                return;
        }
        _desktop.Refresh();
    }

    private static string Scale(double scale) =>
        scale == Math.Round(scale) ? $"{scale:0}×" : $"{scale:0.00}×";

    private void Ask(string? error)
    {
        Session.Visibility = Visibility.Collapsed;
        Form.Visibility = Visibility.Visible;
        Title = "wlshare";
        Form.Show(_last ?? Destination.Remembered(), error);
    }

    private void OnDisconnect(object sender, RoutedEventArgs e)
    {
        EndSession();
        Ask(null);
    }

    // ── 1× and 2× ───────────────────────────────────────────────────────────

    private void OnScale(object sender, RoutedEventArgs e)
    {
        var scale = ((ToggleButton)sender).Tag as string == "2" ? 2 : 1;
        ShowScale(scale);
        if (_desktop is null)
        {
            return;
        }
        _desktop.DesktopScale = scale;
        // Remembered, so the next session starts at the scale this one ended at.
        if (_last is not null)
        {
            _last = _last with { Scale = scale };
            _last.Save();
        }
        // The keyboard back to the desktop: the button took it.
        _desktop.Focus(FocusState.Programmatic);
    }

    /// <summary>Two toggles that behave as one choice: the one that is the
    /// scale is on, and clicking it again leaves it on.</summary>
    private void ShowScale(int scale)
    {
        Scale1.IsChecked = scale == 1;
        Scale2.IsChecked = scale == 2;
    }
}
