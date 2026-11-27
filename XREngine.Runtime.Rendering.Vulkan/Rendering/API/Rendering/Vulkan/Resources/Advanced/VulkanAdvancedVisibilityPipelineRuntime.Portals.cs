using System;
using XREngine.Rendering;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanAdvancedVisibilityPipelineRuntime
{
    private const string PortalClipPlanePreamble = "#define XR_ADV_VISIBILITY_CLIP_PLANE 1\n";

    // Portal visibility programs compile lazily on first active portal frame so non-portal
    // frames pay zero compilation or memory overhead.
    private XRRenderProgram? _portalOpaqueRasterProgram;
    private XRRenderProgram? _portalMaskedRasterProgram;
    private XRRenderProgram? _portalOpaqueMeshRasterProgram;
    private XRRenderProgram? _portalMaskedMeshRasterProgram;
    private XRRenderProgram? _portalOpaqueMultiviewRasterProgram;
    private XRRenderProgram? _portalMaskedMultiviewRasterProgram;
    private XRRenderProgram? _portalOpaqueMultiviewMeshRasterProgram;
    private XRRenderProgram? _portalMaskedMultiviewMeshRasterProgram;
    private XRRenderProgram? _portalStencilCarveProgram;
    private XRRenderProgram? _portalStencilCarveMultiviewProgram;
    private XRRenderProgram? _portalDepthResetProgram;
    private XRRenderProgram? _portalDepthResetMultiviewProgram;
    private XRRenderProgram? _portalTransparentDepthRestoreProgram;
    private XRRenderProgram? _portalTransparentDepthRestoreMultiviewProgram;
    private XRRenderProgram? _portalTransparentDepthRestoreMsaaProgram;
    private XRRenderProgram? _portalTransparentDepthRestoreMsaaMultiviewProgram;
    private XRRenderProgram? _portalMsaaStencilRepopulateProgram;
    private XRRenderProgram? _portalMsaaStencilRepopulateMultiviewProgram;
    private XRRenderProgram? _portalSkyUnwindProgram;
    private XRRenderProgram? _portalSkyUnwindMultiviewProgram;
    private XRRenderProgram? _portalGlassCompositeProgram;
    private XRRenderProgram? _portalGlassCompositeMultiviewProgram;

    /// <summary>
    /// Lazily prepares and returns the portal visibility raster program with
    /// <c>XR_ADV_VISIBILITY_CLIP_PLANE</c> enabled for oblique portal-plane clipping.
    /// </summary>
    internal VulkanAdvancedVisibilityPipelineReadiness TryGetPortalRasterProgram(
        EAdvancedMaterialCoverageMode coverage,
        bool meshlet,
        bool multiview,
        out VkRenderProgram program,
        out string reason)
    {
        program = null!;
        VulkanAdvancedVisibilityPipelineReadiness readiness = GetReadiness(out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
            return readiness;
        if (multiview && meshlet &&
            !_resources.AdvancedVisibilityResources.SupportsMultiviewMeshRaster)
        {
            reason = "The Vulkan device did not enable multiview mesh shaders for stereo portal rasterization.";
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }
        if (coverage is not (EAdvancedMaterialCoverageMode.Opaque or EAdvancedMaterialCoverageMode.Masked))
        {
            reason = $"Portal visibility raster coverage '{coverage}' has no production program.";
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }

        try
        {
            ref XRRenderProgram? retainedProgram = ref GetPortalRasterProgramSlot(coverage, meshlet, multiview);
            bool opaque = coverage == EAdvancedMaterialCoverageMode.Opaque;
            string preamble = PortalClipPlanePreamble + (opaque ? IdentityOnlyRasterPreamble : string.Empty);
            retainedProgram ??= meshlet
                ? CreateMeshRasterProgram(
                    opaque
                        ? AdvancedVisibilityShaderLibrary.OpaqueFragment
                        : AdvancedVisibilityShaderLibrary.MaskedFragment,
                    opaque
                        ? (multiview ? "VulkanAdvancedPortalVisibilityMeshOpaqueMultiview" : "VulkanAdvancedPortalVisibilityMeshOpaque")
                        : (multiview ? "VulkanAdvancedPortalVisibilityMeshMaskedMultiview" : "VulkanAdvancedPortalVisibilityMeshMasked"),
                    multiview,
                    preamble)
                : CreateRasterProgram(
                    opaque
                        ? AdvancedVisibilityShaderLibrary.OpaqueFragment
                        : AdvancedVisibilityShaderLibrary.MaskedFragment,
                    opaque
                        ? (multiview ? "VulkanAdvancedPortalVisibilityOpaqueMultiview" : "VulkanAdvancedPortalVisibilityOpaque")
                        : (multiview ? "VulkanAdvancedPortalVisibilityMaskedMultiview" : "VulkanAdvancedPortalVisibilityMasked"),
                    multiview,
                    preamble);
            return TryPrepareProgram(
                retainedProgram,
                out program,
                out reason,
                "portal visibility raster program did not link a Vulkan pipeline layout");
        }
        catch (Exception exception)
        {
            reason = exception.Message;
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }
    }

    /// <summary>
    /// Lazily prepares and returns the portal quad stencil-carve program.
    /// </summary>
    internal VulkanAdvancedVisibilityPipelineReadiness TryGetPortalStencilCarveProgram(
        bool multiview,
        out VkRenderProgram program,
        out string reason)
    {
        program = null!;
        VulkanAdvancedVisibilityPipelineReadiness readiness = GetReadiness(out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
            return readiness;
        try
        {
            ref XRRenderProgram? retainedProgram = ref multiview
                ? ref _portalStencilCarveMultiviewProgram
                : ref _portalStencilCarveProgram;
            retainedProgram ??= CreateVertexFragmentProgram(
                AdvancedVisibilityShaderLibrary.PortalQuadVertex,
                AdvancedVisibilityShaderLibrary.PortalStencilCarveFragment,
                multiview
                    ? "VulkanAdvancedPortalStencilCarveMultiview"
                    : "VulkanAdvancedPortalStencilCarve",
                multiview);
            return TryPrepareProgram(
                retainedProgram,
                out program,
                out reason,
                "portal stencil carve program did not link a Vulkan pipeline layout");
        }
        catch (Exception exception)
        {
            reason = exception.Message;
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }
    }

    /// <summary>
    /// Lazily prepares and returns the portal depth and visibility reset program.
    /// </summary>
    internal VulkanAdvancedVisibilityPipelineReadiness TryGetPortalDepthResetProgram(
        bool multiview,
        out VkRenderProgram program,
        out string reason)
    {
        program = null!;
        VulkanAdvancedVisibilityPipelineReadiness readiness = GetReadiness(out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
            return readiness;
        try
        {
            ref XRRenderProgram? retainedProgram = ref multiview
                ? ref _portalDepthResetMultiviewProgram
                : ref _portalDepthResetProgram;
            retainedProgram ??= CreateVertexFragmentProgram(
                AdvancedVisibilityShaderLibrary.PortalQuadVertex,
                AdvancedVisibilityShaderLibrary.PortalDepthResetFragment,
                multiview
                    ? "VulkanAdvancedPortalDepthResetMultiview"
                    : "VulkanAdvancedPortalDepthReset",
                multiview);
            return TryPrepareProgram(
                retainedProgram,
                out program,
                out reason,
                "portal depth reset program did not link a Vulkan pipeline layout");
        }
        catch (Exception exception)
        {
            reason = exception.Message;
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }
    }

    /// <summary>
    /// Lazily prepares and returns the depth-only fragment program that restores primary
    /// visibility depth on uncovered transparent-mirror pixels after reflected rasterization.
    /// </summary>
    internal VulkanAdvancedVisibilityPipelineReadiness TryGetPortalTransparentDepthRestoreProgram(
        bool multiview,
        bool msaa,
        out VkRenderProgram program,
        out string reason)
    {
        program = null!;
        VulkanAdvancedVisibilityPipelineReadiness readiness = GetReadiness(out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
            return readiness;
        try
        {
            ref XRRenderProgram? retainedProgram = ref msaa
                ? ref (multiview
                    ? ref _portalTransparentDepthRestoreMsaaMultiviewProgram
                    : ref _portalTransparentDepthRestoreMsaaProgram)
                : ref (multiview
                    ? ref _portalTransparentDepthRestoreMultiviewProgram
                    : ref _portalTransparentDepthRestoreProgram);
            string preamble = msaa ? "#define XR_ADV_PORTAL_DEPTH_RESTORE_MSAA 1\n" : string.Empty;
            string name = msaa
                ? (multiview ? "VulkanAdvancedPortalTransparentDepthRestoreMsaaMultiview" : "VulkanAdvancedPortalTransparentDepthRestoreMsaa")
                : (multiview ? "VulkanAdvancedPortalTransparentDepthRestoreMultiview" : "VulkanAdvancedPortalTransparentDepthRestore");
            retainedProgram ??= CreateVertexFragmentProgram(
                AdvancedVisibilityShaderLibrary.PortalQuadVertex,
                AdvancedVisibilityShaderLibrary.PortalTransparentDepthRestoreFragment,
                name,
                multiview,
                preamble);
            return TryPrepareProgram(
                retainedProgram,
                out program,
                out reason,
                "portal transparent depth restore program did not link a Vulkan pipeline layout");
        }
        catch (Exception exception)
        {
            reason = exception.Message;
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }
    }

    /// <summary>
    /// Lazily prepares and returns the fullscreen MSAA post-resolve portal stencil
    /// repopulation program that writes single-sample portal node bits (<c>0x7C</c>).
    /// </summary>
    internal VulkanAdvancedVisibilityPipelineReadiness TryGetPortalMsaaStencilRepopulateProgram(
        bool multiview,
        out VkRenderProgram program,
        out string reason)
    {
        program = null!;
        VulkanAdvancedVisibilityPipelineReadiness readiness = GetReadiness(out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
            return readiness;
        try
        {
            ref XRRenderProgram? retainedProgram = ref multiview
                ? ref _portalMsaaStencilRepopulateMultiviewProgram
                : ref _portalMsaaStencilRepopulateProgram;
            if (retainedProgram is null)
            {
                XRShader vertexAsset = XRShader.EngineShader(
                    "Advanced/Visibility/ResolveAdvancedMsaaVisibility.vert",
                    EShaderType.Vertex);
                XRShader fragmentAsset = XRShader.EngineShader(
                    AdvancedVisibilityShaderLibrary.PortalMsaaStencilRepopulateFragment,
                    EShaderType.Fragment);
                string preamble = "#define XR_ADV_PORTAL_MSAA_STENCIL_REPOPULATE 1\n" +
                    VulkanAdvancedSceneProgramBindingContract.BuildShaderPreamble(
                        _resources.AdvancedSceneResources);
                if (multiview)
                    preamble = "#define XR_ADV_MULTIVIEW_RESOLVE 1\n" + preamble;
                string name = multiview
                    ? "VulkanAdvancedMsaaPortalStencilRepopulateMultiview"
                    : "VulkanAdvancedMsaaPortalStencilRepopulate";
                retainedProgram = new XRRenderProgram(
                    linkNow: false,
                    separable: false,
                    CreateShaderWithPreamble(vertexAsset, preamble, name + ".vert"),
                    CreateShaderWithPreamble(fragmentAsset, preamble, name + ".frag"))
                {
                    Name = name,
                    ExternallyOwnedDescriptorSetMask =
                        VulkanAdvancedSceneProgramBindingContract.ExternallyOwnedSetMask,
                };
            }
            return TryPrepareProgram(
                retainedProgram,
                out program,
                out reason,
                "portal MSAA stencil repopulation program did not link a Vulkan pipeline layout");
        }
        catch (Exception exception)
        {
            reason = exception.Message;
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }
    }

    /// <summary>
    /// Lazily prepares and returns the reflected sky unwind program rendered on uncovered
    /// far-depth portal pixels during <see cref="EAdvancedRenderStage.LatePasses"/>.
    /// </summary>
    internal VulkanAdvancedVisibilityPipelineReadiness TryGetPortalSkyUnwindProgram(
        bool multiview,
        out VkRenderProgram program,
        out string reason)
    {
        program = null!;
        VulkanAdvancedVisibilityPipelineReadiness readiness = GetReadiness(out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
            return readiness;
        try
        {
            ref XRRenderProgram? retainedProgram = ref multiview
                ? ref _portalSkyUnwindMultiviewProgram
                : ref _portalSkyUnwindProgram;
            retainedProgram ??= CreateVertexFragmentProgram(
                AdvancedVisibilityShaderLibrary.PortalQuadVertex,
                AdvancedVisibilityShaderLibrary.PortalSkyUnwindFragment,
                multiview
                    ? "VulkanAdvancedPortalSkyUnwindMultiview"
                    : "VulkanAdvancedPortalSkyUnwind",
                multiview);
            return TryPrepareProgram(
                retainedProgram,
                out program,
                out reason,
                "portal sky unwind program did not link a Vulkan pipeline layout");
        }
        catch (Exception exception)
        {
            reason = exception.Message;
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }
    }

    /// <summary>
    /// Lazily prepares and returns the mirror glass composite and stencil unwind program.
    /// </summary>
    internal VulkanAdvancedVisibilityPipelineReadiness TryGetPortalGlassCompositeProgram(
        bool multiview,
        out VkRenderProgram program,
        out string reason)
    {
        program = null!;
        VulkanAdvancedVisibilityPipelineReadiness readiness = GetReadiness(out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
            return readiness;
        try
        {
            ref XRRenderProgram? retainedProgram = ref multiview
                ? ref _portalGlassCompositeMultiviewProgram
                : ref _portalGlassCompositeProgram;
            retainedProgram ??= CreateVertexFragmentProgram(
                AdvancedVisibilityShaderLibrary.PortalGlassQuadVertex,
                AdvancedVisibilityShaderLibrary.PortalGlassCompositeFragment,
                multiview
                    ? "VulkanAdvancedPortalGlassCompositeMultiview"
                    : "VulkanAdvancedPortalGlassComposite",
                multiview);
            return TryPrepareProgram(
                retainedProgram,
                out program,
                out reason,
                "portal glass composite program did not link a Vulkan pipeline layout");
        }
        catch (Exception exception)
        {
            reason = exception.Message;
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }
    }

    private ref XRRenderProgram? GetPortalRasterProgramSlot(
        EAdvancedMaterialCoverageMode coverage,
        bool meshlet,
        bool multiview)
    {
        bool opaque = coverage == EAdvancedMaterialCoverageMode.Opaque;
        if (multiview)
        {
            if (meshlet)
                return ref opaque ? ref _portalOpaqueMultiviewMeshRasterProgram : ref _portalMaskedMultiviewMeshRasterProgram;
            return ref opaque ? ref _portalOpaqueMultiviewRasterProgram : ref _portalMaskedMultiviewRasterProgram;
        }

        if (meshlet)
            return ref opaque ? ref _portalOpaqueMeshRasterProgram : ref _portalMaskedMeshRasterProgram;
        return ref opaque ? ref _portalOpaqueRasterProgram : ref _portalMaskedRasterProgram;
    }
}
