using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace HapticBridge.Integrations.Apollo;

/// <summary>
/// Strongly-typed representation of Sony DualShock 4 input report emitted by ViGEmBus (DS4_REPORT_EX).
/// </summary>
public class Ds4ReportEx
{
    // Analog Sticks (0 - 255, 128 = Center)
    public byte ThumbLX { get; set; } = 128;
    public byte ThumbLY { get; set; } = 128;
    public byte ThumbRX { get; set; } = 128;
    public byte ThumbRY { get; set; } = 128;

    // Analog Triggers (0 - 255)
    public byte TriggerL { get; set; }
    public byte TriggerR { get; set; }

    // Buttons
    public ushort Buttons { get; set; }
    public byte SpecialButtons { get; set; }
    public ushort RawTimestamp { get; set; }

    // D-Pad (0 = N, 1 = NE, 2 = E, 3 = SE, 4 = S, 5 = SW, 6 = W, 7 = NW, 8 = Neutral)
    public byte DPad => (byte)(Buttons & 0x0F);

    // Standard Buttons (Decoded from Buttons bitmask)
    public bool ButtonSquare => (Buttons & (1 << 4)) != 0;
    public bool ButtonCross => (Buttons & (1 << 5)) != 0;
    public bool ButtonCircle => (Buttons & (1 << 6)) != 0;
    public bool ButtonTriangle => (Buttons & (1 << 7)) != 0;
    public bool ButtonL1 => (Buttons & (1 << 8)) != 0;
    public bool ButtonR1 => (Buttons & (1 << 9)) != 0;
    public bool ButtonL2 => (Buttons & (1 << 10)) != 0;
    public bool ButtonR2 => (Buttons & (1 << 11)) != 0;
    public bool ButtonShare => (Buttons & (1 << 12)) != 0;
    public bool ButtonOptions => (Buttons & (1 << 13)) != 0;
    public bool ButtonL3 => (Buttons & (1 << 14)) != 0;
    public bool ButtonR3 => (Buttons & (1 << 15)) != 0;

    // Special Buttons
    public bool ButtonPs => (SpecialButtons & 0x01) != 0;
    public bool ButtonTouchpadClick => (SpecialButtons & 0x02) != 0;

    // 6-Axis Motion Data
    public short GyroX { get; set; }
    public short GyroY { get; set; }
    public short GyroZ { get; set; }
    public short AccelX { get; set; }
    public short AccelY { get; set; }
    public short AccelZ { get; set; }

    // Touchpad Data
    public bool Touch1Active { get; set; }
    public byte Touch1TrackingId { get; set; }
    public ushort Touch1X { get; set; }
    public ushort Touch1Y { get; set; }
    public bool Touch2Active { get; set; }
    public byte Touch2TrackingId { get; set; }
    public ushort Touch2X { get; set; }
    public ushort Touch2Y { get; set; }

    public static Ds4ReportEx CreateDefault()
    {
        return new Ds4ReportEx
        {
            ThumbLX = 128,
            ThumbLY = 128,
            ThumbRX = 128,
            ThumbRY = 128,
            Buttons = 0x08 // Neutral D-pad
        };
    }
}

/// <summary>
/// Parser for raw DS4 HID input reports emitted by ViGEmBus and Apollo.
/// </summary>
public static class Ds4ReportParser
{
    public static Ds4ReportEx Parse(ReadOnlySpan<byte> report) =>
        TryParse(report, out var result) ? result : Ds4ReportEx.CreateDefault();

    public static bool TryParse(ReadOnlySpan<byte> report, [NotNullWhen(true)] out Ds4ReportEx? result)
    {
        const int payloadLength = 63;
        const int touchPacketCountOffset = 32;
        result = null;

        // USB report ID 1 precedes the packed ViGEm DS4_REPORT_EX payload.
        if (report.Length == payloadLength + 1 && report[0] == 1) report = report[1..];
        if (report.Length != payloadLength || (report[4] & 0x0F) > 8 || report[touchPacketCountOffset] > 3)
            return false;

        result = new Ds4ReportEx
        {
            ThumbLX = report[0],
            ThumbLY = report[1],
            ThumbRX = report[2],
            ThumbRY = report[3],
            Buttons = BinaryPrimitives.ReadUInt16LittleEndian(report[4..]),
            SpecialButtons = report[6],
            TriggerL = report[7],
            TriggerR = report[8],
            RawTimestamp = BinaryPrimitives.ReadUInt16LittleEndian(report[9..]),
            GyroX = BinaryPrimitives.ReadInt16LittleEndian(report[12..]),
            GyroY = BinaryPrimitives.ReadInt16LittleEndian(report[14..]),
            GyroZ = BinaryPrimitives.ReadInt16LittleEndian(report[16..]),
            AccelX = BinaryPrimitives.ReadInt16LittleEndian(report[18..]),
            AccelY = BinaryPrimitives.ReadInt16LittleEndian(report[20..]),
            AccelZ = BinaryPrimitives.ReadInt16LittleEndian(report[22..])
        };

        if (report[touchPacketCountOffset] > 0)
        {
            (result.Touch1Active, result.Touch1TrackingId, result.Touch1X, result.Touch1Y) = ReadContact(report[34..]);
            (result.Touch2Active, result.Touch2TrackingId, result.Touch2X, result.Touch2Y) = ReadContact(report[38..]);
        }

        return true;
    }

    private static (bool Active, byte TrackingId, ushort X, ushort Y) ReadContact(ReadOnlySpan<byte> contact)
    {
        bool active = (contact[0] & 0x80) == 0;
        byte id = (byte)(contact[0] & 0x7F);
        ushort x = (ushort)(contact[1] | ((contact[2] & 0x0F) << 8));
        ushort y = (ushort)((contact[2] >> 4) | (contact[3] << 4));
        return (active, id, x, y);
    }
}

/// <summary>
/// Service interface for reading and streaming raw DS4 HID reports from Apollo's virtual DS4 device.
/// </summary>
public interface IApolloHidInputReader : IDisposable
{
    bool IsConnected { get; }
    string? DevicePath { get; }
    event Action<Ds4ReportEx>? ReportReceived;
    bool TryConnect(string? devicePath = null);
    void Disconnect();
    void FeedRawReport(ReadOnlySpan<byte> report);
}

/// <summary>
/// Software report harness. The live HID reader requires a verified Apollo device interface.
/// </summary>
public class ApolloHidInputReader : IApolloHidInputReader
{
    public bool IsConnected => false;

    public string? DevicePath => null;

    public event Action<Ds4ReportEx>? ReportReceived;

    public bool TryConnect(string? devicePath = null) => false;

    public void Disconnect() { }

    /// <summary>
    /// Feeds a raw report directly to the parser and dispatches the event.
    /// Used by streaming loops and unit test harnesses.
    /// </summary>
    public void FeedRawReport(ReadOnlySpan<byte> report)
    {
        if (Ds4ReportParser.TryParse(report, out var parsed)) ReportReceived?.Invoke(parsed);
    }

    public void Dispose()
    {
        ReportReceived = null;
    }
}
