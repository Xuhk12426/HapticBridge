using System.Numerics;

namespace HapticBridge.Audio.Wasapi;

internal sealed class HapticChannelMap
{
    private readonly int[] _destinationSlots;
    private readonly int _sampleBytes;

    public HapticChannelMap(WasapiMixFormat source, WasapiMixFormat destination)
    {
        // Front pair plus an identified back or side pair. Reject stereo and unnamed lanes.
        if (!source.IsSupported || source.Channels != 4 || source.SpeakerMask is not (0x33 or 0x603))
            throw new NotSupportedException($"Expected four 48 kHz DualSense channels with named front and rear lanes. Observed {source}.");
        if (!destination.IsSupported)
            throw new NotSupportedException($"VB-CABLE must expose at least four named channels in its Windows 48 kHz mix. Observed {destination}.");
        if (destination.Encoding != source.Encoding || destination.ContainerBits != source.ContainerBits)
            throw new NotSupportedException($"VB-CABLE must use the same 48 kHz sample encoding. Observed {destination}.");
        if ((destination.SpeakerMask & source.SpeakerMask) != source.SpeakerMask)
            throw new NotSupportedException("VB-CABLE does not expose all four matching speaker positions.");

        SourceFrameBytes = source.BlockAlign;
        DestinationFrameBytes = destination.BlockAlign;
        _sampleBytes = source.ContainerBits / 8;
        _destinationSlots = new int[source.Channels];
        uint remaining = source.SpeakerMask;
        for (int i = 0; i < _destinationSlots.Length; i++)
        {
            uint channel = 1u << BitOperations.TrailingZeroCount(remaining);
            _destinationSlots[i] = BitOperations.PopCount(destination.SpeakerMask & (channel - 1));
            remaining &= ~channel;
        }
    }

    public int SourceFrameBytes { get; }
    public int DestinationFrameBytes { get; }
    public int[] GetDestinationChannels() => _destinationSlots.Select(slot => slot + 1).ToArray();

    public void CopyFrame(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        destination.Clear();
        for (int i = 0; i < _destinationSlots.Length; i++)
        {
            source.Slice(i * _sampleBytes, _sampleBytes)
                .CopyTo(destination.Slice(_destinationSlots[i] * _sampleBytes, _sampleBytes));
        }
    }
}
