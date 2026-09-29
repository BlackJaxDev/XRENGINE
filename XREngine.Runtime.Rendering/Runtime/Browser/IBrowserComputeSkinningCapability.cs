using System.Numerics;

namespace XREngine.Rendering;

/// <summary>Explicit compute deformation using the engine's packed influence, palette and sparse morph contracts.</summary>
public interface IBrowserComputeSkinningCapability
{
    void ConfigureComputeSkinning(BrowserResourceHandle mesh, BrowserSkinningData data);
    /// <summary>Publishes final affine palettes in the mesh's draw space; world-space palettes require an identity draw transform.</summary>
    void UpdateComputeSkinning(BrowserResourceHandle mesh, ReadOnlySpan<SkinPaletteMatrix> palette, ReadOnlySpan<Vector2> activeMorphs);
    void ReleaseComputeSkinning(BrowserResourceHandle mesh);
}
