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
    public static void InitializeGraphics(int id) => Get(id).InitializeGraphics();

    [JSExport]
    public static void SetInstanceCount(int id, int count) => Get(id).SetInstanceCount(count);

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

    [JSImport("createMesh", "xrengine.canvas")]
    internal static partial int CreateMesh(int session, [JSMarshalAs<JSType.MemoryView>] Span<byte> vertexMemory,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> indexMemory);

    [JSImport("createTexture", "xrengine.canvas")]
    internal static partial int CreateTexture(int session, int width, int height,
        [JSMarshalAs<JSType.MemoryView>] Span<byte> rgbaMemory);

    [JSImport("createMaterial", "xrengine.canvas")]
    internal static partial int CreateMaterial(int session, int textureHandle, float r, float g, float b, float a);

    [JSImport("destroyResource", "xrengine.canvas")]
    internal static partial void DestroyResource(int session, int packedHandle);

    [JSImport("submitPacket", "xrengine.canvas")]
    internal static partial void SubmitPacket(int session, [JSMarshalAs<JSType.MemoryView>] Span<byte> memoryView);
}
