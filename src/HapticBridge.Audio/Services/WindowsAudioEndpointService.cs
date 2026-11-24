using System.Runtime.InteropServices;
using System.Diagnostics;
using HapticBridge.Audio.Native;
using HapticBridge.Core.Models;
using HapticBridge.Core.Services;

namespace HapticBridge.Audio.Services;

public class WindowsAudioEndpointService : IAudioEndpointService
{
    private static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    public IReadOnlyList<AudioEndpointModel> GetAllRenderEndpoints()
    {
        var list = new List<AudioEndpointModel>();
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            int hr = enumerator.EnumAudioEndpoints(
                CoreAudioConstants.EDataFlow_Render,
                CoreAudioConstants.DEVICE_STATEMASK_ALL,
                out collection);

            if (hr != 0 || collection == null) return list;

            collection.GetCount(out uint count);
            var defaultId = GetDefaultEndpointId();

            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out var dev) == 0 && dev != null)
                {
                    try
                    {
                        var model = BuildEndpointModel(dev, defaultId);
                        if (model != null) list.Add(model);
                    }
                    finally { Marshal.ReleaseComObject(dev); }
                }
            }
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Core Audio endpoint enumeration failed: {0}", exception);
        }
        finally
        {
            if (collection is not null) Marshal.ReleaseComObject(collection);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }

        return list;
    }

    public IReadOnlyList<AudioEndpointModel> GetPhysicalPlaybackEndpoints()
    {
        return GetAllRenderEndpoints()
            .Where(e => e.IsActive && e.EndpointType == AudioEndpointType.PhysicalPlayback)
            .OrderBy(e => e.FriendlyName)
            .ToList();
    }

    public (float Level, bool IsMuted)? SetPhysicalPlaybackVolume(string endpointId, int volumePercent) =>
        SetPlaybackVolume(endpointId, volumePercent, AudioEndpointType.PhysicalPlayback);

    public async Task MaximizeNewHapticsEndpointAsync(CancellationToken stop)
    {
        var existing = GetActiveDualSenseEndpoints().Select(endpoint => endpoint.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            await Task.Delay(500, stop).ConfigureAwait(false);
            var added = GetActiveDualSenseEndpoints().Where(endpoint => !existing.Contains(endpoint.Id)).ToArray();
            if (added is [var device] &&
                SetPlaybackVolume(device.Id, 100, AudioEndpointType.VirtualDualSense) is { Level: >= 0.999f, IsMuted: false })
                return;
        }
    }

    private IEnumerable<AudioEndpointModel> GetActiveDualSenseEndpoints() =>
        GetAllRenderEndpoints().Where(endpoint => endpoint.IsActive && endpoint.EndpointType == AudioEndpointType.VirtualDualSense);

    private (float Level, bool IsMuted)? SetPlaybackVolume(string endpointId, int volumePercent, AudioEndpointType expectedType)
    {
        if (string.IsNullOrWhiteSpace(endpointId) || volumePercent is < 0 or > 100) return null;

        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDevice(endpointId, out device) < 0 || device is not IMMEndpoint endpoint ||
                endpoint.GetDataFlow(out int flow) < 0 || flow != CoreAudioConstants.EDataFlow_Render ||
                device.GetState(out int state) < 0 || state != CoreAudioConstants.DEVICE_STATE_ACTIVE)
                return null;

            var (name, description) = ReadEndpointNames(device, endpointId);
            if (CategorizeEndpoint(name, description) != expectedType) return null;

            Guid iid = IID_IAudioEndpointVolume;
            if (device.Activate(ref iid, 1, IntPtr.Zero, out var native) < 0 || native is null) return null;
            try
            {
                if (native is not IAudioEndpointVolume volume) return null;
                Guid context = Guid.Empty;
                if (volume.SetMasterVolumeLevelScalar(volumePercent / 100f, ref context) < 0 ||
                    (volumePercent > 0 && volume.SetMute(false, ref context) < 0))
                    return null;

                var readback = ReadVolume(volume);
                return volumePercent > 0 && readback is { IsMuted: true } ? null : readback;
            }
            finally { Marshal.ReleaseComObject(native); }
        }
        catch (COMException exception)
        {
            Trace.TraceWarning("Could not change the Windows playback volume: {0}", exception);
            return null;
        }
        finally
        {
            if (device is not null) Marshal.ReleaseComObject(device);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
    }

    public AudioEndpointModel? GetDefaultPlaybackEndpoint()
    {
        var id = GetDefaultEndpointId();
        if (string.IsNullOrEmpty(id)) return null;

        return GetAllRenderEndpoints().FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    public AudioEndpointModel? GetVirtualDualSenseEndpoint()
    {
        return GetAllRenderEndpoints()
            .FirstOrDefault(e => e.EndpointType == AudioEndpointType.VirtualDualSense && e.IsActive)
            ?? GetAllRenderEndpoints().FirstOrDefault(e => e.EndpointType == AudioEndpointType.VirtualDualSense);
    }

    public AudioEndpointModel? GetVirtualCableEndpoint()
    {
        return GetAllRenderEndpoints()
            .FirstOrDefault(e => e.EndpointType == AudioEndpointType.VirtualCable && e.IsActive)
            ?? GetAllRenderEndpoints().FirstOrDefault(e => e.EndpointType == AudioEndpointType.VirtualCable);
    }

    public AudioEndpointModel? GetVoicemeeterVaioEndpoint()
    {
        return GetAllRenderEndpoints()
            .FirstOrDefault(e => e.EndpointType == AudioEndpointType.VoicemeeterVAIO && e.IsActive)
            ?? GetAllRenderEndpoints().FirstOrDefault(e => e.EndpointType == AudioEndpointType.VoicemeeterVAIO);
    }

    public bool SetDefaultEndpoint(string endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId)) return false;

        try
        {
            var policy = (IPolicyConfig)new PolicyConfigClientComObject();
            int hr1 = policy.SetDefaultEndpoint(endpointId, CoreAudioConstants.ERole_Console);
            int hr2 = policy.SetDefaultEndpoint(endpointId, CoreAudioConstants.ERole_Multimedia);
            int hr3 = policy.SetDefaultEndpoint(endpointId, CoreAudioConstants.ERole_Communications);

            return hr1 == 0 && hr2 == 0;
        }
        catch
        {
            return false;
        }
    }

    private string GetDefaultEndpointId()
    {
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            if (enumerator.GetDefaultAudioEndpoint(
                CoreAudioConstants.EDataFlow_Render,
                CoreAudioConstants.ERole_Multimedia,
                out var defaultDev) == 0 && defaultDev != null)
            {
                try
                {
                    defaultDev.GetId(out string id);
                    return id ?? string.Empty;
                }
                finally { Marshal.ReleaseComObject(defaultDev); }
            }
        }
        catch
        {
            // Ignore
        }
        finally
        {
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
        return string.Empty;
    }

    private AudioEndpointModel? BuildEndpointModel(IMMDevice dev, string defaultId)
    {
        try
        {
            if (dev.GetId(out string id) != 0 || string.IsNullOrEmpty(id) || dev.GetState(out int state) != 0) return null;

            var (friendlyName, desc) = ReadEndpointNames(dev, id);
            var type = CategorizeEndpoint(friendlyName, desc);
            bool isActive = state == CoreAudioConstants.DEVICE_STATE_ACTIVE;
            bool isDefault = string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase);

            (float Level, bool IsMuted)? outputVolume = null;
            if (isActive)
            {
                try
                {
                    Guid iid = IID_IAudioEndpointVolume;
                    if (dev.Activate(ref iid, 1, IntPtr.Zero, out var volObj) == 0 && volObj is not null)
                    {
                        try
                        {
                            if (volObj is IAudioEndpointVolume volume) outputVolume = ReadVolume(volume);
                        }
                        finally { Marshal.ReleaseComObject(volObj); }
                    }
                }
                catch
                {
                    // Ignore volume read error
                }
            }

            return new AudioEndpointModel
            {
                Id = id,
                FriendlyName = friendlyName,
                DeviceDescription = desc,
                EndpointType = type,
                IsActive = isActive,
                IsDefault = isDefault,
                StateRaw = state,
                VolumeLevel = outputVolume?.Level ?? 1f,
                IsMuted = outputVolume?.IsMuted
            };
        }
        catch (Exception exception)
        {
            Trace.TraceWarning("Core Audio endpoint metadata failed: {0}", exception);
            return null;
        }
    }

    private static (string Name, string Description) ReadEndpointNames(IMMDevice device, string id)
    {
        string name = string.Empty;
        string description = string.Empty;
        if (device.OpenPropertyStore(CoreAudioConstants.STGM_READ, out var properties) == 0 && properties is not null)
        {
            try
            {
                name = ReadString(properties, PropertyKeys.PKEY_Device_FriendlyName);
                if (string.IsNullOrWhiteSpace(name))
                    name = ReadString(properties, PropertyKeys.PKEY_DeviceInterface_FriendlyName);
                description = ReadString(properties, PropertyKeys.PKEY_Device_DeviceDesc);
            }
            finally { Marshal.ReleaseComObject(properties); }
        }

        if (string.IsNullOrWhiteSpace(name)) name = string.IsNullOrWhiteSpace(description) ? id : description;
        return (name, description);
    }

    private static (float Level, bool IsMuted)? ReadVolume(IAudioEndpointVolume volume) =>
        volume.GetMasterVolumeLevelScalar(out float level) >= 0 && volume.GetMute(out bool muted) >= 0
            ? (level, muted)
            : null;

    private static string ReadString(IPropertyStore properties, PROPERTYKEY key)
    {
        if (properties.GetValue(ref key, out var value) < 0) return string.Empty;
        try
        {
            return value.vt == PROPVARIANT.VT_LPWSTR && value.pwszVal != 0
                ? Marshal.PtrToStringUni(value.pwszVal) ?? string.Empty : string.Empty;
        }
        finally { PropVariantClear(ref value); }
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(ref PROPVARIANT value);

    private static AudioEndpointType CategorizeEndpoint(string name, string desc)
    {
        string combined = (name + " " + desc).ToLowerInvariant();

        if (combined.Contains("dualsense") || combined.Contains("wireless controller"))
        {
            return AudioEndpointType.VirtualDualSense;
        }
        if (combined.Contains("vb-audio virtual cable") || combined.Contains("cable in") || combined.Contains("cable-a") || combined.Contains("cable-b"))
        {
            return AudioEndpointType.VirtualCable;
        }
        if (combined.Contains("voicemeeter") || combined.Contains("vaio"))
        {
            return AudioEndpointType.VoicemeeterVAIO;
        }
        if (combined.Contains("steam streaming"))
        {
            return AudioEndpointType.VirtualSink;
        }

        return AudioEndpointType.PhysicalPlayback;
    }
}
