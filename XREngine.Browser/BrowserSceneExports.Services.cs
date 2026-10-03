using System.Runtime.InteropServices.JavaScript;

namespace XREngine.Browser;

public static partial class BrowserSceneExports
{
    /// <summary>Applies committed DOM text without treating IME composition as gameplay input.</summary>
    [JSExport]
    public static void SetSceneLabel(int id, string label) => Get(id).SetSceneLabel(label);
}
