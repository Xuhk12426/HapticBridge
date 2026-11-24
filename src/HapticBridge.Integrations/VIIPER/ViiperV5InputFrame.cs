using System.Buffers.Binary;
using HapticBridge.Integrations.Apollo;

namespace HapticBridge.Integrations.VIIPER;

// Mapped input (33 bytes) for the VIIPER v0.1.9-rc4.6.6 DualSense V5 stream.
// This writes wire bytes only. Opening an audio-capable device and sending its stream is a separate lifecycle.
internal static class ViiperV5InputFrame
{
    private const int HeaderSize = 16;
    private const int PayloadSize = 33;
    public const int Size = HeaderSize + PayloadSize;
    private static readonly byte[] DPadValues = [1, 9, 8, 10, 2, 6, 4, 5, 0];

    public static void Write(Ds4ReportEx input, uint sequence, Span<byte> frame)
    {
        if (frame.Length < Size) throw new ArgumentException("A complete V5 frame requires 49 bytes.", nameof(frame));
        if (input.DPad >= DPadValues.Length) throw new ArgumentException("Invalid DS4 D-pad position.", nameof(input));

        "VPCM"u8.CopyTo(frame);
        frame[4] = 5;
        frame[5] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(frame[6..], PayloadSize);
        BinaryPrimitives.WriteUInt32LittleEndian(frame[8..], sequence);

        var payload = frame.Slice(HeaderSize, PayloadSize);
        payload[0] = unchecked((byte)(input.ThumbLX - 128));
        payload[1] = unchecked((byte)(input.ThumbLY - 128));
        payload[2] = unchecked((byte)(input.ThumbRX - 128));
        payload[3] = unchecked((byte)(input.ThumbRY - 128));
        uint buttons = (uint)(input.Buttons & 0xFFF0) | ((uint)(input.SpecialButtons & 0x03) << 16);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], buttons);
        payload[8] = DPadValues[input.DPad];
        payload[9] = input.TriggerL;
        payload[10] = input.TriggerR;
        WriteTouch(payload[11..], input.Touch1X, input.Touch1Y, input.Touch1Active, input.Touch1TrackingId);
        WriteTouch(payload[16..], input.Touch2X, input.Touch2Y, input.Touch2Active, input.Touch2TrackingId);
        BinaryPrimitives.WriteInt16LittleEndian(payload[21..], input.GyroX);
        BinaryPrimitives.WriteInt16LittleEndian(payload[23..], input.GyroY);
        BinaryPrimitives.WriteInt16LittleEndian(payload[25..], input.GyroZ);
        BinaryPrimitives.WriteInt16LittleEndian(payload[27..], input.AccelX);
        BinaryPrimitives.WriteInt16LittleEndian(payload[29..], input.AccelY);
        BinaryPrimitives.WriteInt16LittleEndian(payload[31..], input.AccelZ);

        BinaryPrimitives.WriteUInt32LittleEndian(frame[12..], Checksum(frame[4..12], payload));
    }

    private static void WriteTouch(Span<byte> target, ushort x, ushort y, bool active, byte tracking)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(target, x);
        BinaryPrimitives.WriteUInt16LittleEndian(target[2..], y);
        target[4] = (byte)((tracking & 0x7F) | (active ? 0 : 0x80));
    }

    internal static uint Checksum(ReadOnlySpan<byte> header, ReadOnlySpan<byte> payload) =>
        ~AppendCrc32(AppendCrc32(uint.MaxValue, header), payload);

    private static uint AppendCrc32(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u);
        }
        return crc;
    }
}
