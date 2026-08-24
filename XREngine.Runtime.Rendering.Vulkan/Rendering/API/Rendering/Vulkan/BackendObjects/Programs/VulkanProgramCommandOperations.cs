using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;
using XREngine.Scene;

namespace XREngine.Rendering.Vulkan;

/// <summary>Behavior-only command operations required while recording program and mesh wrappers.</summary>
internal sealed class VulkanProgramCommandOperations(VulkanCommandRuntime commands)
{
    internal void SetEngineUniforms(XRRenderProgram program, XRCamera camera) => commands.SetEngineUniforms(program, camera);
    internal VulkanFixedFunctionStateSnapshot CaptureFixedFunctionState() => commands.CaptureFixedFunctionState();
    internal void RestoreFixedFunctionState(in VulkanFixedFunctionStateSnapshot snapshot) => commands.RestoreFixedFunctionState(in snapshot);
    internal XRFrameBuffer? ResolveCurrentDrawTarget() => commands.ResolveCurrentDrawTarget();
    internal VulkanMeshProducerSnapshot CaptureIndirectProducerSnapshot(XRFrameBuffer? target) => commands.CaptureIndirectProducerSnapshot(target);

    /// <summary>
    /// Captures the exact draw target and raster state while the producer's
    /// render-target scope is still active.
    /// </summary>
    internal VulkanMeshProducerSnapshot CaptureMeshProducerSnapshot(
        in FrameOpContext context)
    {
        XRFrameBuffer? target = commands.ResolveCurrentDrawTarget();
        return commands.CaptureIndirectProducerSnapshot(target) with
        {
            Context = context,
        };
    }
    internal ComputeDispatchSnapshot? GetForwardLightingBindingSnapshotForArtifact(Lights3DCollection lights, XRRenderProgram programData, VkRenderProgram backendProgram) => commands.GetForwardLightingBindingSnapshotForArtifact(lights, programData, backendProgram);
    internal void SetMaterialUniforms(XRMaterial material, XRRenderProgram program, VkRenderProgram? backendProgram, in LayeredShadowUniformState shadowState) => commands.SetMaterialUniforms(material, program, backendProgram, in shadowState);
    internal void SetMaterialRuntimeUniforms(XRMaterial material, XRRenderProgram program, VkRenderProgram? backendProgram, in LayeredShadowUniformState shadowState) => commands.SetMaterialRuntimeUniforms(material, program, backendProgram, in shadowState);
    internal void ApplyRenderParameters(RenderingParameters parameters) => commands.ApplyRenderParameters(parameters);
    internal float MaterialUniformUpdateDelta => commands.MaterialUniformUpdateDelta;
    internal float MaterialUniformSeconds => commands.MaterialUniformSeconds;
    internal float MaterialUniformRenderDelta => commands.MaterialUniformRenderDelta;
    internal void MarkCommandBuffersDirtyForLegacyMeshState() => commands.MarkCommandBuffersDirtyForLegacyMeshState();
    internal void RemoveMeshFrameDataManifestRenderer(VkMeshRenderer owner) => commands.RemoveMeshFrameDataManifestRenderer(owner);
    internal bool CanUpdateCompletedDescriptorFrameSlot(int slot) => commands.CanUpdateCompletedDescriptorFrameSlot(slot);
    internal void BindPipelineTracked(CommandBuffer commandBuffer, PipelineBindPoint bindPoint, Pipeline pipeline) => commands.BindPipelineTracked(commandBuffer, bindPoint, pipeline);
    internal void BindIndexBufferTracked(CommandBuffer commandBuffer, Silk.NET.Vulkan.Buffer buffer, ulong offset, IndexType indexType) => commands.BindIndexBufferTracked(commandBuffer, buffer, offset, indexType);
    internal void BindVertexBuffersTracked(CommandBuffer commandBuffer, uint firstBinding, Silk.NET.Vulkan.Buffer[] buffers, ulong[] offsets) => commands.BindVertexBuffersTracked(commandBuffer, firstBinding, buffers, offsets);
    internal void BindVertexBufferTracked(CommandBuffer commandBuffer, uint binding, Silk.NET.Vulkan.Buffer buffer, ulong offset) => commands.BindVertexBufferTracked(commandBuffer, binding, buffer, offset);
    internal void BindDescriptorSetsTracked(CommandBuffer commandBuffer, PipelineBindPoint bindPoint, PipelineLayout layout, uint firstSet, DescriptorSet[] sets) => commands.BindDescriptorSetsTracked(commandBuffer, bindPoint, layout, firstSet, sets);
    internal void BindDescriptorSetsTracked(CommandBuffer commandBuffer, PipelineBindPoint bindPoint, PipelineLayout layout, uint firstSet, ReadOnlySpan<DescriptorSet> sets, ReadOnlySpan<uint> dynamicOffsets) => commands.BindDescriptorSetsTracked(commandBuffer, bindPoint, layout, firstSet, sets, dynamicOffsets);
    internal void PushConstantsTracked<T>(CommandBuffer commandBuffer, PipelineLayout layout, ShaderStageFlags stages, uint offset, in T value) where T : unmanaged => commands.PushConstantsTracked(commandBuffer, layout, stages, offset, in value);
    internal bool TryPushDescriptorHeapProgramData(CommandBuffer commandBuffer, VkRenderProgram program, DescriptorHeapPushDataPayload? payload, out string reason)
    {
        reason = string.Empty;
        if (payload is null)
        {
            reason = $"descriptor heap payload is missing for program '{program.Data.Name ?? "UnnamedProgram"}'.";
            return false;
        }
        if (commands.TryPushProgramDescriptorHeapData(commandBuffer, program, payload))
            return true;
        reason = $"descriptor heap push failed for program '{program.Data.Name ?? "UnnamedProgram"}'.";
        return false;
    }
    internal int ResolveCommandBufferImageIndex(CommandBuffer commandBuffer) => commands.ResolveCommandBufferImageIndex(commandBuffer);
    internal bool TransitionPublishedDescriptorSetImagesForSampling(
        CommandBuffer commandBuffer,
        DescriptorSet descriptorSet,
        XRFrameBuffer? target,
        int passIndex,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata)
        => commands.TransitionPublishedDescriptorSetImagesForSampling(
            commandBuffer,
            descriptorSet,
            target,
            passIndex,
            passMetadata);
    internal bool TryAcquireMappedFrameArenaRecordingLease(CommandBuffer commandBuffer, VkMeshRenderer owner, int drawSlot, ulong sealedGeneration, out string reason) => commands.TryAcquireMappedFrameArenaRecordingLease(commandBuffer, owner, drawSlot, sealedGeneration, out reason);
    internal bool CommandBufferReferencesAllDescriptorSets(CommandBuffer commandBuffer, ReadOnlySpan<DescriptorSet> sets, out ulong missing) => commands.CommandBufferReferencesAllDescriptorSets(commandBuffer, sets, out missing);
    internal void SetBoundFrameBufferState(EFramebufferTarget target, XRFrameBuffer? frameBuffer) => commands.SetBoundFrameBufferState(target, frameBuffer);
}
