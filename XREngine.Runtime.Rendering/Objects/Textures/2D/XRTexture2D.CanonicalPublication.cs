using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

public partial class XRTexture2D
{
    private IAdvancedGpuPublicationSourceLifetime? _canonicalPublicationLifetime;

    /// <summary>Optional producer lifetime required by every canonical material snapshot.</summary>
    internal IAdvancedGpuPublicationSourceLifetime? CanonicalPublicationLifetime
    {
        get => _canonicalPublicationLifetime;
        set => SetField(ref _canonicalPublicationLifetime, value);
    }

    /// <summary>Publishes a GPU-authored revision only after its writer has completed.</summary>
    internal ulong AdvanceCanonicalGpuContentGeneration()
    {
        PublishCanonicalSourceContentMutation();
        return CanonicalSourceContentGeneration;
    }
}
