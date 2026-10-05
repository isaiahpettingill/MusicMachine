namespace MusicMachine.Core;

/// <summary>Unfiltered, unnormalized oscillator shapes for editing and deterministic preset conversion.</summary>
public static class WaveformShape
{
    public const int EditorSamples = 128;
    public static double Triangle(double phase, double peak)
        => peak == .5 ? 1 - 4 * Math.Abs(phase - .5) : phase < peak ? -1 + 2 * phase / peak : 1 - 2 * (phase - peak) / (1 - peak);

    public static double Sample(short[] samples, double phase)
    {
        if (samples.Length == 0) return 0;
        phase -= Math.Floor(phase);
        var x = phase * samples.Length; var a = (int)x; var b = (a + 1) % samples.Length;
        return (samples[a] + (samples[b] - samples[a]) * (x - a)) / 32768d;
    }

    /// <summary>Returns a phase-zero, unit-gain cycle. Amplitude and phase stay separate and are never baked twice.
    /// Noise conversion deliberately freezes this documented fixed-seed cycle; live Noise remains stochastic.</summary>
    public static short[] Cycle(Instrument instrument, int frame = -1, int length = EditorSamples)
    {
        if (length is < 2 or > SongLimits.MaxWaveSamples) throw new ArgumentOutOfRangeException(nameof(length));
        var result = new short[length]; uint random = 0x4D555349;
        for (var n = 0; n < result.Length; n++)
        {
            var p = n / (double)length;
            random ^= random << 13; random ^= random >> 17; random ^= random << 5;
            var value = instrument.Waveform switch
            {
                Waveform.Triangle => Triangle(p, instrument.TrianglePeak),
                Waveform.Saw => 2 * p - 1,
                Waveform.Square => p < instrument.SquareWidth ? instrument.WaveHigh : instrument.WaveLow,
                Waveform.Pulse => p < instrument.PulseWidth ? instrument.WaveHigh : instrument.WaveLow,
                Waveform.Noise => random / 2147483648d - 1,
                Waveform.Custom => Sample(instrument.CustomWave, p),
                Waveform.Wavetable => Table(instrument, frame, p),
                _ => Math.Sin(p * 2 * Math.PI)
            };
            result[n] = (short)Math.Clamp(Math.Round(value * 32768), short.MinValue, short.MaxValue);
        }
        return result;
    }

    private static double Table(Instrument instrument, int frame, double phase)
    {
        if (instrument.Wavetable.Count == 0) return Sample(instrument.CustomWave, phase);
        if (frame >= 0) return Sample(instrument.Wavetable[Math.Clamp(frame, 0, instrument.Wavetable.Count - 1)], phase);
        var position = instrument.WavetablePosition * (instrument.Wavetable.Count - 1);
        var a = (int)position; var b = Math.Min(a + 1, instrument.Wavetable.Count - 1);
        return Sample(instrument.Wavetable[a], phase) * (1 - (position - a)) + Sample(instrument.Wavetable[b], phase) * (position - a);
    }
}
