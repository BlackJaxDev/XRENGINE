using XREngine.Rendering.Commands;

namespace XREngine.Rendering.WebGPU;

/// <summary>Exact raster/coverage state shared by a GPU compact triangle range.</summary>
internal readonly record struct WebGpuAdvancedVisibilityBucketKey(
    uint RasterStateClass, uint CullMode, EAdvancedMaterialCoverageMode Coverage,
    EAdvancedGeometryProducer Producer, AdvancedGpuHandle CoverageTexture, AdvancedGpuHandle CoverageSampler,
    AdvancedGpuHandle OpacityTexture, AdvancedGpuHandle OpacitySampler,
    uint CoverageSamplingKey = 0, uint OpacitySamplingKey = 0, AdvancedGpuHandle UberMaterial = default);
