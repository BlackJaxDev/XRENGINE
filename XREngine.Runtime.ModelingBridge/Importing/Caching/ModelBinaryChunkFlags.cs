namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Stable behavior flags stored in each model-cache chunk entry.
/// </summary>
[Flags]
internal enum ModelBinaryChunkFlags : uint
{
    None = 0,
    Required = 1 << 0,
}
