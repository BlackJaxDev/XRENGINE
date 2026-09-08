namespace XREngine.Rendering;

/// <summary>
/// Operational rules and fallback resolution for advanced global illumination.
/// </summary>
public static class AdvancedGlobalIlluminationContract
{
    /// <summary>
    /// Validates that an active GI provider is valid for use, or determines its fallback mode.
    /// </summary>
    public static EGlobalIlluminationMode ResolveActiveMode(IAdvancedGlobalIlluminationProvider? provider)
    {
        if (provider == null || !provider.IsSupported)
            return EGlobalIlluminationMode.LightProbesAndIbl;

        return provider.ActiveMode;
    }

    /// <summary>Only the native probe/IBL provider is presently executable by Advanced shading.</summary>
    public static bool IsNativeProvider(IAdvancedGlobalIlluminationProvider? provider)
        => provider is AdvancedLightProbesAndIblProvider { IsSupported: true };
}
