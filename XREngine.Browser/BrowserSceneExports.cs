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
        => CreateCore(canvasId, null);

    /// <summary>Creates a session from a bounded, versioned portable scene payload.</summary>
    [JSExport]
    public static int CreateFromSnapshot(string canvasId, string json)
        => CreateCore(canvasId, BrowserSceneSnapshot.FromJson(json));

    [JSExport]
    public static void ValidateSnapshot(string json) => _ = BrowserSceneSnapshot.FromJson(json);

    private static int CreateCore(string canvasId, BrowserSceneSnapshot? snapshot)
    {
        // Do not reuse an ID: late callbacks from an old canvas must never reach a new scene.
        if (_nextId == int.MaxValue)
            throw new InvalidOperationException("Browser scene session identifiers are exhausted.");
        int id = ++_nextId;
        BrowserSceneSession session = new(id, canvasId, snapshot);
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
    public static void InitializeGraphics(int id, string colorFormat)
    {
        BrowserSceneSession session = Get(id);
        session.Target.SetColorFormat(colorFormat);
        session.InitializeGraphics();
    }

    [JSExport]
    public static void SetCullingEnabled(int id, bool enabled) => Get(id).SetCullingEnabled(enabled);

    [JSExport]
    public static void SetRenderableTint(int id, int index, double red, double green, double blue)
        => Get(id).SetRenderableTint(index,
            new System.Numerics.Vector4((float)red, (float)green, (float)blue, 1));

    /// <summary>Allocates a diagnostic snapshot only in response to an explicit host request.</summary>
    [JSExport]
    public static string GetStatistics(int id)
    {
        BrowserSceneSession session = Get(id);
        return System.Text.Json.JsonSerializer.Serialize(new BrowserSceneStatistics(
            session.CullingEnabled, session.VisibilityCandidates, session.VisibilityCulled, session.VisibilityDrawn,
            session.RetainedMeshCount, session.RetainedMaterialCount, session.RetainedTextureCount,
            session.Target.TryDescribeFrameOutput(out RenderFrameOutputDescription output) ? output : null));
    }

    /// <summary>Records a browser renderer failure before the frame loop stops.</summary>
    [JSExport]
    public static void RendererFailed(int id, bool deviceLost) => Get(id).RendererFailed(deviceLost);

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

}
