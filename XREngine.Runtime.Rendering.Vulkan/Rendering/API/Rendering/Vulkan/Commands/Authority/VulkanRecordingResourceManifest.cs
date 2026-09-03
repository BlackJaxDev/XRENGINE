using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable pre-sealed native resource manifest acquired before vkBeginCommandBuffer.
/// Bundles required native resource dependencies for a logical lane recording, eliminating
/// per-vkCmd* dictionary lookup in Runtime.CommandBuffers.TrackingBatches and per-command monitor locks.
/// </summary>
internal sealed class VulkanRecordingResourceManifest
{
    public static readonly VulkanRecordingResourceManifest Empty = new(
        EVulkanAcceptedFrameLane.MainScene,
        0,
        Array.Empty<VulkanResourceLifetimeKey>());

    public EVulkanAcceptedFrameLane Lane { get; }
    public int FrameSlot { get; }
    public IReadOnlyList<VulkanResourceLifetimeKey> Dependencies { get; }
    public ulong TargetGeneration { get; }

    public VulkanRecordingResourceManifest(
        EVulkanAcceptedFrameLane lane,
        int frameSlot,
        IReadOnlyList<VulkanResourceLifetimeKey> dependencies,
        ulong targetGeneration = 0UL)
    {
        Lane = lane;
        FrameSlot = frameSlot;
        Dependencies = dependencies ?? Array.Empty<VulkanResourceLifetimeKey>();
        TargetGeneration = targetGeneration;
    }
}
