using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using HapticBridge.Audio.Native;
using static HapticBridge.Audio.Wasapi.WasapiLoopbackRoute;

namespace HapticBridge.Audio.Wasapi;

/// <summary>Plays the DualSense front stereo audio on the selected Windows render endpoint.</summary>
public sealed class WasapiStereoPlayback : IDisposable
{
    private const int BlockBytes = 1920;
    private const int BytesPerFrame = 4;
    private const uint EventCallback = 0x00040000;
    private const uint AutoConvertPcm = 0x80000000;
    private const uint DefaultResamplingQuality = 0x08000000;
    private const uint Silent = 0x2;
    private readonly object _routing = new();
    private volatile PlaybackRoute? _route;
    private Task _worker = Task.CompletedTask;
    public string? Error => _route?.Error;
    public long RenderedFrames
    {
        get
        {
            var route = _route;
            return route is null ? 0 : Interlocked.Read(ref route.RenderedFrames);
        }
    }

    public void SelectOutput(string? endpointId)
    {
        lock (_routing)
        {
            if (string.Equals(_route?.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase)) return;
            if (_route is { Finished: false } previous) previous.Stop.Cancel();
            if (endpointId is null)
            {
                _route = null;
                return;
            }

            var route = new PlaybackRoute(endpointId);
            _route = route;
            _worker = PlayAfterAsync(_worker, route);
        }
    }

    public void Submit(ReadOnlySpan<byte> stereoPcm)
    {
        var route = _route;
        if (route is not null && !route.Finished && stereoPcm.Length == BlockBytes)
            route.Queue.Writer.TryWrite(stereoPcm.ToArray());
    }

    public void Dispose() => SelectOutput(null);

    private async Task PlayAfterAsync(Task previous, PlaybackRoute route)
    {
        try
        {
            await previous.ConfigureAwait(false);
            await Task.Factory.StartNew(() => Render(route), route.Stop.Token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (route.Stop.IsCancellationRequested) { }
        catch (Exception exception)
        {
            route.Error = exception.Message;
            Trace.TraceWarning("DualSense listening output stopped: {0}", exception);
        }
        finally
        {
            lock (_routing)
            {
                route.Finished = true;
                route.Queue.Writer.TryComplete();
                route.Stop.Dispose();
            }
        }
    }

    private unsafe void Render(PlaybackRoute route)
    {
        using var renderReady = new AutoResetEvent(false);
        using var output = new AudioClientEndpoint(route.EndpointId);

        // WAVEFORMATEX: 48 kHz stereo signed PCM. Shared mode mixes it to the
        // endpoint's format, including endpoints that run at another rate.
        Span<byte> format = stackalloc byte[18];
        format.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(format, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(format[2..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(format[4..], 48000);
        BinaryPrimitives.WriteUInt32LittleEndian(format[8..], 48000 * BytesPerFrame);
        BinaryPrimitives.WriteUInt16LittleEndian(format[12..], BytesPerFrame);
        BinaryPrimitives.WriteUInt16LittleEndian(format[14..], 16);
        fixed (byte* pointer = format)
            output.Initialize(EventCallback | AutoConvertPcm | DefaultResamplingQuality, renderReady, (nint)pointer);

        var render = output.GetService<IAudioRenderClient>();
        try
        {
            Check(output.Client.GetBufferSize(out uint capacity), "get listening buffer size");
            Check(render.GetBuffer(capacity, out _), "get initial listening buffer");
            Check(render.ReleaseBuffer(capacity, Silent), "prime listening output");
            route.Stop.Token.ThrowIfCancellationRequested();
            Check(output.Client.Start(), "start the listening output");
            try
            {
                WaitHandle[] events = [route.Stop.Token.WaitHandle, renderReady];
                while (!route.Stop.IsCancellationRequested)
                {
                    int signal = WaitHandle.WaitAny(events, 3000);
                    if (signal == 0) break;
                    if (signal == WaitHandle.WaitTimeout) throw new IOException("The listening device stopped responding.");
                    Check(output.Client.GetCurrentPadding(out uint padding), "query listening buffer padding");
                    if (padding >= capacity) continue;
                    uint frames = capacity - padding;
                    Check(render.GetBuffer(frames, out nint buffer), "get listening buffer");
                    uint releaseFlags = Silent;
                    try
                    {
                        if (buffer == 0) throw new IOException("The listening device returned an empty buffer.");
                        var samples = new Span<byte>((void*)buffer, checked((int)frames * BytesPerFrame));
                        int written = route.Fill(samples);
                        if (written == 0) continue;
                        Interlocked.Add(ref route.RenderedFrames, written / BytesPerFrame);
                        releaseFlags = 0;
                    }
                    finally { Check(render.ReleaseBuffer(frames, releaseFlags), "release listening buffer"); }
                }
            }
            finally { output.Client.Stop(); }
        }
        finally { Marshal.ReleaseComObject(render); }
    }

    private sealed class PlaybackRoute(string endpointId)
    {
        private byte[]? _buffer;
        private int _offset;
        public string EndpointId { get; } = endpointId;
        public CancellationTokenSource Stop { get; } = new();
        public volatile bool Finished;
        public volatile string? Error;
        public long RenderedFrames;
        public Channel<byte[]> Queue { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(6)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });

        public int Fill(Span<byte> destination)
        {
            int written = 0;
            while (written < destination.Length)
            {
                if (_buffer is null || _offset == _buffer.Length)
                {
                    if (!Queue.Reader.TryRead(out _buffer)) break;
                    _offset = 0;
                }
                int bytes = Math.Min(destination.Length - written, _buffer.Length - _offset);
                _buffer.AsSpan(_offset, bytes).CopyTo(destination[written..]);
                written += bytes;
                _offset += bytes;
            }
            destination[written..].Clear();
            return written;
        }
    }
}
