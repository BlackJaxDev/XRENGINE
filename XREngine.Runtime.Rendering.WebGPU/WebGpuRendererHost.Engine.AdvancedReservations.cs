using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.WebGPU;

public sealed partial class WebGpuRendererHost
{
    // Every simultaneously admitted output may own a distinct canonical
    // publication, so the atomic frame must fit even without slot sharing.
    internal const int MaximumAdvancedOutputFamilies = (int)AdvancedFrameSlotContract.DefaultSlotCount;
    private static readonly string[] RequiredAdvancedPrograms =
    [
        "advanced::compact-triangles", "advanced::finalize-triangles", "advanced::visibility-pull",
        "advanced::aggregate-deformation", "advanced::deformation-copy",
        "advanced::uber-visibility", "advanced::uber-raster-surface",
        "advanced::shade-uber-native", "advanced::shade-uber-native-depth",
        "advanced::shade-uber-surface-exports", "advanced::shade-uber-surface-exports-depth",
        "advanced::depth-pyramid", "advanced::shade-classify",
        "advanced::shade-finalize", "advanced::shade-native", "advanced::shade-native-depth", "advanced::shade-background",
    ];
    private readonly WebGpuAdvancedOutputReservation?[] _advancedReservations = new WebGpuAdvancedOutputReservation[MaximumAdvancedOutputFamilies];
    private WebPipelineArtifactCatalog? _advancedPipelineArtifacts;
    private string? _advancedProgramFailure = "WebGPU.Advanced.CatalogMissing: the output package has not installed its native program family.";
    private string? _advancedAmbientOcclusionProgramFailure = "WebGPU.Advanced.AmbientOcclusionCatalogMissing: the GTAO program is not installed.";
    private bool _advancedReservationsInitialized;
    private long _advancedReservationIncarnation;
    private int _advancedReservationFailures;
    private uint _advancedCompletedSequence;

    /// <summary>Installs exact native programs from the owning package before output admission or recovery.</summary>
    public void BindAdvancedPipelineArtifacts(WebPipelineArtifactCatalog? artifacts)
    {
        ObjectDisposedException.ThrowIf(State == BrowserRendererState.Disposed, this);
        if (_engineRecording || _advancedReservationsInitialized)
            throw new InvalidOperationException("WebGPU.Advanced.CatalogActive: native programs must be installed before reserving an output.");
        string? failure = null;
        foreach (string binding in RequiredAdvancedPrograms)
        {
            if (artifacts is null || !artifacts.TryResolve(binding, out ShaderProgramArtifact? artifact))
            {
                failure = $"WebGPU.Advanced.ProgramMissing: the package requires '{binding}'.";
                break;
            }
            string pass = artifact.Pass;
            if (pass is "aggregate-deformation" or "deformation-copy")
                WebGpuAdvancedDeformationProgramContract.Validate(artifact, pass == "deformation-copy");
            else if (pass is "uber-visibility" or "uber-raster-surface")
                WebGpuAdvancedUberRasterProgramContract.Validate(artifact, pass);
            else if (pass is "compact-triangles" or "finalize-triangles" or "visibility-pull")
                WebGpuAdvancedVisibilityProgramContract.Validate(artifact, pass);
            else if (pass == "depth-pyramid")
                WebGpuAdvancedDepthProgramContract.Validate(artifact, ambientOcclusion: false);
            else
                WebGpuAdvancedShadingProgramContract.Validate(artifact, pass);
        }
        SetField(ref _advancedPipelineArtifacts, artifacts, publishNotifications: false);
        SetField(ref _advancedProgramFailure, failure, publishNotifications: false);
        SetField(ref _advancedAmbientOcclusionProgramFailure,
            ValidateAdvancedAmbientOcclusionProgram(artifacts), publishNotifications: false);
        SetField(ref _advancedMultisampleProgramFailure, ValidateAdvancedMultisamplePrograms(artifacts), publishNotifications: false);
    }

    private static string? ValidateAdvancedAmbientOcclusionProgram(WebPipelineArtifactCatalog? artifacts)
    {
        if (artifacts is null || !artifacts.TryResolve("advanced::gtao", out ShaderProgramArtifact? artifact))
            return "WebGPU.Advanced.AmbientOcclusionProgramMissing: enabled AO requires 'advanced::gtao'.";
        WebGpuAdvancedDepthProgramContract.Validate(artifact, ambientOcclusion: true);
        return null;
    }

    private string? GetAdvancedAmbientOcclusionRejection()
    {
        if (_advancedAmbientOcclusionProgramFailure is { } missing)
            return missing;
        if (!_advancedPipelineArtifacts!.TryResolve("advanced::gtao", out ShaderProgramArtifact? artifact))
            return "WebGPU.Advanced.AmbientOcclusionCatalogChanged: the selected GTAO program is missing.";
        foreach ((string limit, int required) in artifact.RequiredLimits)
            if (!HasAdvancedLimit(limit, required))
                return $"WebGPU.Advanced.AmbientOcclusionLimit: 'advanced::gtao' requires {limit}>={required}.";
        return null;
    }

    /// <summary>Admits only the installed single-view native family against the actual device limits.</summary>
    public override AdvancedVisibilityFamilyAdmission GetAdvancedVisibilityFamilyAdmission()
    {
        if (State != BrowserRendererState.Ready)
            return new(State == BrowserRendererState.Pending ? EAdvancedProductionExecutionState.PendingResources : EAdvancedProductionExecutionState.Unsupported,
                "WebGPU.Advanced.DevicePending: native visibility requires a ready device.");
        if (_advancedProgramFailure is { } missing)
            return new(EAdvancedProductionExecutionState.Unsupported, missing);
        if (!HasAdvancedLimit("maxColorAttachments", 3) || !HasAdvancedLimit("maxColorAttachmentBytesPerSample", 16) ||
            !HasAdvancedLimit("maxStorageBuffersPerShaderStage", 7) || !HasAdvancedLimit("maxStorageTexturesPerShaderStage", 4) ||
            !HasAdvancedLimit("maxSampledTexturesPerShaderStage", 16) || !HasAdvancedLimit("maxSamplersPerShaderStage", 12) ||
            !HasAdvancedLimit("maxBindGroups", 3) || !HasAdvancedLimit("maxBindingsPerBindGroup", 28) ||
            !HasAdvancedLimit("maxUniformBuffersPerShaderStage", 2) || !HasAdvancedLimit("maxDynamicUniformBuffersPerPipelineLayout", 2))
            return new(EAdvancedProductionExecutionState.Unsupported,
                "WebGPU.Advanced.DeviceLimits: native visibility requires 3 integer attachments, 7 storage buffers, 4 storage outputs, 16 sampled textures, 12 samplers, and 2 dynamic uniforms across 3 bind groups.");
        foreach (string binding in RequiredAdvancedPrograms)
        {
            ShaderProgramArtifact artifact = _advancedPipelineArtifacts!.TryResolve(binding, out ShaderProgramArtifact? found)
                ? found : throw new InvalidOperationException("The installed Advanced catalog changed after validation.");
            foreach ((string limit, int required) in artifact.RequiredLimits)
                if (!HasAdvancedLimit(limit, required))
                    return new(EAdvancedProductionExecutionState.Unsupported,
                        $"WebGPU.Advanced.DeviceLimit: '{binding}' requires {limit}>={required}.");
        }
        return new(EAdvancedProductionExecutionState.Admitted, "Ready");
    }

    public override bool TryReserveAdvancedVisibilityFamily(ulong outputId,
        out AdvancedVisibilityFamilyReservation reservation, out string failureReason)
    {
        reservation = default;
        AdvancedVisibilityFamilyAdmission admission = GetAdvancedVisibilityFamilyAdmission();
        failureReason = admission.Reason;
        if (!admission.IsAdmitted) return false;
        if (outputId == 0)
        {
            failureReason = "WebGPU.Advanced.OutputIdentityMissing: an output requires a stable nonzero identity.";
            return false;
        }
        for (int index = 0; index < _advancedReservations.Length; index++)
        {
            WebGpuAdvancedOutputReservation? bank = _advancedReservations[index];
            if (bank?.State == EAdvancedOutputReservationBankState.Active && bank.Reservation.OutputId == outputId)
            {
                reservation = bank.Reservation;
                return true;
            }
            if (bank?.State == EAdvancedOutputReservationBankState.Retiring && bank.Reservation.OutputId == outputId)
            {
                failureReason = "WebGPU.Advanced.OutputRetiring: the previous output incarnation remains queue-owned.";
                return false;
            }
        }
        for (int index = 0; index < _advancedReservations.Length; index++)
        {
            WebGpuAdvancedOutputReservation? bank = _advancedReservations[index];
            if (bank is not null && bank.State != EAdvancedOutputReservationBankState.Free) continue;
            bank ??= _advancedReservations[index] = new();
            bank.Reservation = reservation = new(BackendGeneration, outputId, checked((ulong)index + 1), checked(++_advancedReservationIncarnation));
            bank.State = EAdvancedOutputReservationBankState.Active;
            SetField(ref _advancedReservationsInitialized, true, publishNotifications: false);
            return true;
        }
        SetField(ref _advancedReservationFailures, _advancedReservationFailures + 1, publishNotifications: false);
        failureReason = "WebGPU.Advanced.OutputCapacity: all three native output banks remain owner- or queue-retained.";
        return false;
    }

    private WebGpuAdvancedOutputReservation? FindAdvancedReservation(in AdvancedVisibilityFamilyReservation reservation)
    {
        if (!reservation.IsValid || reservation.BackendGeneration != BackendGeneration ||
            reservation.ReservationId > (ulong)_advancedReservations.Length) return null;
        WebGpuAdvancedOutputReservation? bank = _advancedReservations[(int)reservation.ReservationId - 1];
        return bank?.Reservation == reservation ? bank : null;
    }

    public override bool IsAdvancedVisibilityFamilyReservationCurrent(in AdvancedVisibilityFamilyReservation reservation)
        => State == BrowserRendererState.Ready && FindAdvancedReservation(in reservation)?.State == EAdvancedOutputReservationBankState.Active;

    public override void ReleaseAdvancedVisibilityFamilyOwner(in AdvancedVisibilityFamilyReservation reservation)
    {
        WebGpuAdvancedOutputReservation? bank = FindAdvancedReservation(in reservation);
        if (bank is null || bank.State != EAdvancedOutputReservationBankState.Active) return;
        bank.State = EAdvancedOutputReservationBankState.Retiring;
        ReclaimAdvancedReservations(_advancedCompletedSequence);
    }

    private bool TryBeginAdvancedStage(in AdvancedVisibilityStageBackendRequest request, out string reason)
    {
        reason = request.GetInvalidReason() ?? string.Empty;
        if (reason.Length != 0) return false;
        AdvancedVisibilityFamilyReservation reservation = request.Reservation;
        WebGpuAdvancedOutputReservation? bank = FindAdvancedReservation(in reservation);
        XRRenderPipelineInstance? owner = RuntimeEngine.Rendering.State.CurrentRenderingPipeline;
        if (!_engineRecording || bank?.State != EAdvancedOutputReservationBankState.Active || owner?.Pipeline is null ||
            owner.AdvancedOutputBinding.Reservation != reservation || bank.Owner is not null && !ReferenceEquals(bank.Owner, owner))
        {
            reason = "WebGPU.Advanced.ReservationStale: the stage requires its current renderer, output owner, and recording reservation.";
            return false;
        }
        foreach (WebGpuAdvancedOutputReservation? retained in _advancedReservations)
        {
            if (retained is null || ReferenceEquals(retained, bank) || !ReferenceEquals(retained.Owner, owner)) continue;
            reason = "WebGPU.Advanced.OwnerRetained: the pipeline instance still belongs to another active or retiring output incarnation.";
            return false;
        }
        int nativeOffset = request.MsaaSampleCount == 4 ? 5 : 4;
        bool ambientOcclusion = request.AmbientOcclusionTargetName == AdvancedAmbientOcclusionContract.ResourceName;
        int operation = request.Stage switch
        {
            EAdvancedRenderStage.VisibilityPreparation => 0,
            EAdvancedRenderStage.VisibilityRaster => 1,
            EAdvancedRenderStage.DepthPyramidAndLateVisibility => request.Phase == EAdvancedVisibilityStageBackendPhase.LateCompute ? 2 :
                request.Phase == EAdvancedVisibilityStageBackendPhase.LateRaster ? 3 :
                request.MsaaSampleCount == 4 && request.Phase == EAdvancedVisibilityStageBackendPhase.MultisampleResolve ? 4 : -1,
            EAdvancedRenderStage.AmbientOcclusion => ambientOcclusion ? nativeOffset : -1,
            EAdvancedRenderStage.WorkClassification => nativeOffset + (ambientOcclusion ? 1 : 0),
            EAdvancedRenderStage.NativeOpaqueShading => nativeOffset + (ambientOcclusion ? 2 : 1),
            _ => -1,
        };
        if (operation == 0 && bank.RecordingSequence != _engineFrameSequence)
        {
            bank.Owner = owner;
            bank.RecordingSequence = _engineFrameSequence;
            bank.NextOperation = 0;
            bank.ResourceGeneration = owner.ResourceGeneration;
            bank.Request = request;
        }
        if (bank.RecordingSequence != _engineFrameSequence || operation != bank.NextOperation ||
            owner.ResourceGeneration != bank.ResourceGeneration || request.Publication != bank.Request.Publication ||
            request.RenderFrameId != bank.Request.RenderFrameId || !request.Views.Equals(bank.Request.Views) ||
            !ReferenceEquals(request.BackendReadyPackage, bank.Request.BackendReadyPackage) ||
            request.MsaaSampleCount != bank.Request.MsaaSampleCount || request.SampleEncoding != bank.Request.SampleEncoding ||
            request.EnableBuiltInAmbientOcclusion != bank.Request.EnableBuiltInAmbientOcclusion ||
            request.AmbientOcclusionTargetName != bank.Request.AmbientOcclusionTargetName ||
            !IsAdvancedStageTargetCurrent(owner, in request))
        {
            reason = "WebGPU.Advanced.FamilyChanged: the stage must preserve its frozen publication, view, output generation, and native stage order.";
            return false;
        }
        return true;
    }

    private void CompleteAdvancedStage(in AdvancedVisibilityStageBackendRequest request)
    {
        AdvancedVisibilityFamilyReservation reservation = request.Reservation;
        FindAdvancedReservation(in reservation)!.NextOperation++;
    }

    private bool AreAdvancedFamiliesComplete()
    {
        XRRenderPipelineInstance? primary = _engineViewport?.RenderPipelineInstance;
        bool primaryComplete = primary?.Pipeline is not IAdvancedRenderStageFamilyHost { UsesAdvancedStageFamily: true };
        foreach (WebGpuAdvancedOutputReservation? bank in _advancedReservations)
        {
            if (bank?.RecordingSequence == _engineFrameSequence && bank.NextOperation !=
                (bank.Request.IsMinimalVisibilityOutput ? 4 : 6 +
                    (bank.Request.AmbientOcclusionTargetName == AdvancedAmbientOcclusionContract.ResourceName ? 1 : 0)) +
                (bank.Request.MsaaSampleCount == 4 ? 1 : 0))
                return false;
            if (bank?.RecordingSequence == _engineFrameSequence && ReferenceEquals(bank.Owner, primary))
                primaryComplete = true;
        }
        return primaryComplete;
    }

    private void EndAdvancedReservationRecording(bool submitted)
    {
        foreach (WebGpuAdvancedOutputReservation? bank in _advancedReservations)
        {
            if (bank?.RecordingSequence != _engineFrameSequence) continue;
            if (submitted) bank.SubmittedSequence = _engineFrameSequence;
            bank.RecordingSequence = 0;
            bank.Request = default;
        }
        ReclaimAdvancedReservations(_advancedCompletedSequence);
    }

    private void ReclaimAdvancedReservations(uint completedSequence)
    {
        SetField(ref _advancedCompletedSequence, completedSequence, publishNotifications: false);
        foreach (WebGpuAdvancedOutputReservation? bank in _advancedReservations)
        {
            if (bank is null) continue;
            if (bank.SubmittedSequence <= completedSequence) bank.SubmittedSequence = 0;
            if (bank.State != EAdvancedOutputReservationBankState.Retiring || bank.RecordingSequence != 0 || bank.SubmittedSequence != 0) continue;
            if (bank.Owner is { } owner) ReleaseAdvancedVisibilityOutput(owner);
            bank.Owner = null;
            bank.Reservation = default;
            bank.State = EAdvancedOutputReservationBankState.Free;
        }
    }

    private void ClearAdvancedReservations()
    {
        Array.Clear(_advancedReservations);
        SetField(ref _advancedReservationsInitialized, false, publishNotifications: false);
    }

    public override AdvancedOutputReservationDiagnosticsSnapshot CaptureAdvancedOutputReservationDiagnostics()
    {
        AdvancedOutputReservationBankDiagnostic[] diagnostics = new AdvancedOutputReservationBankDiagnostic[MaximumAdvancedOutputFamilies];
        int active = 0, retiring = 0, recorded = 0;
        for (int index = 0; index < diagnostics.Length; index++)
        {
            WebGpuAdvancedOutputReservation? bank = _advancedReservations[index];
            if (bank is null) continue;
            if (bank.State == EAdvancedOutputReservationBankState.Active) active++;
            if (bank.State == EAdvancedOutputReservationBankState.Retiring) retiring++;
            if (bank.RecordingSequence != 0) recorded++;
            diagnostics[index] = new(bank.Reservation.OutputId, bank.Reservation.ReservationId, bank.Reservation.BankIncarnation,
                bank.State, 0, bank.RecordingSequence != 0 ? 1 : 0, bank.SubmittedSequence != 0 ? 1 : 0, 0, null);
        }
        return new(BackendGeneration, MaximumAdvancedOutputFamilies, active, retiring,
            MaximumAdvancedOutputFamilies - active - retiring, _advancedReservationFailures, 0,
            MaximumAdvancedOutputFamilies, recorded, diagnostics);
    }
}
