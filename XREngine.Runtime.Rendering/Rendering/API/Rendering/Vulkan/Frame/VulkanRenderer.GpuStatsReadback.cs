using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const int GpuRenderStatsReadbackRingSize = 32;
    private const uint GpuRenderStatsReadbackInlineUIntCapacity = 64u;

    private enum GpuRenderStatsReadbackKind
    {
        DrawCountBuffer,
        StatsBuffer,
    }

    private sealed class GpuRenderStatsReadbackSlot
    {
        public Buffer StagingBuffer;
        public DeviceMemory StagingMemory;
        public ulong CapacityBytes;
        public uint ByteCount;
        public uint ElementCount;
        public CommandPool CommandPool;
        public CommandBuffer CommandBuffer;
        public Fence Fence;
        public bool Active;
        public bool PublishDraws;
        public bool PublishTriangles;
        public GpuRenderStatsReadbackKind Kind;
        public string SourceName = string.Empty;
        public ulong SourceHandle;
    }

    private readonly GpuRenderStatsReadbackSlot?[] _gpuRenderStatsReadbackSlots =
        new GpuRenderStatsReadbackSlot?[GpuRenderStatsReadbackRingSize];
    private readonly Dictionary<string, ulong> _gpuRenderStatsTraceHashes = [];
    private int _gpuRenderStatsReadbackCursor;

    public override void PollGpuRenderStatsReadbacks()
    {
        if (!RuntimeEngine.IsRenderThread)
        {
            RuntimeEngine.EnqueueMainThreadTask(
                PollGpuRenderStatsReadbacks,
                "VulkanRenderer.PollGpuRenderStatsReadbacks",
                RenderThreadJobKind.Readback);
            return;
        }

        for (int i = 0; i < _gpuRenderStatsReadbackSlots.Length; ++i)
        {
            GpuRenderStatsReadbackSlot? slot = _gpuRenderStatsReadbackSlots[i];
            if (slot is not null && slot.Active)
                TryConsumeGpuRenderStatsReadback(slot);
        }
    }

    public override bool QueueGpuRenderDrawCountReadback(
        XRDataBuffer drawCountBuffer,
        uint countByteOffset = 0,
        uint countElementCount = 1)
        => QueueGpuRenderStatsReadback(
            drawCountBuffer,
            countByteOffset,
            checked(countElementCount * (uint)sizeof(uint)),
            countElementCount,
            GpuRenderStatsReadbackKind.DrawCountBuffer,
            publishDraws: true,
            publishTriangles: false);

    public override bool QueueGpuRenderStatsBufferReadback(
        XRDataBuffer statsBuffer,
        bool publishDraws,
        bool publishTriangles)
    {
        if (!publishDraws && !publishTriangles)
            return false;

        return QueueGpuRenderStatsReadback(
            statsBuffer,
            0u,
            checked(GpuStatsLayout.FieldCount * (uint)sizeof(uint)),
            GpuStatsLayout.FieldCount,
            GpuRenderStatsReadbackKind.StatsBuffer,
            publishDraws,
            publishTriangles);
    }

    private bool QueueGpuRenderStatsReadback(
        XRDataBuffer sourceBuffer,
        uint sourceByteOffset,
        uint byteCount,
        uint elementCount,
        GpuRenderStatsReadbackKind kind,
        bool publishDraws,
        bool publishTriangles)
    {
        if (_deviceLost || !RuntimeEngine.Rendering.Stats.EnableTracking || byteCount == 0u || elementCount == 0u)
            return false;

        if (!RuntimeEngine.IsRenderThread)
        {
            RuntimeEngine.EnqueueMainThreadTask(
                () => QueueGpuRenderStatsReadback(
                    sourceBuffer,
                    sourceByteOffset,
                    byteCount,
                    elementCount,
                    kind,
                    publishDraws,
                    publishTriangles),
                "VulkanRenderer.QueueGpuRenderStatsReadback",
                RenderThreadJobKind.Readback);
            return false;
        }

        ulong requestedEnd = (ulong)sourceByteOffset + byteCount;
        if (requestedEnd > sourceBuffer.Length)
            return false;

        PollGpuRenderStatsReadbacks();

        if (GenericToAPI<VkDataBuffer>(sourceBuffer) is not { } sourceVkBuffer ||
            !sourceVkBuffer.TryEnsureReadyForRendering(allowSynchronousUpload: false) ||
            sourceVkBuffer.BufferHandle is not { } sourceHandle ||
            sourceHandle.Handle == 0 ||
            !sourceVkBuffer.LastUsageFlags.HasFlag(BufferUsageFlags.TransferSrcBit))
        {
            return false;
        }

        GpuRenderStatsReadbackSlot? slot = AcquireGpuRenderStatsReadbackSlot();
        if (slot is null || !EnsureGpuRenderStatsReadbackResources(slot, byteCount))
            return false;

        Result resetFenceResult = Api!.ResetFences(device, 1, in slot.Fence);
        Result resetCommandResult = ResetVulkanCommandBufferTracked(slot.CommandBuffer);
        if (resetFenceResult != Result.Success || resetCommandResult != Result.Success)
            return false;

        CommandBufferBeginInfo beginInfo = new()
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        if (Api.BeginCommandBuffer(slot.CommandBuffer, in beginInfo) != Result.Success)
            return false;

        ResetCommandBufferBindState(slot.CommandBuffer);

        BufferMemoryBarrier sourceBarrier = new()
        {
            SType = StructureType.BufferMemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderWriteBit | AccessFlags.TransferWriteBit | AccessFlags.MemoryWriteBit,
            DstAccessMask = AccessFlags.TransferReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer = sourceHandle,
            Offset = sourceByteOffset,
            Size = byteCount,
        };
        CmdPipelineBarrierTracked(
            slot.CommandBuffer,
            PipelineStageFlags.AllCommandsBit,
            PipelineStageFlags.TransferBit,
            0,
            0,
            null,
            1,
            &sourceBarrier,
            0,
            null);

        BufferCopy copy = new()
        {
            SrcOffset = sourceByteOffset,
            DstOffset = 0,
            Size = byteCount,
        };
        CmdCopyBufferTracked(slot.CommandBuffer, sourceHandle, slot.StagingBuffer, 1, &copy);

        if (Api.EndCommandBuffer(slot.CommandBuffer) != Result.Success)
            return false;

        CommandBuffer readbackCommandBuffer = slot.CommandBuffer;
        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &readbackCommandBuffer,
        };

        Result submitResult;
        lock (_oneTimeSubmitLock)
            submitResult = SubmitToQueueTracked(graphicsQueue, ref submitInfo, slot.Fence);

        if (submitResult != Result.Success)
        {
            if (submitResult == Result.ErrorDeviceLost)
                MarkDeviceLost();
            return false;
        }

        slot.ByteCount = byteCount;
        slot.ElementCount = elementCount;
        slot.Kind = kind;
        slot.PublishDraws = publishDraws;
        slot.PublishTriangles = publishTriangles;
        slot.SourceName = sourceBuffer.AttributeName ?? sourceBuffer.Target.ToString();
        slot.SourceHandle = sourceHandle.Handle;
        slot.Active = true;
        return true;
    }

    private GpuRenderStatsReadbackSlot? AcquireGpuRenderStatsReadbackSlot()
    {
        for (int i = 0; i < _gpuRenderStatsReadbackSlots.Length; ++i)
        {
            int index = (_gpuRenderStatsReadbackCursor + i) % _gpuRenderStatsReadbackSlots.Length;
            GpuRenderStatsReadbackSlot slot = _gpuRenderStatsReadbackSlots[index] ??= new GpuRenderStatsReadbackSlot();
            if (slot.Active && !TryConsumeGpuRenderStatsReadback(slot))
                continue;

            _gpuRenderStatsReadbackCursor = (index + 1) % _gpuRenderStatsReadbackSlots.Length;
            return slot;
        }

        return null;
    }

    private bool EnsureGpuRenderStatsReadbackResources(GpuRenderStatsReadbackSlot slot, uint byteCount)
    {
        if (slot.CommandBuffer.Handle == 0)
        {
            slot.CommandPool = GetThreadCommandPool();
            CommandBufferAllocateInfo allocateInfo = new()
            {
                SType = StructureType.CommandBufferAllocateInfo,
                Level = CommandBufferLevel.Primary,
                CommandPool = slot.CommandPool,
                CommandBufferCount = 1,
            };
            if (AllocateVulkanCommandBuffersTracked(
                    ref allocateInfo,
                    out slot.CommandBuffer,
                    "GpuStatsReadback") != Result.Success)
            {
                return false;
            }
        }

        if (slot.Fence.Handle == 0)
        {
            FenceCreateInfo fenceCreateInfo = new()
            {
                SType = StructureType.FenceCreateInfo,
                Flags = FenceCreateFlags.SignaledBit,
            };
            if (Api!.CreateFence(device, in fenceCreateInfo, null, out slot.Fence) != Result.Success)
                return false;

            SetDebugObjectName(ObjectType.Fence, slot.Fence.Handle, "GpuStatsReadback.Fence");
        }

        if (slot.CapacityBytes >= byteCount && slot.StagingBuffer.Handle != 0)
            return true;

        if (slot.StagingBuffer.Handle != 0)
            DestroyBuffer(slot.StagingBuffer, slot.StagingMemory);

        (slot.StagingBuffer, slot.StagingMemory) = CreateReadbackBuffer(byteCount);
        slot.CapacityBytes = byteCount;
        SetDebugObjectName(ObjectType.Buffer, slot.StagingBuffer.Handle, "GpuStatsReadback.Staging");
        return slot.StagingBuffer.Handle != 0;
    }

    private bool TryConsumeGpuRenderStatsReadback(GpuRenderStatsReadbackSlot slot)
    {
        if (!slot.Active)
            return true;

        Result fenceResult = Api!.GetFenceStatus(device, slot.Fence);
        if (fenceResult is Result.NotReady or Result.Timeout)
            return false;
        if (fenceResult != Result.Success)
        {
            if (fenceResult == Result.ErrorDeviceLost)
                MarkDeviceLost();
            return false;
        }

        NotifyVulkanFenceCompleted(slot.Fence);

        uint inlineCount = Math.Min(slot.ElementCount, GpuRenderStatsReadbackInlineUIntCapacity);
        Span<uint> inlineValues = stackalloc uint[(int)inlineCount];
        uint[]? rented = null;
        Span<uint> values = slot.ElementCount <= GpuRenderStatsReadbackInlineUIntCapacity
            ? inlineValues[..(int)slot.ElementCount]
            : (rented = ArrayPool<uint>.Shared.Rent((int)slot.ElementCount)).AsSpan(0, (int)slot.ElementCount);

        try
        {
            if (!TryMapReadbackMemory(slot.StagingBuffer, slot.StagingMemory, 0, slot.ByteCount, out void* mappedPtr))
                return false;

            try
            {
                new ReadOnlySpan<uint>(mappedPtr, (int)slot.ElementCount).CopyTo(values);
            }
            finally
            {
                UnmapBufferMemory(slot.StagingBuffer, slot.StagingMemory);
            }

            PublishGpuRenderStatsReadback(slot, values);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<uint>.Shared.Return(rented);

            slot.Active = false;
            slot.ByteCount = 0u;
            slot.ElementCount = 0u;
            slot.PublishDraws = false;
            slot.PublishTriangles = false;
        }

        return true;
    }

    private void PublishGpuRenderStatsReadback(
        GpuRenderStatsReadbackSlot slot,
        ReadOnlySpan<uint> values)
    {
        switch (slot.Kind)
        {
            case GpuRenderStatsReadbackKind.DrawCountBuffer:
            {
                ulong drawCount = 0ul;
                for (int i = 0; i < values.Length; ++i)
                    drawCount += values[i];

                if (slot.PublishDraws && drawCount > 0ul)
                    RuntimeEngine.Rendering.Stats.Frame.IncrementDrawCalls(SaturateGpuStatsToInt(drawCount));

                if (IndirectTraceEnabled)
                {
                    Debug.Vulkan("[VulkanIndirect] delayed draw counts source={0} elements={1} sum={2}", slot.SourceName, values.Length, drawCount);
                    WriteGpuRenderStatsTraceIfChanged(slot.SourceName, slot.SourceHandle, "draw-counts", values);
                }
                break;
            }
            case GpuRenderStatsReadbackKind.StatsBuffer:
            {
                uint draws = values.Length > (int)GpuStatsLayout.StatsDrawCount
                    ? values[(int)GpuStatsLayout.StatsDrawCount]
                    : 0u;
                uint triangles = values.Length > (int)GpuStatsLayout.StatsTriangleCount
                    ? values[(int)GpuStatsLayout.StatsTriangleCount]
                    : 0u;

                if (slot.PublishDraws && draws > 0u)
                    RuntimeEngine.Rendering.Stats.Frame.IncrementDrawCalls(SaturateGpuStatsToInt(draws));
                if (slot.PublishTriangles && triangles > 0u)
                    RuntimeEngine.Rendering.Stats.Frame.AddTrianglesRendered(SaturateGpuStatsToInt(triangles));

                if (values.Length > (int)GpuStatsLayout.MeshletTaskRecordsHiZCulled)
                {
                    RuntimeEngine.Rendering.Stats.GpuMeshlets.RecordGpuMeshletTaskStats(
                        values[(int)GpuStatsLayout.MeshletTaskRecordsEmitted],
                        values[(int)GpuStatsLayout.MeshletTaskRecordsFrustumCulled],
                        values[(int)GpuStatsLayout.MeshletTaskRecordsConeCulled],
                        values[(int)GpuStatsLayout.MeshletTaskRecordsHiZCulled]);
                }

                if (IndirectTraceEnabled && values.Length > (int)GpuStatsLayout.StatsRejectedDistance)
                {
                    Debug.Vulkan(
                        "[VulkanIndirect] delayed stats input={0} culled={1} draws={2} triangles={3} frustumRejected={4} distanceRejected={5}",
                        values[(int)GpuStatsLayout.StatsInputCount],
                        values[(int)GpuStatsLayout.StatsCulledCount],
                        draws,
                        triangles,
                        values[(int)GpuStatsLayout.StatsRejectedFrustum],
                        values[(int)GpuStatsLayout.StatsRejectedDistance]);
                    WriteGpuRenderStatsTraceIfChanged(slot.SourceName, slot.SourceHandle, "stats", values);
                }
                break;
            }
        }
    }

    private void WriteGpuRenderStatsTraceIfChanged(
        string sourceName,
        ulong sourceHandle,
        string kind,
        ReadOnlySpan<uint> values)
    {
        ulong hash = 1469598103934665603ul;
        for (int i = 0; i < values.Length; ++i)
        {
            hash ^= values[i];
            hash *= 1099511628211ul;
        }

        string key = $"{kind}:{sourceName}:0x{sourceHandle:X}";
        if (_gpuRenderStatsTraceHashes.TryGetValue(key, out ulong previousHash) && previousHash == hash)
            return;

        _gpuRenderStatsTraceHashes[key] = hash;

        StringBuilder line = new(128 + values.Length * 12);
        line.Append(DateTime.UtcNow.ToString("O"));
        line.Append(" kind=").Append(kind);
        line.Append(" source=").Append(sourceName);
        line.Append(" handle=0x").Append(sourceHandle.ToString("X"));
        line.Append(" values=[");
        for (int i = 0; i < values.Length; ++i)
        {
            if (i > 0)
                line.Append(',');
            line.Append(values[i]);
        }
        line.AppendLine("]");

        try
        {
            string logDirectory = Path.Combine(Environment.CurrentDirectory, "Build", "Logs");
            Directory.CreateDirectory(logDirectory);
            File.AppendAllText(Path.Combine(logDirectory, "vulkan-indirect-delayed-readback.log"), line.ToString());
        }
        catch
        {
            // Diagnostic logging must never affect rendering.
        }
    }

    private static int SaturateGpuStatsToInt(ulong value)
        => value > int.MaxValue ? int.MaxValue : (int)value;

    private static int SaturateGpuStatsToInt(uint value)
        => value > int.MaxValue ? int.MaxValue : (int)value;

    private void DisposeGpuRenderStatsReadbacks()
    {
        for (int i = 0; i < _gpuRenderStatsReadbackSlots.Length; ++i)
        {
            GpuRenderStatsReadbackSlot? slot = _gpuRenderStatsReadbackSlots[i];
            if (slot is null)
                continue;

            if (slot.Fence.Handle != 0)
                Api!.DestroyFence(device, slot.Fence, null);
            if (slot.CommandBuffer.Handle != 0)
            {
                CommandBuffer commandBuffer = slot.CommandBuffer;
                FreeVulkanCommandBufferTracked(slot.CommandPool, ref commandBuffer, "GpuStatsReadback.Dispose");
                RemoveCommandBufferBindState(slot.CommandBuffer);
            }
            if (slot.StagingBuffer.Handle != 0)
                DestroyBuffer(slot.StagingBuffer, slot.StagingMemory);

            slot.Active = false;
            slot.Fence = default;
            slot.CommandBuffer = default;
            slot.StagingBuffer = default;
            slot.StagingMemory = default;
            slot.CapacityBytes = 0;
        }

        _gpuRenderStatsTraceHashes.Clear();
    }
}
