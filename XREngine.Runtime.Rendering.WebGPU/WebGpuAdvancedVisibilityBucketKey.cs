using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

/// <summary>Exact raster/coverage state shared by a GPU compact triangle range.</summary>
internal readonly record struct WebGpuAdvancedVisibilityBucketKey(
    uint RasterStateClass, uint CullMode, EAdvancedMaterialCoverageMode Coverage,
    EAdvancedGeometryProducer Producer, AdvancedGpuHandle CoverageTexture, AdvancedGpuHandle CoverageSampler);
