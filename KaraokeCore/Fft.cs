namespace KaraokeCore;

/// <summary>In-place radix-2 complex FFT; magnitude helper for real frames.</summary>
public sealed class Fft
{
    readonly int _n; readonly float[] _cos, _sin; readonly int[] _rev;
    readonly float[] _re, _im;

    public Fft(int n)
    {
        if ((n & (n - 1)) != 0) throw new ArgumentException("n must be a power of two");
        _n = n; _cos = new float[n / 2]; _sin = new float[n / 2]; _rev = new int[n];
        for (int i = 0; i < n / 2; i++) { _cos[i] = MathF.Cos(2 * MathF.PI * i / n); _sin[i] = -MathF.Sin(2 * MathF.PI * i / n); }
        int bits = (int)Math.Log2(n);
        for (int i = 0; i < n; i++) { int r = 0; for (int b = 0; b < bits; b++) r |= ((i >> b) & 1) << (bits - 1 - b); _rev[i] = r; }
        _re = new float[n]; _im = new float[n];
    }

    /// <summary>|X[k]| for k = 0..n/2 of a real frame (already windowed).</summary>
    public void Magnitude(ReadOnlySpan<float> frame, Span<float> mag)
    {
        for (int i = 0; i < _n; i++) { _re[_rev[i]] = frame[i]; _im[_rev[i]] = 0; }
        for (int size = 2; size <= _n; size <<= 1)
        {
            int half = size >> 1, step = _n / size;
            for (int start = 0; start < _n; start += size)
                for (int k = 0; k < half; k++)
                {
                    float c = _cos[k * step], s = _sin[k * step];
                    int a = start + k, b = a + half;
                    float tr = _re[b] * c - _im[b] * s, ti = _re[b] * s + _im[b] * c;
                    _re[b] = _re[a] - tr; _im[b] = _im[a] - ti; _re[a] += tr; _im[a] += ti;
                }
        }
        for (int k = 0; k <= _n / 2; k++) mag[k] = MathF.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]);
    }
}
