using System;
using System.Diagnostics;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private MemoryDecompressionMethodFlagsNV SelectNvDecompressionMethod()
    {
        ulong methodsMask = (ulong)NvMemoryDecompressionMethods;
        if (methodsMask == 0)
            return 0;

        ulong firstBit = methodsMask & (~methodsMask + 1);
        return (MemoryDecompressionMethodFlagsNV)firstBit;
    }

    public bool TryDecompressBufferGDeflateNv(
        Buffer srcBuffer,
        ulong srcOffset,
        ulong compressedSize,
        Buffer dstBuffer,
        ulong dstOffset,
        ulong decompressedSize)
    {
        if (!SupportsNvMemoryDecompression || !SupportsBufferDeviceAddress || compressedSize == 0 || decompressedSize == 0)
            return false;

        MemoryDecompressionMethodFlagsNV method = SelectNvDecompressionMethod();
        if (method == 0)
            return false;

        BufferDeviceAddressInfo srcInfo = new()
        {
            SType = StructureType.BufferDeviceAddressInfo,
            PNext = null,
            Buffer = srcBuffer,
        };

        BufferDeviceAddressInfo dstInfo = new()
        {
            SType = StructureType.BufferDeviceAddressInfo,
            PNext = null,
            Buffer = dstBuffer,
        };

        ulong srcAddress = Api!.GetBufferDeviceAddress(device, &srcInfo);
        ulong dstAddress = Api.GetBufferDeviceAddress(device, &dstInfo);
        if (srcAddress == 0 || dstAddress == 0)
            return false;

        DecompressMemoryRegionNV region = new()
        {
            SrcAddress = srcAddress + srcOffset,
            DstAddress = dstAddress + dstOffset,
            CompressedSize = compressedSize,
            DecompressedSize = decompressedSize,
            DecompressionMethod = method,
        };

        return TryDecompressMemoryNv(region);
    }

    /// <summary>
    /// Attempts to execute a Vulkan NV memory decompression command for a single region.
    /// The source and destination addresses are GPU virtual addresses (buffer device addresses),
    /// not CPU pointers.
    /// </summary>
    public bool TryDecompressMemoryNv(DecompressMemoryRegionNV region)
        => TryDecompressMemoryNv(new ReadOnlySpan<DecompressMemoryRegionNV>(in region));

    /// <summary>
    /// Attempts to execute Vulkan NV memory decompression for one or more regions.
    /// Requires VK_NV_memory_decompression support to be enabled at device creation time.
    /// </summary>
    public bool TryDecompressMemoryNv(ReadOnlySpan<DecompressMemoryRegionNV> regions)
    {
        if (!SupportsNvMemoryDecompression || regions.IsEmpty)
            return false;

        long compressedBytes = 0;
        long decompressedBytes = 0;
        for (int i = 0; i < regions.Length; i++)
        {
            compressedBytes += (long)Math.Min(regions[i].CompressedSize, (ulong)long.MaxValue);
            decompressedBytes += (long)Math.Min(regions[i].DecompressedSize, (ulong)long.MaxValue);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var scope = NewCommandScope();
            _nvMemoryDecompression!.CmdDecompressMemory(scope.CommandBuffer, regions);
            Engine.Rendering.Stats.RecordRtxIoDecompression(compressedBytes, decompressedBytes, stopwatch.Elapsed);
            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarning($"[Vulkan] VK_NV_memory_decompression failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Attempts to execute Vulkan NV indirect-count memory decompression.
    /// </summary>
    public bool TryDecompressMemoryIndirectCountNv(
        ulong indirectCommandsAddress,
        ulong indirectCommandsCountAddress,
        uint stride)
    {
        if (!SupportsNvMemoryDecompression)
            return false;

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var scope = NewCommandScope();
            _nvMemoryDecompression!.CmdDecompressMemoryIndirectCount(
                scope.CommandBuffer,
                indirectCommandsAddress,
                indirectCommandsCountAddress,
                stride);
            Engine.Rendering.Stats.RecordRtxIoDecompression(0, 0, stopwatch.Elapsed);
            return true;
        }
        catch (Exception ex)
        {
            Debug.VulkanWarning($"[Vulkan] VK_NV_memory_decompression indirect failed: {ex.Message}");
            return false;
        }
    }
}
