using HapticBridge.Integrations.Apollo;

namespace HapticBridge.Integrations.VIIPER;

/// <summary>
/// Normalized DualSense controller input structure for VIIPER virtual device emulation.
/// </summary>
public class DualsenseInput
{
    // Analog Sticks (0 - 255, 128 = Center)
    public byte ThumbLX { get; set; } = 128;
    public byte ThumbLY { get; set; } = 128;
    public byte ThumbRX { get; set; } = 128;
    public byte ThumbRY { get; set; } = 128;

    // Analog Triggers (0 - 255)
    public byte TriggerL2 { get; set; }
    public byte TriggerR2 { get; set; }

    // D-Pad (0 = N, 1 = NE, 2 = E, 3 = SE, 4 = S, 5 = SW, 6 = W, 7 = NW, 8 = Neutral)
    public byte DPad { get; set; } = 8;

    // Face Buttons
    public bool ButtonSquare { get; set; }
    public bool ButtonCross { get; set; }
    public bool ButtonCircle { get; set; }
    public bool ButtonTriangle { get; set; }

    // Shoulder & Trigger Digital Clicks
    public bool ButtonL1 { get; set; }
    public bool ButtonR1 { get; set; }
    public bool ButtonL2 { get; set; }
    public bool ButtonR2 { get; set; }

    // Menu / Navigation Buttons
    public bool ButtonCreate { get; set; } // Mapped from DS4 Share
    public bool ButtonOptions { get; set; }
    public bool ButtonL3 { get; set; }
    public bool ButtonR3 { get; set; }
    public bool ButtonPs { get; set; }
    public bool ButtonTouchpadClick { get; set; }
    public bool ButtonMicMute { get; set; }

    // 6-Axis Motion Data
    public short GyroX { get; set; }
    public short GyroY { get; set; }
    public short GyroZ { get; set; }
    public short AccelX { get; set; }
    public short AccelY { get; set; }
    public short AccelZ { get; set; }

    // Multi-touch Touchpad
    public bool Touch1Active { get; set; }
    public ushort Touch1X { get; set; }
    public ushort Touch1Y { get; set; }
    public bool Touch2Active { get; set; }
    public ushort Touch2X { get; set; }
    public ushort Touch2Y { get; set; }

    public static DualsenseInput CreateDefault()
    {
        return new DualsenseInput
        {
            ThumbLX = 128,
            ThumbLY = 128,
            ThumbRX = 128,
            ThumbRY = 128,
            DPad = 8
        };
    }
}

/// <summary>
/// High-performance zero-allocation translator mapping Apollo DS4 reports to VIIPER DualSense input frames.
/// </summary>
public static class Ds4ToDualsenseTranslator
{
    public static DualsenseInput Translate(Ds4ReportEx ds4)
    {
        var ds = DualsenseInput.CreateDefault();
        if (ds4 == null) return ds;

        // Sticks
        ds.ThumbLX = ds4.ThumbLX;
        ds.ThumbLY = ds4.ThumbLY;
        ds.ThumbRX = ds4.ThumbRX;
        ds.ThumbRY = ds4.ThumbRY;

        // Triggers
        ds.TriggerL2 = ds4.TriggerL;
        ds.TriggerR2 = ds4.TriggerR;

        // D-Pad
        ds.DPad = ds4.DPad;

        // Face Buttons
        ds.ButtonSquare = ds4.ButtonSquare;
        ds.ButtonCross = ds4.ButtonCross;
        ds.ButtonCircle = ds4.ButtonCircle;
        ds.ButtonTriangle = ds4.ButtonTriangle;

        // Shoulder & Triggers
        ds.ButtonL1 = ds4.ButtonL1;
        ds.ButtonR1 = ds4.ButtonR1;
        ds.ButtonL2 = ds4.ButtonL2 || ds4.TriggerL > 10;
        ds.ButtonR2 = ds4.ButtonR2 || ds4.TriggerR > 10;

        // Navigation (Share -> Create)
        ds.ButtonCreate = ds4.ButtonShare;
        ds.ButtonOptions = ds4.ButtonOptions;
        ds.ButtonL3 = ds4.ButtonL3;
        ds.ButtonR3 = ds4.ButtonR3;
        ds.ButtonPs = ds4.ButtonPs;
        ds.ButtonTouchpadClick = ds4.ButtonTouchpadClick;

        // Motion Sensors
        ds.GyroX = ds4.GyroX;
        ds.GyroY = ds4.GyroY;
        ds.GyroZ = ds4.GyroZ;
        ds.AccelX = ds4.AccelX;
        ds.AccelY = ds4.AccelY;
        ds.AccelZ = ds4.AccelZ;

        // Touchpad
        ds.Touch1Active = ds4.Touch1Active;
        ds.Touch1X = ds4.Touch1X;
        ds.Touch1Y = ds4.Touch1Y;
        ds.Touch2Active = ds4.Touch2Active;
        ds.Touch2X = ds4.Touch2X;
        ds.Touch2Y = ds4.Touch2Y;

        return ds;
    }
}

/// <summary>
/// Tracks active USBIP ports allocated to VIIPER to prevent feedback loops
/// (where HapticBridge re-ingests its own virtual DualSense as input).
/// </summary>
public class ViiperUsbipPortManager
{
    private readonly HashSet<int> _ownedPorts = [];
    private readonly object _lock = new();

    public void RegisterOwnedPort(int port)
    {
        lock (_lock)
        {
            _ownedPorts.Add(port);
        }
    }

    public void UnregisterOwnedPort(int port)
    {
        lock (_lock)
        {
            _ownedPorts.Remove(port);
        }
    }

    public bool IsPortOwned(int port)
    {
        lock (_lock)
        {
            return _ownedPorts.Contains(port);
        }
    }

    public bool ShouldAcceptIncomingController(int port, string deviceId)
    {
        // 1. Rejects controller if attached on an owned VIIPER USBIP port
        if (port > 0 && IsPortOwned(port))
        {
            return false;
        }

        // 2. Reject if device ID indicates self-emulated VIIPER DualSense node
        if (deviceId.Contains("USBIP", StringComparison.OrdinalIgnoreCase) &&
            deviceId.Contains("PID_0CE6", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }
}
