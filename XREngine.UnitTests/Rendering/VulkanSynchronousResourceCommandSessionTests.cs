using System.Reflection;
using System.Runtime.CompilerServices;
using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Rendering.Vulkan;
using XREngine.Rendering.Vulkan.DeviceBootstrap;

namespace XREngine.UnitTests.Rendering;

/// <summary>
/// Covers how a synchronous resource command session releases its command buffer when it ends
/// before the queue accepted it. The session constructor allocates a native command pool and
/// command buffer, so these tests build the state that a successful begin leaves. The command
/// runtime uses a device context without a logical device, so the release skips the native free
/// and changes only the tracking state.
/// </summary>
[TestFixture]
public sealed class VulkanSynchronousResourceCommandSessionTests
{
    private const ulong CommandBufferHandle = 0x5101;
    private const ulong CommandPoolHandle = 0x5102;
    private const string Owner = "VulkanSynchronousResourceCommandSessionTests";

    [Test]
    public void Dispose_WhileRecording_AbandonsAndFreesCommandBuffer()
    {
        VulkanCommandRuntime commands = CreateDeviceFreeCommandRuntime();
        VulkanResourceLifetimeTracker tracker = commands.ResourceRuntime.Lifetime.Tracker;
        VulkanResourceLifetimeKey poolKey = new(ObjectType.CommandPool, CommandPoolHandle);
        AddRecordingCommandBuffer(commands, poolKey);
        VulkanSynchronousResourceCommandSession session = CreateRecordingSession(commands);

        Should.NotThrow(session.Dispose);

        // A tracking batch that still records blocks a reset of the thread's graphics pool.
        commands.CommandBuffers.TrackingBatches.ContainsKey(CommandBufferHandle).ShouldBeFalse();
        tracker.CommandBufferLifetimes.ContainsKey(CommandBufferHandle).ShouldBeFalse();
        tracker.CommandBuffersByPool.ContainsKey(poolKey).ShouldBeFalse();
        GetField<bool>(session, "_commandBufferReleased").ShouldBeTrue();
    }

    [Test]
    public void Dispose_WhenFreeFails_LogsAndKeepsCommandBufferForRetry()
    {
        VulkanCommandRuntime commands = CreateDeviceFreeCommandRuntime();
        VulkanResourceLifetimeTracker tracker = commands.ResourceRuntime.Lifetime.Tracker;
        VulkanResourceLifetimeKey poolKey = new(ObjectType.CommandPool, CommandPoolHandle);
        AddRecordingCommandBuffer(commands, poolKey);
        VulkanCommandBufferLifetimeRecord lifetime = tracker.CommandBufferLifetimes[CommandBufferHandle];
        // A queued lifetime submission makes the free throw.
        lifetime.QueuedSubmissionCount = 1;
        VulkanSynchronousResourceCommandSession session = CreateRecordingSession(commands);

        Should.NotThrow(session.Dispose);

        GetField<bool>(session, "_commandBufferReleased").ShouldBeFalse();
        tracker.CommandBufferLifetimes.ContainsKey(CommandBufferHandle).ShouldBeTrue();

        lifetime.QueuedSubmissionCount = 0;
        Should.NotThrow(session.Dispose);

        GetField<bool>(session, "_commandBufferReleased").ShouldBeTrue();
        tracker.CommandBufferLifetimes.ContainsKey(CommandBufferHandle).ShouldBeFalse();
    }

    [Test]
    public void Dispose_AfterAcceptedSubmission_LeavesCommandBufferToDebtRetirement()
    {
        VulkanCommandRuntime commands = CreateDeviceFreeCommandRuntime();
        VulkanResourceLifetimeKey poolKey = new(ObjectType.CommandPool, CommandPoolHandle);
        VulkanCommandBufferTrackingBatch batch = AddRecordingCommandBuffer(commands, poolKey);
        batch.IsRecording = false;
        batch.QueuedSubmissionCount = 1;
        VulkanSynchronousResourceCommandSession session = CreateRecordingSession(commands);
        SetField(session, "_nativeSubmissionAccepted", true);

        session.Dispose();

        commands.CommandBuffers.TrackingBatches[CommandBufferHandle].ShouldBeSameAs(batch);
        commands.ResourceRuntime.Lifetime.Tracker.CommandBufferLifetimes.ContainsKey(CommandBufferHandle).ShouldBeTrue();
        GetField<bool>(session, "_commandBufferReleased").ShouldBeFalse();
    }

    private static VulkanCommandRuntime CreateDeviceFreeCommandRuntime()
    {
        VulkanCommandRuntime commands = new();
        commands.ConfigurePrimaryRecording(
            new VulkanDeviceContext(),
            new VulkanResourceRuntime(1),
            new VulkanFrameTelemetry());
        return commands;
    }

    /// <summary>
    /// Publishes the tracking state that a successful begin leaves for an allocated command buffer:
    /// a lifetime record owned by the pool, and a tracking batch that records.
    /// </summary>
    private static VulkanCommandBufferTrackingBatch AddRecordingCommandBuffer(
        VulkanCommandRuntime commands,
        VulkanResourceLifetimeKey poolKey)
    {
        VulkanResourceLifetimeTracker tracker = commands.ResourceRuntime.Lifetime.Tracker;
        tracker.CommandBufferLifetimes[CommandBufferHandle] = new VulkanCommandBufferLifetimeRecord
        {
            AllocatingCommandPool = poolKey,
        };
        tracker.CommandBuffersByPool[poolKey] = [CommandBufferHandle];

        VulkanCommandBufferTrackingBatch batch = new();
        batch.Reset(recordingGeneration: 1);
        commands.CommandBuffers.TrackingBatches[CommandBufferHandle] = batch;
        return batch;
    }

    private static VulkanSynchronousResourceCommandSession CreateRecordingSession(VulkanCommandRuntime commands)
    {
        VulkanSynchronousResourceCommandSession session = (VulkanSynchronousResourceCommandSession)
            RuntimeHelpers.GetUninitializedObject(typeof(VulkanSynchronousResourceCommandSession));
        SetField(session, "_commands", commands);
        SetField(session, "_pool", new CommandPool(CommandPoolHandle));
        SetField(session, "_owner", Owner);
        SetField(session, "<CommandBuffer>k__BackingField", new CommandBuffer((nint)CommandBufferHandle));
        SetField(session, "<Encoder>k__BackingField", new VulkanTrackedCommandEncoder(commands));
        return session;
    }

    private static void SetField(VulkanSynchronousResourceCommandSession session, string name, object value)
        => GetFieldInfo(name).SetValue(session, value);

    private static T GetField<T>(VulkanSynchronousResourceCommandSession session, string name)
        => (T)GetFieldInfo(name).GetValue(session)!;

    private static FieldInfo GetFieldInfo(string name)
        => typeof(VulkanSynchronousResourceCommandSession).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(VulkanSynchronousResourceCommandSession), name);
}
