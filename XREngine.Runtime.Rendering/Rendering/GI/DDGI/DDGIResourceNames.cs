namespace XREngine.Rendering.GI.DDGI;

/// <summary>
/// Stable provider-owned identities for DDGI resources. Hosts may alias these names but do not define them.
/// </summary>
public static class DDGIResourceNames
{
    public const string CompositeMaterial = "DDGICompositeFBO";
    public const string ScreenDiffuse = "DDGITexture";
    public const string IrradianceAtlas = "DDGIIrradianceAtlas";
    public const string VisibilityAtlas = "DDGIVisibilityAtlas";
    public const string ProbeStateBuffer = "DDGIProbeStateBuffer";
    public const string RayBuffer = "DDGIRayBuffer";
    public const string HitBuffer = "DDGIHitBuffer";
    public const string RayRadianceBuffer = "DDGIRayRadianceBuffer";
}
