using System;
using System.Collections.Concurrent;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Coordinates VulkanLaneRecordingContext instances indexed per logical render lane and frame slot.
/// Provides fast zero-lock lookup for command buffers currently being recorded.
/// </summary>
internal sealed class VulkanLaneRecordingContextTable
{
    private const int LaneCount = 11; // Matches EVulkanAcceptedFrameLane values
    private readonly int _maxFrameSlots;
    private readonly VulkanLaneRecordingContext[,] _contexts;
    private readonly ConcurrentDictionary<ulong, VulkanLaneRecordingContext> _activeByHandle = new();

    public VulkanLaneRecordingContextTable(int maxFrameSlots = 16)
    {
        _maxFrameSlots = Math.Max(1, maxFrameSlots);
        _contexts = new VulkanLaneRecordingContext[LaneCount, _maxFrameSlots];
        for (int lane = 0; lane < LaneCount; lane++)
        {
            for (int slot = 0; slot < _maxFrameSlots; slot++)
            {
                _contexts[lane, slot] = new VulkanLaneRecordingContext((EVulkanAcceptedFrameLane)lane, slot);
            }
        }
    }

    public VulkanLaneRecordingContext GetContext(EVulkanAcceptedFrameLane lane, int frameSlot)
    {
        int laneIndex = (int)lane;
        if ((uint)laneIndex >= LaneCount)
            laneIndex = (int)EVulkanAcceptedFrameLane.MainScene;

        int slot = Math.Clamp(frameSlot, 0, _maxFrameSlots - 1);
        return _contexts[laneIndex, slot];
    }

    public VulkanLaneRecordingContext BeginContext(
        EVulkanAcceptedFrameLane lane,
        int frameSlot,
        CommandBuffer commandBuffer,
        ulong recordingGeneration)
    {
        VulkanLaneRecordingContext context = GetContext(lane, frameSlot);
        context.Begin(commandBuffer, recordingGeneration);
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle != 0)
            _activeByHandle[handle] = context;
        return context;
    }

    public bool TryGetActiveContext(CommandBuffer commandBuffer, out VulkanLaneRecordingContext? context)
    {
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0)
        {
            context = null;
            return false;
        }

        return _activeByHandle.TryGetValue(handle, out context);
    }

    public void EndContext(VulkanLaneRecordingContext context)
    {
        ulong handle = context.CommandBufferHandle;
        if (handle != 0)
            _activeByHandle.TryRemove(handle, out _);
        context.End();
    }
}
