namespace XREngine.Rendering;

/// <summary>Built-in Advanced provider for the shared DDGI update and screen-composite path.</summary>
public sealed class AdvancedDdgiProvider : IAdvancedGlobalIlluminationProvider
{
    public static AdvancedDdgiProvider Instance { get; } = new();

    private AdvancedDdgiProvider() { }

    public EGlobalIlluminationMode ActiveMode => EGlobalIlluminationMode.DDGI;
    public string ProviderName => "DDGI";
    public bool IsSupported => true;
    public bool RequiresTemporalHistory => true;
    public string? OutputResourceName => AdvancedRenderPipeline.DDGITextureName;
}
