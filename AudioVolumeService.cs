using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading.Tasks;

namespace VolumeLock;

public sealed record AudioEndpoint(string Id, string Name, bool IsCapture);

public sealed class AudioVolumeService : IDisposable
{
    private static readonly PROPERTYKEY PKEY_Device_FriendlyName = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        14);

    private readonly IMMDeviceEnumerator _enumerator;
    private bool _disposed;

    public AudioVolumeService()
    {
        _enumerator = CreateEnumerator();
    }

    public Task<IReadOnlyList<AudioEndpoint>> GetDevicesAsync(bool capture)
    {
        ThrowIfDisposed();

        var hr = _enumerator.EnumAudioEndpoints(
            capture ? EDataFlow.Capture : EDataFlow.Render,
            AudioInterop.DEVICE_STATE_ACTIVE,
            out var collection);
        Check(hr);

        Check(collection.GetCount(out var count));

        var devices = new List<AudioEndpoint>();
        for (uint index = 0; index < count; index++)
        {
            Check(collection.Item(index, out var device));

            var id = GetEndpointId(device);
            devices.Add(new AudioEndpoint(id, GetFriendlyName(device, id), capture));
        }

        IReadOnlyList<AudioEndpoint> result = devices
            .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return Task.FromResult(result);
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

        return GetEndpointId(device);
    }

    public float GetVolumeLevel(string deviceId)
    {
        ThrowIfDisposed();

        var volume = ActivateVolume(deviceId);
        Check(volume.GetMasterVolumeLevelScalar(out var level));
        return level;
    }

    public void SetVolumeLevel(string deviceId, float level, bool unmute = false)
    {
        ThrowIfDisposed();

        var volume = ActivateVolume(deviceId);
        var context = Guid.Empty;

        if (unmute)
        {
            Check(volume.SetMute(false, ref context));
        }

        Check(volume.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref context));
    }

    public VolumeLockSession CreateLock(
        string deviceId,
        float targetLevel,
        Action<float, float, bool> onExternalChange)
    {
        ThrowIfDisposed();

        return new VolumeLockSession(ActivateVolume(deviceId), targetLevel, onExternalChange);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Source-generated COM wrappers are ComObject instances, which are not
        // IDisposable and expose no FinalReleaseComObject equivalent: their one
        // reference is released by the finalizer. There is nothing to do here.
    }

    private static IMMDeviceEnumerator CreateEnumerator()
    {
        var clsid = AudioInterop.CLSID_MMDeviceEnumerator;
        var iid = typeof(IMMDeviceEnumerator).GUID;

        Check(AudioInterop.CoCreateInstance(ref clsid, IntPtr.Zero, AudioInterop.CLSCTX_ALL, ref iid, out var pointer));

        return AudioInterop.Wrap<IMMDeviceEnumerator>(pointer);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AudioVolumeService));
        }
    }

    private IAudioEndpointVolume ActivateVolume(string deviceId)
    {
        Check(_enumerator.GetDevice(deviceId, out var device));

        var iid = typeof(IAudioEndpointVolume).GUID;
        Check(device.Activate(ref iid, AudioInterop.CLSCTX_ALL, IntPtr.Zero, out var volume));
        return volume;
    }

    private static string GetEndpointId(IMMDevice device)
    {
        Check(device.GetId(out var id));
        return id ?? string.Empty;
    }

    private static string GetFriendlyName(IMMDevice device, string fallback)
    {
        var hr = device.OpenPropertyStore(AudioInterop.STGM_READ, out var store);
        if (hr < 0)
        {
            return fallback;
        }

        var value = new PROPVARIANT();

        try
        {
            var key = PKEY_Device_FriendlyName;
            hr = store.GetValue(ref key, out value);
            if (hr < 0 || value.vt != AudioInterop.VT_LPWSTR || value.Value1 == IntPtr.Zero)
            {
                return fallback;
            }

            return Marshal.PtrToStringUni(value.Value1) ?? fallback;
        }
        finally
        {
            if (value.vt != 0)
            {
                AudioInterop.PropVariantClear(ref value);
            }
        }
    }

    internal static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);
}

public sealed class VolumeLockSession : IDisposable
{
    private readonly IAudioEndpointVolume _volume;
    private readonly VolumeChangeCallback _callback;
    private readonly Action<float, float, bool> _onExternalChange;
    private readonly Guid _selfContext = Guid.NewGuid();
    private readonly object _sync = new();

    private float _targetLevel;
    private bool _disposed;

    internal VolumeLockSession(
        IAudioEndpointVolume volume,
        float targetLevel,
        Action<float, float, bool> onExternalChange)
    {
        _volume = volume;
        _targetLevel = Math.Clamp(targetLevel, 0f, 1f);
        _onExternalChange = onExternalChange;
        _callback = new VolumeChangeCallback(OnVolumeNotification);

        try
        {
            AudioVolumeService.Check(_volume.RegisterControlChangeNotify(_callback));
            ApplyTargetNoLock();
        }
        catch
        {
            UnregisterQuietly();
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

        UnregisterQuietly();

        // See AudioVolumeService.Dispose: the COM wrapper is released by its finalizer.
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
        AudioVolumeService.Check(_volume.SetMasterVolumeLevelScalar(_targetLevel, ref context));
    }

    private void UnregisterQuietly()
    {
        try
        {
            _volume.UnregisterControlChangeNotify(_callback);
        }
        catch
        {
            // Ignore unregister failures while disposing or unwinding a failed registration.
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(VolumeLockSession));
        }
    }
}

[GeneratedComClass]
public sealed partial class VolumeChangeCallback : IAudioEndpointVolumeCallback
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