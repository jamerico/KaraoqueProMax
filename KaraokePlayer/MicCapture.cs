using KaraokeCore;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace KaraokePlayer;

/// <summary>
/// Captures any Windows input device (WASAPI shared mode), converts it to 16 kHz mono and feeds a <see cref="VoiceTracker"/>.
/// Each frame is stamped with how many seconds ago its audio ended, so the caller can map it onto the song clock.
/// </summary>
public sealed class MicCapture : IDisposable
{
    WasapiCapture? _cap;
    readonly VoiceTracker _tracker = new();
    long _pushed;                         // 16 kHz samples pushed so far
    long _chunkEnd;                       // _pushed after the chunk being processed
    // resampler state: FIR low-pass at the input rate, then linear interpolation to 16 kHz
    float[] _taps = []; float[] _hist = []; int _histPos; double _step, _frac; float _last;
    readonly List<float> _out = new(4096);
    static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>(frame, seconds between the frame's end and "now" at delivery).</summary>
    public event Action<VoiceFrame, double>? Frame;
    /// <summary>Raw 16 kHz mono samples, as delivered (for recording a session).</summary>
    public event Action<float[]>? Samples;

    public MicCapture() => _tracker.Frame += f => Frame?.Invoke(f, (_chunkEnd - (f.Index * VoiceTracker.Hop + 1028)) / (double)VoiceTracker.Rate);

    public static List<(string Id, string Name)> Devices()
    {
        using var en = new MMDeviceEnumerator();
        return en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Select(d => (d.ID, d.FriendlyName)).ToList();
    }

    public void Start(string? deviceId)
    {
        Stop();
        using var en = new MMDeviceEnumerator();
        var dev = deviceId != null ? en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).FirstOrDefault(d => d.ID == deviceId) : null;
        dev ??= en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        _cap = new WasapiCapture(dev, true, 20);
        var wf = _cap.WaveFormat;
        _step = wf.SampleRate / (double)VoiceTracker.Rate; _frac = 0;
        double fc = 7000.0 / wf.SampleRate; int nt = 63; _taps = new float[nt];
        for (int i = 0; i < nt; i++)
        {
            double k = i - (nt - 1) / 2.0, sinc = k == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * k) / (Math.PI * k);
            _taps[i] = (float)(sinc * (0.54 - 0.46 * Math.Cos(2 * Math.PI * i / (nt - 1))));
        }
        _hist = new float[nt]; _histPos = 0;
        _cap.DataAvailable += (_, e) => OnData(e.Buffer, e.BytesRecorded, wf);
        _cap.StartRecording();
    }

    void OnData(byte[] buf, int bytes, WaveFormat wf)
    {
        int ch = wf.Channels, bps = wf.BitsPerSample / 8, frames = bytes / (bps * ch);
        bool isFloat = wf.Encoding == WaveFormatEncoding.IeeeFloat || (wf is WaveFormatExtensible x && x.SubFormat == IeeeFloatSubtype);
        _out.Clear();
        for (int i = 0; i < frames; i++)
        {
            float s = 0;
            for (int c = 0; c < ch; c++)
            {
                int o = (i * ch + c) * bps;
                s += isFloat ? BitConverter.ToSingle(buf, o) : bps == 2 ? BitConverter.ToInt16(buf, o) / 32768f : bps == 3 ? ((buf[o] << 8 | buf[o + 1] << 16 | buf[o + 2] << 24) >> 8) / 8388608f : BitConverter.ToInt32(buf, o) / 2147483648f;
            }
            s /= ch;
            _hist[_histPos] = s; _histPos = (_histPos + 1) % _hist.Length;
            float y = 0; for (int k = 0; k < _taps.Length; k++) y += _taps[k] * _hist[(_histPos + k) % _hist.Length];
            // emit output samples that fall between the previous and this filtered input sample
            while (_frac < 1) { _out.Add(_last + (y - _last) * (float)_frac); _frac += _step; }
            _frac -= 1; _last = y;
        }
        _chunkEnd = _pushed + _out.Count;
        Samples?.Invoke(_out.ToArray());
        _tracker.Push(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_out));
        _pushed = _chunkEnd;
    }

    public void Stop() { try { _cap?.StopRecording(); } catch { } _cap?.Dispose(); _cap = null; }
    public void Dispose() => Stop();
}
