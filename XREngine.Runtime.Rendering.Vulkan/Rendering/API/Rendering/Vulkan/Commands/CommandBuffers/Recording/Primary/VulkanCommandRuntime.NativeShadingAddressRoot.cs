using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    private VulkanAdvancedNativeShadingRootBinding PrepareNativeShadingAddressRoot(
        scoped ref PrimaryCommandBufferRecordingState state, int frameSlot)
    {
        if (!state.Policy.InitializeOutputColor || state.Policy.AllowsArtifactReuse || state.OpenXrTargetContext is not null)
            throw new NotSupportedException("The BufferDeviceAddress shading-root pilot requires an exact presentationless output with fresh primary recording.");
        if (VulkanNativeShadingRootPolicy.AbiVersion != 1 ||
            Unsafe.SizeOf<VulkanAdvancedNativeShadingAddressRoot>() != 16 ||
            Unsafe.SizeOf<VulkanAdvancedNativeShadingPushConstants>() != 64)
            throw new VulkanPlanPreconditionException("The native shading root CPU layout does not match ABI version 1.");
        if (!DeviceContext.SupportsBufferDeviceAddress || ResourceRuntime.FrameDataArena is not { IsActive: true } arena)
            throw new NotSupportedException("The BufferDeviceAddress shading root requires an enabled device-address feature and the frame-data arena.");
        if (!arena.TryAllocate(frameSlot, EVulkanFrameDataLane.Storage, 64u, 16u, out VulkanFrameDataSlice slice))
            throw new VulkanPlanPreconditionException("The native shading root could not reserve its bounded frame-owned parameter range.");

        ulong baseAddress = GetBufferDeviceAddress(slice.Buffer);
        if (baseAddress == 0 || baseAddress > ulong.MaxValue - slice.Offset)
            throw new VulkanPlanPreconditionException("The native shading parameter buffer has no valid GPU address.");
        var binding = new VulkanAdvancedNativeShadingRootBinding(slice, baseAddress + slice.Offset,
            GetResourceGeneration(ObjectType.Buffer, slice.Buffer.Handle), arena.GetFrameSlotResetEpoch(frameSlot));
        if (!binding.IsValid)
            throw new VulkanPlanPreconditionException("The native shading address root has invalid alignment, extent or resource ownership.");
        return binding;
    }

    private ulong WriteNativeShadingAddressRoot(scoped ref PrimaryCommandBufferRecordingState state,
        in VulkanAdvancedNativeShadingRootBinding binding, in VulkanAdvancedNativeShadingPushConstants parameters)
    {
        if (!binding.IsValid || binding.Slice.FrameSlot != checked((int)state.FrameDataImageIndex) ||
            ResourceRuntime.FrameDataArena is not { IsActive: true } arena ||
            arena.GetFrameSlotResetEpoch(binding.Slice.FrameSlot) != binding.SlotResetEpoch ||
            GetResourceGeneration(ObjectType.Buffer, binding.Slice.Buffer.Handle) != binding.NativeBufferGeneration ||
            !arena.TryBeginWrite(binding.Slice, out VulkanFrameDataWriteScope write))
            throw new VulkanPlanPreconditionException("The native shading address root is stale, foreign, or no longer writable.");
        using (write)
            MemoryMarshal.Write(write.Bytes, in parameters);
        // Submission preparation flushes this range before either queue starts.
        // Only the final shader consumes it; its pin outlives the joined receipt.
        TrackCommandBufferResource(state.CommandBuffer, new(ObjectType.Buffer, binding.Slice.Buffer.Handle),
            "AdvancedNativeShade.ParameterRoot", binding.NativeBufferGeneration);
        return binding.ParameterAddress;
    }
}
