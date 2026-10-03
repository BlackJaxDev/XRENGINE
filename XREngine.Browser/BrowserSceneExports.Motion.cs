using System.Runtime.InteropServices.JavaScript;

namespace XREngine.Browser;

public static partial class BrowserSceneExports
{
    [JSExport]
    public static void InputActions(int id, double moveX, double moveY, double lookX, double lookY, bool jump)
        => Get(id).InputActions(moveX, moveY, lookX, lookY, jump);

    [JSExport]
    public static void InputLookDelta(int id, double x, double y) => Get(id).InputLookDelta(x, y);

    [JSExport]
    public static void InputPointer(int id, int pointerId, int phase, double x, double y, int kind)
        => Get(id).InputPointer(pointerId, phase, x, y, kind);

    [JSExport]
    public static void InputWheel(int id, double deltaPixels) => Get(id).InputWheel(deltaPixels);

    [JSExport]
    public static void ResetInput(int id) => Get(id).ResetInput();
}
