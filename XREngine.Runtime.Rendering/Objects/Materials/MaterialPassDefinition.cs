using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering;

/// <summary>
/// Immutable, allocation-free-at-submission description of one pass in a
/// material pass set. Textures, parameters, and authored feature state remain
/// owned by the parent <see cref="XRMaterial"/>.
/// </summary>
public sealed record MaterialPassDefinition
{
    public required EMaterialPassIdentity Identity { get; init; }
    public required int Order { get; init; }
    public required int RenderPass { get; init; }
    public bool Enabled { get; init; } = true;
    public string? SourcePassName { get; init; }
    public string? VertexShaderPath { get; init; }
    public string? FragmentShaderPath { get; init; }
    public string[] VariantMacros { get; init; } = [];
    public RenderingParameters RenderOptions { get; init; } = new();
    public EMaterialPassCoverageRules CoverageRules { get; init; } = EMaterialPassCoverageRules.All;
    public float PolygonOffsetFactor { get; init; }
    public float PolygonOffsetUnits { get; init; }
    public bool IgnoreFog { get; init; }
    public ulong PositionOpacityStateHash { get; init; }
}
