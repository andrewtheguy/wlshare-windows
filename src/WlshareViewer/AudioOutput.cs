using System.Runtime.InteropServices.Marshalling;
using WlshareViewer.Interop;

namespace WlshareViewer;

/// <summary>
/// The desktop's sound, on the Windows default output.
///
/// The core decodes the server's FLAC frames into a buffer of its own; this is
/// the other end of it, a shared-mode WASAPI stream fed from that buffer on the
/// audio device's clock. The stream is 48 kHz float stereo, what the core hands
/// out, and the audio engine converts it to whatever the device mixes at.
///
/// Everything happens on a thread of its own, which the device wakes each time
/// it has room: it reads that much from the core and hands it over, and never
/// touches the window. A new default output — headphones in, another device
/// chosen — or the device going away has it open the default again, and a
/// machine with no output at all has it wait for one.
///
/// Made once the session says the sound is on, and disposed before the
/// <see cref="Client"/> is: the thread reads from the client until it has
/// stopped.
/// </summary>
internal sealed unsafe partial class AudioOutput : IDisposable
{
    /// <summary>How much the device buffers ahead of what it plays, in 100 ns
    /// units: 20 ms, on top of the core's own floor.</summary>
    private const long BufferDuration = 200_000;
    /// <summary>A device that asked for more and has not in this long is one
    /// that has stopped, and is opened again.</summary>
    private const int Silence = 2000;
    /// <summary>How long a device that would not open is left before it is
    /// tried again, unless the default output changes first.</summary>
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(3);

    private readonly Client _client;
    private readonly Thread _thread;
    private readonly ManualResetEvent _stop = new(false);
    /// <summary>Set when the default output changes, from whatever thread the
    /// device enumerator calls back on.</summary>
    private readonly AutoResetEvent _rerouted = new(false);

    public AudioOutput(Client client)
    {
        _client = client;
        _thread = new Thread(Run) { Name = "wlshare-audio", IsBackground = true, Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    /// <summary>Stop the stream and wait for the thread, which is at most one
    /// device period. Nothing reads from the client after this.</summary>
    public void Dispose()
    {
        _stop.Set();
        _thread.Join();
        _stop.Dispose();
        _rerouted.Dispose();
    }

    private void Run()
    {
        // MTA: the one WASAPI's objects live in, and the one a thread with no
        // window of its own belongs in.
        var com = Wasapi.CoInitializeEx(0, 0);
        uint task = 0;
        var mmcss = Wasapi.AvSetMmThreadCharacteristics("Pro Audio", ref task);
        IMMDeviceEnumerator? devices = null;
        var router = new Router(_rerouted);
        try
        {
            devices = Wasapi.DeviceEnumerator();
            // Not being told of a new default only means the stream stays on the
            // old one until it goes away.
            _ = devices.RegisterEndpointNotificationCallback(router);
            while (!_stop.WaitOne(0))
            {
                try
                {
                    Play(devices);
                }
                catch (Exception)
                {
                    // No output, one that would not take the stream, or one that
                    // went away mid-stream. Tried again in a while, or at once
                    // when the default output changes.
                    WaitHandle.WaitAny([_stop, _rerouted], Retry);
                }
            }
        }
        catch (Exception)
        {
            // No device enumerator is no audio on this machine at all: the
            // session goes on silent.
        }
        finally
        {
            if (devices is not null)
            {
                _ = devices.UnregisterEndpointNotificationCallback(router);
                Wasapi.Release(devices);
            }
            if (mmcss != 0)
            {
                Wasapi.AvRevertMmThreadCharacteristics(mmcss);
            }
            if (com >= 0)
            {
                Wasapi.CoUninitialize();
            }
        }
    }

    /// <summary>Open the default output and feed it until the window stops the
    /// sound or the default changes. A failure throws, and the caller waits
    /// before trying again.</summary>
    private void Play(IMMDeviceEnumerator devices)
    {
        IMMDevice? device = null;
        IAudioClient? audio = null;
        IAudioRenderClient? render = null;
        using var ready = new AutoResetEvent(false);
        var started = false;
        try
        {
            Wasapi.Check(devices.GetDefaultAudioEndpoint(Wasapi.FlowRender, Wasapi.RoleConsole, out var endpoint));
            device = Wasapi.Wrap<IMMDevice>(endpoint);
            audio = Wasapi.Activate(device);
            var format = Wasapi.Float48kStereo;
            Wasapi.Check(audio.Initialize(
                Wasapi.ShareModeShared,
                Wasapi.StreamFlagsEventCallback | Wasapi.StreamFlagsAutoConvertPcm | Wasapi.StreamFlagsSrcDefaultQuality,
                BufferDuration,
                0,
                &format,
                null));
            Wasapi.Check(audio.GetBufferSize(out var capacity));
            Wasapi.Check(audio.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()));
            render = Wasapi.RenderClient(audio);
            var left = new float[capacity];
            var right = new float[capacity];

            // Filled before it starts, so the first period is not a glitch.
            Fill(audio, render, capacity, left, right);
            Wasapi.Check(audio.Start());
            started = true;
            while (true)
            {
                switch (WaitHandle.WaitAny([_stop, _rerouted, ready], Silence))
                {
                    case 0:
                    case 1:
                        return;
                    case 2:
                        Fill(audio, render, capacity, left, right);
                        break;
                    default:
                        throw new TimeoutException("the audio device stopped asking for sound");
                }
            }
        }
        finally
        {
            if (started)
            {
                _ = audio!.Stop();
            }
            Wasapi.Release(render);
            Wasapi.Release(audio);
            Wasapi.Release(device);
        }
    }

    /// <summary>Hand the device as much as it has room for, from the core —
    /// silence where the core has none yet.</summary>
    private void Fill(IAudioClient audio, IAudioRenderClient render, uint capacity, float[] left, float[] right)
    {
        Wasapi.Check(audio.GetCurrentPadding(out var padding));
        var room = capacity - padding;
        if (room == 0)
        {
            return;
        }
        byte* data;
        Wasapi.Check(render.GetBuffer(room, &data));
        var frames = (int)room;
        var output = (float*)data;
        fixed (float* l = left, r = right)
        {
            _client.ReadAudio(l, r, frames);
            for (var i = 0; i < frames; i++)
            {
                output[2 * i] = l[i];
                output[2 * i + 1] = r[i];
            }
        }
        Wasapi.Check(render.ReleaseBuffer(room, 0));
    }

    /// <summary>
    /// Told by the device enumerator when the default output changes, on a
    /// thread of the enumerator's. It sets an event and does nothing else: the
    /// callbacks may not block, and may not touch the enumerator.
    /// </summary>
    [GeneratedComClass]
    private sealed partial class Router(AutoResetEvent rerouted) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(nint id, uint state) => 0;

        public int OnDeviceAdded(nint id) => 0;

        public int OnDeviceRemoved(nint id) => 0;

        public int OnDefaultDeviceChanged(int flow, int role, nint id)
        {
            if (flow == Wasapi.FlowRender && role == Wasapi.RoleConsole)
            {
                try
                {
                    rerouted.Set();
                }
                catch (ObjectDisposedException)
                {
                    // A change that arrived as the sound was being put away.
                }
            }
            return 0;
        }

        public int OnPropertyValueChanged(nint id, PropertyKey key) => 0;
    }
}
