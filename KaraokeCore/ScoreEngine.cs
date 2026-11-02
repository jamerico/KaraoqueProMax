namespace KaraokeCore;

/// <summary>Live judgement of one 10 ms frame, for the on-screen pitch strip.</summary>
public readonly record struct FrameJudgement(double Time, float Midi, float Quality, bool Voiced, bool Expected);

public sealed record LineResult(double Start, double End, string Verdict, float Pitch, float Coverage);

public sealed record ScoreResult(int Total, float Afinacao, float Ritmo, float Presenca);

/// <summary>
/// Scores a singer against a song's harmony and lyric timing.
/// Afinação (60): the sung note belongs to the harmony at that moment (chord tones best, key tones next) and is in tune.
/// Ritmo (25): voice happens while the lyrics wipe says to sing, not in the instrumental parts.
/// Presença (15): how much of the sung parts had a voice (60% voiced = full; breaths and consonants are unvoiced).
/// Speaker bleed: <see cref="BleedModel"/> predicts the backing track's energy at the mic per band; a detected note
/// only counts as voice when its harmonics stand clearly above that prediction.
/// </summary>
public sealed class ScoreEngine
{
    readonly SongAnalysis _song;
    public int KeyShift { get; set; }                  // semitones the music is transposed by
    public double ExtraLatency { get; set; }           // added to the measured speaker→mic delay (singer reaction)
    public BleedModel Bleed { get; }
    public static float ExcessNeeded = 4f;             // mic must be this many times the predicted bleed at the note's harmonics
    public static int PersistNeeded = 3;               // ... in this many of the last 5 frames (bleed spikes are brief)
    int _hist5;                                        // bit history of "excess ok" for the last 5 frames

    // pending frames wait until the wipe analysis has caught up with them
    readonly Queue<(double t, VoiceFrame f)> _pending = new();

    // totals (_expected counts strictly-sung frames for Presença; voice timing uses a tolerant window)
    long _expected, _voicedIn, _voicedOut, _voicedStrict; double _pitchSum;
    // current line
    double _lineStart = -1, _lineLast; long _lineExpected, _lineVoiced; double _linePitch;

    public event Action<FrameJudgement>? Judged;
    public event Action<LineResult>? LineDone;

    public ScoreEngine(SongAnalysis song) { _song = song; Bleed = new BleedModel(song); }

    /// <summary>A mic frame captured at song-clock time <paramref name="micTime"/>.</summary>
    public void Add(double micTime, VoiceFrame f)
    {
        Bleed.Observe(micTime, f.Bands);
        bool ok = f.HasPitch && f.Clarity >= 0.7f && f.Rms > 0.002f && Bleed.Excess(micTime, f.Midi, f.Bands) >= ExcessNeeded;
        _hist5 = ((_hist5 << 1) | (ok ? 1 : 0)) & 31;
        bool voice = ok && System.Numerics.BitOperations.PopCount((uint)_hist5) >= PersistNeeded;
        double t = micTime - Bleed.Delay - ExtraLatency;   // the song moment the singer was responding to
        _pending.Enqueue((t, f with { Midi = voice ? f.Midi : float.NaN }));
        while (_pending.Count > 0 && _pending.Peek().t + 0.4 < _song.WipeKnownUntil) { var (pt, pf) = _pending.Dequeue(); Judge(pt, pf); }
    }

    static bool IsVoice(VoiceFrame f) => f.HasPitch;

    /// <summary>0..1: how well a sung pitch fits the harmony at time t and how close it is to a real note.</summary>
    public float Quality(double t, float midi)
    {
        double m = midi - _song.TuningCents / 100.0;
        double n = Math.Round(m); double cents = Math.Abs(m - n) * 100;
        int pc = (int)((((long)n - KeyShift) % 12 + 12) % 12);
        var c = _song.ChromaAt(t);
        double fit = 0.65 * c[pc] + 0.35 * _song.KeyProfile[pc];
        fit = Smooth((fit - 0.30) / 0.45);
        double tune = cents <= 25 ? 1 : Math.Max(0, 1 - (cents - 25) / 25);
        return (float)(fit * tune);
    }

    static double Smooth(double x) { x = Math.Clamp(x, 0, 1); return x * x * (3 - 2 * x); }

    /// <summary>
    /// Maps the mean frame quality to the Afinação scale: random notes average ~0.45 (a quarter of them hit a chord
    /// tone by chance), so that is the floor; a singer on chord/key tones averages 0.85+.
    /// </summary>
    static float Calibrate(float mean) => Math.Clamp((mean - 0.40f) / 0.50f, 0, 1);

    void Judge(double t, VoiceFrame f)
    {
        bool exp = _song.SingingAt(t, 0.35), strict = _song.SingingAt(t, 0.05), voice = IsVoice(f);
        float q = voice ? Quality(t, f.Midi) : 0;
        if (strict) { _expected++; if (voice) _voicedStrict++; }
        if (exp)
        {
            if (voice) { _voicedIn++; _pitchSum += q; }
            if (_lineStart < 0) { _lineStart = t; _lineExpected = _lineVoiced = 0; _linePitch = 0; }
            _lineExpected++; if (voice) { _lineVoiced++; _linePitch += q; }
            _lineLast = t;
        }
        else
        {
            if (voice) _voicedOut++;
            if (_lineStart >= 0 && t - _lineLast > 0.6) CloseLine();
        }
        Judged?.Invoke(new FrameJudgement(t, voice ? f.Midi : float.NaN, q, voice, exp));
    }

    void CloseLine()
    {
        float cov = _lineExpected > 0 ? Math.Min(1f, _lineVoiced / (_lineExpected * 0.6f)) : 0;
        float pitch = _lineVoiced > 0 ? Calibrate((float)(_linePitch / _lineVoiced)) : 0;
        string v = cov < 0.25f ? "Cadê você?" : (pitch * Math.Min(1, cov / 0.7f)) switch
        {
            >= 0.85f => "Perfeito!", >= 0.7f => "Ótimo!", >= 0.5f => "Bom", >= 0.3f => "Quase…", _ => "Fora do tom"
        };
        LineDone?.Invoke(new LineResult(_lineStart, _lineLast, v, pitch, cov));
        _lineStart = -1;
    }

    /// <summary>Flushes pending frames (end of song) and closes the open line.</summary>
    public void Finish()
    {
        while (_pending.Count > 0) { var (pt, pf) = _pending.Dequeue(); Judge(pt, pf); }
        if (_lineStart >= 0) CloseLine();
    }

    public ScoreResult Current
    {
        get
        {
            float a = _voicedIn > 0 ? Calibrate((float)(_pitchSum / _voicedIn)) : 0;
            float r = _voicedIn + _voicedOut > 0 ? _voicedIn / (float)(_voicedIn + _voicedOut) : 0;
            float p = _expected > 0 ? Math.Min(1f, _voicedStrict / (_expected * 0.6f)) : 0;
            float gate = Math.Min(1f, p / 0.7f);          // full pitch/timing points only for singing most of the song
            int total = (int)Math.Round(60 * a * gate + 25 * r * gate + 15 * p);
            return new ScoreResult(total, a, r, p);
        }
    }
}
