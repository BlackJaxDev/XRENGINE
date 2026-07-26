using System.Collections.Concurrent;
using XREngine.Rendering;

namespace XREngine;

internal sealed class EngineRuntimeRenderObjectServices : IRuntimeRenderObjectServices
{
    public AbstractRenderAPIObject?[] CreateObjectsForAllOwners(GenericRenderObject renderObject)
    {
        lock (RuntimeEngine.Windows)
            return [.. RuntimeEngine.Windows.Select(window => window.Renderer.GetOrCreateAPIRenderObject(renderObject))];
    }

    public ConcurrentDictionary<GenericRenderObject, AbstractRenderAPIObject> CreateObjectsForOwner(IRenderApiWrapperOwner owner)
    {
        if (owner is not AbstractRenderer renderer)
            return [];

        ConcurrentDictionary<GenericRenderObject, AbstractRenderAPIObject> wrappers = [];
        lock (GenericRenderObject.RenderObjectCache)
        {
            foreach (var pair in GenericRenderObject.RenderObjectCache)
            {
                foreach (GenericRenderObject renderObject in pair.Value)
                {
                    AbstractRenderAPIObject? wrapper = renderer.GetOrCreateAPIRenderObject(renderObject);
                    if (wrapper is null)
                        continue;

                    wrappers.TryAdd(renderObject, wrapper);
                    renderObject.AddWrapper(wrapper);
                }
            }
        }

        return wrappers;
    }

    public void DestroyObjectsForOwner(IRenderApiWrapperOwner owner)
    {
        if (owner is not AbstractRenderer renderer)
            return;

        List<Exception>? failures = null;
        lock (GenericRenderObject.RenderObjectCache)
        {
            foreach (var pair in GenericRenderObject.RenderObjectCache)
            {
                foreach (GenericRenderObject renderObject in pair.Value)
                {
                    List<AbstractRenderAPIObject> wrappers =
                    [
                        .. renderObject.APIWrappers.Where(
                            wrapper => ReferenceEquals(wrapper.Owner, renderer))
                    ];

                    foreach (AbstractRenderAPIObject wrapper in wrappers)
                    {
                        try
                        {
                            wrapper.Destroy();
                        }
                        catch (Exception ex)
                        {
                            (failures ??= []).Add(ex);
                        }

                        renderObject.RemoveWrapper(wrapper);
                    }
                }
            }
        }

        if (failures is not null)
        {
            throw new AggregateException(
                $"Failed to destroy {failures.Count} renderer API wrapper(s) for '{owner.RenderApiWrapperOwnerName}'.",
                failures);
        }
    }

    public void IssueMemoryBarrier(EMemoryBarrierMask mask)
        => AbstractRenderer.Current?.MemoryBarrier(mask);

    public void LogOutput(string message)
        => Debug.Out(message);

    public void LogWarning(string message)
        => Debug.LogWarning(message);
}
