using System.Diagnostics;

namespace KaraokeCore;

/// <summary>Locates ffmpeg.exe and runs it with raw output on stdout.</summary>
public static class Ffmpeg
{
    static string? _path;

    public static string Path
    {
        get => _path ??= Find() ?? throw new FileNotFoundException("ffmpeg.exe não encontrado. Instale com: winget install Gyan.FFmpeg");
        set => _path = value;
    }

    static string? Find()
    {
        var bundled = System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");   // shipped with the installer
        if (File.Exists(bundled)) return bundled;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
        {
            var p = System.IO.Path.Combine(dir.Trim(), "ffmpeg.exe");
            if (File.Exists(p)) return p;
        }
        var winget = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(winget))
            foreach (var d in Directory.GetDirectories(winget, "Gyan.FFmpeg*"))
            {
                var hit = Directory.GetFiles(d, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (hit != null) return hit;
            }
        return null;
    }

    public static Process Start(params string[] args)
    {
        var psi = new ProcessStartInfo(Path) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error"); psi.ArgumentList.Add("-nostdin");
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { };   // drain stderr so ffmpeg never blocks on it
        p.BeginErrorReadLine();
        return p;
    }

    /// <summary>Decodes the audio of any file to mono float samples at the given rate.</summary>
    public static float[] DecodeAudio(string file, int rate, CancellationToken ct = default)
    {
        using var p = Start("-i", file, "-vn", "-ac", "1", "-ar", rate.ToString(), "-f", "s16le", "-");
        using var ms = new MemoryStream();
        var buf = new byte[1 << 16]; int n;
        var s = p.StandardOutput.BaseStream;
        while ((n = s.Read(buf, 0, buf.Length)) > 0) { ct.ThrowIfCancellationRequested(); ms.Write(buf, 0, n); }
        p.WaitForExit();
        var raw = ms.GetBuffer(); int count = (int)ms.Length / 2;
        var x = new float[count];
        for (int i = 0; i < count; i++) x[i] = (short)(raw[2 * i] | raw[2 * i + 1] << 8) / 32768f;
        return x;
    }
}
