using System.Runtime.InteropServices.JavaScript;
using XREngine.Rendering;

namespace XREngine.Browser;

/// <summary>Routes browser canvas callbacks to independent managed scene sessions.</summary>
public static partial class BrowserSceneExports
{
    private static readonly Dictionary<int, BrowserSceneSession> Sessions = new();
    private static int _nextId;

    [JSExport]
    public static int Create(string canvasId)
    {
        // Do not reuse an ID: late callbacks from an old canvas must never reach a new scene.
        if (_nextId == int.MaxValue)
            throw new InvalidOperationException("Browser scene session identifiers are exhausted.");
        int id = ++_nextId;
        BrowserSceneSession session = new(id, canvasId);
        try
        {
            Sessions.Add(id, session);
            return id;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    [JSExport]
    public static void Resize(int id, double logicalWidth, double logicalHeight, int width, int height,
        double pixelRatio, int generation, bool visible, bool focused, bool attached)
    {
        Get(id).Resize(new RuntimeSurfaceState(logicalWidth, logicalHeight, width, height,
            pixelRatio, generation, visible, focused, attached));
    }

    [JSExport]
    public static void Input(int id, double pointerX, double pointerY, bool down,
        double directionX, double directionY)
    {
        Get(id).Input(new RuntimeInputState(
            checked((float)pointerX), checked((float)pointerY), down,
            checked((float)directionX), checked((float)directionY)));
    }

    [JSExport]
    public static void Frame(int id, double timestampMilliseconds) => Get(id).Frame(timestampMilliseconds);

    [JSExport]
    public static void SetSplitView(int id, bool split) => Get(id).SetSplitView(split);

    [JSExport]
    public static void ResetClock(int id) => Get(id).ResetClock();

    [JSExport]
    public static void Destroy(int id)
    {
        if (!Sessions.Remove(id, out BrowserSceneSession? session))
            return;
        // Remove first so a callback during teardown cannot observe this session.
        session.Dispose();
    }

    private static BrowserSceneSession Get(int id) => Sessions.TryGetValue(id, out BrowserSceneSession? session)
        ? session
        : throw new InvalidOperationException("Browser scene session does not exist.");

    [JSImport("beginFrame", "xrengine.canvas")]
    internal static partial bool BeginFrame(int session, int surfaceGeneration);

    [JSImport("draw", "xrengine.canvas")]
    internal static partial void Draw(int session, int viewIndex, int x, int y, int width, int height,
        float m11, float m12, float m13, float m14,
        float m21, float m22, float m23, float m24,
        float m31, float m32, float m33, float m34,
        float m41, float m42, float m43, float m44);

    [JSImport("endFrame", "xrengine.canvas")]
    internal static partial void EndFrame(int session);

    [JSImport("abortFrame", "xrengine.canvas")]
    internal static partial void AbortFrame(int session);
}
