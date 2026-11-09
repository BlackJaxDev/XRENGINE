using System.Runtime.CompilerServices;
using XREngine.Rendering.Vulkan.Commands;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Reuses the mesh operations of an OpenXR eye capture across frames. Each eye capture
/// slot keeps its own cohort, so the eyes and the desktop never replace each other's
/// recipe. A frame whose raw requests match the retained cohort exactly rebuilds each
/// reusable operation from its recipe with the current per-frame draw values, and
/// enqueues it into the capture's frame-operation queue like a full materialization.
/// </summary>
internal sealed partial class VulkanFrameLoop
{
    private enum EOpenXrMeshCohortMode : byte
    {
        Off,
        On,
        Witness,
    }

    private const int OpenXrMeshCohortSlotCount = 4;

    private static readonly EOpenXrMeshCohortMode s_openXrMeshCohortMode = ResolveOpenXrMeshCohortMode();
    private static readonly bool s_openXrMeshCohortEnabled = s_openXrMeshCohortMode != EOpenXrMeshCohortMode.Off;

    private readonly VulkanPreparedMeshOperationCohort[] _openXrMeshOperationCohorts = CreateOpenXrMeshOperationCohorts();
    private readonly ulong[] _openXrMeshOperationCohortKeys = new ulong[OpenXrMeshCohortSlotCount];
    private int _openXrMeshOperationCohortNextSlot;
    private long _openXrMeshCohortBuilds;
    private long _openXrMeshCohortHits;
    private long _openXrMeshCohortMisses;
    private long _openXrMeshCohortReusedOperations;
    private long _openXrMeshCohortWitnessChecks;
    private int _openXrMeshCohortWitnessMismatches;

    private static EOpenXrMeshCohortMode ResolveOpenXrMeshCohortMode()
        => Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.OpenXrMeshCohort)?.Trim() switch
        {
            "0" => EOpenXrMeshCohortMode.Off,
            "witness" => EOpenXrMeshCohortMode.Witness,
            _ => EOpenXrMeshCohortMode.On,
        };

    private static VulkanPreparedMeshOperationCohort[] CreateOpenXrMeshOperationCohorts()
    {
        VulkanPreparedMeshOperationCohort[] cohorts = new VulkanPreparedMeshOperationCohort[OpenXrMeshCohortSlotCount];
        for (int index = 0; index < cohorts.Length; index++)
            cohorts[index] = new VulkanPreparedMeshOperationCohort();
        return cohorts;
    }

    /// <summary>
    /// Selects the cohort of the capture slot that the first normalized request
    /// belongs to. An unknown slot replaces the oldest one.
    /// </summary>
    private VulkanPreparedMeshOperationCohort SelectOpenXrMeshOperationCohort(int requestCount)
    {
        ref readonly VulkanMeshRenderRequest first = ref _meshOperationRequestScratch[0];
        FrameOpContext context = first.Context;
        FrameOpSignatureHasher hash = new();
        hash.Add(context.PipelineIdentity);
        hash.Add(context.ViewportIdentity);
        hash.Add(context.OutputTargetIdentity);
        hash.Add(RuntimeHelpers.GetHashCode(first.Producer.Target));
        hash.Add(context.StereoEnabled);
        hash.Add(context.MultiviewEnabled);
        ulong key = hash.ToHash() | 1UL;

        for (int slot = 0; slot < OpenXrMeshCohortSlotCount; slot++)
            if (_openXrMeshOperationCohortKeys[slot] == key)
                return _openXrMeshOperationCohorts[slot];

        int replaced = _openXrMeshOperationCohortNextSlot;
        _openXrMeshOperationCohortNextSlot = (replaced + 1) % OpenXrMeshCohortSlotCount;
        _openXrMeshOperationCohortKeys[replaced] = key;
        VulkanPreparedMeshOperationCohort cohort = _openXrMeshOperationCohorts[replaced];
        cohort.Invalidate();
        return cohort;
    }

    /// <summary>
    /// Enqueues the retained cohort for the current requests when every request matches
    /// its entry and every reused binding artifact is still the current one. Nothing is
    /// enqueued unless the whole cohort can be built, so a miss falls back to full
    /// materialization without duplicate operations.
    /// </summary>
    private bool TryReuseOpenXrMeshOperationCohort(
        VulkanPreparedMeshOperationCohort cohort,
        int requestCount,
        in VulkanMeshMaterializationSnapshot materializationSnapshot,
        bool allowFrameOwnedAuthoringOperations)
    {
        if (!cohort.IsValid ||
            cohort.Count != requestCount ||
            _frameOperationQueue.CurrentThread.RenderQueryBracketDepth != 0)
        {
            _openXrMeshCohortMisses++;
            return false;
        }

        for (int index = 0; index < requestCount; index++)
        {
            if (!IsPreparedMeshOperationCohortMatch(
                    in _meshOperationRequestScratch[index],
                    in cohort.GetEntry(index),
                    in cohort.GetOperation(index),
                    openXrEye: true))
            {
                _openXrMeshCohortMisses++;
                return false;
            }
        }

        XRRenderPipelineInstance? scopedPipeline = null;
        XRCamera? scopedCamera = null;
        IDisposable? pipelineScope = null;
        IDisposable? cameraScope = null;
        int reusedCount = 0;
        bool built = false;
        try
        {
            for (int index = 0; index < requestCount; index++)
            {
                ref readonly VulkanMeshRenderRequest request = ref _meshOperationRequestScratch[index];
                XRRenderPipelineInstance pipeline = request.Pipeline!;
                XRCamera? camera = pipeline.LastRenderingCamera ?? pipeline.LastSceneCamera;
                if (!ReferenceEquals(scopedPipeline, pipeline) || !ReferenceEquals(scopedCamera, camera))
                {
                    cameraScope?.Dispose();
                    pipelineScope?.Dispose();
                    pipelineScope = RuntimeRenderingHostServices.Diagnostics.PushRenderingPipeline(pipeline);
                    cameraScope = pipeline.RenderState.PushRenderingCamera(camera);
                    scopedPipeline = pipeline;
                    scopedCamera = camera;
                }

                VulkanMeshOperationRequest operation;
                if (cohort.GetEntry(index).IsReusable)
                {
                    VulkanMeshOperationRequest template = cohort.GetOperation(index);
                    if (!IsOpenXrCohortBindingArtifactCurrent(in request, in template))
                    {
                        cohort.Invalidate();
                        _openXrMeshCohortMisses++;
                        return false;
                    }

                    operation = BuildOpenXrCohortOperation(in request, in template, pipeline);
                    if (s_openXrMeshCohortMode == EOpenXrMeshCohortMode.Witness)
                        operation = WitnessOpenXrCohortOperation(in request, pipeline, in materializationSnapshot, operation);
                    reusedCount++;
                }
                else if (!TryMaterializeQueuedMeshRenderRequest(
                        in request,
                        pipeline,
                        in materializationSnapshot,
                        prewarmDescriptorAllocation: false,
                        out operation,
                        out _))
                {
                    cohort.Invalidate();
                    _openXrMeshCohortMisses++;
                    return false;
                }

                _meshOperationMaterializationScratch[index] = operation;
            }
            built = true;
        }
        finally
        {
            cameraScope?.Dispose();
            pipelineScope?.Dispose();
            if (!built)
                _meshOperationMaterializationScratch.AsSpan(0, requestCount).Clear();
        }

        int enqueueFailures = 0;
        for (int index = 0; index < requestCount; index++)
            if (!EnqueueQueuedMeshDraw(in _meshOperationMaterializationScratch[index], allowFrameOwnedAuthoringOperations, out _))
                enqueueFailures++;
        _meshOperationMaterializationScratch.AsSpan(0, requestCount).Clear();

        // A rejected operation is skipped, as on the full path. The next frame
        // materializes the whole cohort again so the rejection is diagnosed there.
        if (enqueueFailures > 0)
            cohort.Invalidate();
        _openXrMeshCohortHits++;
        _openXrMeshCohortReusedOperations += reusedCount;
        return true;
    }

    /// <summary>
    /// A reused recipe keeps the binding artifact captured when it was built. The
    /// artifact is reacquired for the current material and program; it must be the
    /// same immutable object, which also proves that the typed publishers kept their
    /// generations.
    /// </summary>
    private static bool IsOpenXrCohortBindingArtifactCurrent(
        in VulkanMeshRenderRequest request,
        in VulkanMeshOperationRequest template)
    {
        ComputeDispatchSnapshot? cachedBindings = template.Draw.ProgramBindingSnapshot;
        if (cachedBindings is null)
            return true;

        LayeredShadowUniformState shadowUniformState = request.ViewSnapshot.ShadowUniformState;
        return template.Draw.PreparedProgram is { } expectedProgram &&
            request.Renderer.TryGetCurrentPersistentProgramBindingArtifactForReuse(
                request.ResolvedMaterial.Material,
                expectedProgram,
                in shadowUniformState,
                out ComputeDispatchSnapshot? currentBindings) &&
            currentBindings is { IsImmutableBindingArtifact: true } &&
            ReferenceEquals(currentBindings, cachedBindings);
    }

    private static VulkanMeshOperationRequest BuildOpenXrCohortOperation(
        in VulkanMeshRenderRequest request,
        in VulkanMeshOperationRequest template,
        XRRenderPipelineInstance pipeline)
    {
        PendingMeshDraw draw = template.Draw with
        {
            ModelMatrix = request.ModelMatrix,
            PreviousModelMatrix = request.PreviousModelMatrix,
            SourceFlatDirection = request.SourceFlatDirection,
            MaterialOverride = request.ResolvedMaterial.Material,
            Instances = request.ExpandedInstances,
            BillboardMode = request.BillboardMode,
            TransformId = request.TransformId,
            ViewSnapshot = request.ViewSnapshot,
            ShadowCasterRelevance = request.ShadowCasterRelevance,
            CanonicalDrawIdentitySnapshot = request.CanonicalDrawIdentitySnapshot,
            WindowPresentationSourceMarker = request.WindowPresentationSourceMarker,
        };
        draw = draw with
        {
            AutoUniformPublication = VulkanAutoUniformPublicationSnapshot.Capture(draw, pipeline),
        };
        return template with
        {
            Draw = draw,
            ProducerSnapshot = request.Producer,
        };
    }

    /// <summary>
    /// Materializes the request again and compares the structural draw state with the
    /// reused operation. The fresh operation is the one enqueued, so a mismatch is
    /// logged without affecting the image.
    /// </summary>
    private VulkanMeshOperationRequest WitnessOpenXrCohortOperation(
        in VulkanMeshRenderRequest request,
        XRRenderPipelineInstance pipeline,
        in VulkanMeshMaterializationSnapshot materializationSnapshot,
        VulkanMeshOperationRequest reused)
    {
        if (!TryMaterializeQueuedMeshRenderRequest(
                in request,
                pipeline,
                in materializationSnapshot,
                prewarmDescriptorAllocation: false,
                out VulkanMeshOperationRequest fresh,
                out _))
        {
            return reused;
        }

        long checks = ++_openXrMeshCohortWitnessChecks;
        string? field = FindOpenXrCohortWitnessMismatch(in reused, in fresh);
        if (field is not null && ++_openXrMeshCohortWitnessMismatches <= 20)
        {
            Debug.WriteAuxiliaryLog(
                "openxr-mesh-cohort-witness.log",
                $"Mismatch: field={field} mesh='{request.Renderer.Mesh?.Name ?? "<unnamed>"}' material='{request.ResolvedMaterial.Material.Name ?? "<unnamed>"}'");
        }
        if ((checks & 0x3FFFL) == 0L)
        {
            Debug.WriteAuxiliaryLog(
                "openxr-mesh-cohort-witness.log",
                $"Witness checks={checks} mismatches={_openXrMeshCohortWitnessMismatches} hits={_openXrMeshCohortHits} misses={_openXrMeshCohortMisses} builds={_openXrMeshCohortBuilds} reused={_openXrMeshCohortReusedOperations}");
        }
        return fresh;
    }


    private static string? FindOpenXrCohortWitnessMismatch(
        in VulkanMeshOperationRequest reused,
        in VulkanMeshOperationRequest fresh)
    {
        PendingMeshDraw a = reused.Draw;
        PendingMeshDraw b = fresh.Draw;
        if (reused.PassIndex != fresh.PassIndex) return nameof(reused.PassIndex);
        if (!ReferenceEquals(a.Renderer, b.Renderer)) return nameof(a.Renderer);
        if (!ReferenceEquals(a.PreparedProgram, b.PreparedProgram)) return nameof(a.PreparedProgram);
        if (a.PreparedProgramLinkGeneration != b.PreparedProgramLinkGeneration) return nameof(a.PreparedProgramLinkGeneration);
        if (!string.Equals(a.PreparedProgramIdentity, b.PreparedProgramIdentity, StringComparison.Ordinal)) return nameof(a.PreparedProgramIdentity);
        if (!ReferenceEquals(a.ProgramBindingSnapshot, b.ProgramBindingSnapshot)) return nameof(a.ProgramBindingSnapshot);
        if (!a.Viewport.X.Equals(b.Viewport.X) || !a.Viewport.Y.Equals(b.Viewport.Y) ||
            !a.Viewport.Width.Equals(b.Viewport.Width) || !a.Viewport.Height.Equals(b.Viewport.Height) ||
            !a.Viewport.MinDepth.Equals(b.Viewport.MinDepth) || !a.Viewport.MaxDepth.Equals(b.Viewport.MaxDepth))
            return nameof(a.Viewport);
        if (a.Scissor.Offset.X != b.Scissor.Offset.X || a.Scissor.Offset.Y != b.Scissor.Offset.Y ||
            a.Scissor.Extent.Width != b.Scissor.Extent.Width || a.Scissor.Extent.Height != b.Scissor.Extent.Height)
            return nameof(a.Scissor);
        if (a.ViewportScissorCount != b.ViewportScissorCount) return nameof(a.ViewportScissorCount);
        if (a.RasterizationSamples != b.RasterizationSamples) return nameof(a.RasterizationSamples);
        if (a.DepthTestEnabled != b.DepthTestEnabled || a.DepthWriteEnabled != b.DepthWriteEnabled ||
            a.DepthCompareOp != b.DepthCompareOp)
            return "DepthState";
        if (a.StencilTestEnabled != b.StencilTestEnabled || a.StencilWriteMask != b.StencilWriteMask ||
            !a.FrontStencilState.Equals(b.FrontStencilState) || !a.BackStencilState.Equals(b.BackStencilState))
            return "StencilState";
        if (a.ColorWriteMask != b.ColorWriteMask || a.CullMode != b.CullMode || a.FrontFace != b.FrontFace)
            return "RasterState";
        if (a.BlendEnabled != b.BlendEnabled || a.AlphaToCoverageEnabled != b.AlphaToCoverageEnabled ||
            a.ColorBlendOp != b.ColorBlendOp || a.AlphaBlendOp != b.AlphaBlendOp ||
            a.SrcColorBlendFactor != b.SrcColorBlendFactor || a.DstColorBlendFactor != b.DstColorBlendFactor ||
            a.SrcAlphaBlendFactor != b.SrcAlphaBlendFactor || a.DstAlphaBlendFactor != b.DstAlphaBlendFactor)
            return "BlendState";
        if (a.ModelMatrix != b.ModelMatrix || a.PreviousModelMatrix != b.PreviousModelMatrix) return "ModelMatrices";
        if (!ReferenceEquals(a.MaterialOverride, b.MaterialOverride)) return nameof(a.MaterialOverride);
        if (a.Instances != b.Instances) return nameof(a.Instances);
        if (a.BillboardMode != b.BillboardMode) return nameof(a.BillboardMode);
        if (a.TransformId != b.TransformId) return nameof(a.TransformId);
        if (!ReferenceEquals(a.ViewSnapshot, b.ViewSnapshot) && !Equals(a.ViewSnapshot, b.ViewSnapshot)) return nameof(a.ViewSnapshot);
        if (!a.SourceFlatDirection.Equals(b.SourceFlatDirection)) return nameof(a.SourceFlatDirection);
        if (a.PreparationCompatibilitySignature != b.PreparationCompatibilitySignature) return nameof(a.PreparationCompatibilitySignature);
        if (reused.RequiresExternalUploadBlock != fresh.RequiresExternalUploadBlock) return nameof(reused.RequiresExternalUploadBlock);
        return null;
    }
}
