using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace WlshareViewer;

/// <summary>
/// Where the app starts: one window, which is the connect form until there is a
/// session and the desktop while there is one.
/// </summary>
public partial class App : Application
{
    private const uint MbIconError = 0x10;

    private MainWindow? _window;

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

        _window = new MainWindow(Destination.FromArguments(Environment.GetCommandLineArgs()));
        _window.Activate();
    }

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
