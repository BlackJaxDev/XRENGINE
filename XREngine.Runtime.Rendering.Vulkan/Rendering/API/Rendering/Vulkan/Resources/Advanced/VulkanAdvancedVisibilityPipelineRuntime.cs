using System;
using XREngine.Core.Files;
using XREngine.Rendering;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns the source-level Vulkan realization of the executable visibility
/// preparation and visibility-raster lane. Raster pipeline creation remains
/// gated on an exact render-graph attachment closure.
/// </summary>
internal sealed partial class VulkanAdvancedVisibilityPipelineRuntime
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
    private XRRenderProgram? _opaqueMultiviewRasterProgram;
    private XRRenderProgram? _maskedMultiviewRasterProgram;
    private XRRenderProgram? _opaqueMultiviewMeshRasterProgram;
    private XRRenderProgram? _maskedMultiviewMeshRasterProgram;
    private readonly List<GeneratedShaderSource> _generatedShaderSources = [];

    internal VulkanAdvancedVisibilityPipelineRuntime(VulkanResourceRuntime resources)
        => _resources = resources;

    internal VulkanAdvancedVisibilityPipelineReadiness TryGetComputePipelines(
        out VkRenderProgram earlyVisibility,
        out VkRenderProgram buildIndirect,
        out string reason)
    {
        earlyVisibility = null!;
        buildIndirect = null!;
        VulkanAdvancedVisibilityPipelineReadiness readiness = GetReadiness(out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
            return readiness;
        if (_resources.WrapperLookup.GetOrCreate(_earlyVisibilityProgram!, generateNow: false) is not VkRenderProgram early ||
            _resources.WrapperLookup.GetOrCreate(_buildIndirectProgram!, generateNow: false) is not VkRenderProgram indirect)
        {
            reason = "Prepared visibility compute wrappers are unavailable.";
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }
        earlyVisibility = early;
        buildIndirect = indirect;
        return VulkanAdvancedVisibilityPipelineReadiness.Ready;
    }

    private VulkanAdvancedVisibilityPipelineReadiness PrepareComputePipelines(
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

            VulkanAdvancedVisibilityPipelineReadiness linkReadiness = TryPrepareProgram(
                _earlyVisibilityProgram,
                out VkRenderProgram early,
                out reason,
                "early visibility compute program did not link a Vulkan pipeline layout");
            if (linkReadiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
                return linkReadiness;
            linkReadiness = TryPrepareProgram(
                _buildIndirectProgram,
                out VkRenderProgram indirect,
                out reason,
                "visibility indirect compute program did not link a Vulkan pipeline layout");
            if (linkReadiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
                return linkReadiness;

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
        VulkanAdvancedVisibilityPipelineReadiness readiness = GetReadiness(out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
            return readiness;
        if (_resources.WrapperLookup.GetOrCreate(_buildDepthPyramidProgram!, generateNow: false) is not VkRenderProgram depth ||
            _resources.WrapperLookup.GetOrCreate(_lateVisibilityProgram!, generateNow: false) is not VkRenderProgram late)
        {
            reason = "Prepared late-visibility compute wrappers are unavailable.";
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }
        buildDepthPyramid = depth;
        lateVisibility = late;
        return VulkanAdvancedVisibilityPipelineReadiness.Ready;
    }

    private VulkanAdvancedVisibilityPipelineReadiness PrepareLateVisibilityComputePipelines(
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

            VulkanAdvancedVisibilityPipelineReadiness linkReadiness = TryPrepareProgram(
                _buildDepthPyramidProgram,
                out VkRenderProgram depth,
                out reason,
                "depth-pyramid compute program did not link a Vulkan pipeline layout");
            if (linkReadiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
                return linkReadiness;
            linkReadiness = TryPrepareProgram(
                _lateVisibilityProgram,
                out VkRenderProgram late,
                out reason,
                "late-visibility compute program did not link a Vulkan pipeline layout");
            if (linkReadiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
                return linkReadiness;
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
    internal VulkanAdvancedVisibilityPipelineReadiness TryGetRasterProgram(
        EAdvancedMaterialCoverageMode coverage,
        bool meshlet,
        out VkRenderProgram program,
        out string reason,
        bool multiview = false)
    {
        program = null!;
        VulkanAdvancedVisibilityPipelineReadiness readiness = GetReadiness(out reason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
            return readiness;
        XRRenderProgram? source = GetRasterProgramSlot(coverage, meshlet, multiview);
        if (source is null ||
            _resources.WrapperLookup.GetOrCreate(source, generateNow: false) is not VkRenderProgram raster)
        {
            reason = "Prepared visibility raster wrapper is unavailable.";
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }
        program = raster;
        return VulkanAdvancedVisibilityPipelineReadiness.Ready;
    }

    private VulkanAdvancedVisibilityPipelineReadiness PrepareRasterProgram(
        EAdvancedMaterialCoverageMode coverage,
        bool meshlet,
        out VkRenderProgram program,
        out string reason,
        bool multiview = false)
    {
        program = null!;
        reason = "Ready";
        if (multiview && meshlet &&
            !_resources.AdvancedVisibilityResources.SupportsMultiviewMeshRaster)
        {
            reason = "The Vulkan device did not enable multiview mesh shaders for this stereo mesh submission.";
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }
        if (!_resources.AdvancedSceneResources.IsReady ||
            !_resources.AdvancedVisibilityResources.IsReady)
        {
            reason = !_resources.AdvancedSceneResources.IsReady
                ? _resources.AdvancedSceneResources.AvailabilityReason
                : _resources.AdvancedVisibilityResources.AvailabilityReason;
            return VulkanAdvancedVisibilityPipelineReadiness.Missing;
        }
        if (coverage is not (
                EAdvancedMaterialCoverageMode.Opaque or
                EAdvancedMaterialCoverageMode.Masked))
        {
            reason = $"Visibility raster coverage '{coverage}' has no production program.";
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }

        try
        {
            ref XRRenderProgram? retainedProgram = ref GetRasterProgramSlot(
                coverage,
                meshlet,
                multiview);
            retainedProgram ??= meshlet
                ? CreateMeshRasterProgram(
                    coverage == EAdvancedMaterialCoverageMode.Opaque
                        ? AdvancedVisibilityShaderLibrary.OpaqueFragment
                        : AdvancedVisibilityShaderLibrary.MaskedFragment,
                    coverage == EAdvancedMaterialCoverageMode.Opaque
                        ? "VulkanAdvancedVisibilityMeshOpaque"
                        : "VulkanAdvancedVisibilityMeshMasked",
                    multiview)
                : CreateRasterProgram(
                coverage == EAdvancedMaterialCoverageMode.Opaque
                    ? AdvancedVisibilityShaderLibrary.OpaqueFragment
                    : AdvancedVisibilityShaderLibrary.MaskedFragment,
                coverage == EAdvancedMaterialCoverageMode.Opaque
                    ? "VulkanAdvancedVisibilityOpaque"
                    : "VulkanAdvancedVisibilityMasked",
                multiview);
            VulkanAdvancedVisibilityPipelineReadiness linkReadiness = TryPrepareProgram(
                retainedProgram,
                out VkRenderProgram raster,
                out reason,
                "visibility raster program did not link a Vulkan pipeline layout");
            if (linkReadiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
                return linkReadiness;

            program = raster;
            return VulkanAdvancedVisibilityPipelineReadiness.Ready;
        }
        catch (Exception exception)
        {
            reason = exception.Message;
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }
    }

    private ref XRRenderProgram? GetRasterProgramSlot(
        EAdvancedMaterialCoverageMode coverage,
        bool meshlet,
        bool multiview)
    {
        if (multiview)
        {
            if (meshlet)
                return ref coverage == EAdvancedMaterialCoverageMode.Opaque
                    ? ref _opaqueMultiviewMeshRasterProgram : ref _maskedMultiviewMeshRasterProgram;
            return ref coverage == EAdvancedMaterialCoverageMode.Opaque
                ? ref _opaqueMultiviewRasterProgram : ref _maskedMultiviewRasterProgram;
        }
        if (meshlet)
            return ref coverage == EAdvancedMaterialCoverageMode.Opaque
                ? ref _opaqueMeshRasterProgram
                : ref _maskedMeshRasterProgram;
        return ref coverage == EAdvancedMaterialCoverageMode.Opaque
            ? ref _opaqueRasterProgram
            : ref _maskedRasterProgram;
    }

    private XRRenderProgram CreateComputeProgram(string assetPath, string name, string additionalPreamble = "")
    {
        XRShader asset = XRShader.EngineShader(assetPath, EShaderType.Compute);
        string preamble = additionalPreamble + VulkanAdvancedSceneProgramBindingContract.BuildShaderPreamble(
            _resources.AdvancedSceneResources);
        XRRenderProgram program = new(
            linkNow: false,
            separable: false,
            CreateShaderWithPreamble(asset, preamble, name + ".comp"))
        {
            Name = name,
            // Advanced visibility prepares compute pipelines asynchronously before
            // admission. This explicit program intent lets VkRenderProgram enqueue
            // that preparation when ordinary background compilation is disabled.
            AllowAsyncBackendCompile = true,
            ExternallyOwnedDescriptorSetMask =
                VulkanAdvancedSceneProgramBindingContract.ExternallyOwnedSetMask,
        };
        return program;
    }

    private XRRenderProgram CreateRasterProgram(string fragmentPath, string name, bool multiview)
    {
        XRShader vertexAsset = XRShader.EngineShader(
            AdvancedVisibilityShaderLibrary.Vertex,
            EShaderType.Vertex);
        XRShader fragmentAsset = XRShader.EngineShader(
            fragmentPath,
            EShaderType.Fragment);
        string preamble = VulkanAdvancedSceneProgramBindingContract.BuildShaderPreamble(
            _resources.AdvancedSceneResources);
        if (multiview)
            preamble = "#define XR_ADV_MULTIVIEW_RASTER 1\n" + preamble;
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
        return program;
    }

    private XRRenderProgram CreateMeshRasterProgram(string fragmentPath, string name, bool multiview)
    {
        XRShader meshAsset = XRShader.EngineShader(
            AdvancedVisibilityShaderLibrary.Mesh,
            EShaderType.Mesh);
        XRShader fragmentAsset = XRShader.EngineShader(
            fragmentPath,
            EShaderType.Fragment);
        string preamble = VulkanAdvancedSceneProgramBindingContract.BuildShaderPreamble(
            _resources.AdvancedSceneResources);
        if (multiview)
            preamble = "#define XR_ADV_MULTIVIEW_RASTER 1\n" + preamble;
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
        return program;
    }

    private XRShader CreateShaderWithPreamble(
        XRShader asset,
        string preamble,
        string name)
    {
        XRShader generated = new(asset.Type, CreateSourceWithPreamble(asset, preamble, name))
        {
            Name = name,
        };
        lock (_preparationGate)
            _generatedShaderSources.Add(new(asset, generated, preamble));
        return generated;
    }

    private static TextFile CreateSourceWithPreamble(
        XRShader asset,
        string preamble,
        string fallbackPath)
    {
        string source = asset.Source.Text ?? throw new InvalidOperationException(
            $"Advanced visibility shader asset '{asset.Source.FilePath}' did not provide source text.");
        return new TextFile(asset.Source.FilePath ?? fallbackPath)
        {
            // Preserve the original asset path so relative includes retain their
            // dependency identity after the Vulkan preamble is inserted.
            Text = InsertPreambleAfterVersion(source, preamble),
        };
    }

    private void RefreshGeneratedShaderSources()
    {
        for (int i = 0; i < _generatedShaderSources.Count; i++)
        {
            GeneratedShaderSource binding = _generatedShaderSources[i];
            long revision = binding.Asset.SourceRevision;
            if (revision == binding.AssetRevision)
                continue;

            binding.Generated.Source = CreateSourceWithPreamble(
                binding.Asset,
                binding.Preamble,
                binding.Generated.Name ?? "AdvancedShader");
            binding.AssetRevision = revision;
        }
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

    private VulkanAdvancedVisibilityPipelineReadiness TryPrepareProgram(
        XRRenderProgram source,
        out VkRenderProgram program,
        out string reason,
        string failure)
    {
        program = null!;
        source.AllowLink();
        VkRenderProgram? wrapper =
            _resources.WrapperLookup.GetOrCreate(source, generateNow: false) as VkRenderProgram ??
            _resources.CreateAPIRenderObject(source) as VkRenderProgram;
        if (wrapper is null)
        {
            reason = $"{failure}: Vulkan wrapper creation was not published";
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }
        VulkanProgramLinkReadiness linkReadiness =
            wrapper.TryPrepareLinkNonblocking(out string linkReason);
        if (linkReadiness != VulkanProgramLinkReadiness.Ready ||
            !wrapper.IsLinked || wrapper.PipelineLayout.Handle == 0)
        {
            XRRenderProgram.EShaderProgramBackendStage stage = source.ShaderMetadata.Backend.Stage;
            reason = $"{failure}: {linkReason}";
            return linkReadiness == VulkanProgramLinkReadiness.Failed ||
                stage is XRRenderProgram.EShaderProgramBackendStage.Failed or
                XRRenderProgram.EShaderProgramBackendStage.BinaryUploadFailed or
                XRRenderProgram.EShaderProgramBackendStage.Abandoned
                    ? VulkanAdvancedVisibilityPipelineReadiness.Failed
                    : VulkanAdvancedVisibilityPipelineReadiness.Pending;
        }

        program = wrapper;
        reason = "Ready";
        return VulkanAdvancedVisibilityPipelineReadiness.Ready;
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

    private sealed class GeneratedShaderSource(
        XRShader asset,
        XRShader generated,
        string preamble)
    {
        internal XRShader Asset { get; } = asset;
        internal XRShader Generated { get; } = generated;
        internal string Preamble { get; } = preamble;
        internal long AssetRevision { get; set; } = asset.SourceRevision;
    }
}
