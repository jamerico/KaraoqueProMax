using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace KaraokePlayer;

public sealed class Settings
{
    public List<string> Folders { get; set; } = [@"D:\banda_decrypted", @"D:\tradicionais_decrypted"];
    public string? MicId { get; set; }
    public double ExtraLatency { get; set; } = 0;   // extra reaction delay on top of the measured speaker→mic delay
    public double MusicVolume { get; set; } = 80;

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KaraokePlayer");
    static string File => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(System.IO.File.ReadAllText(File)) ?? new(); } catch { return new(); }
    }
    public void Save() { Directory.CreateDirectory(Dir); System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
}

public sealed record ScoreEntry(DateTime When, string Singer, string Song, int Score, float Afinacao, float Ritmo, float Presenca)
{
    public string Line => $"{Score,3}  {Singer} — {Song}";
}

public static class History
{
    static string File => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KaraokePlayer", "placar.json");
    public static List<ScoreEntry> Load()
    {
        try { return JsonSerializer.Deserialize<List<ScoreEntry>>(System.IO.File.ReadAllText(File)) ?? []; } catch { return []; }
    }
    public static void Save(List<ScoreEntry> all) { Directory.CreateDirectory(Path.GetDirectoryName(File)!); System.IO.File.WriteAllText(File, JsonSerializer.Serialize(all)); }
}

/// <summary>Monitor rectangles in physical pixels (for putting the stage on the TV).</summary>
public static class Monitors
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    /// <summary>Bounds of the monitor showing (most of) the window; the primary monitor if the window is hidden.</summary>
    public static RECT Of(IntPtr hwnd)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(hwnd, 1 /* nearest/primary */), ref mi);
        return mi.rcMonitor;
    }

    public static List<(RECT Bounds, bool Primary)> All()
    {
        var list = new List<(RECT, bool)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(h, ref mi)) list.Add((mi.rcMonitor, (mi.dwFlags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}

/// <summary>Smooth song clock: VLC reports time coarsely, so interpolate between its updates.</summary>
public sealed class SongClock
{
    readonly System.Diagnostics.Stopwatch _sw = new();
    long _baseMs; bool _running;
    public void Sync(long vlcMs)
    {
        double now = Now * 1000;
        if (!_running || Math.Abs(now - vlcMs) > 80) { _baseMs = vlcMs; _sw.Restart(); }
    }
    public void Run(bool on) { if (on == _running) return; _baseMs = (long)(Now * 1000); _running = on; _sw.Restart(); }
    public void Reset() { _baseMs = 0; _running = false; _sw.Reset(); }
    public bool Running => _running;
    public double Now => (_baseMs + (_running ? _sw.Elapsed.TotalMilliseconds : 0)) / 1000.0;
}
