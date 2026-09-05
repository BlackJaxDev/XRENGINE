using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using XREngine.Rendering.Vulkan.RenderGraph;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanAdvancedVisibilityResourceRuntime
{
    private bool TryValidateNativeComputeResources(
        in ResourcePlannerRuntimeState plannerState,
        VulkanPhysicalImageGroup identity, VulkanPhysicalImageGroup metadata,
        VulkanPhysicalImageGroup depth, VulkanPhysicalImageGroup hdr,
        VulkanPhysicalImageGroup velocity, VulkanPhysicalImageGroup reactive,
        VulkanPhysicalImageGroup diagnostics, VulkanPhysicalImageGroup ambientOcclusion, uint slot,
        string activeName, string kernelName, string counterName, string dispatchName,
        string countName, string froxelName, string indexName, string lightingName,
        ref VulkanFrozenBufferBarrier active, ref VulkanFrozenBufferBarrier kernels,
        ref VulkanFrozenBufferBarrier counters, ref VulkanFrozenBufferBarrier dispatch,
        ref VulkanFrozenBufferBarrier counts, ref VulkanFrozenBufferBarrier froxels,
        ref VulkanFrozenBufferBarrier indices, ref VulkanFrozenBufferBarrier lighting,
        out string reason)
    {
        if (slot >= AdvancedFrameSlotContract.DefaultSlotCount)
        {
            reason = "The requested native-compute frame slot is outside the declared advanced resource profile.";
            return false;
        }
        if (!HasImage(identity, Format.R32G32Uint, ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit) ||
            !HasImage(metadata, Format.R32Uint, ImageUsageFlags.SampledBit | ImageUsageFlags.StorageBit) ||
            !HasDepth(depth) || !HasOutput(hdr, Format.R16G16B16A16Sfloat, identity) ||
            !HasOutput(velocity, Format.R16G16Sfloat, identity) || !HasOutput(reactive, Format.R8Unorm, identity) ||
            !HasOutput(diagnostics, Format.R32Uint, identity) ||
            !HasImage(ambientOcclusion, Format.R8Unorm, ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit) ||
            !HasSameExtent(ambientOcclusion, identity) ||
            !HasSameExtent(metadata, identity) || !HasSameExtent(depth, identity))
        {
            reason = "The native-compute images do not satisfy the sampled input and storage output contract.";
            return false;
        }
        ulong tiles;
        ulong activeBytes;
        ulong kernelBytes;
        try
        {
            tiles = checked((ulong)DivideRoundUp(identity.ResolvedExtent.Width,
                    AdvancedClassificationTileDimensions.DefaultTileWidth) *
                DivideRoundUp(identity.ResolvedExtent.Height,
                    AdvancedClassificationTileDimensions.DefaultTileHeight) *
                Math.Max(1u, identity.Template.Layers));
            activeBytes = checked(tiles * 16UL);
            kernelBytes = checked(tiles * AdvancedRenderPipeline.DefaultMaxShadingKernels * 16UL);
        }
        catch (OverflowException)
        {
            reason = "The native-compute tile-grid capacity overflows the fixed storage ABI.";
            return false;
        }
        return Check(in plannerState, activeName, ref active, activeBytes, EBufferTarget.ShaderStorageBuffer, false, false, out reason) &&
            Check(in plannerState, kernelName, ref kernels, kernelBytes, EBufferTarget.ShaderStorageBuffer, false, false, out reason) &&
            Check(in plannerState, counterName, ref counters, 32UL, EBufferTarget.ShaderStorageBuffer, false, true, out reason) &&
            Check(in plannerState, dispatchName, ref dispatch, AdvancedRenderPipeline.DefaultMaxShadingKernels * 16UL, EBufferTarget.DispatchIndirectBuffer, true, false, out reason) &&
            Check(in plannerState, countName, ref counts, AdvancedRenderPipeline.DefaultMaxShadingKernels * sizeof(uint), EBufferTarget.ShaderStorageBuffer, false, true, out reason) &&
            CheckFroxels(in plannerState, froxelName, ref froxels, tiles, out reason) &&
            Check(in plannerState, indexName, ref indices, AdvancedRenderPipeline.DefaultLightIndexListCapacity * sizeof(uint), EBufferTarget.ShaderStorageBuffer, false, false, out reason) &&
            Check(in plannerState, lightingName, ref lighting, 2UL * sizeof(uint), EBufferTarget.ShaderStorageBuffer, false, true, out reason);
    }

    private static bool HasImage(VulkanPhysicalImageGroup group, Format format, ImageUsageFlags usage)
        => group.Samples == SampleCountFlags.Count1Bit && group.Format == format &&
           group.ResolvedExtent.Width != 0u && group.ResolvedExtent.Height != 0u &&
           group.ResolvedExtent.Depth == 1u && (group.Usage & usage) == usage;

    private static bool HasDepth(VulkanPhysicalImageGroup group)
        => group.Samples == SampleCountFlags.Count1Bit &&
           VulkanBarrierUsageMapper.IsDepthFormat(group.Format) &&
           (group.Usage & ImageUsageFlags.SampledBit) != 0;

    private static bool HasOutput(VulkanPhysicalImageGroup group, Format format, VulkanPhysicalImageGroup reference)
        => HasImage(group, format, ImageUsageFlags.StorageBit) && HasSameExtent(group, reference);

    private static bool HasSameExtent(VulkanPhysicalImageGroup left, VulkanPhysicalImageGroup right)
        => left.ResolvedExtent.Equals(right.ResolvedExtent) &&
           Math.Max(1u, left.Template.Layers) == Math.Max(1u, right.Template.Layers);

    private bool CheckFroxels(in ResourcePlannerRuntimeState plannerState, string name, ref VulkanFrozenBufferBarrier buffer, ulong tiles, out string reason)
    {
        if (!GetBuffer(in plannerState, name, buffer, out EBufferTarget target,
                out ulong size, out BufferUsageFlags usage, out reason))
            return false;
        if (target != EBufferTarget.ShaderStorageBuffer ||
            (usage & BufferUsageFlags.StorageBufferBit) == 0 ||
            size % 16UL != 0u || size / 16UL < tiles || size / 16UL % tiles != 0u)
        {
            reason = "The froxel-grid range must exactly cover an integral 16-byte froxel grid.";
            return false;
        }
        buffer = buffer with { NativeSize = size };
        reason = "Ready";
        return true;
    }

    private bool Check(in ResourcePlannerRuntimeState plannerState, string name, ref VulkanFrozenBufferBarrier buffer, ulong bytes, EBufferTarget target, bool indirect, bool transferDst, out string reason)
    {
        if (!GetBuffer(in plannerState, name, buffer, out EBufferTarget actualTarget,
                out ulong size, out BufferUsageFlags actualUsage, out reason))
            return false;
        BufferUsageFlags usage = BufferUsageFlags.StorageBufferBit |
            (indirect ? BufferUsageFlags.IndirectBufferBit : 0) |
            (transferDst ? BufferUsageFlags.TransferDstBit : 0);
        if (actualTarget != target || size != bytes || (actualUsage & usage) != usage)
        {
            reason = $"The native-compute buffer '{name}' has incompatible target ({actualTarget}/{target}), usage ({actualUsage}/{usage}), or logical capacity ({size}/{bytes}).";
            return false;
        }
        // Dispatch dimensions and descriptor bounds use logical capacity,
        // never the driver's rounded backing allocation size.
        buffer = buffer with { NativeSize = size };
        reason = "Ready";
        return true;
    }

    private bool GetBuffer(
        in ResourcePlannerRuntimeState plannerState,
        string name,
        in VulkanFrozenBufferBarrier buffer,
        out EBufferTarget target,
        out ulong size,
        out BufferUsageFlags usage,
        out string reason)
    {
        target = default;
        size = 0u;
        usage = default;
        if (!string.Equals(buffer.LogicalResourceName, name, StringComparison.Ordinal) ||
            buffer.NativeOffset != 0u || buffer.NativeSize == 0u || buffer.NativeGeneration == 0u ||
            _resources.GetPublishedGeneration(ObjectType.Buffer, buffer.NativeBuffer.Handle) != buffer.NativeGeneration)
        {
            reason = $"The native-compute buffer '{name}' has lost its frozen native generation or range.";
            return false;
        }

        // Match the graph freezer's ownership order. A registry-owned buffer
        // must never be replaced by the planner's same-named physical buffer.
        XRDataBuffer? owner = null;
        if (plannerState.LastActiveFrameOpContext is { } context)
        {
            bool resolved = context.ResourceRegistry?.TryGetBuffer(name, out owner) == true;
            if (!resolved && context.ResourceRegistry is { } registry)
                _ = context.PipelineInstance?.Variables.TryResolveBuffer(registry, name, out owner);
            else if (!resolved && context.ResourceRegistry is null)
                _ = context.PipelineInstance?.TryGetBuffer(name, out owner);
        }
        if (owner is not null)
        {
            if (_resources.WrapperLookup.GetOrCreate(owner, generateNow: false) is not VkDataBuffer wrapper)
            {
                reason = $"The native-compute buffer '{name}' has no Vulkan wrapper for its frozen registry owner.";
                return false;
            }
            if (!wrapper.TryCaptureComputeBufferSnapshot(allowSynchronousUpload: false, out VulkanComputeBufferBinding snapshot))
            {
                reason = $"The native-compute buffer '{name}' is not ready (ready={wrapper.IsReadyForRendering}, pendingUpload={wrapper.HasPendingUpload}, allocated={wrapper.AllocatedByteSize}, logical={owner.Length}).";
                return false;
            }
            // Graph barriers cover the native allocation, while descriptors
            // expose its logical data range. Rounded allocation capacity is
            // valid only when it is still the exact frozen native allocation.
            if (snapshot.Buffer.Handle != buffer.NativeBuffer.Handle ||
                wrapper.AllocatedByteSize != buffer.NativeSize ||
                snapshot.Range > buffer.NativeSize || owner.Length != snapshot.Range)
            {
                reason = $"The native-compute buffer '{name}' changed its frozen registry binding (handle={snapshot.Buffer.Handle:X}/{buffer.NativeBuffer.Handle:X}, allocated={wrapper.AllocatedByteSize}/{buffer.NativeSize}, logical={owner.Length}/{snapshot.Range}).";
                return false;
            }
            target = owner.Target;
            // XRDataBuffer.Length includes optional vec4 tail padding. That
            // padding is storage, not another element in the shader contract.
            size = checked((ulong)owner.ElementCount * owner.ElementSize);
            usage = snapshot.UsageFlags;
        }
        else
        {
            VulkanResourceAllocator allocator = plannerState.ResourceAllocator;
            if (!allocator.TryGetBufferAllocation(name, out VulkanBufferAllocation allocation) ||
                !allocator.TryGetPhysicalBufferGroupForResource(name, out VulkanPhysicalBufferGroup? physical) ||
                physical is not { IsAllocated: true } || physical.Buffer.Handle != buffer.NativeBuffer.Handle ||
                buffer.NativeSize != physical.SizeInBytes)
            {
                reason = $"The native-compute buffer '{name}' no longer matches its frozen allocator owner.";
                return false;
            }
            target = allocation.Target;
            size = allocation.SizeInBytes;
            usage = physical.Usage;
        }
        reason = "Ready";
        return true;
    }
}
