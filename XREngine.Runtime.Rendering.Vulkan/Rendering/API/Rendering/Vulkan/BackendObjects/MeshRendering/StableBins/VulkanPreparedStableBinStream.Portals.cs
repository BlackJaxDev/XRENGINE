using System;
using XREngine.Rendering;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanPreparedStableBinStream
{
    private readonly VulkanVisibilityRasterPipeline[] _portalNormalPipelineScratch =
        new VulkanVisibilityRasterPipeline[RasterPipelineScratchCapacity];
    private readonly bool[] _portalNormalPipelineScratchValid =
        new bool[RasterPipelineScratchCapacity];
    private readonly VulkanVisibilityRasterPipeline[] _portalInvertedPipelineScratch =
        new VulkanVisibilityRasterPipeline[RasterPipelineScratchCapacity];
    private readonly bool[] _portalInvertedPipelineScratchValid =
        new bool[RasterPipelineScratchCapacity];

    /// <summary>
    /// Lazily prepares both normal-winding and inverted-winding portal visibility pipelines
    /// for every non-decal sealed visibility bin header. Never called on frames without active
    /// portal sub-views.
    /// </summary>
    internal VulkanAdvancedVisibilityPipelineReadiness TryPreparePortalRasterPipelines(
        VulkanAdvancedVisibilityPipelineRuntime visibilityPipelines,
        in VulkanAdvancedVisibilityTargetClosure target,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(visibilityPipelines);
        if (!_submissionPlansSealed || !target.IsValid || _retainedTemplateCount != _recordCount)
        {
            reason = "stable submission plans, target closure, or resident-template leases are unavailable for portal raster";
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }

        _portalNormalPipelineScratchValid.AsSpan().Clear();
        _portalInvertedPipelineScratchValid.AsSpan().Clear();
        bool multiview = target.DynamicRenderingFormats.ViewMask != 0u;
        for (int headerIndex = 0; headerIndex < _headerCount; ++headerIndex)
        {
            ref readonly VulkanPreparedStableBinHeader header = ref _headers[headerIndex];
            if (!header.IsRasterReady || header.IsSurfaceDecal)
                continue;

            bool reuseNormal = header.PortalNormalRasterPipeline.IsValid &&
                header.PortalNormalRasterPipeline.TargetClosure == target &&
                header.PortalNormalRasterPipeline.ProgramLinkGeneration ==
                    header.PortalNormalRasterPipeline.Program.LinkGeneration;
            bool reuseInverted = header.PortalInvertedRasterPipeline.IsValid &&
                header.PortalInvertedRasterPipeline.TargetClosure == target &&
                header.PortalInvertedRasterPipeline.ProgramLinkGeneration ==
                    header.PortalInvertedRasterPipeline.Program.LinkGeneration;
            if (reuseNormal && reuseInverted)
                continue;

            bool meshlet = header.RasterPipeline.IsMeshShaderPipeline;
            EAdvancedMaterialCoverageMode coverage = header.IndirectRange.Key.Coverage;
            uint cullMode = header.IndirectRange.Key.CullMode;
            int scratchIndex = ResolveRasterPipelineScratchIndex(coverage, meshlet, cullMode, fragmentDepth: false);

            VulkanVisibilityRasterPipeline normalPipeline;
            VulkanVisibilityRasterPipeline invertedPipeline;
            if (scratchIndex >= 0 &&
                _portalNormalPipelineScratchValid[scratchIndex] &&
                _portalInvertedPipelineScratchValid[scratchIndex])
            {
                normalPipeline = _portalNormalPipelineScratch[scratchIndex];
                invertedPipeline = _portalInvertedPipelineScratch[scratchIndex];
            }
            else
            {
                VulkanAdvancedVisibilityPipelineReadiness programReadiness =
                    visibilityPipelines.TryGetPortalRasterProgram(
                        coverage,
                        meshlet,
                        multiview,
                        out VkRenderProgram program,
                        out reason);
                if (programReadiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
                    return programReadiness;

                if (!VulkanCanonicalVisibilityPipelineFactory.TryPrepare(
                        program,
                        coverage,
                        meshlet,
                        cullMode,
                        in target,
                        out normalPipeline,
                        out reason,
                        EVulkanVisibilityPortalRasterMode.NormalWinding) ||
                    !VulkanCanonicalVisibilityPipelineFactory.TryPrepare(
                        program,
                        coverage,
                        meshlet,
                        cullMode,
                        in target,
                        out invertedPipeline,
                        out reason,
                        EVulkanVisibilityPortalRasterMode.InvertedWinding))
                {
                    return VulkanAdvancedVisibilityPipelineReadiness.Failed;
                }

                if (scratchIndex >= 0)
                {
                    _portalNormalPipelineScratch[scratchIndex] = normalPipeline;
                    _portalNormalPipelineScratchValid[scratchIndex] = true;
                    _portalInvertedPipelineScratch[scratchIndex] = invertedPipeline;
                    _portalInvertedPipelineScratchValid[scratchIndex] = true;
                }
            }

            _headers[headerIndex] = header with
            {
                PortalNormalRasterPipeline = normalPipeline,
                PortalInvertedRasterPipeline = invertedPipeline,
            };
        }

        reason = "Ready";
        return VulkanAdvancedVisibilityPipelineReadiness.Ready;
    }
}
