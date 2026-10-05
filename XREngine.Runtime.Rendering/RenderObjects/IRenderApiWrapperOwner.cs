using System.Collections.Concurrent;

namespace XREngine.Rendering;

public interface IRenderApiWrapperOwner
{
    string RenderApiWrapperOwnerName { get; }

    /// <summary>
    /// Gets the generation-local identity stored on wrappers created through this
    /// authority. Most owners are their own identity; renderer facades may delegate
    /// wrapper ownership to an exact backend-generation context.
    /// </summary>
    IRenderApiWrapperOwner ApiWrapperIdentityOwner => this;

    /// <summary>Returns whether <paramref name="wrapper"/> belongs to this exact owner generation.</summary>
    bool OwnsApiWrapper(AbstractRenderAPIObject wrapper)
        => ReferenceEquals(wrapper.Owner, ApiWrapperIdentityOwner);

    AbstractRenderAPIObject? GetOrCreateAPIRenderObject(GenericRenderObject renderObject, bool generateNow = false);

    /// <summary>
    /// Drop any cached API wrapper for <paramref name="renderObject"/> from this owner's render-object cache.
    /// Called when the generic object is being destroyed so the wrapper does not linger in panel queries
    /// or hold a back-pointer after the GL/Vulkan handle is gone.
    /// </summary>
    void RemoveAPIRenderObject(GenericRenderObject renderObject)
    {
    }
}

public interface IRuntimeRenderObjectServices
{
    AbstractRenderAPIObject?[] CreateObjectsForAllOwners(GenericRenderObject renderObject);

    /// <summary>
    /// Evicts a destroyed render object from every renderer cache that
    /// <see cref="CreateObjectsForAllOwners"/> may have populated. A wrapper's identity owner
    /// can be a backend-generation context rather than the renderer that cached it, so the
    /// identity owner's own removal does not reach the renderer cache.
    /// </summary>
    void RemoveObjectFromAllOwners(GenericRenderObject renderObject)
    {
    }

    ConcurrentDictionary<GenericRenderObject, AbstractRenderAPIObject> CreateObjectsForOwner(IRenderApiWrapperOwner owner);
    void DestroyObjectsForOwner(IRenderApiWrapperOwner owner);
    void IssueMemoryBarrier(EMemoryBarrierMask mask);
    void LogOutput(string message);
    void LogWarning(string message);
}

public static class RuntimeRenderObjectServices
{
    public static IRuntimeRenderObjectServices? Current { get; set; }
}
