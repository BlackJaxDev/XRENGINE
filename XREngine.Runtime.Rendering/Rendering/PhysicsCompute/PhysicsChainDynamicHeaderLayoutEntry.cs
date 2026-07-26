namespace XREngine.Rendering.Compute;

/// <summary>
/// Exact reusable layout identity for one request's compact dynamic tree headers.
/// </summary>
internal readonly record struct PhysicsChainDynamicHeaderLayoutEntry(
    int RequestId,
    int TreeCount);
