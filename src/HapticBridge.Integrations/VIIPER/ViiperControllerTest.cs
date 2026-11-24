using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HapticBridge.Integrations.Apollo;

namespace HapticBridge.Integrations.VIIPER;

public sealed record ViiperTestUpdate(long InputReports, long FeedbackFrames, string Detail);

[Flags]
public enum DualSenseTestButtons
{
    None = 0,
    Square = 0x10,
    Cross = 0x20,
    Circle = 0x40,
    Triangle = 0x80,
    L1 = 0x100,
    R1 = 0x200,
    L2 = 0x400,
    R2 = 0x800,
    L3 = 0x4000,
    R3 = 0x8000
}

public readonly record struct DualSenseTestInput(DualSenseTestButtons Buttons, byte LeftTrigger, byte RightTrigger);

public delegate void ViiperSpeakerPcmHandler(ReadOnlySpan<byte> stereoPcm);

public sealed class ViiperControllerDisconnectedException(string message, Exception innerException) : IOException(message, innerException);

public sealed class ViiperControllerTest
{
    private const int DefaultApiPort = 3242;
    public const string TestedVersion = "0.1.9-rc4.6.6";
    internal const string DeviceType = "dualsensecombinedaudioduplexv5";
    private readonly Func<int, CancellationToken, Task<Stream>> _connect;
    private readonly Func<int> _busNumber;
    private readonly Func<IViiperTestBackend>? _startBackend;
    private readonly ViiperSpeakerPcmHandler? _speakerPcm;

    public ViiperControllerTest(ViiperSpeakerPcmHandler? speakerPcm = null)
        : this(ConnectLocalAsync, () => RandomNumberGenerator.GetInt32(16384, 65536), ViiperTestBackend.Start, speakerPcm) { }

    internal ViiperControllerTest(Func<CancellationToken, Task<Stream>> connect, Func<int> busNumber,
        ViiperSpeakerPcmHandler? speakerPcm = null)
        : this((_, token) => connect(token), busNumber, null, speakerPcm) { }

    internal ViiperControllerTest(Func<int, CancellationToken, Task<Stream>> connect, Func<int> busNumber,
        Func<IViiperTestBackend>? startBackend, ViiperSpeakerPcmHandler? speakerPcm = null)
    {
        _connect = connect;
        _busNumber = busNumber;
        _startBackend = startBackend;
        _speakerPcm = speakerPcm;
    }

    public Task<ViiperTestUpdate> RunAsync(IProgress<ViiperTestUpdate> progress, CancellationToken stop) =>
        RunAsync(progress, stop, null);

    public Task<ViiperTestUpdate> RunAsync(IProgress<ViiperTestUpdate> progress, CancellationToken stop,
        IProgress<DualSenseTestInput?>? testInput) =>
        Task.Run(async () =>
        {
            using var input = await WaitForInitialControllerAsync(ViGEmDs4Hid.OpenSingleGamepad, progress, stop).ConfigureAwait(false);
            return await RunFromHidAsync(input, progress, stop, ViGEmDs4Hid.OpenSingleGamepad, testInput).ConfigureAwait(false);
        }, stop);

    private static DualSenseTestInput ReadTestInput(Ds4ReportEx input)
    {
        const DualSenseTestButtons digitalMask = DualSenseTestButtons.Square | DualSenseTestButtons.Cross |
            DualSenseTestButtons.Circle | DualSenseTestButtons.Triangle | DualSenseTestButtons.L1 |
            DualSenseTestButtons.R1 | DualSenseTestButtons.L3 | DualSenseTestButtons.R3;
        const int triggerThreshold = 12;
        var buttons = (DualSenseTestButtons)input.Buttons & digitalMask;
        if (input.TriggerL >= triggerThreshold) buttons |= DualSenseTestButtons.L2;
        if (input.TriggerR >= triggerThreshold) buttons |= DualSenseTestButtons.R2;
        return new(buttons, input.TriggerL, input.TriggerR);
    }

    internal static async Task<Stream> WaitForInitialControllerAsync(Func<Stream> open,
        IProgress<ViiperTestUpdate> progress, CancellationToken token)
    {
        bool waiting = false;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return open(); }
            catch (ViGEmControllerUnavailableException exception)
            {
                if (!waiting) progress.Report(new(0, 0, exception.Message));
                waiting = true;
                await Task.Delay(500, token).ConfigureAwait(false);
            }
        }
    }

    internal async Task<ViiperTestUpdate> RunFromHidAsync(Stream input, IProgress<ViiperTestUpdate> progress,
        CancellationToken stop, Func<Stream>? reopenInput = null, IProgress<DualSenseTestInput?>? testInput = null)
    {
        using var backend = await PrepareBackendAsync(progress, stop).ConfigureAwait(false);
        int port = backend?.ApiPort ?? DefaultApiPort;

        using (var current = await RequestAsync(port, "bus/list", stop).ConfigureAwait(false))
        {
            if (current.RootElement.GetProperty("buses").GetArrayLength() != 0)
                throw new InvalidOperationException("VIIPER already owns a bus. Close the other controller session and reconnect.");
        }

        int bus = _busNumber();
        using (var created = await RequestAsync(port, $"bus/create {bus}", stop).ConfigureAwait(false))
        {
            if (created.RootElement.GetProperty("busId").GetInt32() != bus)
                throw new IOException("VIIPER did not confirm the test bus.");
        }

        string? deviceId = null;
        ViiperTestUpdate? summary = null;
        Exception? testFailure = null;
        string cleanupDetail = string.Empty;
        bool attached = false;
        try
        {
            progress.Report(new(0, 0, "Connecting the virtual DualSense…"));
            using var added = await RequestAsync(port, $"bus/{bus}/add {{\"type\":\"{DeviceType}\"}}",
                stop, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            deviceId = added.RootElement.GetProperty("devId").GetString();
            if (added.RootElement.GetProperty("busId").GetInt32() != bus || !uint.TryParse(deviceId, out uint device) || device == 0 ||
                added.RootElement.GetProperty("type").GetString() != DeviceType)
                throw new IOException("VIIPER did not confirm an audio-capable DualSense on the test bus.");
            if (backend is not null && (!added.RootElement.TryGetProperty("usbipPort", out var usbPort) ||
                !usbPort.TryGetInt32(out int attachedPort) || attachedPort < 1))
                throw new IOException("VIIPER created the DualSense but Windows USB/IP did not attach it. Check the USB/IP driver.");

            await using var stream = await OpenAsync(port, stop).ConfigureAwait(false);
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop))
            {
                handshake.CancelAfter(TimeSpan.FromSeconds(2));
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"bus/{bus}/{device}\0"), handshake.Token).ConfigureAwait(false);
            }
            attached = true;
            progress.Report(new(0, 0, "Virtual DualSense connected. You can now launch the game."));
            summary = await RelayAsync(input, stream, progress, stop, reopenInput, _speakerPcm, testInput).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            summary = new(0, 0, "Virtual DualSense disconnected during startup.");
        }
        catch (Exception exception)
        {
            testFailure = exception;
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                using var listed = await RequestAsync(port, $"bus/{bus}/list", cleanup.Token).ConfigureAwait(false);
                var devices = listed.RootElement.GetProperty("devices");
                if (devices.ValueKind == JsonValueKind.Array &&
                    devices.EnumerateArray().All(entry => deviceId is not null &&
                        entry.GetProperty("devId").GetString() == deviceId &&
                        entry.GetProperty("type").GetString() == DeviceType))
                {
                    using var removed = await RequestAsync(port, $"bus/remove {bus}", cleanup.Token).ConfigureAwait(false);
                    if (removed.RootElement.GetProperty("busId").GetInt32() != bus)
                        throw new IOException("VIIPER returned an unexpected cleanup response.");
                }
                else
                {
                    throw new IOException("The test bus no longer contains only the expected device.");
                }
            }
            catch (Exception exception)
            {
                Trace.TraceWarning("VIIPER test cleanup could not be confirmed: {0}", exception);
                cleanupDetail = " VIIPER cleanup could not be confirmed. The server's stream timeout may still remove the test device.";
            }
        }

        if (testFailure is not null)
        {
            string detail = testFailure.Message + cleanupDetail;
            throw attached
                ? new ViiperControllerDisconnectedException(detail, testFailure)
                : new IOException(detail, testFailure);
        }
        var result = summary ?? throw new InvalidOperationException("The controller test ended without a result.");
        return result with { Detail = result.Detail + cleanupDetail };
    }

    private async Task<IViiperTestBackend?> PrepareBackendAsync(IProgress<ViiperTestUpdate> progress, CancellationToken stop)
    {
        try
        {
            await CheckVersionAsync(DefaultApiPort, stop).ConfigureAwait(false);
            return null;
        }
        catch (IOException exception) when (_startBackend is not null && IsConnectFailure(exception))
        {
            stop.ThrowIfCancellationRequested();
            progress.Report(new(0, 0, "Apollo controller found. Starting VIIPER…"));
            var backend = _startBackend();
            try
            {
                var startup = Stopwatch.StartNew();
                while (!backend.HasExited && startup.Elapsed < TimeSpan.FromSeconds(8))
                {
                    try
                    {
                        await CheckVersionAsync(backend.ApiPort, stop).ConfigureAwait(false);
                        if (!backend.HasExited) return backend;
                        break;
                    }
                    catch (IOException retry) when (IsConnectFailure(retry))
                    {
                        await Task.Delay(150, stop).ConfigureAwait(false);
                    }
                }
                throw new IOException(backend.HasExited
                    ? "The installed VIIPER process exited before the test could start. Check the USB/IP driver."
                    : "The installed VIIPER process did not become ready. Check the USB/IP driver.");
            }
            catch
            {
                backend.Dispose();
                throw;
            }
        }
    }

    private async Task CheckVersionAsync(int port, CancellationToken stop)
    {
        using var identity = await RequestAsync(port, "ping", stop).ConfigureAwait(false);
        if (identity.RootElement.GetProperty("server").GetString() != "VIIPER" ||
            identity.RootElement.GetProperty("version").GetString()?.TrimStart('v') != TestedVersion)
            throw new NotSupportedException($"This test requires VIIPER {TestedVersion}. The API on local port {port} reported a different backend.");
    }

    private static bool IsConnectFailure(IOException exception) => exception.InnerException is
        SocketException { SocketErrorCode: SocketError.ConnectionRefused or SocketError.TimedOut } or OperationCanceledException;

    private static async Task<ViiperTestUpdate> RelayAsync(Stream hid, Stream output, IProgress<ViiperTestUpdate> progress,
        CancellationToken stop, Func<Stream>? reopenInput, ViiperSpeakerPcmHandler? speakerPcm,
        IProgress<DualSenseTestInput?>? testInput)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var token = session.Token;
        long sent = 0;
        long received = 0;
        uint sequence = 0;
        byte[] frame = new byte[ViiperV5InputFrame.Size];
        var timer = Stopwatch.StartNew();
        var receive = ReadFeedbackAsync();
        var send = SendInputAsync();
        Exception? failure = null;
        try
        {
            await await Task.WhenAny(send, receive).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception exception) { failure = exception; }
        finally
        {
            session.Cancel();
            try { await Task.WhenAll(send, receive).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception exception) { failure ??= exception; }
        }

        if (failure is not null) throw new IOException(failure.Message, failure);
        // Closing the stream also starts VIIPER's device teardown. Try to release held buttons first.
        try
        {
            using var neutral = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            ViiperV5InputFrame.Write(Ds4ReportEx.CreateDefault(), ++sequence, frame);
            await output.WriteAsync(frame, neutral.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException) { }
        return new(sent, received, "Virtual DualSense disconnected.");

        async Task SendInputAsync()
        {
            byte[] bytes = new byte[64];
            Stream source = hid;
            Stream? replacement = null;
            DualSenseTestInput? previousInput = null;
            try
            {
                while (true)
                {
                    int read;
                    try { read = await source.ReadAsync(bytes, token).ConfigureAwait(false); }
                    catch (Exception exception) when (reopenInput is not null && !token.IsCancellationRequested &&
                        exception is IOException or OperationCanceledException)
                    {
                        Trace.TraceWarning("Apollo HID input stopped: {0}", exception.Message);
                        read = 0;
                    }

                    if (read == 0)
                    {
                        if (reopenInput is null) throw new IOException("The source controller disconnected.");
                        replacement = null;
                        source.Dispose();
                        previousInput = null;
                        testInput?.Report(null);
                        progress.Report(new(sent, Interlocked.Read(ref received), "Apollo input paused. The virtual DualSense stays connected while waiting for the controller…"));
                        source = replacement = await WaitForInputAsync(reopenInput).ConfigureAwait(false);
                        progress.Report(new(sent, Interlocked.Read(ref received), "Apollo input reconnected. The virtual DualSense is ready."));
                        continue;
                    }

                    if (read != 64 || !Ds4ReportParser.TryParse(bytes, out var input)) continue;
                    await WriteInputAsync(input).ConfigureAwait(false);
                    sent++;
                    if (testInput is not null)
                    {
                        var snapshot = ReadTestInput(input);
                        if (snapshot != previousInput)
                        {
                            previousInput = snapshot;
                            testInput.Report(snapshot);
                        }
                    }
                    if (timer.ElapsedMilliseconds >= 250)
                    {
                        progress.Report(new(sent, Interlocked.Read(ref received), "Forwarding Apollo input to the VIIPER DualSense."));
                        timer.Restart();
                    }
                }
            }
            finally { replacement?.Dispose(); }
        }

        async Task<Stream> WaitForInputAsync(Func<Stream> open)
        {
            var neutral = Ds4ReportEx.CreateDefault();
            while (true)
            {
                await WriteInputAsync(neutral).ConfigureAwait(false);
                await Task.Delay(500, token).ConfigureAwait(false);
                try { return open(); }
                catch (IOException) { }
            }
        }

        async Task WriteInputAsync(Ds4ReportEx input)
        {
            using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            writeTimeout.CancelAfter(TimeSpan.FromSeconds(2));
            ViiperV5InputFrame.Write(input, ++sequence, frame);
            try { await output.WriteAsync(frame, writeTimeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException exception) when (!token.IsCancellationRequested)
            {
                throw new IOException("VIIPER stopped accepting controller input.", exception);
            }
        }

        async Task ReadFeedbackAsync()
        {
            const int stateSize = 474;
            const int speakerOffset = 2 + stateSize;
            const int atomicSize = speakerOffset + 1920;
            const int microphoneStateSize = 1 + sizeof(ulong);
            byte[] header = new byte[16];
            byte[] payload = new byte[atomicSize];
            uint? lastSequence = null;
            while (true)
            {
                await output.ReadExactlyAsync(header, token).ConfigureAwait(false);
                int length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
                if (!header.AsSpan(0, 4).SequenceEqual("VPCM"u8) || header[4] != 5)
                    throw new IOException("VIIPER returned an invalid V5 feedback header.");
                byte type = header[5];
                // The real-time lane carries the same combined state as 0x81.
                if ((type, length) is not ((0x81 or 0x84, stateSize) or (0x83, atomicSize) or (0x85, microphoneStateSize)))
                    throw new IOException($"VIIPER returned V5 feedback type 0x{type:X2} with an unexpected length of {length} bytes.");
                await output.ReadExactlyAsync(payload.AsMemory(0, length), token).ConfigureAwait(false);
                uint sequence = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
                uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
                if (ViiperV5InputFrame.Checksum(header.AsSpan(4, 8), payload.AsSpan(0, length)) != expectedCrc ||
                    (lastSequence.HasValue && sequence != unchecked(lastSequence.Value + 1)) ||
                    (type == 0x83 && BinaryPrimitives.ReadUInt16LittleEndian(payload) != stateSize))
                    throw new IOException("A VIIPER feedback frame failed checksum, sequence, or length validation.");
                lastSequence = sequence;
                Interlocked.Increment(ref received);
                if (type == 0x83 && speakerPcm is not null)
                {
                    try { speakerPcm(payload.AsSpan(speakerOffset, atomicSize - speakerOffset)); }
                    catch (Exception exception)
                    {
                        Trace.TraceWarning("DualSense speaker playback failed: {0}", exception);
                        speakerPcm = null;
                    }
                }
            }
        }
    }

    private async Task<Stream> OpenAsync(int port, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try { return await _connect(port, timeout.Token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is SocketException || exception is OperationCanceledException && !token.IsCancellationRequested)
        {
            throw new IOException($"The VIIPER API did not respond on local port {port}.", exception);
        }
    }

    private async Task<JsonDocument> RequestAsync(int port, string command, CancellationToken token, TimeSpan? responseTimeout = null)
    {
        await using var stream = await OpenAsync(port, token).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(responseTimeout ?? TimeSpan.FromSeconds(2));
        try { return await ReadResponseAsync(stream, command, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException exception) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException($"VIIPER timed out replying to {command.Split(' ')[0]}.", exception);
        }
    }

    private static async Task<JsonDocument> ReadResponseAsync(Stream stream, string command, CancellationToken token)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(command + "\0"), token).ConfigureAwait(false);
        byte[] response = new byte[16385];
        int offset = 0;
        int count;
        while ((count = await stream.ReadAsync(response.AsMemory(offset), token).ConfigureAwait(false)) > 0)
        {
            offset += count;
            if (offset == response.Length) throw new IOException("VIIPER returned an oversized response.");
        }

        var result = JsonDocument.Parse(response.AsMemory(0, offset));
        if (result.RootElement.TryGetProperty("status", out var status))
        {
            string detail = result.RootElement.TryGetProperty("detail", out var message) ? message.ToString() : string.Empty;
            string error = $"VIIPER returned {status} for {command.Split(' ')[0]}. {detail}";
            result.Dispose();
            throw new IOException(error);
        }
        return result;
    }

    private static async Task<Stream> ConnectLocalAsync(int port, CancellationToken token)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, port, token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
