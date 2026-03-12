using System.Collections.Concurrent;
using XREngine.Rendering;

namespace XREngine;

internal sealed class EngineRuntimeRenderObjectServices : IRuntimeRenderObjectServices
{
    public AbstractRenderAPIObject?[] CreateObjectsForAllOwners(GenericRenderObject renderObject)
        => Engine.Rendering.CreateObjectsForAllWindows(renderObject);

    public ConcurrentDictionary<GenericRenderObject, AbstractRenderAPIObject> CreateObjectsForOwner(IRenderApiWrapperOwner owner)
        => owner is AbstractRenderer renderer
            ? Engine.Rendering.CreateObjectsForNewRenderer(renderer)
            : [];

    public void DestroyObjectsForOwner(IRenderApiWrapperOwner owner)
    {
        if (owner is AbstractRenderer renderer)
            Engine.Rendering.DestroyObjectsForRenderer(renderer);
    }

    public void IssueMemoryBarrier(EMemoryBarrierMask mask)
        => AbstractRenderer.Current?.MemoryBarrier(mask);

    public void LogOutput(string message)
        => Debug.Out(message);

    public void LogWarning(string message)
        => Debug.LogWarning(message);
}
