using System;
using System.Diagnostics;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private ulong GetBufferDeviceAddress(Buffer buffer)
    {
        if (!SupportsBufferDeviceAddress)
            return 0;

        BufferDeviceAddressInfo info = new()
        {
            SType = StructureType.BufferDeviceAddressInfo,
            PNext = null,
            Buffer = buffer,
        };

        return Api!.GetBufferDeviceAddress(device, &info);
    }

    private bool TryCreateIndirectCopyCommandBuffer<TCommand>(
        TCommand command,
        out Buffer commandBuffer,
        out DeviceMemory commandMemory,
        out ulong commandAddress)
        where TCommand : unmanaged
    {
        commandBuffer = default;
        commandMemory = default;
        commandAddress = 0;

        ulong commandSize = (ulong)sizeof(TCommand);

        try
        {
            (commandBuffer, commandMemory) = CreateBufferRaw(
                commandSize,
                BufferUsageFlags.TransferSrcBit | BufferUsageFlags.ShaderDeviceAddressBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                enableDeviceAddress: true);

            void* mappedPtr = null;
            if (Api!.MapMemory(device, commandMemory, 0, commandSize, 0, &mappedPtr) != Result.Success)
            {
                DestroyBufferRaw(commandBuffer, commandMemory);
                commandBuffer = default;
                commandMemory = default;
                return false;
            }

            try
            {
                *(TCommand*)mappedPtr = command;
            }
            finally
            {
                Api.UnmapMemory(device, commandMemory);
            }

            commandAddress = GetBufferDeviceAddress(commandBuffer);
            if (commandAddress == 0)
            {
                DestroyBufferRaw(commandBuffer, commandMemory);
                commandBuffer = default;
                commandMemory = default;
                return false;
            }

            return true;
        }
        catch
        {
            DestroyBufferRaw(commandBuffer, commandMemory);
            commandBuffer = default;
            commandMemory = default;
            commandAddress = 0;
            return false;
        }
    }

    public bool TryCopyBufferViaIndirectNv(
        Buffer srcBuffer,
        Buffer dstBuffer,
        ulong size,
        ulong srcOffset = 0,
        ulong dstOffset = 0)
    {
        if (!SupportsNvCopyMemoryIndirect || !SupportsBufferDeviceAddress || size == 0)
            return false;

        ulong srcAddress = GetBufferDeviceAddress(srcBuffer);
        ulong dstAddress = GetBufferDeviceAddress(dstBuffer);
        if (srcAddress == 0 || dstAddress == 0)
            return false;

        CopyMemoryIndirectCommandNV command = new()
        {
            SrcAddress = srcAddress + srcOffset,
            DstAddress = dstAddress + dstOffset,
            Size = size,
        };

        if (!TryCreateIndirectCopyCommandBuffer(command, out Buffer commandBuffer, out DeviceMemory commandMemory, out ulong commandAddress))
            return false;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            bool success = TryCopyMemoryIndirectNv(commandAddress, 1, (uint)sizeof(CopyMemoryIndirectCommandNV));
            if (success)
                Engine.Rendering.Stats.RecordRtxIoCopyIndirect((long)Math.Min(size, long.MaxValue), stopwatch.Elapsed);
            return success;
        }
        finally
        {
            DestroyBufferRaw(commandBuffer, commandMemory);
        }
    }

    public bool TryCopyBufferToImageViaIndirectNv(
        Buffer srcBuffer,
        ulong srcOffset,
        Image dstImage,
        ImageLayout dstImageLayout,
        ImageSubresourceLayers imageSubresource,
        Offset3D imageOffset,
        Extent3D imageExtent)
    {
        if (!SupportsNvCopyMemoryIndirect || !SupportsBufferDeviceAddress)
            return false;

        ulong srcAddress = GetBufferDeviceAddress(srcBuffer);
        if (srcAddress == 0)
            return false;

        CopyMemoryToImageIndirectCommandNV command = new()
        {
            SrcAddress = srcAddress + srcOffset,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = imageSubresource,
            ImageOffset = imageOffset,
            ImageExtent = imageExtent,
        };

        if (!TryCreateIndirectCopyCommandBuffer(command, out Buffer commandBuffer, out DeviceMemory commandMemory, out ulong commandAddress))
            return false;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            bool success = TryCopyMemoryToImageIndirectNv(
                commandAddress,
                1,
                (uint)sizeof(CopyMemoryToImageIndirectCommandNV),
                dstImage,
                dstImageLayout,
                [imageSubresource]);

            if (success)
            {
                ulong pixelCount = Math.Max(imageExtent.Width, 1u) * Math.Max(imageExtent.Height, 1u) * Math.Max(imageExtent.Depth, 1u);
                Engine.Rendering.Stats.RecordRtxIoCopyIndirect((long)Math.Min(pixelCount, long.MaxValue), stopwatch.Elapsed);
            }

            return success;
        }
        finally
        {
            DestroyBufferRaw(commandBuffer, commandMemory);
        }
    }

    /// <summary>
    /// Attempts to execute VK_NV_copy_memory_indirect for commands stored on-GPU.
    /// commandAddress points to an array of CopyMemoryIndirectCommandNV entries.
    /// </summary>
    public bool TryCopyMemoryIndirectNv(ulong commandAddress, uint copyCount, uint stride)
    {
        if (!SupportsNvCopyMemoryIndirect || copyCount == 0)
            return false;

        try
        {
            using var scope = NewCommandScope();
            _nvCopyMemoryIndirect!.CmdCopyMemoryIndirect(
                scope.CommandBuffer,
                commandAddress,
                copyCount,
                stride);
            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarning($"[Vulkan] VK_NV_copy_memory_indirect failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Attempts to execute VK_NV_copy_memory_indirect copy-to-image operations
    /// for commands stored on-GPU.
    /// </summary>
    public bool TryCopyMemoryToImageIndirectNv(
        ulong commandAddress,
        uint copyCount,
        uint stride,
        Image dstImage,
        ImageLayout dstImageLayout,
        ReadOnlySpan<ImageSubresourceLayers> imageSubresources)
    {
        if (!SupportsNvCopyMemoryIndirect || copyCount == 0 || imageSubresources.IsEmpty)
            return false;

        try
        {
            using var scope = NewCommandScope();
            fixed (ImageSubresourceLayers* pImageSubresources = imageSubresources)
            {
                _nvCopyMemoryIndirect!.CmdCopyMemoryToImageIndirect(
                    scope.CommandBuffer,
                    commandAddress,
                    copyCount,
                    stride,
                    dstImage,
                    dstImageLayout,
                    pImageSubresources);
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarning($"[Vulkan] VK_NV_copy_memory_indirect image copy failed: {ex.Message}");
            return false;
        }
    }
}
