using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.DLSS;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>Frame-operation translations that capture immutable context at their authority boundary.</summary>
internal sealed partial class VulkanFrameLoop
{
    private ulong _advancedVisibilityAdmissionFrameId;
    private readonly object _advancedVisibilityAdmissionGate = new();
    private ulong _advancedVisibilityAdmissionOutputId;
    private VulkanAdvancedVisibilityBackendPackageSnapshot _advancedVisibilityAdmissionPackage;
    private bool _advancedVisibilityAdmissionAccepted;
    private string _advancedVisibilityAdmissionReason = string.Empty;
    // Lane groups accepted this frame live in a ring: at most four groups per
    // frame and three frames in flight, each slot validated by render frame id.
    private const int DirectionalShadowLaneRingCapacity = 16;
    private readonly VulkanAdvancedDirectionalShadowLaneStorage[] _directionalShadowLaneRing =
        CreateDirectionalShadowLaneRing();
    private int _directionalShadowLaneRingCursor;

    internal bool SupportsAdvancedVisibilityStage(EAdvancedRenderStage stage)
        => stage is (EAdvancedRenderStage.VisibilityPreparation or
               EAdvancedRenderStage.VisibilityRaster or
               EAdvancedRenderStage.DepthPyramidAndLateVisibility or
               EAdvancedRenderStage.DirectionalShadowRaster or
               EAdvancedRenderStage.AmbientOcclusion or
               EAdvancedRenderStage.WorkClassification or
               EAdvancedRenderStage.NativeOpaqueShading) &&
           _commandRuntime.HasAdvancedVisibilityReservationGeneration &&
           _commandRuntime.GetAdvancedVisibilityPhysicalReadiness(out _) ==
               VulkanAdvancedVisibilityPipelineReadiness.Ready &&
           (stage != EAdvancedRenderStage.DirectionalShadowRaster ||
            _resourceRuntime.AdvancedVisibilityPipelines.GetDirectionalShadowLaneReadiness(out _) ==
                VulkanAdvancedVisibilityPipelineReadiness.Ready);

    private static VulkanAdvancedDirectionalShadowLaneStorage[] CreateDirectionalShadowLaneRing()
    {
        VulkanAdvancedDirectionalShadowLaneStorage[] ring =
            new VulkanAdvancedDirectionalShadowLaneStorage[DirectionalShadowLaneRingCapacity];
        for (int index = 0; index < ring.Length; index++)
            ring[index] = new VulkanAdvancedDirectionalShadowLaneStorage();
        return ring;
    }

    private bool TryCaptureDirectionalShadowLane(
        in AdvancedVisibilityStageBackendRequest request,
        out VulkanAdvancedDirectionalShadowLaneStorage storage,
        out string failureReason)
    {
        storage = _directionalShadowLaneRing[_directionalShadowLaneRingCursor];
        _directionalShadowLaneRingCursor =
            (_directionalShadowLaneRingCursor + 1) % DirectionalShadowLaneRingCapacity;
        if (request.DirectionalShadowLane is not { } lane)
        {
            failureReason = "The directional shadow stage carries no cascade group.";
            return false;
        }

        Extent2D pageExtent = VulkanCommandRuntime.ResolveFrameBufferDrawExtent(request.Target);
        return storage.TryCapture(lane, request.RenderFrameId, pageExtent, out failureReason);
    }

    internal bool TryEnqueueAdvancedVisibilityStage(
        in AdvancedVisibilityStageBackendRequest request,
        out string failureReason)
    {
        if (request.GetInvalidReason() is { } invalidReason)
        {
            failureReason = invalidReason;
            return false;
        }
        AdvancedVisibilityFamilyReservation reservation = request.Reservation;
        if (!_commandRuntime.IsAdvancedVisibilityReservationCurrent(in reservation))
        {
            failureReason = "The advanced visibility reservation is stale or belongs to another output.";
            return false;
        }
        if (request.Views.ViewCount > 2 ||
            (request.Views.ViewCount > 1 && !_deviceContext.AdvancedMultiviewEnabled))
        {
            failureReason = "Advanced layered Vulkan requires an enabled multiview device and one or two views.";
            return false;
        }
        for (int view = 0; view < request.Views.ViewCount; view++)
            if (request.Views.GetView(view).OutputLayer != (uint)view)
            {
                failureReason = "Advanced layered Vulkan requires unique contiguous output layers in frozen view order.";
                return false;
            }
        // Author the complete immutable family while shaders compile. The sealed
        // frame checks pipeline readiness before recording native commands.
        if (!SupportsAdvancedVisibilityStage(request.Stage))
        {
            if (request.Stage == EAdvancedRenderStage.DirectionalShadowRaster &&
                _resourceRuntime.AdvancedVisibilityPipelines.GetDirectionalShadowLaneReadiness(out string laneReason) !=
                    VulkanAdvancedVisibilityPipelineReadiness.Ready)
            {
                failureReason = $"The Vulkan directional shadow lane is unavailable: {laneReason}";
                return false;
            }

            _commandRuntime.GetAdvancedVisibilityPhysicalReadiness(out string availabilityReason);
            failureReason = $"The Vulkan zero-readback visibility lane is unavailable on this device or resource generation: {availabilityReason}";
            return false;
        }

        int passIndex = RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex;
        if (passIndex < 0)
        {
            failureReason = "No render-graph pass is active for the advanced visibility stage.";
            return false;
        }

        FrameOpContext context = CaptureFrameOpContextForCurrentPipelineScope();
        if (context.AdvancedVisibilityOutputIdentity != reservation.OutputId)
        {
            failureReason = "The active Vulkan pipeline scope does not match the output that owns the advanced visibility reservation.";
            return false;
        }
        BackendReadyFramePackage? package = request.BackendReadyPackage;
        if (!VulkanAdvancedVisibilityBackendPackageSnapshot.TryCapture(
                package,
                out VulkanAdvancedVisibilityBackendPackageSnapshot backendPackage))
        {
            failureReason =
                "The active pipeline has no published canonical backend package for this visibility family.";
            return false;
        }
        AdvancedGpuScenePublication scenePublication =
            request.Publication.ScenePublication;
        if (!backendPackage.MatchesScenePublication(in scenePublication))
        {
            failureReason =
                $"The canonical backend package has not caught up to the advanced preparation scene publication. Backend={backendPackage.CanonicalScenePublication}, Prepared={scenePublication}.";
            return false;
        }
        if (!TryValidateAdvancedVisibilityPackageSources(
                in backendPackage,
                request.RenderFrameId,
                reservation.OutputId,
                out failureReason))
        {
            return false;
        }
        VulkanAdvancedVisibilityStageRequest vulkanRequest = new(
            request.Stage,
            request.Phase,
            request.Reservation,
            backendPackage,
            request.Publication,
            request.Publication.VisibilityContentGeneration,
            request.Extractor,
            request.RenderFrameId,
            request.Views,
            request.Target,
            request.IdentityTargetName,
            request.MetadataTargetName,
            request.SelectionTargetName,
            request.DepthTargetName,
            request.AmbientOcclusionTargetName,
            request.CurrentDepthPyramidTargetName,
            request.ShadingDebugView,
            request.RequireNativeOutput,
            request.EnableBuiltInAmbientOcclusion,
            request.EnableLightProbesAndIbl,
            request.IsMinimalVisibilityOutput,
            request.NativeViewIndex,
            request.RequiresMaterialSurfaceExports,
            request.SuppressBaselineDiffuse,
            request.MsaaSampleCount,
            request.HasAuthoredBackground);
        if (request.Stage == EAdvancedRenderStage.DirectionalShadowRaster)
        {
            if (!TryCaptureDirectionalShadowLane(
                    in request,
                    out VulkanAdvancedDirectionalShadowLaneStorage laneStorage,
                    out failureReason))
            {
                return false;
            }
            vulkanRequest = vulkanRequest with { DirectionalShadowLane = laneStorage };
        }
        if (!_frameOperationQueue.TryAcquireAdvancedVisibilityInput(
                in vulkanRequest,
                out VulkanAdvancedVisibilityInputLease inputLease,
                out failureReason))
        {
            return false;
        }

        try
        {
            _frameOperationQueue.EnqueuePrepared(
                new AdvancedVisibilityOp(
                    passIndex,
                    vulkanRequest,
                    inputLease,
                    context));
        }
        catch
        {
            inputLease.Release();
            throw;
        }
        failureReason = "Ready";
        return true;
    }

    private bool TryValidateAdvancedVisibilityPackageSources(
        in VulkanAdvancedVisibilityBackendPackageSnapshot package,
        ulong renderFrameId,
        ulong outputId,
        out string reason)
    {
        lock (_advancedVisibilityAdmissionGate)
            return TryValidateAdvancedVisibilityPackageSourcesLocked(in package, renderFrameId, outputId, out reason);
    }

    private bool TryValidateAdvancedVisibilityPackageSourcesLocked(
        in VulkanAdvancedVisibilityBackendPackageSnapshot package,
        ulong renderFrameId,
        ulong outputId,
        out string reason)
    {
        if (_advancedVisibilityAdmissionFrameId == renderFrameId &&
            _advancedVisibilityAdmissionOutputId == outputId &&
            _advancedVisibilityAdmissionPackage == package)
        {
            reason = _advancedVisibilityAdmissionReason;
            return _advancedVisibilityAdmissionAccepted;
        }

        bool accepted = _resourceRuntime.AdvancedSceneResources
            .TryValidatePackageSourcesForAdmission(
                package.Package,
                out EVulkanAdvancedSceneResourceFailure failure,
                out string validationReason);
        _advancedVisibilityAdmissionFrameId = renderFrameId;
        _advancedVisibilityAdmissionOutputId = outputId;
        _advancedVisibilityAdmissionPackage = package;
        _advancedVisibilityAdmissionAccepted = accepted;
        _advancedVisibilityAdmissionReason = accepted
            ? "Ready"
            : $"The canonical scene source image is not stable ({failure}): {validationReason}";
        reason = _advancedVisibilityAdmissionReason;
        return accepted;
    }

    internal string GetMeshletDispatchUnsupportedReason()
        => _deviceContext.MeshletDispatchStatus;

    internal ERendererComputeEnqueueStatus TryDispatchComputeIndirect(XRRenderProgram program, XRDataBuffer arguments, nint byteOffset, string label)
        => _commandRuntime.TryEnqueueIndirectComputeDispatch(_resourceRuntime.WrapperLookup, _frameOperationQueue, program, arguments, byteOffset, label, RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex, CaptureFrameOpContextOrLastActive(), AllowSynchronousResourceUploads, IsDeviceLost);

    internal ERendererComputeEnqueueStatus TryEnqueueBufferCopy(XRDataBuffer source, nint sourceOffset, XRDataBuffer destination, nint destinationOffset, nuint byteCount, string label)
        => _commandRuntime.TryEnqueueBufferCopy(_resourceRuntime.WrapperLookup, _frameOperationQueue, source, sourceOffset, destination, destinationOffset, byteCount, label, requireGpuWriteVisibility: false, diagnosticReceipt: null, RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex, CaptureFrameOpContextOrLastActive(), AllowSynchronousResourceUploads, IsDeviceLost);

    internal ERendererComputeEnqueueStatus TryEnqueueGpuBufferCopy(XRDataBuffer source, nint sourceOffset, XRDataBuffer destination, nint destinationOffset, nuint byteCount, string label)
        => _commandRuntime.TryEnqueueBufferCopy(_resourceRuntime.WrapperLookup, _frameOperationQueue, source, sourceOffset, destination, destinationOffset, byteCount, label, requireGpuWriteVisibility: true, diagnosticReceipt: null, RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex, CaptureFrameOpContextOrLastActive(), AllowSynchronousResourceUploads, IsDeviceLost);

    internal ERendererComputeEnqueueStatus TryEnqueueGpuDiagnosticBufferSnapshot(XRDataBuffer source, XRDataBuffer destination, nuint byteCount, string label)
        => TryEnqueueGpuDiagnosticBufferSnapshot(source, 0, destination, 0, byteCount, label);

    internal ERendererComputeEnqueueStatus TryEnqueueGpuDiagnosticBufferSnapshot(XRDataBuffer source, nuint sourceByteOffset, XRDataBuffer destination, nuint destinationByteOffset, nuint byteCount, string label)
    {
        GpuDiagnosticSnapshotReceipt receipt = GetOrCreateGpuDiagnosticSnapshotReceipt(destination);
        ERendererComputeEnqueueStatus status = _commandRuntime.TryEnqueueBufferCopy(
            _resourceRuntime.WrapperLookup,
            _frameOperationQueue,
            source,
            checked((nint)sourceByteOffset),
            destination,
            checked((nint)destinationByteOffset),
            byteCount,
            label,
            requireGpuWriteVisibility: true,
            receipt,
            RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex,
            CaptureFrameOpContextForCurrentPipelineScope(),
            AllowSynchronousResourceUploads,
            IsDeviceLost);
        if (status == ERendererComputeEnqueueStatus.Enqueued)
            receipt.RegisterCopy();
        return status;
    }

    internal ERendererComputeEnqueueStatus TryCompleteOrderedComputePass(EMemoryBarrierMask mask, string label)
        => _commandRuntime.TryEnqueueOrderedComputeBarrier(_frameOperationQueue, mask, label, RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex, CaptureFrameOpContextOrLastActive(), IsDeviceLost);

    internal XRGpuFence? InsertOrderedComputeFence(int requiredOperationCount = 0)
        => _commandRuntime.TryEnqueueOrderedComputeFence(
            _frameOperationQueue,
            RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex,
            CaptureFrameOpContextOrLastActive(),
            requiredOperationCount);

    internal bool TryDrawMeshTasksIndirectCount(XRRenderProgram program, XRDataBuffer indirect, XRDataBuffer count, uint maxDrawCount, uint stride, nuint byteOffset, nuint countByteOffset, out string failureReason)
        => _commandRuntime.TryEnqueueMeshTaskIndirectCount(_resourceRuntime.WrapperLookup, _resourceRuntime.Descriptors, _frameOperationQueue, program, indirect, count, maxDrawCount, stride, byteOffset, countByteOffset, RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex, CaptureFrameOpContextForCurrentPipelineScope(), AllowSynchronousResourceUploads, out failureReason);

    internal bool TryEnqueueDlssUpscale(int passIndex, IRuntimeVendorUpscaleSession session, XRTexture sourceColor, XRTexture depth, XRTexture motion, XRTexture outputColor, XRTexture? exposure, in VulkanUpscaleBridgeDispatchParameters parameters, out string failureReason)
        => VulkanUpscaleBridgeSidecar.TryEnqueueDlssUpscale(_resourceRuntime.WrapperLookup, _commandRuntime, _frameOperationQueue, passIndex, session, sourceColor, depth, motion, outputColor, exposure, parameters, CaptureFrameOpContextForCurrentPipelineScope(), out failureReason);

    internal bool TryEnqueueFrameGeneration(int passIndex, IRuntimeVendorUpscaleSession session, XRTexture depth, XRTexture motion, XRTexture hudlessColor, in VulkanUpscaleBridgeDispatchParameters parameters, out string failureReason)
        => VulkanUpscaleBridgeSidecar.TryEnqueueFrameGeneration(_resourceRuntime.WrapperLookup, _commandRuntime, _frameOperationQueue, passIndex, session, depth, motion, hudlessColor, parameters, CaptureFrameOpContextForCurrentPipelineScope(), out failureReason);

    internal bool TryDispatchFrameGeneration(XRViewport viewport, in VulkanUpscaleBridgeDispatchParameters parameters, XRTexture depth, XRTexture motion, XRTexture hudlessColor, out int errorCode, out string? errorMessage)
        => VulkanUpscaleBridgeSidecar.TryDispatchFrameGeneration(_resourceRuntime.WrapperLookup, viewport, parameters, depth, motion, hudlessColor, out errorCode, out errorMessage);
}
