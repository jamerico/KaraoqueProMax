using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace KaraokePlayer;

/// <summary>Single instance: a second launch brings the running app to the front instead of starting another
/// (several copies starting together block each other while loading VLC).</summary>
public partial class App : Application
{
    static Mutex? _single;

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--warmup"))
        {   // run by the installer: VLC's first load on a new PC indexes its plugins (about a minute); do it once now
            LibVLCSharp.Shared.Core.Initialize();
            using (new LibVLCSharp.Shared.LibVLC()) { }
            Shutdown();
            return;
        }
        _single = new Mutex(true, "KaraokePlayer.SingleInstance", out bool first);
        if (!first)
        {
            var me = Process.GetCurrentProcess();
            var other = Process.GetProcessesByName(me.ProcessName).FirstOrDefault(p => p.Id != me.Id && p.MainWindowHandle != IntPtr.Zero);
            if (other != null) { ShowWindow(other.MainWindowHandle, 9); SetForegroundWindow(other.MainWindowHandle); }
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }
}
