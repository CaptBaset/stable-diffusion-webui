using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace HamRxBridge.Sdr;

/// <summary>Mono 16 bit PCM at the receiver's rate in, resampled to 48 kHz for the sound card.</summary>
public sealed class AudioOut : IDisposable
{
    readonly object _gate = new();
    BufferedWaveProvider? _buffer;
    VolumeSampleProvider? _volume;
    WaveOutEvent? _out;
    int _rate;
    bool _muted, _txMuted;
    float _level = 0.7f;

    public float Volume { get => _level; set { _level = Math.Clamp(value, 0f, 1.5f); Apply(); } }
    public bool Muted { get => _muted; set { _muted = value; Apply(); } }
    public bool TxMuted { get => _txMuted; set { _txMuted = value; Apply(); } }

    void Apply()
    {
        lock (_gate) if (_volume != null) _volume.Volume = (_muted || _txMuted) ? 0f : _level;
    }

    void Build(int rate)
    {
        Teardown();
        _rate = rate;
        _buffer = new BufferedWaveProvider(new WaveFormat(rate, 16, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(3),
            DiscardOnBufferOverflow = true
        };
        var resampled = new WdlResamplingSampleProvider(_buffer.ToSampleProvider(), 48000);
        _volume = new VolumeSampleProvider(resampled);
        _out = new WaveOutEvent { DesiredLatency = 160 };
        _out.Init(_volume);
        _out.Play();
        _volume.Volume = (_muted || _txMuted) ? 0f : _level;
    }

    void Teardown()
    {
        try { _out?.Stop(); } catch { }
        _out?.Dispose();
        _out = null; _volume = null; _buffer = null;
    }

    public void Write(short[] pcm, double sampleRate)
    {
        lock (_gate)
        {
            int rate = (int)Math.Round(sampleRate);
            if (rate < 4000 || rate > 48000) rate = KiwiClient.AudioRate;
            if (_buffer == null || Math.Abs(rate - _rate) > 50)
            {
                try { Build(rate); }
                catch (Exception ex) { Log.Write("audio: " + ex.Message); return; }
            }
            // keep latency bounded: drop the backlog if we fall behind by more than ~0.8 s
            if (_buffer!.BufferedDuration.TotalSeconds > 0.8) _buffer.ClearBuffer();
            var bytes = new byte[pcm.Length * 2];
            Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
            _buffer.AddSamples(bytes, 0, bytes.Length);
        }
    }

    public void Dispose()
    {
        lock (_gate) Teardown();
    }
}
