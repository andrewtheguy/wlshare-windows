using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace WlshareViewer;

/// <summary>
/// Where a session starts: a form for the host, the port, the user name and
/// the password, for someone who opened the app from the Start menu
/// and has no command line to put them on.
///
/// It is also where a session ends up — a refused or dropped connection brings
/// this back with the reason on it, so there is somewhere to correct and retry.
/// </summary>
internal sealed partial class ConnectView : UserControl
{
    /// <summary>Called with a destination that parsed. Everything about the
    /// session is the window's business.</summary>
    public event Action<Destination>? Chosen;

    public ConnectView()
    {
        InitializeComponent();
    }

    /// <summary>Fill the form, say why it is back if there is a reason, and put
    /// the cursor where typing should start.</summary>
    public void Show(Destination destination, string? error)
    {
        HostBox.Text = destination.Host;
        PortBox.Text = destination.Port.ToString(CultureInfo.InvariantCulture);
        UsernameBox.Text = destination.Username;
        PasswordBox.Password = destination.Password;
        Say(error);
        var first = string.IsNullOrEmpty(destination.Host) ? (Control)HostBox : PasswordBox;
        first.Focus(FocusState.Programmatic);
    }

    private void Say(string? message)
    {
        Message.Message = message ?? "";
        Message.IsOpen = !string.IsNullOrEmpty(message);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Enter in any field is Connect, which is what the button says too.
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            OnConnect(sender, e);
        }
    }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        if (host.Length == 0)
        {
            Say("Say which host to connect to.");
            return;
        }
        var portText = PortBox.Text.Trim();
        ushort port = 5900;
        if (portText.Length > 0 && (!ushort.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port == 0))
        {
            Say($"{portText} is not a port.");
            return;
        }
        Say(null);
        Chosen?.Invoke(new Destination
        {
            Host = host,
            Port = port,
            Username = UsernameBox.Text.Trim(),
            Password = PasswordBox.Password,
        });
    }
}
