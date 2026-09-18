using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace WlshareViewer.Interop;

// The parts of WASAPI the desktop's sound is played through, as source-generated
// COM. Every method of each interface is declared, used or not, in the order
// the SDK's headers give them: a COM call is a slot in a vtable, and a method
// left out shifts every one after it onto the wrong function. Each returns its
// HRESULT, and the caller decides what a failure means.
//
// An interface a call hands back comes out as a bare pointer and is wrapped by
// the caller (Wasapi.Wrap), so it can be released the moment it is done with
// rather than whenever a finalizer gets round to it.

[GeneratedComInterface]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int flow, uint stateMask, out nint devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out nint endpoint);
    [PreserveSig] int GetDevice(nint id, out nint device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[GeneratedComInterface]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal unsafe partial interface IMMDevice
{
    [PreserveSig] int Activate(Guid* iid, uint context, nint activationParams, out nint instance);
    [PreserveSig] int OpenPropertyStore(uint access, out nint properties);
    [PreserveSig] int GetId(out nint id);
    [PreserveSig] int GetState(out uint state);
}

[GeneratedComInterface]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
internal partial interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged(nint id, uint state);
    [PreserveSig] int OnDeviceAdded(nint id);
    [PreserveSig] int OnDeviceRemoved(nint id);
    [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, nint id);
    [PreserveSig] int OnPropertyValueChanged(nint id, PropertyKey key);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid Format;
    public uint Id;
}

[GeneratedComInterface]
[Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
internal unsafe partial interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, WaveFormatExtensible* format, Guid* session);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint frames);
    [PreserveSig] int IsFormatSupported(int shareMode, WaveFormatExtensible* format, nint* closest);
    [PreserveSig] int GetMixFormat(nint* format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(nint handle);
    [PreserveSig] int GetService(Guid* iid, out nint service);
}

[GeneratedComInterface]
[Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
internal unsafe partial interface IAudioRenderClient
{
    [PreserveSig] int GetBuffer(uint frames, byte** data);
    [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
}

/// <summary>WAVEFORMATEXTENSIBLE, which mmreg.h packs to the byte.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct WaveFormatExtensible
{
    public ushort FormatTag;
    public ushort Channels;
    public uint SamplesPerSec;
    public uint AvgBytesPerSec;
    public ushort BlockAlign;
    public ushort BitsPerSample;
    public ushort Size;
    public ushort ValidBitsPerSample;
    public uint ChannelMask;
    public Guid SubFormat;
}

internal static unsafe partial class Wasapi
{
    public const int FlowRender = 0;
    public const int RoleConsole = 0;
    public const int ShareModeShared = 0;
    public const uint StreamFlagsEventCallback = 0x0004_0000;
    public const uint StreamFlagsSrcDefaultQuality = 0x0800_0000;
    public const uint StreamFlagsAutoConvertPcm = 0x8000_0000;
    private const uint ClsctxAll = 0x17;

    private static readonly Guid MMDeviceEnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>
    /// 32-bit float stereo at 48 kHz, front left and front right: what the core
    /// hands out, so the render thread interleaves and converts nothing. The
    /// audio engine takes it to the device's own mix format.
    /// </summary>
    public static WaveFormatExtensible Float48kStereo => new()
    {
        FormatTag = 0xFFFE,
        Channels = 2,
        SamplesPerSec = 48_000,
        AvgBytesPerSec = 48_000 * 8,
        BlockAlign = 8,
        BitsPerSample = 32,
        Size = 22,
        ValidBitsPerSample = 32,
        ChannelMask = 0x3,
        SubFormat = new Guid("00000003-0000-0010-8000-00AA00389B71"),
    };

    public static IMMDeviceEnumerator DeviceEnumerator()
    {
        var clsid = MMDeviceEnumeratorClass;
        var iid = typeof(IMMDeviceEnumerator).GUID;
        Check(CoCreateInstance(&clsid, 0, ClsctxAll, &iid, out var instance));
        return Wrap<IMMDeviceEnumerator>(instance);
    }

    public static IAudioClient Activate(IMMDevice device)
    {
        var iid = typeof(IAudioClient).GUID;
        Check(device.Activate(&iid, ClsctxAll, 0, out var instance));
        return Wrap<IAudioClient>(instance);
    }

    public static IAudioRenderClient RenderClient(IAudioClient client)
    {
        var iid = typeof(IAudioRenderClient).GUID;
        Check(client.GetService(&iid, out var instance));
        return Wrap<IAudioRenderClient>(instance);
    }

    /// <summary>A pointer a call handed back, as the interface it is. The
    /// wrapper holds the one reference from here on.</summary>
    public static T Wrap<T>(nint instance) where T : class
    {
        try
        {
            return (T)Wrappers.GetOrCreateObjectForComInstance(instance, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(instance);
        }
    }

    /// <summary>Let go of a wrapper's COM object now rather than at its
    /// finalizer.</summary>
    public static void Release(object? wrapper) => (wrapper as ComObject)?.FinalRelease();

    public static void Check(int hresult)
    {
        if (hresult < 0)
        {
            Marshal.ThrowExceptionForHR(hresult);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(Guid* clsid, nint outer, uint context, Guid* iid, out nint instance);

    // Balanced on the thread that called it. The render thread joins the
    // multithreaded apartment, which is what WASAPI's objects live in.
    [LibraryImport("ole32.dll")]
    public static partial int CoInitializeEx(nint reserved, uint model);

    [LibraryImport("ole32.dll")]
    public static partial void CoUninitialize();

    // MMCSS: the render thread is scheduled as audio, ahead of the rest.
    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint AvSetMmThreadCharacteristics(string task, ref uint index);

    [LibraryImport("avrt.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AvRevertMmThreadCharacteristics(nint handle);
}
