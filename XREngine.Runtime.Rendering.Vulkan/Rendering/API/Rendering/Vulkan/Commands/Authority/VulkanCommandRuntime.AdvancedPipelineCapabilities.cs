using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Vulkan;

/// <summary>Command and descriptor capability synthesis for advanced pipeline selection.</summary>
internal sealed partial class VulkanCommandRuntime
{
    private readonly object _advancedVisibilityReservationGate = new();
    private ulong _advancedVisibilityReservedOutputId;
    private ulong _advancedVisibilityReservationId;
    private long _advancedVisibilityReservationGeneration;

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
        => Volatile.Read(ref _advancedVisibilityReservationId) != 0 &&
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

        long generation = checked((long)(ResourceRuntime.FrameDataArena?.Generation ?? 0UL));
        if (generation <= 0)
        {
            failureReason = "The Vulkan advanced visibility frame-storage generation is unavailable.";
            return false;
        }

        lock (_advancedVisibilityReservationGate)
        {
            if (_advancedVisibilityReservationGeneration != generation)
            {
                _advancedVisibilityReservationGeneration = generation;
                _advancedVisibilityReservedOutputId = 0;
                _advancedVisibilityReservationId = 0;
            }
            if (_advancedVisibilityReservedOutputId != 0 &&
                _advancedVisibilityReservedOutputId != outputId)
            {
                failureReason = "This Vulkan renderer generation has already reserved its single mono advanced visibility family for another output.";
                return false;
            }

            _advancedVisibilityReservedOutputId = outputId;
            if (_advancedVisibilityReservationId == 0)
                _advancedVisibilityReservationId = 1;
            reservation = new(generation, outputId, _advancedVisibilityReservationId);
            failureReason = "Ready";
            return true;
        }
    }

    internal bool IsAdvancedVisibilityReservationCurrent(
        in AdvancedVisibilityFamilyReservation reservation)
    {
        if (!reservation.IsValid)
            return false;
        lock (_advancedVisibilityReservationGate)
            return reservation.BackendGeneration == _advancedVisibilityReservationGeneration &&
                   reservation.OutputId == _advancedVisibilityReservedOutputId &&
                   reservation.ReservationId == _advancedVisibilityReservationId;
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
            supportsAdvancedFrameStorage, false,
            // The realized visibility ABI is currently one mono family per
            // primary frame plan. Global pipeline selection has no output-
            // family reservation identity, so advertising the family here
            // could select it independently for multiple mono outputs and
            // reject the combined stream only at preflight. Keep production
            // promotion fail-closed until that cardinality is represented.
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
