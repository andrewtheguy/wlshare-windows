using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace WlshareViewer;

/// <summary>
/// Where a session starts: the saved desktops as a list, and a form for the one
/// selected — its name, the host, the port, the user name, the password, the
/// encoding and whether to play the desktop's sound — for someone who opened
/// the app from the Start menu and has no command line to put them on.
///
/// Every connection made here is to a profile. <b>Connect</b> saves the form
/// into the selected one first, and with nothing selected makes a new one of
/// it, so a desktop connected to once is in the list from then on; <b>+</b>
/// starts an empty one and <b>−</b> deletes one. What is typed into the form is
/// saved when the selection moves, on <b>Connect</b>, and when the window
/// closes.
///
/// It is also where a session ends up — a refused or dropped connection brings
/// this back with the reason on it and the form as it was, password included,
/// so there is somewhere to correct and retry.
/// </summary>
internal sealed partial class ConnectView : UserControl
{
    /// <summary>Called with a destination that parsed, after it has been saved
    /// as a profile. Everything about the session is the window's
    /// business.</summary>
    public event Action<Destination>? Chosen;

    public ProfileStore Profiles { get; } = new();

    /// <summary>The profile the form is showing; null for a desktop not saved
    /// yet.</summary>
    private Guid? _current;
    /// <summary>Set while the list is changed from here, so its selection
    /// moving is not taken for a click.</summary>
    private bool _moving;

    public ConnectView()
    {
        InitializeComponent();
        Refill();
        Select(Profiles.Selected is { } selected && Profiles.Find(selected) is not null
            ? selected
            : Profiles.Profiles.FirstOrDefault()?.Id);
        if (_current is null)
        {
            Fill(new Profile());
        }
    }

    /// <summary>Fill the form with a destination from the command line, before
    /// it is ever shown: the profile it matched if there is one, or a desktop
    /// not saved yet. A connection refused brings this back as it was tried —
    /// with the command line's sound and encoding, not the profile's.</summary>
    public void Load(Destination destination, Guid? profile)
    {
        Commit();
        Select(profile);
        var tried = (profile is { } id ? Profiles.Find(id) : null) ?? Profile.From(destination);
        Fill(tried with { Audio = destination.Audio, Encoding = destination.Encoding });
        PasswordBox.Password = destination.Password;
    }

    /// <summary>Say why the form is back if there is a reason, and put the
    /// cursor where typing should start: a saved desktop usually wants only the
    /// password, or nothing at all.</summary>
    public void Show(string? error)
    {
        Say(error);
        var saved = _current is { } id && Profiles.Find(id)?.SealedPassword is not null;
        Control first = HostBox.Text.Trim().Length == 0 ? HostBox
            : saved || PasswordBox.Password.Length > 0 ? List : PasswordBox;
        first.Focus(FocusState.Programmatic);
    }

    /// <summary>Keep what is typed into the form, for a window about to close.
    /// False when it cannot be kept, with the reason on the form.</summary>
    public bool Save() => Commit();

    // ── The form ────────────────────────────────────────────────────────────

    /// <summary>Show <paramref name="profile"/> in the form. The password field
    /// starts empty whatever is saved: a saved password is opened when it is
    /// connected with, not when it is looked at.</summary>
    private void Fill(Profile profile)
    {
        NameBox.Text = profile.Name;
        HostBox.Text = profile.Host;
        PortBox.Text = profile.Port.ToString(CultureInfo.InvariantCulture);
        UsernameBox.Text = profile.Username;
        PasswordBox.Password = "";
        PasswordBox.PlaceholderText = profile.SealedPassword is null ? "none" : "saved";
        SavesPasswordBox.IsChecked = profile.SavesPassword;
        AudioBox.IsChecked = profile.Audio;
        EncodingBox.SelectedIndex = (int)profile.Encoding;
        Say(null);
        RemoveButton.IsEnabled = _current is not null;
    }

    /// <summary>Write the form into the profile it is showing — or, with
    /// <paramref name="creating"/>, into a new one when it is showing none.
    /// False, with the reason on the form, when the port is not a port, the
    /// password would not seal or the list would not save.</summary>
    private bool Commit(bool creating = false)
    {
        var portText = PortBox.Text.Trim();
        ushort port = 5900;
        if (portText.Length > 0 && (!ushort.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port == 0))
        {
            Fail("A port is a number from 1 to 65535.", PortBox);
            return false;
        }
        Profile profile;
        if (_current is { } id && Profiles.Find(id) is { } saved)
        {
            profile = saved;
        }
        else if (creating)
        {
            profile = new Profile();
        }
        else
        {
            return true;
        }
        profile = profile with
        {
            Name = NameBox.Text.Trim(),
            Host = HostBox.Text.Trim(),
            Port = port,
            Username = UsernameBox.Text.Trim(),
            Audio = AudioBox.IsChecked == true,
            Encoding = EncodingBox.SelectedIndex == (int)PixelEncoding.Zrle ? PixelEncoding.Zrle : PixelEncoding.Vp9,
            SavesPassword = SavesPasswordBox.IsChecked == true,
        };
        if (!profile.SavesPassword)
        {
            profile = profile with { SealedPassword = null };
        }
        else if (PasswordBox.Password.Length > 0)
        {
            try
            {
                profile = profile with { SealedPassword = SafeStorage.Seal(PasswordBox.Password, profile.Id) };
            }
            catch (SafeStorageException e)
            {
                Fail(e.Message, PasswordBox);
                return false;
            }
        }
        try
        {
            Profiles.Put(profile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Fail($"The desktops could not be saved: {e.Message}", NameBox);
            return false;
        }
        if (_current is null)
        {
            _current = profile.Id;
            Profiles.Selected = profile.Id;
            Refill();
            Select(profile.Id);
        }
        else if (Profiles.IndexOf(profile.Id) is var row and >= 0)
        {
            ((ListViewItem)List.Items[row]).Content = Row(profile);
        }
        PasswordBox.PlaceholderText = profile.SealedPassword is null ? "none" : "saved";
        RemoveButton.IsEnabled = true;
        return true;
    }

    private void OnConnect(object sender, RoutedEventArgs e) => Connect();

    private void Connect()
    {
        if (HostBox.Text.Trim().Length == 0)
        {
            Fail("Say which host to connect to.", HostBox);
            return;
        }
        // Taken before the commit, which drops it when the checkbox is off.
        var saved = _current is { } before ? Profiles.Find(before)?.SealedPassword : null;
        if (!Commit(creating: true) || _current is not { } id || Profiles.Find(id) is not { } profile)
        {
            return;
        }
        // What is typed wins; with nothing typed, what is saved. A password
        // saved and no longer wanted is still the one to connect with this
        // once, as it was when the checkbox was unticked.
        var password = PasswordBox.Password;
        if (password.Length == 0 && saved is not null)
        {
            try
            {
                password = SafeStorage.Open(saved, profile.Id);
            }
            catch (SafeStorageException error)
            {
                Fail(error.Message, PasswordBox);
                return;
            }
        }
        Say(null);
        Chosen?.Invoke(profile.ToDestination(password));
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Enter in any field is Connect, which is what the button says too.
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            Connect();
        }
    }

    private void Say(string? message)
    {
        Message.Message = message ?? "";
        Message.IsOpen = !string.IsNullOrEmpty(message);
    }

    private void Fail(string reason, Control field)
    {
        Say(reason);
        field.Focus(FocusState.Programmatic);
    }

    // ── The list ────────────────────────────────────────────────────────────

    /// <summary>The list as tall as the form beside it, a message line more or
    /// less.</summary>
    private void OnFormSized(object sender, SizeChangedEventArgs e)
    {
        ListPanel.Height = e.NewSize.Height;
    }

    private void OnAdd(object sender, RoutedEventArgs e)
    {
        // A desktop typed in but not saved yet is kept, not dropped.
        if (!Commit(creating: HostBox.Text.Trim().Length > 0))
        {
            return;
        }
        var profile = new Profile();
        try
        {
            Profiles.Put(profile);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Fail($"The desktops could not be saved: {error.Message}", NameBox);
            return;
        }
        Refill();
        Select(profile.Id);
        HostBox.Focus(FocusState.Programmatic);
    }

    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (_current is not { } id || Profiles.Find(id) is not { } profile)
        {
            return;
        }
        var ask = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Delete “{profile.Title}”?",
            Content = profile.SealedPassword is null
                ? "It is removed from the list."
                : "It is removed from the list, with its saved password.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ask.ShowAsync() != ContentDialogResult.Primary || _current != id)
        {
            return;
        }

        var row = Profiles.IndexOf(id);
        try
        {
            Profiles.Remove(id);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Fail($"The desktops could not be saved: {error.Message}", NameBox);
            return;
        }
        _current = null;
        Refill();
        var rest = Profiles.Profiles;
        Guid? next = rest.Count == 0 ? null : rest[Math.Min(row, rest.Count - 1)].Id;
        Select(next);
        if (next is null)
        {
            Fill(new Profile());
        }
    }

    /// <summary>Select <paramref name="id"/> in the list, and show it in the
    /// form if it is not already.</summary>
    private void Select(Guid? id)
    {
        var row = id is { } found ? Profiles.IndexOf(found) : -1;
        _moving = true;
        List.SelectedIndex = row;
        _moving = false;
        if (row >= 0)
        {
            List.ScrollIntoView(List.Items[row]);
        }
        Shown(row >= 0 ? id : null);
    }

    private void Shown(Guid? id)
    {
        if (id == _current)
        {
            return;
        }
        _current = id;
        Profiles.Selected = id;
        Fill((id is { } found ? Profiles.Find(found) : null) ?? new Profile());
    }

    /// <summary>Moving off a profile saves what was typed into it, and a port
    /// that is not one puts the selection back until it is put right.</summary>
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_moving)
        {
            return;
        }
        if (!Commit())
        {
            _moving = true;
            List.SelectedIndex = _current is { } id ? Profiles.IndexOf(id) : -1;
            _moving = false;
            return;
        }
        Shown((List.SelectedItem as ListViewItem)?.Tag as Guid?);
    }

    /// <summary>A double-click on a row is Connect; one on the empty space
    /// under the rows is nothing.</summary>
    private void OnListDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        for (var at = e.OriginalSource as DependencyObject; at is not null && at != List; at = VisualTreeHelper.GetParent(at))
        {
            if (at is ListViewItem)
            {
                Connect();
                return;
            }
        }
    }

    /// <summary>The list made again from the saved profiles, with nothing
    /// selected.</summary>
    private void Refill()
    {
        _moving = true;
        List.Items.Clear();
        foreach (var profile in Profiles.Profiles)
        {
            List.Items.Add(new ListViewItem { Content = Row(profile), Tag = profile.Id });
        }
        _moving = false;
    }

    /// <summary>One row: the name, and under it who goes where. No picture of
    /// the desktop — a row is a line of text.</summary>
    private static StackPanel Row(Profile profile)
    {
        var title = new TextBlock
        {
            Text = profile.Title,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var detail = new TextBlock
        {
            Text = profile.Address,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            // Collapsed rather than blank, so a title on its own is centred.
            Visibility = profile.Address.Length == 0 ? Visibility.Collapsed : Visibility.Visible,
        };
        return new StackPanel { Padding = new Thickness(0, 6, 0, 6), VerticalAlignment = VerticalAlignment.Center, Children = { title, detail } };
    }
}
