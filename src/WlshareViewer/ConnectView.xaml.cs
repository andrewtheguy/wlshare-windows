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
/// Every connection made here is to a profile. Nothing is saved by itself:
/// <b>Save</b> writes the form into the selected profile, or makes a new one of
/// it when none is selected, and <b>Connect</b> does the same and then
/// connects, so a desktop connected to once is in the list from then on.
/// <b>+</b> clears the form for a new desktop, which is in the list only once
/// it is saved, and <b>−</b> deletes one. Moving the selection, closing the
/// window and closing the app with something unsaved in the form ask whether
/// to keep it, as a document would.
///
/// One library, as many desktops as have been opened from it: <b>Connect</b>
/// adds a window and leaves this one where it is, never taking one away, and
/// <b>Library</b> in a desktop's toolbar brings it forward from behind
/// whatever is open.
///
/// It is also where a session ends up — a refused or dropped connection brings
/// this back with the reason on it, which desktop it is about, and the form as
/// it was left, so there is somewhere to correct and retry.
/// </summary>
internal sealed partial class ConnectView : UserControl
{
    /// <summary>Called with a destination that parsed, after it has been saved
    /// as a profile. Everything about the session is the window's
    /// business.</summary>
    public event Action<Destination>? Chosen;

    public ProfileStore Profiles { get; } = new();

    /// <summary>Whether the window has been on the screen. A form nobody has
    /// seen holds nothing anybody typed: a command-line launch fills it in
    /// case the connection is refused, and closing the app must not ask to
    /// save that.</summary>
    public bool Presented { get; private set; }

    /// <summary>The profile the form is showing; null for a desktop not saved
    /// yet.</summary>
    private Guid? _current;
    /// <summary>Set while the list is changed from here, so its selection
    /// moving is not taken for a click.</summary>
    private bool _moving;
    /// <summary>Set while a dialog is up: a second one over the same content
    /// is an error, not a queue.</summary>
    private bool _asking;

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
        Select(profile);
        var tried = (profile is { } id ? Profiles.Find(id) : null) ?? Profile.From(destination);
        Fill(tried with { Audio = destination.Audio, Encoding = destination.Encoding });
    }

    /// <summary>Say why the form is back if there is a reason, and put the
    /// cursor where typing should start: a saved desktop usually wants only the
    /// password, or nothing at all.</summary>
    public void Show(string? error)
    {
        // The first time up with nothing to correct, a form filled from the
        // command line goes back to what is stored: nobody typed it, so it is
        // not an edit to offer or ask about. With a reason it stays as it was
        // tried, which is what there is to correct.
        if (!Presented && error is null)
        {
            Fill(Stored() ?? new Profile());
        }
        Presented = true;
        Say(error);
        var saved = _current is { } id && Profiles.Find(id)?.SealedPassword is not null;
        Control first = HostBox.Text.Trim().Length == 0 ? HostBox
            : saved || PasswordBox.Password.Length > 0 ? List : PasswordBox;
        first.Focus(FocusState.Programmatic);
    }

    /// <summary>Whether the form differs from what is saved: the selected
    /// profile, or nothing for a desktop not saved yet. A typed password
    /// counts only when it would be kept.</summary>
    public bool HasEdits
    {
        get
        {
            var stored = Stored() ?? new Profile();
            if (Draft(stored) is not { } draft)
            {
                return true;
            }
            return draft != stored || (draft.SavesPassword && PasswordBox.Password.Length > 0);
        }
    }

    /// <summary>Ask what to do with unsaved edits, when there are any: true
    /// once the form holds nothing that is not saved or let go of, false to
    /// stay put — which is also the answer while another dialog is up. Letting
    /// go puts the form back as it is saved, so what was typed does not come
    /// back with the window.</summary>
    public async Task<bool> SettleAsync()
    {
        if (!HasEdits)
        {
            return true;
        }
        var stored = Stored();
        var ask = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = stored is null ? "Save this desktop?" : $"Save the changes to “{stored.Title}”?",
            Content = "What is typed into the form is lost otherwise.",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Don't Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        switch (await AskAsync(ask))
        {
            case ContentDialogResult.Primary:
                return Commit();
            case ContentDialogResult.Secondary:
                Fill(stored ?? new Profile());
                return true;
            default:
                return false;
        }
    }

    /// <summary>One dialog at a time over this content; a second asked for
    /// while one is up is answered as dismissed.</summary>
    private async Task<ContentDialogResult> AskAsync(ContentDialog dialog)
    {
        if (_asking)
        {
            return ContentDialogResult.None;
        }
        _asking = true;
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            _asking = false;
        }
    }

    // ── The form ────────────────────────────────────────────────────────────

    /// <summary>The profile the form is showing, as it is saved; null for a
    /// desktop not saved yet.</summary>
    private Profile? Stored() => _current is { } id ? Profiles.Find(id) : null;

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
        Edited();
    }

    /// <summary>Something in the form changed: <b>Save</b> is offered while it
    /// differs from what is saved.</summary>
    private void Edited()
    {
        SaveButton.IsEnabled = HasEdits;
    }

    private void OnTextEdited(object sender, TextChangedEventArgs e) => Edited();

    private void OnPasswordEdited(object sender, RoutedEventArgs e) => Edited();

    private void OnEncodingEdited(object sender, SelectionChangedEventArgs e) => Edited();

    private void OnChecked(object sender, RoutedEventArgs e) => Edited();

    /// <summary>The port box's port: 5900 when it is empty, false when it
    /// holds something that is not a port.</summary>
    private bool TryPort(out ushort port)
    {
        port = 5900;
        var text = PortBox.Text.Trim();
        return text.Length == 0
            || (ushort.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port != 0);
    }

    /// <summary>The form as a profile over <paramref name="stored"/> — its id,
    /// and its sealed password while the checkbox says to keep one — or null
    /// when the port is not a port. What is typed into the password field is
    /// not sealed here.</summary>
    private Profile? Draft(Profile stored)
    {
        if (!TryPort(out var port))
        {
            return null;
        }
        var profile = stored with
        {
            Name = NameBox.Text.Trim(),
            Host = HostBox.Text.Trim(),
            Port = port,
            Username = UsernameBox.Text.Trim(),
            Audio = AudioBox.IsChecked == true,
            Encoding = EncodingBox.SelectedIndex == (int)PixelEncoding.Zrle ? PixelEncoding.Zrle : PixelEncoding.Vp9,
            SavesPassword = SavesPasswordBox.IsChecked == true,
        };
        return profile.SavesPassword ? profile : profile with { SealedPassword = null };
    }

    /// <summary>Write the form into the profile it is showing, or into a new
    /// one when it is showing none. False, with the reason on the form, when
    /// there is no host, the port is not a port, the password would not seal
    /// or the list would not save.</summary>
    private bool Commit()
    {
        if (HostBox.Text.Trim().Length == 0)
        {
            Fail("A host is needed.", HostBox);
            return false;
        }
        if (Draft(Stored() ?? new Profile()) is not { } profile)
        {
            Fail("A port is a number from 1 to 65535.", PortBox);
            return false;
        }
        var typed = profile.SavesPassword && PasswordBox.Password.Length > 0;
        if (typed)
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
        if (typed)
        {
            // Saved now, and shown as saved rather than left typed.
            PasswordBox.Password = "";
        }
        if (_current is null)
        {
            _current = profile.Id;
            Profiles.Selected = profile.Id;
        }
        Sync();
        PasswordBox.PlaceholderText = profile.SealedPassword is null ? "none" : "saved";
        RemoveButton.IsEnabled = true;
        Say(null);
        Edited();
        return true;
    }

    /// <summary><b>Save</b>: the form into its profile, and nothing else.</summary>
    private void OnSave(object sender, RoutedEventArgs e) => Commit();

    private void OnConnect(object sender, RoutedEventArgs e) => Connect();

    private void Connect()
    {
        // Both taken before the commit, which drops the saved one when the
        // checkbox is off and clears the field once the typed one is sealed.
        var saved = Stored()?.SealedPassword;
        var typed = PasswordBox.Password;
        if (!Commit() || _current is not { } id || Profiles.Find(id) is not { } profile)
        {
            return;
        }
        // What is typed wins; with nothing typed, what is saved. A password
        // saved and no longer wanted is still the one to connect with this
        // once, as it was when the checkbox was unticked.
        var password = typed;
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

    /// <summary>Clear the form for a desktop not saved yet. It joins the list
    /// on <b>Save</b> or <b>Connect</b>, not before; something unsaved already
    /// in the form is asked about first.</summary>
    private async void OnAdd(object sender, RoutedEventArgs e)
    {
        if (!await SettleAsync())
        {
            return;
        }
        Select(null);
        Fill(new Profile());
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
        if (await AskAsync(ask) != ContentDialogResult.Primary || _current != id)
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

    /// <summary>Moving off a profile with something unsaved typed into it asks
    /// first, and staying is staying: the selection does not move.</summary>
    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_moving)
        {
            return;
        }
        var target = (List.SelectedItem as ListViewItem)?.Tag as Guid?;
        if (HasEdits)
        {
            // Back on what the form shows until the answer is in: a save that
            // fails, or a Cancel, leaves the selection where it was.
            _moving = true;
            List.SelectedIndex = _current is { } id ? Profiles.IndexOf(id) : -1;
            _moving = false;
            if (!await SettleAsync())
            {
                return;
            }
        }
        Select(target);
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

    /// <summary>The list brought up to the saved profiles — which another
    /// launch of the app may have added to or taken from since it was made —
    /// with the selection kept.</summary>
    private void Sync()
    {
        var profiles = Profiles.Profiles;
        var same = List.Items.Count == profiles.Count
            && profiles.Select((profile, row) => ((ListViewItem)List.Items[row]).Tag is Guid id && id == profile.Id).All(x => x);
        if (!same)
        {
            Refill();
            Select(_current);
            return;
        }
        for (var row = 0; row < profiles.Count; row++)
        {
            ((ListViewItem)List.Items[row]).Content = Row(profiles[row]);
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
