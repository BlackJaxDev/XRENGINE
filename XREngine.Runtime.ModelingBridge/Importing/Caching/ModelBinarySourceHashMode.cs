namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Content hash used by the fixed entry-source freshness gate.
/// </summary>
internal enum ModelBinarySourceHashMode : uint
{
    None = 0,
    XxHash3_64 = 1,
}
