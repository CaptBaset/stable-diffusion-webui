namespace HamRxBridge.Rig;

public enum RigLink { NotInstalled, NotConfigured, Disabled, PortBusy, NotResponding, Online, Off }

/// <summary>
/// A source of radio state. Implementations raise their events on the UI thread.
/// </summary>
public interface IRigBackend : IDisposable
{
    RigLink Link { get; }
    string LinkText { get; }
    double FreqHz { get; }
    RigMode? Mode { get; }
    bool Transmitting { get; }

    event Action? StateChanged;

    void SetFrequency(double hz);
    void SetMode(RigMode mode);
}

/// <summary>Placeholder when radio control is switched off.</summary>
public sealed class NoRig : IRigBackend
{
    public RigLink Link => RigLink.Off;
    public string LinkText => "Radio control off";
    public double FreqHz => 0;
    public RigMode? Mode => null;
    public bool Transmitting => false;
    public event Action? StateChanged { add { } remove { } }
    public void SetFrequency(double hz) { }
    public void SetMode(RigMode mode) { }
    public void Dispose() { }
}
