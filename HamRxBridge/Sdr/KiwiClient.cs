using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using HamRxBridge.Rig;

namespace HamRxBridge.Sdr;

/// <summary>
/// Native KiwiSDR client, following the protocol used by the official kiwiclient/kiwirecorder:
///   ws://host:port/&lt;timestamp&gt;/SND and /&lt;timestamp&gt;/W/F
///   "SET auth" first, then the server drives setup: MSG audio_rate -> "SET AR OK",
///   MSG sample_rate -> receiver parameters, MSG wf_setup -> waterfall parameters.
/// Frames: "MSG" text, "SND" flags(1) seq(4 LE) smeter(2 BE) audio, "W/F" pad(1) x_bin(4) flags_zoom(4) seq(4) bins.
/// </summary>
public sealed class KiwiClient : IDisposable
{
    public const int WfBins = 1024;
    public const int AudioRate = 12000;

    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _sndLock = new(1, 1), _wfLock = new(1, 1);
    readonly ImaAdpcm _sndDecoder = new(), _wfDecoder = new();
    ClientWebSocket? _snd, _wf;
    bool _sndReady, _wfReady, _compression;
    string _ident = "HamRxBridge";
    double _versionMaj, _versionMin;
    int _zoomMax = 14;

    // desired state, sent whenever the server is ready
    RigMode _mode = RigMode.USB;
    double _hz = 7_100_000;
    int _lo = 300, _hi = 2700;
    bool _agc = true;
    int _zoom = 7;
    double _viewCenterHz = 7_100_000;

    public double BandwidthHz { get; private set; } = 30_000_000;
    public double FreqOffsetHz { get; private set; }
    public string? Refusal { get; private set; }
    public double SampleRate { get; private set; } = AudioRate;
    public bool Streaming { get; private set; }

    public event Action<short[]>? AudioReceived;
    /// <summary>bins (dBm = value - 255), start Hz, span Hz (all in displayed, offset corrected frequency)</summary>
    public event Action<byte[], double, double>? WaterfallRow;
    public event Action<double>? SignalDbm;
    public event Action<string>? Info;
    public event Action<string>? Disconnected;

    public async Task ConnectAsync(ReceiverEntry rx, string password, string ident, bool compression, CancellationToken ct)
    {
        _compression = compression;
        if (!string.IsNullOrWhiteSpace(ident)) _ident = ident.Trim().Replace(' ', '_');
        string scheme = rx.UseTls ? "wss" : "ws";
        long stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + Environment.ProcessId;
        stamp &= 0xFFFFFFFF;
        string root = $"{scheme}://{rx.Host}:{rx.Port}/{stamp}";

        _snd = new ClientWebSocket();
        _wf = new ClientWebSocket();
        _snd.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        _wf.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(15));
        await _snd.ConnectAsync(new Uri(root + "/SND"), linked.Token);
        await _wf.ConnectAsync(new Uri(root + "/W/F"), linked.Token);

        string pw = (password ?? "").Replace(' ', '_');
        await Send(_snd, _sndLock, $"SET auth t=kiwi p={pw}");
        await Send(_wf, _wfLock, $"SET auth t=kiwi p={pw}");

        _ = Task.Run(() => ReceiveLoop(_snd, true));
        _ = Task.Run(() => ReceiveLoop(_wf, false));
        _ = Task.Run(KeepAlive);
    }

    // ------------------------------------------------------------- commands

    public Task SetTuning(RigMode mode, double hz, int lowCut, int highCut)
    {
        _mode = mode; _hz = hz; _lo = lowCut; _hi = highCut;
        return _sndReady ? SendTuning() : Task.CompletedTask;
    }

    Task SendTuning()
    {
        double baseband = (_hz - FreqOffsetHz) / 1000.0;
        return Send(_snd, _sndLock, string.Create(CultureInfo.InvariantCulture,
            $"SET mod={RigModeInfo.ToWire(_mode)} low_cut={_lo} high_cut={_hi} freq={baseband:F3}"));
    }

    public Task SetAgc(bool on)
    {
        _agc = on;
        return _sndReady ? SendAgc() : Task.CompletedTask;
    }

    Task SendAgc() => Send(_snd, _sndLock,
        $"SET agc={(_agc ? 1 : 0)} hang=0 thresh=-100 slope=6 decay=1000 manGain=50");

    public int ZoomMax => _zoomMax;

    public Task SetView(int zoom, double centerHz)
    {
        _zoom = Math.Clamp(zoom, 0, _zoomMax);
        _viewCenterHz = centerHz;
        return _wfReady ? SendView() : Task.CompletedTask;
    }

    Task SendView()
    {
        double span = BandwidthHz / Math.Pow(2, _zoom);
        double cf = Math.Clamp(_viewCenterHz - FreqOffsetHz, span / 2, BandwidthHz - span / 2);
        double version = _versionMaj + _versionMin / 1000.0;
        if (version > 0 && version < 1.329)
        {
            double start = cf - span / 2;
            long counter = (long)Math.Round(start / BandwidthHz * (1L << _zoomMax) * WfBins);
            return Send(_wf, _wfLock, string.Create(CultureInfo.InvariantCulture, $"SET zoom={_zoom} start={counter}"));
        }
        return Send(_wf, _wfLock, string.Create(CultureInfo.InvariantCulture, $"SET zoom={_zoom} cf={cf / 1000.0:F3}"));
    }

    static async Task Send(ClientWebSocket? ws, SemaphoreSlim gate, string text)
    {
        if (ws == null || ws.State != WebSocketState.Open) return;
        try
        {
            await gate.WaitAsync();
            try { await ws.SendAsync(Encoding.ASCII.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None); }
            finally { gate.Release(); }
        }
        catch (Exception ex) { Log.Write($"kiwi send '{text}' failed: {ex.Message}"); }
    }

    async Task KeepAlive()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(1000, _cts.Token);
                await Send(_snd, _sndLock, "SET keepalive");
                await Send(_wf, _wfLock, "SET keepalive");
            }
        }
        catch (OperationCanceledException) { }
    }

    // ------------------------------------------------------------- server driven setup

    async Task OnSoundSampleRate()
    {
        _sndReady = true;
        await Send(_snd, _sndLock, $"SET ident_user={_ident}");
        await Send(_snd, _sndLock, "SET squelch=0 max=0");
        await Send(_snd, _sndLock, "SET genattn=0");
        await Send(_snd, _sndLock, "SET gen=0 mix=-1");
        await Send(_snd, _sndLock, $"SET compression={(_compression ? 1 : 0)}");
        await SendAgc();
        await SendTuning();
    }

    async Task OnWaterfallSetup()
    {
        _wfReady = true;
        await SendView();
        await Send(_wf, _wfLock, "SET maxdb=-10 mindb=-110");
        await Send(_wf, _wfLock, "SET wf_speed=3");
        await Send(_wf, _wfLock, "SET wf_comp=0");
        await Send(_wf, _wfLock, "SET interp=13");
    }

    // ------------------------------------------------------------- receive

    async Task ReceiveLoop(ClientWebSocket ws, bool isSound)
    {
        var buf = new byte[1 << 16];
        using var ms = new MemoryStream();
        try
        {
            while (!_cts.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                ValueWebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf.AsMemory(), _cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        Disconnected?.Invoke(Refusal ?? "The receiver closed the connection");
                        return;
                    }
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);

                var d = ms.GetBuffer();
                int len = (int)ms.Length;
                if (len < 3) continue;
                string tag = Encoding.ASCII.GetString(d, 0, 3);
                if (tag == "MSG") await HandleMsg(Encoding.UTF8.GetString(d, 3, len - 3), isSound);
                else if (tag == "SND") HandleSnd(d, len);
                else if (tag == "W/F") HandleWf(d, len);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested) Disconnected?.Invoke(Refusal ?? ex.Message);
        }
    }

    async Task HandleMsg(string text, bool isSound)
    {
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = token.IndexOf('=');
            string key = eq < 0 ? token : token[..eq];
            string val = eq < 0 ? "" : Unescape(token[(eq + 1)..]);
            double num = double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : double.NaN;

            switch (key)
            {
                case "audio_rate":
                    if (!double.IsNaN(num))
                        await Send(_snd, _sndLock, $"SET AR OK in={(int)num} out=48000");
                    break;
                case "sample_rate":
                    if (!double.IsNaN(num)) SampleRate = num;
                    if (isSound) await OnSoundSampleRate();
                    break;
                case "wf_setup":
                    if (!isSound) await OnWaterfallSetup();
                    break;
                case "bandwidth":
                    if (num > 1e6) BandwidthHz = num;
                    break;
                case "zoom_max":
                    if (num is >= 1 and <= 20) _zoomMax = (int)num;
                    break;
                case "freq_offset":
                    if (!double.IsNaN(num)) FreqOffsetHz = num * 1000.0;
                    break;
                case "version_maj": if (!double.IsNaN(num)) _versionMaj = num; break;
                case "version_min": if (!double.IsNaN(num)) _versionMin = num; break;
                case "too_busy": Refusal = "All user slots on this receiver are taken. Try another one."; break;
                case "badp":
                    Refusal = val switch
                    {
                        "0" => null,
                        "5" => "This receiver allows only one connection per IP address.",
                        _ => "This receiver needs a password, or all open slots are in use."
                    };
                    break;
                case "down": Refusal = "This receiver is down for maintenance."; break;
                case "redirect": Refusal = "This receiver redirected to another address: " + val; break;
                case "inactivity_timeout": Refusal = "The receiver disconnected you after its time limit."; break;
            }
            if (key is "too_busy" or "badp" or "down" or "redirect" or "version_maj" or "version_min" or "freq_offset")
                Info?.Invoke($"{key}={val}");
        }
    }

    static string Unescape(string s)
    {
        try { return Uri.UnescapeDataString(s); } catch { return s; }
    }

    void HandleSnd(byte[] d, int len)
    {
        if (len < 10) return;
        byte flags = d[3];
        int smeter = (d[8] << 8) | d[9];
        SignalDbm?.Invoke(0.1 * smeter - 127.0);
        if ((flags & 0x08) != 0) return;                      // stereo/IQ not used here

        int count = len - 10;
        if (count <= 0) return;
        short[] pcm;
        if ((flags & 0x10) != 0)
        {
            pcm = _sndDecoder.Decode(d, 10, count);
        }
        else
        {
            bool little = (flags & 0x80) != 0;
            int n = count / 2;
            pcm = new short[n];
            for (int i = 0; i < n; i++)
            {
                int o = 10 + i * 2;
                pcm[i] = little ? (short)(d[o] | (d[o + 1] << 8)) : (short)((d[o] << 8) | d[o + 1]);
            }
        }
        Streaming = true;
        AudioReceived?.Invoke(pcm);
    }

    void HandleWf(byte[] d, int len)
    {
        if (len < 16 + 64) return;
        uint xBin = BitConverter.ToUInt32(d, 4);
        uint zf = BitConverter.ToUInt32(d, 8);
        int zoom = (int)(zf & 0xFFFF);
        int wfFlags = (int)(zf >> 16);
        int dataLen = len - 16;

        byte[] bins = new byte[WfBins];
        if ((wfFlags & 1) != 0)
        {
            _wfDecoder.Reset();
            var s = _wfDecoder.Decode(d, 16, dataLen);
            int usable = Math.Max(0, s.Length - 10);
            for (int i = 0; i < WfBins && i < usable; i++) bins[i] = (byte)Math.Clamp((int)s[i], 0, 255);
        }
        else
        {
            Buffer.BlockCopy(d, 16, bins, 0, Math.Min(WfBins, dataLen));
        }

        if (zoom < 0 || zoom > _zoomMax) zoom = _zoom;
        double span = BandwidthHz / Math.Pow(2, zoom);
        double start = xBin / ((double)WfBins * (1L << _zoomMax)) * BandwidthHz;
        if (start < -span || start > BandwidthHz) start = _viewCenterHz - FreqOffsetHz - span / 2;
        WaterfallRow?.Invoke(bins, start + FreqOffsetHz, span);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _snd?.Abort(); _snd?.Dispose(); } catch { }
        try { _wf?.Abort(); _wf?.Dispose(); } catch { }
    }
}
