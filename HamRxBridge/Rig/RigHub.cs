namespace HamRxBridge.Rig;

/// <summary>Owns whichever radio backend the settings ask for and forwards its state.</summary>
public sealed class RigHub : IDisposable
{
    IRigBackend _backend = new NoRig();

    public IRigBackend Backend => _backend;
    public event Action? Changed;

    public bool Online => _backend.Link == RigLink.Online;

    public void Restart()
    {
        _backend.StateChanged -= OnState;
        _backend.Dispose();
        var s = AppSettings.Current;
        _backend = s.Cat switch
        {
            CatSource.OmniRig => new OmniRigService(s.RigIndex),
            CatSource.IcomCiv => new DirectCat(CatSource.IcomCiv, s.CatPort, s.CatBaud, s.CivAddress, false),
            CatSource.KenwoodYaesu => new DirectCat(CatSource.KenwoodYaesu, s.CatPort, s.CatBaud, s.CivAddress, s.KenwoodRtsCts),
            _ => new NoRig()
        };
        _backend.StateChanged += OnState;
        Changed?.Invoke();
    }

    void OnState() => Changed?.Invoke();

    public void Dispose()
    {
        _backend.StateChanged -= OnState;
        _backend.Dispose();
    }
}
