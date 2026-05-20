// =====================================================================================
// EAtlasTier.cs - residency tier classification for meshes packed into the GPU atlas.
// =====================================================================================

namespace XREngine.Rendering.Commands
{
    /// <summary>
    /// Identifies which atlas residency tier a mesh lives in. Static = upload-once,
    /// Dynamic = append/rebuild as commands change, Streaming = ring-buffered per-frame writes.
    /// </summary>
    public enum EAtlasTier : uint
    {
        Static = 0,
        Dynamic = 1,
        Streaming = 2,
    }
}
