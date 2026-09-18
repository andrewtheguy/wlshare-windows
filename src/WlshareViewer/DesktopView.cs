using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using WlshareViewer.Interop;

namespace WlshareViewer;

/// <summary>
/// The desktop on screen, and every event that goes back to it.
///
/// It draws only when it is told to: a remote desktop that has not changed has
/// nothing to redraw, and the window calls <see cref="Refresh"/> when the core
/// says it has. Its size in device pixels, with the scale chosen for it, is what
/// the desktop is asked to be, so at rest the picture is one device pixel per
/// framebuffer pixel and never resampled.
/// </summary>
internal sealed unsafe partial class DesktopView : UserControl
{
    private readonly CanvasControl _canvas = new();
    private Client? _client;

    /// <summary>The desktop as a bitmap, and which framebuffer it was made for.
    /// A generation that has moved is a desktop of a new size.</summary>
    private CanvasBitmap? _desktop;
    private ulong _generation = ulong.MaxValue;
    /// <summary>Where a damaged region is packed on its way into the bitmap,
    /// kept between frames so a stream of small updates allocates nothing.</summary>
    private byte[] _scratch = [];

    /// <summary>The RFB button mask as it stands, so that a wheel notch does not
    /// let go of the buttons that are down.</summary>
    private byte _buttons;
    /// <summary>Where the pointer was last sent, so that letting go of its
    /// buttons does not also move it to the corner of the desktop.</summary>
    private (ushort X, ushort Y) _last;
    /// <summary>The keysym each held key went down with. A key must be let go
    /// with the one it was pressed with: Shift released first would otherwise
    /// turn an `A` going up into an `a` that was never down.</summary>
    private readonly Dictionary<uint, uint> _held = [];

    private ulong _cursorGeneration;
    /// <summary>The HCURSOR behind the pointer shape in use, destroyed once a
    /// new one has replaced it.</summary>
    private nint _cursorHandle;

    private Client.Surface _surface;
    private double _desktopScale = 1;

    public DesktopView()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = false;
        // A background, so the whole view takes the pointer: an element with
        // none is not hit where nothing is drawn.
        Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Black);
        Content = _canvas;

        _canvas.Draw += OnDraw;
        _canvas.CreateResources += (_, _) =>
        {
            // A new device — the first, or one after the GPU was reset — has
            // none of the old one's bitmaps.
            _desktop = null;
            _generation = ulong.MaxValue;
        };

        SizeChanged += (_, _) => PostSurface();
        Loaded += (_, _) =>
        {
            XamlRoot.Changed += (_, _) => PostSurface();
            PostSurface();
        };
        Unloaded += (_, _) => ReleaseHeld();

        PointerPressed += OnPointerPressed;
        PointerReleased += OnPointer;
        PointerMoved += OnPointer;
        PointerCaptureLost += (_, _) => LetGoOfButtons();
        PointerWheelChanged += OnWheel;
        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += OnKeyUp;
        LostFocus += (_, _) => ReleaseHeld();
    }

    /// <summary>The scale the desktop is drawn at — 1 or 2. Changing it asks
    /// the desktop to draw itself again at the new one.</summary>
    public double DesktopScale
    {
        get => _desktopScale;
        set
        {
            _desktopScale = value;
            PostSurface();
        }
    }

    /// <summary>What the desktop is asked to be: this view's size in device
    /// pixels, at the scale chosen for it.</summary>
    public Client.Surface Surface
    {
        get
        {
            var density = XamlRoot?.RasterizationScale ?? 1;
            static ushort Pixels(double value) => (ushort)Math.Clamp(Math.Round(value), 0, ushort.MaxValue);
            return new Client.Surface(Pixels(ActualWidth * density), Pixels(ActualHeight * density), _desktopScale);
        }
    }

    public void Attach(Client client)
    {
        _client = client;
        _surface = Surface;
        _canvas.Invalidate();
    }

    /// <summary>The session has something new: a frame, a pointer shape.</summary>
    public void Refresh()
    {
        TakeCursor();
        _canvas.Invalidate();
    }

    /// <summary>Let go of everything and draw nothing more: the session is
    /// ending, and the client is about to go.</summary>
    public void Detach()
    {
        ReleaseHeld();
        _client = null;
        _desktop?.Dispose();
        _desktop = null;
        ProtectedCursor = null;
        if (_cursorHandle != 0)
        {
            Win32.DestroyCursor(_cursorHandle);
            _cursorHandle = 0;
        }
    }

    /// <summary>Tell the session what this view is now. Posting the same one
    /// twice is free — the session drops it.</summary>
    private void PostSurface()
    {
        var now = Surface;
        if (now == _surface)
        {
            return;
        }
        _surface = now;
        _client?.Resize(now);
    }

    // ── Drawing ─────────────────────────────────────────────────────────────

    private void OnDraw(CanvasControl canvas, CanvasDrawEventArgs args)
    {
        var session = args.DrawingSession;
        session.Clear(Colors.Black);
        if (_client is null)
        {
            return;
        }
        _client.WithFrame(frame => Upload(canvas, frame));
        if (_desktop is null)
        {
            return;
        }

        // In the canvas's own units, which are device pixels divided by the
        // screen's density.
        var density = XamlRoot?.RasterizationScale ?? 1;
        var pixels = _desktop.SizeInPixels;
        var width = pixels.Width / density;
        var height = pixels.Height / density;
        var source = new Rect(0, 0, pixels.Width, pixels.Height);
        if (pixels.Width == _surface.Width && pixels.Height == _surface.Height)
        {
            // The steady state: a pixel for a pixel, copied rather than filtered.
            session.DrawImage(_desktop, new Rect(0, 0, width, height), source, 1, CanvasImageInterpolation.NearestNeighbor);
            return;
        }
        // The desktop has not caught up with a resize or a change of scale yet:
        // the largest rectangle of its shape that fits, until it has.
        var fit = Math.Min(canvas.ActualWidth / width, canvas.ActualHeight / height);
        var drawn = new Rect((canvas.ActualWidth - width * fit) / 2, (canvas.ActualHeight - height * fit) / 2, width * fit, height * fit);
        session.DrawImage(_desktop, drawn, source, 1, CanvasImageInterpolation.Linear);
    }

    /// <summary>Take the framebuffer's damage into the bitmap. Called with the
    /// framebuffer's lock held, so it copies and returns.</summary>
    private void Upload(CanvasControl canvas, WlshareFrame* frame)
    {
        if (frame->Pixels == null || frame->Width == 0 || frame->Height == 0)
        {
            _desktop?.Dispose();
            _desktop = null;
            _generation = ulong.MaxValue;
            return;
        }
        int width = (int)frame->Width, height = (int)frame->Height, stride = (int)frame->Stride;

        if (_desktop is null || _generation != frame->Generation)
        {
            // A bitmap that has just been made holds nothing, whatever the
            // damage says, so the whole framebuffer goes into it.
            var whole = new byte[width * height * 4];
            for (var row = 0; row < height; row++)
            {
                new ReadOnlySpan<byte>(frame->Pixels + row * stride, width * 4).CopyTo(whole.AsSpan(row * width * 4));
            }
            _desktop?.Dispose();
            // 96 DPI: the bitmap's units are its pixels, and OnDraw says where
            // they go on the screen.
            _desktop = CanvasBitmap.CreateFromBytes(canvas, whole, width, height, DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Ignore);
            _generation = frame->Generation;
            return;
        }
        if (!frame->Damaged)
        {
            return;
        }
        int x = (int)frame->DamageX, y = (int)frame->DamageY;
        int w = Math.Min((int)frame->DamageWidth, width - x), h = Math.Min((int)frame->DamageHeight, height - y);
        if (w <= 0 || h <= 0)
        {
            return;
        }
        var length = w * h * 4;
        if (_scratch.Length < length)
        {
            _scratch = new byte[length];
        }
        for (var row = 0; row < h; row++)
        {
            new ReadOnlySpan<byte>(frame->Pixels + (y + row) * stride + x * 4, w * 4).CopyTo(_scratch.AsSpan(row * w * 4));
        }
        _desktop.SetPixelBytes(_scratch.AsBuffer(0, length), x, y, w, h);
    }

    // ── The pointer's shape ─────────────────────────────────────────────────

    /// <summary>wlshare never paints the pointer into a frame, so the one on the
    /// screen is this one or there is none.</summary>
    private void TakeCursor()
    {
        if (_client is null)
        {
            return;
        }
        var made = (nint)0;
        var changed = false;
        _client.WithCursor(cursor =>
        {
            if (cursor->Generation == _cursorGeneration)
            {
                return;
            }
            _cursorGeneration = cursor->Generation;
            changed = true;
            // A shape that is gone is the server saying there is no pointer to
            // draw — an arrow of our own would be a pointer the desktop does not
            // have — so it is a cursor with nothing in it.
            var blank = stackalloc byte[4];
            made = cursor->Present && cursor->Width > 0 && cursor->Height > 0
                ? Win32.MakeCursor(cursor->Rgba, cursor->Width, cursor->Height, cursor->HotspotX, cursor->HotspotY)
                : Win32.MakeCursor(blank, 1, 1, 0, 0);
        });
        if (!changed || made == 0)
        {
            return;
        }
        // The shape is in the desktop's pixels, which are this view's device
        // pixels: Windows draws a cursor made from a bitmap at the bitmap's own
        // size, so it is the size the desktop meant at either scale.
        ProtectedCursor = Win32.CursorFrom(made);
        if (_cursorHandle != 0)
        {
            Win32.DestroyCursor(_cursorHandle);
        }
        _cursorHandle = made;
    }

    // ── Input ───────────────────────────────────────────────────────────────

    /// <summary>Where a pointer is, in the desktop's pixels — this view's device
    /// pixels.</summary>
    private (ushort X, ushort Y) Position(Microsoft.UI.Input.PointerPoint point)
    {
        var density = XamlRoot?.RasterizationScale ?? 1;
        static ushort Pixel(double value) => (ushort)Math.Clamp(Math.Round(value), 0, ushort.MaxValue);
        return (Pixel(point.Position.X * density), Pixel(point.Position.Y * density));
    }

    private void Send(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        var properties = point.Properties;
        _buttons = (byte)((properties.IsLeftButtonPressed ? Native.ButtonLeft : 0)
            | (properties.IsMiddleButtonPressed ? Native.ButtonMiddle : 0)
            | (properties.IsRightButtonPressed ? Native.ButtonRight : 0));
        _last = Position(point);
        _client?.Pointer(_buttons, _last.X, _last.Y);
        e.Handled = true;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        // A click is where the keyboard goes too: the toolbar may have had it.
        Focus(FocusState.Pointer);
        // Captured, so a drag that leaves the window still lets go inside it.
        CapturePointer(e.Pointer);
        Send(e);
    }

    private void OnPointer(object sender, PointerRoutedEventArgs e)
    {
        Send(e);
        if (_buttons == 0 && PointerCaptures is { Count: > 0 })
        {
            ReleasePointerCaptures();
        }
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        _last = Position(point);
        // RFB has no scroll event: a notch is a press and release of a button
        // above the real three, and the core says how many a scroll comes to.
        foreach (var notch in _client?.Wheel(point.Properties.MouseWheelDelta, point.Properties.IsHorizontalMouseWheel) ?? [])
        {
            _client!.Pointer((byte)(_buttons | notch), _last.X, _last.Y);
            _client.Pointer(_buttons, _last.X, _last.Y);
        }
        e.Handled = true;
    }

    private void LetGoOfButtons()
    {
        if (_buttons != 0)
        {
            _buttons = 0;
            _client?.Pointer(0, _last.X, _last.Y);
        }
    }

    /// <summary>A physical key: its scan code and extended bit, and its virtual
    /// key code for a key with no scan code, which is one Windows made up.</summary>
    private static uint KeyId(KeyRoutedEventArgs e) =>
        ((uint)e.Key << 16) | (e.KeyStatus.ScanCode << 1) | (e.KeyStatus.IsExtendedKey ? 1u : 0u);

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Every key is the desktop's, Tab and the arrows included: handled here,
        // none of them moves the focus off the desktop.
        e.Handled = true;
        // The desktop repeats keys itself; forwarding Windows' repeats too
        // would type everything twice as fast as it was asked for.
        if (_client is null || e.KeyStatus.WasKeyDown)
        {
            return;
        }
        var vk = (uint)e.Key;
        var scan = e.KeyStatus.ScanCode;
        var keysym = Client.Keysym((ushort)vk, (ushort)scan, e.KeyStatus.IsExtendedKey, Win32.Character(vk, scan));
        if (keysym == 0)
        {
            return;
        }
        _held[KeyId(e)] = keysym;
        _client.Key(true, keysym);
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        e.Handled = true;
        if (_held.Remove(KeyId(e), out var keysym))
        {
            _client?.Key(false, keysym);
        }
    }

    /// <summary>Let go of everything this view is holding — it is losing the
    /// keyboard, and a modifier left down on the desktop sticks there.</summary>
    private void ReleaseHeld()
    {
        foreach (var keysym in _held.Values)
        {
            _client?.Key(false, keysym);
        }
        _held.Clear();
        LetGoOfButtons();
    }
}
