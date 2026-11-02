using KaraokeCore;

// KaraokeSim: scores a recorded or synthesised "singer" against a song, offline.
//   KaraokeSim --song <video> --mic <audio file> [--offset s] [--latency s]
//   KaraokeSim --song <video> --make good|random|offbeat|silent [--bleed 0.25] --out <wav>
var opt = new Dictionary<string, string>();
for (int i = 0; i + 1 < args.Length; i += 2) opt[args[i].TrimStart('-')] = args[i + 1];
if (!opt.TryGetValue("song", out var song)) { Console.WriteLine("usage: --song <video> (--mic <audio> | --make <kind> --out <wav>)"); return; }

var sw = System.Diagnostics.Stopwatch.StartNew();
var a = SongAnalysis.Load(Analyzer.CachePath(song));
float[]? songAudio = null;
if (a == null)
{
    a = new SongAnalysis(); songAudio = Ffmpeg.DecodeAudio(song, VoiceTracker.Rate);
    Analyzer.AnalyzeAudio(a, songAudio);
    a.Wipe = new byte[(int)(a.Duration * SongAnalysis.WipeFps) + 60]; a.WipeX = new float[a.Wipe.Length];
    Analyzer.AnalyzeWipe(a, song, default); a.WipeComplete = true; a.Save(Analyzer.CachePath(song));
}
int sung = 0; for (int i = 0; i < a.WipeFrames; i++) if (a.Wipe[i] == 2) sung++;
Console.WriteLine($"analysis {sw.Elapsed.TotalSeconds:F1}s: {a.Duration:F0}s, tuning {a.TuningCents:+0;-0} cents, wipe active {sung / (double)SongAnalysis.WipeFps:F0}s, " +
                  $"key profile {string.Join(" ", a.KeyProfile.Select(v => v.ToString("F2")))}");

if (opt.TryGetValue("make", out var kind))
{
    songAudio ??= Ffmpeg.DecodeAudio(song, VoiceTracker.Rate);
    float bleed = opt.TryGetValue("bleed", out var b) ? float.Parse(b, System.Globalization.CultureInfo.InvariantCulture) : 0.25f;
    var y = Synth.Make(a, songAudio, kind, bleed, new Random(7));
    Synth.WriteWav(opt["out"], y, VoiceTracker.Rate);
    Console.WriteLine($"wrote {opt["out"]}");
    return;
}

var inv = System.Globalization.CultureInfo.InvariantCulture;
var mic = Ffmpeg.DecodeAudio(opt["mic"], VoiceTracker.Rate);
double offset = opt.TryGetValue("offset", out var o) ? double.Parse(o, inv) : 0;
if (opt.TryGetValue("addvoice", out var vpath))
{
    // mix a (bleed-free) voice track, timed on the song clock, into a real recording: the singer follows what they
    // hear, so the voice lands in the mic timeline `voicedelay` after the song moment
    var v = Ffmpeg.DecodeAudio(vpath, VoiceTracker.Rate);
    double vd = opt.TryGetValue("voicedelay", out var d) ? double.Parse(d, inv) : 0.34;
    double db = opt.TryGetValue("voicedb", out var g) ? double.Parse(g, inv) : -25;
    var loud = v.Where(s => Math.Abs(s) > 0.02f).ToArray();
    double rms = Math.Sqrt(loud.Select(s => (double)s * s).DefaultIfEmpty(1e-9).Average());
    float gain = (float)(Math.Pow(10, db / 20) / rms);
    int shift = (int)((vd - offset) * VoiceTracker.Rate);
    for (int j = 0; j < mic.Length; j++) { int k = j - shift; if (k >= 0 && k < v.Length) mic[j] += gain * v[k]; }
    Console.WriteLine($"mixed voice at {db} dBFS rms, delay {vd * 1000:F0} ms");
}
if (opt.TryGetValue("pct", out var pct)) BleedModel.CouplingPercentile = float.Parse(pct, inv);
if (opt.TryGetValue("excess", out var ex)) ScoreEngine.ExcessNeeded = float.Parse(ex, inv);
if (opt.TryGetValue("persist", out var ps)) ScoreEngine.PersistNeeded = int.Parse(ps);
var engine = new ScoreEngine(a) { ExtraLatency = opt.TryGetValue("latency", out var l) ? double.Parse(l, inv) : 0 };
engine.LineDone += r => Console.WriteLine($"  {r.Start,6:F1}-{r.End,6:F1}s  {r.Verdict,-12} pitch {r.Pitch:F2} coverage {r.Coverage:F2}");
var vt = new VoiceTracker();
long frames = 0, voiced = 0;
engine.Judged += j => { frames++; if (j.Voiced) voiced++; };
vt.Frame += f => engine.Add((f.Index * VoiceTracker.Hop + 1028) / (double)VoiceTracker.Rate - 0.032 + offset, f);
for (int i = 0; i < mic.Length; i += 1600) vt.Push(mic.AsSpan(i, Math.Min(1600, mic.Length - i)));
engine.Finish();
var s = engine.Current;
Console.WriteLine($"SCORE {s.Total}  afinação {s.Afinacao:F2}  ritmo {s.Ritmo:F2}  presença {s.Presenca:F2}  (voiced {voiced}/{frames} frames, delay {engine.Bleed.Delay * 1000:F0} ms conf {engine.Bleed.DelayConfidence:F2})");

static class Synth
{
    /// <summary>Synthetic singer + speaker bleed + room noise, aligned to the song.</summary>
    public static float[] Make(SongAnalysis a, float[] songAudio, string kind, float bleed, Random rng)
    {
        int rate = VoiceTracker.Rate, n = songAudio.Length; var y = new float[n];
        const double Seg = 0.3;
        double phase = 0;
        for (double t = 0; t + Seg < a.Duration; t += Seg)
        {
            bool sing = a.SingingAt(t + Seg / 2, 0);
            bool voice = kind switch { "good" or "random" or "real" or "flat" => sing, "offbeat" => !sing && a.EnvelopeAt(t) > 0.01f, _ => false };
            if (!voice || rng.NextDouble() < 0.2) continue;               // 20% breaths
            double midi;
            var c = a.ChromaAt(t + Seg / 2);
            int Pc(double u)
            {   // melody-like choice: chord tone (top 3 of the local chroma), key tone (top 7 of the key profile) or anything
                var byChord = Enumerable.Range(0, 12).OrderByDescending(k => c[k]).ToArray();
                var byKey = Enumerable.Range(0, 12).OrderByDescending(k => a.KeyProfile[k]).ToArray();
                return u < 0.60 ? byChord[rng.Next(3)] : u < 0.95 ? byKey[rng.Next(7)] : rng.Next(12);
            }
            if (kind == "random") midi = 55 + rng.Next(12) + (rng.NextDouble() - 0.5) * 0.9;
            else if (kind is "real" or "flat") midi = 55 + ((Pc(rng.NextDouble()) - 55) % 12 + 12) % 12 + (rng.NextDouble() - 0.5) * 0.3 - (kind == "flat" ? 0.4 : 0);
            else { int pc = Array.IndexOf(c, c.Max()); midi = 55 + ((pc - 55) % 12 + 12) % 12 + (rng.NextDouble() - 0.5) * 0.2; }
            midi += a.TuningCents / 100.0;
            int s0 = (int)(t * rate), s1 = Math.Min(n, (int)((t + Seg * 0.9) * rate));
            for (int s = s0; s < s1; s++)
            {
                double tt = (s - s0) / (double)rate;
                double f = 440 * Math.Pow(2, (midi - 69 + 0.2 * Math.Sin(2 * Math.PI * 5.5 * tt)) / 12);   // vibrato +-20 cents
                phase += 2 * Math.PI * f / rate;
                double env = Math.Min(1, tt / 0.03) * Math.Min(1, (s1 - s) / (0.03 * rate));
                double v = 0; for (int h = 1; h <= 6; h++) v += Math.Sin(h * phase) / (h * h * 0.6 + 0.4);
                y[s] += (float)(0.12 * env * v);
            }
        }
        int delay = rate / 50;
        for (int s = 0; s < n; s++) y[s] += (s >= delay ? bleed * songAudio[s - delay] : 0) + (float)((rng.NextDouble() - 0.5) * 0.004);
        return y;
    }

    public static void WriteWav(string path, float[] x, int rate)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + x.Length * 2); w.Write("WAVEfmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(x.Length * 2);
        float peak = Math.Max(1, x.Max(Math.Abs));
        foreach (var v in x) w.Write((short)(v / peak * 32000));
    }
}
