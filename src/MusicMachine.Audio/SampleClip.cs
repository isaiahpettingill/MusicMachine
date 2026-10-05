namespace MusicMachine.Audio;

/// <summary>A transient, mono source for extracting a synth cycle, not a sample-playback instrument.
/// OriginalChannels is zero when an optional external decoder did not report the source layout.
/// Samples belong to the clip; analysis and shaping never modify them.</summary>
public sealed record SampleClip(float[] Samples, int SampleRate, int OriginalChannels, string Name)
{
    public double DurationSeconds => (double)Samples.Length / SampleRate;
}

/// <summary>Period and Start are measured in source samples; Frequency is in hertz.
/// An unreliable result has Period and Frequency zero, so callers must use a manual period.</summary>
public sealed record SampleDetection(int Start, double Period, double Frequency, double Confidence, string Message = "")
{
    public bool HasPitch => Confidence >= .65 && Period > 0;
}
