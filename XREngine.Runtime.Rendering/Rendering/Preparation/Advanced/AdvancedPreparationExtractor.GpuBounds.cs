using XREngine.Rendering.Commands;
using XREngine.Rendering.Compute;

namespace XREngine.Rendering;

public sealed partial class AdvancedPreparationExtractor
{
    private readonly AdvancedGpuBoundsPatchRoute[] _gpuBoundsRoutes;
    private PhysicsChainGpuOutputPageLease _gpuBoundsPage;
    // Reads covered renderer states of the retained page without the page lock.
    private PhysicsChainGpuRendererStateReader _gpuBoundsStates;
    private GPUPhysicsChainDispatcher? _gpuBoundsDispatcher;
    private bool _physicsOutputMismatch;

    internal bool HasGpuBoundsPublication => _gpuBoundsDispatcher is not null;

    private bool TryCaptureGpuBoundsPublication(AdvancedGpuScenePublicationSnapshot publication, int drawCount)
    {
        for (int index = 0; index < drawCount; ++index)
        {
            if (publication.Submission.DeformationSources[index].GpuBoundsSource is not { } source)
                continue;
            if (_gpuBoundsDispatcher is not null)
            {
                if (!ReferenceEquals(_gpuBoundsDispatcher, source.Dispatcher))
                    return false;
                continue;
            }
            if (!source.Dispatcher.TryAcquirePublishedOutputPage(out _gpuBoundsPage))
                return false;
            _gpuBoundsDispatcher = source.Dispatcher;
            if (!_gpuBoundsDispatcher.TryOpenRendererStateReader(_gpuBoundsPage.Token, out _gpuBoundsStates))
                return false;
        }
        return true;
    }

    internal bool TryCopyGpuBoundsPublication(
        Span<AdvancedGpuBoundsPatchRoute> routes, out PhysicsChainGpuOutputPageLease page)
    {
        page = default;
        if (routes.Length != _drawCount)
            return false;
        if (_gpuBoundsDispatcher is not null &&
            !_gpuBoundsDispatcher.TryRetainOutputPage(_gpuBoundsPage.Token, out page))
            return false;
        _gpuBoundsRoutes.AsSpan(0, _drawCount).CopyTo(routes);
        return true;
    }

    private void ReleaseGpuBoundsPublication()
    {
        _gpuBoundsDispatcher?.ReleaseOutputPage(_gpuBoundsPage.Token);
        _gpuBoundsDispatcher = null;
        _gpuBoundsPage = default;
        _gpuBoundsStates = default;
    }
}
