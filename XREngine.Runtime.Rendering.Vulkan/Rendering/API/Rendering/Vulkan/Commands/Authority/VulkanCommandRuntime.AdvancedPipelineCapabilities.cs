using XREngine.Rendering.RenderGraph;
using System.Diagnostics.CodeAnalysis;

namespace XREngine.Rendering.Vulkan;

/// <summary>Command and descriptor capability synthesis for advanced pipeline selection.</summary>
internal sealed partial class VulkanCommandRuntime
{
    private readonly object _advancedVisibilityReservationGate = new();
    private readonly AdvancedVisibilityOutputBank[] _advancedVisibilityOutputBanks =
        CreateAdvancedVisibilityOutputBanks();
    private long _advancedVisibilityReservationGeneration;
    internal Action<int>? ProvisionAdvancedVisibilityWorkspace { private get; set; }

    // Promotion is deliberately coupled to a live reservation. Capability
    // snapshots remain advisory and cannot independently select a family.
    internal AdvancedVisibilityFamilyAdmission GetAdvancedVisibilityFamilyAdmission()
    {
        VulkanAdvancedVisibilityPipelineReadiness readiness =
            GetAdvancedVisibilityPipelineReadiness(out string reason);
        EAdvancedProductionExecutionState state = readiness switch
        {
            VulkanAdvancedVisibilityPipelineReadiness.Ready => EAdvancedProductionExecutionState.Admitted,
            VulkanAdvancedVisibilityPipelineReadiness.Pending or VulkanAdvancedVisibilityPipelineReadiness.Missing => EAdvancedProductionExecutionState.PendingResources,
            _ => EAdvancedProductionExecutionState.Unsupported,
        };
        return new(state, reason);
    }

    internal bool IsAdvancedVisibilityProductionPromoted
        => Volatile.Read(ref _advancedVisibilityReservationGeneration) != 0 &&
           CanAdmitAdvancedVisibilityFamily();

    internal bool TryReserveAdvancedVisibilityFamily(
        ulong outputId,
        out AdvancedVisibilityFamilyReservation reservation,
        out string failureReason)
    {
        reservation = default;
        if (outputId == 0)
        {
            failureReason = "Advanced visibility requires a non-zero stable output identity.";
            return false;
        }
        VulkanAdvancedVisibilityPipelineReadiness readiness =
            GetAdvancedVisibilityPipelineReadiness(out failureReason);
        if (readiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
        {
            failureReason = $"Advanced visibility pipeline admission is {readiness}: {failureReason}";
            return false;
        }

        // A previous generation may have become provably complete since its
        // last admission attempt. This never waits; incomplete work remains
        // Retiring and keeps capacity unavailable.
        TryFinalizeRetiringAdvancedVisibilityBanks();

        lock (ResourceRuntime.AdvancedVisibilityStorageGate)
        lock (_advancedVisibilityReservationGate)
        {
            long generation = checked((long)(ResourceRuntime.FrameDataArena?.Generation ?? 0UL));
            if (generation <= 0)
            {
                failureReason = "The Vulkan advanced visibility frame-storage generation is unavailable.";
                return false;
            }
            if (_advancedVisibilityReservationGeneration != generation)
            {
                if (!TryRetireAdvancedVisibilityGenerationNoLock(out failureReason))
                    return false;
                _advancedVisibilityReservationGeneration = generation;
            }
            int index = FindActiveAdvancedVisibilityBankNoLock(outputId);
            if (index >= 0)
            {
                AdvancedVisibilityOutputBank existing = _advancedVisibilityOutputBanks[index];
                reservation = new(generation, outputId, checked((ulong)index + 1), existing.BankIncarnation);
                failureReason = "Ready";
                return true;
            }
            if (index < 0)
                index = FindFreeAdvancedVisibilityBankNoLock();
            if (index < 0)
            {
                failureReason = $"This Vulkan renderer generation has reached its bounded {_advancedVisibilityOutputBanks.Length}-output Advanced visibility capacity while existing output banks are active or retiring.";
                return false;
            }

            ulong reservationId = checked((ulong)index + 1);
            AdvancedVisibilityOutputBank bank = _advancedVisibilityOutputBanks[index];
            if (bank.BankIncarnation == long.MaxValue)
            {
                failureReason = "Advanced output-bank incarnation capacity is exhausted; the renderer generation must retire before this bank can be reused.";
                return false;
            }
            long incarnation = bank.BankIncarnation + 1;
            AdvancedVisibilityFamilyReservation pendingReservation = new(
                generation, outputId, reservationId, incarnation);
            // Complete every cold workspace before a reservation becomes visible.
            // A failed activation leaves only dormant, idempotently reusable slots.
            bool measureColdActivation = !bank.HasMeasuredColdActivation;
            bool activationCompleted = false;
            long activationStart = measureColdActivation
                ? GC.GetAllocatedBytesForCurrentThread()
                : 0;
            try
            {
                if (!ResourceRuntime.TryInitializeAdvancedVisibilityOutput(
                        in pendingReservation, DeviceContext, out failureReason))
                    return false;
                if (ProvisionAdvancedVisibilityWorkspace is not { } provisionWorkspace)
                {
                    failureReason = "Advanced output workspace activation is not configured.";
                    return false;
                }
                provisionWorkspace(index);
                activationCompleted = true;
            }
            catch (OutOfMemoryException ex)
            {
                bank.ActivationFailureCount++;
                bank.LastActivationFailure = ex.Message;
                failureReason = "Advanced output workspace activation ran out of managed memory.";
                return false;
            }
            finally
            {
                if (measureColdActivation)
                {
                    bank.ManagedActivationBytes += Math.Max(
                        0,
                        GC.GetAllocatedBytesForCurrentThread() - activationStart);
                    if (activationCompleted)
                        bank.HasMeasuredColdActivation = true;
                }
            }
            bank.OutputId = outputId;
            bank.BackendGeneration = generation;
            bank.BankIncarnation = incarnation;
            bank.State = EAdvancedOutputReservationBankState.Active;
            bank.LastActivationFailure = null;
            reservation = pendingReservation;
            failureReason = "Ready";
            return true;
        }
    }

    internal bool IsAdvancedVisibilityReservationCurrent(
        in AdvancedVisibilityFamilyReservation reservation)
    {
        if (!reservation.IsValid)
            return false;
        long generation = checked((long)(ResourceRuntime.FrameDataArena?.Generation ?? 0UL));
        if (generation <= 0)
            return false;
        lock (_advancedVisibilityReservationGate)
            return generation == _advancedVisibilityReservationGeneration &&
                TryGetCurrentAdvancedVisibilityBankNoLock(in reservation, out _);
    }

    /// <summary>
    /// Accepts only exact sealed-plan work that retained its bank before the
    /// viewport owner retired. Retiring banks cannot admit new plans.
    /// </summary>
    internal bool IsAdvancedVisibilityReservationConsumable(
        in AdvancedVisibilityFamilyReservation reservation)
    {
        if (!reservation.IsValid)
            return false;
        lock (_advancedVisibilityReservationGate)
            return TryGetConsumableAdvancedVisibilityBankNoLock(in reservation, out _);
    }

    internal bool TryAcquireAdvancedVisibilityPlanLease(
        in AdvancedVisibilityFamilyReservation reservation)
    {
        long generation = checked((long)(ResourceRuntime.FrameDataArena?.Generation ?? 0UL));
        if (generation <= 0 || reservation.BackendGeneration != generation)
            return false;
        lock (_advancedVisibilityReservationGate)
        {
            if (generation != _advancedVisibilityReservationGeneration ||
                !TryGetCurrentAdvancedVisibilityBankNoLock(in reservation, out AdvancedVisibilityOutputBank? bank))
                return false;
            bank.PlanLeaseCount++;
            return true;
        }
    }

    internal void ReleaseAdvancedVisibilityPlanLease(
        in AdvancedVisibilityFamilyReservation reservation)
    {
        lock (_advancedVisibilityReservationGate)
        {
            if (!TryGetExactAdvancedVisibilityBankNoLock(in reservation, out AdvancedVisibilityOutputBank? bank) ||
                bank.PlanLeaseCount <= 0)
                throw new InvalidOperationException("Advanced output-bank plan lease underflow or stale token.");
            bank.PlanLeaseCount--;
            TryFinalizeRetiringAdvancedVisibilityBankNoLock(bank);
        }
        TryFinalizeRetiringAdvancedVisibilityBanks();
    }

    internal void ReleaseAdvancedVisibilityFamilyOwner(
        in AdvancedVisibilityFamilyReservation reservation)
    {
        if (!reservation.IsValid)
            return;
        lock (_advancedVisibilityReservationGate)
        {
            if (!TryGetExactAdvancedVisibilityBankNoLock(in reservation, out AdvancedVisibilityOutputBank? bank) ||
                bank.State != EAdvancedOutputReservationBankState.Active)
            {
                return;
            }
            bank.State = EAdvancedOutputReservationBankState.Retiring;
            TryFinalizeRetiringAdvancedVisibilityBankNoLock(bank);
        }
        TryFinalizeRetiringAdvancedVisibilityBanks();
    }

    internal AdvancedOutputReservationDiagnosticsSnapshot CaptureAdvancedOutputReservationDiagnostics()
    {
        CaptureAdvancedVisibilityRecordedLeaseOccupancy(
            out int recordedCommandCapacity,
            out int recordedCommandCount);
        lock (_advancedVisibilityReservationGate)
        {
            AdvancedOutputReservationBankDiagnostic[] banks = new AdvancedOutputReservationBankDiagnostic[_advancedVisibilityOutputBanks.Length];
            int active = 0;
            int retiring = 0;
            int free = 0;
            int failures = 0;
            long allocationBytes = 0;
            for (int index = 0; index < _advancedVisibilityOutputBanks.Length; index++)
            {
                AdvancedVisibilityOutputBank bank = _advancedVisibilityOutputBanks[index];
                switch (bank.State)
                {
                    case EAdvancedOutputReservationBankState.Active: active++; break;
                    case EAdvancedOutputReservationBankState.Retiring: retiring++; break;
                    default: free++; break;
                }
                failures += bank.ActivationFailureCount;
                allocationBytes += bank.ManagedActivationBytes;
                banks[index] = new(bank.OutputId, (ulong)index + 1, bank.BankIncarnation,
                    bank.State, bank.PlanLeaseCount, bank.RecordedCommandBufferLeaseCount,
                    bank.PendingQueueDomainCount,
                    bank.ManagedActivationBytes, bank.LastActivationFailure);
            }
            return new(_advancedVisibilityReservationGeneration, banks.Length, active, retiring,
                free, failures, allocationBytes, recordedCommandCapacity, recordedCommandCount, banks);
        }
    }

    private bool TryRetireAdvancedVisibilityGenerationNoLock(out string failureReason)
    {
        for (int index = 0; index < _advancedVisibilityOutputBanks.Length; index++)
        {
            AdvancedVisibilityOutputBank bank = _advancedVisibilityOutputBanks[index];
            if (bank.State == EAdvancedOutputReservationBankState.Active)
                bank.State = EAdvancedOutputReservationBankState.Retiring;
            TryFinalizeRetiringAdvancedVisibilityBankNoLock(bank);
            if (bank.State != EAdvancedOutputReservationBankState.Free)
            {
                failureReason = "An Advanced output-bank generation is still leased by a sealed plan or pending GPU work.";
                return false;
            }
        }
        failureReason = string.Empty;
        return true;
    }

    private static AdvancedVisibilityOutputBank[] CreateAdvancedVisibilityOutputBanks()
    {
        AdvancedVisibilityOutputBank[] banks = new AdvancedVisibilityOutputBank[VulkanAdvancedVisibilityOutputCapacity.Maximum];
        for (int index = 0; index < banks.Length; index++)
            banks[index] = new();
        return banks;
    }

    private int FindActiveAdvancedVisibilityBankNoLock(ulong outputId)
    {
        for (int index = 0; index < _advancedVisibilityOutputBanks.Length; index++)
            if (_advancedVisibilityOutputBanks[index].State == EAdvancedOutputReservationBankState.Active &&
                _advancedVisibilityOutputBanks[index].OutputId == outputId)
                return index;
        return -1;
    }

    private int FindFreeAdvancedVisibilityBankNoLock()
    {
        for (int index = 0; index < _advancedVisibilityOutputBanks.Length; index++)
            if (_advancedVisibilityOutputBanks[index].State == EAdvancedOutputReservationBankState.Free)
                return index;
        return -1;
    }

    private bool TryGetCurrentAdvancedVisibilityBankNoLock(
        in AdvancedVisibilityFamilyReservation reservation,
        [NotNullWhen(true)] out AdvancedVisibilityOutputBank? bank)
    {
        if (!TryGetExactAdvancedVisibilityBankNoLock(in reservation, out bank))
            return false;
        return bank.State == EAdvancedOutputReservationBankState.Active &&
            bank.BackendGeneration == _advancedVisibilityReservationGeneration;
    }

    private bool TryGetConsumableAdvancedVisibilityBankNoLock(
        in AdvancedVisibilityFamilyReservation reservation,
        [NotNullWhen(true)] out AdvancedVisibilityOutputBank? bank)
    {
        if (!TryGetExactAdvancedVisibilityBankNoLock(in reservation, out bank))
            return false;
        return bank.State != EAdvancedOutputReservationBankState.Free &&
            bank.PlanLeaseCount != 0;
    }

    private bool TryGetExactAdvancedVisibilityBankNoLock(
        in AdvancedVisibilityFamilyReservation reservation,
        [NotNullWhen(true)] out AdvancedVisibilityOutputBank? bank)
    {
        bank = null;
        if (!reservation.IsValid || reservation.ReservationId > (ulong)_advancedVisibilityOutputBanks.Length)
            return false;
        AdvancedVisibilityOutputBank candidate = _advancedVisibilityOutputBanks[checked((int)reservation.ReservationId - 1)];
        if (candidate.OutputId != reservation.OutputId ||
            candidate.BackendGeneration != reservation.BackendGeneration ||
            candidate.BankIncarnation != reservation.BankIncarnation)
            return false;
        bank = candidate;
        return true;
    }

    private static void TryFinalizeRetiringAdvancedVisibilityBankNoLock(AdvancedVisibilityOutputBank bank)
    {
        if (bank.State != EAdvancedOutputReservationBankState.Retiring ||
            bank.PlanLeaseCount != 0 || bank.RecordedCommandBufferLeaseCount != 0 ||
            bank.PendingQueueDomainCount != 0)
            return;
        bank.OutputId = 0;
        bank.BackendGeneration = 0;
        bank.State = EAdvancedOutputReservationBankState.Free;
    }

    private static void TryFinalizeRetiringAdvancedVisibilityBankNoLock(
        AdvancedVisibilityOutputBank bank,
        ulong completedGraphics,
        ulong completedTransfer,
        ulong completedOther)
    {
        if (bank.State != EAdvancedOutputReservationBankState.Retiring ||
            bank.PlanLeaseCount != 0 || bank.RecordedCommandBufferLeaseCount != 0)
            return;
        ClearCompletedAdvancedVisibilityWatermark(
            ref bank.GraphicsCompletionWatermark, completedGraphics, bank);
        ClearCompletedAdvancedVisibilityWatermark(
            ref bank.TransferCompletionWatermark, completedTransfer, bank);
        ClearCompletedAdvancedVisibilityWatermark(
            ref bank.OtherCompletionWatermark, completedOther, bank);
        TryFinalizeRetiringAdvancedVisibilityBankNoLock(bank);
    }

    private static void ClearCompletedAdvancedVisibilityWatermark(
        ref long watermark,
        ulong completed,
        AdvancedVisibilityOutputBank bank)
    {
        long observed = Volatile.Read(ref watermark);
        if (observed == 0 || unchecked((ulong)observed) > completed ||
            Interlocked.CompareExchange(ref watermark, 0, observed) != observed)
            return;
        Interlocked.Decrement(ref bank.PendingQueueDomainCount);
    }

    internal AdvancedRenderPipelineCapabilities GetAdvancedRenderPipelineCapabilities()
    {
        EAdvancedIndirectSubmissionMode indirectSubmission = DeviceContext.SupportsMeshTaskIndirectCount
            ? EAdvancedIndirectSubmissionMode.MeshTasksIndirectCount
            : DeviceContext.Capabilities.Supports(EVulkanDeviceCapability.DrawIndirectCount)
                ? EAdvancedIndirectSubmissionMode.MultiDrawIndirectCount
                : EAdvancedIndirectSubmissionMode.MultiDrawIndirect;
        VulkanAdvancedSceneResourceRuntime advancedResources =
            ResourceRuntime.AdvancedSceneResources;
        bool supportsAdvancedFrameStorage = advancedResources.IsReady &&
            ResourceRuntime.FrameDataArena is { IsActive: true };
        EAdvancedTextureIndirectionMode textureIndirection =
            advancedResources.TextureIndirectionMode;
        return new(
            RuntimeGraphicsApiKind.Vulkan, true, true, EAdvancedVisibilityTargetEncoding.R32G32UInt,
            SupportsOrderedComputeWork, true, indirectSubmission, textureIndirection,
            DeviceContext.SupportsSynchronization2 ? EAdvancedSynchronizationMode.VulkanSynchronization2 : EAdvancedSynchronizationMode.VulkanLegacyBarriers,
            supportsAdvancedFrameStorage, DeviceContext.AdvancedMultiviewEnabled,
            // Global capability snapshots have no output reservation. Only
            // a live output-bank reservation may promote the shader family.
            EAdvancedShaderFamily.None,
            DeviceContext.SupportsBufferDeviceAddress,
            advancedResources.IsReady,
            false, false, DeviceContext.SupportsMeshTaskIndirectCount,
            false, DeviceContext.Capabilities.Supports(EVulkanDeviceCapability.TimelineSemaphores));
    }

    internal bool CanAdmitAdvancedVisibilityFamily()
        => GetAdvancedVisibilityPipelineReadiness(out _) ==
           VulkanAdvancedVisibilityPipelineReadiness.Ready;

    internal VulkanAdvancedVisibilityPipelineReadiness GetAdvancedVisibilityPipelineReadiness(
        out string failureReason)
    {
        if (!DeviceContext.IsOperational)
        {
            failureReason = "The Vulkan device is not operational.";
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }
        if (!DeviceContext.Capabilities.Supports(EVulkanDeviceCapability.DrawIndirectCount))
        {
            failureReason = "The Vulkan device does not support indirect-count draws.";
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }
        if (!ResourceRuntime.AdvancedSceneResources.IsReady)
        {
            failureReason = ResourceRuntime.AdvancedSceneResources.AvailabilityReason;
            return VulkanAdvancedVisibilityPipelineReadiness.Missing;
        }
        if (!ResourceRuntime.AdvancedVisibilityResources.IsReady)
        {
            failureReason = ResourceRuntime.AdvancedVisibilityResources.AvailabilityReason;
            return VulkanAdvancedVisibilityPipelineReadiness.Missing;
        }

        // Target-specific image/view closure is sealed against the accepted
        // frame plan. Capability synthesis covers only device/runtime support;
        // it must not allocate or intern per-frame image views.
        using VulkanProgramLinkPreparationScope programPreparation =
            new(ResourceRuntime);
        VulkanAdvancedVisibilityPipelineRuntime pipelines =
            ResourceRuntime.AdvancedVisibilityPipelines;
        VulkanAdvancedVisibilityPipelineReadiness computeReadiness =
            pipelines.TryGetComputePipelines(out _, out _, out failureReason);
        if (computeReadiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
            return computeReadiness;

        VulkanAdvancedVisibilityPipelineReadiness lateComputeReadiness =
            pipelines.TryGetLateVisibilityComputePipelines(out _, out _, out failureReason);
        if (lateComputeReadiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
            return lateComputeReadiness;

        VulkanAdvancedVisibilityPipelineReadiness nativeComputeReadiness =
            pipelines.TryGetNativeComputePipelines(out _, out failureReason);
        if (nativeComputeReadiness != VulkanAdvancedVisibilityPipelineReadiness.Ready)
            return nativeComputeReadiness;

        if (!pipelines.TryGetRasterProgram(
                EAdvancedMaterialCoverageMode.Opaque,
                meshlet: false,
                out _,
                out failureReason) ||
            !pipelines.TryGetRasterProgram(
                EAdvancedMaterialCoverageMode.Masked,
                meshlet: false,
                out _,
                out failureReason))
        {
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }

        if (DeviceContext.SupportsMeshTaskIndirectCount &&
            (!pipelines.TryGetRasterProgram(
                EAdvancedMaterialCoverageMode.Opaque,
                meshlet: true,
                out _,
                out failureReason) ||
             !pipelines.TryGetRasterProgram(
                EAdvancedMaterialCoverageMode.Masked,
                meshlet: true,
                out _,
                out failureReason)))
        {
            return VulkanAdvancedVisibilityPipelineReadiness.Failed;
        }

        failureReason = "Ready";
        return VulkanAdvancedVisibilityPipelineReadiness.Ready;
    }

    internal ERvcDescriptorBackend RvcDescriptorBackend => ResourceRuntime.Descriptors.ActiveDescriptorBackend switch
    {
        EVulkanDescriptorBackend.DescriptorHeap => ERvcDescriptorBackend.DescriptorHeap,
        EVulkanDescriptorBackend.DescriptorIndexing => ERvcDescriptorBackend.DescriptorIndexing,
        _ => ERvcDescriptorBackend.None,
    };

    internal bool SupportsRvcMaterialResourceTable => RvcDescriptorBackend != ERvcDescriptorBackend.None;
    internal bool SupportsRvcVisibilityTargets =>
        DeviceContext.SupportsDynamicRendering &&
        DeviceContext.SupportsSynchronization2 &&
        DeviceContext.SupportsFragmentStoresAndAtomics &&
        DeviceContext.SupportsVertexPipelineStoresAndAtomics &&
        SupportsRvcMaterialResourceTable;
    internal bool SupportsRvcOpenXrVisibilityMaskStencil => SupportsRvcVisibilityTargets;
    internal ERvcVulkanProductionFeature ResolveRvcProductionFeatures(bool multiview) => DeviceContext.ResolveRvcProductionFeatures(multiview);
}
