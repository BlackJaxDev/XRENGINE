using System.Threading;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;

namespace XREngine.Components.Scene.Mesh;

public partial class RenderableMesh
{
    private RenderInfo.DelAddRenderCommandsCallback? _primaryCollectionCallback;
    private bool _capturedPrimaryPassEligibility;
    private bool _sourceBasePassEnabled;
    private bool _sourceShadowPassEnabled;

    internal bool CoveredShadowPassEnabled => _sourceShadowPassEnabled;

    /// <summary>Refreshes view-independent draw inputs for a covered mesh.</summary>
    internal void RefreshCoveredCommandSource()
    {
        XRMeshRenderer? renderer = CurrentLODRenderer;
        XRMaterial? materialOverride = Volatile.Read(ref _materialOverride);
        XRMaterial? material = materialOverride ?? renderer?.Material;
        SeedInitialRenderState(renderer);
        _rc.Mesh = renderer;
        _rc.MaterialOverride = materialOverride;
        if (material is not null)
            _rc.RenderPass = material.RenderPass;
        SyncMaterialPassCommands(renderer, material, isShadowCollection: false);
        ApplyHighlightRenderOptionsOverride(material);
        ProcessPendingGpuMeshBvhRefresh();

        bool enabled = _rc.Enabled && RenderInfo.ShouldRender;
        bool baseEnabled = enabled && IsPrimaryMaterialPassEnabled(material, false);
        bool shadowEnabled = enabled && IsPrimaryMaterialPassEnabled(material, true);
        if (!_capturedPrimaryPassEligibility ||
            baseEnabled != _sourceBasePassEnabled || shadowEnabled != _sourceShadowPassEnabled)
        {
            _capturedPrimaryPassEligibility = true;
            _sourceBasePassEnabled = baseEnabled;
            _sourceShadowPassEnabled = shadowEnabled;
            _rc.MarkDirty();
        }
    }

    /// <summary>Publishes pending draw inputs before the scene database swaps.</summary>
    internal void PublishCoveredCommandSource()
    {
        if (_rc._dirty)
            _rc.SwapBuffers();
    }

    /// <summary>Gets whether the canonical source can replace main-view CPU collection.</summary>
    internal bool CanUseCanonicalGpuCollection(GPUScene scene)
    {
        XRMeshRenderer? renderer = CurrentLODRenderer;
        XRMaterial? material = MaterialOverride ?? renderer?.Material;
        if (!RenderInfo.SupportsCanonicalGpuCollection ||
            !UsesCommittedWorldBounds || Volatile.Read(ref _lodCount) != 1 ||
            renderer is null || renderer.Submeshes.Count != 0 ||
            !RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader ||
            !_initialRenderStateSeeded || HasPendingRenderMatrixUpdate || _rc._dirty ||
            _rc.ForceCpuRendering || _rc.Instances != 1 ||
            _rc.EditorHighlightBits != 0 || RenderBounds ||
            !ReferenceEquals(RenderInfo.PreCollectCommandsCallback, _primaryCollectionCallback) ||
            RenderInfo.HasCollectedForRenderCallback || RenderInfo.CullingIntersectionOverride is not null ||
            _rc.RequiresCpuCollectionCallbacks || material is null ||
            renderer.HasSettingUniformsHandlers ||
            material is AdvancedProjectiveMirrorMaterial || material.IsTransparentLike() ||
            material.RenderOptions?.ExcludeFromGpuIndirect == true ||
            _rc.RenderOptionsOverride?.ExcludeFromGpuIndirect == true ||
            material.HasSettingUniformsHandlers || material.HasSettingShadowUniformHandlers)
        {
            return false;
        }

        // Texture streaming needs per-view usage until its own GPU consumer is available.
        for (int index = 0; index < material.Textures.Count; ++index)
            if (material.Textures[index] is not null)
                return false;
        MaterialPassDefinition[] passes = material.PassSet.Passes;
        for (int index = 0; index < passes.Length; ++index)
            if (passes[index].Enabled && passes[index].Identity is not
                (EMaterialPassIdentity.Base or EMaterialPassIdentity.Shadow))
                return false;
        for (int index = 0; index < RenderInfo.RenderCommands.Count; ++index)
            if (!ReferenceEquals(RenderInfo.RenderCommands[index], _rc) &&
                RenderInfo.RenderCommands[index].Enabled)
                return false;

        return scene.TryGetCanonicalDraw(_rc, out _) &&
            !scene.TryGetCanonicalCompatibilityReason(_rc, 0, out _);
    }

    private void SeedInitialRenderState(XRMeshRenderer? renderer)
    {
        if (_initialRenderStateSeeded)
            return;
        _initialRenderStateSeeded = true;
        if (renderer?.Mesh?.HasSkinning == true && renderer.EnsureSkinningBuffers(logWarnings: false))
            renderer.RefreshBoneMatricesFromRenderState();
        QueueCurrentRenderMatrixUpdate();
    }
}
