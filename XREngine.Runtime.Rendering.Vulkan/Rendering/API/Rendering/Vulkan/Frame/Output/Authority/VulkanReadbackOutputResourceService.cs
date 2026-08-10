using Silk.NET.Vulkan;
using System.Threading;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Owns native buffers, images, memory, and fences retained by bounded output
/// readback rings. Command encoding and submission remain command concerns.
/// </summary>
internal sealed unsafe class VulkanReadbackOutputResourceService(
    VulkanDeviceContext device,
    VulkanResourceRuntime resources,
    VulkanCommandRuntime commands)
{
    private const ulong MaximumScreenshotResolveImageBytes = 256UL * 1024UL * 1024UL;
    private const int ScreenshotReadbackSlotCount = 8;
    private const int DepthReadbackSlotCount = 8;

    internal bool TryAcquireDepthStagingSlice(
        int frameSlot,
        ulong byteCount,
        out VulkanFrameDataSlice slice,
        out string? failure)
        => TryAcquireStagingSlice(
            ScreenshotReadbackSlotCount + Math.Abs(frameSlot % DepthReadbackSlotCount),
            byteCount,
            submissionCompletionProven: false,
            out slice,
            out failure);

    internal bool TryAcquireStagingSlice(
        int slotIndex,
        ulong byteCount,
        bool submissionCompletionProven,
        out VulkanFrameDataSlice slice,
        out string? failure)
    {
        slice = default;
        failure = null;
        VulkanFrameDataArena? arena = resources.ReadbackFrameDataArena;
        if (arena is null || !arena.IsActive)
        {
            failure = "The Vulkan readback frame-data arena is unavailable.";
            return false;
        }
        if (!arena.TryResetFrameSlot(
                checked((uint)slotIndex),
                arena.Generation,
                submissionCompletionProven))
        {
            failure = $"Readback arena slot {slotIndex} is not reusable.";
            return false;
        }
        if (!arena.TryAllocate(
                slotIndex,
                EVulkanFrameDataLane.Readback,
                byteCount,
                alignment: 16,
                out slice))
        {
            failure = $"Readback arena rejected {byteCount:N0} bytes for slot {slotIndex}.";
            return false;
        }

        return true;
    }

    internal bool TryPrepareStagingSlice(in VulkanFrameDataSlice slice)
        => resources.ReadbackFrameDataArena is { } arena &&
           slice.ArenaIdentity == arena.Identity &&
           arena.TryPrepareFrameSlotForSubmission(
               checked((uint)slice.FrameSlot),
               slice.Generation);

    internal void MarkStagingSliceSubmitted(in VulkanFrameDataSlice slice)
    {
        if (resources.ReadbackFrameDataArena is { } arena &&
            slice.ArenaIdentity == arena.Identity)
        {
            arena.MarkFrameSlotSubmitted(
                checked((uint)slice.FrameSlot),
                slice.Generation);
        }
    }

    internal void CancelStagingSliceSubmission(in VulkanFrameDataSlice slice)
    {
        if (resources.ReadbackFrameDataArena is { } arena &&
            slice.ArenaIdentity == arena.Identity)
        {
            _ = arena.TryCancelFrameSlotSubmission(
                checked((uint)slice.FrameSlot),
                slice.Generation);
        }
    }

    internal bool TryCompleteStagingSlice(in VulkanFrameDataSlice slice)
        => resources.ReadbackFrameDataArena is { } arena &&
           slice.ArenaIdentity == arena.Identity &&
           arena.TryResetFrameSlot(
               checked((uint)slice.FrameSlot),
               slice.Generation,
               submissionCompletionProven: true);

    internal bool TryBeginRead(
        in VulkanFrameDataSlice slice,
        out VulkanFrameDataReadScope scope)
    {
        scope = default;
        return resources.ReadbackFrameDataArena is { } arena &&
               slice.ArenaIdentity == arena.Identity &&
               arena.TryBeginRead(slice, out scope);
    }

    internal bool TryAcquireGpuStatsSlice(
        int slotIndex,
        ulong byteCount,
        out VulkanFrameDataSlice slice)
    {
        slice = default;
        VulkanFrameDataArena? arena = resources.GpuStatsFrameDataArena;
        if (arena is null || !arena.IsActive ||
            !arena.TryResetFrameSlot(
                checked((uint)slotIndex),
                arena.Generation,
                submissionCompletionProven: true))
        {
            return false;
        }

        return arena.TryAllocate(
            slotIndex,
            EVulkanFrameDataLane.Readback,
            byteCount,
            alignment: 16,
            out slice);
    }

    internal bool TryPrepareGpuStatsSlice(in VulkanFrameDataSlice slice)
        => resources.GpuStatsFrameDataArena is { } arena &&
           slice.ArenaIdentity == arena.Identity &&
           arena.TryPrepareFrameSlotForSubmission(
               checked((uint)slice.FrameSlot),
               slice.Generation);

    internal void MarkGpuStatsSliceSubmitted(in VulkanFrameDataSlice slice)
    {
        if (resources.GpuStatsFrameDataArena is { } arena &&
            slice.ArenaIdentity == arena.Identity)
        {
            arena.MarkFrameSlotSubmitted(
                checked((uint)slice.FrameSlot),
                slice.Generation);
        }
    }

    internal void CancelGpuStatsSliceSubmission(in VulkanFrameDataSlice slice)
    {
        if (resources.GpuStatsFrameDataArena is { } arena &&
            slice.ArenaIdentity == arena.Identity)
        {
            _ = arena.TryCancelFrameSlotSubmission(
                checked((uint)slice.FrameSlot),
                slice.Generation);
        }
    }

    internal bool TryCompleteGpuStatsSlice(in VulkanFrameDataSlice slice)
        => resources.GpuStatsFrameDataArena is { } arena &&
           slice.ArenaIdentity == arena.Identity &&
           arena.TryResetFrameSlot(
               checked((uint)slice.FrameSlot),
               slice.Generation,
               submissionCompletionProven: true);

    internal bool TryBeginGpuStatsRead(
        in VulkanFrameDataSlice slice,
        out VulkanFrameDataReadScope scope)
    {
        scope = default;
        return resources.GpuStatsFrameDataArena is { } arena &&
               slice.ArenaIdentity == arena.Identity &&
               arena.TryBeginRead(slice, out scope);
    }

    internal Result EnsureFence(ref Fence fence, string owner)
    {
        if (fence.Handle != 0)
            return Result.Success;

        Result result = CreateFence(FenceCreateFlags.SignaledBit, owner, out fence);
        return result;
    }

    internal Result CreateFence(
        FenceCreateFlags flags,
        string owner,
        out Fence fence)
    {
        FenceCreateInfo createInfo = new()
        {
            SType = StructureType.FenceCreateInfo,
            Flags = flags,
        };
        Result result = device.Api.CreateFence(device.Device, in createInfo, null, out fence);
        device.ObserveNativeResult($"vkCreateFence.{owner}", result);
        return result;
    }

    internal void DestroyFence(ref Fence fence)
    {
        if (fence.Handle == 0)
            return;
        device.Api.DestroyFence(device.Device, fence, null);
        fence = default;
    }

    internal void DestroyFence(Fence fence)
    {
        if (fence.Handle != 0)
            device.Api.DestroyFence(device.Device, fence, null);
    }

    internal bool TryCreateScreenshotResolveImage(
        VulkanTargetOutputContext target,
        VulkanScreenshotReadbackSlot slot,
        int slotIndex,
        Format format,
        uint width,
        uint height,
        ulong requiredBytes,
        out string? failure)
    {
        failure = null;
        ImageCreateInfo imageInfo = new()
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };

        Result createResult = target.CreateVulkanImageTracked(
            ref imageInfo,
            out Image resolveImage,
            $"ScreenshotReadback[{slotIndex}].Resolve");
        if (createResult != Result.Success)
        {
            failure = $"Failed to create Vulkan MSAA screenshot resolve image ({createResult}).";
            return false;
        }

        VulkanMemoryAllocation allocation = VulkanMemoryAllocation.Null;
        try
        {
            allocation = target.AllocateImageMemoryWithFallback(resolveImage, MemoryPropertyFlags.DeviceLocalBit);
            resources.Allocations.Images.Allocations[resolveImage.Handle] = allocation;
            Result bindResult = device.Api.BindImageMemory(
                device.Device,
                resolveImage,
                allocation.Memory,
                allocation.Offset);
            if (bindResult != Result.Success)
                throw new InvalidOperationException($"vkBindImageMemory returned {bindResult}.");

            slot.ResolveImage = resolveImage;
            slot.ResolveAllocation = allocation;
            slot.ResolveFormat = format;
            slot.ResolveWidth = width;
            slot.ResolveHeight = height;
            slot.ResolveByteCount = requiredBytes;
            return true;
        }
        catch (Exception exception)
        {
            resources.Allocations.Images.Allocations.TryRemove(resolveImage.Handle, out _);
            resources.Allocations.Images.DebugInfo.TryRemove(resolveImage.Handle, out _);
            target.DestroyVulkanImageImmediateTracked(resolveImage, "ScreenshotReadback.ResolveCreateFailure");
            if (!allocation.IsNull)
                target.FreeMemoryAllocation(allocation);
            failure = $"Failed to allocate Vulkan MSAA screenshot resolve image: {exception.Message}";
            return false;
        }
    }

    internal bool EnsureScreenshotResolveImage(
        VulkanCaptureOutputState capture,
        VulkanTargetOutputContext target,
        VulkanScreenshotReadbackSlot slot,
        int slotIndex,
        Format format,
        uint width,
        uint height,
        uint sourcePixelSize,
        out bool created,
        out string? failure)
    {
        created = false;
        failure = null;
        if (slot.ResolveImage.Handle != 0 &&
            slot.ResolveFormat == format &&
            slot.ResolveWidth == width &&
            slot.ResolveHeight == height)
        {
            return true;
        }

        if (slot.ResolveImage.Handle != 0)
            RetireScreenshotResolveImage(slot, "ScreenshotReadback.ResolveResize");

        ulong requiredBytes = checked((ulong)width * height * sourcePixelSize);
        EvictIdleScreenshotResolveImages(
            capture,
            slot,
            requiredBytes,
            MaximumScreenshotResolveImageBytes);
        ulong retainedBytes = GetRetainedScreenshotResolveImageBytes(capture);
        if (retainedBytes + requiredBytes > MaximumScreenshotResolveImageBytes)
        {
            failure = $"The Vulkan MSAA screenshot resolve cache would exceed its {MaximumScreenshotResolveImageBytes / (1024 * 1024)} MiB budget.";
            return false;
        }

        created = TryCreateScreenshotResolveImage(
            target,
            slot,
            slotIndex,
            format,
            width,
            height,
            requiredBytes,
            out failure);
        return created;
    }

    internal void RetireScreenshotResolveImage(
        VulkanScreenshotReadbackSlot slot,
        string owner)
    {
        if (slot.ResolveImage.Handle == 0 && slot.ResolveAllocation.IsNull)
            return;

        commands.ClearTrackedImageLayouts(slot.ResolveImage);
        resources.Images.RetireOwnedResources(new RetiredImageResources(
            slot.ResolveImage,
            slot.ResolveAllocation.Memory,
            default,
            [],
            default,
            0),
            owner);
        slot.ResolveImage = default;
        slot.ResolveAllocation = default;
        slot.ResolveFormat = default;
        slot.ResolveWidth = 0;
        slot.ResolveHeight = 0;
        slot.ResolveByteCount = 0;
    }

    private void EvictIdleScreenshotResolveImages(
        VulkanCaptureOutputState capture,
        VulkanScreenshotReadbackSlot requestingSlot,
        ulong requiredBytes,
        ulong maximumRetainedBytes)
    {
        ulong retainedBytes = GetRetainedScreenshotResolveImageBytes(capture);
        if (retainedBytes + requiredBytes <= maximumRetainedBytes)
            return;

        VulkanScreenshotReadbackSlot?[] slots = capture.ScreenshotReadbackSlots;
        for (int index = 0; index < slots.Length; index++)
        {
            VulkanScreenshotReadbackSlot? candidate = slots[index];
            if (candidate is null ||
                ReferenceEquals(candidate, requestingSlot) ||
                candidate.ResolveImage.Handle == 0 ||
                Volatile.Read(ref candidate.State) != (int)EVulkanScreenshotReadbackSlotState.Idle)
            {
                continue;
            }

            RetireScreenshotResolveImage(
                candidate,
                "ScreenshotReadback.ResolveBudgetEviction");
            retainedBytes = GetRetainedScreenshotResolveImageBytes(capture);
            if (retainedBytes + requiredBytes <= maximumRetainedBytes)
                return;
        }
    }

    private static ulong GetRetainedScreenshotResolveImageBytes(
        VulkanCaptureOutputState capture)
    {
        ulong total = 0;
        VulkanScreenshotReadbackSlot?[] slots = capture.ScreenshotReadbackSlots;
        for (int index = 0; index < slots.Length; index++)
            total += slots[index]?.ResolveByteCount ?? 0;
        return total;
    }
}
