using System.Buffers.Binary;
using System.Runtime.InteropServices;
using HapticBridge.Audio.Native;
using HapticBridge.Audio.Wasapi;
using HapticBridge.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HapticBridge.Tests;

[TestClass]
public sealed class WasapiLoopbackTests
{
    [TestMethod]
    public void PropertyStoreUsesWindowsSdkInterfaceAndNativePointerUnionSize()
    {
        Assert.AreEqual(new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), typeof(IPropertyStore).GUID);
        Assert.AreEqual(IntPtr.Size == 8 ? 24 : 16, Marshal.SizeOf<PROPVARIANT>());
    }

    [TestMethod]
    public void ParsesWindowsExtensibleFormat()
    {
        var bytes = new byte[40];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 48000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 48000 * 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), 32);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(16), 22);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(18), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), 0x603);
        new Guid("00000003-0000-0010-8000-00AA00389B71").TryWriteBytes(bytes.AsSpan(24));

        var format = WasapiMixFormat.Parse(bytes);
        Assert.AreEqual(new WasapiMixFormat(48000, 4, 32, 32, 16, 0x603, WasapiSampleEncoding.Float), format);
        Assert.ThrowsException<FormatException>(() => WasapiMixFormat.Parse(bytes.AsSpan(0, 39)));
    }

    [TestMethod]
    public void CopiesFrontAndHapticSamplesToMatchingPositionsWithoutMixing()
    {
        var map = new HapticChannelMap(Pcm(4, 0x603), Pcm(8, 0x63F));
        byte[] output = new byte[16];
        short[] inputs = [-384, 257, -30000, 19000];
        map.CopyFrame(MemoryMarshal.AsBytes(inputs.AsSpan()), output);

        CollectionAssert.AreEqual(new[] { 1, 2, 7, 8 }, map.GetDestinationChannels());
        CollectionAssert.AreEqual(new short[] { -384, 257, 0, 0, 0, 0, -30000, 19000 },
            MemoryMarshal.Cast<byte, short>(output).ToArray());
    }

    [TestMethod]
    public void RefusesStereoUnknownRearPositionsAndImplicitFormatConversion()
    {
        var quad = Pcm(4, 0x33);
        Assert.ThrowsException<NotSupportedException>(() => new HapticChannelMap(Pcm(2, 3), quad));
        Assert.ThrowsException<NotSupportedException>(() => new HapticChannelMap(quad, Pcm(4, 0x603)));
        Assert.ThrowsException<NotSupportedException>(() => new HapticChannelMap(quad, Pcm(16, 0)));
        Assert.ThrowsException<NotSupportedException>(() => new HapticChannelMap(quad, quad with { SampleRate = 44100 }));
        Assert.ThrowsException<NotSupportedException>(() => new HapticChannelMap(quad,
            new WasapiMixFormat(48000, 4, 32, 32, 16, 0x33, WasapiSampleEncoding.Float)));
    }

    [TestMethod]
    public void BoundedQueueDropsOldestCompleteFramesAndPadsUnderrun()
    {
        var queue = new LoopbackFrameBuffer(new HapticChannelMap(Pcm(4, 0x33), Pcm(4, 0x33)), 2);
        short[] input = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
        queue.Write(MemoryMarshal.AsBytes(input.AsSpan()));
        input.AsSpan().Clear();

        byte[] render = new byte[24];
        var read = queue.Read(render);
        Assert.AreEqual(2, read);
        Assert.AreEqual(1L, queue.DroppedFrames);
        Assert.AreEqual(1L, queue.UnderrunFrames);
        CollectionAssert.AreEqual(new short[] { 5, 6, 7, 8, 9, 10, 11, 12, 0, 0, 0, 0 },
            MemoryMarshal.Cast<byte, short>(render).ToArray());
    }

    [TestMethod]
    public void RefusesIdenticalEndpointIdsBeforeOpeningStreams()
    {
        var route = new WasapiLoopbackRoute();
        var source = new AudioEndpointModel { Id = "endpoint", IsActive = true, EndpointType = AudioEndpointType.VirtualDualSense };
        var cable = new AudioEndpointModel { Id = "ENDPOINT", IsActive = true, EndpointType = AudioEndpointType.VirtualCable };
        Assert.ThrowsException<ArgumentException>(() => route.RunAsync(source, cable, CancellationToken.None));
    }

    private static WasapiMixFormat Pcm(int channels, uint mask) =>
        new(48000, channels, 16, 16, channels * 2, mask, WasapiSampleEncoding.Pcm);
}
