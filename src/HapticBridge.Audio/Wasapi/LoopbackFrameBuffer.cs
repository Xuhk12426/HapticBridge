namespace HapticBridge.Audio.Wasapi;

// Capture and render are serviced by the same event thread.
internal sealed class LoopbackFrameBuffer(HapticChannelMap map, int capacityFrames)
{
    private readonly byte[] _buffer = new byte[checked(capacityFrames * map.SourceFrameBytes)];
    private int _nextRead;
    private int _count;

    public long DroppedFrames { get; private set; }
    public long UnderrunFrames { get; private set; }

    public void Write(ReadOnlySpan<byte> frames)
    {
        int frameCount = frames.Length / map.SourceFrameBytes;
        int excess = Math.Max(0, _count + frameCount - capacityFrames);
        if (excess > 0)
        {
            DroppedFrames += excess;
            int discardQueued = Math.Min(excess, _count);
            _nextRead = (_nextRead + discardQueued) % capacityFrames;
            _count -= discardQueued;
            frames = frames[((excess - discardQueued) * map.SourceFrameBytes)..];
            frameCount -= excess - discardQueued;
        }

        int start = (_nextRead + _count) % capacityFrames;
        int first = Math.Min(frameCount, capacityFrames - start) * map.SourceFrameBytes;
        frames[..first].CopyTo(_buffer.AsSpan(start * map.SourceFrameBytes, first));
        frames[first..].CopyTo(_buffer);
        _count += frameCount;
    }

    public int Read(Span<byte> destination)
    {
        int requested = destination.Length / map.DestinationFrameBytes;
        int read = Math.Min(requested, _count);
        for (int i = 0; i < read; i++)
        {
            map.CopyFrame(_buffer.AsSpan(_nextRead * map.SourceFrameBytes, map.SourceFrameBytes),
                destination.Slice(i * map.DestinationFrameBytes, map.DestinationFrameBytes));
            _nextRead = (_nextRead + 1) % capacityFrames;
        }

        _count -= read;
        destination[(read * map.DestinationFrameBytes)..].Clear();
        UnderrunFrames += requested - read;
        return read;
    }
}
