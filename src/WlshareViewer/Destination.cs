using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WlshareViewer;

/// <summary>How the desktop's pixels are to arrive.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PixelEncoding>))]
internal enum PixelEncoding
{
    /// <summary>wlshare's VP9 stream: the whole desktop, 4:4:4, at the quality
    /// the server sets. The default: small and smooth when the desktop moves. A
    /// server without it is an error, not a fallback to ZRLE.</summary>
    Vp9,
    /// <summary>ZRLE: every pixel exactly as the desktop drew it.</summary>
    Zrle,
}

/// <summary>
/// A desktop to connect to, and what is remembered about it between launches.
///
/// The host, the port, the user name, the encoding and whether to play the
/// desktop's sound are remembered, in %LOCALAPPDATA%\wlshare\settings.json.
/// The password is not: it is typed into the form each time, and a file is no
/// place for one.
/// </summary>
internal sealed record Destination
{
    public string Host { get; init; } = "";
    public ushort Port { get; init; } = 5900;
    public string Username { get; init; } = "";
    [JsonIgnore]
    public string Password { get; init; } = "";
    /// <summary>Ask for the desktop's sound. A server without it gives none
    /// either way.</summary>
    public bool Audio { get; init; }
    public PixelEncoding Encoding { get; init; } = PixelEncoding.Vp9;

    public string Label => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    /// <summary>
    /// The command line, for a launch that came from a shell:
    ///
    ///     WlshareViewer.exe --server 192.168.1.10:5900 --username me --audio --encoding zrle
    ///
    /// --audio is the form's sound checkbox; without it the session is silent.
    /// --encoding is the form's encoding, vp9 (the default) or zrle.
    /// Null when no --server was given, which is every launch from the Start
    /// menu — those get the form. There is no password argument: an argument
    /// list is in the shell's history and in every process listing. A launch
    /// from here connects without one, and a desktop that wants one refuses and
    /// brings the form back to type it into.
    /// </summary>
    public static Destination? FromArguments(string[] args)
    {
        string? Value(string name)
        {
            var at = Array.IndexOf(args, name);
            return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
        }

        var server = Value("--server");
        if (server is null)
        {
            return null;
        }
        var (host, port) = Split(server);
        return Remembered() with
        {
            Host = host,
            Port = port,
            Username = Value("--username") ?? "",
            Password = "",
            Audio = args.Contains("--audio"),
            Encoding = string.Equals(Value("--encoding"), "zrle", StringComparison.OrdinalIgnoreCase) ? PixelEncoding.Zrle : PixelEncoding.Vp9,
        };
    }

    /// <summary>`host:port`, `host`, or an IPv6 literal in brackets.</summary>
    public static (string Host, ushort Port) Split(string server)
    {
        if (server.StartsWith('[') && server.IndexOf(']') is var end and > 0)
        {
            var rest = server[(end + 1)..];
            var port = rest.StartsWith(':') && ushort.TryParse(rest[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var p) ? p : (ushort)5900;
            return (server[1..end], port);
        }
        var colon = server.LastIndexOf(':');
        // More than one colon and no brackets is a bare IPv6 address.
        if (colon < 0 || server.IndexOf(':') != colon
            || !ushort.TryParse(server[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
        {
            return (server, 5900);
        }
        return (server[..colon], parsed);
    }

    private static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "wlshare", "settings.json");

    /// <summary>What the form opens filled with.</summary>
    public static Destination Remembered()
    {
        try
        {
            var saved = JsonSerializer.Deserialize(File.ReadAllText(SettingsPath), SettingsJson.Default.Destination);
            if (saved is not null)
            {
                return saved with { Port = saved.Port == 0 ? (ushort)5900 : saved.Port };
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Nothing remembered yet, or nothing readable: the form starts empty.
        }
        return new Destination();
    }

    /// <summary>Remember this one, without its password.</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, SettingsJson.Default.Destination));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not remembering is not a reason to stop connecting.
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Destination))]
internal sealed partial class SettingsJson : JsonSerializerContext;
