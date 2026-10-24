namespace XREngine.Rendering.UI;

internal readonly struct UIWorldPassManifestScope : IDisposable
{
    private readonly UIWorldPassManifest? _previous;

    internal UIWorldPassManifestScope(UIWorldPassManifest manifest)
    {
        _previous = UIWorldPassManifest.Active;
        UIWorldPassManifest.Active = manifest;
    }

    public void Dispose() => UIWorldPassManifest.Active = _previous;
}
