namespace HapticBridge.Audio.Wasapi;

public static partial class WasapiTestSignal
{
    private static byte[] BuildDualSenseBeat(WasapiMixFormat format)
    {
        const double beatSeconds = 60.0 / 112;
        int[] bass = [38, 34, 41, 36];
        int[,] arpeggio = { { 62, 69, 65, 72 }, { 58, 65, 62, 69 }, { 65, 69, 72, 77 }, { 60, 67, 64, 72 } };
        var pulses = new List<Pulse>();

        for (int beat = 0; beat < 16; beat++)
        {
            bool left = beat % 2 == 0;
            int front = left ? FrontLeft : FrontRight;
            int rear = left ? RearLeft : RearRight;
            double at = HapticLeadIn + beat * beatSeconds;
            double accent = beat % 4 == 0 ? 1 : 0.9;
            pulses.Add(HapticPulse(rear, at, 0.24, 0.28 * accent));
            pulses.Add(new(front, at, 0.27, 115, 0.26 * accent, 58, Percussive: true, Attack: 0.006));
            pulses.Add(new(front, at, 0.10, 220, 0.08 * accent, 116, Percussive: true, Attack: 0.006));

            double bassHz = NoteHz(bass[beat / 4]);
            pulses.Add(new(front, at, 0.40, bassHz, 0.09, Percussive: true));
            pulses.Add(new(front, at, 0.32, 2 * bassHz, 0.05, Percussive: true));
            if (!left)
            {
                pulses.Add(new(front, at, 0.19, 0, 0.11, Percussive: true, Attack: 0.004, Noise: true));
                pulses.Add(new(front, at, 0.13, 195, 0.08, 135, Percussive: true, Attack: 0.005));
            }

            for (int eighth = 0; eighth < 2; eighth++)
            {
                double noteAt = at + eighth * beatSeconds / 2;
                double noteHz = NoteHz(arpeggio[beat / 4, (beat * 2 + eighth) % 4]);
                pulses.Add(new(front, noteAt, 0.23, noteHz, 0.085, Percussive: true, Attack: 0.009));
                pulses.Add(new(front, noteAt, 0.15, 2 * noteHz, 0.03, Percussive: true, Attack: 0.009));
            }
            pulses.Add(new(front, at + beatSeconds / 2, 0.085, 0, 0.055, Percussive: true, Attack: 0.003, Noise: true));
        }

        return BuildSamples(format, true, HapticLeadIn + 16 * beatSeconds + HapticTail, pulses);

        static double NoteHz(int midi) => 440 * Math.Pow(2, (midi - 69) / 12.0);
    }
}
