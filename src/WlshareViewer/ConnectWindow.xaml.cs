using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace WlshareViewer;

/// <summary>
/// The library: the window the saved desktops and the form live in. There is
/// one of it for the whole app, made once and brought forward or put away
/// rather than made again, and every session is started from it — it stays
/// where it is when one is, behind the desktop that opens in front, and comes
/// forward on <b>Library</b> and on the last <b>Disconnect</b> — never for a
/// desktop's trouble, which that desktop's window shows. It opens where it
/// was last left.
///
/// Closing it with something unsaved in the form asks first, as a document
/// would. Closing it while a desktop is open only puts it away, since it is
/// the only library there is and the app is not over; closing it with none
/// open closes the app, this being the last window.
/// </summary>
internal sealed partial class ConnectWindow : Window
{
    /// <summary>Called with a destination that parsed, after it has been saved
    /// as a profile. Everything about the session is the app's business; the
    /// library stays where it is.</summary>
    public event Action<Destination>? Chosen;

    public ConnectView View => Form;

    /// <summary>Whether a desktop is open, and so whether closing this window
    /// is putting it away rather than ending the app.</summary>
    private readonly Func<bool> _elsewhere;
    /// <summary>Set once the form has settled and the window is going, so the
    /// close that follows is not asked about again.</summary>
    private bool _leaving;

    public ConnectWindow(Func<bool> elsewhere)
    {
        _elsewhere = elsewhere;
        InitializeComponent();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "wlshare.ico"));
        Place();

        Form.Chosen += destination => Chosen?.Invoke(destination);
        AppWindow.Closing += (sender, e) =>
        {
            if (_leaving)
            {
                return;
            }
            // Not while the form holds something unsaved that has been seen:
            // it asks, and stays up with the reason when the answer was to
            // keep it and it cannot be — up where that can be read, rather
            // than left hidden and refusing to close from the taskbar.
            if (Form.Presented && Form.HasEdits)
            {
                e.Cancel = true;
                AppWindow.Show();
                Activate();
                _ = SettleThenLeave();
                return;
            }
            Remember();
            if (_elsewhere())
            {
                e.Cancel = true;
                AppWindow.Hide();
            }
        };
    }

    /// <summary>Bring the library forward, with <paramref name="error"/> on
    /// it when the form's own contents could not be used.</summary>
    public void Show(string? error)
    {
        AppWindow.Show();
        Activate();
        Form.Show(error);
    }

    /// <summary>The close that was asked about, once the form has settled:
    /// out of the way behind the desktops while there are any — a window that
    /// is merely hidden does not count as the last one, so putting it away
    /// cannot close the app — and closed for good, with the app, when there
    /// are none.</summary>
    private async Task SettleThenLeave()
    {
        if (!await Form.SettleAsync())
        {
            return;
        }
        Remember();
        if (_elsewhere())
        {
            AppWindow.Hide();
            return;
        }
        _leaving = true;
        Close();
    }

    /// <summary>Where it was last left, or, the first time and when that
    /// place is on no screen any more, three quarters of the screen it opens
    /// on, in the middle of it: the form is centred in whatever it is given,
    /// and a window measured in the screen's own pixels is the one size that
    /// is right at every pixel density.</summary>
    private void Place()
    {
        if (Form.Profiles.Library is { } left
            && DisplayArea.GetFromRect(new RectInt32(left.X, left.Y, left.Width, left.Height), DisplayAreaFallback.None) is { } display)
        {
            // Within that screen's work area, as far as it fits: a window left
            // on a screen that has since shrunk is not left hanging off it.
            var work = display.WorkArea;
            var width = Math.Min(left.Width, work.Width);
            var height = Math.Min(left.Height, work.Height);
            var x = Math.Clamp(left.X, work.X, work.X + work.Width - width);
            var y = Math.Clamp(left.Y, work.Y, work.Y + work.Height - height);
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
            return;
        }
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var wide = area.Width * 3 / 4;
        var tall = area.Height * 3 / 4;
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - wide) / 2, area.Y + (area.Height - tall) / 2, wide, tall));
    }

    /// <summary>Keep where the window is for next time — once it has been on
    /// the screen at all, and as a plain window, not the frame a maximized one
    /// fills, which would come back as an ordinary window the size of the
    /// screen.</summary>
    private void Remember()
    {
        if (Form.Presented && AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            Form.Profiles.Library = new Placement
            {
                X = AppWindow.Position.X,
                Y = AppWindow.Position.Y,
                Width = AppWindow.Size.Width,
                Height = AppWindow.Size.Height,
            };
        }
    }
}
