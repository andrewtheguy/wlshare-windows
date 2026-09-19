using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WlshareViewer.Interop;

namespace WlshareViewer;

/// <summary>
/// A saved desktop: what the form holds, under a name — and the password only
/// when it was asked to keep one, and then sealed, never in the clear.
/// </summary>
internal sealed record Profile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public string Host { get; init; } = "";
    public ushort Port { get; init; } = 5900;
    public string Username { get; init; } = "";
    public bool Audio { get; init; }
    public PixelEncoding Encoding { get; init; } = PixelEncoding.Vp9;
    /// <summary>Whether the form's <b>Save the password</b> is ticked for this
    /// one.</summary>
    public bool SavesPassword { get; init; }
    /// <summary>The password, sealed by <see cref="SafeStorage"/> with this
    /// profile's id bound in. Null is none saved, which a ticked checkbox with
    /// nothing typed also is.</summary>
    public byte[]? SealedPassword { get; init; }

    /// <summary>What the list shows in bold: the name, or where it goes when it
    /// has none.</summary>
    [JsonIgnore]
    public string Title =>
        Name.Length > 0 ? Name : Host.Length == 0 ? "New Desktop" : Destination.Format(Host, Port);

    /// <summary>What the list shows under the title: who goes where, less
    /// whatever the title already says. Empty for a profile with only a host
    /// and port.</summary>
    [JsonIgnore]
    public string Address
    {
        get
        {
            if (Host.Length == 0)
            {
                return "";
            }
            if (Name.Length == 0)
            {
                return Username;
            }
            var label = Destination.Format(Host, Port);
            return Username.Length == 0 ? label : $"{Username}@{label}";
        }
    }

    /// <summary>The saved password, opened; null when none is saved.</summary>
    public string? Password() => SealedPassword is null ? null : SafeStorage.Open(SealedPassword, Id);

    public Destination ToDestination(string password) => new()
    {
        Host = Host,
        Port = Port,
        Username = Username,
        Password = password,
        Audio = Audio,
        Encoding = Encoding,
    };

    /// <summary>A profile not saved yet, filled with a destination from
    /// somewhere else.</summary>
    public static Profile From(Destination destination) => new()
    {
        Host = destination.Host,
        Port = destination.Port,
        Username = destination.Username,
        Audio = destination.Audio,
        Encoding = destination.Encoding,
    };
}

/// <summary>
/// The saved profiles, in the order they were made, and which one the form was
/// last showing, in %LOCALAPPDATA%\wlshare\profiles.json.
///
/// A file is all right here: the only secret in a profile is sealed, and the
/// key that opens it is in Credential Manager.
/// </summary>
internal sealed class ProfileStore
{
    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wlshare", "profiles.json");

    private readonly List<Profile> _profiles;
    private Guid? _selected;

    public ProfileStore()
    {
        ProfileFile? saved = null;
        try
        {
            saved = JsonSerializer.Deserialize(File.ReadAllText(FilePath), ProfilesJson.Default.ProfileFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Nothing saved yet, or nothing readable: the list starts empty.
        }
        _profiles = saved?.Profiles ?? [];
        _selected = saved?.Selected;
    }

    public IReadOnlyList<Profile> Profiles => _profiles;

    /// <summary>The one the form was showing when the app was last used. Not
    /// keeping it is not a reason to stop anything, so a write that fails is
    /// let go.</summary>
    public Guid? Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
            {
                return;
            }
            _selected = value;
            try
            {
                Write();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public Profile? Find(Guid id) => _profiles.Find(p => p.Id == id);

    public int IndexOf(Guid id) => _profiles.FindIndex(p => p.Id == id);

    /// <summary>The first profile that goes where <paramref name="destination"/>
    /// does, as the same user: what a <c>--server</c> launch takes a saved
    /// password from.</summary>
    public Profile? Matching(Destination destination) => _profiles.Find(p =>
        p.Host == destination.Host && p.Port == destination.Port && p.Username == destination.Username);

    /// <summary>Save <paramref name="profile"/>, over the one with its id or
    /// after the rest. Throws what the file does.</summary>
    public void Put(Profile profile)
    {
        var at = IndexOf(profile.Id);
        if (at >= 0)
        {
            _profiles[at] = profile;
        }
        else
        {
            _profiles.Add(profile);
        }
        Write();
    }

    /// <summary>Throws what the file does.</summary>
    public void Remove(Guid id)
    {
        _profiles.RemoveAll(p => p.Id == id);
        if (_selected == id)
        {
            _selected = null;
        }
        Write();
    }

    /// <summary>Written beside itself and moved over the old one, so a write
    /// cut short leaves the last whole list, not half of this one.</summary>
    private void Write()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var partial = FilePath + ".partial";
        File.WriteAllText(partial, JsonSerializer.Serialize(new ProfileFile { Profiles = _profiles, Selected = _selected }, ProfilesJson.Default.ProfileFile));
        File.Move(partial, FilePath, overwrite: true);
    }
}

internal sealed record ProfileFile
{
    public List<Profile> Profiles { get; init; } = [];
    public Guid? Selected { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ProfileFile))]
internal sealed partial class ProfilesJson : JsonSerializerContext;

/// <summary>Why a saved password cannot be sealed or opened, in words for the
/// form.</summary>
internal sealed class SafeStorageException(string message) : Exception(message);

/// <summary>
/// Saved passwords the way Chrome and Slack keep theirs: one random key in
/// Credential Manager — the only credential this app puts there — and every
/// password sealed with it where the profiles are.
///
/// A password is sealed with AES-GCM and the profile's id as associated data,
/// so a sealed password copied onto another profile does not open.
/// </summary>
internal static class SafeStorage
{
    /// <summary>The generic credential's name, which is what Credential
    /// Manager lists it as.</summary>
    public const string Target = "WlshareViewer Safe Storage";
    private const string Account = "WlshareViewer";
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <summary>Read at most once a launch.</summary>
    private static byte[]? s_key;

    /// <summary>The password as it is kept: the nonce, the ciphertext and the
    /// tag, in that order.</summary>
    public static byte[] Seal(string password, Guid id)
    {
        var key = MasterKey(creating: true);
        var plain = Encoding.UTF8.GetBytes(password);
        var box = new byte[NonceSize + plain.Length + TagSize];
        var nonce = box.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, box.AsSpan(NonceSize, plain.Length), box.AsSpan(NonceSize + plain.Length), Bound(id));
        return box;
    }

    public static string Open(byte[] box, Guid id)
    {
        var key = MasterKey(creating: false);
        if (box.Length < NonceSize + TagSize)
        {
            throw Unreadable();
        }
        var plain = new byte[box.Length - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        try
        {
            aes.Decrypt(box.AsSpan(0, NonceSize), box.AsSpan(NonceSize, plain.Length), box.AsSpan(NonceSize + plain.Length), plain, Bound(id));
        }
        catch (AuthenticationTagMismatchException)
        {
            throw Unreadable();
        }
        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] Bound(Guid id) => Encoding.UTF8.GetBytes(id.ToString("D"));

    private static SafeStorageException Unreadable() =>
        new("The saved password does not open with Credential Manager's key; type it again.");

    /// <summary>
    /// The key, from Credential Manager — made and put there by the first
    /// password saved, and never by a read: a key made when the old one is
    /// missing opens nothing the old one sealed.
    ///
    /// Kept as base64 text in UTF-16, so the credential reads as an ordinary
    /// password to anything that lists them.
    /// </summary>
    private static byte[] MasterKey(bool creating)
    {
        if (s_key is not null)
        {
            return s_key;
        }
        byte[]? found;
        try
        {
            found = Win32.ReadCredential(Target);
        }
        catch (Win32Exception e)
        {
            throw new SafeStorageException($"Credential Manager would not give up the key to saved passwords: {e.Message}");
        }
        if (found is not null)
        {
            byte[]? raw = null;
            try
            {
                raw = Convert.FromBase64String(Encoding.Unicode.GetString(found));
            }
            catch (FormatException)
            {
            }
            if (raw is not { Length: KeySize })
            {
                throw new SafeStorageException("The key to saved passwords in Credential Manager is not one this app made.");
            }
            return s_key = raw;
        }
        if (!creating)
        {
            throw new SafeStorageException("The key to saved passwords is gone from Credential Manager; type the password again.");
        }
        var made = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            Win32.WriteCredential(Target, Account, Encoding.Unicode.GetBytes(Convert.ToBase64String(made)));
        }
        catch (Win32Exception e)
        {
            throw new SafeStorageException($"Credential Manager would not keep the key to saved passwords: {e.Message}");
        }
        return s_key = made;
    }
}
