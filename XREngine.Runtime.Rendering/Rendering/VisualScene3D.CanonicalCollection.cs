using XREngine.Components.Scene.Mesh;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;

namespace XREngine.Scene;

public partial class VisualScene3D
{
    private readonly List<RenderableMesh> _coveredCommandSources = [];
    private readonly HashSet<RenderInfo3D> _canonicalGpuCollectionSet = [];
    private int _coveredShadowCasterLayerMask;
    private long _coveredShadowCasterOutputRevision;
    private long _lastCoveredShadowCasterOutputStamp;

    internal bool TryGetCoveredShadowCasterOutputRevision(uint cameraLayerMask, out ulong revision)
    {
        if ((unchecked((uint)System.Threading.Volatile.Read(ref _coveredShadowCasterLayerMask)) & cameraLayerMask) == 0u)
        {
            revision = 0u;
            return false;
        }

        revision = unchecked((ulong)System.Threading.Volatile.Read(ref _coveredShadowCasterOutputRevision));
        return true;
    }

    internal void NotifyCoveredShadowCasterOutputChanged(uint producerEpoch, bool published)
    {
        if (producerEpoch == 0u)
            return;

        long stamp = ((long)producerEpoch << 1) | (published ? 1L : 0L);
        long prior = System.Threading.Interlocked.Exchange(ref _lastCoveredShadowCasterOutputStamp, stamp);
        if (prior != stamp)
            System.Threading.Interlocked.Increment(ref _coveredShadowCasterOutputRevision);
    }

    private void RefreshCoveredCommandSources()
    {
        _coveredCommandSources.Clear();
        _canonicalGpuCollectionSet.Clear();
        uint casterLayerMask = 0u;
        bool casterPolicyChanged = false;
        foreach (RenderableMesh mesh in _skinnedMeshes)
        {
            mesh.ReconcileCommittedWorldBounds();
            bool covered = mesh.UsesCommittedWorldBounds;
            if (_isGpuDispatchActive)
            {
                RenderInfo3D info = mesh.RenderInfo;
                if (covered)
                {
                    if (_committedCpuTreeMembers.Add(info))
                        ActiveCpuRenderTree.Add(info);
                }
                else if (_committedCpuTreeMembers.Remove(info))
                    ActiveCpuRenderTree.Remove(info);
            }
            if (!covered)
            {
                casterPolicyChanged |= mesh.SetCoveredShadowCasterLayerMask(0u);
                continue;
            }
            mesh.RefreshCoveredCommandSource();
            _coveredCommandSources.Add(mesh);
            bool canonical = mesh.RenderInfo.SupportsCanonicalGpuCollection &&
                mesh.CanUseCanonicalGpuCollection(GPUCommands);
            if (canonical)
                _canonicalGpuCollectionSet.Add(mesh.RenderInfo);
            uint casterLayer = mesh.RenderInfo.CastsShadows &&
                mesh.CoveredShadowPassEnabled
                    ? 1u << mesh.RenderInfo.Layer
                    : 0u;
            casterPolicyChanged |= mesh.SetCoveredShadowCasterLayerMask(casterLayer);
            casterLayerMask |= casterLayer;
        }
        int previousLayerMask = System.Threading.Interlocked.Exchange(
            ref _coveredShadowCasterLayerMask, unchecked((int)casterLayerMask));
        casterPolicyChanged |= unchecked((uint)previousLayerMask) != casterLayerMask;
        if (casterPolicyChanged)
            System.Threading.Interlocked.Increment(ref _coveredShadowCasterOutputRevision);
    }

    private void PublishCoveredCommandSources()
    {
        for (int index = 0; index < _coveredCommandSources.Count; ++index)
            _coveredCommandSources[index].PublishCoveredCommandSource();
    }

    private bool CanCollectCoveredSourcesOnGpu(
        RenderCommandCollection commands,
        XRRenderPipelineInstance.RenderingState state)
        => !commands.IsShadowPass && !state.ShadowPass && !state.CapturePolicy.IsCapture &&
            !ModelRenderDiagnostics.HasActiveTrace && !RenderDiagnosticsFlags.SkinCullRejectDiag &&
            !RuntimeRenderingHostServices.FrameTiming.RenderCullingVolumesEnabled &&
            commands.UsesCanonicalGpuCollection(state.WindowViewport);

    private bool IsCollectedByCanonicalGpu(RenderInfo3D info, bool allowGpuCollection)
        => allowGpuCollection && _canonicalGpuCollectionSet.Contains(info) &&
            info.OwnerRenderableMesh?.CanUseCanonicalGpuCollection(GPUCommands) == true;
}
