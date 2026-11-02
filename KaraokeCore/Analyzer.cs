namespace KaraokeCore;

/// <summary>
/// Builds a <see cref="SongAnalysis"/>: the audio part (harmony, tuning, envelope) is computed up front in a few seconds;
/// the lyrics-wipe part streams in from the video in the background, ahead of playback. Results are cached per song.
/// </summary>
public static class Analyzer
{
    const int Rate = 16000, Nfft = 4096, Hop = 800;          // chroma: 256 ms window, 50 ms hop
    const int VW = 320, VH = 180;

    public static string CacheDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KaraokePlayer", "cache");

    public static string CachePath(string video) =>
        System.IO.Path.Combine(CacheDir, System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(video)!) + "_" + System.IO.Path.GetFileNameWithoutExtension(video).Split(' ')[0] + ".kpa");

    /// <summary>Returns the analysis with its audio part complete; the wipe keeps filling in on a background task unless cached.</summary>
    public static async Task<SongAnalysis> AnalyzeAsync(string video, CancellationToken ct = default)
    {
        var cached = SongAnalysis.Load(CachePath(video));
        if (cached != null) return cached;
        var a = new SongAnalysis();
        await Task.Run(() => AnalyzeAudio(a, Ffmpeg.DecodeAudio(video, Rate, ct)), ct);
        int frames = (int)(a.Duration * SongAnalysis.WipeFps) + 2 * SongAnalysis.WipeFps;
        a.Wipe = new byte[frames]; a.WipeX = new float[frames];
        _ = Task.Run(() =>
        {
            try { AnalyzeWipe(a, video, ct); a.WipeComplete = true; a.Save(CachePath(video)); }
            catch (OperationCanceledException) { }
            catch (Exception) { a.WipeComplete = true; }   // no usable video: scoring falls back to "sing anytime"
        }, ct);
        return a;
    }

    public static void AnalyzeAudio(SongAnalysis a, float[] x)
    {
        a.Duration = x.Length / (double)Rate;
        // loudness envelope, 10 ms
        int ne = x.Length / 160; a.Envelope = new float[ne];
        for (int i = 0; i < ne; i++) { double s = 0; for (int j = 0; j < 160; j++) { float v = x[i * 160 + j]; s += v * v; } a.Envelope[i] = (float)Math.Sqrt(s / 160); }
        // band powers per 10 ms, same analysis as the microphone side (for predicting speaker bleed)
        a.SongBands = new Half[ne * Bands.Count];
        {
            var bf = new Fft(Bands.Nfft); var fr = new float[Bands.Nfft]; var mg = new float[Bands.Nfft / 2 + 1]; var bp = new float[Bands.Count];
            for (int i = 0; i < ne; i++)
            {
                Bands.Compute(bf, x, (i + 1) * 160, fr, mg, bp);
                for (int b = 0; b < Bands.Count; b++) a.SongBands[i * Bands.Count + b] = (Half)MathF.Log10(bp[b] + 1e-12f);
            }
        }

        int nf = Math.Max(0, 1 + (x.Length - Nfft) / Hop);
        var win = new float[Nfft]; for (int i = 0; i < Nfft; i++) win[i] = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / Nfft);
        var fft = new Fft(Nfft); var frame = new float[Nfft]; var mags = new float[nf][];
        for (int f = 0; f < nf; f++)
        {
            for (int i = 0; i < Nfft; i++) frame[i] = x[f * Hop + i] * win[i];
            mags[f] = new float[Nfft / 2 + 1]; fft.Magnitude(frame, mags[f]);
        }
        a.TuningCents = EstimateTuning(mags);

        // chroma: bins 80 Hz..2 kHz folded to pitch classes relative to the song's tuning
        int k0 = (int)(80.0 * Nfft / Rate), k1 = (int)(2000.0 * Nfft / Rate);
        var pc = new int[k1 + 1]; var wt = new float[k1 + 1];
        for (int k = k0; k <= k1; k++)
        {
            double m = 69 + 12 * Math.Log2(k * (double)Rate / Nfft / 440) - a.TuningCents / 100.0;
            double r = Math.Round(m); pc[k] = (int)(((long)r % 12 + 12) % 12);
            wt[k] = (float)Math.Pow(Math.Cos(Math.PI * (m - r)), 2);
        }
        var raw = new float[nf][];
        for (int f = 0; f < nf; f++)
        {
            var c = new float[12];
            for (int k = k0; k <= k1; k++) c[pc[k]] += wt[k] * MathF.Sqrt(mags[f][k]);
            raw[f] = c;
        }
        // smooth +-200 ms and normalise each frame to max 1
        a.Chroma = new float[nf][]; var key = new double[12];
        for (int f = 0; f < nf; f++)
        {
            var c = new float[12];
            for (int g = Math.Max(0, f - 4); g <= Math.Min(nf - 1, f + 4); g++) for (int k = 0; k < 12; k++) c[k] += raw[g][k];
            float mx = c.Max();
            if (mx > 1e-6f) for (int k = 0; k < 12; k++) { c[k] /= mx; key[k] += c[k]; }
            a.Chroma[f] = c;
        }
        double km = key.Max();
        for (int k = 0; k < 12; k++) a.KeyProfile[k] = km > 0 ? (float)(key[k] / km) : 1;
    }

    /// <summary>Weighted circular mean of spectral-peak deviations from the A440 semitone grid, in cents.</summary>
    static float EstimateTuning(float[][] mags)
    {
        double sx = 0, sy = 0;
        int k0 = (int)(150.0 * Nfft / Rate), k1 = (int)(2000.0 * Nfft / Rate);
        for (int f = 0; f < mags.Length; f += 2)
        {
            var m = mags[f]; float thr = 0;
            for (int k = k0; k <= k1; k++) thr = Math.Max(thr, m[k]);
            thr *= 0.1f;
            for (int k = k0 + 1; k < k1; k++)
            {
                if (m[k] < thr || m[k] <= m[k - 1] || m[k] < m[k + 1]) continue;
                double a = Math.Log(m[k - 1] + 1e-9), b = Math.Log(m[k] + 1e-9), c = Math.Log(m[k + 1] + 1e-9);
                double d = 0.5 * (a - c) / (a - 2 * b + c);
                double freq = (k + d) * Rate / Nfft, midi = 69 + 12 * Math.Log2(freq / 440);
                double ang = 2 * Math.PI * (midi - Math.Round(midi));
                sx += m[k] * Math.Cos(ang); sy += m[k] * Math.Sin(ang);
            }
        }
        return (float)(Math.Atan2(sy, sx) / (2 * Math.PI) * 100);
    }

    /// <summary>
    /// Colour-independent lyrics-wipe detector: while a line is being sung, the only frame-to-frame change in the
    /// lower part of the picture is a narrow vertical strip (the wipe edge). Background motion is spread out and
    /// a line change alters the whole line at once, so both fail the concentration test.
    /// </summary>
    public static void AnalyzeWipe(SongAnalysis a, string video, CancellationToken ct)
    {
        using var p = Ffmpeg.Start("-i", video, "-an", "-vf", $"fps={SongAnalysis.WipeFps},scale={VW}:{VH}", "-pix_fmt", "rgb24", "-f", "rawvideo", "-");
        var s = p.StandardOutput.BaseStream;
        int fsz = VW * VH * 3, y0 = (int)(VH * 0.15);
        var prev = new byte[fsz]; var cur = new byte[fsz]; var col = new int[VW];
        const int Win = 12;
        int n = 0;
        try
        {
            while (true)
            {
                int got = 0;
                while (got < fsz) { int r = s.Read(cur, got, fsz - got); if (r <= 0) break; got += r; }
                if (got < fsz) break;
                ct.ThrowIfCancellationRequested();
                byte state = 1; float wx = 0;
                if (n > 0)
                {
                    Array.Clear(col); int total = 0;
                    for (int y = y0; y < VH; y++)
                    {
                        int row = y * VW * 3;
                        for (int x = 0; x < VW; x++)
                        {
                            int o = row + x * 3;
                            if (Math.Abs(cur[o] - prev[o]) > 50 || Math.Abs(cur[o + 1] - prev[o + 1]) > 50 || Math.Abs(cur[o + 2] - prev[o + 2]) > 50) { col[x]++; total++; }
                        }
                    }
                    int sum = 0, best = 0, bx = 0;
                    for (int x = 0; x < VW; x++) { sum += col[x]; if (x >= Win) sum -= col[x - Win]; if (sum > best) { best = sum; bx = x; } }
                    if (best >= 4 && best / (total + 10.0) >= 0.45) { state = 2; wx = (bx - Win / 2f) / VW; }
                }
                if (n >= a.Wipe.Length) { Array.Resize(ref a.Wipe, n * 2); Array.Resize(ref a.WipeX, n * 2); }
                a.Wipe[n] = state; a.WipeX[n] = wx; n++; a.WipeFrames = n;
                (prev, cur) = (cur, prev);
            }
        }
        finally { if (!p.HasExited) try { p.Kill(); } catch { } }
        // bridge single-frame dropouts (gaps up to 0.3 s) inside a sung line
        for (int i = 0; i < n; i++)
            if (a.Wipe[i] == 1)
            {
                int j = i; while (j < n && a.Wipe[j] == 1) j++;
                if (i > 0 && j < n && j - i <= 4) for (int k = i; k < j; k++) { a.Wipe[k] = 2; a.WipeX[k] = a.WipeX[i - 1]; }
                i = j - 1;
            }
    }
}
