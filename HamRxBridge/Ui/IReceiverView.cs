using System.Windows;
using HamRxBridge.Rig;

namespace HamRxBridge.Ui;

public enum StatusLevel { Info, Good, Warn, Bad }

public sealed class ReceiverCaps
{
    public bool Bandwidth { get; init; }
    public bool SMeter { get; init; }
    public bool Volume { get; init; }
    public bool Agc { get; init; }
}

/// <summary>One receiver display that the control panel drives.</summary>
public interface IReceiverView : IDisposable
{
    FrameworkElement Element { get; }
    ReceiverCaps Caps { get; }
    string ControlMethod { get; }

    void Tune(double hz, RigMode mode, int lowCut, int highCut);
    void SetMuted(bool muted);
    void SetTxMuted(bool muted);
    void SetVolume(double volume);
    void SetAgc(bool on);

    /// <summary>The operator tuned on the receiver itself (waterfall click or the web page). Mode is null when unknown.</summary>
    event Action<double, RigMode?>? ReceiverTuned;
    event Action<double>? Signal;
    event Action<string, StatusLevel>? StatusChanged;
    event Action? CapsChanged;
}

public static class Passbands
{
    public static double DefaultWidth(RigMode m) => m switch
    {
        RigMode.USB or RigMode.LSB => 2700,
        RigMode.CW => 500,
        RigMode.AM => 9000,
        _ => 12000
    };

    public static double[] Presets(RigMode m) => m switch
    {
        RigMode.USB or RigMode.LSB => new double[] { 1800, 2400, 2700, 3000 },
        RigMode.CW => new double[] { 250, 500, 1000, 2000 },
        RigMode.AM => new double[] { 5000, 6000, 9000, 12000 },
        _ => new double[] { 9000, 12000 }
    };

    public static int CwPitch => Math.Clamp(AppSettings.Current.CwPitch, 300, 1000);

    /// <summary>
    /// Passband edges in Hz relative to the carrier (KiwiSDR convention). For SSB and AM the
    /// carrier equals the radio dial. In CW the dial shows the signal frequency, so the carrier
    /// sits one pitch below it and the passband is centred on the pitch.
    /// </summary>
    /// <summary>Carrier minus dial: negative pitch in CW, zero otherwise.</summary>
    public static double CarrierOffset(RigMode m) => m == RigMode.CW ? -CwPitch : 0;

    public static (int lo, int hi) Edges(RigMode m, double width) => m switch
    {
        RigMode.USB => (200, (int)(200 + width)),
        RigMode.LSB => ((int)-(200 + width), -200),
        RigMode.CW => ((int)(CwPitch - width / 2), (int)(CwPitch + width / 2)),
        _ => ((int)-(width / 2), (int)(width / 2))
    };
}
