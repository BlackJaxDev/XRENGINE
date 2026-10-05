using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    private readonly ConcurrentDictionary<ulong, (VulkanLaneRecordingContext Context, ulong Generation)> _activeByHandle = new();

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
        if (context.IsActive)
            throw new InvalidOperationException("A Vulkan recording lane cannot begin another command buffer while its current recording is active.");
        RemoveRegistration(context.CommandBufferHandle, context, context.RecordingGeneration);
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0)
            throw new ArgumentException("A live command buffer is required.", nameof(commandBuffer));
        if (TryGetActiveContext(commandBuffer, out _))
            throw new InvalidOperationException("A Vulkan command buffer already belongs to an active recording lane.");
        context.Begin(commandBuffer, recordingGeneration);
        if (!_activeByHandle.TryAdd(handle, (context, recordingGeneration)))
        {
            context.End();
            throw new InvalidOperationException("Vulkan recording lane ownership changed while registering the command buffer.");
        }
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

        if (_activeByHandle.TryGetValue(handle, out var registration))
        {
            context = registration.Context;
            if (context.IsActive && context.CommandBufferHandle == handle &&
                context.RecordingGeneration == registration.Generation)
                return true;
            // A reused lane object may now belong to another handle. Remove
            // only this obsolete registration; never end its current owner.
            RemoveRegistration(handle, context, registration.Generation);
        }
        context = null;
        return false;
    }

    public bool TryGetActiveContext(CommandBuffer commandBuffer, ulong recordingGeneration, out VulkanLaneRecordingContext? context)
    {
        if (!TryGetActiveContext(commandBuffer, out context))
            return false;
        if (context!.RecordingGeneration != recordingGeneration)
            throw new InvalidOperationException("A Vulkan recording lane does not belong to the command buffer's current recording generation.");
        return true;
    }

    /// <summary>Detaches the exact command owner when recording is abandoned, reset or destroyed.</summary>
    public void AbandonContext(CommandBuffer commandBuffer)
    {
        if (TryGetActiveContext(commandBuffer, out VulkanLaneRecordingContext? context))
            EndContext(context!);
    }

    public void EndContext(VulkanLaneRecordingContext context)
    {
        ulong handle = context.CommandBufferHandle;
        if (RemoveRegistration(handle, context, context.RecordingGeneration))
            context.End();
    }

    private bool RemoveRegistration(ulong handle, VulkanLaneRecordingContext context, ulong generation)
        => handle != 0 && _activeByHandle.TryRemove(
            new KeyValuePair<ulong, (VulkanLaneRecordingContext Context, ulong Generation)>(handle, (context, generation)));
}
