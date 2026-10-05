namespace MusicMachine.Audio;

/// <summary>Small, deterministic tools for turning a representative source cycle into an embedded synth waveform.
/// Detection deliberately examines at most five 2,048-point windows at no more than 12 kHz (40–2,000 Hz pitch range).
/// It is a monophonic pitch suggestion, not a polyphonic transcription or a promise about unpitched samples.</summary>
public static class SampleAnalysis
{
    private const int AnalysisRate = 12_000;
    private const int WindowPoints = 2048;
    private const int WindowCount = 5;

    public static SampleDetection Detect(SampleClip clip, int start, int length, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateClip(clip);
        if (start < 0 || length <= 0 || start > clip.Samples.Length - length)
            throw new ArgumentOutOfRangeException(nameof(start), "Choose a nonempty region within the sample.");
        int stride = Math.Max(1, (clip.SampleRate + AnalysisRate - 1) / AnalysisRate);
        int points = Math.Min(WindowPoints, length / stride);
        if (points < 32) return new SampleDetection(start, 0, 0, 0, "The region is too short to detect a repeating pitch.");
        int span = points * stride, available = length - span;
        int windows = available == 0 ? 1 : WindowCount;
        int energeticStart = windows == 1 ? start : FindEnergeticStart(clip.Samples, start, length, span, cancellationToken);
        SampleDetection best = new(start, 0, 0, 0, "No clear repeating pitch. Adjust the region or set a period manually.");
        double bestRms = 0;
        bool audible = false;
        var values = new double[points];
        for (int w = 0; w < windows; w++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int offset = start + (windows == 1 ? 0 : (int)((long)available * w / (windows - 1)));
            if (windows > 1 && w == windows / 2) offset = energeticStart;
            double mean = 0, squared = 0;
            for (int i = 0; i < points; i++)
            {
                double sum = 0;
                for (int j = 0; j < stride; j++) sum += FiniteSample(clip.Samples[offset + i * stride + j]);
                values[i] = sum / stride; mean += values[i]; squared += values[i] * values[i];
            }
            mean /= points;
            double rms = Math.Sqrt(Math.Max(0, squared / points - mean * mean));
            if (rms < .00001) continue;
            audible = true;
            var (period, confidence) = FindPeriod(values, (double)clip.SampleRate / stride, cancellationToken);
            if (best.HasPitch && confidence < .65) continue;
            if (!(confidence >= .65 && !best.HasPitch) &&
                (confidence < best.Confidence - .015 || (Math.Abs(confidence - best.Confidence) <= .015 && rms <= bestRms))) continue;
            bestRms = rms;
            if (confidence < .65 || period <= 0)
            {
                best = new SampleDetection(offset, 0, 0, confidence, "No clear repeating pitch. Adjust the region or set a period manually.");
                continue;
            }
            double sourcePeriod = period * stride;
            int cycle = FindCycleStart(clip.Samples, offset, span, sourcePeriod, mean);
            best = new SampleDetection(cycle, sourcePeriod, clip.SampleRate / sourcePeriod, confidence, "Representative repeating cycle found. Audition and adjust if needed.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return !audible ? new SampleDetection(start, 0, 0, 0, "The analyzed windows are silent or have no varying signal.") : best;
    }

    private static int FindEnergeticStart(float[] samples, int start, int length, int span, CancellationToken token)
    {
        // A sparse, bounded scout prevents long leading/trailing silence from hiding a brief note.
        // Uneven sample positions avoid consistently landing on zero crossings of a periodic source.
        const int buckets = 128, probes = 16;
        double bestEnergy = 0;
        int best = start;
        for (int b = 0; b < buckets; b++)
        {
            if ((b & 15) == 0) token.ThrowIfCancellationRequested();
            int left = start + (int)((long)length * b / buckets);
            int right = start + (int)((long)length * (b + 1) / buckets);
            int width = right - left;
            if (width == 0) continue;
            double sum = 0, squared = 0;
            for (int j = 0; j < probes; j++)
            {
                int offset = left + (int)((long)(j * 73 % 257) * width / 257);
                double sample = FiniteSample(samples[offset]); sum += sample; squared += sample * sample;
            }
            double energy = squared / probes - Math.Pow(sum / probes, 2);
            if (energy <= bestEnergy) continue;
            bestEnergy = energy;
            best = Math.Clamp(left + width / 2 - span / 2, start, start + length - span);
        }
        return best;
    }

    private static (double Period, double Confidence) FindPeriod(double[] values, double rate, CancellationToken token)
    {
        int minimum = Math.Max(2, (int)(rate / 2000));
        int maximum = Math.Min((int)Math.Ceiling(rate / 40), (values.Length - 1) / 3);
        if (maximum <= minimum + 1) return (0, 0);
        int count = values.Length - maximum;
        var difference = new double[maximum + 1];
        difference[0] = 1;
        double accumulated = 0;
        // YIN cumulative-mean-normalized difference: the first strong trough avoids integer-period multiples.
        for (int lag = 1; lag <= maximum; lag++)
        {
            if ((lag & 15) == 0) token.ThrowIfCancellationRequested();
            double sum = 0;
            for (int i = 0; i < count; i++) { double d = values[i] - values[i + lag]; sum += d * d; }
            accumulated += sum;
            difference[lag] = accumulated <= 1e-20 ? 1 : sum * lag / accumulated;
        }
        int selected = minimum;
        bool found = false;
        for (int lag = minimum; lag < maximum; lag++)
        {
            if (difference[lag] >= .12) continue;
            while (lag < maximum && difference[lag + 1] < difference[lag]) lag++;
            selected = lag; found = true; break;
        }
        if (!found)
            for (int lag = minimum + 1; lag <= maximum; lag++)
                if (difference[lag] < difference[selected]) selected = lag;
        double fraction = 0;
        if (selected > minimum && selected < maximum)
        {
            double a = difference[selected - 1], b = difference[selected], c = difference[selected + 1];
            double denominator = a - 2 * b + c;
            if (Math.Abs(denominator) > 1e-15) fraction = Math.Clamp(.5 * (a - c) / denominator, -.5, .5);
        }
        return (selected + fraction, Math.Clamp(1 - difference[selected], 0, 1));
    }

    private static int FindCycleStart(float[] samples, int windowStart, int span, double period, double mean)
    {
        int first = windowStart + Math.Max(0, (int)(span * .5 - period));
        int last = Math.Min(windowStart + span - 1 - (int)Math.Ceiling(period), first + (int)Math.Ceiling(period * 2));
        if (last < first) return windowStart;
        int best = first;
        double bestCost = double.PositiveInfinity;
        for (int i = first; i <= last; i++)
        {
            double value = FiniteSample(samples[i]), next = FiniteSample(samples[i + 1]);
            // A rising center crossing makes phase and the sine-morph direction predictable.
            if (next <= value) continue;
            double cost = Math.Abs(value - mean) + .5 * Math.Abs(value - Interpolate(samples, i + period));
            if (cost < bestCost) { bestCost = cost; best = i; }
        }
        return best;
    }

    /// <summary>Resample one source period into PCM16. A smooth endpoint correction removes the wrap discontinuity,
    /// then DC is removed and the cycle is peak-normalized to 95% headroom. The source is never modified.</summary>
    public static short[] Extract(SampleClip clip, int start, double period, int size = 128)
    {
        ValidateClip(clip);
        if (size is < 2 or > 4096) throw new ArgumentOutOfRangeException(nameof(size), "Waveforms require 2–4,096 points.");
        if (start < 0 || start >= clip.Samples.Length || !double.IsFinite(period) || period < 2 || period > clip.Samples.Length - 1d - start)
            throw new ArgumentOutOfRangeException(nameof(period), "The complete period and its endpoint must fit within the sample.");
        var result = new double[size];
        double initial = Interpolate(clip.Samples, start), final = Interpolate(clip.Samples, start + period);
        double target = (initial + final) * .5;
        const double seamWidth = .12;
        for (int i = 0; i < size; i++)
        {
            double phase = (double)i / size;
            double value = Interpolate(clip.Samples, start + phase * period);
            if (phase < seamWidth)
            {
                double fade = .5 + .5 * Math.Cos(Math.PI * phase / seamWidth);
                value += (target - initial) * fade;
            }
            else if (phase > 1 - seamWidth)
            {
                double fade = .5 + .5 * Math.Cos(Math.PI * (1 - phase) / seamWidth);
                value += (target - final) * fade;
            }
            result[i] = value;
        }
        return Normalize(result);
    }

    /// <summary>Return a fresh derived waveform for normalized 0–1 smoothing, saturation and phase-matched sine morph.
    /// Always apply to the original extracted cycle so moving controls back cannot accumulate destructive edits.</summary>
    public static short[] Shape(short[] baseWave, double smoothing, double drive, double sineBlend)
    {
        ArgumentNullException.ThrowIfNull(baseWave);
        if (baseWave.Length is < 2 or > 4096) throw new ArgumentOutOfRangeException(nameof(baseWave));
        ValidateControl(smoothing, nameof(smoothing)); ValidateControl(drive, nameof(drive)); ValidateControl(sineBlend, nameof(sineBlend));
        if (smoothing == 0 && drive == 0 && sineBlend == 0) return (short[])baseWave.Clone();
        var wave = new double[baseWave.Length];
        for (int i = 0; i < wave.Length; i++) wave[i] = baseWave[i] / 32768d;
        if (smoothing > 0)
        {
            int radius = Math.Max(1, (int)Math.Round(smoothing * wave.Length / 16));
            var next = new double[wave.Length];
            for (int pass = 0; pass < 2; pass++)
            {
                double sum = 0;
                for (int j = -radius; j <= radius; j++) sum += wave[(j + wave.Length) % wave.Length];
                for (int i = 0; i < wave.Length; i++)
                {
                    next[i] = wave[i] * (1 - smoothing) + sum / (radius * 2 + 1) * smoothing;
                    sum += wave[(i + radius + 1) % wave.Length] - wave[(i - radius + wave.Length) % wave.Length];
                }
                (wave, next) = (next, wave);
            }
        }
        if (drive > 0)
        {
            double gain = 1 + drive * 12, scale = Math.Tanh(gain);
            for (int i = 0; i < wave.Length; i++) wave[i] = Math.Tanh(wave[i] * gain) / scale;
        }
        if (sineBlend > 0)
        {
            double sin = 0, cos = 0;
            for (int i = 0; i < wave.Length; i++)
            {
                double angle = i * 2 * Math.PI / wave.Length;
                sin += wave[i] * Math.Sin(angle); cos += wave[i] * Math.Cos(angle);
            }
            double phase = Math.Abs(sin) + Math.Abs(cos) > 1e-12 ? Math.Atan2(cos, sin) : 0;
            for (int i = 0; i < wave.Length; i++) wave[i] = wave[i] * (1 - sineBlend) + Math.Sin(i * 2 * Math.PI / wave.Length + phase) * sineBlend;
        }
        return Normalize(wave);
    }

    private static short[] Normalize(double[] wave)
    {
        double mean = wave.Average(), peak = 0;
        for (int i = 0; i < wave.Length; i++) { wave[i] -= mean; peak = Math.Max(peak, Math.Abs(wave[i])); }
        var result = new short[wave.Length];
        if (peak < 1e-10) return result;
        for (int i = 0; i < wave.Length; i++) result[i] = (short)Math.Clamp((int)Math.Round(wave[i] / peak * 31128), -31128, 31128);
        return result;
    }

    private static void ValidateControl(double value, string parameter)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1) throw new ArgumentOutOfRangeException(parameter, "Shaping controls range from 0 to 1.");
    }

    private static void ValidateClip(SampleClip clip)
    {
        ArgumentNullException.ThrowIfNull(clip);
        if (clip.Samples is null || clip.Samples.Length is < 1 or > SampleImporter.MaxSamples || clip.SampleRate is < 1 or > SampleImporter.MaxSampleRate ||
            clip.Samples.Length > (long)clip.SampleRate * SampleImporter.MaxDurationSeconds)
            throw new ArgumentException("Invalid or oversized source clip.", nameof(clip));
    }

    private static double FiniteSample(float value)
    {
        if (!float.IsFinite(value)) throw new ArgumentException("Source audio must contain finite samples.");
        return Math.Clamp(value, -1, 1);
    }

    private static double Interpolate(float[] samples, double position)
    {
        int left = (int)position, right = Math.Min(left + 1, samples.Length - 1);
        double fraction = position - left;
        return FiniteSample(samples[left]) * (1 - fraction) + FiniteSample(samples[right]) * fraction;
    }
}
