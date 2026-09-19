using System.Runtime.InteropServices;

namespace WlshareViewer.Interop;

// The Rust core's C ABI, as C#. Hand-written, not generated:
// core/tests/interop_matches.rs reads this file beside core/src/ffi.rs and
// fails if a function, a callback or a struct field differs between the two.
// A ushort where the Rust says u32 loads cleanly and corrupts memory at run
// time; nothing else would catch it.
//
// Every struct is read through a pointer the core hands a callback, never
// marshalled, so a bool field is the one byte Rust's bool is.

/// <summary>Where a session has got to, and what the desktop looks like.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WlshareStatus
{
    public int State;
    public uint Width;
    public uint Height;
    public double Scale;
    // The desktop's sound is on: asked for, and the server has it.
    public bool Audio;
}

/// <summary>
/// The framebuffer, for the length of one callback: B, G, R, X, Stride bytes a
/// row, null before the desktop's size is known. A Generation the window has not
/// seen is a framebuffer of a new size.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WlshareFrame
{
    public byte* Pixels;
    public uint Width;
    public uint Height;
    public uint Stride;
    public ulong Generation;
    public bool Damaged;
    public uint DamageX;
    public uint DamageY;
    public uint DamageWidth;
    public uint DamageHeight;
}

/// <summary>
/// The pointer's shape, for the length of one callback: premultiplied RGBA.
/// Present false is a pointer that is hidden or has not arrived; Generation
/// tells the two apart, being zero until the first shape.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WlshareCursor
{
    public byte* Rgba;
    public ulong Generation;
    public ushort Width;
    public ushort Height;
    public ushort HotspotX;
    public ushort HotspotY;
    public bool Present;
}

internal static unsafe partial class Native
{
    private const string Dll = "wlshare_client_core";

    public const int StateConnecting = 0;
    public const int StateReady = 1;
    public const int StateClosed = 2;

    // How the desktop's pixels arrive: wlshare's VP9 stream, 4:4:4 at a
    // quality the server lowers while the link is behind, or exact ZRLE.
    public const byte EncodingVp9 = 0;
    public const byte EncodingZrle = 1;

    // The three real buttons of the RFB button mask; the wheel's four come out
    // of Wheel.
    public const byte ButtonLeft = 1;
    public const byte ButtonMiddle = 2;
    public const byte ButtonRight = 4;

    // Start a session. Never null: a connection that fails does so in the
    // status. An empty password asks for the None security type, any other for
    // RSA-AES. `audio` asks for the desktop's sound. `encoding` is one of the
    // Encoding* values; anything else is VP9.
    [LibraryImport(Dll, EntryPoint = "wlshare_client_connect", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial nint Connect(string host, ushort port, string username, string password, [MarshalAs(UnmanagedType.U1)] bool audio, byte encoding, ushort surfaceWidth, ushort surfaceHeight, double scale);

    // End the session and wait for its thread. The wake callback is cleared
    // first, so nothing calls back into the app after this returns.
    [LibraryImport(Dll, EntryPoint = "wlshare_client_close")]
    internal static partial void Close(nint client);

    [LibraryImport(Dll, EntryPoint = "wlshare_client_status")]
    internal static partial void Status(nint client, WlshareStatus* output);

    // Why the session ended, and the desktop's name, NUL-terminated into
    // `output` and truncated to fit. Both return the full length.
    [LibraryImport(Dll, EntryPoint = "wlshare_client_error")]
    internal static partial nuint Error(nint client, byte* output, nuint cap);

    [LibraryImport(Dll, EntryPoint = "wlshare_client_name")]
    internal static partial nuint Name(nint client, byte* output, nuint cap);

    // Call `wake` from the session's thread whenever there is something new to
    // draw. Null takes the callback off.
    [LibraryImport(Dll, EntryPoint = "wlshare_client_on_frame")]
    internal static partial void OnFrame(nint client, delegate* unmanaged[Cdecl]<nint, void> wake, nint ctx);

    [LibraryImport(Dll, EntryPoint = "wlshare_client_with_frame")]
    internal static partial void WithFrame(nint client, delegate* unmanaged[Cdecl]<nint, WlshareFrame*, void> visit, nint ctx);

    [LibraryImport(Dll, EntryPoint = "wlshare_client_damage_all")]
    internal static partial void DamageAll(nint client);

    [LibraryImport(Dll, EntryPoint = "wlshare_client_with_cursor")]
    internal static partial void WithCursor(nint client, delegate* unmanaged[Cdecl]<nint, WlshareCursor*, void> visit, nint ctx);

    [LibraryImport(Dll, EntryPoint = "wlshare_client_pointer")]
    internal static partial void Pointer(nint client, byte buttons, ushort x, ushort y);

    [LibraryImport(Dll, EntryPoint = "wlshare_client_key")]
    internal static partial void Key(nint client, [MarshalAs(UnmanagedType.U1)] bool down, uint keysym);

    [LibraryImport(Dll, EntryPoint = "wlshare_client_surface")]
    internal static partial void Surface(nint client, ushort width, ushort height, double scale);

    // The Windows clipboard, `len` bytes of UTF-8, for the desktop. The core
    // sends it only when the desktop asks for it.
    [LibraryImport(Dll, EntryPoint = "wlshare_client_set_clipboard")]
    internal static partial void SetClipboard(nint client, byte* text, nuint len);

    // The desktop's clipboard, which arrival it is and `len` bytes of UTF-8 —
    // null and 0 before the desktop has provided any.
    [LibraryImport(Dll, EntryPoint = "wlshare_client_with_clipboard")]
    internal static partial void WithClipboard(nint client, delegate* unmanaged[Cdecl]<nint, ulong, byte*, nuint, void> visit, nint ctx);

    // The next `frames` of the desktop's sound, 48 kHz stereo, into two
    // separate channel buffers — silence where there is none yet. For the audio
    // device's render thread; the core holds its lock for the copy.
    [LibraryImport(Dll, EntryPoint = "wlshare_client_read_audio")]
    internal static partial void ReadAudio(nint client, float* left, float* right, nuint frames);

    [LibraryImport(Dll, EntryPoint = "wlshare_client_wheel")]
    internal static partial nuint Wheel(nint client, int delta, [MarshalAs(UnmanagedType.U1)] bool horizontal, byte* output, nuint cap);

    [LibraryImport(Dll, EntryPoint = "wlshare_keysym")]
    internal static partial uint Keysym(ushort vk, ushort scan, [MarshalAs(UnmanagedType.U1)] bool extended, uint character);
}
