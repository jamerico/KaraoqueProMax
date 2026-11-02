namespace KaraokeCore;

/// <summary>
/// What the scorer knows about a song: the harmony of the backing track (chroma) over time, the song's tuning,
/// its loudness envelope (to tell the singer from speaker bleed) and when the lyrics wipe says to sing.
/// The wipe part fills in progressively while the video is being analysed.
/// </summary>
public sealed class SongAnalysis
{
    public const double ChromaHop = 0.05;   // s per chroma frame
    public const double EnvHop = 0.01;      // s per envelope frame
    public const int WipeFps = 15;
    const int Magic = 0x4B504131;           // "KPA1"
    const int Version = 3;

    public double Duration;
    public float TuningCents;               // offset of the song's notes from A440 equal temperament
    public float[][] Chroma = [];           // [frame][12] smoothed, max-normalised to 1
    public float[] KeyProfile = new float[12];
    public float[] Envelope = [];           // RMS of the backing track per 10 ms
    public Half[] SongBands = [];           // [frame * Bands.Count + b] log10 band powers per 10 ms (frame i ends at (i+1)*10 ms)
    public byte[] Wipe = [];                // per video frame: 0 = not analysed yet, 1 = lyrics still, 2 = wipe advancing
    public float[] WipeX = [];              // wipe edge position (0..1 of width) when advancing
    volatile int _wipeFrames;
    public int WipeFrames { get => _wipeFrames; set => _wipeFrames = value; }
    public bool WipeComplete { get; set; }

    /// <summary>Seconds of video for which the wipe is known.</summary>
    public double WipeKnownUntil => WipeComplete ? double.MaxValue : WipeFrames / (double)WipeFps;

    public float[] ChromaAt(double t)
    {
        int i = (int)(t / ChromaHop);
        return Chroma.Length == 0 ? KeyProfile : Chroma[Math.Clamp(i, 0, Chroma.Length - 1)];
    }

    public float EnvelopeAt(double t)
    {
        if (Envelope.Length == 0) return 0;
        int i = Math.Clamp((int)(t / EnvHop), 0, Envelope.Length - 1);
        return Envelope[i];
    }

    /// <summary>Band power b of the backing track in the 10 ms frame ending nearest to t (0 outside the song).</summary>
    public float BandAt(double t, int b)
    {
        int i = (int)Math.Round(t / EnvHop) - 1;
        return i < 0 || (i + 1) * Bands.Count > SongBands.Length ? 0 : MathF.Pow(10, (float)SongBands[i * Bands.Count + b]);
    }

    /// <summary>True when the wipe was advancing within ±tol seconds of t (singing expected).</summary>
    public bool SingingAt(double t, double tol = 0.3)
    {
        int a = Math.Max(0, (int)((t - tol) * WipeFps)), b = Math.Min(WipeFrames - 1, (int)((t + tol) * WipeFps));
        for (int i = a; i <= b; i++) if (Wipe[i] == 2) return true;
        return false;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var w = new BinaryWriter(File.Create(path));
        w.Write(Magic); w.Write(Version); w.Write(Duration); w.Write(TuningCents);
        w.Write(Chroma.Length); foreach (var c in Chroma) foreach (var v in c) w.Write(v);
        foreach (var v in KeyProfile) w.Write(v);
        w.Write(Envelope.Length); foreach (var v in Envelope) w.Write(v);
        w.Write(SongBands.Length); foreach (var v in SongBands) w.Write(v);
        w.Write(WipeFrames); w.Write(Wipe, 0, WipeFrames); for (int i = 0; i < WipeFrames; i++) w.Write(WipeX[i]);
    }

    public static SongAnalysis? Load(string path)
    {
        try
        {
            using var r = new BinaryReader(File.OpenRead(path));
            if (r.ReadInt32() != Magic || r.ReadInt32() != Version) return null;
            var a = new SongAnalysis { Duration = r.ReadDouble(), TuningCents = r.ReadSingle() };
            a.Chroma = new float[r.ReadInt32()][];
            for (int i = 0; i < a.Chroma.Length; i++) { a.Chroma[i] = new float[12]; for (int k = 0; k < 12; k++) a.Chroma[i][k] = r.ReadSingle(); }
            for (int k = 0; k < 12; k++) a.KeyProfile[k] = r.ReadSingle();
            a.Envelope = new float[r.ReadInt32()]; for (int i = 0; i < a.Envelope.Length; i++) a.Envelope[i] = r.ReadSingle();
            a.SongBands = new Half[r.ReadInt32()]; for (int i = 0; i < a.SongBands.Length; i++) a.SongBands[i] = r.ReadHalf();
            int n = r.ReadInt32(); a.Wipe = r.ReadBytes(n); a.WipeX = new float[n]; for (int i = 0; i < n; i++) a.WipeX[i] = r.ReadSingle();
            a.WipeFrames = n; a.WipeComplete = true;
            return a;
        }
        catch { return null; }
    }
}
