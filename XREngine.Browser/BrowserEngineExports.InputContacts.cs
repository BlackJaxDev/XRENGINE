using System.Runtime.InteropServices.JavaScript;

namespace XREngine.Browser;

public static partial class BrowserEngineExports
{
    /// <summary>Forwards a touch contact; UI ownership and gameplay mappings remain in the shared engine.</summary>
    [JSExport]
    public static bool InputContact(int id, int phase, float x, float y)
        => _session?.InputContact(id, phase, x, y) ?? false;
}
