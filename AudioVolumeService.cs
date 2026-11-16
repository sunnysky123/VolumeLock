using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace VolumeLock;

public sealed record AudioEndpoint(string Id, string Name, bool IsCapture);

public sealed class AudioVolumeService : IDisposable
{
    private const uint CLSCTX_ALL = 0x17;
    private const uint DEVICE_STATE_ACTIVE = 0x00000001;
    private const uint STGM_READ = 0;
    private const ushort VT_LPWSTR = 31;

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly PROPERTYKEY PKEY_Device_FriendlyName = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        14);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT value);

    private readonly IMMDeviceEnumerator _enumerator;
    private bool _disposed;

    public AudioVolumeService()
    {
        var enumeratorType = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)
            ?? throw new InvalidOperationException("无法创建 Windows 音频设备枚举器。");

        _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;
    }

    public Task<IReadOnlyList<AudioEndpoint>> GetDevicesAsync(bool capture)
    {
        ThrowIfDisposed();

        var hr = _enumerator.EnumAudioEndpoints(
            capture ? EDataFlow.Capture : EDataFlow.Render,
            DEVICE_STATE_ACTIVE,
            out var collection);
        Check(hr);

        try
        {
            Check(collection.GetCount(out var count));

            var devices = new List<AudioEndpoint>();
            for (uint index = 0; index < count; index++)
            {
                Check(collection.Item(index, out var device));

                try
                {
                    var id = GetEndpointId(device);
                    devices.Add(new AudioEndpoint(id, GetFriendlyName(device, id), capture));
                }
                finally
                {
                    Marshal.FinalReleaseComObject(device);
                }
            }

            IReadOnlyList<AudioEndpoint> result = devices
                .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            return Task.FromResult(result);
        }
        finally
        {
            Marshal.FinalReleaseComObject(collection);
        }
    }

    public string? GetDefaultDeviceId(bool capture)
    {
        ThrowIfDisposed();

        var hr = _enumerator.GetDefaultAudioEndpoint(
            capture ? EDataFlow.Capture : EDataFlow.Render,
            ERole.Console,
            out var device);

        if (hr < 0)
        {
            return null;
        }

        try
        {
            return GetEndpointId(device);
        }
        finally
        {
            Marshal.FinalReleaseComObject(device);
        }
    }

    public float GetVolumeLevel(string deviceId)
    {
        ThrowIfDisposed();

        using var handle = ActivateVolume(deviceId);
        Check(handle.Volume.GetMasterVolumeLevelScalar(out var level));
        return level;
    }

    public void SetVolumeLevel(string deviceId, float level, bool unmute = false)
    {
        ThrowIfDisposed();

        using var handle = ActivateVolume(deviceId);
        var context = Guid.Empty;

        if (unmute)
        {
            Check(handle.Volume.SetMute(false, ref context));
        }

        Check(handle.Volume.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref context));
    }

    public VolumeLockSession CreateLock(
        string deviceId,
        float targetLevel,
        Action<float, float, bool> onExternalChange)
    {
        ThrowIfDisposed();

        var handle = ActivateVolume(deviceId);

        try
        {
            return new VolumeLockSession(handle, targetLevel, onExternalChange);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Marshal.FinalReleaseComObject(_enumerator);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AudioVolumeService));
        }
    }

    private IMMDevice GetDevice(string deviceId)
    {
        var hr = _enumerator.GetDevice(deviceId, out var device);
        Check(hr);
        return device;
    }

    private EndpointVolumeHandle ActivateVolume(string deviceId)
    {
        var device = GetDevice(deviceId);

        try
        {
            var iid = typeof(IAudioEndpointVolume).GUID;
            var hr = device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var pointer);
            Check(hr);

            try
            {
                var volume = (IAudioEndpointVolume)Marshal.GetUniqueObjectForIUnknown(pointer);
                return new EndpointVolumeHandle(volume, pointer);
            }
            catch
            {
                Marshal.Release(pointer);
                throw;
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(device);
        }
    }

    private static string GetEndpointId(IMMDevice device)
    {
        var hr = device.GetId(out var pointer);
        Check(hr);

        try
        {
            return Marshal.PtrToStringUni(pointer) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    private static string GetFriendlyName(IMMDevice device, string fallback)
    {
        var hr = device.OpenPropertyStore(STGM_READ, out var storePointer);
        if (hr < 0)
        {
            return fallback;
        }

        IPropertyStore? store = null;
        var value = new PROPVARIANT();

        try
        {
            store = (IPropertyStore)Marshal.GetUniqueObjectForIUnknown(storePointer);

            var key = PKEY_Device_FriendlyName;
            hr = store.GetValue(ref key, out value);
            if (hr < 0 || value.vt != VT_LPWSTR || value.Value1 == IntPtr.Zero)
            {
                return fallback;
            }

            return Marshal.PtrToStringUni(value.Value1) ?? fallback;
        }
        finally
        {
            if (value.vt != 0)
            {
                PropVariantClear(ref value);
            }

            if (store is not null)
            {
                Marshal.FinalReleaseComObject(store);
            }

            Marshal.Release(storePointer);
        }
    }

    internal static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);
}

public sealed class VolumeLockSession : IDisposable
{
    private readonly EndpointVolumeHandle _handle;
    private readonly VolumeChangeCallback _callback;
    private readonly Action<float, float, bool> _onExternalChange;
    private readonly Guid _selfContext = Guid.NewGuid();
    private readonly object _sync = new();

    private float _targetLevel;
    private bool _disposed;

    internal VolumeLockSession(
        EndpointVolumeHandle handle,
        float targetLevel,
        Action<float, float, bool> onExternalChange)
    {
        _handle = handle;
        _targetLevel = Math.Clamp(targetLevel, 0f, 1f);
        _onExternalChange = onExternalChange;
        _callback = new VolumeChangeCallback(OnVolumeNotification);

        try
        {
            AudioVolumeService.Check(handle.Volume.RegisterControlChangeNotify(_callback));
            ApplyTargetNoLock();
        }
        catch
        {
            try
            {
                handle.Volume.UnregisterControlChangeNotify(_callback);
            }
            catch
            {
                // Ignore cleanup failures from the failed registration path.
            }

            throw;
        }
    }

    public void UpdateTarget(float level)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            _targetLevel = Math.Clamp(level, 0f, 1f);
            ApplyTargetNoLock();
        }
    }

    public void Refresh()
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                ApplyTargetNoLock();
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        try
        {
            _handle.Volume.UnregisterControlChangeNotify(_callback);
        }
        catch
        {
            // Ignore unregister failures while disposing.
        }

        _handle.Dispose();
    }

    private void OnVolumeNotification(Guid context, float observedLevel, bool muted)
    {
        float targetLevel;

        lock (_sync)
        {
            if (_disposed || context == _selfContext)
            {
                return;
            }

            targetLevel = _targetLevel;
            ApplyTargetNoLock();
        }

        _onExternalChange(targetLevel, observedLevel, muted);
    }

    private void ApplyTargetNoLock()
    {
        var context = _selfContext;
        AudioVolumeService.Check(_handle.Volume.SetMasterVolumeLevelScalar(_targetLevel, ref context));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(VolumeLockSession));
        }
    }
}

internal sealed class EndpointVolumeHandle : IDisposable
{
    private IntPtr _pointer;
    private bool _disposed;

    public EndpointVolumeHandle(IAudioEndpointVolume volume, IntPtr pointer)
    {
        Volume = volume;
        _pointer = pointer;
    }

    public IAudioEndpointVolume Volume { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_pointer != IntPtr.Zero)
        {
            Marshal.Release(_pointer);
            _pointer = IntPtr.Zero;
        }

        Marshal.FinalReleaseComObject(Volume);
    }
}

[ComVisible(true)]
public sealed class VolumeChangeCallback : IAudioEndpointVolumeCallback
{
    private readonly Action<Guid, float, bool> _onChanged;

    public VolumeChangeCallback(Action<Guid, float, bool> onChanged)
    {
        _onChanged = onChanged;
    }

    public int OnNotify(IntPtr notificationData)
    {
        try
        {
            if (notificationData == IntPtr.Zero)
            {
                return 0;
            }

            var data = Marshal.PtrToStructure<AUDIO_VOLUME_NOTIFICATION_DATA>(notificationData);
            _onChanged(data.EventContext, data.MasterVolume, data.Muted);
        }
        catch
        {
            // Never let exceptions cross the COM callback boundary.
        }

        return 0;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct AUDIO_VOLUME_NOTIFICATION_DATA
{
    public Guid EventContext;

    [MarshalAs(UnmanagedType.Bool)]
    public bool Muted;

    public float MasterVolume;

    public uint ChannelCount;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
    public float[] ChannelVolumes;
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

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig]
    int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection collection);

    [PreserveSig]
    int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);

    [PreserveSig]
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig]
    int Activate(ref Guid iid, uint classContext, IntPtr activationParams, out IntPtr interfacePointer);

    [PreserveSig]
    int OpenPropertyStore(uint access, out IntPtr propertyStore);

    [PreserveSig]
    int GetId(out IntPtr idPointer);

    [PreserveSig]
    int GetState(out uint state);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int Item(uint index, out IMMDevice device);
}

[ComImport]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint count);

    [PreserveSig]
    int GetAt(uint index, out PROPERTYKEY key);

    [PreserveSig]
    int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);

    [PreserveSig]
    int SetValue(ref PROPERTYKEY key, IntPtr value);

    [PreserveSig]
    int Commit();
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

[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
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

[ComVisible(true)]
[ComImport]
[Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IAudioEndpointVolumeCallback
{
    [PreserveSig]
    int OnNotify(IntPtr notificationData);
}
