using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Dispatching;

namespace WlshareViewer.Interop;

/// <summary>
/// The Rust core, as C#.
///
/// A thin wrapper and deliberately nothing more: the session, the decoders and
/// the framebuffer live on the other side of <see cref="Native"/>, and this
/// file exists to turn pointers into values and delegates into context
/// pointers. Every rule of that ABI holds here — a callback runs with a lock
/// held and must not block, and the pointers it is given last only for the
/// length of the call.
/// </summary>
internal sealed unsafe class Client : IDisposable
{
    private nint _handle;
    private readonly Wake _wake;
    private GCHandle _wakeHandle;

    /// <summary>
    /// What the session thread's wake callback is handed. It does no more than
    /// post <c>onChange</c> to the window's dispatcher queue, once however many
    /// wakes arrive before that runs: the session thread is never held up by a
    /// redraw, and a burst of frames is one redraw, not a queue of them.
    /// </summary>
    private sealed class Wake(DispatcherQueue queue, Action onChange)
    {
        private int _posted;
        /// <summary>Set before the session is closed: a redraw that was already
        /// queued lands on nothing rather than on a client that is gone.</summary>
        public volatile bool Closed;

        public void Post()
        {
            if (Interlocked.Exchange(ref _posted, 1) == 1)
            {
                return;
            }
            queue.TryEnqueue(() =>
            {
                // Cleared before the call, so a wake during it posts again.
                Volatile.Write(ref _posted, 0);
                if (!Closed)
                {
                    onChange();
                }
            });
        }
    }

    public Client(Destination destination, Surface surface, DispatcherQueue queue, Action onChange)
    {
        _handle = Native.Connect(destination.Host, destination.Port, destination.Username, destination.Password, surface.Width, surface.Height, surface.Scale);
        _wake = new Wake(queue, onChange);
        _wakeHandle = GCHandle.Alloc(_wake);
        Native.OnFrame(_handle, &OnWake, GCHandle.ToIntPtr(_wakeHandle));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnWake(nint ctx)
    {
        try
        {
            ((Wake)GCHandle.FromIntPtr(ctx).Target!).Post();
        }
        catch
        {
            // An exception may not cross into Rust: it would take the process
            // with it. A missed wake is a frame drawn at the next one.
        }
    }

    public void Dispose()
    {
        if (_handle == 0)
        {
            return;
        }
        _wake.Closed = true;
        // Clears the callback and joins the session's thread, so nothing calls
        // OnWake after this returns and the handle can go.
        Native.Close(_handle);
        _handle = 0;
        _wakeHandle.Free();
    }

    /// <summary>What the window asks the desktop to be: its size in device
    /// pixels, and the scale — 1 or 2 — the desktop is drawn at.</summary>
    public readonly record struct Surface(ushort Width, ushort Height, double Scale);

    public enum State { Connecting, Ready, Closed }

    public readonly record struct Status(State State, uint Width, uint Height, double Scale, string Name, string? Error);

    public Status Read()
    {
        // What a client that is gone reads as: the core writes nothing for it.
        var raw = new WlshareStatus { State = Native.StateClosed };
        Native.Status(_handle, &raw);
        var state = raw.State switch
        {
            Native.StateReady => State.Ready,
            Native.StateClosed => State.Closed,
            _ => State.Connecting,
        };
        var error = String((output, cap) => Native.Error(_handle, output, cap));
        return new Status(state, raw.Width, raw.Height, raw.Scale, String((output, cap) => Native.Name(_handle, output, cap)), error.Length == 0 ? null : error);
    }

    private delegate nuint StringReader(byte* output, nuint cap);

    /// <summary>Read a string out of the core, which writes as much as fits and
    /// says how long it is; anything longer is asked for again with room.</summary>
    private static string String(StringReader read)
    {
        var buffer = new byte[256];
        nuint length;
        fixed (byte* p = buffer)
        {
            length = read(p, (nuint)buffer.Length);
        }
        if (length >= (nuint)buffer.Length)
        {
            buffer = new byte[(int)length + 1];
            fixed (byte* p = buffer)
            {
                length = read(p, (nuint)buffer.Length);
            }
        }
        return Encoding.UTF8.GetString(buffer, 0, (int)Math.Min(length, (nuint)buffer.Length - 1));
    }

    // ── Pixels ──────────────────────────────────────────────────────────────

    public delegate void FrameVisitor(WlshareFrame* frame);

    public delegate void CursorVisitor(WlshareCursor* cursor);

    /// <summary>The framebuffer, for the length of the call, with its damage
    /// taken.</summary>
    public void WithFrame(FrameVisitor visit) => Visit(visit, (client, ctx) => Native.WithFrame(client, &VisitFrame, ctx));

    /// <summary>The pointer's shape, for the length of the call.</summary>
    public void WithCursor(CursorVisitor visit) => Visit(visit, (client, ctx) => Native.WithCursor(client, &VisitCursor, ctx));

    /// <summary>The whole framebuffer must be uploaded again — the window lost
    /// its bitmap, or is making a new one.</summary>
    public void DamageAll() => Native.DamageAll(_handle);

    /// <summary>
    /// Hand the core a visitor as a context pointer, and rethrow here whatever
    /// it threw there: an exception may not unwind through Rust, so the
    /// trampoline catches it and it comes out on this side of the call.
    /// </summary>
    private void Visit(Delegate visit, Action<nint, nint> call)
    {
        var box = new Visiting(visit);
        var handle = GCHandle.Alloc(box);
        try
        {
            call(_handle, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }
        box.Thrown?.Throw();
    }

    private sealed class Visiting(Delegate visit)
    {
        public readonly Delegate Visit = visit;
        public System.Runtime.ExceptionServices.ExceptionDispatchInfo? Thrown;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void VisitFrame(nint ctx, WlshareFrame* frame)
    {
        var box = (Visiting)GCHandle.FromIntPtr(ctx).Target!;
        try
        {
            ((FrameVisitor)box.Visit)(frame);
        }
        catch (Exception e)
        {
            box.Thrown = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void VisitCursor(nint ctx, WlshareCursor* cursor)
    {
        var box = (Visiting)GCHandle.FromIntPtr(ctx).Target!;
        try
        {
            ((CursorVisitor)box.Visit)(cursor);
        }
        catch (Exception e)
        {
            box.Thrown = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e);
        }
    }

    // ── Input ───────────────────────────────────────────────────────────────

    public void Pointer(byte buttons, ushort x, ushort y) => Native.Pointer(_handle, buttons, x, y);

    public void Key(bool down, uint keysym) => Native.Key(_handle, down, keysym);

    public void Resize(Surface surface) => Native.Surface(_handle, surface.Width, surface.Height, surface.Scale);

    /// <summary>The wheel notches a scroll comes to, as button-mask bits to
    /// click.</summary>
    public byte[] Wheel(int delta, bool horizontal)
    {
        var notches = stackalloc byte[16];
        var count = (int)Native.Wheel(_handle, delta, horizontal, notches, 16);
        return new ReadOnlySpan<byte>(notches, count).ToArray();
    }

    /// <summary>The X11 keysym for a key, or 0 for a key not worth sending.</summary>
    public static uint Keysym(ushort vk, ushort scan, bool extended, uint character) => Native.Keysym(vk, scan, extended, character);
}
