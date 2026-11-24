namespace HapticBridge.Core.Models;

public enum OverallSystemStatus
{
    SystemReady,
    NeedsAttention,
    PartiallyConfigured,
    SetupRequired
}

public enum ComponentHealthState
{
    Working,        // Green
    NeedsAttention, // Yellow
    Malfunctioning, // Red
    Disconnected,   // Grey / Not Installed
    Running,
    Connected,
    Ready
}

public enum ComponentLifecycleState
{
    NotInstalled,
    InstalledDormant, // Installed on disk/registry, but process/service is stopped
    Initializing,
    Running,          // Process active, but idle
    Connected,        // Active session / stream verified
    Misconfigured,    // Running, but configuration differs from reference
    Malfunctioning,   // Process crashed or driver in error state
    Unknown
}

public class SystemComponentItem
{
    public string Name { get; set; } = string.Empty;
    public string StatusText { get; set; } = "Checking...";
    public ComponentHealthState Health { get; set; } = ComponentHealthState.Disconnected;
    public ComponentLifecycleState LifecycleState { get; set; } = ComponentLifecycleState.NotInstalled;
    public string Details { get; set; } = string.Empty;
    public string InstalledVersion { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
}

public enum PhysicalControllerAttribution
{
    Unknown,
    DirectHardwareAttached,
    RemoteRelayInferred,
    None
}

public class ControllerPipelineStatus
{
    public bool IsApolloRunning { get; set; }
    public bool? IsApolloSessionActive { get; set; }
    public bool IsVirtualDS4Present { get; set; }
    public int VirtualDS4CandidateCount { get; set; }
    public string VirtualDS4InstanceId { get; set; } = string.Empty;
    public bool IsViiperRunning { get; set; }
    public bool IsVirtualDualSensePresent { get; set; }
    public bool IsDualSenseAudioEndpointPresent { get; set; }
    public PhysicalControllerAttribution PhysicalAttribution { get; set; } = PhysicalControllerAttribution.Unknown;
    public string PhysicalDeviceDescription { get; set; } = "Unknown (Streamed via Android)";

    public string SummaryStatus =>
        IsVirtualDualSensePresent && IsDualSenseAudioEndpointPresent ? "USB/IP DualSense and audio endpoint candidate detected" :
        IsVirtualDualSensePresent ? "Virtual DualSense Present (Audio Endpoint Initializing/Missing)" :
        VirtualDS4CandidateCount > 1 ? "Multiple ViGEm DS4 candidates" :
        IsVirtualDS4Present ? "ViGEm DS4 candidate detected" :
        IsApolloRunning ? "Apollo running · Remote session unverified" : "Controller Inactive";
}

public class ControllerDetectionResult
{
    public bool IsKishiDetected { get; set; }
    public bool IsVirtualDualSenseDetected { get; set; }
    public bool IsVirtualDS4Detected { get; set; }
    public ControllerPipelineStatus PipelineStatus { get; set; } = new();
    public string MainStatus { get; set; } = "Controller not detected";
    public string Subtitle { get; set; } = "No compatible controller or DualSense detected";
    public string RawDeviceName { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
}

public enum AudioEndpointType
{
    PhysicalPlayback,
    VirtualSink,
    VirtualCable,
    VirtualDualSense,
    VoicemeeterVAIO,
    Other
}

public class AudioEndpointModel
{
    public string Id { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public string DeviceDescription { get; set; } = string.Empty;
    public AudioEndpointType EndpointType { get; set; } = AudioEndpointType.PhysicalPlayback;
    public bool IsActive { get; set; }
    public bool IsDefault { get; set; }
    public float VolumeLevel { get; set; } = 1.0f;
    public bool? IsMuted { get; set; }
    public int StateRaw { get; set; } = 1; // 1 = Active
}

public class VoicemeeterRoutingState
{
    public bool IsConnected { get; set; }
    public bool HasReferenceParameters { get; set; }
    public bool HasOutputDeviceNames { get; set; }
    public int VoicemeeterType { get; set; } // 1=Standard, 2=Banana, 3=Potato
    public bool StripVaioA1 { get; set; }
    public bool StripVaioA2 { get; set; }
    public bool StripVaioB1 { get; set; }
    public bool StripVaioB2 { get; set; }
    public bool BusA2Composite { get; set; }
    public int PatchCompositeCh1 { get; set; }
    public int PatchCompositeCh2 { get; set; }
    public string PhysicalOutA1Device { get; set; } = string.Empty;
    public string PhysicalOutA2Device { get; set; } = string.Empty;

    public bool IsProperlyConfigured =>
        IsConnected &&
        HasReferenceParameters &&
        VoicemeeterType == 2 &&
        StripVaioA1 &&
        StripVaioA2 &&
        !StripVaioB1 &&
        !StripVaioB2 &&
        BusA2Composite &&
        PatchCompositeCh1 == 11 &&
        PatchCompositeCh2 == 12;
}

public class UpdateProposal
{
    public string ApplicationName { get; set; } = string.Empty;
    public string InstalledVersion { get; set; } = string.Empty;
    public string ProposedVersion { get; set; } = string.Empty;
    public string OfficialSourceUrl { get; set; } = string.Empty;
    public string RecommendationReason { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
    public string CompatibilityNotes { get; set; } = string.Empty;
    public bool RequiresAdmin { get; set; }
    public bool RequiresRestart { get; set; }
    public string RestartTarget { get; set; } = "Application"; // Application or Windows
}
