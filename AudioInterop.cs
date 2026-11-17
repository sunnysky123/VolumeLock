using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace VolumeLock;

/// <summary>
/// COM declarations for the Windows Core Audio endpoint APIs.
/// </summary>
/// <remarks>
/// These use source-generated COM interop (<c>[GeneratedComInterface]</c> /
/// <c>[GeneratedComClass]</c>) rather than legacy <c>[ComImport]</c> built-in interop.
/// Built-in interop generates its marshalling stubs at run time, and ILLink trimming
/// breaks that generation: the stubs end up dispatching through the wrong vtable slot,
/// so calls return <c>E_POINTER</c>/<c>E_INVALIDARG</c> or fault with an access violation
/// (0xC0000005). That made every volume read fail with "扬声器读取失败" in trimmed builds.
/// Source-generated stubs are compiled ahead of time and survive trimming intact.
/// </remarks>
internal static class AudioInterop
{
    public const uint CLSCTX_ALL = 0x17;
    public const uint DEVICE_STATE_ACTIVE = 0x00000001;
    public const uint STGM_READ = 0;
    public const ushort VT_LPWSTR = 31;

    public static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

    [DllImport("ole32.dll")]
    public static extern int CoCreateInstance(
        ref Guid clsid,
        IntPtr outer,
        uint classContext,
        ref Guid iid,
        out IntPtr instance);

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PROPVARIANT value);

    /// <summary>
    /// Wraps a COM interface pointer in its source-generated managed wrapper.
    /// </summary>
    /// <remarks>
    /// <see cref="ComInterfaceMarshaller{T}.ConvertToManaged"/> takes its own reference, so
    /// the reference owned by the caller is released here. Ownership therefore transfers to
    /// the returned wrapper, whose reference is released when the wrapper is collected.
    /// </remarks>
    public static unsafe T Wrap<T>(IntPtr pointer) where T : class
    {
        try
        {
            return ComInterfaceMarshaller<T>.ConvertToManaged((void*)pointer)
                ?? throw new InvalidOperationException($"无法创建 {typeof(T).Name} 的 COM 包装。");
        }
        finally
        {
            if (pointer != IntPtr.Zero)
            {
                Marshal.Release(pointer);
            }
        }
    }
}

[GeneratedComInterface]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection collection);

    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
}

[GeneratedComInterface]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal partial interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, uint classContext, IntPtr activationParams, out IAudioEndpointVolume volume);

    [PreserveSig]
    int OpenPropertyStore(uint access, out IPropertyStore propertyStore);

    [PreserveSig]
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

    [PreserveSig]
    int GetState(out uint state);
}

[GeneratedComInterface]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
internal partial interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int Item(uint index, out IMMDevice device);
}

[GeneratedComInterface]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
internal partial interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int GetAt(uint index, out PROPERTYKEY key);

    [PreserveSig]
    int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);

    [PreserveSig]
    int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);

    [PreserveSig]
    int Commit();
}

[GeneratedComInterface]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
internal partial interface IAudioEndpointVolume
{
    [PreserveSig]
    int RegisterControlChangeNotify(IAudioEndpointVolumeCallback callback);

    [PreserveSig]
    int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback callback);

    [PreserveSig]
    int GetChannelCount(out uint count);

    [PreserveSig]
    int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);

    [PreserveSig]
    int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

    [PreserveSig]
    int GetMasterVolumeLevel(out float levelDb);

    [PreserveSig]
    int GetMasterVolumeLevelScalar(out float level);

    [PreserveSig]
    int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);

    [PreserveSig]
    int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);

    [PreserveSig]
    int GetChannelVolumeLevel(uint channel, out float levelDb);

    [PreserveSig]
    int GetChannelVolumeLevelScalar(uint channel, out float level);

    [PreserveSig]
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);

    [PreserveSig]
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[GeneratedComInterface]
[Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
internal partial interface IAudioEndpointVolumeCallback
{
    [PreserveSig]
    int OnNotify(IntPtr notificationData);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROPERTYKEY
{
    public Guid FormatId;
    public uint PropertyId;

    public PROPERTYKEY(Guid formatId, uint propertyId)
    {
        FormatId = formatId;
        PropertyId = propertyId;
    }
}

[StructLayout(LayoutKind.Sequential, Size = 24)]
internal struct PROPVARIANT
{
    public ushort vt;
    public ushort wReserved1;
    public ushort wReserved2;
    public ushort wReserved3;
    public IntPtr Value1;
    public IntPtr Value2;
}

internal enum EDataFlow
{
    Render = 0,
    Capture = 1,
    All = 2
}

internal enum ERole
{
    Console = 0,
    Multimedia = 1,
    Communications = 2
}
