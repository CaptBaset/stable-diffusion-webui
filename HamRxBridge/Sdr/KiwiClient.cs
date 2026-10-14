using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using HamRxBridge.Rig;

namespace HamRxBridge.Sdr;

/// <summary>
/// Native KiwiSDR client: one websocket for audio (SND), one for the waterfall (W/F).
/// Written from the public KiwiSDR client protocol. Anything unexpected is surfaced
/// through <see cref="Log"/> so mismatches are easy to diagnose.
/// </summary>
public sealed class KiwiClient : IDisposable
{
    public const int WfBins = 1024;
    public const int AudioRate = 12000;

    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _sndLock = new(1, 1), _wfLock = new(1, 1);
    ClientWebSocket? _snd, _wf;

    public double BandwidthHz { get; private set; } = 30_000_000;
    public int Zoom { get; private set; }
    public double WfCenterHz { get; private set; } = 15_000_000;
    public string? Refusal { get; private set; }

    public event Action<short[]>? AudioReceived;
    /// <summary>bins as dBm bytes (value minus 255), start frequency Hz, span Hz</summary>
    public event Action<byte[], double, double>? WaterfallRow;
    public event Action<double>? SignalDbm;
    public event Action<string>? Log;
    public event Action<string>? Disconnected;

    public async Task ConnectAsync(ReceiverEntry rx, string password, CancellationToken ct)
    {
        string scheme = rx.UseTls ? "wss" : "ws";
        long stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string baseUrl = $"{scheme}://{rx.Host}:{rx.Port}/kiwi/{stamp}";

        _snd = new ClientWebSocket();
        _wf = new ClientWebSocket();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        linked.CancelAfter(TimeSpan.FromSeconds(15));

        await _snd.ConnectAsync(new Uri(baseUrl + "/SND"), linked.Token);
        await _wf.ConnectAsync(new Uri(baseUrl + "/W/F"), linked.Token);

        string pw = string.IsNullOrEmpty(password) ? "" : password;
        await SendSnd($"SET auth t=kiwi p={pw}");
        await SendSnd($"SET AR OK in={AudioRate} out=48000");
        await SendSnd("SET compression=0");
        await SendSnd("SET agc=1 hang=0 thresh=-100 slope=6 decay=1000 manGain=50");
        await SendSnd("SET ident_user=HamRxBridge");

        await SendWf($"SET auth t=kiwi p={pw}");
        await SendWf("SET send_dB=1");
        await SendWf("SET wf_comp=0");
        await SendWf("SET wf_speed=2");
        await SendWf("SET maxdb=-10 mindb=-110");

        _ = Task.Run(() => ReceiveLoop(_snd, true));
        _ = Task.Run(() => ReceiveLoop(_wf, false));
        _ = Task.Run(KeepAlive);
    }

    // ---- commands -------------------------------------------------------

    public Task SetTuning(RigMode mode, double hz, int lowCut, int highCut)
    {
        string cmd = string.Create(CultureInfo.InvariantCulture,
            $"SET mod={RigModeInfo.ToWire(mode)} low_cut={lowCut} high_cut={highCut} freq={hz / 1000.0:F3}");
        return SendSnd(cmd);
    }

    public Task SetAgc(bool on) => SendSnd(on
        ? "SET agc=1 hang=0 thresh=-100 slope=6 decay=1000 manGain=50"
        : "SET agc=0 hang=0 thresh=-100 slope=6 decay=1000 manGain=50");

    public Task SetView(int zoom, double centerHz)
    {
        Zoom = Math.Clamp(zoom, 0, 14);
        WfCenterHz = centerHz;
        return SendWf(string.Create(CultureInfo.InvariantCulture,
            $"SET zoom={Zoom} cf={centerHz / 1000.0:F3}"));
    }

    Task SendSnd(string s) => Send(_snd, _sndLock, s);
    Task SendWf(string s) => Send(_wf, _wfLock, s);

    async Task Send(ClientWebSocket? ws, SemaphoreSlim gate, string text)
    {
        if (ws == null || ws.State != WebSocketState.Open) return;
        await gate.WaitAsync(_cts.Token);
        try
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token);
        }
        catch (Exception ex) { Log?.Invoke("send failed: " + ex.Message); }
        finally { gate.Release(); }
    }

    async Task KeepAlive()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(4000, _cts.Token);
                await SendSnd("SET keepalive");
                await SendWf("SET keepalive");
            }
        }
        catch (OperationCanceledException) { }
    }

    // ---- receive --------------------------------------------------------

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
                        Disconnected?.Invoke(Refusal ?? "Server closed the connection");
                        return;
                    }
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);

                var data = ms.GetBuffer();
                int len = (int)ms.Length;
                if (len < 3) continue;
                string tag = Encoding.ASCII.GetString(data, 0, 3);
                if (tag == "MSG") HandleMsg(Encoding.UTF8.GetString(data, 3, len - 3));
                else if (tag == "SND" && isSound) HandleSnd(data, len);
                else if (tag == "W/F" && !isSound) HandleWf(data, len);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested) Disconnected?.Invoke(Refusal ?? ex.Message);
        }
    }

    void HandleMsg(string text)
    {
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = token.IndexOf('=');
            string key = eq < 0 ? token : token[..eq];
            string val = eq < 0 ? "" : Uri.UnescapeDataString(token[(eq + 1)..]);
            switch (key)
            {
                case "bandwidth":
                    if (double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var bw) && bw > 1e6)
                        BandwidthHz = bw;
                    break;
                case "too_busy": Refusal = "All user slots on this receiver are in use"; break;
                case "badp": if (val != "0") Refusal = "This receiver needs a password"; break;
                case "down": Refusal = "This receiver is down for maintenance"; break;
                case "inactivity_timeout": Refusal = "Disconnected by the server for inactivity"; break;
                case "kiwi_kick": Refusal = "Kicked by the server"; break;
            }
            if (key is "too_busy" or "badp" or "down" or "inactivity_timeout" or "kiwi_kick" or "version_maj"
                or "version_min" or "audio_rate" or "sample_rate" or "bandwidth" or "center_freq")
                Log?.Invoke($"server: {key}={val}");
        }
    }

    void HandleSnd(byte[] d, int len)
    {
        if (len < 10) return;
        byte flags = d[3];
        int smeter = (d[8] << 8) | d[9];
        SignalDbm?.Invoke(0.1 * smeter - 127.0);

        if ((flags & 0x10) != 0) { Log?.Invoke("server sent compressed audio, which is not supported yet"); return; }
        if ((flags & 0x08) != 0) return; // stereo / IQ

        bool little = (flags & 0x80) != 0;
        int n = (len - 10) / 2;
        if (n <= 0) return;
        var pcm = new short[n];
        for (int i = 0; i < n; i++)
        {
            int o = 10 + i * 2;
            pcm[i] = little ? (short)(d[o] | (d[o + 1] << 8)) : (short)((d[o] << 8) | d[o + 1]);
        }
        AudioReceived?.Invoke(pcm);
    }

    void HandleWf(byte[] d, int len)
    {
        int h = len - WfBins - 12;     // header sits directly before the bins
        if (h < 3) return;
        uint xBin = BitConverter.ToUInt32(d, h);
        uint fz = BitConverter.ToUInt32(d, h + 4);
        int zoom = (int)(fz & 0xFFFF);

        var bins = new byte[WfBins];
        Buffer.BlockCopy(d, len - WfBins, bins, 0, WfBins);

        double span = BandwidthHz / Math.Pow(2, Zoom);
        double start = WfCenterHz - span / 2;
        if (zoom == Zoom)
        {
            double s2 = xBin * BandwidthHz / ((double)WfBins * Math.Pow(2, zoom));
            if (s2 >= -span && s2 <= BandwidthHz) start = s2;
        }
        WaterfallRow?.Invoke(bins, start, span);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _snd?.Dispose(); } catch { }
        try { _wf?.Dispose(); } catch { }
    }
}
