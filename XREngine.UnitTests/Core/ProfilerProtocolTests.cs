using MemoryPack;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Profiling;

namespace XREngine.UnitTests.Core;

/// <summary>
/// Verifies MemoryPack serialize → deserialize round-trips for every profiler packet type,
/// and tests the wire-protocol framing helpers in <see cref="ProfilerProtocol"/>.
/// </summary>
[TestFixture]
public sealed class ProfilerProtocolTests
{
    // ── ProfilerFramePacket ────────────────────────────────────────────

    [Test]
    public void ProfilerFramePacket_RoundTrip()
    {
        var original = new ProfilerFramePacket
        {
            FrameTime = 12.345f,
            Threads =
            [
                new ProfilerThreadData
                {
                    ThreadId = 1,
                    TotalTimeMs = 3.5f,
                    RootNodes =
                    [
                        new ProfilerNodeData
                        {
                            Name = "Update",
                            ElapsedMs = 2.0f,
                            Children =
                            [
                                new ProfilerNodeData { Name = "Physics", ElapsedMs = 1.5f, Children = [] }
                            ]
                        },
                        new ProfilerNodeData { Name = "Render", ElapsedMs = 1.5f, Children = [] }
                    ]
                },
                new ProfilerThreadData
                {
                    ThreadId = 7,
                    TotalTimeMs = 1.0f,
                    RootNodes = [new ProfilerNodeData { Name = "Audio", ElapsedMs = 1.0f, Children = [] }]
                }
            ],
            ThreadHistory = new Dictionary<int, float[]>
            {
                [1] = [1.0f, 2.0f, 3.0f],
                [7] = [0.5f, 0.8f]
            },
            ComponentTimings =
            [
                new ProfilerComponentTimingData
                {
                    ComponentId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                    ComponentName = "PlayerMovement",
                    ComponentType = "CharacterMovementComponent",
                    SceneNodeName = "PlayerRoot",
                    ElapsedMs = 1.75f,
                    CallCount = 2,
                    TickGroupMask = 3,
                }
            ]
        };

        var clone = RoundTrip(original);

        clone.FrameTime.ShouldBe(original.FrameTime);
        clone.Threads.Length.ShouldBe(2);
        clone.Threads[0].ThreadId.ShouldBe(1);
        clone.Threads[0].TotalTimeMs.ShouldBe(3.5f);
        clone.Threads[0].RootNodes.Length.ShouldBe(2);
        clone.Threads[0].RootNodes[0].Name.ShouldBe("Update");
        clone.Threads[0].RootNodes[0].Children.Length.ShouldBe(1);
        clone.Threads[0].RootNodes[0].Children[0].Name.ShouldBe("Physics");
        clone.Threads[1].ThreadId.ShouldBe(7);
        clone.ThreadHistory.Count.ShouldBe(2);
        clone.ThreadHistory[1].ShouldBe(new[] { 1.0f, 2.0f, 3.0f });
        clone.ComponentTimings.Length.ShouldBe(1);
        clone.ComponentTimings[0].ComponentName.ShouldBe("PlayerMovement");
        clone.ComponentTimings[0].TickGroupMask.ShouldBe(3);
    }

    [Test]
    public void ProfilerFramePacket_EmptyThreads_RoundTrip()
    {
        var original = new ProfilerFramePacket { FrameTime = 0.0f, Threads = [], ThreadHistory = [], ComponentTimings = [] };
        var clone = RoundTrip(original);
        clone.Threads.Length.ShouldBe(0);
        clone.ThreadHistory.Count.ShouldBe(0);
        clone.ComponentTimings.Length.ShouldBe(0);
    }

    // ── RenderStatsPacket ──────────────────────────────────────────────

    [Test]
    public void RenderStatsPacket_RoundTrip()
    {
        var original = new RenderStatsPacket
        {
            DrawCalls = 1234,
            MultiDrawCalls = 56,
            TrianglesRendered = 9_876_543,
            VulkanDeviceLocalAllocationCount = 7,
            VulkanDeviceLocalAllocatedBytes = 70_000,
            VulkanUploadAllocationCount = 3,
            VulkanUploadAllocatedBytes = 30_000,
            VulkanReadbackAllocationCount = 2,
            VulkanReadbackAllocatedBytes = 20_000,
            VulkanDescriptorPoolCreateCount = 5,
            VulkanDescriptorPoolDestroyCount = 4,
            VulkanDescriptorPoolResetCount = 6,
            VulkanQueueSubmitCount = 8,
            VulkanDroppedFrameOps = 3,
            VulkanDroppedDrawOps = 2,
            VulkanDroppedComputeOps = 1,
            VulkanSceneSwapchainWriters = 0,
            VulkanOverlaySwapchainWriters = 1,
            VulkanForcedDiagnosticSwapchainWriters = 1,
            VulkanFboOnlyDrawOps = 4,
            VulkanFboOnlyBlitOps = 5,
            VulkanMissingSceneSwapchainWriteFrames = 1,
            VulkanFirstFailedFrameOpPassIndex = 42,
            VulkanFirstFailedFrameOpPipelineIdentity = 1001,
            VulkanFirstFailedFrameOpViewportIdentity = 2002,
            VulkanFirstFailedFrameOpType = "MeshDrawOp",
            VulkanFirstFailedFrameOpTargetName = "<swapchain/null>",
            VulkanFirstFailedFrameOpMaterialName = "BrokenMaterial",
            VulkanFirstFailedFrameOpShaderName = "BrokenShader.fs",
            VulkanFirstFailedFrameOpMessage = "descriptor set mismatch",
            VulkanFrameDiagnosticSummary = "ops=8 writers scene=0 overlay=1",
            VulkanValidationMessageCount = 2,
            VulkanValidationErrorCount = 1,
            VulkanLastValidationMessage = "validation error",
            VulkanDescriptorFallbackSampledImages = 3,
            VulkanDescriptorFallbackStorageImages = 4,
            VulkanDescriptorFallbackUniformBuffers = 5,
            VulkanDescriptorFallbackStorageBuffers = 6,
            VulkanDescriptorFallbackTexelBuffers = 7,
            VulkanDescriptorBindingFailures = 8,
            VulkanDescriptorSkippedDraws = 9,
            VulkanDescriptorSkippedDispatches = 10,
            VulkanDescriptorFallbackSummary = "sampled-image:Material.Albedo",
            VulkanDescriptorFailureSummary = "storage-buffer:ComputeParticles",
            VulkanDynamicUniformAllocations = 11,
            VulkanDynamicUniformAllocatedBytes = 12_288,
            VulkanDynamicUniformExhaustions = 1,
            VulkanRetiredResourcePlanReplacements = 2,
            VulkanRetiredResourcePlanImages = 13,
            VulkanRetiredResourcePlanBuffers = 14,
            AllocatedVRAMBytes = 512 * 1024 * 1024L,
            AllocatedBufferBytes = 128 * 1024 * 1024L,
            AllocatedTextureBytes = 256 * 1024 * 1024L,
            AllocatedRenderBufferBytes = 64 * 1024 * 1024L,
            FBOBandwidthBytes = 100_000_000,
            FBOBindCount = 42,
            PhysicsChainCpuUploadBytes = 10_000,
            PhysicsChainGpuCopyBytes = 20_000,
            PhysicsChainCpuReadbackBytes = 30_000,
            PhysicsChainDispatchGroupCount = 4,
            PhysicsChainDispatchIterationCount = 9,
            PhysicsChainResidentParticleBytes = 40_000,
            PhysicsChainStandaloneCpuUploadBytes = 2_000,
            PhysicsChainStandaloneCpuReadbackBytes = 3_000,
            PhysicsChainBatchedCpuUploadBytes = 8_000,
            PhysicsChainBatchedGpuCopyBytes = 20_000,
            PhysicsChainBatchedCpuReadbackBytes = 27_000,
            PhysicsChainHierarchyRecalcMilliseconds = 1.75,
            RenderMatrixStatsReady = true,
            RenderMatrixApplied = 300,
            RenderMatrixBatchCount = 12,
            RenderMatrixMaxBatchSize = 64,
            RenderMatrixSetCalls = 150,
            RenderMatrixListenerInvocations = 900,
            RenderMatrixListenerCounts =
            [
                new RenderMatrixListenerEntry { Name = "MeshRenderer", Count = 500 },
                new RenderMatrixListenerEntry { Name = "LightProbe", Count = 200 }
            ],
            SkinnedBoundsStatsReady = true,
            SkinnedBoundsDeferredScheduledCount = 7,
            SkinnedBoundsDeferredCompletedCount = 5,
            SkinnedBoundsDeferredFailedCount = 1,
            SkinnedBoundsDeferredInFlightCount = 1,
            SkinnedBoundsDeferredMaxInFlightCount = 3,
            SkinnedBoundsDeferredQueueWaitMs = 9.5,
            SkinnedBoundsDeferredCpuJobMs = 4.25,
            SkinnedBoundsDeferredApplyMs = 1.75,
            SkinnedBoundsDeferredMaxQueueWaitMs = 5.5,
            SkinnedBoundsDeferredMaxCpuJobMs = 2.25,
            SkinnedBoundsDeferredMaxApplyMs = 1.0,
            SkinnedBoundsGpuCompletedCount = 2,
            SkinnedBoundsGpuComputeMs = 0.75,
            SkinnedBoundsGpuApplyMs = 0.25,
            SkinnedBoundsGpuMaxComputeMs = 0.5,
            SkinnedBoundsGpuMaxApplyMs = 0.2,
            OctreeStatsReady = true,
            OctreeCollectCallCount = 9,
            OctreeVisibleRenderableCount = 123,
            OctreeEmittedCommandCount = 456,
            OctreeMaxVisibleRenderablesPerCollect = 40,
            OctreeMaxEmittedCommandsPerCollect = 120,
            OctreeAddCount = 10,
            OctreeMoveCount = 20,
            OctreeRemoveCount = 5,
            OctreeSkippedMoveCount = 3,
            OctreeSwapDrainedCommandCount = 14,
            OctreeSwapBufferedCommandCount = 11,
            OctreeSwapExecutedCommandCount = 9,
            OctreeSwapDrainMs = 2.5,
            OctreeSwapExecuteMs = 7.75,
            OctreeSwapMaxCommandMs = 4.5,
            OctreeSwapMaxCommandKind = "Move",
            OctreeRaycastProcessedCommandCount = 6,
            OctreeRaycastDroppedCommandCount = 2,
            OctreeRaycastTraversalMs = 12.25,
            OctreeRaycastCallbackMs = 3.5,
            OctreeRaycastMaxTraversalMs = 8.25,
            OctreeRaycastMaxCallbackMs = 2.0,
            OctreeRaycastMaxCommandMs = 9.75,
            GpuRenderPipelineProfilingEnabled = true,
            GpuRenderPipelineProfilingSupported = true,
            GpuRenderPipelineTimingsReady = true,
            GpuRenderPipelineBackend = "OpenGL",
            GpuRenderPipelineStatusMessage = string.Empty,
            GpuRenderPipelineFrameMs = 3.25,
            GpuRenderPipelineTimingRoots =
            [
                new GpuPipelineTimingNodeData
                {
                    Name = "DefaultRenderPipeline | Camera=MainCamera (XRCamera) | Viewport=Viewport#0 (1280x720)",
                    ElapsedMs = 3.25,
                    SampleCount = 1,
                    Children =
                    [
                        new GpuPipelineTimingNodeData
                        {
                            Name = "VPRC_RenderMeshesPassCPU",
                            ElapsedMs = 1.5,
                            SampleCount = 1,
                            Children = []
                        }
                    ]
                }
            ],
        };

        var clone = RoundTrip(original);

        clone.DrawCalls.ShouldBe(1234);
        clone.MultiDrawCalls.ShouldBe(56);
        clone.TrianglesRendered.ShouldBe(9_876_543);
        clone.VulkanDeviceLocalAllocationCount.ShouldBe(7);
        clone.VulkanDeviceLocalAllocatedBytes.ShouldBe(70_000);
        clone.VulkanUploadAllocationCount.ShouldBe(3);
        clone.VulkanUploadAllocatedBytes.ShouldBe(30_000);
        clone.VulkanReadbackAllocationCount.ShouldBe(2);
        clone.VulkanReadbackAllocatedBytes.ShouldBe(20_000);
        clone.VulkanDescriptorPoolCreateCount.ShouldBe(5);
        clone.VulkanDescriptorPoolDestroyCount.ShouldBe(4);
        clone.VulkanDescriptorPoolResetCount.ShouldBe(6);
        clone.VulkanQueueSubmitCount.ShouldBe(8);
        clone.VulkanDroppedFrameOps.ShouldBe(3);
        clone.VulkanDroppedDrawOps.ShouldBe(2);
        clone.VulkanDroppedComputeOps.ShouldBe(1);
        clone.VulkanSceneSwapchainWriters.ShouldBe(0);
        clone.VulkanOverlaySwapchainWriters.ShouldBe(1);
        clone.VulkanForcedDiagnosticSwapchainWriters.ShouldBe(1);
        clone.VulkanFboOnlyDrawOps.ShouldBe(4);
        clone.VulkanFboOnlyBlitOps.ShouldBe(5);
        clone.VulkanMissingSceneSwapchainWriteFrames.ShouldBe(1);
        clone.VulkanFirstFailedFrameOpType.ShouldBe("MeshDrawOp");
        clone.VulkanFirstFailedFrameOpPassIndex.ShouldBe(42);
        clone.VulkanFirstFailedFrameOpPipelineIdentity.ShouldBe(1001);
        clone.VulkanFirstFailedFrameOpViewportIdentity.ShouldBe(2002);
        clone.VulkanFirstFailedFrameOpTargetName.ShouldBe("<swapchain/null>");
        clone.VulkanFirstFailedFrameOpMaterialName.ShouldBe("BrokenMaterial");
        clone.VulkanFirstFailedFrameOpShaderName.ShouldBe("BrokenShader.fs");
        clone.VulkanFirstFailedFrameOpMessage.ShouldBe("descriptor set mismatch");
        clone.VulkanFrameDiagnosticSummary.ShouldBe("ops=8 writers scene=0 overlay=1");
        clone.VulkanValidationMessageCount.ShouldBe(2);
        clone.VulkanValidationErrorCount.ShouldBe(1);
        clone.VulkanLastValidationMessage.ShouldBe("validation error");
        clone.VulkanDescriptorFallbackSampledImages.ShouldBe(3);
        clone.VulkanDescriptorFallbackStorageImages.ShouldBe(4);
        clone.VulkanDescriptorFallbackUniformBuffers.ShouldBe(5);
        clone.VulkanDescriptorFallbackStorageBuffers.ShouldBe(6);
        clone.VulkanDescriptorFallbackTexelBuffers.ShouldBe(7);
        clone.VulkanDescriptorBindingFailures.ShouldBe(8);
        clone.VulkanDescriptorSkippedDraws.ShouldBe(9);
        clone.VulkanDescriptorSkippedDispatches.ShouldBe(10);
        clone.VulkanDescriptorFallbackSummary.ShouldBe("sampled-image:Material.Albedo");
        clone.VulkanDescriptorFailureSummary.ShouldBe("storage-buffer:ComputeParticles");
        clone.VulkanDynamicUniformAllocations.ShouldBe(11);
        clone.VulkanDynamicUniformAllocatedBytes.ShouldBe(12_288);
        clone.VulkanDynamicUniformExhaustions.ShouldBe(1);
        clone.VulkanRetiredResourcePlanReplacements.ShouldBe(2);
        clone.VulkanRetiredResourcePlanImages.ShouldBe(13);
        clone.VulkanRetiredResourcePlanBuffers.ShouldBe(14);
        clone.AllocatedVRAMBytes.ShouldBe(512 * 1024 * 1024L);
        clone.FBOBandwidthBytes.ShouldBe(100_000_000);
        clone.FBOBindCount.ShouldBe(42);
        clone.PhysicsChainCpuUploadBytes.ShouldBe(10_000);
        clone.PhysicsChainGpuCopyBytes.ShouldBe(20_000);
        clone.PhysicsChainCpuReadbackBytes.ShouldBe(30_000);
        clone.PhysicsChainDispatchGroupCount.ShouldBe(4);
        clone.PhysicsChainDispatchIterationCount.ShouldBe(9);
        clone.PhysicsChainResidentParticleBytes.ShouldBe(40_000);
        clone.PhysicsChainStandaloneCpuUploadBytes.ShouldBe(2_000);
        clone.PhysicsChainStandaloneCpuReadbackBytes.ShouldBe(3_000);
        clone.PhysicsChainBatchedCpuUploadBytes.ShouldBe(8_000);
        clone.PhysicsChainBatchedGpuCopyBytes.ShouldBe(20_000);
        clone.PhysicsChainBatchedCpuReadbackBytes.ShouldBe(27_000);
        clone.PhysicsChainHierarchyRecalcMilliseconds.ShouldBe(1.75);
        clone.RenderMatrixStatsReady.ShouldBeTrue();
        clone.RenderMatrixApplied.ShouldBe(300);
        clone.RenderMatrixBatchCount.ShouldBe(12);
        clone.RenderMatrixMaxBatchSize.ShouldBe(64);
        clone.RenderMatrixListenerCounts.Length.ShouldBe(2);
        clone.RenderMatrixListenerCounts[0].Name.ShouldBe("MeshRenderer");
        clone.RenderMatrixListenerCounts[0].Count.ShouldBe(500);
        clone.SkinnedBoundsStatsReady.ShouldBeTrue();
        clone.SkinnedBoundsDeferredScheduledCount.ShouldBe(7);
        clone.SkinnedBoundsDeferredCompletedCount.ShouldBe(5);
        clone.SkinnedBoundsDeferredFailedCount.ShouldBe(1);
        clone.SkinnedBoundsDeferredInFlightCount.ShouldBe(1);
        clone.SkinnedBoundsDeferredMaxInFlightCount.ShouldBe(3);
        clone.SkinnedBoundsDeferredQueueWaitMs.ShouldBe(9.5);
        clone.SkinnedBoundsDeferredCpuJobMs.ShouldBe(4.25);
        clone.SkinnedBoundsDeferredApplyMs.ShouldBe(1.75);
        clone.SkinnedBoundsGpuCompletedCount.ShouldBe(2);
        clone.SkinnedBoundsGpuMaxComputeMs.ShouldBe(0.5);
        clone.OctreeStatsReady.ShouldBeTrue();
        clone.OctreeCollectCallCount.ShouldBe(9);
        clone.OctreeVisibleRenderableCount.ShouldBe(123);
        clone.OctreeEmittedCommandCount.ShouldBe(456);
        clone.OctreeMaxVisibleRenderablesPerCollect.ShouldBe(40);
        clone.OctreeMaxEmittedCommandsPerCollect.ShouldBe(120);
        clone.OctreeAddCount.ShouldBe(10);
        clone.OctreeSwapDrainedCommandCount.ShouldBe(14);
        clone.OctreeSwapBufferedCommandCount.ShouldBe(11);
        clone.OctreeSwapExecutedCommandCount.ShouldBe(9);
        clone.OctreeSwapDrainMs.ShouldBe(2.5);
        clone.OctreeSwapExecuteMs.ShouldBe(7.75);
        clone.OctreeSwapMaxCommandMs.ShouldBe(4.5);
        clone.OctreeSwapMaxCommandKind.ShouldBe("Move");
        clone.OctreeRaycastProcessedCommandCount.ShouldBe(6);
        clone.OctreeRaycastDroppedCommandCount.ShouldBe(2);
        clone.OctreeRaycastTraversalMs.ShouldBe(12.25);
        clone.OctreeRaycastCallbackMs.ShouldBe(3.5);
        clone.OctreeRaycastMaxTraversalMs.ShouldBe(8.25);
        clone.OctreeRaycastMaxCallbackMs.ShouldBe(2.0);
        clone.OctreeRaycastMaxCommandMs.ShouldBe(9.75);
        clone.GpuRenderPipelineProfilingEnabled.ShouldBeTrue();
        clone.GpuRenderPipelineProfilingSupported.ShouldBeTrue();
        clone.GpuRenderPipelineTimingsReady.ShouldBeTrue();
        clone.GpuRenderPipelineBackend.ShouldBe("OpenGL");
        clone.GpuRenderPipelineFrameMs.ShouldBe(3.25);
        clone.GpuRenderPipelineTimingRoots.Length.ShouldBe(1);
        clone.GpuRenderPipelineTimingRoots[0].Children.Length.ShouldBe(1);
    }

    // ── ThreadAllocationsPacket ────────────────────────────────────────

    [Test]
    public void ThreadAllocationsPacket_RoundTrip()
    {
        var original = new ThreadAllocationsPacket
        {
            Render = new AllocationSlice { LastBytes = 1024, AverageBytes = 900.5, MaxBytes = 2048, Samples = 120, Capacity = 240 },
            CollectSwap = new AllocationSlice { LastBytes = 512, AverageBytes = 400.0, MaxBytes = 1024, Samples = 60, Capacity = 240 },
            Update = new AllocationSlice { LastBytes = 2048, AverageBytes = 1800.0, MaxBytes = 4096, Samples = 200, Capacity = 240 },
            FixedUpdate = new AllocationSlice { LastBytes = 256, AverageBytes = 200.0, MaxBytes = 512, Samples = 30, Capacity = 240 },
        };

        var clone = RoundTrip(original);

        clone.Render.LastBytes.ShouldBe(1024);
        clone.Render.AverageBytes.ShouldBe(900.5);
        clone.Render.MaxBytes.ShouldBe(2048);
        clone.CollectSwap.Samples.ShouldBe(60);
        clone.Update.LastKB.ShouldBe(2.0);
        clone.FixedUpdate.Capacity.ShouldBe(240);
    }

    // ── BvhMetricsPacket ───────────────────────────────────────────────

    [Test]
    public void BvhMetricsPacket_RoundTrip()
    {
        var original = new BvhMetricsPacket
        {
            BuildCount = 100,
            BuildMilliseconds = 1.234,
            RefitCount = 200,
            RefitMilliseconds = 0.567,
            CullCount = 50,
            CullMilliseconds = 0.123,
            RaycastCount = 10,
            RaycastMilliseconds = 0.045,
        };

        var clone = RoundTrip(original);

        clone.BuildCount.ShouldBe(100u);
        clone.BuildMilliseconds.ShouldBe(1.234);
        clone.RefitCount.ShouldBe(200u);
        clone.CullMilliseconds.ShouldBe(0.123);
        clone.RaycastCount.ShouldBe(10u);
    }

    // ── JobSystemStatsPacket ───────────────────────────────────────────

    [Test]
    public void JobSystemStatsPacket_RoundTrip()
    {
        var original = new JobSystemStatsPacket
        {
            WorkerCount = 8,
            IsQueueBounded = true,
            QueueCapacity = 1024,
            QueueSlotsInUse = 37,
            QueueSlotsAvailable = 987,
            Priorities =
            [
                new JobPriorityStatsEntry { Priority = 0, PriorityName = "Lowest", QueuedAny = 5, QueuedMain = 1, QueuedCollect = 0, AvgWaitMs = 12.5 },
                new JobPriorityStatsEntry { Priority = 4, PriorityName = "Highest", QueuedAny = 0, QueuedMain = 0, QueuedCollect = 0, AvgWaitMs = 0.1 },
            ]
        };

        var clone = RoundTrip(original);

        clone.WorkerCount.ShouldBe(8);
        clone.IsQueueBounded.ShouldBeTrue();
        clone.QueueCapacity.ShouldBe(1024);
        clone.Priorities.Length.ShouldBe(2);
        clone.Priorities[0].PriorityName.ShouldBe("Lowest");
        clone.Priorities[0].AvgWaitMs.ShouldBe(12.5);
        clone.Priorities[1].Priority.ShouldBe(4);
    }

    // ── MainThreadInvokesPacket ────────────────────────────────────────

    [Test]
    public void MainThreadInvokesPacket_RoundTrip()
    {
        var original = new MainThreadInvokesPacket
        {
            Entries =
            [
                new MainThreadInvokeEntryData
                {
                    Sequence = 42,
                    TimestampTicks = DateTimeOffset.UtcNow.Ticks,
                    Reason = "SceneLoad",
                    Mode = "Queued",
                    CallerThreadId = 3,
                },
            ]
        };

        var clone = RoundTrip(original);

        clone.Entries.Length.ShouldBe(1);
        clone.Entries[0].Sequence.ShouldBe(42);
        clone.Entries[0].Reason.ShouldBe("SceneLoad");
        clone.Entries[0].Mode.ShouldBe("Queued");
        clone.Entries[0].CallerThreadId.ShouldBe(3);
    }

    [Test]
    public void MainThreadInvokesPacket_Empty_RoundTrip()
    {
        var original = new MainThreadInvokesPacket { Entries = [] };
        var clone = RoundTrip(original);
        clone.Entries.Length.ShouldBe(0);
    }

    // ── HeartbeatPacket ────────────────────────────────────────────────

    [Test]
    public void HeartbeatPacket_RoundTrip()
    {
        var original = new HeartbeatPacket
        {
            ProcessName = "XREngine.Editor",
            ProcessId = 12345,
            UptimeMs = 60_000,
        };

        var clone = RoundTrip(original);

        clone.ProcessName.ShouldBe("XREngine.Editor");
        clone.ProcessId.ShouldBe(12345);
        clone.UptimeMs.ShouldBe(60_000);
    }

    // ── Wire framing ───────────────────────────────────────────────────

    [Test]
    public void WriteFrame_ThenReadFrame_RoundTrips()
    {
        byte[] payload = [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02];
        byte[] buffer = new byte[ProfilerProtocol.MaxDatagramSize];

        int written = ProfilerProtocol.WriteFrame(buffer, ProfilerProtocol.MessageType.RenderStats, payload);
        written.ShouldBe(ProfilerProtocol.HeaderSize + payload.Length);

        bool ok = ProfilerProtocol.TryReadFrame(buffer.AsSpan(0, written), out var type, out var readPayload);
        ok.ShouldBeTrue();
        type.ShouldBe(ProfilerProtocol.MessageType.RenderStats);
        readPayload.Length.ShouldBe(payload.Length);
        readPayload.ToArray().ShouldBe(payload);
    }

    [Test]
    public void TryReadFrame_TooShort_ReturnsFalse()
    {
        byte[] tooShort = [0x01, 0x00];
        ProfilerProtocol.TryReadFrame(tooShort, out _, out _).ShouldBeFalse();
    }

    [Test]
    public void WriteFrame_PayloadTooLarge_ReturnsNegative()
    {
        byte[] hugePayload = new byte[ProfilerProtocol.MaxPayloadSize + 1];
        byte[] buffer = new byte[ProfilerProtocol.MaxDatagramSize];
        int result = ProfilerProtocol.WriteFrame(buffer, ProfilerProtocol.MessageType.ProfilerFrame, hugePayload);
        result.ShouldBe(-1);
    }

    // ── Full end-to-end: serialize packet → frame → unframe → deserialize ──

    [Test]
    public void FullPipeline_ProfilerFrame_SerializeFrameDeserialize()
    {
        var original = new ProfilerFramePacket
        {
            FrameTime = 99.9f,
            Threads =
            [
                new ProfilerThreadData
                {
                    ThreadId = 5,
                    TotalTimeMs = 8.0f,
                    RootNodes = [new ProfilerNodeData { Name = "Tick", ElapsedMs = 8.0f, Children = [] }]
                }
            ],
            ThreadHistory = new Dictionary<int, float[]> { [5] = [8.0f] },
            ComponentTimings =
            [
                new ProfilerComponentTimingData
                {
                    ComponentId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                    ComponentName = "AvatarIK",
                    ComponentType = "HumanoidComponent",
                    SceneNodeName = "Avatar",
                    ElapsedMs = 4.0f,
                    CallCount = 1,
                    TickGroupMask = 1,
                }
            ]
        };

        // Serialize
        byte[] payload = MemoryPackSerializer.Serialize(original);

        // Frame
        byte[] buffer = new byte[ProfilerProtocol.MaxDatagramSize];
        int written = ProfilerProtocol.WriteFrame(buffer, ProfilerProtocol.MessageType.ProfilerFrame, payload);
        written.ShouldBeGreaterThan(0);

        // Simulate receive: read frame
        bool ok = ProfilerProtocol.TryReadFrame(buffer.AsSpan(0, written), out var type, out var readPayload);
        ok.ShouldBeTrue();
        type.ShouldBe(ProfilerProtocol.MessageType.ProfilerFrame);

        // Deserialize
        var clone = MemoryPackSerializer.Deserialize<ProfilerFramePacket>(readPayload);
        clone.ShouldNotBeNull();
        clone!.FrameTime.ShouldBe(99.9f);
        clone.Threads[0].RootNodes[0].Name.ShouldBe("Tick");
        clone.ComponentTimings[0].ComponentType.ShouldBe("HumanoidComponent");
    }

    // ── helper ─────────────────────────────────────────────────────────

    private static T RoundTrip<T>(T value) where T : class
    {
        byte[] bytes = MemoryPackSerializer.Serialize(value);
        bytes.Length.ShouldBeGreaterThan(0);
        var result = MemoryPackSerializer.Deserialize<T>(bytes);
        result.ShouldNotBeNull();
        return result!;
    }
}
