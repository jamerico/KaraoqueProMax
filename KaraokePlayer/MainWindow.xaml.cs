using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using KaraokeCore;
using LibVLCSharp.Shared;

namespace KaraokePlayer;

public sealed record QueueItem(Song Song, string Singer)
{
    public string Line => $"{Singer} — {Song.Display}";
}

/// <summary>
/// Search screen (search, queue, microphone, scoreboard). While a song plays, this window hides and the
/// full-screen <see cref="StageWindow"/> takes over; it comes back when the queue runs out or the user leaves.
/// </summary>
public partial class MainWindow : Window
{
    readonly Settings _set = Settings.Load();
    Catalog _cat = new();
    LibVLC _vlc = null!;
    MediaPlayer _mp = null!;
    readonly Task _vlcReady;             // VLC loads in the background (its first load on a new PC can take a minute)
    StageWindow? _stage;                 // created fresh for each play session (reusing a hidden one leaves VLC's overlay misplaced)
    bool _closingStage;
    readonly MicCapture _mic = new();
    readonly ObservableCollection<QueueItem> _queue = [];
    readonly ObservableCollection<ScoreEntry> _board = [];
    readonly List<ScoreEntry> _history = History.Load();
    readonly Dictionary<string, Task<SongAnalysis>> _analyses = [];

    // current song
    QueueItem? _cur; SongAnalysis? _ana; ScoreEngine? _engine; int _key; bool _paused;
    readonly object _engineLock = new();
    readonly SongClock _clock = new();
    readonly ConcurrentQueue<FrameJudgement> _incoming = new();
    readonly List<FrameJudgement> _recent = [];
    volatile float _micDb, _micMidi = float.NaN;
    readonly DispatcherTimer _ui = new() { Interval = TimeSpan.FromMilliseconds(33) };
    readonly DispatcherTimer _search = new() { Interval = TimeSpan.FromMilliseconds(200) };
    DispatcherTimer? _afterResult;
    bool _playMode;
    int _tick;

    static void Log(string s)
    {
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "karaoke.log"), $"{DateTime.Now:HH:mm:ss.fff} {s}\n"); } catch { }
    }

    public MainWindow()
    {
        Log("start");
        InitializeComponent();
        QueueList.ItemsSource = _queue; Board.ItemsSource = _board;

        _vlcReady = Task.Run(() =>
        {
            Core.Initialize();
            _vlc = new LibVLC("--no-video-title-show", "--no-snapshot-preview");
            _mp = new MediaPlayer(_vlc) { Volume = (int)_set.MusicVolume };
            _mp.TimeChanged += (_, e) => { lock (_clock) _clock.Sync(e.Time); };
            _mp.Playing += (_, _) => { lock (_clock) _clock.Run(true); };
            _mp.Paused += (_, _) => { lock (_clock) _clock.Run(false); };
            _mp.EndReached += (_, _) => Dispatcher.BeginInvoke(() => EndSong(false));
            _mp.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() => EndSong(false));
            Log("libvlc ok");
        });

        _mic.Frame += OnMicFrame;
        _mic.Samples += OnMicSamples;
        var devs = MicCapture.Devices().Select(d => new MicItem(d.Id, d.Name)).ToList();
        MicBox.ItemsSource = devs;
        MicBox.SelectedItem = devs.FirstOrDefault(d => d.Id == _set.MicId) ?? devs.FirstOrDefault();

        _search.Tick += (_, _) => { _search.Stop(); RunSearch(); };
        _ui.Tick += (_, _) => UiTick();
        _ui.Start();
        Closed += (_, _) => { _set.Save(); _mic.Dispose(); _mp?.Stop(); CloseStage(); Application.Current.Shutdown(); };

        foreach (var e in _history.Where(h => h.When.Date == DateTime.Today).OrderByDescending(h => h.Score)) _board.Add(e);

        LoadCatalog(firstRun: true);
        Log("ready");
    }

    /// <summary>Loads the song folders. When none of the configured folders exists (first run, another PC, a different
    /// drive letter), looks for song folders with a catalog file at the top level of every drive and in Videos\Karaoke.</summary>
    void LoadCatalog(bool firstRun)
    {
        ResultCount.Text = "Carregando catálogo…";
        Task.Run(() =>
        {
            if (!_set.Folders.Any(Directory.Exists))
            {
                var found = new List<string>();
                var candidates = new List<string> { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Karaoke") };
                foreach (var d in DriveInfo.GetDrives())
                {
                    try { if (d.IsReady) candidates.AddRange(Directory.GetDirectories(d.RootDirectory.FullName)); } catch { }
                }
                foreach (var f in candidates)
                {
                    try { if (File.Exists(Path.Combine(f, Catalog.CatalogFile)) || f.EndsWith(@"\Karaoke") && Directory.Exists(f)) found.Add(f); } catch { }
                }
                if (found.Count > 0) { _set.Folders = found; _set.Save(); }
            }
            return Catalog.Load(_set.Folders);
        }).ContinueWith(t =>
        {
            _cat = t.Result;
            ResultCount.Text = _cat.Songs.Count > 0
                ? $"{_cat.Songs.Count:N0} músicas em {_set.Folders.Count} pasta(s). Digite parte do nome."
                : "Nenhuma música encontrada. Clique em “📁 Pastas das músicas” e adicione as pastas com os vídeos.";
            SearchBox.Focus();
            if (!firstRun) return;
            var args = Environment.GetCommandLineArgs();
            int p = Array.IndexOf(args, "--play");
            if (p > 0 && p + 1 < args.Length) { SearchBox.Text = args[p + 1]; RunSearch(); Enqueue(true); }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void Folders_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new FoldersWindow(_set.Folders) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _set.Folders = dlg.Folders; _set.Save();
        LoadCatalog(firstRun: false);
    }

    sealed record MicItem(string Id, string Name);

    // ---------- screens ----------

    void EnterPlayMode()
    {
        if (_playMode) return;
        _playMode = true;
        var s = new StageWindow();
        s.Loaded += (_, _) => { if (_mp != null) s.Video.MediaPlayer = _mp; };
        s.SetVolume(_set.MusicVolume);
        s.PauseRequested += TogglePause;
        s.NextRequested += () => { if (_cur != null) EndSong(true); else StartNext(); };
        s.BackRequested += BackToList;
        s.KeyRequested += ChangeKey;
        s.VolumeRequested += v => { if (_mp != null) _mp.Volume = (int)v; _set.MusicVolume = v; };
        s.SeekRequested += f => { if (_cur != null && _mp != null && _mp.Length > 0) _mp.Time = (long)(f * _mp.Length); };
        s.Closing += (_, _) => { if (!_closingStage) Dispatcher.BeginInvoke(BackToList); };   // Alt+F4 on the video
        _stage = s;
        s.ShowFullScreen(this);
        Hide();
    }

    void ExitPlayMode()
    {
        if (!_playMode) return;
        _playMode = false;
        Show(); Activate();
        CloseStage();
        SearchBox.Focus(); SearchBox.SelectAll();
    }

    void CloseStage()
    {
        if (_stage == null) return;
        _closingStage = true;
        try { _stage.Video.MediaPlayer = null; _stage.Close(); } catch { }
        _closingStage = false;
        _stage = null;
    }

    /// <summary>Stop everything that is playing and return to the search screen (keeps the queue).</summary>
    void BackToList()
    {
        _afterResult?.Stop(); _afterResult = null;
        if (_cur != null) { _cur = null; StopRecording(); _mp?.Stop(); lock (_engineLock) _engine = null; lock (_clock) _clock.Reset(); }
        ExitPlayMode();
    }

    // ---------- search & queue ----------

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e) { _search.Stop(); _search.Start(); }

    void RunSearch()
    {
        var list = _cat.Search(SearchBox.Text).ToList();
        Results.ItemsSource = list;
        if (list.Count > 0) Results.SelectedIndex = 0;
        ResultCount.Text = SearchBox.Text.Trim().Length == 0 ? $"{_cat.Songs.Count:N0} músicas." : $"{list.Count} encontrada(s). Enter adiciona à fila, Ctrl+Enter canta agora.";
    }

    void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && Results.Items.Count > 0) { Results.Focus(); (Results.ItemContainerGenerator.ContainerFromIndex(Results.SelectedIndex) as ListViewItem)?.Focus(); e.Handled = true; }
        else if (e.Key == Key.Enter) { _search.Stop(); RunSearch(); Enqueue(Keyboard.Modifiers.HasFlag(ModifierKeys.Control)); e.Handled = true; }
    }

    void Results_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Enqueue(Keyboard.Modifiers.HasFlag(ModifierKeys.Control)); e.Handled = true; }
    }

    void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Enqueue(true);
    void AddQueue_Click(object sender, RoutedEventArgs e) => Enqueue(false);
    void SingNow_Click(object sender, RoutedEventArgs e) => Enqueue(true);

    /// <summary>Adds the selected song; "sing now" puts it first and starts it. The queue only starts on its own via "sing now"
    /// or when the queue was empty and nothing is playing.</summary>
    void Enqueue(bool now)
    {
        if (Results.SelectedItem is not Song s) return;
        var item = new QueueItem(s, string.IsNullOrWhiteSpace(SingerBox.Text) ? "Cantor" : SingerBox.Text.Trim());
        if (now) { _queue.Insert(0, item); StartNext(); }
        else { _queue.Add(item); _ = Prepare(_queue[0].Song.Path); }
        SearchBox.SelectAll(); SearchBox.Focus();
    }

    void RemoveQueue_Click(object sender, RoutedEventArgs e) { if (QueueList.SelectedItem is QueueItem q) _queue.Remove(q); }
    void StartQueue_Click(object sender, RoutedEventArgs e) => StartNext();

    Task<SongAnalysis> Prepare(string path)
    {
        if (!_analyses.TryGetValue(path, out var t) || t.IsFaulted) _analyses[path] = t = Analyzer.AnalyzeAsync(path);
        return t;
    }

    // ---------- playback ----------

    void StartNext()
    {
        _afterResult?.Stop(); _afterResult = null;
        if (_cur != null) { _cur = null; StopRecording(); _mp?.Stop(); lock (_engineLock) _engine = null; }
        if (_queue.Count == 0) { ExitPlayMode(); return; }
        var item = _queue[0]; _queue.RemoveAt(0);
        _ = StartSong(item);
    }

    async Task StartSong(QueueItem item)
    {
        _cur = item; _key = 0; _paused = false;
        EnterPlayMode();
        _stage!.SetKey(0);
        _stage.ShowPreparing(item.Singer, item.Song.Display);
        if (!_vlcReady.IsCompleted)
        {
            _stage.ShowPreparing(item.Singer, item.Song.Display + "\n(iniciando o player de vídeo — só demora na primeira vez)");
            await _vlcReady;
            if (_cur != item) return;
        }
        if (_stage != null && _stage.Video.MediaPlayer == null) _stage.Video.MediaPlayer = _mp;
        SongAnalysis ana;
        try { ana = await Prepare(item.Song.Path); }
        catch (Exception ex) { Log("analysis failed: " + ex.Message); if (_cur == item) { _cur = null; StartNext(); } return; }
        if (_cur != item) return;                        // skipped while preparing
        _ana = ana;
        var eng = new ScoreEngine(ana) { ExtraLatency = _set.ExtraLatency, KeyShift = 0 };
        eng.Judged += j => _incoming.Enqueue(j);
        eng.LineDone += r => Dispatcher.BeginInvoke(() => _stage?.ShowVerdict(r.Verdict));
        lock (_engineLock) _engine = eng;
        _recent.Clear(); while (_incoming.TryDequeue(out _)) { }
        Play(0);
        StartRecording(item.Song);
        _stage?.ShowSinging(item.Singer);
        if (_queue.Count > 0) _ = Prepare(_queue[0].Song.Path);
    }

    void Play(long startMs)
    {
        if (_cur == null) return;
        using var media = new Media(_vlc, new Uri(_cur.Song.Path));
        media.AddOption(":audio-filter=scaletempo_pitch");
        media.AddOption(":pitch-shift=" + _key.ToString(CultureInfo.InvariantCulture));
        if (startMs > 0) media.AddOption(":start-time=" + (startMs / 1000.0).ToString(CultureInfo.InvariantCulture));
        lock (_clock) { _clock.Reset(); _clock.Sync(startMs); }
        _mp.Play(media);
    }

    /// <summary>Song finished or skipped: record the score, show the result full screen, then the next song or the list.</summary>
    void EndSong(bool skipped)
    {
        if (_cur == null) return;
        var item = _cur; _cur = null;
        StopRecording();
        _mp?.Stop(); lock (_clock) _clock.Reset();
        ScoreResult? r = null;
        lock (_engineLock) { if (_engine != null) { _engine.Finish(); r = _engine.Current; } _engine = null; }
        bool sang = r != null && r.Presenca > 0.05f;
        if (sang)
        {
            var e = new ScoreEntry(DateTime.Now, item.Singer, item.Song.Display, r!.Total, r.Afinacao, r.Ritmo, r.Presenca);
            _history.Add(e); History.Save(_history);
            int i = 0; while (i < _board.Count && _board[i].Score >= e.Score) i++;
            _board.Insert(i, e);
            LastTitle.Text = $"{item.Singer} — {item.Song.Display}"; LastScore.Text = r.Total.ToString();
            LastDetail.Text = $"afinação {r.Afinacao:P0} · ritmo {r.Ritmo:P0} · presença {r.Presenca:P0}";
        }
        if (!sang && skipped) { StartNext(); return; }   // skipped without singing: no result screen

        bool hasNext = _queue.Count > 0;
        string next = hasNext ? $"Próxima: {_queue[0].Singer} — {_queue[0].Song.Display} (em 12 s)" : "Voltando para a lista em 10 s";
        _stage?.ShowResult(item.Singer, item.Song.Display, r ?? new ScoreResult(0, 0, 0, 0), next, hasNext);
        _afterResult = new DispatcherTimer { Interval = TimeSpan.FromSeconds(hasNext ? 12 : 10) };
        _afterResult.Tick += (_, _) => { _afterResult?.Stop(); _afterResult = null; if (_queue.Count > 0) StartNext(); else ExitPlayMode(); };
        _afterResult.Start();
    }

    void TogglePause()
    {
        if (_cur == null || _mp == null) return;
        _mp.Pause(); _paused = !_paused; _stage?.SetPaused(_paused);
    }

    void ChangeKey(int d)
    {
        if (_cur == null || _mp == null) return;
        _key = Math.Clamp(_key + d, -6, 6); _stage?.SetKey(_key);
        lock (_engineLock) if (_engine != null) _engine.KeyShift = _key;
        long t; lock (_clock) t = (long)(_clock.Now * 1000);
        Play(t); _paused = false; _stage?.SetPaused(false);
    }

    // ---------- microphone ----------

    void MicBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MicBox.SelectedItem is not MicItem m) return;
        _set.MicId = m.Id;
        try { _mic.Start(m.Id); } catch (Exception ex) { MicNote.Text = "erro"; MessageBox.Show(this, "Não foi possível abrir o microfone:\n" + ex.Message, "Karaokê"); }
    }

    // Optional session recording (--record <folder>): the mic at 16 kHz from the moment the song clock runs,
    // plus a sidecar with the song time of the first sample, so KaraokeSim can replay it offline.
    NAudio.Wave.WaveFileWriter? _rec;
    readonly object _recLock = new();
    double _recStart = double.NaN; string _recBase = "";

    void StartRecording(Song song)
    {
        var args = Environment.GetCommandLineArgs(); int i = Array.IndexOf(args, "--record");
        if (i < 0 || i + 1 >= args.Length) return;
        Directory.CreateDirectory(args[i + 1]);
        var baseName = Path.Combine(args[i + 1], $"{DateTime.Now:yyyyMMdd_HHmmss}_{song.Collection}_{song.Code}");
        lock (_recLock) { _rec = new NAudio.Wave.WaveFileWriter(baseName + ".wav", NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(VoiceTracker.Rate, 1)); _recStart = double.NaN; _recBase = baseName; }
    }

    void StopRecording()
    {
        lock (_recLock)
        {
            if (_rec == null) return;
            _rec.Dispose(); _rec = null;
            File.WriteAllText(_recBase + ".txt", _recStart.ToString(CultureInfo.InvariantCulture));
        }
    }

    void OnMicSamples(float[] s)
    {
        lock (_recLock)
        {
            if (_rec == null) return;
            double now; bool running; lock (_clock) { now = _clock.Now; running = _clock.Running; }
            if (!running) return;
            if (double.IsNaN(_recStart)) _recStart = now - s.Length / (double)VoiceTracker.Rate;
            _rec.WriteSamples(s, 0, s.Length);
        }
    }

    /// <summary>Capture thread: meter + hand the frame to the scorer on the song clock.</summary>
    void OnMicFrame(VoiceFrame f, double ago)
    {
        _micDb = 20 * MathF.Log10(Math.Max(f.Rms, 1e-6f)) + 60;
        _micMidi = f.HasPitch && f.Clarity >= 0.7f && f.Rms > 0.003f ? f.Midi : float.NaN;
        double now; bool running;
        lock (_clock) { now = _clock.Now; running = _clock.Running; }
        if (!running) return;
        lock (_engineLock) _engine?.Add(now - ago - 0.032, f);
    }

    // ---------- UI refresh ----------

    void UiTick()
    {
        MicLevel.Value = Math.Clamp(_micDb, 0, 60);
        MicNote.Text = PitchStrip.NoteName(_micMidi);
        if (!_playMode || _stage == null) return;
        _stage.SetMicLevel(_micDb);
        if (_cur == null || _ana == null) return;
        double now; lock (_clock) now = _clock.Now;
        while (_incoming.TryDequeue(out var j)) _recent.Add(j);
        int cut = _recent.FindIndex(j => j.Time > now - 5); if (cut > 0) _recent.RemoveRange(0, cut);
        double delay; lock (_engineLock) delay = _engine?.Bleed.Delay ?? 0.3;
        _stage.StripRenderer.Render(now - delay - _set.ExtraLatency, _ana, _key, _recent);
        if (++_tick % 8 == 0)
        {
            ScoreResult s; lock (_engineLock) { if (_engine == null) return; s = _engine.Current; }
            _stage.UpdateLive(s.Total, PitchStrip.NoteName(_micMidi));
            _stage.SetProgress(now, (_mp?.Length ?? 0) / 1000.0);
        }
    }
}
