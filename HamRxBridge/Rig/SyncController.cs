namespace HamRxBridge.Rig;

/// <summary>Anything that can be tuned by the rig and can report manual tuning back.</summary>
public interface ITunable
{
    void TuneFromRig(double hz, RigMode mode);
    void SetTxMute(bool muted);
    event Action<double, RigMode>? UserTuned;
}

/// <summary>
/// Keeps the radio and the remote receiver on the same frequency without
/// feedback loops. Echoes (the other side repeating what we just sent) are
/// ignored with a small tolerance and a short hold-off.
/// </summary>
public sealed class SyncController
{
    readonly OmniRigService _rig;
    ITunable? _target;
    DateTime _holdUntil = DateTime.MinValue;

    public SyncMode Mode { get; set; } = AppSettings.Current.Sync;

    public SyncController(OmniRigService rig)
    {
        _rig = rig;
        _rig.FrequencyChanged += _ => PushRigToSdr();
        _rig.ModeChanged += _ => PushRigToSdr();
        _rig.TransmitChanged += tx =>
        {
            if (AppSettings.Current.MuteOnTx) _target?.SetTxMute(tx);
        };
    }

    public void Attach(ITunable target)
    {
        Detach();
        _target = target;
        target.UserTuned += OnUserTuned;
        PushRigToSdr();
    }

    public void Detach()
    {
        if (_target != null) _target.UserTuned -= OnUserTuned;
        _target = null;
    }

    public void PushRigToSdr()
    {
        if (_target == null || Mode == SyncMode.Off) return;
        if (_rig.Link != RigLink.Online || _rig.FreqHz <= 0) return;
        _holdUntil = DateTime.UtcNow.AddMilliseconds(500);
        _target.TuneFromRig(_rig.FreqHz, _rig.Mode ?? RigModeInfo.DefaultForFrequency(_rig.FreqHz));
    }

    void OnUserTuned(double hz, RigMode mode)
    {
        if (Mode != SyncMode.TwoWay || _rig.Link != RigLink.Online) return;
        if (DateTime.UtcNow < _holdUntil) return;
        if (Math.Abs(hz - _rig.FreqHz) >= 1) _rig.SetFrequency(hz);
        if (_rig.Mode != mode) _rig.SetMode(mode);
    }
}
