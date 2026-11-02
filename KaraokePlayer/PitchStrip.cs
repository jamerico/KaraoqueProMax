using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaraokeCore;

namespace KaraokePlayer;

/// <summary>
/// The scrolling strip over the video: rows are notes, lit by how well each note fits the song's harmony at that
/// moment (chord tones brightest); the singer's voice is drawn on top, green/yellow/red by quality.
/// A thin orange band at the bottom shows when the lyrics say to sing. "Now" is the vertical line at 80%.
/// </summary>
public sealed class PitchStrip
{
    public const int W = 960, H = 150, Rows = 24;
    const double Past = 4.0, Future = 1.0;
    public WriteableBitmap Bitmap { get; } = new(W, H, 96, 96, PixelFormats.Pbgra32, null);
    readonly int[] _px = new int[W * H];
    double _lo = 48;                 // lowest visible MIDI note (follows the singer)

    static int Argb(int a, int r, int g, int b) => a << 24 | (r * a / 255) << 16 | (g * a / 255) << 8 | (b * a / 255);

    public void Render(double now, SongAnalysis? song, int keyShift, IReadOnlyList<FrameJudgement> recent)
    {
        // follow the singer's register
        var sung = recent.Where(j => j.Voiced && j.Time > now - 3).Select(j => (double)j.Midi).OrderBy(m => m).ToList();
        if (sung.Count > 10) { double target = Math.Clamp(sung[sung.Count / 2] - Rows / 2.0, 30, 70); _lo += (target - _lo) * 0.05; }
        double rowH = H / (double)Rows, nowX = W * Past / (Past + Future);

        for (int x = 0; x < W; x++)
        {
            double t = now - Past + x * (Past + Future) / W;
            float[]? c = song?.ChromaAt(Math.Max(0, t));
            bool singHere = song != null && t >= 0 && song.SingingAt(t, 0.05);
            for (int y = 0; y < H; y++)
            {
                int color;
                if (y >= H - 4) color = singHere ? Argb(230, 255, 150, 30) : Argb(120, 20, 20, 30);
                else if (c == null) color = Argb(150, 15, 15, 25);
                else
                {
                    double midi = _lo + (H - 1 - y) / rowH;
                    int pc = (int)((((long)Math.Round(midi) - keyShift) % 12 + 12) % 12);
                    double fit = Math.Clamp((0.65 * c[pc] + 0.35 * song!.KeyProfile[pc] - 0.30) / 0.45, 0, 1);
                    bool rowEdge = (int)((H - 1 - y) % rowH) == 0;
                    // three clear levels: chord tone (bright), key tone (dim), anything else (dark)
                    color = fit >= 0.8 ? Argb(210, 40, 170, 210) : fit >= 0.45 ? Argb(170, 25, 60, 95) : Argb(150, 12, 14, 24);
                    if (rowEdge) color = Argb(170, 8, 8, 14);
                    if (pc == 0 && (int)((H - 1 - y) % rowH) == 1) color = Argb(200, 90, 90, 110);   // faint line under each Dó
                }
                _px[y * W + x] = color;
            }
        }
        // now line
        for (int y = 0; y < H - 4; y++) _px[y * W + (int)nowX] = Argb(255, 255, 255, 255);
        // the singer
        foreach (var j in recent)
        {
            if (!j.Voiced || j.Time < now - Past || j.Time > now) continue;
            int x = (int)((j.Time - (now - Past)) / (Past + Future) * W);
            int y = (int)(H - 1 - (j.Midi - _lo) * rowH);
            if (y < 2 || y >= H - 7) continue;
            int col = j.Quality >= 0.7f ? Argb(255, 60, 230, 90) : j.Quality >= 0.4f ? Argb(255, 250, 210, 40) : Argb(255, 240, 70, 60);
            for (int dy = -2; dy <= 2; dy++) for (int dx = -1; dx <= 1; dx++)
                { int xx = x + dx, yy = y + dy; if (xx >= 0 && xx < W) _px[yy * W + xx] = col; }
        }
        Bitmap.WritePixels(new Int32Rect(0, 0, W, H), _px, W * 4, 0);
    }

    static readonly string[] Names = ["Dó", "Dó#", "Ré", "Ré#", "Mi", "Fá", "Fá#", "Sol", "Sol#", "Lá", "Lá#", "Si"];
    public static string NoteName(float midi) => float.IsNaN(midi) ? "—" : Names[((int)Math.Round(midi) % 12 + 12) % 12] + ((int)Math.Round(midi) / 12 - 1);
}
