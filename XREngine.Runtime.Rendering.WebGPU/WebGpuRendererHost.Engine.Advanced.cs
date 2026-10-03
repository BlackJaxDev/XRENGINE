using System.Numerics;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Shaders.Compilation;
using static XREngine.Rendering.XRRenderProgram;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost : IAdvancedVisibilityStageBackendCapability
{
    private readonly record struct AdvancedStageProgram(XRRenderProgram Program, ObjectCacheOwnership Ownership);
    private readonly Dictionary<string, AdvancedStageProgram> _advancedStagePrograms = new(StringComparer.Ordinal);

    /// <summary>Reports the installed native family and its selected-device physical contracts.</summary>
    public override AdvancedRenderPipelineCapabilities GetAdvancedRenderPipelineCapabilities()
    {
        bool ready = State == BrowserRendererState.Ready;
        bool integerTargets = ready && HasAdvancedLimit("maxColorAttachments", 3) &&
            HasAdvancedLimit("maxColorAttachmentBytesPerSample", 16);
        return AdvancedRenderPipelineCapabilities.UnsupportedBackend with
        {
            Backend = RuntimeGraphicsApiKind.WebGPU,
            RendererAvailable = ready,
            SupportsIntegerRenderTargets = integerTargets,
            VisibilityTargetEncoding = integerTargets ? EAdvancedVisibilityTargetEncoding.R32G32UInt : EAdvancedVisibilityTargetEncoding.None,
            SupportsComputeShaders = ready && HasAdvancedLimit("maxComputeInvocationsPerWorkgroup", 256),
            SupportsStorageBuffers = ready && HasAdvancedLimit("maxStorageBuffersPerShaderStage", 7),
            // This family writes firstInstance=0 and pulls canonical identity from storage.
            // The generic indexed renderer's optional indirect-first-instance feature is unrelated.
            IndirectSubmission = ready ? EAdvancedIndirectSubmissionMode.GpuVertexPullIndirect : EAdvancedIndirectSubmissionMode.None,
            Synchronization = ready ? EAdvancedSynchronizationMode.WebGpuPassBoundaries : EAdvancedSynchronizationMode.None,
            SupportsStereoArrayResources = false,
            TextureIndirection = ready && HasAdvancedLimit("maxSampledTexturesPerShaderStage", 16) &&
                HasAdvancedLimit("maxSamplersPerShaderStage", 12)
                ? EAdvancedTextureIndirectionMode.WebGpuCohortBindings : EAdvancedTextureIndirectionMode.None,
            SupportsFrameSlotStorage = ready,
            ShaderFamily = GetAdvancedVisibilityFamilyAdmission().IsAdmitted
                ? EAdvancedShaderFamily.VisibilityBuffer : EAdvancedShaderFamily.None,
        };
    }

    private bool HasAdvancedLimit(string name, long required)
        => DeviceCapabilities is { } capabilities && capabilities.Limits.TryGetValue(name, out long value) && value >= required;

    public bool SupportsAdvancedComputeMeshletVisibility => GetAdvancedVisibilityFamilyAdmission().IsAdmitted;

    /// <summary>Reports only complete native stages, independently of whole-family admission.</summary>
    public bool SupportsAdvancedVisibilityStage(EAdvancedRenderStage stage)
        => GetAdvancedVisibilityFamilyAdmission().IsAdmitted &&
           (stage == EAdvancedRenderStage.AmbientOcclusion
               ? GetAdvancedAmbientOcclusionRejection() is null
               : stage is EAdvancedRenderStage.VisibilityPreparation or EAdvancedRenderStage.VisibilityRaster or
                   EAdvancedRenderStage.DepthPyramidAndLateVisibility or EAdvancedRenderStage.WorkClassification or
                   EAdvancedRenderStage.NativeOpaqueShading);

    /// <summary>Records one shared native stage; a rejected producer prevents presentation of the entire frame.</summary>
    public bool TryEnqueueAdvancedVisibilityStage(in AdvancedVisibilityStageBackendRequest request, out string failureReason)
    {
        bool accepted = TryBeginAdvancedStage(in request, out failureReason) &&
            TryEnqueueAdvancedStageCore(in request, out failureReason);
        if (accepted) CompleteAdvancedStage(in request);
        if (!accepted && _engineRecording) MarkEngineDrawPending();
        return accepted;
    }

    private bool TryEnqueueAdvancedStageCore(in AdvancedVisibilityStageBackendRequest request, out string failureReason)
    {
        if (!SupportsAdvancedVisibilityStage(request.Stage))
        {
            failureReason = "WebGPU.Advanced.StageUnsupported: the requested native stage has no admitted installed program family.";
            return false;
        }
        if (request.GetInvalidReason() is { } invalid)
        {
            failureReason = invalid;
            return false;
        }
        if (request.Views.ViewCount != 1 || request.NativeViewIndex != 0)
        {
            failureReason = "WebGPU.Advanced.ViewUnsupported: this depth-derived stage requires an explicit single-view 2D resource family.";
            return false;
        }
        if (request.MsaaSampleCount is not (1 or 4) || request.MsaaSampleCount == 4 &&
            request.SampleEncoding != EAdvancedVisibilitySampleEncoding.PackedUInt16)
        {
            failureReason = "WebGPU.Advanced.SampleProfileUnsupported: native visibility requires one sample or the explicit packed16 four-sample encoding.";
            return false;
        }
        if (request.MsaaSampleCount == 4 && GetAdvancedMultisampleRejection() is { } sampleRejection)
        {
            failureReason = sampleRejection;
            return false;
        }
        XRRenderPipelineInstance? instance = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        if (instance?.Pipeline is null)
        {
            failureReason = "WebGPU.Advanced.OutputMissing: an active authored pipeline instance is required.";
            return false;
        }
        if (request.Stage == EAdvancedRenderStage.VisibilityPreparation)
            return TryEnqueueAdvancedPreparation(in request, instance, out failureReason);
        if (request.Stage == EAdvancedRenderStage.VisibilityRaster)
            return TryEnqueueAdvancedRaster(in request, instance, out failureReason);
        if (request.Stage == EAdvancedRenderStage.DepthPyramidAndLateVisibility)
            return TryEnqueueAdvancedDepth(in request, instance, out failureReason);
        if (request.Stage is EAdvancedRenderStage.WorkClassification or EAdvancedRenderStage.NativeOpaqueShading)
            return TryEnqueueAdvancedShading(in request, instance, out failureReason);
        if (instance?.Pipeline is not { } owner ||
            !instance.Resources.TryGetTexture(request.DepthTargetName, out XRTexture? depth) || depth is not XRTexture2D depth2D ||
            !instance.Resources.TryGetTexture(request.AmbientOcclusionTargetName, out XRTexture? output) || output is not XRTexture2D output2D)
        {
            failureReason = "WebGPU.Advanced.ResourcesMissing: the frozen output requires depth and AO 2D textures from its active resource generation.";
            return false;
        }
        if (!owner.TryGetWebPipelineArtifact("advanced::gtao", out ShaderProgramArtifact? artifact))
        {
            failureReason = "WebGPU.Advanced.ArtifactMissing: the authored output must supply advanced::gtao.";
            return false;
        }
        BackendReadyFramePackage package = request.BackendReadyPackage!;
        RenderFrameViewDescriptor source = request.Views.GetView(0);
        BackendReadyCanonicalViewRecord canonical = package.ApplyCanonicalViewPolicy(
            BackendReadyFramePackage.CreateCanonicalViewRecord(in source, package.CanonicalScenePublication.FrameGeneration));
        AdvancedViewRecord view = AdvancedViewRecordFactory.Create(in canonical);
        XRRenderProgram program = GetAdvancedStageProgram(artifact);
        ERendererComputeEnqueueStatus status = TryDispatchAdvancedAmbientOcclusion(
            program, depth2D, output2D, in view, request.EnableBuiltInAmbientOcclusion);
        failureReason = status == ERendererComputeEnqueueStatus.Enqueued
            ? string.Empty : $"WebGPU.Advanced.AmbientOcclusion.{status}: the complete frame remains unsubmitted.";
        return status == ERendererComputeEnqueueStatus.Enqueued;
    }

    /// <summary>
    /// Executes the Advanced GTAO primitive for an explicitly supplied program and frozen view.
    /// It writes normalized, UNORM8-quantized visibility into the physical R32F output.
    /// </summary>
    public ERendererComputeEnqueueStatus TryDispatchAdvancedAmbientOcclusion(
        XRRenderProgram program, XRTexture2D depth, XRTexture2D output,
        in AdvancedViewRecord view, bool enabled)
    {
        if (State != BrowserRendererState.Ready)
            return ERendererComputeEnqueueStatus.DeviceLost;
        if (!_engineRecording)
            return ERendererComputeEnqueueStatus.NoPassContext;
        ValidateAdvancedDepthTextures(depth, output, coarse: false);
        if (view.RenderSizeAndInverse.X != depth.Width || view.RenderSizeAndInverse.Y != depth.Height ||
            view.RenderSizeAndInverse.Z != 1.0f / depth.Width || view.RenderSizeAndInverse.W != 1.0f / depth.Height)
            throw new NotSupportedException("WebGPU.Advanced.ViewExtentMismatch: AO must use the canonical view that produced the depth extent.");
        WebGpuRenderProgram api = (WebGpuRenderProgram)GetOrCreateAPIRenderObject(program)!;
        api.Generate();
        WebGpuAdvancedDepthProgramContract.Validate(api.Artifact, ambientOcclusion: true);
        program.Uniform("View", view.View);
        program.Uniform("InverseViewProjection", view.InverseViewProjectionJittered);
        program.Uniform("ProjectionUnjittered", view.ProjectionUnjittered);
        program.Uniform("RenderSizeAndInverse", view.RenderSizeAndInverse);
        program.Uniform("DepthParams", view.DepthParams);
        program.Uniform("ViewFlags", (uint)view.Flags);
        program.Uniform("AoEnabled", enabled ? 1u : 0u);
        return DispatchAdvancedDepthProgram(api, depth, output,
            (depth.Width + 15u) / 16u, (depth.Height + 15u) / 16u);
    }

    /// <summary>
    /// Executes the Advanced conservative 64-pixel depth reduction without reading depth or counts back.
    /// This primitive alone does not advertise the complete depth-pyramid/late-visibility stage.
    /// </summary>
    public ERendererComputeEnqueueStatus TryBuildAdvancedDepthPyramid(
        XRRenderProgram program, XRTexture2D depth, XRTexture2D output, bool reversedDepth)
    {
        if (State != BrowserRendererState.Ready)
            return ERendererComputeEnqueueStatus.DeviceLost;
        if (!_engineRecording)
            return ERendererComputeEnqueueStatus.NoPassContext;
        ValidateAdvancedDepthTextures(depth, output, coarse: true);
        WebGpuRenderProgram api = (WebGpuRenderProgram)GetOrCreateAPIRenderObject(program)!;
        api.Generate();
        WebGpuAdvancedDepthProgramContract.Validate(api.Artifact, ambientOcclusion: false);
        program.Uniform("Extent", new Vector2(depth.Width, depth.Height));
        program.Uniform("ReversedDepth", reversedDepth ? 1u : 0u);
        return DispatchAdvancedDepthProgram(api, depth, output, output.Width, output.Height);
    }

    private ERendererComputeEnqueueStatus DispatchAdvancedDepthProgram(
        WebGpuRenderProgram api, XRTexture2D depth, XRTexture2D output, uint groupsX, uint groupsY)
    {
        XRRenderProgram program = api.Data;
        try
        {
            program.Sampler("VisibilityDepth", depth, 0);
            program.BindImageTexture(1, output, 0, false, 0, EImageAccess.WriteOnly, EImageFormat.R32F);
            return TryDispatchCompute(program, groupsX, groupsY, 1);
        }
        finally
        {
            // Binding failure must not lend a partial resource set to a retry.
            api.ClearTransientComputeBindings();
        }
    }

    private static void ValidateAdvancedDepthTextures(XRTexture2D depth, XRTexture2D output, bool coarse)
    {
        ArgumentNullException.ThrowIfNull(depth);
        ArgumentNullException.ThrowIfNull(output);
        uint divisor = coarse ? 64u : 1u;
        if (ReferenceEquals(depth, output) || depth.MultiSampleCount != 1 || output.MultiSampleCount != 1 ||
            depth.Width == 0 || depth.Height == 0 || output.Width != (depth.Width + divisor - 1u) / divisor ||
            output.Height != (depth.Height + divisor - 1u) / divisor || output.SizedInternalFormat != ESizedInternalFormat.R32f ||
            !output.RequiresStorageUsage || depth.SizedInternalFormat is not (ESizedInternalFormat.DepthComponent32f or
                ESizedInternalFormat.Depth24Stencil8 or ESizedInternalFormat.Depth32fStencil8))
            throw new NotSupportedException("WebGPU.Advanced.DepthResourceMismatch: a resolved depth input and distinct matching single-sample R32F storage output are required.");
    }

    private XRRenderProgram GetAdvancedStageProgram(ShaderProgramArtifact artifact)
    {
        if (_advancedStagePrograms.TryGetValue(artifact.Identity, out AdvancedStageProgram existing))
            return existing.Program;
        if (_advancedStagePrograms.Count >= WebPipelineArtifactCatalog.MaximumEntries)
            throw new InvalidOperationException("WebGPU.Advanced.ProgramCapacity: the renderer exceeds the bounded cooked program catalog.");
        using ObjectCachePublicationScope ownership = XRObjectBase.BeginIndependentObjectCachePublication();
        using IDisposable suppression = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        XRRenderProgram program = new() { Name = artifact.Name, CookedArtifact = artifact };
        _advancedStagePrograms.Add(artifact.Identity, new(program, ownership.CompleteWithOwnership()));
        return program;
    }

    private void DestroyAdvancedStagePrograms()
    {
        DestroyAdvancedDeformationInputs();
        ClearAdvancedReservations();
        DestroyAdvancedVisibilityOutputs();
        foreach (AdvancedStageProgram entry in _advancedStagePrograms.Values)
            entry.Ownership.Dispose();
        _advancedStagePrograms.Clear();
    }
}
