using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace HamRxBridge.Sdr;

/// <summary>
/// Receiver audio to the sound card through the clock-tracking <see cref="AdaptiveResampler"/>:
/// steady latency (about a quarter second of network cushion), no clicks from buffer dumps.
/// </summary>
public sealed class AudioOut : IDisposable
{
    readonly object _gate = new();
    AdaptiveResampler? _rs;
    VolumeSampleProvider? _volume;
    WaveOutEvent? _out;
    int _rate;
    bool _muted, _txMuted;
    float _level = 0.7f;

    public float Volume { get => _level; set { _level = Math.Clamp(value, 0f, 1.5f); Apply(); } }
    public bool Muted { get => _muted; set { _muted = value; Apply(); } }
    public bool TxMuted { get => _txMuted; set { _txMuted = value; Apply(); } }

    /// <summary>Receiver clock versus sound card, as tracked by the resampler.</summary>
    public double ClockOffsetPpm => _rs?.CorrectionPpm ?? 0;
    public int Underruns => _rs?.Underruns ?? 0;

    void Apply()
    {
        lock (_gate) if (_volume != null) _volume.Volume = (_muted || _txMuted) ? 0f : _level;
    }

    void Build(int rate)
    {
        _rate = rate;
        if (_rs != null) { _rs.Reset(rate); return; }       // keep the sound device open
        _rs = new AdaptiveResampler(rate);
        _volume = new VolumeSampleProvider(_rs);
        _out = new WaveOutEvent { DesiredLatency = 120, NumberOfBuffers = 3 };
        _out.Init(_volume);
        _out.Play();
        _volume.Volume = (_muted || _txMuted) ? 0f : _level;
    }

    public void Write(short[] pcm, double sampleRate)
    {
        lock (_gate)
        {
            int rate = (int)Math.Round(sampleRate);
            if (rate < 4000 || rate > 48000) rate = KiwiClient.AudioRate;
            if (_rs == null || Math.Abs(rate - _rate) > 50)
            {
                try { Build(rate); }
                catch (Exception ex) { Log.Write("audio: " + ex.Message); return; }
            }
            _rs!.Write(pcm);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _out?.Stop(); } catch { }
            _out?.Dispose();
            _out = null; _volume = null; _rs = null;
        }
    }
}
