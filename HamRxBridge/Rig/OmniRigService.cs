using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace HamRxBridge.Rig;

/// <summary>
/// OmniRig through late bound COM (IDispatch): no interop assembly, works from 64 bit.
/// State is polled every 200 ms, which avoids COM event sink trouble.
/// </summary>
public sealed class OmniRigService : IRigBackend
{
    object? _omni;
    readonly DispatcherTimer _timer;
    readonly int _rigIndex;

    public RigLink Link { get; private set; } = RigLink.NotInstalled;
    public string LinkText { get; private set; } = "Starting OmniRig";
    public double FreqHz { get; private set; }
    public RigMode? Mode { get; private set; }
    public bool Transmitting { get; private set; }

    public event Action? StateChanged;

    public OmniRigService(int rigIndex)
    {
        _rigIndex = rigIndex == 2 ? 2 : 1;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Poll();
        try
        {
            var t = Type.GetTypeFromProgID("OmniRig.OmniRigX");
            if (t == null) { Set(RigLink.NotInstalled, "OmniRig not installed"); return; }
            _omni = Activator.CreateInstance(t);
            _timer.Start();
            Poll();
        }
        catch (Exception ex)
        {
            Set(RigLink.NotInstalled, "OmniRig failed: " + ex.Message);
        }
    }

    public static bool IsInstalled() => Type.GetTypeFromProgID("OmniRig.OmniRigX") != null;

    dynamic? Rig()
    {
        if (_omni == null) return null;
        dynamic o = _omni;
        return _rigIndex == 2 ? o.Rig2 : o.Rig1;
    }

    void Poll()
    {
        try
        {
            dynamic? rig = Rig();
            if (rig == null) return;
            int status = rig.Status;
            var link = status switch
            {
                0 => RigLink.NotConfigured,
                1 => RigLink.Disabled,
                2 => RigLink.PortBusy,
                3 => RigLink.NotResponding,
                4 => RigLink.Online,
                _ => RigLink.NotResponding
            };
            string name = "";
            try { name = (string)rig.RigType; } catch { }
            string text = link == RigLink.Online
                ? $"OmniRig {_rigIndex}: {name}"
                : $"OmniRig {_rigIndex}: {(string)rig.StatusStr}";

            bool changed = link != Link || text != LinkText;
            Link = link; LinkText = text;

            if (link == RigLink.Online)
            {
                double f = (int)rig.Freq;
                var mode = RigModeInfo.FromOmniRig((int)rig.Mode);
                bool tx = ((int)rig.Tx) == RigModeInfo.PM_TX;
                if (f > 0 && Math.Abs(f - FreqHz) >= 1) { FreqHz = f; changed = true; }
                if (mode.HasValue && mode != Mode) { Mode = mode; changed = true; }
                if (tx != Transmitting) { Transmitting = tx; changed = true; }
            }
            if (changed) StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Set(RigLink.NotResponding, "OmniRig error: " + ex.Message);
        }
    }

    void Set(RigLink link, string text)
    {
        if (link == Link && text == LinkText) return;
        Link = link; LinkText = text;
        StateChanged?.Invoke();
    }

    public void SetFrequency(double hz)
    {
        if (Link != RigLink.Online) return;
        try { Rig()!.Freq = (int)Math.Round(hz); FreqHz = Math.Round(hz); } catch { }
    }

    public void SetMode(RigMode mode)
    {
        if (Link != RigLink.Online) return;
        try { Rig()!.Mode = RigModeInfo.ToOmniRig(mode); Mode = mode; } catch { }
    }

    public static void ShowDialog()
    {
        try
        {
            var t = Type.GetTypeFromProgID("OmniRig.OmniRigX");
            if (t == null) return;
            dynamic o = Activator.CreateInstance(t)!;
            o.DialogVisible = true;
        }
        catch { }
    }

    public void Dispose()
    {
        _timer.Stop();
        if (_omni != null)
        {
            try { Marshal.FinalReleaseComObject(_omni); } catch { }
            _omni = null;
        }
    }
}
