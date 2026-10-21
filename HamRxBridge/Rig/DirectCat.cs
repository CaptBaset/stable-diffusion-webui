using System.Globalization;
using System.IO.Ports;
using System.Text;

namespace HamRxBridge.Rig;

/// <summary>
/// Built in CAT for when OmniRig is not wanted: Icom CI-V (IC-7300, IC-705, IC-7610 ...)
/// and Kenwood or Yaesu ASCII (TS-480, TS-590, TS-2000, FT-991 ...).
///
/// Safety: this class only ever sends read commands (frequency, mode, TX state, ID) and
/// frequency or mode set commands. It never sends a transmit command. RTS and DTR are held
/// low unless Kenwood hardware flow control is explicitly enabled.
/// </summary>
public sealed class DirectCat : IRigBackend
{
    readonly CatSource _proto;
    readonly string _portName;
    readonly int _baud;
    readonly byte _civ;
    readonly bool _rtsCts;
    readonly SynchronizationContext? _ui;
    readonly Thread _thread;
    readonly object _gate = new();
    volatile bool _stop;
    double _pendingHz = -1;
    RigMode? _pendingMode;

    // Kenwood/Yaesu dialect learned from the radio's answers
    bool? _useIf;
    int _faDigits = 11;
    bool _yaesuMd;

    public RigLink Link { get; private set; } = RigLink.NotResponding;
    public string LinkText { get; private set; } = "Opening port";
    public double FreqHz { get; private set; }
    public RigMode? Mode { get; private set; }
    public bool Transmitting { get; private set; }
    public event Action? StateChanged;

    public DirectCat(CatSource proto, string port, int baud, string civHex, bool rtsCts)
    {
        _proto = proto;
        _portName = port;
        _baud = baud;
        _civ = byte.TryParse(civHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var a) ? a : (byte)0x94;
        _rtsCts = rtsCts;
        _ui = SynchronizationContext.Current;   // the WPF UI thread when created from the app
        _thread = new Thread(Run) { IsBackground = true, Name = "DirectCat" };
        _thread.Start();
    }

    string Label => _proto == CatSource.IcomCiv ? $"Icom CI-V {_portName}" : $"Kenwood/Yaesu {_portName}";

    public void SetFrequency(double hz) { lock (_gate) _pendingHz = Math.Round(hz); }
    public void SetMode(RigMode mode) { lock (_gate) _pendingMode = mode; }

    void Publish(RigLink link, string text, double? hz = null, RigMode? mode = null, bool? tx = null)
    {
        void Apply()
        {
            bool changed = link != Link || text != LinkText;
            Link = link; LinkText = text;
            if (hz is > 0 && Math.Abs(hz.Value - FreqHz) >= 1) { FreqHz = hz.Value; changed = true; }
            if (mode.HasValue && mode != Mode) { Mode = mode; changed = true; }
            if (tx.HasValue && tx.Value != Transmitting) { Transmitting = tx.Value; changed = true; }
            if (changed) StateChanged?.Invoke();
        }
        if (_ui != null) _ui.Post(_ => Apply(), null);
        else Apply();
    }

    static SerialPort OpenPort(string name, int baud, bool rtsCts)
    {
        var p = new SerialPort(name, baud, Parity.None, 8, StopBits.One)
        {
            Handshake = rtsCts ? Handshake.RequestToSend : Handshake.None,
            DtrEnable = false,
            ReadTimeout = 40,
            WriteTimeout = 500,
            Encoding = Encoding.ASCII
        };
        if (!rtsCts) p.RtsEnable = false;
        p.Open();
        return p;
    }

    void Run()
    {
        SerialPort? port = null;
        int misses = 0;
        while (!_stop)
        {
            try
            {
                if (port == null)
                {
                    if (string.IsNullOrWhiteSpace(_portName))
                    {
                        Publish(RigLink.NotConfigured, "Pick a COM port in Settings");
                        Thread.Sleep(2000);
                        continue;
                    }
                    try { port = OpenPort(_portName, _baud, _proto == CatSource.KenwoodYaesu && _rtsCts); }
                    catch (Exception ex)
                    {
                        Publish(RigLink.PortBusy, $"{_portName} busy or missing ({ex.GetType().Name})");
                        Thread.Sleep(3000);
                        continue;
                    }
                }

                ApplyPending(port);
                bool ok = _proto == CatSource.IcomCiv ? PollIcom(port) : PollKenwood(port);
                if (ok) misses = 0;
                else if (++misses >= 3) Publish(RigLink.NotResponding, $"{Label}: radio not answering at {_baud} baud");
                Thread.Sleep(200);
            }
            catch (Exception ex)
            {
                Log.Write("DirectCat: " + ex.Message);
                Publish(RigLink.NotResponding, $"{Label}: {ex.Message}");
                try { port?.Dispose(); } catch { }
                port = null;
                Thread.Sleep(2000);
            }
        }
        try { port?.Dispose(); } catch { }
    }

    void ApplyPending(SerialPort p)
    {
        double hz; RigMode? mode;
        lock (_gate) { hz = _pendingHz; mode = _pendingMode; _pendingHz = -1; _pendingMode = null; }
        if (hz > 0)
        {
            if (_proto == CatSource.IcomCiv) IcomTransact(p, 0x05, IcomBcd(hz), 0xFB);
            else WriteAscii(p, "FA" + ((long)hz).ToString("D" + (_useIf == true ? 11 : _faDigits), CultureInfo.InvariantCulture) + ";");
        }
        if (mode.HasValue)
        {
            if (_proto == CatSource.IcomCiv) IcomTransact(p, 0x06, new[] { IcomModeCode(mode.Value) }, 0xFB);
            else WriteAscii(p, _yaesuMd ? $"MD0{KenwoodModeCode(mode.Value)};" : $"MD{KenwoodModeCode(mode.Value)};");
        }
    }

    // ------------------------------------------------------------------ Icom CI-V

    readonly List<byte> _rx = new();
    readonly byte[] _buf = new byte[512];

    bool PollIcom(SerialPort p)
    {
        var f = IcomTransact(p, 0x03, Array.Empty<byte>(), 0x03);
        if (f == null) return false;
        double? hz = f.Length >= 10 ? IcomFreq(f, 5) : null;

        RigMode? mode = null;
        var m = IcomTransact(p, 0x04, Array.Empty<byte>(), 0x04);
        if (m != null && m.Length >= 7) mode = IcomMode(m[5]);

        bool? tx = null;
        var t = IcomTransact(p, 0x1C, new byte[] { 0x00 }, 0x1C);
        if (t != null && t.Length >= 8 && t[5] == 0x00) tx = t[6] == 0x01;

        Publish(RigLink.Online, $"{Label} (addr {_civ:X2}h)", hz, mode, tx);
        return true;
    }

    /// <summary>Send one CI-V command and wait for the radio's reply with the given command byte.</summary>
    byte[]? IcomTransact(SerialPort p, byte cmd, byte[] data, byte expect)
    {
        var frame = new List<byte> { 0xFE, 0xFE, _civ, 0xE0, cmd };
        frame.AddRange(data);
        frame.Add(0xFD);
        p.DiscardInBuffer();
        _rx.Clear();
        p.Write(frame.ToArray(), 0, frame.Count);

        var deadline = DateTime.UtcNow.AddMilliseconds(250);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                int n = p.Read(_buf, 0, _buf.Length);
                for (int i = 0; i < n; i++) _rx.Add(_buf[i]);
            }
            catch (TimeoutException) { }

            while (TryTakeFrame(out var fr))
            {
                if (fr.Length < 5 || fr[3] == 0xE0) continue;          // our own echo
                if (fr[2] == 0x00 && fr[4] == 0x00 && fr.Length >= 10)  // transceive broadcast: frequency
                    Publish(RigLink.Online, $"{Label} (addr {_civ:X2}h)", IcomFreq(fr, 5));
                if (fr[2] != 0xE0) continue;
                if (fr[4] == expect || fr[4] == 0xFA) return fr[4] == 0xFA ? null : fr;
            }
        }
        return null;
    }

    bool TryTakeFrame(out byte[] frame)
    {
        frame = Array.Empty<byte>();
        int start = -1;
        for (int i = 0; i + 1 < _rx.Count; i++)
            if (_rx[i] == 0xFE && _rx[i + 1] == 0xFE) { start = i; break; }
        if (start < 0) { if (_rx.Count > 256) _rx.Clear(); return false; }
        while (start + 2 < _rx.Count && _rx[start + 2] == 0xFE) start++;   // collapse extra preambles
        int end = _rx.IndexOf(0xFD, start);
        if (end < 0) return false;
        frame = _rx.GetRange(start, end - start + 1).ToArray();
        _rx.RemoveRange(0, end + 1);
        return true;
    }

    static double IcomFreq(byte[] f, int off)
    {
        double hz = 0, mul = 1;
        for (int i = 0; i < 5 && off + i < f.Length - 1; i++)
        {
            byte b = f[off + i];
            hz += ((b & 0x0F) + 10 * (b >> 4)) * mul;
            mul *= 100;
        }
        return hz;
    }

    static byte[] IcomBcd(double hz)
    {
        long v = (long)Math.Round(hz);
        var r = new byte[5];
        for (int i = 0; i < 5; i++)
        {
            int two = (int)(v % 100);
            r[i] = (byte)(((two / 10) << 4) | (two % 10));
            v /= 100;
        }
        return r;
    }

    static RigMode? IcomMode(byte code) => code switch
    {
        0x00 or 0x04 => RigMode.LSB,
        0x01 or 0x08 => RigMode.USB,
        0x02 => RigMode.AM,
        0x03 or 0x07 => RigMode.CW,
        0x05 or 0x06 => RigMode.NFM,
        _ => null
    };

    static byte IcomModeCode(RigMode m) => m switch
    {
        RigMode.LSB => 0x00,
        RigMode.USB => 0x01,
        RigMode.AM => 0x02,
        RigMode.CW => 0x03,
        _ => 0x05
    };

    // ------------------------------------------------------------------ Kenwood / Yaesu

    bool PollKenwood(SerialPort p)
    {
        if (_useIf != false)
        {
            var r = AskAscii(p, "IF;", "IF");
            if (r != null && r.Length == 37 && long.TryParse(r.AsSpan(2, 11), out var f))
            {
                _useIf = true;
                Publish(RigLink.Online, Label, f, KenwoodMode(r[29]), r[28] == '1');
                return true;
            }
            if (r != null) _useIf = false;          // answered, but not the Kenwood layout: Yaesu or other
            else if (_useIf == null) return false;
        }

        var fa = AskAscii(p, "FA;", "FA");
        if (fa == null || fa.Length < 6 || !long.TryParse(fa.AsSpan(2), out var hz)) return false;
        _faDigits = fa.Length - 2;

        RigMode? mode = null;
        var md = AskAscii(p, "MD;", "MD");
        if (md != null && md.Length == 3) { _yaesuMd = false; mode = KenwoodMode(md[2]); }
        else if (md != null && md.Length == 4) { _yaesuMd = true; mode = YaesuMode(md[3]); }

        Publish(RigLink.Online, Label, hz, mode);
        return true;
    }

    void WriteAscii(SerialPort p, string s) => p.Write(s);

    string? AskAscii(SerialPort p, string cmd, string prefix)
    {
        p.DiscardInBuffer();
        p.Write(cmd);
        var sb = new StringBuilder();
        var deadline = DateTime.UtcNow.AddMilliseconds(300);
        while (DateTime.UtcNow < deadline)
        {
            try { sb.Append(p.ReadExisting()); } catch (TimeoutException) { }
            string s = sb.ToString();
            int semi;
            while ((semi = s.IndexOf(';')) >= 0)
            {
                string one = s[..semi];
                s = s[(semi + 1)..];
                if (one.StartsWith(prefix, StringComparison.Ordinal)) return one;
            }
            sb.Clear().Append(s);
            Thread.Sleep(10);
        }
        return null;
    }

    static RigMode? KenwoodMode(char c) => c switch
    {
        '1' or '6' => RigMode.LSB,
        '2' or '9' => RigMode.USB,
        '3' or '7' => RigMode.CW,
        '4' => RigMode.NFM,
        '5' => RigMode.AM,
        _ => null
    };

    static RigMode? YaesuMode(char c) => c switch
    {
        '1' or '6' or '8' => RigMode.LSB,
        '2' or '9' or 'C' => RigMode.USB,
        '3' or '7' => RigMode.CW,
        '4' or 'A' or 'B' or 'E' => RigMode.NFM,
        '5' or 'D' => RigMode.AM,
        _ => null
    };

    static char KenwoodModeCode(RigMode m) => m switch
    {
        RigMode.LSB => '1',
        RigMode.USB => '2',
        RigMode.CW => '3',
        RigMode.NFM => '4',
        _ => '5'
    };

    // ------------------------------------------------------------------ detection

    /// <summary>
    /// Try to find a radio. Probes are read only and RTS/DTR stay low, so a PTT interface on
    /// another port cannot be keyed. Kenwood rigs that insist on RTS/CTS may not answer here.
    /// </summary>
    public static (CatSource proto, string port, int baud, string civ, string text)? Detect(Action<string> progress)
    {
        int[] icomBauds = { 115200, 19200, 9600, 38400, 57600, 4800 };
        int[] kwBauds = { 9600, 38400, 57600, 115200, 19200, 4800 };
        byte[] civAddrs = { 0x94, 0xA4, 0x98, 0x88, 0xA2, 0x7C, 0x76, 0x80, 0x70, 0x00 };

        foreach (var name in SerialPort.GetPortNames().OrderBy(n => n))
        {
            foreach (int baud in icomBauds)
            {
                progress($"Trying Icom on {name} at {baud} ...");
                try
                {
                    using var p = OpenPort(name, baud, false);
                    foreach (var a in civAddrs)
                    {
                        var frame = new byte[] { 0xFE, 0xFE, a, 0xE0, 0x03, 0xFD };
                        p.DiscardInBuffer();
                        p.Write(frame, 0, frame.Length);
                        Thread.Sleep(120);
                        var got = new byte[256];
                        int n = 0;
                        try { n = p.Read(got, 0, got.Length); } catch (TimeoutException) { }
                        for (int i = 0; i + 5 < n; i++)
                            if (got[i] == 0xFE && got[i + 1] == 0xFE && got[i + 2] == 0xE0 && got[i + 4] == 0x03)
                                return (CatSource.IcomCiv, name, baud, got[i + 3].ToString("X2"), $"Icom found on {name}, {baud} baud, address {got[i + 3]:X2}h");
                    }
                }
                catch { break; }   // port busy or missing: skip to the next port
            }
            foreach (int baud in kwBauds)
            {
                progress($"Trying Kenwood/Yaesu on {name} at {baud} ...");
                try
                {
                    using var p = OpenPort(name, baud, false);
                    p.DiscardInBuffer();
                    p.Write("ID;");
                    Thread.Sleep(150);
                    string s = "";
                    try { s = p.ReadExisting(); } catch { }
                    int at = s.IndexOf("ID", StringComparison.Ordinal);
                    if (at >= 0 && s.IndexOf(';', at) > at)
                        return (CatSource.KenwoodYaesu, name, baud, "94", $"Kenwood/Yaesu found on {name}, {baud} baud ({s[at..s.IndexOf(';', at)]})");
                }
                catch { break; }
            }
        }
        return null;
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(1500);
    }
}
