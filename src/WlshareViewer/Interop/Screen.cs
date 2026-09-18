using System.Runtime.InteropServices;

namespace WlshareViewer.Interop;

/// <summary>
/// How many pixels a screen has to the inch of glass — its raw DPI, which
/// Windows works out from the size the monitor's EDID gives. Windows' scale
/// setting is a preference and says nothing about the panel.
/// </summary>
internal static partial class Screen
{
    private const uint MonitorDefaultToNearest = 2;
    private const int MdtRawDpi = 2;

    /// <summary>The pixels per inch of the screen most of `window` is on, or
    /// null for one that does not say how big it is — a remote session's, a
    /// virtual machine's, a projector's.</summary>
    public static double? PixelsPerInch(nint window)
    {
        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        if (monitor == 0 || GetDpiForMonitor(monitor, MdtRawDpi, out var x, out _) < 0 || x == 0)
        {
            return null;
        }
        return x;
    }

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
}
