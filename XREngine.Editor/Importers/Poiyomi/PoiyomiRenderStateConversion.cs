using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Scene.Importers.Poiyomi;

/// <summary>
/// Complete render-state and pass-set result for an imported Poiyomi material.
/// </summary>
public sealed record PoiyomiRenderStateConversion
{
    public required PoiyomiRenderPreset Preset { get; init; }
    public required MaterialPassSet PassSet { get; init; }
    public required EMaterialPassIdentity PrimaryPassIdentity { get; init; }
    public required ETransparencyMode TransparencyMode { get; init; }
}
