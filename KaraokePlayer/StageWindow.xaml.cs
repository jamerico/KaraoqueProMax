using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using KaraokeCore;

namespace KaraokePlayer;

/// <summary>
/// Full-screen song view: video, harmony/pitch strip, live score, line verdicts, results, and the media controls
/// (auto-hiding bar + keyboard). It only raises requests; MainWindow decides what happens.
/// </summary>
public partial class StageWindow : Window
{
    public PitchStrip StripRenderer { get; } = new();

    public event Action? PauseRequested, NextRequested, BackRequested;
    public event Action<int>? KeyRequested;            // -1 / +1
    public event Action<double>? VolumeRequested;      // 0..100
    public event Action<double>? SeekRequested;        // 0..1

    readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(3) };
    bool _settingVolume;

    public StageWindow()
    {
        InitializeComponent();
        Strip.Source = StripRenderer.Bitmap;
        _hide.Tick += (_, _) => { _hide.Stop(); if (!Controls.IsMouseOver) { Controls.Visibility = Visibility.Collapsed; Cursor = Cursors.None; Overlay.Cursor = Cursors.None; } };
        PreviewKeyDown += OnKey;
        // the overlay lives in its own (layered) window above the video: listen there too, and hide/show it with us
        Overlay.Loaded += (_, _) =>
        {
            var w = GetWindow(Overlay);
            if (w != null && w != this) { _overlayWindow = w; w.PreviewKeyDown += OnKey; w.MouseMove += (_, _) => Poke(); }
        };
        MouseMove += (_, _) => Poke();
    }

    Window? _overlayWindow;

    /// <summary>Full screen on the same monitor as the given window (positioned in WPF units so display scaling stays right).</summary>
    public void ShowFullScreen(Window near)
    {
        WindowState = WindowState.Normal;
        Left = near.Left + 40; Top = near.Top + 40;
        Show();
        WindowState = WindowState.Maximized;
        _overlayWindow?.Show();
        Activate(); Focus();
        Poke();
        Dispatcher.BeginInvoke(() =>
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
            var ow = _overlayWindow;
            System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "karaoke.log"),
                $"{DateTime.Now:HH:mm:ss.fff} stage {ActualWidth}x{ActualHeight} at {Left},{Top} dpi {dpi.DpiScaleX} state {WindowState}; " +
                $"overlay {(ow == null ? "none" : $"{ow.ActualWidth}x{ow.ActualHeight} at {ow.Left},{ow.Top}")}; " +
                $"screen {SystemParameters.PrimaryScreenWidth}x{SystemParameters.PrimaryScreenHeight}\n");
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    public void HideStage()
    {
        _overlayWindow?.Hide();
        Hide();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space: PauseRequested?.Invoke(); break;
            case Key.N: NextRequested?.Invoke(); break;
            case Key.Escape: BackRequested?.Invoke(); break;
            case Key.OemPlus: case Key.Add: KeyRequested?.Invoke(+1); break;
            case Key.OemMinus: case Key.Subtract: KeyRequested?.Invoke(-1); break;
            case Key.Up: Volume.Value = Math.Min(100, Volume.Value + 5); break;
            case Key.Down: Volume.Value = Math.Max(0, Volume.Value - 5); break;
            default: return;
        }
        e.Handled = true; Poke();
    }

    /// <summary>Shows the control bar and the cursor; hides them again after 3 s without mouse movement.</summary>
    public void Poke()
    {
        if (TopBar.Visibility != Visibility.Visible && ResultBars.Visibility != Visibility.Visible) { Cursor = null; Overlay.Cursor = null; return; }
        Controls.Visibility = TopBar.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        Cursor = null; Overlay.Cursor = null;
        _hide.Stop(); _hide.Start();
    }

    void Overlay_MouseMove(object sender, MouseEventArgs e) => Poke();
    void Pause_Click(object sender, RoutedEventArgs e) => PauseRequested?.Invoke();
    void Next_Click(object sender, RoutedEventArgs e) => NextRequested?.Invoke();
    void NextNow_Click(object sender, RoutedEventArgs e) => NextRequested?.Invoke();
    void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke();
    void KeyDown_Click(object sender, RoutedEventArgs e) => KeyRequested?.Invoke(-1);
    void KeyUp_Click(object sender, RoutedEventArgs e) => KeyRequested?.Invoke(+1);
    void Seek_PreviewMouseUp(object sender, MouseButtonEventArgs e) => SeekRequested?.Invoke(Seek.Value);
    void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (!_settingVolume) VolumeRequested?.Invoke(e.NewValue); }

    public void SetVolume(double v) { _settingVolume = true; Volume.Value = v; _settingVolume = false; }
    public void SetKey(int k) => KeyText.Text = k > 0 ? "+" + k : k.ToString();
    public void SetPaused(bool p) => PauseButton.Content = p ? "▶ Continuar" : "⏸ Pausar";
    public void SetMicLevel(double db) => MicLevel.Value = Math.Clamp(db, 0, 60);

    public void SetProgress(double now, double length)
    {
        if (length <= 0) return;
        if (!Seek.IsMouseCaptureWithin) Seek.Value = Math.Clamp(now / length, 0, 1);
        TimeText.Text = $"{Fmt(now)} / {Fmt(length)}";
    }
    static string Fmt(double s) => $"{(int)s / 60}:{(int)s % 60:00}";

    public void ShowPreparing(string singer, string song)
    {
        TopBar.Visibility = Visibility.Collapsed; ResultBars.Visibility = Visibility.Collapsed; Controls.Visibility = Visibility.Collapsed;
        PanelTitle.Text = singer; PanelSub.Text = song + "\n\nPreparando…"; Panel.Visibility = Visibility.Visible;
    }

    public void ShowSinging(string singer)
    {
        Panel.Visibility = Visibility.Collapsed; TopBar.Visibility = Visibility.Visible;
        SingerText.Text = singer; LiveScore.Text = "0"; NoteText.Text = "—"; SetPaused(false);
        Poke();
    }

    public void ShowVerdict(string text)
    {
        Verdict.Text = text;
        Verdict.Foreground = text switch
        {
            "Perfeito!" => System.Windows.Media.Brushes.Gold, "Ótimo!" => System.Windows.Media.Brushes.LightGreen,
            "Bom" => System.Windows.Media.Brushes.White, _ => System.Windows.Media.Brushes.Salmon
        };
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(150))));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1400))));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2000))));
        Verdict.BeginAnimation(OpacityProperty, anim);
    }

    public void ShowResult(string singer, string song, ScoreResult r, string next, bool hasNext)
    {
        TopBar.Visibility = Visibility.Collapsed; Controls.Visibility = Visibility.Collapsed;
        PanelTitle.Text = singer; PanelSub.Text = song;
        ResultScore.Text = r.Total.ToString(); BarA.Value = r.Afinacao; BarR.Value = r.Ritmo; BarP.Value = r.Presenca;
        ResultNext.Text = next; NextNowButton.Visibility = hasNext ? Visibility.Visible : Visibility.Collapsed;
        ResultBars.Visibility = Visibility.Visible; Panel.Visibility = Visibility.Visible;
        Cursor = null; Overlay.Cursor = null;
    }

    public void UpdateLive(int score, string note) { LiveScore.Text = score.ToString(); NoteText.Text = note; }
}
