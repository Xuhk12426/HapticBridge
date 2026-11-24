using System.Runtime.InteropServices;

namespace HapticBridge.Audio.Wasapi;

internal sealed class TriggerRumble
{
    private const float FullGain = 0.64f;
    private const double FullFrequency = 115;
    private const double FullCycle = 2 * Math.PI;
    private readonly WasapiMixFormat _format;
    private readonly int _channel;
    private readonly float _startGain;
    private readonly double _startFrequency;
    private readonly float _squeezeStep;
    private readonly float _easeStep;
    private readonly float _fadeInStep;
    private readonly float _fadeOutStep;
    private int _minimumPulseFrames;
    private float _targetDepth;
    private float _depth;
    private float _envelope;
    private double _phase;

    public TriggerRumble(WasapiMixFormat format, DualSenseGrip grip)
    {
        _format = format;
        _channel = WasapiTestSignal.GripChannel(grip);
        // The right grip only registered at high travel in the Kishi test, so it starts above the previous maximum.
        (_startGain, _startFrequency) = grip == DualSenseGrip.Right ? (0.51f, 110) : (0.45f, 100);
        _minimumPulseFrames = (int)(format.SampleRate * 0.15);
        _squeezeStep = 1 / (format.SampleRate * 0.42f);
        _easeStep = 1 / (format.SampleRate * 0.18f);
        _fadeInStep = 1 / (format.SampleRate * 0.012f);
        _fadeOutStep = 1 / (format.SampleRate * 0.065f);
    }

    public int Fill(Span<byte> buffer, byte? travel)
    {
        buffer.Clear();
        int capacity = buffer.Length / _format.BlockAlign;
        if (travel == 0) return capacity; // Keep the output running while the trigger prompt waits.

        if (travel is { } pressed) _targetDepth = pressed / 255f;
        if (_targetDepth == 0) return 0;
        bool isFloat = _format.Encoding == WasapiSampleEncoding.Float;
        var floats = isFloat ? MemoryMarshal.Cast<byte, float>(buffer) : default;
        var pcm = isFloat ? default : MemoryMarshal.Cast<byte, short>(buffer);

        int frame = 0;
        for (; frame < capacity; frame++)
        {
            // A short tap still plays its minimum cue. A held trigger tracks depth without restarting the waveform.
            if (travel.HasValue || _minimumPulseFrames > 0)
            {
                _depth += Math.Clamp(_targetDepth - _depth, -_easeStep, _squeezeStep);
                _envelope = Math.Min(1, _envelope + _fadeInStep);
                if (_minimumPulseFrames > 0) _minimumPulseFrames--;
            }
            else
            {
                _envelope = Math.Max(0, _envelope - _fadeOutStep);
                if (_envelope == 0) break;
            }

            double frequency = _startFrequency + (FullFrequency - _startFrequency) * _depth;
            float gain = _startGain + (FullGain - _startGain) * _depth * (0.8f + 0.2f * _depth);
            float sample = _envelope * gain * (float)Math.Sin(_phase);
            _phase += FullCycle * frequency / _format.SampleRate;
            if (_phase >= FullCycle) _phase -= FullCycle;

            int offset = frame * _format.Channels + _channel;
            if (isFloat) floats[offset] = sample;
            else pcm[offset] = (short)Math.Round(sample * short.MaxValue);
        }

        return frame;
    }
}
