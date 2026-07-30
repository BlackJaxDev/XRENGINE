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
    public static BackendReadyFramePackageIdentity Unspecified => new(
        0UL,
        -1L,
        0UL,
        0,
        0,
        0,
        0,
        0,
        0,
        0);
}
