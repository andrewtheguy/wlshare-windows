using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace WlshareViewer;

/// <summary>
/// Where the app starts, and what holds it together: one connect form, and a
/// window for every desktop opened from it.
///
/// <b>Connect</b> adds a session; it never takes one away, so several desktops
/// stand side by side. The form is put away while a desktop is up and comes
/// back on <b>New connection</b>, on <b>Disconnect</b>, and whenever a session
/// ends by itself, with the reason and which desktop it is about. Closing the
/// last desktop's window with the form already put away closes the app, as
/// closing the form does when there is no desktop left.
/// </summary>
public partial class App : Application
{
    private const uint MbIconError = 0x10;

    private ConnectWindow? _form;
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

        // Made whether or not it is shown at once: it is the app's only form,
        // and the window a session comes back to.
        var form = new ConnectWindow(() => _sessions.Count > 0);
        form.Chosen += Open;
        _form = form;

        // A launch from a shell says where to go; a launch from the Start menu
        // asks.
        if (Destination.FromArguments(Environment.GetCommandLineArgs()) is not { } fromArguments)
        {
            form.Show(null);
            return;
        }
        // The password saved for the same place and user, if any. One that will
        // not open is not tried as none: the form says why, and is where it is
        // typed. The form gets the destination without it, so a retry does not
        // show it.
        var profile = form.View.Profiles.Matching(fromArguments);
        form.View.Load(fromArguments, profile?.Id);
        Destination attempt;
        try
        {
            attempt = fromArguments with { Password = profile?.Password() ?? "" };
        }
        catch (SafeStorageException e)
        {
            form.Show(e.Message);
            return;
        }
        Open(attempt);
    }

    /// <summary>Open a desktop in a window of its own, beside whatever is
    /// already open, and put the form away behind it.</summary>
    private void Open(Destination destination)
    {
        var session = new SessionWindow(destination, _opened++);
        session.FormWanted += () => _form?.Show(null);
        session.Dropped += (window, reason) =>
        {
            // The form first, with which desktop it is about, and only then the
            // window away — in that order, because an app briefly down to no
            // windows at all is an app that closes itself.
            _form?.Show($"{window.Destination.Label}: {reason}");
            window.Close();
        };
        session.Closed += (_, _) => Ended(session);
        _sessions.Add(session);
        session.Activate();
        _form?.AppWindow.Hide();
    }

    /// <summary>A desktop's window has gone. With nothing else on the screen —
    /// no other desktop, and a form that was put away rather than asked for —
    /// the app goes with it.</summary>
    private void Ended(SessionWindow session)
    {
        _sessions.Remove(session);
        if (_sessions.Count == 0 && _form is { } form && !form.AppWindow.IsVisible)
        {
            form.Close();
        }
    }

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
