using System.Runtime.InteropServices;
using HapticBridge.Audio.Native;
using static HapticBridge.Audio.Wasapi.WasapiLoopbackRoute;

namespace HapticBridge.Audio.Wasapi;

public enum DualSenseHapticCue { Tap, DoubleTap, Flutter, Rumble, Bumper, Stick }
public enum DualSenseGrip { Left, Right }

/// <summary>Plays speaker and controller test signals without changing Windows audio settings.</summary>
public static partial class WasapiTestSignal
{
    private const uint EventCallback = 0x00040000;
    private const double PulseDuration = 0.33;
    private const double PulseSpacing = 0.54;
    private const int FrontLeft = 1 << 0;
    private const int FrontRight = 1 << 1;
    private const int RearLeft = 1 << 2;
    private const int RearRight = 1 << 3;
    private const double HapticLeadIn = 0.12;
    private const double HapticTail = 0.14;
    // The Kishi V2 Pro user felt this sweep on the left trigger in 1.3.5.
    private const double HapticStartHz = 115;
    private const double HapticEndHz = 58;

    public static Task<int> PlayAsync(string endpointId, bool dualSense, CancellationToken stop) =>
        PlayAsync(endpointId, format => BuildPattern(format, dualSense), stop);

    public static Task<int> PlayHapticCueAsync(string endpointId, DualSenseHapticCue cue, DualSenseGrip grip,
        CancellationToken stop) => PlayAsync(endpointId, format => BuildHapticCue(format, cue, grip), stop);

    /// <summary>Reads zero while waiting for a squeeze, positive travel while pressed, and null on release.</summary>
    public static Task<int> PlayTriggerRumbleAsync(string endpointId, DualSenseGrip grip, Func<byte?> readTravel,
        CancellationToken stop) => PlaySignalAsync(endpointId, format =>
        {
            ValidateFormat(format, true);
            var rumble = new TriggerRumble(format, grip);
            return buffer => rumble.Fill(buffer, readTravel());
        }, stop);

    private delegate int FillSignal(Span<byte> buffer);

    private static Task<int> PlayAsync(string endpointId, Func<WasapiMixFormat, byte[]> build, CancellationToken stop) =>
        PlaySignalAsync(endpointId, format =>
        {
            var samples = build(format);
            int offset = 0;
            return buffer =>
            {
                int count = Math.Min(buffer.Length, samples.Length - offset);
                samples.AsSpan(offset, count).CopyTo(buffer);
                offset += count;
                return count / format.BlockAlign;
            };
        }, stop);

    private static Task<int> PlaySignalAsync(string endpointId, Func<WasapiMixFormat, FillSignal> create, CancellationToken stop) =>
        Task.Factory.StartNew(() => Play(endpointId, create, stop), stop,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static unsafe int Play(string endpointId, Func<WasapiMixFormat, FillSignal> create, CancellationToken stop)
    {
        using var ready = new AutoResetEvent(false);
        using var endpoint = new AudioClientEndpoint(endpointId);
        var format = endpoint.Format;
        var fill = create(format);
        endpoint.Initialize(EventCallback, ready);
        var render = endpoint.GetService<IAudioRenderClient>();
        try
        {
            Check(endpoint.Client.GetBufferSize(out uint capacity), "get the test output buffer size");
            int sent = 0;
            bool finished = false;
            WriteFrames(capacity);
            stop.ThrowIfCancellationRequested();
            if (sent == 0) return 0;
            Check(endpoint.Client.Start(), "start the test signal");
            try
            {
                WaitHandle[] events = [stop.WaitHandle, ready];
                while (true)
                {
                    if (WaitHandle.WaitAny(events, 2000) == WaitHandle.WaitTimeout)
                        throw new IOException("Windows stopped signaling the test audio output.");
                    stop.ThrowIfCancellationRequested();
                    Check(endpoint.Client.GetCurrentPadding(out uint padding), "read the test output buffer");
                    int written = !finished && padding < capacity ? WriteFrames(capacity - padding) : 0;
                    if (finished && padding == 0 && written == 0) return sent;
                }
            }
            finally { endpoint.Client.Stop(); }

            int WriteFrames(uint available)
            {
                Check(render.GetBuffer(available, out nint output), "open the test audio buffer");
                uint submitted = 0;
                try
                {
                    if (output == 0) throw new IOException("Windows returned an empty test audio buffer.");
                    var buffer = new Span<byte>((void*)output, checked((int)available * format.BlockAlign));
                    int count = fill(buffer);
                    sent += count;
                    finished = count < available;
                    if (count == 0) return 0;
                    buffer[(count * format.BlockAlign)..].Clear();
                    submitted = available;
                    return count;
                }
                finally { Check(render.ReleaseBuffer(submitted, 0), "release the test audio buffer"); }
            }
        }
        finally { Marshal.ReleaseComObject(render); }
    }

    internal static byte[] BuildPattern(WasapiMixFormat format, bool dualSense)
    {
        if (dualSense) return BuildDualSenseBeat(format);

        int secondChannel = format.Channels == 1 ? FrontLeft : FrontRight;
        return BuildSamples(format, false, 2 * PulseSpacing,
            [new(FrontLeft, 0, PulseDuration, 440, 0.075),
             new(secondChannel, PulseSpacing, PulseDuration, 660, 0.075)]);
    }

    internal static byte[] BuildHapticCue(WasapiMixFormat format, DualSenseHapticCue cue, DualSenseGrip grip)
    {
        int channel = 1 << GripChannel(grip);
        (double Start, double Length, double Gain, double Frequency, double EndFrequency)[] hits = cue switch
        {
            DualSenseHapticCue.Tap => [(0, 0.19, 0.51, 112, 100)],
            DualSenseHapticCue.DoubleTap => [(0, 0.15, 0.49, 110, 98), (0.30, 0.15, 0.51, 110, 98)],
            DualSenseHapticCue.Flutter =>
                [(0, 0.13, 0.49, 113, 101), (0.25, 0.13, 0.49, 113, 101), (0.50, 0.13, 0.52, 114, 103)],
            DualSenseHapticCue.Rumble => [(0, 0.60, 0.56, 102, 115)],
            DualSenseHapticCue.Bumper => [(0, 0.28, 0.54, 108, 98), (0.42, 0.14, 0.50, 114, 105)],
            DualSenseHapticCue.Stick => [(0, 0.14, 0.48, 114, 103), (0.34, 0.35, 0.53, 110, 98)],
            _ => throw new ArgumentOutOfRangeException(nameof(cue))
        };
        var pulses = new List<Pulse>(hits.Length);
        foreach (var (start, length, gain, frequency, endFrequency) in hits)
            pulses.Add(new(channel, HapticLeadIn + start, length, frequency, gain, endFrequency,
                Attack: 0.007, Release: 0.016));
        return BuildSamples(format, true, HapticLeadIn + hits[^1].Start + hits[^1].Length + HapticTail, pulses);
    }

    internal static int GripChannel(DualSenseGrip grip) => grip switch
    {
        DualSenseGrip.Left => 2,
        DualSenseGrip.Right => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(grip))
    };

    private static Pulse HapticPulse(int channels, double start, double duration, double gain) =>
        new(channels, start, duration, HapticStartHz, gain, HapticEndHz);

    private static byte[] BuildSamples(WasapiMixFormat format, bool dualSense, double duration, IReadOnlyList<Pulse> pulses)
    {
        ValidateFormat(format, dualSense);
        bool floatMix = format.Encoding == WasapiSampleEncoding.Float;

        int frames = (int)Math.Round(duration * format.SampleRate);
        byte[] bytes = new byte[frames * format.BlockAlign];
        Span<float> mix = floatMix ? MemoryMarshal.Cast<byte, float>(bytes) : new float[frames * format.Channels];
        foreach (var pulse in pulses)
        {
            int start = (int)Math.Round(pulse.Start * format.SampleRate);
            int framesPerPulse = (int)Math.Round(pulse.Duration * format.SampleRate);
            double sweep = ((pulse.EndFrequency ?? pulse.Frequency) - pulse.Frequency) / (2 * pulse.Duration);
            double attackFrames = format.SampleRate * pulse.Attack;
            double releaseFrames = format.SampleRate * pulse.Release;
            uint noiseState = (uint)start + 1;
            for (int frame = 0; frame < framesPerPulse; frame++)
            {
                double envelope = Math.Min(1, Math.Min(frame / attackFrames, (framesPerPulse - 1 - frame) / releaseFrames));
                if (pulse.Percussive)
                {
                    double remaining = 1 - (double)frame / framesPerPulse;
                    envelope *= remaining * remaining;
                }
                double seconds = frame / (double)format.SampleRate;
                double wave;
                if (pulse.Noise)
                {
                    noiseState = unchecked(noiseState * 1664525u + 1013904223u);
                    wave = 2 * (noiseState / (double)uint.MaxValue) - 1;
                }
                else
                {
                    double cycles = seconds * (pulse.Frequency + sweep * seconds);
                    wave = Math.Sin(2 * Math.PI * cycles);
                }
                float sample = (float)(pulse.Gain * envelope * wave);
                for (int channel = 0; channel < format.Channels; channel++)
                {
                    if ((pulse.Channels & (1 << channel)) == 0) continue;
                    mix[(start + frame) * format.Channels + channel] += sample;
                }
            }
        }
        if (!floatMix)
        {
            var pcm = MemoryMarshal.Cast<byte, short>(bytes);
            for (int sample = 0; sample < pcm.Length; sample++)
                pcm[sample] = (short)Math.Round(Math.Clamp(mix[sample], -1f, 1f) * short.MaxValue);
        }
        return bytes;
    }

    private static void ValidateFormat(WasapiMixFormat format, bool dualSense)
    {
        bool floatMix = format.Encoding == WasapiSampleEncoding.Float && format.ContainerBits == 32;
        bool pcmMix = format.Encoding == WasapiSampleEncoding.Pcm && format.ContainerBits == 16 && format.ValidBits == 16;
        if ((!floatMix && !pcmMix) || format.SampleRate is < 8000 or > 384000 ||
            format.Channels is < 1 or > 18 || format.BlockAlign != format.Channels * (format.ContainerBits / 8))
            throw new NotSupportedException($"The test does not support this Windows mix format ({format}).");
        if (dualSense && (format.Channels != 4 || format.SampleRate != 48000 ||
            format.SpeakerMask is not (0x33 or 0x603)))
            throw new NotSupportedException("The DualSense test requires a 48 kHz four-channel playback device.");
    }

    private readonly record struct Pulse(int Channels, double Start, double Duration, double Frequency,
        double Gain, double? EndFrequency = null, bool Percussive = false, double Attack = 0.025, bool Noise = false,
        double Release = 0.025);
}
