namespace XREngine.Rendering.Compute;

/// <summary>Resolves renderer-specific physics compute adapters in preference order.</summary>
internal static class PhysicsChainComputeBackendFactory
{
    public static bool TryCreate(AbstractRenderer? renderer, out IPhysicsChainComputeBackend? backend)
    {
        backend = null;
        return renderer is IRuntimeRendererHost rendererHost
            && rendererHost.TryGetBackendCapability<IPhysicsChainComputeBackendFactoryCapability>(out var factory)
            && factory is not null
            && factory.TryCreatePhysicsChainComputeBackend(out backend);
    }
}
