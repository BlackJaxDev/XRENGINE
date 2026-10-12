namespace XREngine.Browser;

internal sealed partial class BrowserEngineSession
{
    public bool InputContact(int id, int phase, float x, float y)
        => _inputViewport?.Contact(id, phase, x, y) ?? false;
}
