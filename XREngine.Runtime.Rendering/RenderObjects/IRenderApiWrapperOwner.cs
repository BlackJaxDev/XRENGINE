using System.Collections.Concurrent;

namespace XREngine.Rendering;

public interface IRenderApiWrapperOwner
{
    string RenderApiWrapperOwnerName { get; }
    AbstractRenderAPIObject? GetOrCreateAPIRenderObject(GenericRenderObject renderObject, bool generateNow = false);
}

public interface IRuntimeRenderObjectServices
{
    AbstractRenderAPIObject?[] CreateObjectsForAllOwners(GenericRenderObject renderObject);
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
