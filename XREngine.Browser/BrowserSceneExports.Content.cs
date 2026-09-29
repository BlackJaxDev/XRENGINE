using System.Numerics;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using XREngine.Rendering;

namespace XREngine.Browser;

public static partial class BrowserSceneExports
{
    /// <summary>Creates an empty imported scene for dependency-ordered incremental uploads.</summary>
    [JSExport]
    public static int CreateCooked(string canvasId)
    {
        int id = CreateCore(canvasId, new BrowserSceneSnapshot([], [], [],
            new BrowserCameraSnapshot(Matrix4x4.Identity, Matrix4x4.Identity)));
        Get(id).EnableCookedContent();
        return id;
    }

    /// <summary>Copies one bounded typed byte array through the synchronous managed upload boundary.</summary>
    [JSExport]
    public static void UploadCookedAsset(int id, string assetId, string kind, string metadataJson, byte[] payload)
        => Get(id).UploadCookedAsset(assetId, kind, metadataJson, payload);

    /// <summary>Reports content payload ownership separately from browser network/cache accounting.</summary>
    [JSExport]
    public static string GetCookedContentStatistics(int id)
        => JsonSerializer.Serialize(Get(id).CaptureCookedContentStatistics(),
            BrowserCookedJsonContext.Default.BrowserCookedContentStatistics);
}
