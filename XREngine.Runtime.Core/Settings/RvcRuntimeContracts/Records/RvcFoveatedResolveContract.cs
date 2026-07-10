namespace XREngine;

public readonly record struct RvcFoveatedResolveContract(
    ERvcFovealAntiAliasingPath FovealAntiAliasingPath,
    bool FoveatedTaaFallbackAvailable,
    bool EdgeAwareUpsamplingUnderstandsViewIdentity,
    bool WideInsetMirrorComposition)
{
    public static RvcFoveatedResolveContract Default => new(
        ERvcFovealAntiAliasingPath.VisibilityEdgeAA,
        FoveatedTaaFallbackAvailable: true,
        EdgeAwareUpsamplingUnderstandsViewIdentity: true,
        WideInsetMirrorComposition: true);
}
