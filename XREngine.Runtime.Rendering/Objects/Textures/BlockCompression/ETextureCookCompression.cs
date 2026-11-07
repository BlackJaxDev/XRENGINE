namespace XREngine.Rendering;

/// <summary>
/// Selects how the texture cook stores 2D texture mips in the streaming cache.
/// </summary>
public enum ETextureCookCompression
{
    /// <summary>Stores uncompressed RGBA8 mips.</summary>
    None,

    /// <summary>
    /// Stores BC7 mips (1 byte per pixel against 4 for RGBA8). Sources that are not
    /// 8-bit RGBA, and caches cooked without an installed encoder, stay RGBA8.
    /// </summary>
    Bc7,
}
