namespace XREngine.Rendering;

/// <summary>
/// Built-in native provider for captured octahedral irradiance and prefiltered
/// reflection probes. It intentionally owns the only GI mode currently
/// admitted to Advanced native shading.
/// </summary>
public sealed class AdvancedLightProbesAndIblProvider : IAdvancedGlobalIlluminationProvider
{
    public static AdvancedLightProbesAndIblProvider Instance { get; } = new();

    private AdvancedLightProbesAndIblProvider() { }

    public EGlobalIlluminationMode ActiveMode => EGlobalIlluminationMode.LightProbesAndIbl;
    public string ProviderName => "Light Probes and IBL";
    public bool IsSupported => true;
    public bool RequiresTemporalHistory => false;
    public string? OutputResourceName => null;
}
