using NAudio.Wave;

namespace HamRxBridge.Sdr;

/// <summary>
/// Network audio to sound card, the way professional SDR clients do it.
///
/// The receiver's sample clock and the PC sound card never run at exactly the same rate
/// (tens to hundreds of ppm apart). Instead of letting the buffer grow and then dumping it
/// (audible clicks, wandering latency), this resampler steers its own conversion ratio with a
/// slow PI loop so the buffer stays at a fixed target fill. The correction is a fraction of a
/// percent: far below what the ear can hear as pitch.
///
/// Interpolation is 4 point cubic (Catmull-Rom), followed by a 4th order Butterworth low pass
/// at 48 kHz to suppress the interpolation images above the receiver's audio band.
/// Mono float out at 48 kHz. Thread safe: Write from the network thread, Read from the audio thread.
/// </summary>
public sealed class AdaptiveResampler : ISampleProvider
{
    public const int OutRate = 48000;

    readonly object _gate = new();
    readonly float[] _ring;
    int _w;              // write index
    double _r;           // fractional read position
    int _count;          // samples available
    double _inRate;
    double _targetFill;  // seconds
    double _integral;
    double _fillAvg = -1;
    bool _priming = true;

    // 4th order Butterworth low pass as two biquads, state per section
    readonly double[] _b = new double[6], _a = new double[4];
    double _z1a, _z2a, _z1b, _z2b;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(OutRate, 1);

    /// <summary>Current ratio correction in ppm (positive means playing faster).</summary>
    public double CorrectionPpm { get; private set; }
    public int Underruns { get; private set; }
    public double FillSeconds { get { lock (_gate) return _count / _inRate; } }

    public AdaptiveResampler(double inputRate, double targetFillSeconds = 0.25)
    {
        _inRate = inputRate;
        _targetFill = targetFillSeconds;
        _ring = new float[(int)(inputRate * 4)];
        DesignLowPass(Math.Min(9000, inputRate * 0.46));
    }

    void DesignLowPass(double fc)
    {
        // two cascaded biquads with Butterworth Q values for a 4th order response
        double[] q = { 0.54119610, 1.3065630 };
        for (int s = 0; s < 2; s++)
        {
            double w0 = 2 * Math.PI * fc / OutRate, cos = Math.Cos(w0), alpha = Math.Sin(w0) / (2 * q[s]);
            double a0 = 1 + alpha;
            _b[s * 3 + 0] = (1 - cos) / 2 / a0;
            _b[s * 3 + 1] = (1 - cos) / a0;
            _b[s * 3 + 2] = (1 - cos) / 2 / a0;
            _a[s * 2 + 0] = -2 * cos / a0;
            _a[s * 2 + 1] = (1 - alpha) / a0;
        }
    }

    public void Write(ReadOnlySpan<short> pcm)
    {
        lock (_gate)
        {
            foreach (var v in pcm)
            {
                _ring[_w] = v / 32768f;
                _w = (_w + 1) % _ring.Length;
            }
            _count += pcm.Length;
            if (_count > _ring.Length - 8)
            {
                // far too much backlog (e.g. after a network stall): drop the oldest, keep the target
                int keep = (int)(_targetFill * _inRate);
                int drop = _count - keep;
                _r = (_r + drop) % _ring.Length;
                _count = keep;
            }
        }
    }

    float At(int i) => _ring[((i % _ring.Length) + _ring.Length) % _ring.Length];

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            double fill = _count / _inRate;
            if (_priming)
            {
                if (fill < _targetFill) { Array.Clear(buffer, offset, count); return count; }
                _priming = false;
            }

            // The buffer integrates the rate mismatch: d(fill)/dt = offset - corr. A PI controller
            // around an integrator is second order: s^2 + Kp s + Ki. Critically damped with
            // wn = 0.05 rad/s (settles in about 20 s), so jitter never becomes audible wobble.
            // The fill is smoothed over ~3 s first to average out packet steps and network jitter.
            double dt = count / (double)OutRate;
            _fillAvg = _fillAvg < 0 ? fill : _fillAvg + (fill - _fillAvg) * Math.Min(1, dt / 3.0);
            double err = _fillAvg - _targetFill;                            // seconds, +: too much buffered
            const double Wn = 0.05, Kp = 2 * Wn, Ki = Wn * Wn;
            _integral = Math.Clamp(_integral + Ki * err * dt, -0.004, 0.004);
            double corr = Math.Clamp(Kp * err + _integral, -0.005, 0.005);  // at most 0.5 percent
            CorrectionPpm = corr * 1e6;
            double step = _inRate / OutRate * (1 + corr);

            for (int n = 0; n < count; n++)
            {
                if (_count < 4)
                {
                    // underrun: fade to silence and prime again
                    Underruns++;
                    _priming = true;
                    for (int k = n; k < count; k++) buffer[offset + k] = 0;
                    return count;
                }
                int i = (int)Math.Floor(_r);
                double t = _r - i;
                double y0 = At(i - 1), y1 = At(i), y2 = At(i + 1), y3 = At(i + 2);
                // Catmull-Rom cubic
                double v = y1 + 0.5 * t * (y2 - y0 + t * (2 * y0 - 5 * y1 + 4 * y2 - y3 + t * (3 * (y1 - y2) + y3 - y0)));

                // 4th order low pass (direct form II transposed)
                double s1 = _b[0] * v + _z1a; _z1a = _b[1] * v - _a[0] * s1 + _z2a; _z2a = _b[2] * v - _a[1] * s1;
                double s2 = _b[3] * s1 + _z1b; _z1b = _b[4] * s1 - _a[2] * s2 + _z2b; _z2b = _b[5] * s1 - _a[3] * s2;
                buffer[offset + n] = (float)s2;

                _r += step;
                int whole = (int)Math.Floor(_r) - i;
                if (whole > 0)
                {
                    _count -= whole;
                    if (_r >= _ring.Length) _r -= _ring.Length;
                }
            }
            return count;
        }
    }

    /// <summary>Change the input rate (a different receiver), keeping the output running.</summary>
    public void Reset(double inputRate)
    {
        lock (_gate)
        {
            _inRate = inputRate;
            _count = 0; _w = 0; _r = 0; _integral = 0; _fillAvg = -1; _priming = true;
            _z1a = _z2a = _z1b = _z2b = 0;
            DesignLowPass(Math.Min(9000, inputRate * 0.46));
        }
    }
}
