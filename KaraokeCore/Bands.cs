namespace KaraokeCore;

/// <summary>48 log-spaced band powers, 90 Hz – 4 kHz, from a 1024-point spectrum at 16 kHz (song and mic use the same).</summary>
public static class Bands
{
    public const int Count = 48, Nfft = 1024;
    public const double Lo = 90, Hi = 4000;
    static readonly int[] BinBand = Build();
    static readonly float[] Window = Enumerable.Range(0, Nfft).Select(i => 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / Nfft)).ToArray();

    static int[] Build()
    {
        var map = new int[Nfft / 2 + 1];
        for (int k = 0; k < map.Length; k++)
        {
            double f = k * 16000.0 / Nfft;
            map[k] = f < Lo || f >= Hi ? -1 : (int)(Count * Math.Log(f / Lo) / Math.Log(Hi / Lo));
        }
        return map;
    }

    public static int BandOf(double hz) => hz < Lo || hz >= Hi ? -1 : (int)(Count * Math.Log(hz / Lo) / Math.Log(Hi / Lo));

    /// <summary>Band powers of the last <see cref="Nfft"/> samples ending at <paramref name="end"/>.</summary>
    public static void Compute(Fft fft, ReadOnlySpan<float> x, int end, float[] frame, float[] mag, float[] dst)
    {
        int start = end - Nfft;
        for (int i = 0; i < Nfft; i++) { int j = start + i; frame[i] = j >= 0 && j < x.Length ? x[j] * Window[i] : 0; }
        fft.Magnitude(frame, mag);
        Array.Clear(dst);
        for (int k = 0; k < mag.Length; k++) { int b = BinBand[k]; if (b >= 0) dst[b] += mag[k] * mag[k]; }
    }
}
