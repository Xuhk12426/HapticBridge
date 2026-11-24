using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using HapticBridge.Core.Models;
using HapticBridge.Core.Services;
using HapticBridge.Integrations.Apollo;

namespace HapticBridge.Integrations.Controllers;

public class ControllerDetectionService : IControllerDetectionService
{
    private readonly IAudioEndpointService _audio;
    private readonly Func<IReadOnlyList<UsbDevice>> _readUsbDevices;
    private readonly Func<bool> _apolloRunning;
    private readonly Func<bool> _viiperRunning;

    public ControllerDetectionService(IAudioEndpointService audioEndpointService)
        : this(audioEndpointService, ReadUsbDevices, () => new ApolloIntegration().IsRunning, () =>
        {
            var processes = Process.GetProcessesByName("viiper");
            foreach (var process in processes) process.Dispose();
            return processes.Length > 0;
        })
    {
    }

    internal ControllerDetectionService(IAudioEndpointService audio, Func<IReadOnlyList<UsbDevice>> readUsbDevices,
        Func<bool> apolloRunning, Func<bool> viiperRunning)
    {
        _audio = audio;
        _readUsbDevices = readUsbDevices;
        _apolloRunning = apolloRunning;
        _viiperRunning = viiperRunning;
    }

    public ControllerDetectionResult DetectControllers()
    {
        var result = new ControllerDetectionResult();
        var pipeline = result.PipelineStatus;
        pipeline.IsApolloRunning = _apolloRunning();
        pipeline.IsViiperRunning = _viiperRunning();
        pipeline.IsDualSenseAudioEndpointPresent = _audio.GetVirtualDualSenseEndpoint() is { IsActive: true };

        IReadOnlyList<UsbDevice> devices;
        try
        {
            devices = _readUsbDevices();
        }
        catch (Exception exception) when (exception is ManagementException or COMException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("HapticBridge PnP inspection failed: {0}", exception);
            result.MainStatus = "Controller status unavailable";
            result.Subtitle = "Windows device inspection failed.";
            return result;
        }

        var ds4 = devices.Where(device => device.Bus == UsbBus.ViGEm &&
                (device.IsProduct("VID_054C&PID_05C4") || device.IsProduct("VID_054C&PID_09CC")))
            .DistinctBy(device => device.InstanceId, StringComparer.OrdinalIgnoreCase).ToArray();
        var dualSense = devices.FirstOrDefault(device => device.Bus == UsbBus.UsbIp &&
            (device.IsProduct("VID_054C&PID_0CE6") || device.IsProduct("VID_054C&PID_0DF2")));
        var kishi = devices.FirstOrDefault(device => device.Bus == UsbBus.Physical &&
            device.InstanceId.StartsWith(@"USB\VID_1532&PID_", StringComparison.OrdinalIgnoreCase) &&
            device.Name.Contains("Kishi", StringComparison.OrdinalIgnoreCase));

        pipeline.VirtualDS4CandidateCount = ds4.Length;
        pipeline.IsVirtualDS4Present = result.IsVirtualDS4Detected = ds4.Length > 0;
        if (ds4.Length == 1) pipeline.VirtualDS4InstanceId = ds4[0].InstanceId;

        pipeline.IsVirtualDualSensePresent = result.IsVirtualDualSenseDetected = dualSense is not null;
        result.RawDeviceName = dualSense?.Name ?? string.Empty;
        result.DeviceId = dualSense?.InstanceId ?? string.Empty;
        result.IsKishiDetected = kishi is not null;

        if (kishi is not null)
        {
            pipeline.PhysicalAttribution = PhysicalControllerAttribution.DirectHardwareAttached;
            pipeline.PhysicalDeviceDescription = kishi.Name;
            result.MainStatus = "Kishi detected via USB";
        }
        else if (ds4.Length > 1)
            result.MainStatus = "Ambiguous virtual DS4";
        else if (dualSense is not null)
            result.MainStatus = "USB/IP DualSense detected";
        else if (ds4.Length == 1)
            result.MainStatus = "ViGEm DS4 candidate";

        result.Subtitle = ds4.Length switch
        {
            > 1 => $"{ds4.Length} ViGEm DS4 devices found. Apollo ownership is unverified.",
            _ when dualSense is not null && pipeline.IsDualSenseAudioEndpointPresent => "Audio endpoint candidate available. Haptic audio flow is unverified.",
            _ when dualSense is not null => "Waiting for a DualSense audio endpoint.",
            1 => "Apollo session and remote controller identity are unverified.",
            _ when pipeline.IsApolloRunning => "Apollo running. Remote controller status is unverified.",
            _ => "No supported virtual controller observed."
        };
        return result;
    }

    private static IReadOnlyList<UsbDevice> ReadUsbDevices()
    {
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE Present = TRUE AND " +
            "(DeviceID LIKE 'USB%VID_054C%' OR DeviceID LIKE 'USB%VID_1532%')");
        using var matches = searcher.Get();
        var devices = new List<UsbDevice>();
        foreach (ManagementObject device in matches)
        {
            using (device)
            {
                if (device["DeviceID"] is not string id) continue;
                var parts = id.Split('\\');
                if (parts.Length != 3 || parts[0] != "USB" || parts[1].Contains("&MI_", StringComparison.OrdinalIgnoreCase))
                    continue;

                devices.Add(new UsbDevice(id, device["Name"] as string ?? string.Empty, ReadUsbBus(id)));
            }
        }

        return devices;
    }

    internal static UsbBus ReadUsbBus(string instanceId)
    {
        if (CM_Locate_DevNodeW(out uint node, instanceId, 0) != 0) return UsbBus.Unknown;

        const uint serviceProperty = 5; // CM_DRP_SERVICE
        var service = new StringBuilder(256);
        bool physicalHost = false;
        while (CM_Get_Parent(out uint parent, node, 0) == 0)
        {
            node = parent;
            uint bytes = (uint)service.Capacity * sizeof(char);
            if (CM_Get_DevNode_Registry_PropertyW(node, serviceProperty, out _, service, ref bytes, 0) != 0)
                continue;

            switch (service.ToString().ToUpperInvariant())
            {
                case "VIGEMBUS": return UsbBus.ViGEm;
                case "USBIP2_UDE":
                case "USBIP_UDE": return UsbBus.UsbIp;
                case "USBXHCI":
                case "USBEHCI": physicalHost = true; break;
            }
        }

        return physicalHost ? UsbBus.Physical : UsbBus.Unknown;
    }

    internal enum UsbBus { Unknown, Physical, ViGEm, UsbIp }

    internal sealed record UsbDevice(string InstanceId, string Name, UsbBus Bus)
    {
        public bool IsProduct(string hardwareId)
        {
            string prefix = $@"USB\{hardwareId}";
            return InstanceId.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase) ||
                InstanceId.StartsWith(prefix + "&REV_", StringComparison.OrdinalIgnoreCase);
        }
    }

    [DllImport("cfgmgr32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Locate_DevNodeW(out uint device, string instanceId, uint flags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true)]
    private static extern uint CM_Get_Parent(out uint parent, uint device, uint flags);

    [DllImport("cfgmgr32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_DevNode_Registry_PropertyW(uint device, uint property, out uint type,
        [Out] StringBuilder value, ref uint size, uint flags);
}
