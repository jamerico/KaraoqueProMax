namespace KaraokeCore;

/// <summary>
/// Predicts how much of the backing track reaches the microphone, per frequency band, so a sung note is only
/// credited when the mic has clearly more energy at that note's harmonics than the speakers alone explain.
/// Learns online: the speaker→mic delay (by matching loudness curves) and the per-band coupling (a low percentile of
/// mic/track power ratios — the singer is sparse in time and frequency, the band is not).
/// </summary>
public sealed class BleedModel
{
    const int Hist = 3000, MaxLag = 70;          // 30 s of 10 ms frames; delays up to 700 ms
    readonly SongAnalysis _song;
    readonly float[][] _mic = new float[Hist][]; readonly double[] _t = new double[Hist];
    int _n, _pos, _since;
    readonly float[] _g = new float[Bands.Count], _noise = new float[Bands.Count];
    float[]? _songTotal;                          // log10 total band power per song frame

    public static float CouplingPercentile = 0.7f;   // tuned on a real laptop-speaker recording (KaraokeSim --pct)

    public double Delay { get; private set; } = 0.30;
    public float DelayConfidence { get; private set; }
    public bool Ready { get; private set; }

    public BleedModel(SongAnalysis song) => _song = song;

    public void Observe(double micTime, float[] bands)
    {
        _mic[_pos] = bands; _t[_pos] = micTime; _pos = (_pos + 1) % Hist; _n = Math.Min(Hist, _n + 1);
        if (++_since % 50 != 0 || _n < 300) return;
        if (_since % 500 == 0 || !Ready) EstimateDelay();
        UpdateCoupling();
        Ready = true;
    }

    IEnumerable<int> Recent(int count) { for (int k = Math.Min(count, _n); k >= 1; k--) yield return ((_pos - k) % Hist + Hist) % Hist; }

    float SongTotal(double t)
    {
        _songTotal ??= BuildTotals();
        int i = (int)Math.Round(t / SongAnalysis.EnvHop) - 1;
        return i < 0 || i >= _songTotal.Length ? -12 : _songTotal[i];
    }

    float[] BuildTotals()
    {
        int nf = _song.SongBands.Length / Bands.Count; var r = new float[nf];
        for (int i = 0; i < nf; i++) { double s = 0; for (int b = 0; b < Bands.Count; b++) s += Math.Pow(10, (float)_song.SongBands[i * Bands.Count + b]); r[i] = (float)Math.Log10(s + 1e-12); }
        return r;
    }

    void EstimateDelay()
    {
        var idx = Recent(2000).ToArray();
        var me = idx.Select(i => (double)MathF.Log10(_mic[i].Sum() + 1e-12f)).ToArray();
        double bestC = -1; int bestL = 0;
        for (int lag = 0; lag <= MaxLag; lag++)
        {
            var se = idx.Select(i => (double)SongTotal(_t[i] - lag * 0.01)).ToArray();
            double c = Corr(me, se);
            if (c > bestC) { bestC = c; bestL = lag; }
        }
        // the delay is fixed for a song: keep the most confident estimate (a singer only lowers the correlation)
        if (bestC > 0.25 && bestC > DelayConfidence) { Delay = bestL * 0.01; DelayConfidence = (float)bestC; }
    }

    static double Corr(double[] a, double[] b)
    {
        double ma = a.Average(), mb = b.Average(), sab = 0, saa = 0, sbb = 0;
        for (int i = 0; i < a.Length; i++) { double x = a[i] - ma, y = b[i] - mb; sab += x * y; saa += x * x; sbb += y * y; }
        return saa > 0 && sbb > 0 ? sab / Math.Sqrt(saa * sbb) : 0;
    }

    void UpdateCoupling()
    {
        var idx = Recent(Hist).ToArray();
        var r = new List<float>(idx.Length); var m = new float[idx.Length];
        for (int b = 0; b < Bands.Count; b++)
        {
            r.Clear();
            for (int k = 0; k < idx.Length; k++)
            {
                int i = idx[k]; float mb = _mic[i][b]; m[k] = mb;
                float sb = _song.BandAt(_t[i] - Delay, b);
                if (sb > 1e-6f) r.Add(MathF.Log10((mb + 1e-12f) / sb));
            }
            r.Sort(); Array.Sort(m);
            _g[b] = r.Count > 50 ? MathF.Pow(10, r[(int)(r.Count * CouplingPercentile)]) : 0;
            _noise[b] = m[(int)(m.Length * 0.1)];
        }
    }

    /// <summary>Mic energy at the first four harmonics of f0 divided by the predicted bleed + noise there.</summary>
    public float Excess(double micTime, float midi, float[] bands)
    {
        if (!Ready) return 0;
        double f0 = 440 * Math.Pow(2, (midi - 69) / 12.0);
        double num = 0, den = 0; int last = -1;
        for (int h = 1; h <= 4; h++)
        {
            int b = Bands.BandOf(h * f0);
            if (b < 0 || b == last) continue; last = b;
            num += bands[b];
            den += _g[b] * _song.BandAt(micTime - Delay, b) + _noise[b];
        }
        return den > 0 ? (float)(num / den) : 0;
    }
}
