namespace XREngine;

public readonly record struct RvcReusePolicy(
    ERvcReuseDomain EnabledDomains,
    float MaxNormalAngleDegrees,
    float MaxDepthDeltaMeters,
    byte MaxRoughnessBucketDelta,
    byte MaxLodBucketDelta,
    ERvcMaterialViewDependency ExcludedViewDependencies)
{
    public static RvcReusePolicy DefaultsWithStereoOff => new(
        ERvcReuseDomain.IntraView | ERvcReuseDomain.InsetWide,
        MaxNormalAngleDegrees: 5.0f,
        MaxDepthDeltaMeters: 0.05f,
        MaxRoughnessBucketDelta: 1,
        MaxLodBucketDelta: 0,
        ERvcMaterialViewDependency.Refraction |
        ERvcMaterialViewDependency.ParallaxOcclusion |
        ERvcMaterialViewDependency.VirtualDisplacement |
        ERvcMaterialViewDependency.ScreenSpaceEffect);

    public bool IsEnabled(ERvcReuseDomain domain)
        => (EnabledDomains & domain) != 0;
}
