using System.Runtime.InteropServices.JavaScript;

namespace XREngine.Browser;

public static partial class BrowserSceneExports
{
    [JSExport]
    public static void SetComputeSkinning(int id, bool enabled) => Get(id).SetComputeSkinning(enabled);
}
