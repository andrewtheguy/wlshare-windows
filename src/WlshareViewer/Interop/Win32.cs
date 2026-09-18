using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using WinRT;

namespace WlshareViewer.Interop;

/// <summary>
/// The few things WinUI does not say and Win32 does: what character a key
/// types, and a pointer shape made from pixels.
/// </summary>
internal static unsafe partial class Win32
{
    // ── Keys ────────────────────────────────────────────────────────────────

    private const int VkControl = 0x11, VkMenu = 0x12, VkLControl = 0xA2, VkRControl = 0xA3, VkLMenu = 0xA4, VkRMenu = 0xA5;
    /// <summary>ToUnicodeEx's "leave the keyboard state alone" flag: without it,
    /// asking what a dead key types consumes it, and the next key comes out
    /// without its accent.</summary>
    private const uint NoStateChange = 0x4;

    /// <summary>
    /// The character a key types with Control and Alt let go and Shift and Caps
    /// Lock as they are, as a Unicode scalar, or 0 for a key that types nothing.
    /// That is the character the desktop is sent: with Control held, Control-A
    /// types U+0001, which is not the key that was pressed.
    ///
    /// AltGr is the exception. Windows reports it as left Control and right
    /// Alt, and on a layout that has it, it is what types @, € or {; so while it
    /// is held the key is asked with both kept, and only a key AltGr types
    /// nothing with is asked again without them.
    ///
    /// A dead key answers with the accent it would put on the next key, which
    /// is what the desktop is sent for it; the desktop does its own composing.
    /// </summary>
    public static uint Character(uint vk, uint scan)
    {
        var state = stackalloc byte[256];
        if (!GetKeyboardState(state))
        {
            return 0;
        }
        var layout = GetKeyboardLayout(0);
        var typed = stackalloc char[8];
        var count = 0;
        if ((state[VkRMenu] & 0x80) != 0 && (state[VkLControl] & 0x80) != 0)
        {
            count = Math.Abs(ToUnicodeEx(vk, scan, state, typed, 8, NoStateChange, layout));
        }
        if (count == 0)
        {
            foreach (var modifier in (ReadOnlySpan<int>)[VkControl, VkLControl, VkRControl, VkMenu, VkLMenu, VkRMenu])
            {
                state[modifier] = 0;
            }
            count = Math.Abs(ToUnicodeEx(vk, scan, state, typed, 8, NoStateChange, layout));
        }
        if (count == 0)
        {
            return 0;
        }
        return count >= 2 && char.IsSurrogatePair(typed[0], typed[1])
            ? (uint)char.ConvertToUtf32(typed[0], typed[1])
            : char.IsSurrogate(typed[0]) ? 0u : typed[0];
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetKeyboardState(byte* state);

    [LibraryImport("user32.dll")]
    private static partial nint GetKeyboardLayout(uint thread);

    [LibraryImport("user32.dll")]
    private static partial int ToUnicodeEx(uint vk, uint scan, byte* state, char* buffer, int size, uint flags, nint layout);

    // ── Pointer shapes ──────────────────────────────────────────────────────

    /// <summary>
    /// An HCURSOR of `width` by `height` premultiplied RGBA — the desktop's
    /// pointer, as the core hands it over — with its hotspot where the desktop
    /// says. Destroy it with <see cref="DestroyCursor"/>.
    ///
    /// Windows takes a cursor's colour as straight alpha, so each pixel is
    /// divided back out of its alpha on the way in; left premultiplied, every
    /// antialiased edge and shadow would be darkened a second time.
    /// </summary>
    public static nint MakeCursor(byte* rgba, int width, int height, int hotspotX, int hotspotY)
    {
        var header = new BitmapV5Header
        {
            Size = (uint)sizeof(BitmapV5Header),
            Width = width,
            // Negative: rows top down, as the image is.
            Height = -height,
            Planes = 1,
            BitCount = 32,
            Compression = BiBitfields,
            RedMask = 0x00FF0000,
            GreenMask = 0x0000FF00,
            BlueMask = 0x000000FF,
            AlphaMask = 0xFF000000,
        };
        var screen = GetDC(0);
        void* bits;
        var color = CreateDIBSection(screen, &header, DibRgbColors, &bits, 0, 0);
        ReleaseDC(0, screen);
        if (color == 0)
        {
            return 0;
        }
        var into = (byte*)bits;
        for (var i = 0; i < width * height; i++)
        {
            byte r = rgba[i * 4], g = rgba[i * 4 + 1], b = rgba[i * 4 + 2], a = rgba[i * 4 + 3];
            into[i * 4] = Unpremultiply(b, a);
            into[i * 4 + 1] = Unpremultiply(g, a);
            into[i * 4 + 2] = Unpremultiply(r, a);
            into[i * 4 + 3] = a;
        }
        // A colour bitmap with alpha makes the mask a formality, but it has to
        // be there and be the same size. Zeroed rather than left to GDI, which
        // leaves a bitmap made from no bits undefined; rows are WORD-aligned.
        var maskBits = new byte[(width + 15) / 16 * 2 * height];
        nint mask;
        fixed (byte* bitsOfMask = maskBits)
        {
            mask = CreateBitmap(width, height, 1, 1, bitsOfMask);
        }
        var info = new IconInfo { Icon = 0, HotspotX = (uint)hotspotX, HotspotY = (uint)hotspotY, Mask = mask, Color = color };
        var cursor = CreateIconIndirect(&info);
        DeleteObject(mask);
        DeleteObject(color);
        return cursor;
    }

    private static byte Unpremultiply(byte channel, byte alpha) =>
        alpha == 0 ? (byte)0 : (byte)Math.Min(255, (channel * 255 + alpha / 2) / alpha);

    /// <summary>
    /// A WinUI pointer shape made from an HCURSOR, through the Windows App SDK's
    /// <c>IInputCursorStaticsInterop</c> — XAML has no public way to take one,
    /// and the desktop's pointer is pixels, not a shape Windows has a name for.
    /// </summary>
    public static InputCursor CursorFrom(nint hcursor)
    {
        var interop = new Guid("ac6f5065-90c4-46ce-beb7-05e138e54117");
        using var factory = ActivationFactory.Get("Microsoft.UI.Input.InputCursor");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory.ThisPtr, in interop, out var statics));
        try
        {
            // IInspectable's six methods come first; CreateFromHCursor is the
            // interface's one of its own.
            var create = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)(*(void***)statics)[6];
            nint made;
            Marshal.ThrowExceptionForHR(create(statics, hcursor, &made));
            try
            {
                return InputCursor.FromAbi(made);
            }
            finally
            {
                Marshal.Release(made);
            }
        }
        finally
        {
            Marshal.Release(statics);
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyCursor(nint cursor);

    private const uint BiBitfields = 3;
    private const uint DibRgbColors = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapV5Header
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
        public uint RedMask;
        public uint GreenMask;
        public uint BlueMask;
        public uint AlphaMask;
        public uint CsType;
        public fixed int Endpoints[9];
        public uint GammaRed;
        public uint GammaGreen;
        public uint GammaBlue;
        public uint Intent;
        public uint ProfileData;
        public uint ProfileSize;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int Icon;
        public uint HotspotX;
        public uint HotspotY;
        public nint Mask;
        public nint Color;
    }

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(nint dc, BitmapV5Header* header, uint usage, void** bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, void* bits);

    [LibraryImport("user32.dll")]
    private static partial nint CreateIconIndirect(IconInfo* info);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);
}
