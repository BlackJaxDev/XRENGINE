using Silk.NET.OpenGL;

namespace XREngine.Rendering.OpenGL;

/// <summary>Immediate GL authoring authority for one sealed Advanced family.</summary>
public partial class OpenGLRenderer : IAdvancedVisibilityStageBackendCapability
{
    private bool _advancedAdmissionReady;
    private bool _advancedLimitsLogged;
    private bool _advancedFamilyActive;
    private AdvancedVisibilityStageBackendRequest _advancedActiveRequest;
    private int _advancedNextOperation;

    public override AdvancedVisibilityFamilyAdmission GetAdvancedVisibilityFamilyAdmission()
    {
        if (!RuntimeEngine.IsRenderThread)
            return _advancedAdmissionReady
                ? new(EAdvancedProductionExecutionState.Admitted, "Ready")
                : new(EAdvancedProductionExecutionState.PendingResources, "OpenGL Advanced admission requires its current render context.");
        _advancedAdmissionReady = false;
        if (!_advancedLimitsLogged)
        {
            _advancedLimitsLogged = true;
            Debug.Rendering($"[OpenGL Advanced] Storage block limits: compute={RawGL.GetInteger(GLEnum.MaxComputeShaderStorageBlocks)}, vertex={RawGL.GetInteger(GLEnum.MaxVertexShaderStorageBlocks)}, fragment={RawGL.GetInteger(GLEnum.MaxFragmentShaderStorageBlocks)}.");
        }
        if (!SupportsIndirectCountDraw() || !SupportsBindlessTextureHandles)
            return new(EAdvancedProductionExecutionState.Unsupported, "OpenGL Advanced requires indirect-count submission and bindless texture/sampler handles.");
        if (RawGL.GetInteger(GLEnum.MaxShaderStorageBufferBindings) < 90 ||
            RawGL.GetInteger(GLEnum.MaxComputeShaderStorageBlocks) < 15 ||
            RawGL.GetInteger(GLEnum.MaxVertexShaderStorageBlocks) < 8 ||
            RawGL.GetInteger(GLEnum.MaxFragmentShaderStorageBlocks) < 2 ||
            RawGL.GetInteger(GLEnum.MaxUniformBufferBindings) < 3 ||
            RawGL.GetInteger(GLEnum.MaxImageUnits) < 6 ||
            RawGL.GetInteger(GLEnum.MaxComputeTextureImageUnits) < 5)
            return new(EAdvancedProductionExecutionState.Unsupported, "OpenGL Advanced requires 90 SSBO bindings, 15 compute/8 vertex/2 fragment storage blocks, 3 UBO bindings, 6 image units, and 5 compute texture units.");
        if (!TryEnsureAdvancedRuntime(out string reason) || !TryEnsureAdvancedStagePrograms(out reason))
            return new(EAdvancedProductionExecutionState.PendingResources, reason);
        _advancedAdmissionReady = true;
        return new(EAdvancedProductionExecutionState.Admitted, "Ready");
    }

    public override bool TryReserveAdvancedVisibilityFamily(ulong outputId, out AdvancedVisibilityFamilyReservation reservation, out string failureReason)
    {
        reservation = default;
        AdvancedVisibilityFamilyAdmission admission = GetAdvancedVisibilityFamilyAdmission();
        failureReason = admission.Reason;
        return admission.IsAdmitted && _advancedOutputRegistry!.TryReserve(outputId, 1, out reservation, out failureReason);
    }

    public override bool IsAdvancedVisibilityFamilyReservationCurrent(in AdvancedVisibilityFamilyReservation reservation)
        => _advancedOutputRegistry?.IsCurrent(in reservation) == true;

    public override void ReleaseAdvancedVisibilityFamilyOwner(in AdvancedVisibilityFamilyReservation reservation)
        => _advancedOutputRegistry?.Retire(in reservation);

    public bool SupportsAdvancedVisibilityStage(EAdvancedRenderStage stage)
        => _advancedAdmissionReady && stage is (EAdvancedRenderStage.VisibilityPreparation or
            EAdvancedRenderStage.VisibilityRaster or EAdvancedRenderStage.DepthPyramidAndLateVisibility or
            EAdvancedRenderStage.AmbientOcclusion or EAdvancedRenderStage.WorkClassification or EAdvancedRenderStage.NativeOpaqueShading);

    public bool TryEnqueueAdvancedVisibilityStage(in AdvancedVisibilityStageBackendRequest request, out string failureReason)
    {
        if (RuntimeEngine.IsRenderThread)
            _advancedOutputRegistry?.PollCompletedPublications();
        failureReason = request.GetInvalidReason() ?? string.Empty;
        if (failureReason.Length != 0) return false;
        AdvancedVisibilityFamilyReservation reservation = request.Reservation;
        if (!RuntimeEngine.IsRenderThread || !SupportsAdvancedVisibilityStage(request.Stage) ||
            !IsAdvancedVisibilityFamilyReservationCurrent(in reservation))
        {
            failureReason = "The OpenGL Advanced stage has no current admitted renderer reservation.";
            return false;
        }
        for (int view = 0; view < request.Views.ViewCount; view++)
            if (request.Views.GetView(view).OutputLayer != (uint)view)
            {
                failureReason = "OpenGL Advanced requires contiguous physical layers in canonical view order.";
                return false;
            }
        int ordinal = request.Stage switch
        {
            EAdvancedRenderStage.VisibilityPreparation => 0,
            EAdvancedRenderStage.VisibilityRaster => 1,
            EAdvancedRenderStage.DepthPyramidAndLateVisibility => request.Phase == EAdvancedVisibilityStageBackendPhase.LateCompute ? 2 : 3,
            EAdvancedRenderStage.AmbientOcclusion => 4 + (int)request.NativeViewIndex,
            EAdvancedRenderStage.WorkClassification => 4 + request.Views.ViewCount + (int)request.NativeViewIndex,
            EAdvancedRenderStage.NativeOpaqueShading => 4 + 2 * request.Views.ViewCount + (int)request.NativeViewIndex,
            _ => -1,
        };
        if (ordinal == 0)
        {
            if (_advancedFamilyActive)
            {
                AbortAdvancedVisibilityFamily();
                failureReason = "The previous OpenGL Advanced family ended before all required stages were authored.";
                return false;
            }
            _advancedFamilyActive = true;
            _advancedActiveRequest = request;
            _advancedNextOperation = 0;
        }
        if (!_advancedFamilyActive || ordinal != _advancedNextOperation ||
            request.Reservation != _advancedActiveRequest.Reservation || request.Publication != _advancedActiveRequest.Publication ||
            request.RenderFrameId != _advancedActiveRequest.RenderFrameId || !request.Views.Equals(_advancedActiveRequest.Views) ||
            !ReferenceEquals(request.BackendReadyPackage, _advancedActiveRequest.BackendReadyPackage))
        {
            failureReason = "The OpenGL Advanced stage does not match its sealed family or required per-view order.";
            AbortAdvancedVisibilityFamily();
            return false;
        }
        try
        {
            bool accepted = request.Stage switch
            {
                EAdvancedRenderStage.VisibilityPreparation => TryPrepareAdvancedVisibility(in request, out failureReason),
                EAdvancedRenderStage.VisibilityRaster => TryRasterAdvancedVisibility(in request, false, out failureReason),
                EAdvancedRenderStage.DepthPyramidAndLateVisibility when request.Phase == EAdvancedVisibilityStageBackendPhase.LateCompute
                    => TryDispatchAdvancedLateVisibility(in request, out failureReason),
                EAdvancedRenderStage.DepthPyramidAndLateVisibility => TryRasterAdvancedVisibility(in request, true, out failureReason),
                _ => TryDispatchAdvancedNativeStage(in request, out failureReason),
            };
            if (!accepted) { AbortAdvancedVisibilityFamily(); return false; }
            _advancedNextOperation++;
            int requiredOperations = request.IsMinimalVisibilityOutput ? 4 : 4 + 3 * request.Views.ViewCount;
            if (_advancedNextOperation == requiredOperations)
            {
                bool completed = TryCompleteAdvancedVisibilityFamily(in request, out failureReason);
                _advancedFamilyActive = false;
                return completed;
            }
            return true;
        }
        catch
        {
            AbortAdvancedVisibilityFamily();
            throw;
        }
    }

    private void AbortAdvancedVisibilityFamily()
    {
        if (!_advancedFamilyActive) return;
        _advancedFamilyActive = false;
        AdvancedVisibilityFamilyReservation reservation = _advancedActiveRequest.Reservation;
        if (_advancedOutputRegistry is not null && _advancedOutputRegistry.TryGetRetainedSlot(in reservation, out var slot) && slot is not null)
        {
            // Even a rejected family may already have authored GL work. Protect
            // every input/output allocation before permitting another producer.
            slot.MarkSubmitted(this);
            _advancedOutputRegistry.Complete(in reservation);
        }
    }
}
