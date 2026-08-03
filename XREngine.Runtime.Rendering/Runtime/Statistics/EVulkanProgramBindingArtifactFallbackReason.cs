namespace XREngine.Rendering;

/// <summary>
/// Classifies why a draw cannot consume a persistent Vulkan program-binding
/// artifact and must retain the conservative capture path.
/// </summary>
public enum EVulkanProgramBindingArtifactFallbackReason
{
    None = 0,
    ShadowPass,
    RendererCallback,
    MaterialCallback,
    ActiveScopedBindings,
    PipelineVariables,
    UnsupportedEngineRequirements,
    MissingLightingOwner,
    LightingPublicationUnavailable,
    AmbientOcclusionOnly,
    MutableLegacyUniform,
    UnownedDescriptorResource,
    UnownedUniform,
    IncompleteRuntimeUniformPublication,
    ArtifactContentUnsupported,
    InvalidPublisherState,
    PublisherChangedDuringPublication,
    Count,
}
