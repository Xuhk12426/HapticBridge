using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Text;
using HapticBridge.Core.Models;
using HapticBridge.Integrations.Apollo;
using HapticBridge.Integrations.VIIPER;
using HapticBridge.Setup.Engine;
using HapticBridge.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HapticBridge.Tests;

[TestClass]
public sealed class NativeTestSessionTests
{
    private const string Identity = """{"server":"VIIPER","version":"0.1.9-rc4.6.6"}""";

    [TestMethod]
    public async Task InitialConnectionWaitsForApolloToPublishTheController()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var progress = new InlineProgress();
        var controller = new MemoryStream();
        int attempts = 0;
        using var source = await ViiperControllerTest.WaitForInitialControllerAsync(() =>
        {
            if (++attempts < 2) throw new ViGEmControllerUnavailableException("Waiting for Artemis");
            return controller;
        }, progress, timeout.Token);

        Assert.AreSame(controller, source);
        Assert.AreEqual(2, attempts);
    }

    [TestMethod]
    public async Task ConnectPreparesHidHideBeforeStartingTheRelay()
    {
        var order = new List<string>();
        SetupRepairState state = SetupRepairState.Verified;
        var controller = new ControllerSessionViewModel(() => true, () => false, (_, _) =>
        {
            order.Add("relay");
            return Task.FromResult(new ViiperTestUpdate(1, 1, "Connected"));
        }, () =>
        {
            order.Add("access");
            return new SetupRepairItem("HidHide", state, "Access could not be verified");
        });

        await controller.StartAsync();
        CollectionAssert.AreEqual(new[] { "access", "relay" }, order);

        order.Clear();
        state = SetupRepairState.Unverified;
        await controller.StartAsync();
        CollectionAssert.AreEqual(new[] { "access" }, order);
        StringAssert.Contains(controller.Status, "Access could not be verified");
    }

    [TestMethod]
    public async Task ControllerTestRejectsAnAlreadyOccupiedViiperServer()
    {
        var transport = new ScriptedApi(Identity, """{"buses":[1]}""");
        var test = new ViiperControllerTest(transport.ConnectAsync, () => 23124);

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            test.RunFromHidAsync(Stream.Null, new InlineProgress(), CancellationToken.None));

        StringAssert.Contains(exception.Message, "already owns a bus");
        CollectionAssert.AreEqual(new[] { "ping\0", "bus/list\0" }, transport.Requests());
    }

    [TestMethod]
    public async Task FailedDeviceCreationRemovesOnlyTheConfirmedEmptyTestBus()
    {
        var transport = new ScriptedApi(Identity, """{"buses":[]}""", """{"busId":23124}""",
            """{"status":409,"detail":"USB/IP attachment failed"}""", """{"devices":[]}""", """{"busId":23124}""");
        var test = new ViiperControllerTest(transport.ConnectAsync, () => 23124);

        var exception = await Assert.ThrowsExceptionAsync<IOException>(() =>
            test.RunFromHidAsync(Stream.Null, new InlineProgress(), CancellationToken.None));

        StringAssert.Contains(exception.Message, "USB/IP attachment failed");
        CollectionAssert.AreEqual(new[]
        {
            "ping\0", "bus/list\0", "bus/create 23124\0", "bus/23124/add {\"type\":\"dualsensecombinedaudioduplexv5\"}\0",
            "bus/23124/list\0", "bus/remove 23124\0"
        }, transport.Requests());
    }

    [TestMethod]
    public async Task ControllerTestWritesAReportAndReleasesItsBusOnStop()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var transport = new ScriptedApi(Identity, """{"buses":[]}""", """{"busId":23124}""",
            """{"busId":23124,"devId":"7","type":"dualsensecombinedaudioduplexv5"}""", null,
            """{"devices":[{"devId":"7","type":"dualsensecombinedaudioduplexv5"}]}""", """{"busId":23124}""");
        transport.AfterWireWrite = bytes =>
        {
            if (bytes.Length == ViiperV5InputFrame.Size && !stop.IsCancellationRequested) stop.Cancel();
        };
        var test = new ViiperControllerTest(transport.ConnectAsync, () => 23124);
        byte[] hid = new byte[64];
        hid[0] = 1;
        hid[1] = 250;
        hid[5] = 0x28;
        using var input = new WaitingStream(hid);

        var result = await test.RunFromHidAsync(input, new InlineProgress(), stop.Token);

        Assert.AreEqual(1L, result.InputReports);
        var written = transport.Streams[4].Written.ToArray();
        byte[] handshake = Encoding.ASCII.GetBytes("bus/23124/7\0");
        Assert.IsTrue(written.AsSpan(0, handshake.Length).SequenceEqual(handshake));
        Assert.IsTrue(written.AsSpan(handshake.Length, 4).SequenceEqual("VPCM"u8));
        Assert.AreEqual((byte)122, written[handshake.Length + 16]);
        Assert.AreEqual("bus/remove 23124\0", transport.Requests()[^1]);
    }

    [TestMethod]
    public async Task CleanupLeavesAChangedBusAloneAndReportsTheFailure()
    {
        var transport = new ScriptedApi(Identity, """{"buses":[]}""", """{"busId":23124}""",
            """{"status":409,"detail":"USB/IP attachment failed"}""", """{"devices":[{"devId":"99","type":"keyboard"}]}""");
        var test = new ViiperControllerTest(transport.ConnectAsync, () => 23124);

        var exception = await Assert.ThrowsExceptionAsync<IOException>(() =>
            test.RunFromHidAsync(Stream.Null, new InlineProgress(), CancellationToken.None));

        StringAssert.Contains(exception.Message, "VIIPER cleanup could not be confirmed");
        Assert.AreEqual("bus/23124/list\0", transport.Requests()[^1]);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ControllerTestStartsAndStopsItsIsolatedBackendWhenTheDefaultPortIsClosed(bool timesOut)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var api = new ScriptedApi(Identity, """{"buses":[]}""", """{"busId":23124}""",
            """{"busId":23124,"devId":"7","type":"dualsensecombinedaudioduplexv5","usbipPort":1}""", null,
            """{"devices":[{"devId":"7","type":"dualsensecombinedaudioduplexv5"}]}""", """{"busId":23124}""");
        api.AfterWireWrite = bytes =>
        {
            if (bytes.Length == ViiperV5InputFrame.Size) stop.Cancel();
        };
        var child = new OwnedBackend();
        int starts = 0;
        var ports = new List<int>();
        var test = new ViiperControllerTest((port, token) =>
        {
            ports.Add(port);
            return port == 3242
                ? Task.FromException<Stream>(timesOut ? new OperationCanceledException() :
                    new SocketException((int)SocketError.ConnectionRefused))
                : api.ConnectAsync(token);
        }, () => 23124, () => { starts++; return child; });
        byte[] hid = new byte[64];
        hid[0] = 1;
        hid[5] = 8;
        using var input = new WaitingStream(hid);

        var result = await test.RunFromHidAsync(input, new InlineProgress(), stop.Token);

        Assert.AreEqual(1L, result.InputReports);
        Assert.AreEqual(1, starts);
        Assert.IsTrue(child.HasExited);
        Assert.AreEqual(3242, ports[0]);
        Assert.IsTrue(ports.Skip(1).All(port => port == child.ApiPort));
        Assert.AreEqual("bus/remove 23124\0", api.Requests()[^1]);
    }

    [TestMethod]
    public async Task ControllerTestDisposesItsBackendWhenTheVersionProbeFails()
    {
        var api = new ScriptedApi("""{"server":"VIIPER","version":"1.0.0"}""");
        var child = new OwnedBackend();
        var test = new ViiperControllerTest((port, token) => port == 3242
            ? Task.FromException<Stream>(new SocketException((int)SocketError.ConnectionRefused))
            : api.ConnectAsync(token), () => 23124, () => child);
        await Assert.ThrowsExceptionAsync<NotSupportedException>(() =>
            test.RunFromHidAsync(Stream.Null, new InlineProgress(), CancellationToken.None));
        Assert.IsTrue(child.HasExited);
        CollectionAssert.AreEqual(new[] { "ping\0" }, api.Requests());
    }

    private sealed class OwnedBackend : IViiperTestBackend
    {
        public int ApiPort => 33892;
        public bool HasExited { get; private set; }
        public void Dispose() => HasExited = true;
    }

    [TestMethod]
    public async Task ControllerKeepsItsDeviceAndReleasesHeldButtonsWhileApolloReconnects()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var api = new ScriptedApi(Identity, """{"buses":[]}""", """{"busId":23124}""",
            """{"busId":23124,"devId":"7","type":"dualsensecombinedaudioduplexv5"}""", null,
            """{"devices":[{"devId":"7","type":"dualsensecombinedaudioduplexv5"}]}""", """{"busId":23124}""");
        var inputs = new List<byte[]>();
        api.AfterWireWrite = bytes =>
        {
            if (bytes.Length != ViiperV5InputFrame.Size) return;
            inputs.Add(bytes);
            if (inputs.Count == 3) stop.Cancel();
        };
        var test = new ViiperControllerTest(api.ConnectAsync, () => 23124);
        var original = new byte[64];
        original[0] = 1;
        original[1] = 255;
        original[5] = 0x28;
        byte[] resumed = (byte[])original.Clone();
        resumed[1] = 0;
        using var input = new WaitingStream(original, waitAfterInput: false);
        int opens = 0;

        var result = await test.RunFromHidAsync(input, new InlineProgress(), stop.Token, () =>
        {
            opens++;
            return new WaitingStream(resumed);
        });

        Assert.AreEqual(2L, result.InputReports);
        Assert.AreEqual(1, opens);
        Assert.AreEqual(0, inputs[1][16], "The pending input is neutral while Apollo is unavailable.");
        Assert.AreEqual(0, inputs[1][20] & 0x20, "A held Cross button must be released.");
        Assert.AreEqual(1, api.Requests().Count(request => request.StartsWith("bus/create ", StringComparison.Ordinal)));
        Assert.AreEqual("bus/remove 23124\0", api.Requests()[^1]);
    }

    [TestMethod]
    public async Task ClosingAudioToolsDoesNotCancelTheSharedControllerConnection()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = new ControllerSessionViewModel(() => true, () => false, async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new ViiperTestUpdate(0, 0, "DualSense disconnected.");
        });
        var audioTests = new NativeTestViewModel(new MockAudioEndpointService(), () => false, controller);
        var notices = new List<string>();
        controller.UnexpectedDisconnect += notices.Add;
        Task session = controller.StartAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        audioTests.StopAudio();
        Assert.IsTrue(controller.IsRunning);
        Assert.IsFalse(session.IsCompleted);

        await controller.StopAsync();
        Assert.IsFalse(controller.IsRunning);
        Assert.IsTrue(session.IsCompletedSuccessfully);
        Assert.IsEmpty(notices);
    }

    [TestMethod]
    public async Task ForwardedReportsPublishTestButtonsAndIntermediateTriggerTravel()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var api = FeedbackApi([]);
        (ushort Buttons, byte LeftTrigger, byte RightTrigger)[] states =
        [
            (0, 0, 0), (0x20, 0, 0), (0x20, 0, 0), (0x60, 0, 0), (0, 0, 0), (0x80, 0, 0),
            (0x100, 0, 0), (0x200, 0, 0), (0x400, 11, 0), (0x400, 12, 0),
            (0x400, 100, 0), (0x400, 100, 0), (0x400, 5, 0), (0x800, 0, 43),
            (0x800, 0, 255), (0x4000, 0, 0), (0x8000, 0, 0)
        ];
        byte[] reports = new byte[64 * states.Length];
        for (int index = 0; index < states.Length; index++)
        {
            int offset = index * 64;
            var state = states[index];
            reports[offset] = 1;
            BinaryPrimitives.WriteUInt16LittleEndian(reports.AsSpan(offset + 5), (ushort)(state.Buttons | 8));
            reports[offset + 8] = state.LeftTrigger;
            reports[offset + 9] = state.RightTrigger;
        }
        int writes = 0;
        api.AfterWireWrite = bytes =>
        {
            if (bytes.Length == ViiperV5InputFrame.Size && ++writes == states.Length) stop.Cancel();
        };
        using var input = new WaitingStream(reports);
        var observed = new List<DualSenseTestInput?>();
        var test = new ViiperControllerTest(api.ConnectAsync, () => 23124);
        await test.RunFromHidAsync(input, new InlineProgress(), stop.Token,
            testInput: new InlineObserver<DualSenseTestInput?>(observed.Add));

        CollectionAssert.AreEqual(new DualSenseTestInput?[]
        {
            new DualSenseTestInput(DualSenseTestButtons.None, 0, 0),
            new DualSenseTestInput(DualSenseTestButtons.Cross, 0, 0),
            new DualSenseTestInput(DualSenseTestButtons.Cross | DualSenseTestButtons.Circle, 0, 0),
            new DualSenseTestInput(DualSenseTestButtons.None, 0, 0),
            new DualSenseTestInput(DualSenseTestButtons.Triangle, 0, 0),
            new DualSenseTestInput(DualSenseTestButtons.L1, 0, 0),
            new DualSenseTestInput(DualSenseTestButtons.R1, 0, 0),
            new DualSenseTestInput(DualSenseTestButtons.None, 11, 0),
            new DualSenseTestInput(DualSenseTestButtons.L2, 12, 0),
            new DualSenseTestInput(DualSenseTestButtons.L2, 100, 0),
            new DualSenseTestInput(DualSenseTestButtons.None, 5, 0),
            new DualSenseTestInput(DualSenseTestButtons.R2, 0, 43),
            new DualSenseTestInput(DualSenseTestButtons.R2, 0, 255),
            new DualSenseTestInput(DualSenseTestButtons.L3, 0, 0),
            new DualSenseTestInput(DualSenseTestButtons.R3, 0, 0)
        }, observed);
    }

    [TestMethod]
    public async Task RealtimeHapticsAndAdjacentV5FramesDoNotInterruptControllerInput()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var api = FeedbackApi(BuildFeedbackFrames((0x81, 474), (0x84, 474), (0x83, 2396), (0x84, 474), (0x85, 9), (0x81, 474)));
        api.AfterWireWrite = bytes =>
        {
            if (bytes.Length == ViiperV5InputFrame.Size) stop.Cancel();
        };
        var speakerBlocks = new List<byte[]>();
        var test = new ViiperControllerTest(api.ConnectAsync, () => 23124, pcm => speakerBlocks.Add(pcm.ToArray()));
        byte[] hid = new byte[64];
        hid[0] = 1;
        hid[5] = 8;
        using var input = new WaitingStream(hid);

        var result = await test.RunFromHidAsync(input, new InlineProgress(), stop.Token);

        Assert.AreEqual(1L, result.InputReports);
        Assert.AreEqual(6L, result.FeedbackFrames);
        Assert.HasCount(1, speakerBlocks);
        var expectedStereo = new byte[1920];
        expectedStereo[0] = 0x21;
        expectedStereo[^1] = 0x12;
        CollectionAssert.AreEqual(expectedStereo, speakerBlocks[0]);
        Assert.AreEqual("bus/remove 23124\0", api.Requests()[^1]);
    }

    [TestMethod]
    public async Task InvalidFeedbackInAnActiveSessionRaisesOneDisconnectNotification()
    {
        var api = FeedbackApi(BuildFeedbackFrames((0x81, 474), (0x84, 473)));
        var test = new ViiperControllerTest(api.ConnectAsync, () => 23124);
        using var input = new WaitingStream([]);
        var controller = new ControllerSessionViewModel(() => true, () => false,
            (progress, token) => test.RunFromHidAsync(input, progress, token));
        var notices = new List<string>();
        controller.UnexpectedDisconnect += notices.Add;

        await controller.StartAsync();

        Assert.HasCount(1, notices);
        StringAssert.Contains(notices[0], "0x84");
        StringAssert.Contains(notices[0], "473 bytes");
        Assert.IsFalse(controller.IsRunning);
        Assert.AreEqual("bus/remove 23124\0", api.Requests()[^1]);
    }

    [TestMethod]
    public async Task ASetupFailureBeforeTheControllerConnectsDoesNotRaiseADisconnectNotification()
    {
        var controller = new ControllerSessionViewModel(() => true, () => false,
            (_, _) => Task.FromException<ViiperTestUpdate>(new IOException("VIIPER setup failed.")));
        var notices = new List<string>();
        controller.UnexpectedDisconnect += notices.Add;

        await controller.StartAsync();

        Assert.IsEmpty(notices);
        StringAssert.Contains(controller.Status, "VIIPER setup failed");
    }

    private static ScriptedApi FeedbackApi(byte[] feedback) =>
        new(Identity, """{"buses":[]}""", """{"busId":23124}""",
            """{"busId":23124,"devId":"7","type":"dualsensecombinedaudioduplexv5"}""", null,
            """{"devices":[{"devId":"7","type":"dualsensecombinedaudioduplexv5"}]}""", """{"busId":23124}""")
        {
            Feedback = feedback
        };

    private static byte[] BuildFeedbackFrames(params (byte Type, ushort Length)[] frames)
    {
        using var bytes = new MemoryStream();
        uint sequence = 0;
        foreach (var (type, length) in frames)
        {
            byte[] frame = new byte[16 + length];
            "VPCM"u8.CopyTo(frame);
            frame[4] = 5;
            frame[5] = type;
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), length);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), ++sequence);
            if (type == 0x83)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(16), 474);
                frame[16 + 2 + 474 - 1] = 0xFF;
                frame[16 + 2 + 474] = 0x21;
                frame[^1] = 0x12;
            }
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(12), ViiperV5InputFrame.Checksum(frame.AsSpan(4, 8), frame.AsSpan(16)));
            bytes.Write(frame);
        }
        return bytes.ToArray();
    }

    [TestMethod]
    public async Task AudioButtonsWaitForBothEndpointsAndBlockConcurrentSoundSync()
    {
        var audio = new MockAudioEndpointService();
        audio.RenderEndpoints.Add(new AudioEndpointModel
        {
            Id = "cable", FriendlyName = "VB-CABLE", EndpointType = AudioEndpointType.VirtualCable, IsActive = true
        });
        var controller = new ControllerSessionViewModel(() => false, () => false, (_, _) => throw new InvalidOperationException());
        var model = new NativeTestViewModel(audio, () => true, controller);
        await model.RefreshEndpointsAsync();
        Assert.IsFalse(model.StartAudioCommand.CanExecute(null));
        StringAssert.Contains(model.AudioStatus, "Waiting for a DualSense");

        audio.RenderEndpoints.Add(new AudioEndpointModel
        {
            Id = "dualsense", FriendlyName = "DualSense", EndpointType = AudioEndpointType.VirtualDualSense, IsActive = true
        });
        await model.RefreshEndpointsAsync();
        Assert.IsTrue(model.StartAudioCommand.CanExecute(null));
        await model.StartAudioAsync();
        StringAssert.Contains(model.AudioStatus, "Close SoundSync");
        Assert.IsFalse(model.AudioRunning);
    }

    private sealed class InlineProgress : IProgress<ViiperTestUpdate> { public void Report(ViiperTestUpdate value) { } }
    private sealed class InlineObserver<T>(Action<T> receive) : IProgress<T> { public void Report(T value) => receive(value); }

    private sealed class ScriptedApi(params string?[] responses)
    {
        public List<WaitingStream> Streams { get; } = [];
        public Action<byte[]>? AfterWireWrite { get; set; }
        public byte[] Feedback { get; init; } = [];

        public Task<Stream> ConnectAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var response = responses[Streams.Count];
            var stream = new WaitingStream(response is null ? Feedback : Encoding.UTF8.GetBytes(response), response is null);
            if (response is null) stream.AfterWrite = AfterWireWrite;
            Streams.Add(stream);
            return Task.FromResult<Stream>(stream);
        }

        public string[] Requests() => Streams.Select(s => Encoding.ASCII.GetString(s.Written.ToArray())).ToArray();
    }

    private sealed class WaitingStream(byte[] input, bool waitAfterInput = true) : Stream
    {
        private readonly MemoryStream _input = new(input);
        public MemoryStream Written { get; } = new();
        public Action<byte[]>? AfterWrite { get; set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            int count = _input.Read(buffer.Span);
            if (count == 0 && waitAfterInput) await Task.Delay(Timeout.Infinite, token);
            return count;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Written.Write(buffer.Span);
            AfterWrite?.Invoke(buffer.ToArray());
            return ValueTask.CompletedTask;
        }
    }
}
