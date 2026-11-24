using HapticBridge.Integrations.Apollo;
using HapticBridge.Integrations.VIIPER;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HapticBridge.Tests;

[TestClass]
public sealed class ViiperV5InputFrameTests
{
    [TestMethod]
    public void WritesMappedInputAndIeeeChecksumForInstalledFork()
    {
        var input = new Ds4ReportEx
        {
            ThumbLX = 0, ThumbLY = 128, ThumbRX = 255, ThumbRY = 129, Buttons = 0x0721, SpecialButtons = 3,
            TriggerL = 23, TriggerR = 250, Touch1X = 321, Touch1Y = 456, Touch1Active = true, Touch1TrackingId = 17,
            Touch2X = 654, Touch2Y = 789, Touch2TrackingId = 47,
            GyroX = -1724, GyroY = 401, GyroZ = -33, AccelX = 8192, AccelY = -16, AccelZ = 27
        };
        byte[] bytes = new byte[ViiperV5InputFrame.Size];
        ViiperV5InputFrame.Write(input, 0x01020304, bytes);

        const string expected = "5650434D0501210004030201181B13F680007F01200703000917FA4101C801118E021503AF44F99101DFFF0020F0FF1B00";
        Assert.AreEqual(expected, Convert.ToHexString(bytes));
    }

    [TestMethod]
    public void RejectsTruncatedFrameAndReservedDpadValue()
    {
        var input = Ds4ReportEx.CreateDefault();
        Assert.ThrowsException<ArgumentException>(() => ViiperV5InputFrame.Write(input, 0, new byte[48]));
        input.Buttons = 9;
        Assert.ThrowsException<ArgumentException>(() =>
            ViiperV5InputFrame.Write(input, 0, new byte[ViiperV5InputFrame.Size]));
    }
}
