using System.Runtime.InteropServices;

namespace XREngine.Rendering;

/// <summary>
/// Visibility range key. Material instance identity is deliberately absent.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct AdvancedIndirectRangeKey(
    uint RasterStateClass,
    EAdvancedMaterialCoverageMode Coverage,
    uint CullMode,
    uint PrimitiveTopology,
    EAdvancedGeometryProducer Producer);
