using System.Diagnostics;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>Command-owned RTX IO encoding and transient command storage.</summary>
internal sealed partial class VulkanCommandRuntime
{
    private const bool EnableNvIndirectCopyUploads = false;

    internal bool CanUseNvIndirectBufferCopyUploads
        => EnableNvIndirectCopyUploads
            && DeviceContext.SupportsNvCopyMemoryIndirectCommands
            && DeviceContext.Capabilities.Supports(EVulkanDeviceCapability.BufferDeviceAddress);

    internal ulong GetBufferDeviceAddress(Buffer buffer)
        => DeviceContext.GetBufferDeviceAddress(buffer);

    internal unsafe bool TryCopyBufferViaIndirectNv(
        Buffer source,
        Buffer destination,
        ulong size,
        ulong sourceOffset,
        ulong destinationOffset)
    {
        if (!CanUseNvIndirectBufferCopyUploads)
            return false;

        ulong sourceAddress = DeviceContext.GetBufferDeviceAddress(source);
        ulong destinationAddress = DeviceContext.GetBufferDeviceAddress(destination);
        if (sourceAddress == 0 || destinationAddress == 0)
            return false;

        CopyMemoryIndirectCommandNV command = new()
        {
            SrcAddress = sourceAddress + sourceOffset,
            DstAddress = destinationAddress + destinationOffset,
            Size = size,
        };
        if (!TryCreateIndirectCopyCommandBuffer(
                command,
                out Buffer commandBuffer,
                out DeviceMemory commandMemory,
                out ulong commandAddress))
            return false;

        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            bool succeeded = TryCopyMemoryIndirectNv(
                commandAddress,
                1,
                (uint)sizeof(CopyMemoryIndirectCommandNV));
            if (succeeded)
                RuntimeEngine.Rendering.Stats.RtxIo.RecordRtxIoCopyIndirect(
                    (long)Math.Min(size, (ulong)long.MaxValue),
                    stopwatch.Elapsed);
            return succeeded;
        }
        finally
        {
            DestroyIndirectCommandBuffer(commandBuffer, commandMemory);
        }
    }

    internal unsafe bool TryCopyBufferToImageViaIndirectNv(
        Buffer source,
        ulong sourceOffset,
        Image destination,
        ImageLayout destinationLayout,
        ImageSubresourceLayers subresource,
        Offset3D imageOffset,
        Extent3D imageExtent)
    {
        if (!CanUseNvIndirectBufferCopyUploads)
            return false;

        ulong sourceAddress = DeviceContext.GetBufferDeviceAddress(source);
        if (sourceAddress == 0)
            return false;

        CopyMemoryToImageIndirectCommandNV command = new()
        {
            SrcAddress = sourceAddress + sourceOffset,
            ImageSubresource = subresource,
            ImageOffset = imageOffset,
            ImageExtent = imageExtent,
        };
        if (!TryCreateIndirectCopyCommandBuffer(
                command,
                out Buffer commandBuffer,
                out DeviceMemory commandMemory,
                out ulong commandAddress))
            return false;

        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            bool succeeded = TryCopyMemoryToImageIndirectNv(
                commandAddress,
                1,
                (uint)sizeof(CopyMemoryToImageIndirectCommandNV),
                destination,
                destinationLayout,
                new ReadOnlySpan<ImageSubresourceLayers>(in subresource));
            if (succeeded)
            {
                ulong pixels = Math.Max(imageExtent.Width, 1u)
                    * Math.Max(imageExtent.Height, 1u)
                    * Math.Max(imageExtent.Depth, 1u);
                RuntimeEngine.Rendering.Stats.RtxIo.RecordRtxIoCopyIndirect(
                    (long)Math.Min(pixels, (ulong)long.MaxValue),
                    stopwatch.Elapsed);
            }
            return succeeded;
        }
        finally
        {
            DestroyIndirectCommandBuffer(commandBuffer, commandMemory);
        }
    }

    internal bool TryCopyMemoryIndirectNv(ulong commandAddress, uint copyCount, uint stride)
    {
        if (!DeviceContext.SupportsNvCopyMemoryIndirectCommands || copyCount == 0)
            return false;

        try
        {
            using CommandScope scope = NewCommandScope();
            DeviceContext.ExtensionFunctions.NvCopyMemoryIndirect!.CmdCopyMemoryIndirect(
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

    internal unsafe bool TryCopyMemoryToImageIndirectNv(
        ulong commandAddress,
        uint copyCount,
        uint stride,
        Image destination,
        ImageLayout destinationLayout,
        ReadOnlySpan<ImageSubresourceLayers> subresources)
    {
        if (!DeviceContext.SupportsNvCopyMemoryIndirectCommands || copyCount == 0 || subresources.IsEmpty)
            return false;

        try
        {
            using CommandScope scope = NewCommandScope();
            fixed (ImageSubresourceLayers* pointer = subresources)
            {
                DeviceContext.ExtensionFunctions.NvCopyMemoryIndirect!.CmdCopyMemoryToImageIndirect(
                    scope.CommandBuffer,
                    commandAddress,
                    copyCount,
                    stride,
                    destination,
                    destinationLayout,
                    pointer);
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarning($"[Vulkan] VK_NV_copy_memory_indirect image copy failed: {ex.Message}");
            return false;
        }
    }

    internal bool TryDecompressBufferGDeflateNv(
        Buffer source,
        ulong sourceOffset,
        ulong compressedSize,
        Buffer destination,
        ulong destinationOffset,
        ulong decompressedSize)
    {
        if (!DeviceContext.SupportsNvMemoryDecompressionCommands
            || compressedSize == 0
            || decompressedSize == 0)
            return false;

        MemoryDecompressionMethodFlagsNV method = DeviceContext.PreferredNvMemoryDecompressionMethod;
        ulong sourceAddress = DeviceContext.GetBufferDeviceAddress(source);
        ulong destinationAddress = DeviceContext.GetBufferDeviceAddress(destination);
        if (method == 0 || sourceAddress == 0 || destinationAddress == 0)
            return false;

        DecompressMemoryRegionNV region = new()
        {
            SrcAddress = sourceAddress + sourceOffset,
            DstAddress = destinationAddress + destinationOffset,
            CompressedSize = compressedSize,
            DecompressedSize = decompressedSize,
            DecompressionMethod = method,
        };
        return TryDecompressMemoryNv(new ReadOnlySpan<DecompressMemoryRegionNV>(in region));
    }

    internal bool TryDecompressMemoryNv(ReadOnlySpan<DecompressMemoryRegionNV> regions)
    {
        if (!DeviceContext.SupportsNvMemoryDecompressionCommands || regions.IsEmpty)
            return false;

        long compressedBytes = 0;
        long decompressedBytes = 0;
        for (int index = 0; index < regions.Length; index++)
        {
            compressedBytes += (long)Math.Min(regions[index].CompressedSize, (ulong)long.MaxValue);
            decompressedBytes += (long)Math.Min(regions[index].DecompressedSize, (ulong)long.MaxValue);
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            using CommandScope scope = NewCommandScope();
            DeviceContext.ExtensionFunctions.NvMemoryDecompression!.CmdDecompressMemory(
                scope.CommandBuffer,
                regions);
            RuntimeEngine.Rendering.Stats.RtxIo.RecordRtxIoDecompression(
                compressedBytes,
                decompressedBytes,
                stopwatch.Elapsed);
            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarning($"[Vulkan] VK_NV_memory_decompression failed: {ex.Message}");
            return false;
        }
    }

    internal bool TryDecompressMemoryIndirectCountNv(
        ulong indirectCommandsAddress,
        ulong indirectCommandsCountAddress,
        uint stride)
    {
        if (!DeviceContext.SupportsNvMemoryDecompressionCommands)
            return false;

        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            using CommandScope scope = NewCommandScope();
            DeviceContext.ExtensionFunctions.NvMemoryDecompression!.CmdDecompressMemoryIndirectCount(
                scope.CommandBuffer,
                indirectCommandsAddress,
                indirectCommandsCountAddress,
                stride);
            RuntimeEngine.Rendering.Stats.RtxIo.RecordRtxIoDecompression(0, 0, stopwatch.Elapsed);
            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarning($"[Vulkan] VK_NV_memory_decompression indirect failed: {ex.Message}");
            return false;
        }
    }

    private unsafe bool TryCreateIndirectCopyCommandBuffer<TCommand>(
        TCommand command,
        out Buffer commandBuffer,
        out DeviceMemory commandMemory,
        out ulong commandAddress)
        where TCommand : unmanaged
    {
        VulkanBackendObjectContext context = RequireBackendObjectContext();
        commandBuffer = default;
        commandMemory = default;
        commandAddress = 0;
        ulong commandSize = (ulong)sizeof(TCommand);

        try
        {
            (commandBuffer, commandMemory) = ResourceRuntime.Buffers.Create(
                context,
                commandSize,
                BufferUsageFlags.TransferSrcBit | BufferUsageFlags.ShaderDeviceAddressBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                enableDeviceAddress: true,
                owner: "RtxIo.IndirectCommand");
            if (!ResourceRuntime.Buffers.TryCreateMappedSlice(
                    context, commandBuffer, commandMemory, 0, commandSize, out VulkanMappedMemorySlice slice) ||
                !ResourceRuntime.Buffers.TryAcquireWrite(context, in slice, out VulkanMappedMemoryWriteLease lease))
            {
                DestroyIndirectCommandBuffer(commandBuffer, commandMemory);
                commandBuffer = default;
                commandMemory = default;
                return false;
            }

            using (lease)
            {
                if (lease.Bytes.Length < sizeof(TCommand))
                    return false;
                Unsafe.WriteUnaligned(ref lease.Bytes[0], command);
            }

            commandAddress = DeviceContext.GetBufferDeviceAddress(commandBuffer);
            if (commandAddress != 0)
                return true;

            DestroyIndirectCommandBuffer(commandBuffer, commandMemory);
            commandBuffer = default;
            commandMemory = default;
            return false;
        }
        catch
        {
            DestroyIndirectCommandBuffer(commandBuffer, commandMemory);
            commandBuffer = default;
            commandMemory = default;
            commandAddress = 0;
            return false;
        }
    }

    private VulkanBackendObjectContext RequireBackendObjectContext()
        => ResourceRuntime.BackendObjectContext
            ?? throw new InvalidOperationException("RTX IO command encoding requires a published backend-object context.");

    private void DestroyIndirectCommandBuffer(Buffer buffer, DeviceMemory memory)
    {
        if (buffer.Handle == 0 && memory.Handle == 0)
            return;
        ResourceRuntime.Buffers.Destroy(
            RequireBackendObjectContext(),
            buffer,
            memory,
            "RtxIo.IndirectCommand");
    }
}
