using System.Runtime.InteropServices;
using HapticBridge.Audio.Native;
using HapticBridge.Core.Models;

namespace HapticBridge.Audio.Wasapi;

public sealed record WasapiRouteProbe(
    WasapiMixFormat? SourceFormat, WasapiMixFormat? DestinationFormat, int[] DestinationChannels, string Detail);

public sealed record WasapiRouteCounters(long CapturedFrames, long ForwardedFrames, long DroppedFrames, long UnderrunFrames, long Discontinuities);

/// <summary>
/// Isolated shared-mode loopback proof of concept. Probe opens endpoint objects only.
/// Running the route opens audio streams and is reserved for deliberate integration tests.
/// </summary>
public sealed class WasapiLoopbackRoute
{
    private const uint EventCallback = 0x00040000;
    private const uint Loopback = 0x00020000;
    private const uint Discontinuity = 0x00000001;
    private const uint Silent = 0x00000002;
    private const int BufferFrames = 2880; // 60 ms at 48 kHz

    public WasapiRouteProbe Probe(AudioEndpointModel source, AudioEndpointModel destination)
    {
        WasapiMixFormat? sourceFormat = null;
        WasapiMixFormat? destinationFormat = null;
        try
        {
            CheckEndpoints(source, destination);
            sourceFormat = ReadMixFormat(source.Id);
            destinationFormat = ReadMixFormat(destination.Id);
            var map = new HapticChannelMap(sourceFormat, destinationFormat);
            return new(sourceFormat, destinationFormat, map.GetDestinationChannels(),
                "Mix formats can carry the same four speaker positions. Signal flow and Voicemeeter routing remain unverified.");
        }
        catch (Exception exception) when (exception is COMException or FormatException or NotSupportedException or ArgumentException)
        {
            return new(sourceFormat, destinationFormat, [], exception.Message);
        }
    }

    public WasapiMixFormat ReadMixFormat(string endpointId)
    {
        using var endpoint = new AudioClientEndpoint(endpointId);
        return endpoint.Format;
    }

    public Task<WasapiRouteCounters> RunAsync(AudioEndpointModel source, AudioEndpointModel destination, CancellationToken stop)
    {
        CheckEndpoints(source, destination);
        string sourceId = source.Id;
        string destinationId = destination.Id;
        return Task.Factory.StartNew(() => Run(sourceId, destinationId, stop),
            stop, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private static WasapiRouteCounters Run(string sourceId, string destinationId, CancellationToken stop)
    {
        using var captureReady = new AutoResetEvent(false);
        using var renderReady = new AutoResetEvent(false);
        using var source = new AudioClientEndpoint(sourceId);
        using var destination = new AudioClientEndpoint(destinationId);
        var map = new HapticChannelMap(source.Format, destination.Format);
        var queue = new LoopbackFrameBuffer(map, BufferFrames);
        source.Initialize(Loopback | EventCallback, captureReady);
        destination.Initialize(EventCallback, renderReady);
        var capture = source.GetService<IAudioCaptureClient>();
        try
        {
            var render = destination.GetService<IAudioRenderClient>();
            try
            {
                return StreamAudio(source, destination, capture, render, queue,
                    [stop.WaitHandle, captureReady, renderReady], stop);
            }
            finally { Marshal.ReleaseComObject(render); }
        }
        finally { Marshal.ReleaseComObject(capture); }
    }

    private static unsafe WasapiRouteCounters StreamAudio(AudioClientEndpoint source, AudioClientEndpoint destination,
        IAudioCaptureClient capture, IAudioRenderClient render, LoopbackFrameBuffer queue,
        WaitHandle[] events, CancellationToken stop)
    {
        Check(source.Client.GetBufferSize(out uint captureCapacity), "get loopback buffer size");
        Check(destination.Client.GetBufferSize(out uint renderCapacity), "get render buffer size");
        var silence = new byte[checked((int)captureCapacity * source.Format.BlockAlign)];
        long captured = 0, forwarded = 0, discontinuities = 0;
        bool captureStarted = false, renderStarted = false;
        try
        {
            Check(render.GetBuffer(renderCapacity, out _), "get initial render buffer");
            Check(render.ReleaseBuffer(renderCapacity, Silent), "prime render with silence");
            if (!stop.IsCancellationRequested)
            {
                Check(source.Client.Start(), "start loopback capture");
                captureStarted = true;
                Check(destination.Client.Start(), "start VB-CABLE render");
                renderStarted = true;
            }

            while (!stop.IsCancellationRequested)
            {
                int signal = WaitHandle.WaitAny(events, 3000);
                if (signal == 0) break;
                if (signal == WaitHandle.WaitTimeout) throw new IOException("WASAPI stopped signaling its audio buffers.");

                // Drain capture first so simultaneous notifications do not render stale silence.
                while (!stop.IsCancellationRequested)
                {
                    Check(capture.GetNextPacketSize(out uint packetFrames), "query loopback packet");
                    if (packetFrames == 0) break;
                    Check(capture.GetBuffer(out nint data, out uint frames, out uint flags, out _, out _), "lock loopback packet");
                    try
                    {
                        if (frames == 0) break;
                        int bytes = checked((int)frames * source.Format.BlockAlign);
                        if (frames > captureCapacity || (data == 0 && (flags & Silent) == 0))
                            throw new IOException("WASAPI returned an invalid loopback packet.");
                        if ((flags & Discontinuity) != 0) discontinuities++;
                        // The queue copies the packet before releasing the driver-owned buffer.
                        queue.Write((flags & Silent) != 0 ? silence.AsSpan(0, bytes) : new ReadOnlySpan<byte>((void*)data, bytes));
                        captured += frames;
                    }
                    finally { Check(capture.ReleaseBuffer(frames), "release loopback packet"); }
                }

                if (signal != 2 || stop.IsCancellationRequested) continue;
                Check(destination.Client.GetCurrentPadding(out uint padding), "read render padding");
                if (padding >= renderCapacity) continue;
                uint available = renderCapacity - padding;
                Check(render.GetBuffer(available, out nint output), "lock VB-CABLE render buffer");
                uint releaseFlags = Silent;
                try
                {
                    if (output == 0) throw new IOException("WASAPI returned an empty render buffer.");
                    int bytes = checked((int)available * destination.Format.BlockAlign);
                    int frames = queue.Read(new Span<byte>((void*)output, bytes));
                    forwarded += frames;
                    releaseFlags = frames == 0 ? Silent : 0;
                }
                finally { Check(render.ReleaseBuffer(available, releaseFlags), "release VB-CABLE render buffer"); }
            }
        }
        finally
        {
            if (renderStarted) destination.Client.Stop();
            if (captureStarted) source.Client.Stop();
        }
        return new(captured, forwarded, queue.DroppedFrames, queue.UnderrunFrames, discontinuities);
    }

    private static void CheckEndpoints(AudioEndpointModel source, AudioEndpointModel destination)
    {
        if (!source.IsActive || source.EndpointType != AudioEndpointType.VirtualDualSense ||
            !destination.IsActive || destination.EndpointType != AudioEndpointType.VirtualCable ||
            string.IsNullOrWhiteSpace(source.Id) || string.IsNullOrWhiteSpace(destination.Id) ||
            string.Equals(source.Id, destination.Id, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Select distinct active DualSense and VB-CABLE playback endpoints.");
    }

    internal static void Check(int result, string operation)
    {
        if (result < 0) throw new COMException($"WASAPI could not {operation} (0x{result:X8}).", result);
    }

    internal sealed class AudioClientEndpoint : IDisposable
    {
        private object? _clientObject;
        private nint _format;

        public IAudioClient Client { get; }
        public WasapiMixFormat Format { get; }

        public AudioClientEndpoint(string endpointId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            try
            {
                Check(enumerator.GetDevice(endpointId, out var device), "open the specified endpoint");
                try
                {
                    Check(device.GetState(out int state), "read the endpoint state");
                    if (state != CoreAudioConstants.DEVICE_STATE_ACTIVE) throw new NotSupportedException("The audio endpoint is inactive.");
                    var iid = typeof(IAudioClient).GUID;
                    Check(device.Activate(ref iid, 1, 0, out var native), "activate IAudioClient");
                    _clientObject = native;
                    Client = (IAudioClient)native;
                    Check(Client.GetMixFormat(out _format), "read the endpoint mix format");
                    if (_format == 0) throw new FormatException("WASAPI returned no format descriptor.");
                    int length = unchecked((ushort)Marshal.ReadInt16(_format)) == 0xFFFE &&
                        Marshal.ReadInt16(_format, 16) >= WasapiMixFormat.ExtendedBytes - WasapiMixFormat.HeaderBytes
                        ? WasapiMixFormat.ExtendedBytes : WasapiMixFormat.HeaderBytes;
                    var bytes = new byte[length];
                    Marshal.Copy(_format, bytes, 0, length);
                    Format = WasapiMixFormat.Parse(bytes);
                }
                finally { Marshal.ReleaseComObject(device); }
            }
            catch
            {
                Dispose();
                throw;
            }
            finally { Marshal.ReleaseComObject(enumerator); }
        }

        public void Initialize(uint flags, AutoResetEvent signal, nint streamFormat = 0)
        {
            const int shared = 0;
            Check(Client.Initialize(shared, flags, 0, 0, streamFormat == 0 ? _format : streamFormat, 0), "initialize the shared-mode stream");
            Check(Client.SetEventHandle(signal.SafeWaitHandle.DangerousGetHandle()), "register the stream event");
        }

        public T GetService<T>() where T : class
        {
            var iid = typeof(T).GUID;
            Check(Client.GetService(iid, out var native), "open the stream service");
            if (native is T service) return service;
            Marshal.ReleaseComObject(native);
            throw new NotSupportedException($"The endpoint returned an unexpected {typeof(T).Name} service.");
        }

        public void Dispose()
        {
            if (_format != 0)
            {
                Marshal.FreeCoTaskMem(_format);
                _format = 0;
            }
            if (_clientObject is not null)
            {
                Marshal.ReleaseComObject(_clientObject);
                _clientObject = null;
            }
        }
    }
}
