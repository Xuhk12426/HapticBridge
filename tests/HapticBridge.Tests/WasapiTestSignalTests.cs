using System.Runtime.InteropServices;
using HapticBridge.Audio.Wasapi;
using HapticBridge.Integrations.VIIPER;
using HapticBridge.UI.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HapticBridge.Tests;

[TestClass]
public sealed class WasapiTestSignalTests
{
    [TestMethod]
    public void ButtonGateRequiresReleaseAndThePromptedButton()
    {
        var gate = new ButtonPressGate(DualSenseTestButtons.Cross, DualSenseTestButtons.Cross);
        Assert.IsFalse(gate.Observe(DualSenseTestButtons.Cross));
        Assert.IsFalse(gate.Observe(DualSenseTestButtons.None));
        Assert.IsFalse(gate.Observe(DualSenseTestButtons.Cross | DualSenseTestButtons.Circle));
        Assert.IsFalse(gate.Observe(DualSenseTestButtons.Cross));
        Assert.IsFalse(gate.Observe(null));
        Assert.IsFalse(gate.Observe(DualSenseTestButtons.Cross));
        Assert.IsFalse(gate.Observe(DualSenseTestButtons.None));
        Assert.IsTrue(gate.Observe(DualSenseTestButtons.Cross));
        Assert.IsFalse(gate.Observe(DualSenseTestButtons.Cross));
    }

    [TestMethod]
    public void EveryButtonCueUsesOnlyTheRequestedGrip()
    {
        var format = new WasapiMixFormat(48000, 4, 32, 32, 16, 0x33, WasapiSampleEncoding.Float);
        var lengths = new HashSet<int>();
        foreach (var cue in Enum.GetValues<DualSenseHapticCue>())
        {
            foreach (var grip in Enum.GetValues<DualSenseGrip>())
            {
                var signal = MemoryMarshal.Cast<byte, float>(WasapiTestSignal.BuildHapticCue(format, cue, grip));
                int near = grip == DualSenseGrip.Left ? 2 : 3;
                int far = grip == DualSenseGrip.Left ? 3 : 2;
                if (grip == DualSenseGrip.Left) Assert.IsTrue(lengths.Add(signal.Length));
                Assert.AreEqual(0f, Peak(signal, 0), $"{cue} leaked into the left speaker.");
                Assert.AreEqual(0f, Peak(signal, 1), $"{cue} leaked into the right speaker.");
                float peak = Peak(signal, near);
                Assert.IsTrue(peak is >= 0.50f and <= 0.56f, $"{cue} had {peak} peak on {grip}.");
                if (cue == DualSenseHapticCue.Rumble)
                    Assert.IsTrue(Peak(Slice(signal, 0.35, 0.30), near) > 0.55f, "Y should sustain a strong cue.");
                Assert.AreEqual(0f, Peak(signal, far), $"{cue} leaked into the other grip.");
                Assert.AreEqual(0f, Peak(signal[..(4800 * 4)], near), $"{cue} has no playback lead-in.");
                Assert.AreEqual(0f, Peak(signal[^(4800 * 4)..], near), $"{cue} has no playback tail.");
            }
        }
    }

    [TestMethod]
    public void TapRhythmsHaveSilentGapsBetweenDistinctHits()
    {
        var format = new WasapiMixFormat(48000, 4, 32, 32, 16, 0x33, WasapiSampleEncoding.Float);
        var cues = new[] { DualSenseHapticCue.Tap, DualSenseHapticCue.DoubleTap, DualSenseHapticCue.Flutter };
        for (int cue = 0; cue < cues.Length; cue++)
        {
            var signal = MemoryMarshal.Cast<byte, float>(WasapiTestSignal.BuildHapticCue(format, cues[cue], DualSenseGrip.Right));
            int hits = 0;
            bool previous = false;
            const int window = 960 * 4;
            for (int offset = 0; offset + window <= signal.Length; offset += window)
            {
                bool active = Peak(signal.Slice(offset, window), 3) > 0.15f;
                if (active && !previous) hits++;
                previous = active;
            }
            Assert.AreEqual(cue + 1, hits);
        }
    }

    [TestMethod]
    [DataRow(32, DualSenseGrip.Left)]
    [DataRow(16, DualSenseGrip.Right)]
    public void TriggerRumbleFollowsTravelAndFadesOnRelease(int bits, DualSenseGrip grip)
    {
        int blockAlign = 4 * (bits / 8);
        var format = new WasapiMixFormat(48000, 4, bits, bits, blockAlign, 0x33,
            bits == 32 ? WasapiSampleEncoding.Float : WasapiSampleEncoding.Pcm);
        var rumble = new TriggerRumble(format, grip);
        const int frames = 1200;
        var bytes = new byte[frames * blockAlign];
        int near = grip == DualSenseGrip.Left ? 2 : 3;
        int far = grip == DualSenseGrip.Left ? 3 : 2;
        float light = Hold(12);
        float quarter = Hold(64);
        float half = Hold(128);
        float full = Hold(255);
        Assert.IsTrue(grip == DualSenseGrip.Right ? light is > 0.51f and < 0.53f : light is > 0.45f and < 0.47f);
        Assert.IsTrue(quarter > light + 0.02f);
        Assert.IsTrue(half > quarter + 0.025f);
        Assert.IsTrue(full is > 0.63f and <= 0.641f && full > half + 0.06f);
        for (int block = 0; block < 48; block++)
        {
            Assert.AreEqual(frames, rumble.Fill(bytes, 255));
            Assert.IsTrue(ReadPeak(near) > 0.63f);
        }
        Assert.AreEqual(half, Hold(128), 0.001f);
        Assert.AreEqual(full, Hold(255), 0.001f);
        Assert.AreEqual(frames, rumble.Fill(bytes, null));
        Assert.IsTrue(ReadPeak(near) > 0.56f);
        Assert.AreEqual(frames, rumble.Fill(bytes, null));
        Assert.IsTrue(ReadPeak(near) is > 0.35f and < 0.405f);
        Assert.IsTrue(rumble.Fill(bytes, null) is > 0 and < frames);
        Assert.IsTrue(ReadPeak(near) is > 0.12f and < 0.16f);
        Assert.AreEqual(0, rumble.Fill(bytes, null));
        Assert.AreEqual(0f, ReadPeak(near));

        float Hold(byte travel)
        {
            for (int block = 0; block < 20; block++)
                Assert.AreEqual(frames, rumble.Fill(bytes, travel));
            Assert.AreEqual(0f, ReadPeak(0));
            Assert.AreEqual(0f, ReadPeak(1));
            float peak = ReadPeak(near);
            Assert.AreEqual(0f, ReadPeak(far));
            return peak;
        }

        float ReadPeak(int channel)
        {
            if (bits == 32) return Peak(MemoryMarshal.Cast<byte, float>(bytes), channel);
            var samples = MemoryMarshal.Cast<byte, short>(bytes);
            float peak = 0;
            for (int sample = channel; sample < samples.Length; sample += 4)
                peak = Math.Max(peak, Math.Abs(samples[sample] / (float)short.MaxValue));
            return peak;
        }
    }

    [TestMethod]
    public void FullTriggerPullRisesAcrossBuffersAndThenSustains()
    {
        var format = new WasapiMixFormat(48000, 4, 32, 32, 16, 0x33, WasapiSampleEncoding.Float);
        var rumble = new TriggerRumble(format, DualSenseGrip.Left);
        const int frames = 2400;
        var bytes = new byte[frames * format.BlockAlign];
        var peaks = new float[12];
        var cycles = new int[12];
        for (int block = 0; block < peaks.Length; block++)
        {
            Assert.AreEqual(frames, rumble.Fill(bytes, 255));
            var signal = MemoryMarshal.Cast<byte, float>(bytes);
            peaks[block] = Peak(signal, 2);
            for (int sample = 6; sample < signal.Length; sample += 4)
                if (signal[sample - 4] < 0 && signal[sample] >= 0) cycles[block]++;
        }

        Assert.IsTrue(peaks[0] is > 0.45f and < 0.48f);
        Assert.IsTrue(peaks[3] is > 0.51f and < 0.54f);
        Assert.IsTrue(peaks[6] is > 0.58f and < 0.61f);
        Assert.IsTrue(peaks[9..].All(peak => peak > 0.63f));
        Assert.IsTrue(cycles[9..].Sum() > cycles[..3].Sum(), "The carrier should rise in pitch as well as strength.");
    }

    [TestMethod]
    public void TriggerRendererWaitsSilentlyAndPlaysAPressAndReleaseBetweenBuffers()
    {
        var format = new WasapiMixFormat(48000, 4, 32, 32, 16, 0x33, WasapiSampleEncoding.Float);
        var rumble = new TriggerRumble(format, DualSenseGrip.Left);
        var gate = new TriggerReleaseGate(0);
        const int bufferFrames = 480;
        var buffer = new byte[bufferFrames * format.BlockAlign];
        Assert.AreEqual(bufferFrames, rumble.Fill(buffer, gate.ReadTravel()));
        Assert.IsFalse(buffer.Any(sample => sample != 0));

        gate.Observe(1);
        gate.Observe(0);
        Assert.IsTrue(gate.Completed);
        Assert.AreEqual((byte?)1, gate.ReadTravel());
        Assert.IsNull(gate.ReadTravel());

        var quick = new TriggerReleaseGate(0);
        quick.Observe(1);
        quick.Observe(0);
        int frames = 0;
        float peak = 0;
        int written = bufferFrames;
        for (int block = 0; block < 22 && written == bufferFrames; block++)
        {
            written = rumble.Fill(buffer, quick.ReadTravel());
            frames += written;
            var signal = MemoryMarshal.Cast<byte, float>(buffer);
            peak = Math.Max(peak, Peak(signal, 2));
            Assert.AreEqual(0f, Peak(signal, 3));
        }
        Assert.IsTrue(frames is > 10100 and < 10560, "The short cue should sustain, fade, and complete.");
        Assert.IsTrue(peak is > 0.45f and < 0.46f, "Even the smallest squeeze should produce a cue.");
    }

    [TestMethod]
    public void TriggerStepCompletesOnZeroAfterAnyPositiveTravel()
    {
        var quick = new TriggerReleaseGate(0);
        quick.Observe(0);
        Assert.IsFalse(quick.Completed);
        quick.Observe(1);
        Assert.IsTrue(quick.Pressed);
        Assert.IsFalse(quick.Completed);
        quick.Observe(0);
        Assert.IsTrue(quick.Completed);

        var varied = new TriggerReleaseGate(0);
        varied.Observe(64);
        varied.Observe(255);
        varied.Observe(1);
        Assert.IsTrue(varied.Pressed);
        Assert.IsFalse(varied.Completed, "A partial release must leave the trigger step active.");
        varied.Observe(0);
        Assert.IsTrue(varied.Completed);
    }

    [TestMethod]
    public void TriggerStepRequiresNeutralBeforePressAndDisconnectionIsNotRelease()
    {
        var gate = new TriggerReleaseGate(128);
        gate.Observe(255);
        Assert.IsFalse(gate.Pressed);
        gate.Observe(0);
        Assert.IsFalse(gate.Completed);
        gate.Observe(32);
        Assert.IsTrue(gate.Pressed);
        gate.Observe(null);
        Assert.IsFalse(gate.Pressed);
        gate.Observe(0);
        Assert.IsFalse(gate.Completed);
        gate.Observe(16);
        gate.Observe(0);
        Assert.IsTrue(gate.Completed);
    }

    [TestMethod]
    public void DualSenseBeatAlternatesMusicAndGripHitsInSync()
    {
        var format = new WasapiMixFormat(48000, 4, 32, 32, 16, 0x33, WasapiSampleEncoding.Float);
        var samples = MemoryMarshal.Cast<byte, float>(WasapiTestSignal.BuildPattern(format, true));
        Assert.IsTrue(samples.Length / 4.0 / format.SampleRate is > 8.5 and < 9);
        Assert.IsTrue(Peak(samples, 0) is > 0.30f and < 0.65f);
        Assert.IsTrue(Peak(samples, 1) is > 0.30f and < 0.65f);
        for (int beat = 0; beat < 16; beat++)
        {
            int side = beat % 2;
            double start = 0.12 + beat * 60.0 / 112;
            var attack = Slice(samples, start, 0.08);
            Assert.IsTrue(Peak(attack, side) > 0.2f);
            Assert.IsTrue(Peak(attack, side + 2) > 0.24f);
            var quarter = Slice(samples, start, 0.50);
            Assert.AreEqual(0f, Peak(quarter, 1 - side), $"Beat {beat} leaked to the other audio channel.");
            Assert.AreEqual(0f, Peak(quarter, 3 - side), $"Beat {beat} leaked to the other grip.");
            var gap = Slice(samples, start + 0.25, 0.25);
            Assert.AreEqual(0f, Peak(gap, side + 2), $"Beat {beat} left a continuous buzz.");
        }
    }

    [TestMethod]
    public void PcmBeatKeepsSignalsSeparatedAndMatchesFloatTiming()
    {
        var floatFormat = new WasapiMixFormat(48000, 4, 32, 32, 16, 0x33, WasapiSampleEncoding.Float);
        var pcmFormat = new WasapiMixFormat(48000, 4, 16, 16, 8, 0x603, WasapiSampleEncoding.Pcm);
        var floats = MemoryMarshal.Cast<byte, float>(WasapiTestSignal.BuildPattern(floatFormat, true));
        var pcm = MemoryMarshal.Cast<byte, short>(WasapiTestSignal.BuildPattern(pcmFormat, true));
        Assert.AreEqual(floats.Length, pcm.Length);
        for (int sample = 0; sample < pcm.Length; sample++)
        {
            if (Math.Abs(floats[sample] - pcm[sample] / (float)short.MaxValue) > 1f / short.MaxValue)
            {
                Assert.Fail($"The PCM beat differs from the float mix at sample {sample}.");
            }
        }
    }

    [TestMethod]
    public void SpeakerTestCanUseMonoOutputAndDualSenseTestRequiresFourChannels()
    {
        var format = new WasapiMixFormat(44100, 1, 16, 16, 2, 0, WasapiSampleEncoding.Pcm);
        var samples = MemoryMarshal.Cast<byte, short>(WasapiTestSignal.BuildPattern(format, false));
        Assert.IsTrue(samples[100] != 0);
        Assert.IsTrue(samples[samples.Length / 2 + 100] != 0);
        Assert.ThrowsExactly<NotSupportedException>(() => WasapiTestSignal.BuildPattern(format, true));
    }

    private static float Peak(ReadOnlySpan<float> samples, int channel)
    {
        float peak = 0;
        for (int sample = channel; sample < samples.Length; sample += 4)
            peak = Math.Max(peak, Math.Abs(samples[sample]));
        return peak;
    }

    private static ReadOnlySpan<float> Slice(ReadOnlySpan<float> samples, double start, double length) =>
        samples.Slice((int)Math.Round(start * 48000) * 4, (int)Math.Round(length * 48000) * 4);
}
