using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace HamRxBridge.Rig;

public enum RigLink { NotInstalled, NotConfigured, Disabled, PortBusy, NotResponding, Online }

/// <summary>
/// Talks to OmniRig through late bound COM (IDispatch), so it works regardless of
/// 32 or 64 bit and needs no interop assembly. State is polled, which avoids COM
/// event sink problems and is plenty fast for a receiver follower.
/// </summary>
public sealed class OmniRigService : IDisposable
{
    object? _omni;
    readonly DispatcherTimer _timer;

    public int RigIndex { get; private set; } = 1;
    public RigLink Link { get; private set; } = RigLink.NotInstalled;
    public string LinkText { get; private set; } = "OmniRig not started";
    public string RigName { get; private set; } = "";
    public double FreqHz { get; private set; }
    public RigMode? Mode { get; private set; }
    public bool Transmitting { get; private set; }

    public event Action? StateChanged;
    public event Action<double>? FrequencyChanged;
    public event Action<RigMode>? ModeChanged;
    public event Action<bool>? TransmitChanged;

    public OmniRigService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => Poll();
    }

    public void Start(int rigIndex)
    {
        RigIndex = rigIndex;
        try
        {
            if (_omni == null)
            {
                var t = Type.GetTypeFromProgID("OmniRig.OmniRigX");
                if (t == null)
                {
                    Set(RigLink.NotInstalled, "OmniRig is not installed (get it from dxatlas.com/omnirig)");
                    return;
                }
                _omni = Activator.CreateInstance(t);
            }
        }
        catch (Exception ex)
        {
            Set(RigLink.NotInstalled, "OmniRig could not be started: " + ex.Message);
            return;
        }
        _timer.Start();
        Poll();
    }

    public void SelectRig(int rigIndex)
    {
        RigIndex = rigIndex;
        FreqHz = 0;
        Mode = null;
        Poll();
    }

    dynamic? CurrentRig()
    {
        if (_omni == null) return null;
        dynamic o = _omni;
        return RigIndex == 2 ? o.Rig2 : o.Rig1;
    }

    void Poll()
    {
        try
        {
            dynamic? rig = CurrentRig();
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
            string text = link == RigLink.Online ? $"Rig {RigIndex}: {name}" : $"Rig {RigIndex}: {(string)rig.StatusStr}";

            bool linkChanged = link != Link || text != LinkText || name != RigName;
            Link = link; LinkText = text; RigName = name;

            if (link == RigLink.Online)
            {
                double f = (int)rig.Freq;
                int pm = (int)rig.Mode;
                bool tx = ((int)rig.Tx) == RigModeInfo.PM_TX;
                var mode = RigModeInfo.FromOmniRig(pm);

                if (tx != Transmitting) { Transmitting = tx; TransmitChanged?.Invoke(tx); linkChanged = true; }
                if (f > 0 && Math.Abs(f - FreqHz) >= 1) { FreqHz = f; FrequencyChanged?.Invoke(f); linkChanged = true; }
                if (mode.HasValue && mode != Mode) { Mode = mode; ModeChanged?.Invoke(mode.Value); linkChanged = true; }
            }
            if (linkChanged) StateChanged?.Invoke();
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
        try { CurrentRig()!.Freq = (int)Math.Round(hz); FreqHz = Math.Round(hz); } catch { }
    }

    public void SetMode(RigMode mode)
    {
        if (Link != RigLink.Online) return;
        try { CurrentRig()!.Mode = RigModeInfo.ToOmniRig(mode); Mode = mode; } catch { }
    }

    public void ShowOmniRigDialog()
    {
        try { ((dynamic)_omni!).DialogVisible = true; } catch { }
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
