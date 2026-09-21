using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace WlshareViewer;

/// <summary>
/// The window the form lives in: the saved desktops and the one being filled
/// in. There is one of it for the whole app, and every session is started from
/// it — it is put away when one is, and comes back on <b>New connection</b>, on
/// <b>Disconnect</b>, and whenever a session ends by itself.
///
/// Closing it while a desktop is open only puts it away, since it is the only
/// form there is and the app is not over; closing it with none open closes the
/// app, this being the last window.
/// </summary>
internal sealed partial class ConnectWindow : Window
{
    /// <summary>Called with a destination that parsed, after it has been saved
    /// as a profile. Everything about the session is the app's business.</summary>
    public event Action<Destination>? Chosen;

    public ConnectView View => Form;

    /// <summary>Whether a desktop is open, and so whether closing this window
    /// is putting it away rather than ending the app.</summary>
    private readonly Func<bool> _elsewhere;

    public ConnectWindow(Func<bool> elsewhere)
    {
        _elsewhere = elsewhere;
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "wlshare.ico"));
        Place();

        Form.Chosen += destination => Chosen?.Invoke(destination);
        AppWindow.Closing += (_, e) =>
        {
            // Not while the form holds something to correct: it stays up with
            // the reason on it, which is worth seeing even when it was put away
            // and is being closed from the taskbar.
            if (!Form.Save())
            {
                e.Cancel = true;
                // Up where the reason on it can be read — `Save` has just put
                // one there — rather than left hidden and refusing to close.
                AppWindow.Show();
                Activate();
                return;
            }
            if (_elsewhere())
            {
                e.Cancel = true;
                AppWindow.Hide();
            }
        };
    }

    /// <summary>Bring the form up, with <paramref name="error"/> on it when
    /// this is the second attempt at something.</summary>
    public void Show(string? error)
    {
        AppWindow.Show();
        Activate();
        Form.Show(error);
    }

    /// <summary>Three quarters of the screen it opens on, in the middle of it:
    /// the form is centred in whatever it is given, and a window measured in
    /// the screen's own pixels is the one size that is right at every pixel
    /// density.</summary>
    private void Place()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = area.Width * 3 / 4;
        var height = area.Height * 3 / 4;
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
    }
}
