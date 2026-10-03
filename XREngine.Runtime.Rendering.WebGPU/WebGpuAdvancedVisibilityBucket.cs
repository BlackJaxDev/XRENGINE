namespace XREngine.Rendering.WebGPU;

/// <summary>One immutable-state bucket; counts and triangle membership remain GPU-owned.</summary>
internal struct WebGpuAdvancedVisibilityBucket
{
    internal WebGpuAdvancedVisibilityBucketKey Key;
    internal XRTexture2D? CoverageTexture;
    internal uint TriangleBase;
    internal uint TriangleCapacity;
}
