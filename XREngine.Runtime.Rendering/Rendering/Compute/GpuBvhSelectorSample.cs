namespace XREngine.Rendering.Compute;

/// <summary>One flat-versus-BVH timing observation used to calibrate selection.</summary>
public readonly record struct GpuBvhSelectorSample(
    GpuBvhSelectorBucket Bucket,
    uint CommandCount,
    double FlatNanoseconds,
    double BvhNanoseconds);
