using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    /// <summary>
    /// Pins the exact heap generations on every use, then omits native binds only
    /// when this recording already has the same complete binding state.
    /// </summary>
    internal bool TryEnsureDescriptorHeapsBound(CommandBuffer commandBuffer, out string reason)
    {
        DescriptorHeapBindingIdentity identity = CaptureDescriptorHeapBindingIdentity();
        return TryEnsureDescriptorHeapsBound(commandBuffer, in identity, out reason);
    }

    private bool TryEnsureDescriptorHeapsBound(
        CommandBuffer commandBuffer,
        in DescriptorHeapBindingIdentity identity,
        out string reason)
    {
        reason = string.Empty;
        if (!identity.IsComplete)
        {
            reason = "descriptor heap binding has no complete storage generation";
            return false;
        }

        // A state-cache hit does not replace native-resource ownership. The
        // expected-generation path participates in the retirement handshake.
        PrimaryCommandEncoder.Track(commandBuffer, identity.SamplerBuffer.Key.Type,
            identity.SamplerBuffer.Key.Handle, identity.SamplerBuffer.Generation);
        PrimaryCommandEncoder.Track(commandBuffer, identity.ResourceBuffer.Key.Type,
            identity.ResourceBuffer.Key.Handle, identity.ResourceBuffer.Generation);

        if (LaneRecordingContexts.TryGetActiveContext(commandBuffer, out VulkanLaneRecordingContext? lane) && lane is not null)
            return TryApplyDescriptorHeapBinding(commandBuffer, in identity, ref lane.BindState, out reason);

        lock (CommandBuffers.BindStateGate)
        {
            ulong handle = unchecked((ulong)commandBuffer.Handle);
            if (!CommandBuffers.BindStates.TryGetValue(handle, out CommandBufferBindState state))
            {
                reason = "descriptor heaps require an active command-buffer recording";
                return false;
            }
            bool result = TryApplyDescriptorHeapBinding(commandBuffer, in identity, ref state, out reason);
            CommandBuffers.BindStates[handle] = state;
            return result;
        }
    }

    private bool TryApplyDescriptorHeapBinding(
        CommandBuffer commandBuffer,
        in DescriptorHeapBindingIdentity identity,
        ref CommandBufferBindState state,
        out string reason)
    {
        reason = string.Empty;
        if (state.InheritsDescriptorHeaps)
        {
            // Vulkan forbids rebinding a heap inherited by a secondary. Reject
            // invalidated or replaced storage instead of attempting a repair.
            if (!state.HasDescriptorHeapBinding || state.InheritedDescriptorHeapBinding != identity)
            {
                reason = "inherited descriptor heaps were invalidated or replaced";
                return false;
            }
            return true;
        }
        if (state.HasDescriptorHeapBinding && state.DescriptorHeapBinding == identity)
            return true;

        VulkanTrackedCommandEncoder.BindDescriptorHeaps(commandBuffer, ResourceRuntime.Descriptors.Heap, in identity);
        state.DescriptorHeapBinding = identity;
        state.HasDescriptorHeapBinding = true;
        state.GraphicsDescriptorSignature = 0;
        state.ComputeDescriptorSignature = 0;
        return true;
    }

    /// <summary>Captures heap storage and its published native generations without allocating.</summary>
    internal DescriptorHeapBindingIdentity CaptureDescriptorHeapBindingIdentity()
    {
        VulkanDescriptorHeapState heap = ResourceRuntime.Descriptors.Heap;
        if (heap.ActiveBackend != EVulkanDescriptorBackend.DescriptorHeap ||
            heap.NativeFunctions is null || !heap.SamplerStorage.IsReady || !heap.ResourceStorage.IsReady)
            return default;

        return new DescriptorHeapBindingIdentity(
            new VulkanPinnedResourceGeneration(
                new VulkanResourceLifetimeKey(ObjectType.Buffer, heap.SamplerStorage.Buffer.Handle),
                GetResourceGeneration(ObjectType.Buffer, heap.SamplerStorage.Buffer.Handle)),
            heap.SamplerStorage.DeviceAddress,
            heap.SamplerStorage.Size,
            0,
            Math.Max(heap.Properties.MinSamplerHeapReservedRange, heap.Properties.MinSamplerHeapReservedRangeWithEmbedded),
            new VulkanPinnedResourceGeneration(
                new VulkanResourceLifetimeKey(ObjectType.Buffer, heap.ResourceStorage.Buffer.Handle),
                GetResourceGeneration(ObjectType.Buffer, heap.ResourceStorage.Buffer.Handle)),
            heap.ResourceStorage.DeviceAddress,
            heap.ResourceStorage.Size,
            0,
            heap.Properties.MinResourceHeapReservedRange);
    }
}
