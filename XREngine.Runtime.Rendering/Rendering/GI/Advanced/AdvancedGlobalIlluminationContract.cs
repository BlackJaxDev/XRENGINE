namespace XREngine.Rendering;

/// <summary>
/// Provider validation rules for native Advanced global illumination.
/// </summary>
public static class AdvancedGlobalIlluminationContract
{
    /// <summary>Returns whether the provider implements its matching native Advanced shading mode.</summary>
    public static bool IsNativeProvider(IAdvancedGlobalIlluminationProvider? provider)
        => provider is AdvancedLightProbesAndIblProvider { IsSupported: true } or
            AdvancedDdgiProvider { IsSupported: true };
}
