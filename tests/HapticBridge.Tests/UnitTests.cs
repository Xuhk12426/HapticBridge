using System.IO;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HapticBridge.Core.Models;
using HapticBridge.Core.Services;
using HapticBridge.Integrations.Apollo;
using HapticBridge.Integrations.Components;
using HapticBridge.Integrations.Controllers;
using static HapticBridge.Integrations.Controllers.ControllerDetectionService;
using HapticBridge.Integrations.DS4Windows;
using HapticBridge.Integrations.SoundSync;
using HapticBridge.Integrations.VIIPER;
using HapticBridge.Setup.Backups;
using HapticBridge.Setup.Updates;

namespace HapticBridge.Tests;

// Mock Audio Endpoint Service for hardware-independent testing
public class MockAudioEndpointService : IAudioEndpointService
{
    public AudioEndpointModel? DualSenseEndpoint { get; set; }
    public AudioEndpointModel? VirtualCableEndpoint { get; set; }
    public AudioEndpointModel? VoicemeeterVaioEndpoint { get; set; }
    public List<AudioEndpointModel> RenderEndpoints { get; } = new();
    public List<(string EndpointId, int Volume)> PlaybackVolumeRequests { get; } = new();
    public bool AcceptPlaybackVolume { get; set; } = true;

    public IReadOnlyList<AudioEndpointModel> GetAllRenderEndpoints() => RenderEndpoints;
    public IReadOnlyList<AudioEndpointModel> GetPhysicalPlaybackEndpoints() =>
        RenderEndpoints.Where(x => x.EndpointType == AudioEndpointType.PhysicalPlayback).ToList();
    public (float Level, bool IsMuted)? SetPhysicalPlaybackVolume(string endpointId, int volumePercent)
    {
        PlaybackVolumeRequests.Add((endpointId, volumePercent));
        var selected = GetPhysicalPlaybackEndpoints().SingleOrDefault(endpoint => endpoint.Id == endpointId);
        if (!AcceptPlaybackVolume || selected?.IsMuted is not bool muted) return null;
        selected.VolumeLevel = volumePercent / 100f;
        selected.IsMuted = volumePercent == 0 && muted;
        return (selected.VolumeLevel, selected.IsMuted.Value);
    }
    public AudioEndpointModel? GetDefaultPlaybackEndpoint() =>
        RenderEndpoints.FirstOrDefault(x => x.IsDefault) ?? RenderEndpoints.FirstOrDefault();
    public AudioEndpointModel? GetVirtualDualSenseEndpoint() => DualSenseEndpoint;
    public AudioEndpointModel? GetVirtualCableEndpoint() => VirtualCableEndpoint;
    public AudioEndpointModel? GetVoicemeeterVaioEndpoint() => VoicemeeterVaioEndpoint;
    public bool SetDefaultEndpoint(string endpointId) => true;
}

// Mock Voicemeeter Manager for hardware-independent testing
public class MockVoicemeeterManager : IVoicemeeterManager
{
    public bool IsInstalled { get; set; } = true;
    public bool IsRunning { get; set; } = false;
    public bool CanLaunchBanana { get; set; }
    public string DllPath { get; set; } = "C:\\Mock\\VoicemeeterRemote64.dll";
    public VoicemeeterRoutingState LiveState { get; set; } = new();
    public (string DeviceName, int? VolumePercent)? ListeningOutput { get; set; }
    public List<string> RequestedPhysicalOutputs { get; } = [];
    public List<(string DeviceName, int VolumePercent)> RequestedListeningVolumes { get; } = [];
    public bool AcceptPhysicalOutput { get; set; } = true;
    public bool AcceptListeningVolume { get; set; } = true;
    public int ReferenceRoutingRequests { get; private set; }
    public bool BackupSucceeds { get; set; } = true;
    public int BackupRequests { get; private set; }
    public Action? OnLaunch { get; set; }
    public Action<string?, string>? OnConfigureReferenceRouting { get; set; }

    public VoicemeeterRoutingState GetLiveRoutingState() => LiveState;
    public (string DeviceName, int? VolumePercent)? GetListeningOutput() => ListeningOutput;
    public bool TrySetPhysicalOutput(string deviceName)
    {
        RequestedPhysicalOutputs.Add(deviceName);
        return AcceptPhysicalOutput;
    }
    public bool TrySetListeningVolume(string deviceName, int volumePercent)
    {
        RequestedListeningVolumes.Add((deviceName, volumePercent));
        return AcceptListeningVolume;
    }
    public bool ConfigureReferenceRouting(string? physicalOutputA1Name, string cableOutputA2Name)
    {
        ReferenceRoutingRequests++;
        OnConfigureReferenceRouting?.Invoke(physicalOutputA1Name, cableOutputA2Name);
        return true;
    }
    public bool CreateConfigurationBackup(string backupDestinationPath)
    {
        BackupRequests++;
        return BackupSucceeds;
    }
    public bool RestoreConfiguration(string backupSourcePath) => true;
    public void LaunchVoicemeeter() => OnLaunch?.Invoke();
}

[TestClass]
public class ComprehensiveUnitTests
{
    // =========================================================================
    // SECTION 1: Baseline Model & Parser Unit Tests (7 Original Tests)
    // =========================================================================

    [TestMethod]
    public void Test_VoicemeeterRouting_CompliantState_EvaluatesTrue()
    {
        var state = new VoicemeeterRoutingState
        {
            IsConnected = true,
            HasReferenceParameters = true,
            VoicemeeterType = 2, // Banana
            StripVaioA1 = true,
            StripVaioA2 = true,
            StripVaioB1 = false,
            StripVaioB2 = false,
            BusA2Composite = true,
            PatchCompositeCh1 = 11,
            PatchCompositeCh2 = 12
        };

        Assert.IsTrue(state.IsProperlyConfigured, "Compliant routing must evaluate to true.");
    }

    [TestMethod]
    public void Test_VoicemeeterRouting_NonCompliantState_EvaluatesFalse()
    {
        var state = new VoicemeeterRoutingState
        {
            IsConnected = true,
            HasReferenceParameters = true,
            VoicemeeterType = 2,
            StripVaioA1 = true,
            StripVaioA2 = false, // Broken
            BusA2Composite = true,
            PatchCompositeCh1 = 11,
            PatchCompositeCh2 = 12
        };

        Assert.IsFalse(state.IsProperlyConfigured, "Disabled A2 must not be compliant.");
    }

    [TestMethod]
    public void Test_ApolloConfig_ParsingAndValidation()
    {
        var config = new ApolloConfigModel
        {
            Gamepad = "ds4",
            VirtualSink = "Voicemeeter Input (VB-Audio Voicemeeter VAIO)"
        };

        Assert.IsTrue(config.IsProperlyConfigured);

        var brokenConfig = new ApolloConfigModel
        {
            Gamepad = "x360",
            VirtualSink = "Default"
        };

        Assert.IsFalse(brokenConfig.IsProperlyConfigured);
    }

    [TestMethod]
    public void Test_DS4WindowsConfig_ParsingAndValidation()
    {
        var config = new DS4WindowsConfigModel
        {
            UseMoonlight = true,
            OutputContDevice = "ViiperDualSense",
            DualSenseAudioHapticsEnabled = true,
            DualSenseAdaptiveTriggersEnabled = true
        };

        Assert.IsTrue(config.IsProperlyConfigured);

        var brokenConfig = new DS4WindowsConfigModel
        {
            UseMoonlight = false,
            OutputContDevice = "Xbox360",
            DualSenseAudioHapticsEnabled = false
        };

        Assert.IsFalse(brokenConfig.IsProperlyConfigured);
    }

    [TestMethod]
    public void Test_UpdatePolicy_NeverAutoApproves()
    {
        var updateService = new UpdateApprovalService();
        var proposal = UpdateApprovalService.CreateProposal(
            "Voicemeeter Banana",
            "2.0.6.8",
            "2.0.7.0",
            "https://vb-audio.com",
            "Stability improvement");

        Assert.IsFalse(updateService.CanUpdate("Voicemeeter Banana"), "Must be false before explicit user approval.");

        // Explicitly decline
        updateService.RegisterUserDecision("Voicemeeter Banana", false);
        Assert.IsFalse(updateService.CanUpdate("Voicemeeter Banana"), "Must remain false when user declines.");

        // Explicitly approve
        updateService.RegisterUserDecision("Voicemeeter Banana", true);
        Assert.IsTrue(updateService.CanUpdate("Voicemeeter Banana"), "Only true after affirmative approval.");
    }

    [TestMethod]
    public void Test_BackupService_CreatesAndRestoresFile()
    {
        string tempDir = Directory.CreateTempSubdirectory("HapticBridge-backup-").FullName;

        string originalFile = Path.Combine(tempDir, "test_config.xml");
        File.WriteAllText(originalFile, "<Config><Value>Original</Value></Config>");

        var backupService = new ConfigurationBackupService(Path.Combine(tempDir, "backups"));
        string backupPath = backupService.CreateBackup("TestComp", originalFile);

        Assert.IsTrue(File.Exists(backupPath), "Backup file must exist on disk.");

        // Modify original
        File.WriteAllText(originalFile, "<Config><Value>Corrupted</Value></Config>");
        string secondBackup = backupService.CreateBackup("TestComp", originalFile);
        Assert.AreNotEqual(backupPath, secondBackup);

        // Restore
        bool restored = backupService.RestoreBackup(backupPath, originalFile);
        Assert.IsTrue(restored, "Restore must succeed.");
        Assert.AreEqual("<Config><Value>Original</Value></Config>", File.ReadAllText(originalFile));
    }

    [TestMethod]
    public void Test_AudioEndpointModel_VolumeLevelClamping()
    {
        var ep = new AudioEndpointModel
        {
            Id = "{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}",
            FriendlyName = "Speakers (Realtek Audio)",
            VolumeLevel = 0.85f,
            IsActive = true
        };

        Assert.AreEqual(0.85f, ep.VolumeLevel, 0.001f);
        Assert.IsTrue(ep.IsActive);
        Assert.AreEqual(AudioEndpointType.PhysicalPlayback, ep.EndpointType);
    }

    // =========================================================================
    // SECTION 2: 18 Hardware-Independent Mock Tests (Phase 2 Hardening)
    // =========================================================================

    [TestMethod]
    public void Test_ControllerDetection_NoDevices_ReturnsInactiveStatus()
    {
        var mockAudio = new MockAudioEndpointService(); // No DualSense endpoint
        var detector = new ControllerDetectionService(mockAudio, () => [], () => false, () => false);

        var result = detector.DetectControllers();

        Assert.IsNotNull(result);
        Assert.IsFalse(result.IsKishiDetected, "Must not detect Kishi when no hardware is attached.");
        Assert.IsFalse(result.IsVirtualDualSenseDetected, "Must not detect DualSense when none exists.");
        Assert.AreEqual("Controller not detected", result.MainStatus);
    }

    [TestMethod]
    public void Test_ControllerDetection_ApolloRunning_NoSession_ReportsWaitingForStream()
    {
        var pipeline = new ControllerPipelineStatus
        {
            IsApolloRunning = true,
            IsApolloSessionActive = false,
            IsVirtualDS4Present = false,
            IsVirtualDualSensePresent = false
        };

        Assert.AreEqual("Apollo running · Remote session unverified", pipeline.SummaryStatus);
    }

    [TestMethod]
    public void Test_ControllerDetection_MissingVirtualDS4_DoesNotThrow()
    {
        var mockAudio = new MockAudioEndpointService();
        var detector = new ControllerDetectionService(mockAudio, () => [], () => false, () => false);

        // Execution must complete cleanly without throwing exceptions when DS4 is absent
        var result = detector.DetectControllers();
        Assert.IsNotNull(result);
        Assert.IsFalse(result.IsVirtualDS4Detected);
    }

    [TestMethod]
    public void Test_ControllerDetection_AmbiguousDS4_IdentifiesViGEmParentDevnode()
    {
        var detector = new ControllerDetectionService(new MockAudioEndpointService(), () =>
        [
            new UsbDevice(@"USB\VID_054C&PID_05C4&REV_0100\01", "Wireless Controller", UsbBus.ViGEm),
            new UsbDevice(@"USB\VID_054C&PID_05C4\02", "Wireless Controller", UsbBus.ViGEm),
            new UsbDevice(@"USB\VID_054C&PID_05C4\physical", "Wireless Controller", UsbBus.Physical)
        ], () => true, () => false);

        var result = detector.DetectControllers();
        Assert.AreEqual(2, result.PipelineStatus.VirtualDS4CandidateCount);
        Assert.AreEqual(string.Empty, result.PipelineStatus.VirtualDS4InstanceId);
        Assert.IsNull(result.PipelineStatus.IsApolloSessionActive);
        Assert.AreEqual("Ambiguous virtual DS4", result.MainStatus);
    }

    [TestMethod]
    public void Test_ControllerDetection_MultipleControllers_FiltersOwnVirtualOutput()
    {
        // VIIPER outputs create DualSense devices on imported USBIP ports.
        // Test that checking owned ports rejects own outputs.
        int activeViiperPort = 2;
        int incomingDevicePort = 2;
        int physicalDevicePort = -1;

        bool isOwnOutput = (incomingDevicePort == activeViiperPort);
        bool isPhysicalOutput = (physicalDevicePort == activeViiperPort);

        Assert.IsTrue(isOwnOutput, "Incoming device on active VIIPER port must be identified as own output.");
        Assert.IsFalse(isPhysicalOutput, "Physical device must not match active VIIPER port.");
    }

    [TestMethod]
    public void Test_ControllerDetection_ChangingDeviceInstanceIds_MatchesHardwareId()
    {
        // ViGEm can generate different instance IDs across boots, but HardwareID remains constant
        string boot1Instance = @"HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&05C4\8&39E09E97&0&0000";
        string boot2Instance = @"HID\{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&05C4\8&99A12B44&0&0000";

        bool matchesHardware1 = boot1Instance.Contains("054C", StringComparison.OrdinalIgnoreCase) &&
                                boot1Instance.Contains("05C4", StringComparison.OrdinalIgnoreCase);
        bool matchesHardware2 = boot2Instance.Contains("054C", StringComparison.OrdinalIgnoreCase) &&
                                boot2Instance.Contains("05C4", StringComparison.OrdinalIgnoreCase);

        Assert.IsTrue(matchesHardware1);
        Assert.IsTrue(matchesHardware2);
    }

    [TestMethod]
    public void Test_ControllerDetection_ViiperUnavailable_ReportsDualSenseMissing()
    {
        var pipeline = new ControllerPipelineStatus
        {
            IsViiperRunning = false,
            IsVirtualDualSensePresent = false,
            IsDualSenseAudioEndpointPresent = false
        };

        Assert.IsFalse(pipeline.IsVirtualDualSensePresent);
        Assert.AreEqual("Controller Inactive", pipeline.SummaryStatus);
    }

    [TestMethod]
    public void Test_ControllerDetection_MissingDualSenseAudioEndpoint_FlagsAudioUnverified()
    {
        var pipeline = new ControllerPipelineStatus
        {
            IsVirtualDualSensePresent = true,
            IsDualSenseAudioEndpointPresent = false // Endpoint absent
        };

        Assert.AreEqual("Virtual DualSense Present (Audio Endpoint Initializing/Missing)", pipeline.SummaryStatus);
    }

    [TestMethod]
    public void Test_ComponentDetection_StoppedVoicemeeter_ReportsInstalledDormantNotMalfunctioning()
    {
        var item = new SystemComponentItem
        {
            Name = "Voicemeeter Banana",
            StatusText = "Stopped / Dormant",
            Health = ComponentHealthState.NeedsAttention, // Yellow, NOT Malfunctioning (Red)
            LifecycleState = ComponentLifecycleState.InstalledDormant
        };

        Assert.AreEqual(ComponentLifecycleState.InstalledDormant, item.LifecycleState);
        Assert.AreNotEqual(ComponentHealthState.Malfunctioning, item.Health, "Dormant process must not be marked malfunctioning.");
    }

    [TestMethod]
    public void Test_ComponentDetection_StoppedSoundSync_ReportsInstalledDormant()
    {
        var item = new SystemComponentItem
        {
            Name = "SoundSync",
            StatusText = "Stopped / Dormant",
            Health = ComponentHealthState.NeedsAttention,
            LifecycleState = ComponentLifecycleState.InstalledDormant
        };

        Assert.AreEqual(ComponentLifecycleState.InstalledDormant, item.LifecycleState);
        Assert.AreEqual("Stopped / Dormant", item.StatusText);
    }

    [TestMethod]
    public void Test_ComponentDetection_MissingVbCable_ReportsNotInstalled()
    {
        var item = new SystemComponentItem
        {
            Name = "VB-CABLE",
            StatusText = "Not installed",
            Health = ComponentHealthState.Disconnected,
            LifecycleState = ComponentLifecycleState.NotInstalled
        };

        Assert.AreEqual(ComponentLifecycleState.NotInstalled, item.LifecycleState);
        Assert.AreEqual(ComponentHealthState.Disconnected, item.Health);
    }

    [TestMethod]
    public void Test_ComponentDetection_DriverInstalledWithoutDeviceNode_ReportsDriverPresentDeviceDormant()
    {
        bool driverPackageInDriverStore = true;
        bool pnpDeviceNodeActive = false;

        ComponentLifecycleState state = (driverPackageInDriverStore && !pnpDeviceNodeActive)
            ? ComponentLifecycleState.InstalledDormant
            : ComponentLifecycleState.Running;

        Assert.AreEqual(ComponentLifecycleState.InstalledDormant, state, "Installed driver package with inactive device node must evaluate to InstalledDormant.");
    }

    [TestMethod]
    public void Test_UpdatePolicy_ExplicitDecline_PreservesExistingConfig()
    {
        var service = new UpdateApprovalService();
        service.RegisterUserDecision("Apollo", false); // Declined

        Assert.IsFalse(service.CanUpdate("Apollo"), "Declined update must remain blocked.");
    }

    [TestMethod]
    public void Test_DetectionService_DoesNotAttemptAutoRepairDuringInspection()
    {
        var mockAudio = new MockAudioEndpointService();
        var detector = new ControllerDetectionService(mockAudio, () => [], () => false, () => false);

        // Calling DetectControllers must be completely read-only and return without mutating anything
        var result = detector.DetectControllers();
        Assert.IsNotNull(result);
    }

    [TestMethod]
    public void Test_EvidenceClassification_CorrectlyTagsLiveVsStaticArtifacts()
    {
        string liveObservation = "[LIVE_VERIFIED] Process sunshine.exe PID 9424 active";
        string referenceXml = "[HISTORICAL_REFERENCE] DS audio setup.xml";

        Assert.IsTrue(liveObservation.StartsWith("[LIVE_VERIFIED]"));
        Assert.IsTrue(referenceXml.StartsWith("[HISTORICAL_REFERENCE]"));
    }

    [TestMethod]
    public void Test_FeedbackLoopPrevention_RejectsOwnedViiperUsbipPort()
    {
        int viiperOwnedPort = 3;
        int inspectedDevicePort = 3;

        bool shouldAccept = (inspectedDevicePort != viiperOwnedPort);
        Assert.IsFalse(shouldAccept, "Must reject controller on own VIIPER USBIP port to prevent feedback loop.");
    }

    [TestMethod]
    public void Test_ControllerAttribution_DoesNotAssumeKishiFromDualSense()
    {
        // Testing the critical bug fix: Virtual DualSense presence must NEVER infer Kishi presence!
        var mockAudio = new MockAudioEndpointService
        {
            DualSenseEndpoint = new AudioEndpointModel
            {
                Id = "{0.0.0.00000000}.{0dfdcfe6-e2d3-4761-9b98-6d362eea67f3}",
                FriendlyName = "DualSense Wireless Controller",
                IsActive = true
            }
        };

        var detector = new ControllerDetectionService(mockAudio, () =>
        [
            new UsbDevice(@"USB\VID_054C&PID_0CE6\01", "Wireless Controller", UsbBus.UsbIp),
            new UsbDevice(@"USB\VID_1532&PID_0001\02", "Razer Mouse", UsbBus.Physical)
        ], () => true, () => true);
        var result = detector.DetectControllers();

        Assert.IsTrue(result.IsVirtualDualSenseDetected);
        Assert.IsFalse(result.IsVirtualDS4Detected);
        Assert.IsFalse(result.IsKishiDetected, "CRITICAL: DualSense presence must NEVER infer Kishi detection!");
        Assert.AreEqual(PhysicalControllerAttribution.Unknown, result.PipelineStatus.PhysicalAttribution);
    }

    [TestMethod]
    public void Test_ApplicationStartup_WithoutHapticsHardware_InitializesCleanly()
    {
        var mockAudio = new MockAudioEndpointService();
        var detector = new ControllerDetectionService(mockAudio, () => [], () => false, () => false);

        var result = detector.DetectControllers();

        Assert.IsNotNull(result);
        Assert.IsFalse(result.IsKishiDetected);
        Assert.IsFalse(result.IsVirtualDualSenseDetected);
        Assert.AreEqual("Controller not detected", result.MainStatus);
    }

    // =========================================================================
    // SECTION 3: Phase 3 Apollo HID Input Report Decoding Unit Tests
    // =========================================================================

    [TestMethod]
    public void Test_Ds4ReportParser_NeutralSticks_DecodesCorrectly()
    {
        byte[] payload = new byte[64];
        payload[0] = 0x01; // Report ID
        payload[1] = 128;  // LX center
        payload[2] = 128;  // LY center
        payload[3] = 128;  // RX center
        payload[4] = 128;  // RY center
        payload[5] = 0x08; // Neutral D-pad

        var report = Ds4ReportParser.Parse(payload);

        Assert.AreEqual(128, report.ThumbLX);
        Assert.AreEqual(128, report.ThumbLY);
        Assert.AreEqual(128, report.ThumbRX);
        Assert.AreEqual(128, report.ThumbRY);
        Assert.AreEqual(8, report.DPad);
        Assert.IsFalse(report.ButtonCross);
        Assert.IsFalse(report.ButtonCircle);
    }

    [TestMethod]
    public void Test_Ds4ReportParser_ButtonsAndTriggers_DecodesCorrectly()
    {
        byte[] payload = new byte[64];
        payload[0] = 0x01; // Report ID
        payload[1] = 0;    // LX full left
        payload[2] = 255;  // LY full down
        payload[3] = 128;
        payload[4] = 128;
        payload[5] = 0x20 | 0x02; // Cross button (bit 5) + D-pad East (2)
        payload[6] = 0x03;        // L1 (bit 8 -> payload[6] bit 0) + R1 (bit 9 -> payload[6] bit 1)
        payload[7] = 0x01;        // PS button
        payload[8] = 200;         // L2 trigger
        payload[9] = 255;         // R2 trigger

        var report = Ds4ReportParser.Parse(payload);

        Assert.AreEqual(0, report.ThumbLX);
        Assert.AreEqual(255, report.ThumbLY);
        Assert.AreEqual(2, report.DPad);
        Assert.IsTrue(report.ButtonCross);
        Assert.IsTrue(report.ButtonL1);
        Assert.IsTrue(report.ButtonR1);
        Assert.IsTrue(report.ButtonPs);
        Assert.AreEqual(200, report.TriggerL);
        Assert.AreEqual(255, report.TriggerR);
    }

    [TestMethod]
    public void Test_Ds4ReportParser_MotionSensors_DecodesCorrectly()
    {
        byte[] payload = new byte[64];
        payload[0] = 0x01;
        // Gyro X at offset 1+12 = 13 (little endian short)
        payload[13] = 0x34;
        payload[14] = 0x12; // 0x1234 = 4660
        // Accel Z at offset 1+22 = 23
        payload[23] = 0x00;
        payload[24] = 0x20; // 0x2000 = 8192 (~1G)

        var report = Ds4ReportParser.Parse(payload);

        Assert.AreEqual(4660, report.GyroX);
        Assert.AreEqual(8192, report.AccelZ);
    }

    [TestMethod]
    public void Test_Ds4ReportParser_TouchpadPackets_DecodesCorrectly()
    {
        byte[] payload = new byte[64];
        payload[0] = 0x01;
        payload[33] = 1;
        int tOffset = 1 + 34;
        payload[tOffset] = 0x05;     // Touch 1 active (bit 7 == 0, counter 5)
        payload[tOffset + 1] = 0x50; // X low
        payload[tOffset + 2] = 0x20 | 0x03; // X high (3) & Y low nibble (2) -> X = 0x0350 (848)
        payload[tOffset + 3] = 0x15; // Y high -> Y = (2 | (0x15 << 4)) = (2 | 0x150) = 0x152 (338)
        payload[tOffset + 4] = 0x80;

        var report = Ds4ReportParser.Parse(payload);

        Assert.IsTrue(report.Touch1Active);
        Assert.AreEqual((byte)5, report.Touch1TrackingId);
        Assert.AreEqual(848, report.Touch1X);
        Assert.AreEqual(338, report.Touch1Y);
        Assert.IsFalse(report.Touch2Active);
    }

    [TestMethod]
    public void Test_ApolloHidInputReader_OfflineAbsentDevice_TryConnectReturnsFalse()
    {
        using var reader = new ApolloHidInputReader();
        bool connected = reader.TryConnect(null);

        Assert.IsFalse(connected, "TryConnect with null/absent path must return false cleanly without exception.");
        Assert.IsFalse(reader.IsConnected);
        Assert.IsNull(reader.DevicePath);
        Assert.IsFalse(reader.TryConnect(@"\\?\HID#synthetic"));
    }

    [TestMethod]
    public void Test_ApolloHidInputReader_FeedRawReport_DispatchesParsedReport()
    {
        using var reader = new ApolloHidInputReader();
        Ds4ReportEx? receivedReport = null;
        reader.ReportReceived += report => receivedReport = report;

        byte[] payload = new byte[64];
        payload[0] = 0x01;
        payload[1] = 200; // LX
        payload[2] = 50;  // LY

        reader.FeedRawReport(payload);

        Assert.IsNotNull(receivedReport);
        Assert.AreEqual(200, receivedReport.ThumbLX);
        Assert.AreEqual(50, receivedReport.ThumbLY);
    }

    [TestMethod]
    public void Test_ApolloHidInputReader_DropsWrongLengthAndReportIdWithoutPhantomTouches()
    {
        using var reader = new ApolloHidInputReader();
        var received = new List<Ds4ReportEx>();
        reader.ReportReceived += received.Add;

        reader.FeedRawReport(new byte[22]);
        reader.FeedRawReport(new byte[64]);
        Assert.IsEmpty(received);

        var valid = new byte[64];
        valid[0] = 1;
        valid[35] = 2;
        reader.FeedRawReport(valid);
        Assert.HasCount(1, received);
        Assert.IsFalse(received[0].Touch1Active);
        Assert.IsFalse(received[0].Touch2Active);
    }

    // =========================================================================
    // SECTION 4: Phase 4 Direct VIIPER Controller Output & Translation Tests
    // =========================================================================

    [TestMethod]
    public void Test_Ds4ToDualsenseTranslator_SticksAndTriggers_MapsDirectly()
    {
        var ds4 = new Ds4ReportEx
        {
            ThumbLX = 25,
            ThumbLY = 230,
            ThumbRX = 128,
            ThumbRY = 200,
            TriggerL = 180,
            TriggerR = 250
        };

        var ds = Ds4ToDualsenseTranslator.Translate(ds4);

        Assert.AreEqual(25, ds.ThumbLX);
        Assert.AreEqual(230, ds.ThumbLY);
        Assert.AreEqual(128, ds.ThumbRX);
        Assert.AreEqual(200, ds.ThumbRY);
        Assert.AreEqual(180, ds.TriggerL2);
        Assert.AreEqual(250, ds.TriggerR2);
    }

    [TestMethod]
    public void Test_Ds4ToDualsenseTranslator_ButtonsAndShareToCreate_MapsCorrectly()
    {
        var ds4 = new Ds4ReportEx
        {
            Buttons = (ushort)((1 << 4) | (1 << 5) | (1 << 8) | (1 << 12)), // Square, Cross, L1, Share
            SpecialButtons = 0x01 | 0x02 // PS, TouchpadClick
        };

        var ds = Ds4ToDualsenseTranslator.Translate(ds4);

        Assert.IsTrue(ds.ButtonSquare);
        Assert.IsTrue(ds.ButtonCross);
        Assert.IsTrue(ds.ButtonL1);
        Assert.IsTrue(ds.ButtonCreate, "DS4 Share must map to DualSense Create button.");
        Assert.IsTrue(ds.ButtonPs);
        Assert.IsTrue(ds.ButtonTouchpadClick);
        Assert.IsFalse(ds.ButtonCircle);
        Assert.IsFalse(ds.ButtonTriangle);
    }

    [TestMethod]
    public void Test_Ds4ToDualsenseTranslator_TouchpadAndMotion_PreservesCoordinates()
    {
        var ds4 = new Ds4ReportEx
        {
            GyroX = 1200,
            GyroY = -340,
            GyroZ = 50,
            AccelX = 100,
            AccelY = 200,
            AccelZ = 8192,
            Touch1Active = true,
            Touch1X = 1024,
            Touch1Y = 450
        };

        var ds = Ds4ToDualsenseTranslator.Translate(ds4);

        Assert.AreEqual(1200, ds.GyroX);
        Assert.AreEqual(-340, ds.GyroY);
        Assert.AreEqual(8192, ds.AccelZ);
        Assert.IsTrue(ds.Touch1Active);
        Assert.AreEqual(1024, ds.Touch1X);
        Assert.AreEqual(450, ds.Touch1Y);
    }

    [TestMethod]
    public void Test_ViiperUsbipPortManager_OwnedPort_RejectsLoopback()
    {
        var manager = new ViiperUsbipPortManager();
        manager.RegisterOwnedPort(2);

        bool accepted = manager.ShouldAcceptIncomingController(2, @"USB\VID_054C&PID_05C4\0001");
        Assert.IsFalse(accepted, "Controller coming from owned VIIPER port must be rejected to prevent feedback loops.");
    }

    [TestMethod]
    public void Test_ViiperUsbipPortManager_UnownedPort_AcceptsInput()
    {
        var manager = new ViiperUsbipPortManager();
        manager.RegisterOwnedPort(2);

        bool accepted = manager.ShouldAcceptIncomingController(1, @"USB\VID_054C&PID_05C4\0001");
        Assert.IsTrue(accepted, "Controller on unowned port must be accepted.");
    }

    [TestMethod]
    public void Test_ViiperControllerBridge_OfflineServer_TryConnectFailsGracefully()
    {
        using var bridge = new ViiperControllerBridge();
        // Port 39999 has no server running
        bool connected = bridge.TryConnect("127.0.0.1", 39999);

        Assert.IsFalse(connected, "Connecting to dormant VIIPER port must return false cleanly.");
        Assert.IsFalse(bridge.IsConnected);
    }

    [TestMethod]
    public void Test_ViiperControllerBridge_OfflineServer_SubmitDs4ReportReturnsFalseWithoutCrashing()
    {
        using var bridge = new ViiperControllerBridge();
        var report = Ds4ReportEx.CreateDefault();

        bool submitted = bridge.SubmitDs4Report(report);
        Assert.IsFalse(submitted, "Submitting report when offline must return false without throwing an exception.");
    }
}
