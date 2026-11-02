using XREngine.Rendering.Materials;

namespace XREngine.Rendering;

/// <summary>Retains both texture aliases for one standard lit material edit.</summary>
public readonly record struct StandardLitTextureBindingEdit(
    int LegacyIndex,
    int SurfaceIndex,
    XRTexture PreviousTexture,
    XRTexture ReplacementTexture,
    MaterialSurfaceTextureBinding PreviousSurface,
    MaterialSurfaceTextureBinding ReplacementSurface);
