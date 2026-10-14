using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace HamRxBridge.Sdr;

/// <summary>12 kHz mono PCM in, resampled to 48 kHz for the sound card.</summary>
public sealed class AudioOut : IDisposable
{
    readonly BufferedWaveProvider _buffer;
    readonly VolumeSampleProvider _volume;
    readonly WaveOutEvent _out;
    bool _muted, _txMuted;
    float _level = 0.7f;

    public AudioOut()
    {
        _buffer = new BufferedWaveProvider(new WaveFormat(KiwiClient.AudioRate, 16, 1))
        {
            BufferDuration = TimeSpan.FromSeconds(3),
            DiscardOnBufferOverflow = true
        };
        var resampled = new WdlResamplingSampleProvider(_buffer.ToSampleProvider(), 48000);
        _volume = new VolumeSampleProvider(resampled);
        _out = new WaveOutEvent { DesiredLatency = 150 };
        _out.Init(_volume);
        _out.Play();
        Apply();
    }

    public float Volume { get => _level; set { _level = Math.Clamp(value, 0f, 1.5f); Apply(); } }
    public bool Muted { get => _muted; set { _muted = value; Apply(); } }
    public bool TxMuted { get => _txMuted; set { _txMuted = value; Apply(); } }

    void Apply() => _volume.Volume = (_muted || _txMuted) ? 0f : _level;

    public void Write(short[] pcm)
    {
        // Keep latency bounded: if we drift more than ~0.8 s behind, drop the backlog.
        if (_buffer.BufferedDuration.TotalSeconds > 0.8) _buffer.ClearBuffer();
        var bytes = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        _buffer.AddSamples(bytes, 0, bytes.Length);
    }

    public void Dispose()
    {
        try { _out.Stop(); } catch { }
        _out.Dispose();
    }
}
