using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace WlshareViewer;

/// <summary>
/// Where the app starts, and what holds it together: one library, and a window
/// for every desktop opened from it.
///
/// <b>Connect</b> adds a session in front of the library, which stays where it
/// is; it never takes one away, so several desktops stand side by side. There
/// is one library window, brought forward rather than made again: on
/// <b>Library</b> and on the last <b>Disconnect</b>. A desktop's window is only
/// ever that desktop's: a connection that is refused or drops says so in it,
/// and it stays until it is closed. Closing the last
/// window — the library or a desktop, with the library put away — closes the
/// app.
/// </summary>
public partial class App : Application
{
    private const uint MbIconError = 0x10;

    private ConnectWindow? _library;
    private readonly List<SessionWindow> _sessions = [];
    /// <summary>How many desktops have been opened, so each window lands a step
    /// down and right of the one before it.</summary>
    private int _opened;

    public App()
    {
        InitializeComponent();

        // Anything unhandled that reaches the framework would otherwise end the
        // process as a stowed exception with no message at all.
        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            MessageBox(IntPtr.Zero, e.Exception.ToString(), "wlshare", MbIconError);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Before the first P/Invoke, which without the DLL takes the process
        // down with nothing on the screen to say why.
        var core = Path.Combine(AppContext.BaseDirectory, "wlshare_client_core.dll");
        if (!File.Exists(core))
        {
            MessageBox(IntPtr.Zero, $"The wlshare core is missing:\n{core}\n\nBuild it with build-core.ps1.", "wlshare cannot start", MbIconError);
            Exit();
            return;
        }

        // Made whether or not it is shown at once: it is the app's only
        // library, and the window a session comes back to.
        var library = new ConnectWindow(() => _sessions.Count > 0);
        library.Chosen += Open;
        _library = library;

        // A launch from a shell says where to go; a launch from the Start menu
        // asks.
        if (Destination.FromArguments(Environment.GetCommandLineArgs()) is not { } fromArguments)
        {
            library.Show(null);
            return;
        }
        // The password saved for the same place and user, if any. One that will
        // not open is not tried as none: the library says why, and is where it
        // is typed. The form gets the destination without it, so a retry does
        // not show it.
        var profile = library.View.Profiles.Matching(fromArguments);
        library.View.Load(fromArguments, profile?.Id);
        Destination attempt;
        try
        {
            attempt = fromArguments with { Password = profile?.Password() ?? "" };
        }
        catch (SafeStorageException e)
        {
            library.Show(e.Message);
            return;
        }
        Open(attempt);
    }

    /// <summary>Open a desktop in a window of its own, in front of the library
    /// and beside whatever else is already open.</summary>
    private void Open(Destination destination)
    {
        var session = new SessionWindow(destination, _opened++);
        session.LibraryWanted += () => _library?.Show(null);
        session.Leaving += window =>
        {
            // The library first when this is the last desktop, so the app is
            // never for a moment down to no windows at all.
            if (_sessions.Count == 1)
            {
                _library?.Show(null);
            }
            window.Close();
        };
        session.Closed += (_, _) => Ended(session);
        _sessions.Add(session);
        session.Activate();
    }

    /// <summary>A desktop's window has gone. With nothing else on the screen —
    /// no other desktop, and a library that was put away rather than asked
    /// for — the app goes with it.</summary>
    private void Ended(SessionWindow session)
    {
        _sessions.Remove(session);
        if (_sessions.Count == 0 && _library is { } library && !library.AppWindow.IsVisible)
        {
            library.Close();
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
