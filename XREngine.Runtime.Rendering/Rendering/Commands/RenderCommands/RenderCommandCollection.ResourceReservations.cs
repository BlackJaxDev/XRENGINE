namespace XREngine.Rendering.Commands;

public sealed partial class RenderCommandCollection
{
    // Fixed storage makes saturation a clean admission rejection, never an
    // unbounded allocation or a missing pending reader. Ordinary commands use none.
    private IRenderCommandCollectedResource?[] _updatingResources = new IRenderCommandCollectedResource?[128];
    private IRenderCommandCollectedResource?[] _renderingResources = new IRenderCommandCollectedResource?[128];
    private int _updatingResourceCount;
    private int _renderingResourceCount;

    private bool TryReserveCollectedResourceNoLock(RenderCommand command)
    {
        if (command is not IRenderCommandCollectedResource resource)
            return true;
        for (int i = 0; i < _updatingResourceCount; ++i)
            if (ReferenceEquals(_updatingResources[i], resource))
                return true;
        if (_updatingResourceCount == _updatingResources.Length || !resource.TryRetainCollectedResource())
            return false;
        _updatingResources[_updatingResourceCount++] = resource;
        return true;
    }

    private void SwapCollectedResourcesNoLock()
    {
        (_updatingResources, _renderingResources) = (_renderingResources, _updatingResources);
        (_updatingResourceCount, _renderingResourceCount) = (_renderingResourceCount, _updatingResourceCount);
        ReleaseCollectedResourcesNoLock(_updatingResources, ref _updatingResourceCount);
    }

    private void CancelCollectedResourcesNoLock()
    {
        ReleaseCollectedResourcesNoLock(_updatingResources, ref _updatingResourceCount);
        ReleaseCollectedResourcesNoLock(_renderingResources, ref _renderingResourceCount);
    }

    private static void ReleaseCollectedResourcesNoLock(IRenderCommandCollectedResource?[] resources, ref int count)
    {
        while (count != 0)
        {
            int index = --count;
            IRenderCommandCollectedResource resource = resources[index]!;
            resources[index] = null;
            resource.ReleaseCollectedResource();
        }
    }
}
