namespace HamRxBridge.Rig;

public enum RigMode { USB, LSB, CW, AM, NFM }

public static class RigModeInfo
{
    // OmniRig RigParamX bit values (from the OmniRig type library).
    public const int PM_CW_U = 0x00800000;
    public const int PM_CW_L = 0x01000000;
    public const int PM_SSB_U = 0x02000000;
    public const int PM_SSB_L = 0x04000000;
    public const int PM_DIG_U = 0x08000000;
    public const int PM_DIG_L = 0x10000000;
    public const int PM_AM = 0x20000000;
    public const int PM_FM = 0x40000000;
    public const int PM_TX = 0x00400000;

    public static RigMode? FromOmniRig(int pm) => pm switch
    {
        PM_CW_U or PM_CW_L => RigMode.CW,
        PM_SSB_U or PM_DIG_U => RigMode.USB,
        PM_SSB_L or PM_DIG_L => RigMode.LSB,
        PM_AM => RigMode.AM,
        PM_FM => RigMode.NFM,
        _ => null
    };

    public static int ToOmniRig(RigMode m) => m switch
    {
        RigMode.USB => PM_SSB_U,
        RigMode.LSB => PM_SSB_L,
        RigMode.CW => PM_CW_U,
        RigMode.AM => PM_AM,
        _ => PM_FM
    };

    /// <summary>Mode string used by KiwiSDR and by the WebSDR "tune" URL parameter.</summary>
    public static string ToWire(RigMode m) => m switch
    {
        RigMode.USB => "usb",
        RigMode.LSB => "lsb",
        RigMode.CW => "cw",
        RigMode.AM => "am",
        _ => "nbfm"
    };

    /// <summary>WebSDR uses "fm" rather than "nbfm".</summary>
    public static string ToWebSdr(RigMode m) => m == RigMode.NFM ? "fm" : ToWire(m);

    public static RigMode DefaultForFrequency(double hz) => hz < 10_000_000 ? RigMode.LSB : RigMode.USB;
}
