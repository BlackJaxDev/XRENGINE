namespace XREngine.Rendering;

/// <summary>
/// Describes whether a texture can currently be sampled and identifies the
/// renderer-owned descriptor resource that would be published for it.
/// </summary>
/// <remarks>
/// <see cref="DescriptorResourceEpoch"/> is opaque to renderer-neutral code.
/// Backends that retain descriptor artifacts must advance it whenever the
/// physical image, image view, sampler, or equivalent binding resource changes.
/// </remarks>
public readonly record struct RenderTextureSamplingState(
    bool IsReady,
    ulong DescriptorResourceEpoch)
{
    /// <summary>
    /// Creates a state from a backend's monotonic descriptor generation while
    /// reserving zero for a texture that has no backend descriptor owner yet.
    /// </summary>
    public static RenderTextureSamplingState FromBackendGeneration(
        bool isReady,
        ulong descriptorGeneration)
        => new(
            isReady,
            descriptorGeneration == ulong.MaxValue
                ? ulong.MaxValue
                : descriptorGeneration + 1UL);

    /// <summary>
    /// Ready state for renderers whose logical texture reference is the complete
    /// binding identity and which do not retain physical descriptor artifacts.
    /// </summary>
    public static RenderTextureSamplingState LogicalResourceReady
        => new(true, 1UL);
}
