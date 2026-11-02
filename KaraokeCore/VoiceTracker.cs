namespace KaraokeCore;

/// <summary>One 10 ms analysis frame of the microphone.</summary>
public readonly record struct VoiceFrame(long Index, float Rms, float Midi, float Clarity, float[] Bands)
{
    public bool HasPitch => !float.IsNaN(Midi);
}

/// <summary>
/// Turns a 16 kHz mono microphone stream into 10 ms frames with loudness and pitch (YIN, 70 Hz – 1 kHz).
/// The pitch is median-filtered over 5 frames to drop octave blips.
/// </summary>
public sealed class VoiceTracker
{
    public const int Rate = 16000, Hop = 160;
    const int W = 800, TauMin = Rate / 1000, TauMax = Rate / 70, Need = W + TauMax;
    const float Threshold = 0.15f;

    readonly float[] _buf = new float[Need + Hop]; int _fill;
    readonly float[] _d = new float[TauMax + 1];
    readonly float[] _recent = new float[5]; int _recentN;
    long _index;
    readonly Fft _fft = new(Bands.Nfft); readonly float[] _fr = new float[Bands.Nfft], _mag = new float[Bands.Nfft / 2 + 1];

    public event Action<VoiceFrame>? Frame;

    public void Push(ReadOnlySpan<float> samples)
    {
        foreach (var v in samples)
        {
            _buf[_fill++] = v;
            if (_fill == Need + Hop) { Array.Copy(_buf, Hop, _buf, 0, Need); _fill = Need; }
            if (_fill == Need) Analyse();
        }
    }

    void Analyse()
    {
        double e = 0; for (int i = Need - 400; i < Need; i++) e += _buf[i] * _buf[i];
        float rms = (float)Math.Sqrt(e / 400);
        var (midi, clarity) = Yin();
        // median of the last 5 pitched values (NaN counts as "no pitch" and wins only if it is the majority)
        for (int i = 4; i > 0; i--) _recent[i] = _recent[i - 1];
        _recent[0] = midi; _recentN = Math.Min(5, _recentN + 1);
        var vals = _recent.Take(_recentN).Where(v => !float.IsNaN(v)).OrderBy(v => v).ToArray();
        float med = vals.Length * 2 > _recentN ? vals[vals.Length / 2] : float.NaN;
        if (float.IsNaN(midi)) med = float.NaN;
        var bands = new float[Bands.Count];
        Bands.Compute(_fft, _buf.AsSpan(0, Need), Need, _fr, _mag, bands);
        Frame?.Invoke(new VoiceFrame(_index++, rms, med, clarity, bands));
    }

    (float midi, float clarity) Yin()
    {
        const int off = 0;   // analysis window starts at the oldest buffered sample
        _d[0] = 1; double run = 0;
        for (int tau = 1; tau <= TauMax; tau++)
        {
            double s = 0;
            for (int i = 0; i < W; i++) { float df = _buf[off + i] - _buf[off + i + tau]; s += df * df; }
            run += s; _d[tau] = run > 0 ? (float)(s * tau / run) : 1;
        }
        int best = -1;
        for (int tau = TauMin; tau < TauMax; tau++)
            if (_d[tau] < Threshold) { while (tau + 1 < TauMax && _d[tau + 1] < _d[tau]) tau++; best = tau; break; }
        if (best < 0)
        {   // no dip under the threshold: take the global minimum but report its (low) clarity
            best = TauMin; for (int tau = TauMin; tau < TauMax; tau++) if (_d[tau] < _d[best]) best = tau;
            if (_d[best] > 0.35f) return (float.NaN, 1 - _d[best]);
        }
        float a = _d[best - 1], b = _d[best], c = _d[best + 1];
        float shift = (a - 2 * b + c) != 0 ? 0.5f * (a - c) / (a - 2 * b + c) : 0;
        double f0 = Rate / (best + shift);
        return ((float)(69 + 12 * Math.Log2(f0 / 440)), 1 - b);
    }
}
