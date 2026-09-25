using System.Collections.Concurrent;
using XREngine.Rendering;

namespace XREngine;

internal sealed class EngineRuntimeRenderObjectServices : IRuntimeRenderObjectServices
{
    public AbstractRenderAPIObject?[] CreateObjectsForAllOwners(GenericRenderObject renderObject)
    {
        XRWindow[] windows;
        lock (RuntimeEngine.Windows)
            windows = [.. RuntimeEngine.Windows];

        AbstractRenderAPIObject?[] wrappers = new AbstractRenderAPIObject?[windows.Length];
        for (int index = 0; index < windows.Length; index++)
            wrappers[index] = windows[index].Renderer.TryPublishAPIRenderObject(renderObject);
        return wrappers;
    }

    public ConcurrentDictionary<GenericRenderObject, AbstractRenderAPIObject> CreateObjectsForOwner(IRenderApiWrapperOwner owner)
    {
        if (owner is not AbstractRenderer renderer)
            return [];

        List<GenericRenderObject> renderObjects = [];
        lock (GenericRenderObject.RenderObjectCache)
        {
            foreach (var pair in GenericRenderObject.RenderObjectCache)
                foreach (GenericRenderObject renderObject in pair.Value)
                    if (renderObject.IsApiWrapperPublicationReady && !renderObject.PublishWrappersOnOwnerFirstUse)
                        renderObjects.Add(renderObject);
        }

        ConcurrentDictionary<GenericRenderObject, AbstractRenderAPIObject> wrappers = [];
        for (int index = 0; index < renderObjects.Count; index++)
        {
            GenericRenderObject renderObject = renderObjects[index];
            AbstractRenderAPIObject? wrapper = renderer.TryPublishAPIRenderObject(renderObject);
            if (wrapper is null)
                continue;

            wrappers.TryAdd(renderObject, wrapper);
            renderObject.AddWrapper(wrapper);
        }

        return wrappers;
    }

    public void DestroyObjectsForOwner(IRenderApiWrapperOwner owner)
    {
        if (owner is not AbstractRenderer)
            return;

        List<GenericRenderObject> renderObjects = [];
        lock (GenericRenderObject.RenderObjectCache)
        {
            foreach (var pair in GenericRenderObject.RenderObjectCache)
                foreach (GenericRenderObject renderObject in pair.Value)
                    renderObjects.Add(renderObject);
        }

        List<Exception>? failures = null;
        for (int index = 0; index < renderObjects.Count; index++)
        {
            GenericRenderObject renderObject = renderObjects[index];
            List<AbstractRenderAPIObject> wrappers =
            [
                .. renderObject.APIWrappers.Where(
                    owner.OwnsApiWrapper)
            ];

            foreach (AbstractRenderAPIObject wrapper in wrappers)
            {
                try
                {
                    wrapper.Retire();
                }
                catch (Exception ex)
                {
                    (failures ??= []).Add(ex);
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
