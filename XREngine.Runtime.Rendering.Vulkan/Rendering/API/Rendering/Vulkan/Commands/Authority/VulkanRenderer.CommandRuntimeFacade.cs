using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    internal VulkanCommandRuntime CommandRuntime => _commandRuntime;
    // Legacy renderer partials are transitional call boundaries only. Keep the
    // persistent state in the command authority; do not recreate it here.
    private ref CommandBuffer[]? _commandBuffers => ref _commandRuntime.CommandBuffers.Buffers;
    private ref CommandBuffer[]? _activeCommandBuffers => ref _commandRuntime.CommandBuffers.ActiveBuffers;
    private ref VulkanPrimaryCommandPlan[]? _primaryCommandPlans => ref _commandRuntime.CommandBuffers.PrimaryPlans;
    private ref PrimaryCommandArtifactOwner[]? _primaryCommandArtifactOwners => ref _commandRuntime.CommandBuffers.PrimaryOwners;
    private ref CommandBuffer[]? _dynamicUiBatchTextSecondaryCommandBuffers => ref _commandRuntime.CommandBuffers.DynamicUiSecondaries;
    private ref CommandBuffer[]? _dynamicUiBatchTextOverlayCommandBuffers => ref _commandRuntime.CommandBuffers.DynamicUiOverlays;
    private ref int[]? _dynamicUiBatchTextSecondaryOpCounts => ref _commandRuntime.CommandBuffers.DynamicUiOpCounts;
    private ref ulong[]? _dynamicUiBatchTextSecondarySignatures => ref _commandRuntime.CommandBuffers.DynamicUiSignatures;
    private ref ulong[]? _commandBufferFrameOpSignatures => ref _commandRuntime.CommandBuffers.FrameOpSignatures;
    private ref ulong[]? _commandBufferPlannerRevisions => ref _commandRuntime.CommandBuffers.PlannerRevisions;
    private ref ComputeTransientResources[]? _computeTransientResources => ref _commandRuntime.CommandBuffers.ComputeTransientResources;
    private ref List<DeferredSecondaryCommandBuffer>[]? _deferredSecondaryCommandBuffers => ref _commandRuntime.CommandBuffers.DeferredSecondaries;
    private object _oneTimeCommandPoolsLock => _commandRuntime.CommandBuffers.OneTimePoolsGate;
    private Dictionary<nint, OneTimeCommandOwner> _oneTimeCommandPools => _commandRuntime.CommandBuffers.OneTimePools;
    private object _commandBindStateLock => _commandRuntime.CommandBuffers.BindStateGate;
    private Dictionary<ulong, CommandBufferBindState> _commandBindStates => _commandRuntime.CommandBuffers.BindStates;
    private Dictionary<ulong, int> _commandBufferImageIndices => _commandRuntime.CommandBuffers.ImageIndices;
    private ref long _commandBufferRecordingGeneration => ref _commandRuntime.CommandBuffers.RecordingGeneration;
    private object _ownedCommandChainSecondaryPoolsLock => _commandRuntime.CommandBuffers.OwnedSecondaryPoolsGate;
    private Dictionary<ulong, OwnedCommandChainSecondaryPool> _ownedCommandChainSecondaryPools => _commandRuntime.CommandBuffers.OwnedSecondaryPools;
    private ref bool _enableSecondaryCommandBuffers => ref _commandRuntime.CommandBuffers.EnableSecondary;
    private ref bool _enableComputeSecondaryCommandBuffers => ref _commandRuntime.CommandBuffers.EnableComputeSecondary;
    private ref bool _enableTransferSecondaryCommandBuffers => ref _commandRuntime.CommandBuffers.EnableTransferSecondary;
    private ref bool _enableQuerySecondaryCommandBuffers => ref _commandRuntime.CommandBuffers.EnableQuerySecondary;
    private ref FrameOpSignatureDebugPart[][]? _commandBufferFrameOpSignatureDebugParts => ref _commandRuntime.CommandBuffers.SignatureDebugParts;
    private ref int _frameOpSignatureDiffLogCount => ref _commandRuntime.CommandBuffers.SignatureDiffLogCount;
    private ref string? _vulkanDiagnosticBaseWindowTitle => ref _commandRuntime.CommandBuffers.DiagnosticBaseWindowTitle;
    private ref string? _vulkanDiagnosticLastTitle => ref _commandRuntime.CommandBuffers.DiagnosticLastTitle;
    private ref int _vulkanLastFrameDroppedDrawOps => ref _commandRuntime.CommandBuffers.LastFrameDroppedDrawOps;
    private ref int _vulkanLastFrameDroppedOps => ref _commandRuntime.CommandBuffers.LastFrameDroppedOps;
    private ref long _lastCommandBufferDirtyTimestamp => ref _commandRuntime.CommandBuffers.LastDirtyTimestamp;
    private VulkanFrameWideMeshFrameDataReservationManifest _frameWideMeshFrameDataManifest => _commandRuntime.CommandBuffers.FrameWideMeshDataManifest;
    private ref long _observedMeshFrameDataManifestGeneration => ref _commandRuntime.CommandBuffers.ObservedMeshFrameDataManifestGeneration;
    private ref bool _lastEnsureCommandBufferRecordedPrimary => ref _commandRuntime.CommandBuffers.LastEnsureRecordedPrimary;
    private ref string? _lastReusableFrameDataRefreshFailureReason => ref _commandRuntime.CommandBuffers.LastReusableFrameDataRefreshFailureReason;
    private CommandPool commandPool
    {
        get => _commandRuntime.Pools.PrimaryGraphics;
        set => _commandRuntime.Pools.PrimaryGraphics = value;
    }

    private CommandPool transferCommandPool
    {
        get => _commandRuntime.Pools.PrimaryTransfer;
        set => _commandRuntime.Pools.PrimaryTransfer = value;
    }

    private object CommandPoolsGate => _commandRuntime.Pools.Gate;

    internal bool VulkanPrimaryCommandBufferReuseEnabled
        => _commandRuntime.VulkanPrimaryCommandBufferReuseEnabled;

    internal int DescriptorFrameSlotFrameCount
        => _commandRuntime.DescriptorFrameSlotFrameCount;

    internal static bool DescriptorTraceEnabled
        => VulkanCommandRuntime.DescriptorTraceEnabled;

    private ThreadLocal<CommandBufferRecordingScratch> _commandBufferRecordingScratch
        => _commandRuntime.CommandBuffers.RecordingScratch;

    private object _oneTimeSubmitLock
        => _commandRuntime.CommandBuffers.OneTimeSubmitGate;

    internal void MarkCommandBuffersDirty(
        [CallerMemberName] string? reason = null)
        => _commandRuntime.MarkCommandBuffersDirty(reason);

    internal void MarkCommandBuffersDirtyForLegacyMeshState(
        [CallerMemberName] string? reason = null)
        => _commandRuntime.MarkCommandBuffersDirtyForLegacyMeshState(reason);

    internal void BindDescriptorSetsTracked(
        CommandBuffer commandBuffer,
        PipelineBindPoint bindPoint,
        PipelineLayout layout,
        uint firstSet,
        DescriptorSet[] sets)
        => _commandRuntime.BindDescriptorSetsTracked(
            commandBuffer,
            bindPoint,
            layout,
            firstSet,
            sets);

    internal void BindDescriptorSetTracked(
        CommandBuffer commandBuffer,
        PipelineBindPoint bindPoint,
        PipelineLayout layout,
        uint firstSet,
        DescriptorSet descriptorSet)
        => _commandRuntime.BindDescriptorSetTracked(
            commandBuffer,
            bindPoint,
            layout,
            firstSet,
            descriptorSet);

    internal void BindDescriptorSetsTracked(
        CommandBuffer commandBuffer,
        PipelineBindPoint bindPoint,
        PipelineLayout layout,
        uint firstSet,
        ReadOnlySpan<DescriptorSet> sets,
        ReadOnlySpan<uint> dynamicOffsets)
        => _commandRuntime.BindDescriptorSetsTracked(
            commandBuffer,
            bindPoint,
            layout,
            firstSet,
            sets,
            dynamicOffsets);

    internal CommandPool GetThreadCommandPool()
        => _commandRuntime.GetThreadCommandPool();

    internal CommandPool GetThreadTransferCommandPool()
        => _commandRuntime.GetThreadTransferCommandPool();

    internal void ResetCommandBufferBindState(CommandBuffer commandBuffer)
        => _commandRuntime.ResetCommandBufferBindState(commandBuffer);

    internal void RemoveCommandBufferBindState(CommandBuffer commandBuffer)
        => _commandRuntime.RemoveCommandBufferBindState(commandBuffer);

    internal ulong ResolveCommandBufferRecordingGeneration(CommandBuffer commandBuffer)
        => _commandRuntime.ResolveCommandBufferRecordingGeneration(commandBuffer);

    internal int ResolveCommandBufferImageIndex(CommandBuffer commandBuffer)
        => _commandRuntime.ResolveCommandBufferImageIndex(commandBuffer);

    internal Result EndCommandBufferTracked(
        CommandBuffer commandBuffer,
        bool cacheVariant = true)
        => _commandRuntime.EndCommandBufferTracked(commandBuffer, cacheVariant);

    internal Result EndCommandBufferTracked(
        CommandBuffer commandBuffer,
        bool cacheVariant,
        out string trackingFailure)
        => _commandRuntime.EndCommandBufferTracked(commandBuffer, cacheVariant, out trackingFailure);

    internal void EnsureCommandBufferFrameDataSlotCapacity(int frameDataSlotCount)
        => _commandRuntime.EnsureCommandBufferFrameDataSlotCapacity(frameDataSlotCount);

    internal bool CanUpdateCompletedDescriptorFrameSlot(int frameDataSlot)
        => _commandRuntime.CanUpdateCompletedDescriptorFrameSlot(frameDataSlot);

    internal void BindPipelineTracked(
        CommandBuffer commandBuffer,
        PipelineBindPoint bindPoint,
        Pipeline pipeline)
        => _commandRuntime.BindPipelineTracked(commandBuffer, bindPoint, pipeline);

    internal void BindIndexBufferTracked(
        CommandBuffer commandBuffer,
        Silk.NET.Vulkan.Buffer indexBuffer,
        ulong offset,
        IndexType indexType)
        => _commandRuntime.BindIndexBufferTracked(commandBuffer, indexBuffer, offset, indexType);

    internal void BindVertexBuffersTracked(
        CommandBuffer commandBuffer,
        uint firstBinding,
        Silk.NET.Vulkan.Buffer[] buffers,
        ulong[] offsets)
        => _commandRuntime.BindVertexBuffersTracked(commandBuffer, firstBinding, buffers, offsets);

    internal void BindVertexBufferTracked(
        CommandBuffer commandBuffer,
        uint binding,
        Silk.NET.Vulkan.Buffer buffer,
        ulong offset)
        => _commandRuntime.BindVertexBufferTracked(commandBuffer, binding, buffer, offset);

    internal void PushConstantsTracked<T>(
        CommandBuffer commandBuffer,
        PipelineLayout layout,
        ShaderStageFlags stageFlags,
        uint offset,
        in T value) where T : unmanaged
        => _commandRuntime.PushConstantsTracked(commandBuffer, layout, stageFlags, offset, in value);

    internal bool TransitionPublishedDescriptorSetImagesForSampling(
        CommandBuffer commandBuffer,
        DescriptorSet descriptorSet,
        XRFrameBuffer? target)
        => _commandRuntime.TransitionPublishedDescriptorSetImagesForSampling(
            commandBuffer,
            descriptorSet,
            target);

    internal void EnsureCommandBufferVariantContextBeforeSubmit(
        uint imageIndex,
        PrimaryCommandArtifactOwner variant,
        ulong frameOpContextFingerprint,
        ulong frameOpContextId,
        string submitPath)
        => _commandRuntime.EnsureCommandBufferVariantContextBeforeSubmit(
            imageIndex,
            variant,
            frameOpContextFingerprint,
            frameOpContextId,
            submitPath);

    internal static ulong HashSamplerUnitBindingLayout(
        Dictionary<uint, XRTexture> samplers,
        Dictionary<uint, string> samplerNamesByUnit)
        => VulkanCommandRuntime.HashSamplerUnitBindingLayout(samplers, samplerNamesByUnit);

    internal static ulong HashSamplerNameBindingLayout(Dictionary<string, XRTexture> samplers)
        => VulkanCommandRuntime.HashSamplerNameBindingLayout(samplers);

    internal static ulong HashImageBindingLayout(Dictionary<uint, ProgramImageBinding> images)
        => VulkanCommandRuntime.HashImageBindingLayout(images);

    internal static ulong HashBufferBindingLayout(Dictionary<uint, VulkanComputeBufferBinding> buffers)
        => VulkanCommandRuntime.HashBufferBindingLayout(buffers);

    internal static int GetFrameWideMeshDrawUniformSlot(
        Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> slotsByRendererFamily,
        Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> familyBases,
        VkMeshRenderer renderer,
        int frameDataSlot,
        EVulkanMeshFrameDataStreamKind streamKind,
        in FrameOpContext context,
        in PendingMeshDraw draw)
        => VulkanCommandRuntime.GetFrameWideMeshDrawUniformSlot(
            slotsByRendererFamily,
            familyBases,
            renderer,
            frameDataSlot,
            streamKind,
            in context,
            in draw);

    internal static void MarkCommandChainSecondaryCommandBufferInvalid(
        CommandChain chain,
        EVulkanRecordedCommandArtifactInvalidationReason reason =
            EVulkanRecordedCommandArtifactInvalidationReason.RecordingStarted)
        => VulkanCommandRuntime.MarkCommandChainSecondaryCommandBufferInvalid(chain, reason);

    internal bool TryRegisterFrameWideMeshFrameDataRequirements(
        FrameOperationSequence primaryOps,
        FrameOperationSequence secondaryOps,
        int frameDataSlot,
        bool sealAfterRegister,
        Dictionary<VkMeshRenderer, int> requirements,
        CommandBufferRecordingScratch scratch,
        Dictionary<VulkanMeshFrameDataRendererFamilyKey, int> resolvedFamilyBases,
        out ulong manifestGeneration,
        out string reason)
        => _commandRuntime.TryRegisterFrameWideMeshFrameDataRequirements(
            primaryOps,
            secondaryOps,
            frameDataSlot,
            sealAfterRegister,
            requirements,
            scratch,
            resolvedFamilyBases,
            out manifestGeneration,
            out reason);

    internal bool TryRefreshReusableCommandBufferFrameData(
        uint imageIndex,
        ReadOnlySpan<VulkanReusableFrameDataRefreshRequest> requests,
        ReadOnlySpan<VulkanReusableFrameDataRefreshRequest> ownerWorkRequests,
        in VulkanReusableFrameDataRefreshBatchInfo batchInfo,
        VulkanReusableFrameDataRefreshState refreshState,
        bool dynamicUi,
        bool descriptorResourcesCapturedByFrameSignature = false,
        bool refreshMaterialUniforms = true,
        IReadOnlyDictionary<CommandChainKey, CommandChain>? commandChainCache = null,
        ReadOnlySpan<CommandChainKey> scheduledCommandChainKeys = default)
        => _commandRuntime.TryRefreshReusableCommandBufferFrameData(
            imageIndex,
            requests,
            ownerWorkRequests,
            in batchInfo,
            refreshState,
            dynamicUi,
            descriptorResourcesCapturedByFrameSignature,
            refreshMaterialUniforms,
            commandChainCache,
            scheduledCommandChainKeys);

    private void CreateCommandPool() => _commandRuntime.CreateCommandPool();
    private void DestroyCommandPool() => _commandRuntime.DestroyCommandPool();
    private void DestroyComputeTransientResources()
        => _commandRuntime.DestroyComputeTransientResources();
    private void CancelRecordedTextureUploadPublications(string reason)
        => _commandRuntime.CancelRecordedTextureUploadPublications(reason);
}
