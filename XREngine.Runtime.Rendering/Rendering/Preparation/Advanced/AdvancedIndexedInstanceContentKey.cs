using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>Identifies immutable indexed source content within one database generation.</summary>
internal readonly record struct AdvancedIndexedInstanceContentKey(
    AdvancedGpuHandle Geometry,
    AdvancedGpuHandle Material,
    uint PrimitiveSection,
    uint IndexCount,
    uint VertexCount);
