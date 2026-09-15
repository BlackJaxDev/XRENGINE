namespace XREngine.Rendering.Commands;

/// <summary>
/// Immutable authority to consume one published backend-ready frame package.
/// </summary>
internal readonly record struct BackendReadyFramePackageConsumptionAuthority(
    RenderCommandCollection? Commands,
    long PackageGeneration,
    long CollectGeneration)
{
    /// <summary>Gets whether this authority can identify an explicitly collected package.</summary>
    public bool IsValid
        => Commands is not null &&
           PackageGeneration > 0L &&
           CollectGeneration >= 0L;
}
