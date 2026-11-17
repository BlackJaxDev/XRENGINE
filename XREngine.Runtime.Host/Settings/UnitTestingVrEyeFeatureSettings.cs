namespace XREngine.Runtime.Bootstrap;

/// <summary>
/// Sets the <c>VrEyeFeatures</c> engine setting (<see cref="VrEyeFeatureProfile"/>): the optional
/// rendering features of every VR eye frame. The defaults match the engine defaults: depth of
/// field and motion blur off for comfort, every other feature on.
/// </summary>
public class UnitTestingVrEyeFeatureSettings
{
    public bool AmbientOcclusion { get; set; } = true;
    public bool FogAndAerialPerspective { get; set; } = true;
    public bool OrderIndependentTransparency { get; set; } = true;
    public bool DepthOfField { get; set; } = false;
    public bool MotionBlur { get; set; } = false;
    public bool DeferredMsaa { get; set; } = true;
    public bool ForwardPlusLightCulling { get; set; } = true;

    /// <summary>Copies these values to <paramref name="profile"/>.</summary>
    public void ApplyTo(VrEyeFeatureProfile profile)
    {
        profile.AmbientOcclusion = AmbientOcclusion;
        profile.FogAndAerialPerspective = FogAndAerialPerspective;
        profile.OrderIndependentTransparency = OrderIndependentTransparency;
        profile.DepthOfField = DepthOfField;
        profile.MotionBlur = MotionBlur;
        profile.DeferredMsaa = DeferredMsaa;
        profile.ForwardPlusLightCulling = ForwardPlusLightCulling;
    }
}
