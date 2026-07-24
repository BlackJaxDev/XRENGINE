using XREngine.Rendering;

namespace XREngine.Editor;

/// <summary>
/// Resolves optional tooling capabilities from the active renderer or an editor window.
/// </summary>
internal static class EditorRendererCapabilityResolver
{
    public static bool TryGet<TCapability>(out TCapability capability)
        where TCapability : class
    {
        if (AbstractRenderer.Current is TCapability current)
        {
            capability = current;
            return true;
        }

        foreach (XRWindow window in Engine.Windows)
        {
            if (window.Renderer is not TCapability candidate)
                continue;

            capability = candidate;
            return true;
        }

        capability = null!;
        return false;
    }

    public static IEnumerable<TCapability> Enumerate<TCapability>()
        where TCapability : class
    {
        foreach (XRWindow window in Engine.Windows)
            if (window.Renderer is TCapability capability)
                yield return capability;
    }

    public static bool TryGetForBackend<TCapability>(
        RendererBackendId backendId,
        out TCapability capability)
        where TCapability : class
    {
        if (AbstractRenderer.Current is { } current
            && current.BackendId == backendId
            && current is TCapability currentCapability)
        {
            capability = currentCapability;
            return true;
        }

        foreach (XRWindow window in Engine.Windows)
        {
            if (window.Renderer.BackendId != backendId
                || window.Renderer is not TCapability candidate)
                continue;

            capability = candidate;
            return true;
        }

        capability = null!;
        return false;
    }

    public static bool TryGetRegistered<TCapability>(
        RendererBackendId backendId,
        out TCapability capability)
        where TCapability : class
    {
        if (TryGetForBackend(backendId, out capability))
            return true;

        if (RuntimeRenderingHostServices.Factories.RendererBackends.TryGet(
                backendId,
                out RendererBackendRegistration registration)
            && registration.Factory is TCapability factoryCapability)
        {
            capability = factoryCapability;
            return true;
        }

        capability = null!;
        return false;
    }
}
