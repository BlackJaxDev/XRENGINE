namespace XREngine.Rendering.Compute;

/// <summary>Resolves renderer-specific physics compute adapters in preference order.</summary>
internal static class PhysicsChainComputeBackendFactory
{
    public static bool TryCreate(AbstractRenderer? renderer, out IPhysicsChainComputeBackend? backend)
        => OpenGLPhysicsChainComputeBackend.TryCreate(renderer, out backend)
        || VulkanPhysicsChainComputeBackend.TryCreate(renderer, out backend);
}
