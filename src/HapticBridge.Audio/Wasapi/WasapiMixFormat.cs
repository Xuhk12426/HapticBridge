using System.Buffers.Binary;
using System.Numerics;

namespace HapticBridge.Audio.Wasapi;

public enum WasapiSampleEncoding
{
    Unknown,
    Pcm,
    Float
}

public sealed record WasapiMixFormat(
    int SampleRate, int Channels, int ContainerBits, int ValidBits, int BlockAlign, uint SpeakerMask, WasapiSampleEncoding Encoding)
{
    private static readonly Guid PcmSubtype = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid FloatSubtype = new("00000003-0000-0010-8000-00AA00389B71");
    internal const int HeaderBytes = 18;
    internal const int ExtendedBytes = 40;

    internal static WasapiMixFormat Parse(ReadOnlySpan<byte> format)
    {
        if (format.Length < HeaderBytes) throw new FormatException("Truncated WAVEFORMATEX.");

        ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(format);
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..]);
        int rate = BinaryPrimitives.ReadInt32LittleEndian(format[4..]);
        int align = BinaryPrimitives.ReadUInt16LittleEndian(format[12..]);
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(format[14..]);
        int validBits = bits;
        uint mask = 0;
        var encoding = tag switch
        {
            1 => WasapiSampleEncoding.Pcm,
            3 => WasapiSampleEncoding.Float,
            _ => WasapiSampleEncoding.Unknown
        };

        if (tag == 0xFFFE)
        {
            if (format.Length < ExtendedBytes || BinaryPrimitives.ReadUInt16LittleEndian(format[16..]) < ExtendedBytes - HeaderBytes)
                throw new FormatException("Truncated WAVEFORMATEXTENSIBLE.");

            validBits = BinaryPrimitives.ReadUInt16LittleEndian(format[18..]);
            mask = BinaryPrimitives.ReadUInt32LittleEndian(format[20..]);
            var subtype = new Guid(format[24..ExtendedBytes]);
            if (subtype == PcmSubtype) encoding = WasapiSampleEncoding.Pcm;
            if (subtype == FloatSubtype) encoding = WasapiSampleEncoding.Float;
        }

        return new WasapiMixFormat(rate, channels, bits, validBits, align, mask, encoding);
    }

    internal bool IsSupported =>
        SampleRate == 48000 && Channels is >= 4 and <= 18 && ValidBits == ContainerBits &&
        BlockAlign == Channels * (ContainerBits / 8) && BitOperations.PopCount(SpeakerMask) == Channels &&
        (SpeakerMask & ~0x3FFFFu) == 0 &&
        ((Encoding == WasapiSampleEncoding.Pcm && ContainerBits == 16) ||
         (Encoding == WasapiSampleEncoding.Float && ContainerBits == 32));

    public override string ToString() =>
        $"{SampleRate} Hz, {Channels} channels, {ContainerBits}-bit {Encoding}, mask 0x{SpeakerMask:X}";
}
