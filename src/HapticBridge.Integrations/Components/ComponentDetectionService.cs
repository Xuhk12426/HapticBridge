using HapticBridge.Core.Models;
using HapticBridge.Core.Services;
using HapticBridge.Integrations.Apollo;
using HapticBridge.Integrations.DS4Windows;
using HapticBridge.Integrations.SoundSync;

namespace HapticBridge.Integrations.Components;

public class ComponentDetectionService : IComponentDetectionService
{
    private readonly ApolloIntegration _apollo;
    private readonly DS4WindowsIntegration _ds4;
    private readonly IVoicemeeterManager _voicemeeter;
    private readonly IAudioEndpointService _audio;
    private readonly SoundSyncIntegration _soundSync;

    public ComponentDetectionService(
        ApolloIntegration apollo,
        DS4WindowsIntegration ds4,
        IVoicemeeterManager voicemeeter,
        IAudioEndpointService audio,
        SoundSyncIntegration soundSync)
    {
        _apollo = apollo;
        _ds4 = ds4;
        _voicemeeter = voicemeeter;
        _audio = audio;
        _soundSync = soundSync;
    }

    public async Task<IReadOnlyList<SystemComponentItem>> DetectAllComponentsAsync()
    {
        return await Task.Run(() =>
        {
            var list = new List<SystemComponentItem>
            {
                DetectApollo(),
                DetectDS4Windows(),
                DetectViiper(),
                DetectVoicemeeter(),
                DetectVbCable(),
                DetectSoundSync()
            };
            return (IReadOnlyList<SystemComponentItem>)list;
        });
    }

    public SystemComponentItem DetectApollo()
    {
        var item = new SystemComponentItem
        {
            Name = "Apollo",
            InstalledVersion = _apollo.DetectedVersion,
            ExecutablePath = ApolloIntegration.LocateInstallDirectory()
        };

        if (!_apollo.IsInstalled)
        {
            item.StatusText = "Not installed";
            item.Health = ComponentHealthState.Disconnected;
            item.LifecycleState = ComponentLifecycleState.NotInstalled;
            item.Details = "Apollo host was not detected on this PC.";
            return item;
        }

        if (!_apollo.IsRunning)
        {
            item.StatusText = "Stopped / Dormant";
            item.Health = ComponentHealthState.NeedsAttention;
            item.LifecycleState = ComponentLifecycleState.InstalledDormant;
            item.Details = "Apollo host is installed on disk but service/process is currently stopped.";
            return item;
        }

        var config = _apollo.ReadConfiguration();
        if (config.IsProperlyConfigured)
        {
            item.StatusText = "Running";
            item.Health = ComponentHealthState.Working;
            item.LifecycleState = ComponentLifecycleState.Running;
            item.Details = $"Host process running. Saved virtual sink: {config.VirtualSink}. Remote session unverified.";
        }
        else
        {
            item.StatusText = "Needs attention";
            item.Health = ComponentHealthState.NeedsAttention;
            item.LifecycleState = ComponentLifecycleState.Misconfigured;
            item.Details = "Running, but gamepad emulation (gamepad=ds4) or virtual sink needs configuration.";
        }

        return item;
    }

    internal static SystemComponentItem DescribeStoppedTool(SystemComponentItem item, string executable)
    {
        bool found = Path.IsPathFullyQualified(executable) && File.Exists(executable);
        item.Health = ComponentHealthState.NeedsAttention;
        item.LifecycleState = found ? ComponentLifecycleState.InstalledDormant : ComponentLifecycleState.Unknown;
        item.StatusText = found ? "Stopped / Dormant" : "Installation unverified";
        item.Details = found ? $"Start {item.Name} to use the external route."
            : $"Windows found setup files but could not locate {item.Name}. Open your copy or check Install prerequisites.";
        return item;
    }

    public SystemComponentItem DetectDS4Windows()
    {
        var item = new SystemComponentItem
        {
            Name = "DS4Windows",
            InstalledVersion = _ds4.DetectedVersion,
            ExecutablePath = DS4WindowsIntegration.LocateInstallDirectory()
        };

        bool running = _ds4.IsRunning;
        if (!running && !_ds4.IsInstalled)
        {
            item.StatusText = "Not installed";
            item.Health = ComponentHealthState.Disconnected;
            item.LifecycleState = ComponentLifecycleState.NotInstalled;
            item.Details = "DS4Windows was not detected on this PC.";
            return item;
        }

        if (!running)
        {
            return DescribeStoppedTool(item, Path.Combine(item.ExecutablePath, "DS4Windows.exe"));
        }

        var config = _ds4.ReadConfiguration();
        bool viiperRunning = _ds4.IsViiperRunning;

        if (config.IsProperlyConfigured && viiperRunning)
        {
            item.StatusText = "Running";
            item.Health = ComponentHealthState.Working;
            item.LifecycleState = ComponentLifecycleState.Running;
            item.Details = "DS4Windows and VIIPER processes running. The saved profile enables DualSense emulation.";
        }
        else if (!viiperRunning)
        {
            item.StatusText = "Needs attention";
            item.Health = ComponentHealthState.NeedsAttention;
            item.LifecycleState = ComponentLifecycleState.Misconfigured;
            item.Details = "DS4Windows running, but VIIPER helper process is stopped.";
        }
        else
        {
            item.StatusText = "Needs attention";
            item.Health = ComponentHealthState.NeedsAttention;
            item.LifecycleState = ComponentLifecycleState.Misconfigured;
            item.Details = "Moonlight hook or DualSense emulation is not enabled in profile.";
        }

        return item;
    }

    public SystemComponentItem DetectVoicemeeter()
    {
        string dllPath = _voicemeeter.DllPath;
        string executable = Path.Combine(Path.GetDirectoryName(dllPath) ?? string.Empty, "voicemeeterpro.exe");
        bool foundExecutable = Path.IsPathFullyQualified(executable) && File.Exists(executable);
        var item = new SystemComponentItem
        {
            Name = "Voicemeeter Banana",
            ExecutablePath = foundExecutable ? executable : string.Empty,
            InstalledVersion = foundExecutable
                ? System.Diagnostics.FileVersionInfo.GetVersionInfo(executable).FileVersion ?? string.Empty
                : string.Empty
        };

        if (!_voicemeeter.IsInstalled)
        {
            item.StatusText = "Not installed";
            item.Health = ComponentHealthState.Disconnected;
            item.LifecycleState = ComponentLifecycleState.NotInstalled;
            item.Details = "Voicemeeter Banana was not found on this system.";
            return item;
        }

        if (!_voicemeeter.IsRunning)
        {
            item.StatusText = "Stopped / Dormant";
            item.Health = ComponentHealthState.NeedsAttention;
            item.LifecycleState = ComponentLifecycleState.InstalledDormant;
            item.Details = "Voicemeeter Banana is installed on disk but not currently running.";
            return item;
        }

        var routing = _voicemeeter.GetLiveRoutingState();
        if (routing.IsProperlyConfigured)
        {
            item.StatusText = "Configured";
            item.Health = ComponentHealthState.Working;
            item.LifecycleState = ComponentLifecycleState.Running;
            item.Details = "Voicemeeter reports the expected reference routing parameters.";
        }
        else if (routing.HasReferenceParameters)
        {
            item.StatusText = "Needs attention";
            item.Health = ComponentHealthState.NeedsAttention;
            item.LifecycleState = ComponentLifecycleState.Misconfigured;
            item.Details = "Voicemeeter running, but routing matrix differs from haptic reference.";
        }
        else
        {
            item.StatusText = "Routing unverified";
            item.Health = ComponentHealthState.NeedsAttention;
            item.LifecycleState = ComponentLifecycleState.Running;
            item.Details = "Voicemeeter is running. The Remote API did not return all required routing parameters.";
        }

        return item;
    }

    public SystemComponentItem DetectVbCable()
    {
        var item = new SystemComponentItem
        {
            Name = "VB-CABLE"
        };

        var cableDev = _audio.GetVirtualCableEndpoint();
        if (cableDev == null)
        {
            item.StatusText = "Endpoint unavailable";
            item.Health = ComponentHealthState.Disconnected;
            item.LifecycleState = ComponentLifecycleState.Unknown;
            item.Details = "Windows did not expose a VB-CABLE playback endpoint. Driver installation is unverified.";
            return item;
        }

        if (cableDev.IsActive)
        {
            item.StatusText = "Endpoint active";
            item.Health = ComponentHealthState.Working;
            item.LifecycleState = ComponentLifecycleState.Running;
            item.Details = $"VB-Audio Virtual Cable active ({cableDev.FriendlyName}).";
        }
        else
        {
            item.StatusText = "Disabled / Missing";
            item.Health = ComponentHealthState.Malfunctioning;
            item.LifecycleState = ComponentLifecycleState.Malfunctioning;
            item.Details = "VB-CABLE driver is present but endpoint is disabled or unplugged.";
        }

        return item;
    }

    private SystemComponentItem DetectViiper()
    {
        string path = Path.Combine(DS4WindowsIntegration.LocateInstallDirectory(), "VIIPER", "viiper.exe");
        bool installed = Path.IsPathFullyQualified(path) && File.Exists(path);
        bool running = _ds4.IsViiperRunning;
        return new SystemComponentItem
        {
            Name = "VIIPER",
            ExecutablePath = installed ? path : string.Empty,
            InstalledVersion = installed
                ? System.Diagnostics.FileVersionInfo.GetVersionInfo(path).ProductVersion ?? string.Empty
                : string.Empty,
            StatusText = running ? "Running" : installed ? "Stopped / Dormant" : "Installation unverified",
            LifecycleState = running ? ComponentLifecycleState.Running
                : installed ? ComponentLifecycleState.InstalledDormant : ComponentLifecycleState.Unknown,
            Health = running ? ComponentHealthState.Running : ComponentHealthState.Disconnected,
            Details = running ? "Process observed. Device ownership and audio flow are unverified."
                : installed ? "The DS4Windows-bundled executable is present." : "The DS4Windows-bundled executable was not found."
        };
    }

    public SystemComponentItem DetectSoundSync()
    {
        var item = new SystemComponentItem
        {
            Name = "SoundSync",
            InstalledVersion = _soundSync.DetectedVersion,
            ExecutablePath = SoundSyncIntegration.LocateExecutablePath()
        };

        bool running = _soundSync.IsRunning;
        if (!running && !_soundSync.IsInstalled)
        {
            item.StatusText = "Not installed";
            item.Health = ComponentHealthState.Disconnected;
            item.LifecycleState = ComponentLifecycleState.NotInstalled;
            item.Details = "SoundSync was not found on this PC.";
            return item;
        }

        if (!running)
        {
            return DescribeStoppedTool(item, item.ExecutablePath);
        }

        var cable = _audio.GetVirtualCableEndpoint();
        if (cable is not { IsActive: true })
        {
            item.StatusText = "Output unavailable";
            item.Health = ComponentHealthState.NeedsAttention;
            item.LifecycleState = ComponentLifecycleState.Running;
            item.Details = "SoundSync is running. The VB-CABLE endpoint is unavailable.";
            return item;
        }

        bool cableConfigured = _soundSync.IsCableOutputConfigured(cable.Id);

        if (cableConfigured)
        {
            item.StatusText = "Profile configured";
            item.Health = ComponentHealthState.Working;
            item.LifecycleState = ComponentLifecycleState.Running;
            item.Details = "SoundSync is running and the saved profile selects VB-CABLE. Audio flow is unverified.";
        }
        else
        {
            item.StatusText = "Needs attention";
            item.Health = ComponentHealthState.NeedsAttention;
            item.LifecycleState = ComponentLifecycleState.Misconfigured;
            item.Details = "SoundSync running, but VB-CABLE output is not enabled or volume < 100%.";
        }

        return item;
    }
}
