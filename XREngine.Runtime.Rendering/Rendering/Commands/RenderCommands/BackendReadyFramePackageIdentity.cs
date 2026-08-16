namespace XREngine.Rendering.Commands;

/// <summary>
/// Identifies the scene, pipeline, resource, and viewport generations captured
/// by a backend-ready frame package.
/// </summary>
public readonly record struct BackendReadyFramePackageIdentity(
    ulong FrameId,
    long CollectGeneration,
    ulong CommandGeneration,
    int ResourceGeneration,
    int DescriptorGeneration,
    int RenderGraphGeneration,
    int ViewportWidth,
    int ViewportHeight,
    int InternalWidth,
    int InternalHeight)
{
    /// <summary>
    /// Identifies a package that predates explicit identity publication and
    /// therefore retains the historical validation-bypass behavior.
    /// </summary>
    public const long UnspecifiedCollectGeneration = long.MinValue;

    /// <summary>
    /// Identifies a package whose collected command membership is intentionally
    /// retained until its owner explicitly publishes a replacement. Shadow
    /// viewports use this because atlas content hashes, rather than the global
    /// visibility generation, decide when their cached caster set is rebuilt.
    /// </summary>
    public const long RetainedCollectGeneration = -1L;

    public static BackendReadyFramePackageIdentity Unspecified => new(
        0UL,
        UnspecifiedCollectGeneration,
        0UL,
        0,
        0,
        0,
        0,
        0,
        0,
        0);
}
