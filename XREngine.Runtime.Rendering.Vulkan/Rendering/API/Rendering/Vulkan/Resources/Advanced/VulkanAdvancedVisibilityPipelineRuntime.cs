using System;
using XREngine.Core.Files;
using XREngine.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns the source-level Vulkan realization of the executable visibility
/// preparation and visibility-raster lane. Raster pipeline creation remains
/// gated on an exact render-graph attachment closure.
/// </summary>
internal sealed class VulkanAdvancedVisibilityPipelineRuntime
{
    private readonly VulkanResourceRuntime _resources;
    private XRRenderProgram? _earlyVisibilityProgram;
    private XRRenderProgram? _buildIndirectProgram;
    private XRRenderProgram? _buildDepthPyramidProgram;
    private XRRenderProgram? _lateVisibilityProgram;
    private XRRenderProgram? _opaqueRasterProgram;
    private XRRenderProgram? _maskedRasterProgram;
    private XRRenderProgram? _opaqueMeshRasterProgram;
    private XRRenderProgram? _maskedMeshRasterProgram;

    internal VulkanAdvancedVisibilityPipelineRuntime(VulkanResourceRuntime resources)
        => _resources = resources;

    internal VulkanAdvancedVisibilityPipelineReadiness TryGetComputePipelines(
        out VkRenderProgram earlyVisibility,
        out VkRenderProgram buildIndirect,
        out string reason)
    {
        earlyVisibility = null!;
        buildIndirect = null!;
        reason = "Ready";
        VulkanAdvancedSceneResourceRuntime scene = _resources.AdvancedSceneResources;
        if (!scene.IsReady)
        {
            reason = scene.AvailabilityReason;
            return VulkanAdvancedVisibilityPipelineReadiness.Missing;
        }

        try
        {
            _earlyVisibilityProgram ??= CreateComputeProgram(
                AdvancedVisibilityShaderLibrary.EarlyVisibilityCompute,
                "VulkanAdvancedEarlyVisibility");
            _buildIndirectProgram ??= CreateComputeProgram(
                AdvancedVisibilityShaderLibrary.BuildIndirectCompute,
                "VulkanAdvancedBuildVisibilityIndirect");

            if (_resources.WrapperLookup.GetOrCreate(
                    _earlyVisibilityProgram,
                    generateNow: true) is not VkRenderProgram early ||
                !early.Link(allowAsyncShaderCompile: false) ||
                !early.IsLinked || early.PipelineLayout.Handle == 0)
            {
                reason = DescribeProgramFailure(
                    _earlyVisibilityProgram,
                    "early visibility compute program did not link a Vulkan pipeline layout");
                return VulkanAdvancedVisibilityPipelineReadiness.Failed;
            }
            if (_resources.WrapperLookup.GetOrCreate(
                    _buildIndirectProgram,
                    generateNow: true) is not VkRenderProgram indirect ||
                !indirect.Link(allowAsyncShaderCompile: false) ||
                !indirect.IsLinked || indirect.PipelineLayout.Handle == 0)
            {
                reason = "visibility indirect compute program did not link a Vulkan pipeline layout";
                return VulkanAdvancedVisibilityPipelineReadiness.Failed;
            }

            VulkanComputePipelineReadiness earlyReadiness = early.TryGetOrRequestComputePipeline(
                int.MinValue, null, out _, out string earlyReason);
            if (earlyReadiness != VulkanComputePipelineReadiness.Ready)
                return DescribeComputePipelineReadiness(earlyReadiness, "early visibility", earlyReason, out reason);

            VulkanComputePipelineReadiness indirectReadiness = indirect.TryGetOrRequestComputePipeline(
                int.MinValue, null, out _, out string indirectReason);
            if (indirectReadiness != VulkanComputePipelineReadiness.Ready)
                return DescribeComputePipelineReadiness(indirectReadiness, "visibility indirect", indirectReason, out reason);

            earlyVisibility = early;
            buildIndirect = indirect;
            return VulkanAdvancedVisibilityPipelineReadiness.Ready;
        }
        catch (Exception exception)
        {
            reason = exception.Message;
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }
    }

    /// <summary>
    /// Resolves the compute half of the second visibility phase.  This is kept
    /// separate from the early producer closure so a device cannot advertise
    /// the full shader family merely because the early pair linked.
    /// </summary>
    internal VulkanAdvancedVisibilityPipelineReadiness TryGetLateVisibilityComputePipelines(
        out VkRenderProgram buildDepthPyramid,
        out VkRenderProgram lateVisibility,
        out string reason)
    {
        buildDepthPyramid = null!;
        lateVisibility = null!;
        reason = "Ready";
        VulkanAdvancedSceneResourceRuntime scene = _resources.AdvancedSceneResources;
        if (!scene.IsReady || !_resources.AdvancedVisibilityResources.IsReady)
        {
            reason = !scene.IsReady
                ? scene.AvailabilityReason
                : _resources.AdvancedVisibilityResources.AvailabilityReason;
            return VulkanAdvancedVisibilityPipelineReadiness.Missing;
        }

        try
        {
            _buildDepthPyramidProgram ??= CreateComputeProgram(
                AdvancedVisibilityShaderLibrary.DepthPyramidCompute,
                "VulkanAdvancedBuildDepthPyramid");
            _lateVisibilityProgram ??= CreateComputeProgram(
                AdvancedVisibilityShaderLibrary.LateVisibilityCompute,
                "VulkanAdvancedLateVisibility");

            if (_resources.WrapperLookup.GetOrCreate(
                    _buildDepthPyramidProgram,
                    generateNow: true) is not VkRenderProgram depth ||
                !depth.Link(allowAsyncShaderCompile: false) || !depth.IsLinked ||
                depth.PipelineLayout.Handle == 0)
            {
                reason = "depth-pyramid compute program did not link a Vulkan pipeline layout";
                return VulkanAdvancedVisibilityPipelineReadiness.Failed;
            }
            if (_resources.WrapperLookup.GetOrCreate(
                    _lateVisibilityProgram,
                    generateNow: true) is not VkRenderProgram late ||
                !late.Link(allowAsyncShaderCompile: false) || !late.IsLinked ||
                late.PipelineLayout.Handle == 0)
            {
                reason = "late-visibility compute program did not link a Vulkan pipeline layout";
                return VulkanAdvancedVisibilityPipelineReadiness.Failed;
            }
            VulkanComputePipelineReadiness depthReadiness = depth.TryGetOrRequestComputePipeline(
                int.MinValue, null, out _, out string depthReason);
            if (depthReadiness != VulkanComputePipelineReadiness.Ready)
                return DescribeComputePipelineReadiness(depthReadiness, "depth pyramid", depthReason, out reason);

            VulkanComputePipelineReadiness lateReadiness = late.TryGetOrRequestComputePipeline(
                int.MinValue, null, out _, out string lateReason);
            if (lateReadiness != VulkanComputePipelineReadiness.Ready)
                return DescribeComputePipelineReadiness(lateReadiness, "late visibility", lateReason, out reason);

            buildDepthPyramid = depth;
            lateVisibility = late;
            return VulkanAdvancedVisibilityPipelineReadiness.Ready;
        }
        catch (Exception exception)
        {
            reason = exception.Message;
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }
    }

    /// <summary>
    /// Links the visibility-only vertex/fragment family. The programs have no
    /// set-0 dependency; sets 1-3 are supplied by the advanced runtimes. They
    /// are never substituted for an ordinary material program on a resident
    /// template.
    /// </summary>
    internal bool TryGetRasterProgram(
        EAdvancedMaterialCoverageMode coverage,
        bool meshlet,
        out VkRenderProgram program,
        out string reason)
    {
        program = null!;
        reason = "Ready";
        if (!_resources.AdvancedSceneResources.IsReady ||
            !_resources.AdvancedVisibilityResources.IsReady)
        {
            reason = !_resources.AdvancedSceneResources.IsReady
                ? _resources.AdvancedSceneResources.AvailabilityReason
                : _resources.AdvancedVisibilityResources.AvailabilityReason;
            return false;
        }
        if (coverage is not (
                EAdvancedMaterialCoverageMode.Opaque or
                EAdvancedMaterialCoverageMode.Masked))
        {
            reason = $"Visibility raster coverage '{coverage}' has no production program.";
            return false;
        }

        try
        {
            ref XRRenderProgram? retainedProgram = ref GetRasterProgramSlot(
                coverage,
                meshlet);
            retainedProgram ??= meshlet
                ? CreateMeshRasterProgram(
                    coverage == EAdvancedMaterialCoverageMode.Opaque
                        ? AdvancedVisibilityShaderLibrary.OpaqueFragment
                        : AdvancedVisibilityShaderLibrary.MaskedFragment,
                    coverage == EAdvancedMaterialCoverageMode.Opaque
                        ? "VulkanAdvancedVisibilityMeshOpaque"
                        : "VulkanAdvancedVisibilityMeshMasked")
                : CreateRasterProgram(
                coverage == EAdvancedMaterialCoverageMode.Opaque
                    ? AdvancedVisibilityShaderLibrary.OpaqueFragment
                    : AdvancedVisibilityShaderLibrary.MaskedFragment,
                coverage == EAdvancedMaterialCoverageMode.Opaque
                    ? "VulkanAdvancedVisibilityOpaque"
                    : "VulkanAdvancedVisibilityMasked");
            if (_resources.WrapperLookup.GetOrCreate(
                    retainedProgram,
                    generateNow: true) is not VkRenderProgram raster ||
                !raster.Link(allowAsyncShaderCompile: false) ||
                !raster.IsLinked || raster.PipelineLayout.Handle == 0)
            {
                reason = "visibility raster program did not link a Vulkan pipeline layout";
                return false;
            }

            program = raster;
            return true;
        }
        catch (Exception exception)
        {
            reason = exception.Message;
            return false;
        }
    }

    private ref XRRenderProgram? GetRasterProgramSlot(
        EAdvancedMaterialCoverageMode coverage,
        bool meshlet)
    {
        if (meshlet)
            return ref coverage == EAdvancedMaterialCoverageMode.Opaque
                ? ref _opaqueMeshRasterProgram
                : ref _maskedMeshRasterProgram;
        return ref coverage == EAdvancedMaterialCoverageMode.Opaque
            ? ref _opaqueRasterProgram
            : ref _maskedRasterProgram;
    }

    private XRRenderProgram CreateComputeProgram(string assetPath, string name)
    {
        XRShader asset = XRShader.EngineShader(assetPath, EShaderType.Compute);
        string source = asset.Source.Text ?? throw new InvalidOperationException(
            $"Advanced visibility shader asset '{assetPath}' did not provide source text.");
        string preamble = VulkanAdvancedSceneProgramBindingContract.BuildShaderPreamble(
            _resources.AdvancedSceneResources);
        TextFile sourceWithPreamble = new(asset.Source.FilePath ?? assetPath)
        {
            // Preserve the original asset path: relative advanced includes are
            // resolved from it after the Vulkan preamble is inserted.
            Text = InsertPreambleAfterVersion(source, preamble),
        };
        XRRenderProgram program = new(
            linkNow: false,
            separable: false,
            new XRShader(EShaderType.Compute, sourceWithPreamble)
            {
                Name = name + ".comp",
            })
        {
            Name = name,
            // Advanced visibility prepares compute pipelines asynchronously before
            // admission. This explicit program intent lets VkRenderProgram enqueue
            // that preparation when ordinary background compilation is disabled.
            AllowAsyncBackendCompile = true,
            ExternallyOwnedDescriptorSetMask =
                VulkanAdvancedSceneProgramBindingContract.ExternallyOwnedSetMask,
        };
        program.AllowLink();
        return program;
    }

    private XRRenderProgram CreateRasterProgram(string fragmentPath, string name)
    {
        XRShader vertexAsset = XRShader.EngineShader(
            AdvancedVisibilityShaderLibrary.Vertex,
            EShaderType.Vertex);
        XRShader fragmentAsset = XRShader.EngineShader(
            fragmentPath,
            EShaderType.Fragment);
        string preamble = VulkanAdvancedSceneProgramBindingContract.BuildShaderPreamble(
            _resources.AdvancedSceneResources);
        XRRenderProgram program = new(
            linkNow: false,
            separable: false,
            CreateShaderWithPreamble(vertexAsset, preamble, name + ".vert"),
            CreateShaderWithPreamble(fragmentAsset, preamble, name + ".frag"))
        {
            Name = name,
            ExternallyOwnedDescriptorSetMask =
                VulkanAdvancedSceneProgramBindingContract.ExternallyOwnedSetMask,
        };
        program.AllowLink();
        return program;
    }

    private XRRenderProgram CreateMeshRasterProgram(string fragmentPath, string name)
    {
        XRShader meshAsset = XRShader.EngineShader(
            AdvancedVisibilityShaderLibrary.Mesh,
            EShaderType.Mesh);
        XRShader fragmentAsset = XRShader.EngineShader(
            fragmentPath,
            EShaderType.Fragment);
        string preamble = VulkanAdvancedSceneProgramBindingContract.BuildShaderPreamble(
            _resources.AdvancedSceneResources);
        XRRenderProgram program = new(
            linkNow: false,
            separable: false,
            CreateShaderWithPreamble(meshAsset, preamble, name + ".mesh"),
            CreateShaderWithPreamble(fragmentAsset, preamble, name + ".frag"))
        {
            Name = name,
            ExternallyOwnedDescriptorSetMask =
                VulkanAdvancedSceneProgramBindingContract.ExternallyOwnedSetMask,
        };
        program.AllowLink();
        return program;
    }

    private static XRShader CreateShaderWithPreamble(
        XRShader asset,
        string preamble,
        string name)
    {
        string source = asset.Source.Text ?? throw new InvalidOperationException(
            $"Advanced visibility shader asset '{asset.Source.FilePath}' did not provide source text.");
        TextFile sourceWithPreamble = new(asset.Source.FilePath ?? name)
        {
            Text = InsertPreambleAfterVersion(source, preamble),
        };
        return new XRShader(asset.Type, sourceWithPreamble) { Name = name };
    }

    private static string InsertPreambleAfterVersion(string source, string preamble)
    {
        int versionEnd = source.IndexOf('\n');
        if (versionEnd < 0 || !source.AsSpan(0, versionEnd).Trim().StartsWith("#version", StringComparison.Ordinal))
            throw new InvalidOperationException("Advanced Vulkan shader source must begin with #version.");

        return string.Concat(
            source.AsSpan(0, versionEnd + 1),
            preamble,
            source.AsSpan(versionEnd + 1));
    }

    private static string DescribeProgramFailure(
        XRRenderProgram program,
        string fallback)
    {
        XRRenderProgram.ShaderProgramBackendStatus status =
            program.ShaderMetadata.Backend;
        if (string.IsNullOrWhiteSpace(status.FailureReason) &&
            string.IsNullOrWhiteSpace(status.Detail))
        {
            return fallback;
        }

        return $"{fallback}: {status.FailureReason ?? "no backend failure reason"} ({status.Detail ?? "no backend detail"})";
    }

    private static VulkanAdvancedVisibilityPipelineReadiness DescribeComputePipelineReadiness(
        VulkanComputePipelineReadiness readiness,
        string pipelineName,
        string detail,
        out string reason)
    {
        reason = $"{pipelineName} compute pipeline: {detail}";
        return readiness == VulkanComputePipelineReadiness.Pending
            ? VulkanAdvancedVisibilityPipelineReadiness.Pending
            : VulkanAdvancedVisibilityPipelineReadiness.Failed;
    }
}
