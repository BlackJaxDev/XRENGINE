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
                            ScopeKind = ProfilerScopeKind.AlwaysOnHotPathLoop,
                            Children =
                            [
                                new ProfilerNodeData { Name = "Physics", ElapsedMs = 1.5f, ScopeKind = ProfilerScopeKind.ConditionalLoop, Children = [] }
                            ]
                        },
                        new ProfilerNodeData { Name = "Render", ElapsedMs = 1.5f, ScopeKind = ProfilerScopeKind.AlwaysOnHotPathLoop, Children = [] }
                    ]
                },
                new ProfilerThreadData
                {
                    ThreadId = 7,
                    TotalTimeMs = 1.0f,
                    RootNodes = [new ProfilerNodeData { Name = "Audio", ElapsedMs = 1.0f, ScopeKind = ProfilerScopeKind.OneOffInvoke, Children = [] }]
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
        clone.Threads[0].RootNodes[0].ScopeKind.ShouldBe(ProfilerScopeKind.AlwaysOnHotPathLoop);
        clone.Threads[0].RootNodes[0].Children.Length.ShouldBe(1);
        clone.Threads[0].RootNodes[0].Children[0].Name.ShouldBe("Physics");
        clone.Threads[0].RootNodes[0].Children[0].ScopeKind.ShouldBe(ProfilerScopeKind.ConditionalLoop);
        clone.Threads[1].ThreadId.ShouldBe(7);
        clone.Threads[1].RootNodes[0].ScopeKind.ShouldBe(ProfilerScopeKind.OneOffInvoke);
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
            GpuCpuFallbackEvents = 2,
            GpuCpuFallbackRecoveredCommands = 17,
            ForbiddenGpuFallbackEvents = 1,
            GpuMappedBuffers = 3,
            GpuReadbackBytes = 4096,
            ShadowAtlasSolve = new ShadowAtlasSolveDiagnosticsData
            {
                ElapsedMilliseconds = 1.25,
                ClassifiedRequestCount = 12,
                DirectionalRequestCount = 4,
                SpotRequestCount = 2,
                PointRequestCount = 6,
                DepthRequestCount = 10,
                Variance2RequestCount = 1,
                ExponentialVariance2RequestCount = 0,
                ExponentialVariance4RequestCount = 1,
                BalancedSolveAttemptCount = 3,
                FailedCandidateCount = 2,
                DemotionCount = 5,
                StickyDemotionCount = 1,
                DirectionalGroupDemotionCount = 4,
                DeterministicFallbackDemotionCount = 0,
                PriorReserveHitCount = 7,
                PriorReserveMissCount = 2,
                PriorSubBlockHitCount = 1,
                PriorSubBlockMissCount = 1,
                PageAllocationAttemptCount = 8,
                PageAllocationSuccessCount = 6,
                PageCreateAttemptCount = 1,
                PageCreateSuccessCount = 1,
                PageClearCount = 2,
                DirectionalGroupSeedCount = 1,
                DirectionalGroupMemberCount = 4,
                DirectionalGroupCoLocationFailureCount = 0,
                PointGroupSeedCount = 1,
                PointGroupMemberCount = 6,
                PointGroupCoLocationFailureCount = 0,
                LastFailureReason = "PageBudgetExceeded",
            },
            RenderProfilerV2 = new RenderProfilerV2Data
            {
                RendererState = new RenderProfilerRendererStateData
                {
                    IndirectCountCalls = 6,
                    ShaderProgramSwitches = 7,
                    ProgramPipelineSwitches = 8,
                    VaoBinds = 9,
                    VaoBindSkips = 10,
                    ArrayBufferBinds = 11,
                    ElementArrayBufferBinds = 12,
                    DrawIndirectBufferBinds = 13,
                    ParameterBufferBinds = 14,
                    SsboBinds = 15,
                    UboBinds = 16,
                    TextureBinds = 17,
                    TextureBindSkips = 18,
                    TextureUnitSwitches = 19,
                    UniformCalls = 20,
                    SamplerUniformCalls = 21,
                    BufferUploadBytes = 22_000,
                    BarrierCalls = 23,
                    BarrierAll = 24,
                    BarrierCommand = 25,
                    BarrierBufferUpdate = 26,
                    BarrierShaderStorage = 27,
                    BarrierTextureFetch = 28,
                    BarrierTextureUpdate = 29,
                    BarrierFramebuffer = 30,
                    TimestampQueryCount = 31,
                    TimestampQueryReadbackBytes = 32_000,
                    TimestampDenseModeFrames = 1,
                    RedundantStateSkips = 33,
                    CpuDirectDrawCalls = 34,
                    GpuIndirectDrawCalls = 35,
                    GpuMeshletDrawCalls = 36,
                    UnknownStrategyDrawCalls = 37,
                    ActiveTextureBindingRung = "bindless-material-table",
                    ActiveStereoMode = "multiview",
                    ActiveSubmissionStrategy = "GpuIndirectZeroReadback",
                    ActiveRenderBackend = "OpenGL",
                    ValidationLayersEnabled = false,
                    DebugOutputEnabled = true,
                    GpuTimestampsDenseMode = true,
                },
                SceneAssets = new RenderProfilerSceneAssetData
                {
                    VisibleRendererCount = 38,
                    VisibleSubmeshCount = 39,
                    VisibleTriangleCount = 40_000,
                    MaterialSlotCount = 41,
                    ActiveMaterialCount = 42,
                    TextureCount = 43,
                    ResidentTextureMemoryBytes = 44_000,
                    TextureUploadJobs = 45,
                    TextureUploadBytes = 46_000,
                    TextureUploadMs = 0.46,
                    ShaderVariantsRequested = 47,
                    ShaderVariantsWarming = 48,
                    ShaderVariantsLinked = 49,
                    ShaderVariantsFailed = 1,
                    ShaderVariantsLoadedFromDiskCache = 50,
                    ShaderVariantsGeneratedThisRun = 51,
                    SkinnedRendererCount = 52,
                    BoneMatrixUploadBytes = 53_000,
                    BlendshapeWeightUploadBytes = 54_000,
                    BlendshapeActiveListUploadBytes = 54_050,
                    BlendshapeDeltaBytes = 54_075,
                    SkinningCoreInfluenceBytes = 54_100,
                    SkinningSpillHeaderBytes = 54_200,
                    SkinningSpillEntryBytes = 54_300,
                    SkinPaletteUploadBytes = 54_400,
                    SkinningComputeDispatchCount = 55,
                    BlendshapeComputeDispatchCount = 56,
                    SkippedSkinningComputeDispatchCount = 56_100,
                    SkippedBlendshapeComputeDispatchCount = 56_150,
                    ReusedSkinnedOutputBufferCount = 56_200,
                    LiveSkinningShaderPermutationCount = 56_300,
                    BlendshapeAuthoredShapeCount = 56_400,
                    BlendshapeActiveShapeCount = 56_500,
                    BlendshapeAffectedVertexCount = 56_600,
                    CompactedActiveBlendshapeCount = 56_700,
                    LiveBlendshapeShaderPermutationCount = 56_800,
                    AvatarSourceMeshCount = 57,
                    AvatarOptimizedLodCount = 58,
                    AvatarMeshletCount = 59,
                    AvatarVisibilityBufferCount = 60,
                    AvatarClusterVirtualizedCount = 61,
                    AvatarOctahedralImpostorCount = 62,
                    AvatarGaussianSplatCount = 63,
                    RenderAssetCostRows =
                    [
                        new RenderAssetCostRowData
                        {
                            SourceAssetIdentity = "Assets/Models/avatar.fbx",
                            CookedVariantIdentity = "avatar:lod0:deferred",
                            MeshName = "AvatarBody",
                            MaterialName = "Skin",
                            Representation = "source_mesh",
                            DrawCalls = 64,
                            Triangles = 65_000,
                            MaterialSlots = 66,
                            TextureCount = 67,
                            SkinnedDraws = 68
                        }
                    ],
                },
                GpuDriven = new RenderProfilerGpuDrivenData
                {
                    GpuDrivenCulledCommandCount = 69,
                    GpuDrivenActiveBucketCount = 70,
                    GpuDrivenEmptyBucketSkips = 71,
                    GpuDrivenFullBucketScans = 72,
                    GpuDrivenMaterialScatterDispatches = 73,
                    GpuDrivenIndirectCommandGenerationMs = 0.74,
                    GpuDrivenGpuCullMs = 0.75,
                    GpuDrivenGpuSortCompactMs = 0.76,
                    GpuDrivenDelayedDrawCountBufferValue = 77,
                    GpuDrivenDelayedDiagnosticReadbackBytes = 78,
                    GpuDrivenDelayedDiagnosticReadbackCount = 79,
                    GpuCompactionOverflow = 80,
                    GpuActiveListOverflow = 81,
                    GpuBucketOverflow = 82,
                    GpuMeshletOverflow = 83,
                    GpuHiZMode = "two-phase-history-depth",
                    GpuHiZOnePhaseFrames = 84,
                    GpuHiZTwoPhaseFrames = 85,
                    GpuHiZPhaseOneDraws = 86,
                    GpuHiZPhaseTwoDraws = 87,
                    VisibilityPassDraws = 88,
                    VisibilityClassifiedPixels = 89,
                    VisibilityActiveMaterialTiles = 90,
                    VisibilityClassificationOverflow = 91,
                    VisibilityReconstructionMs = 0.92,
                    VisibilityMaterialShadingMs = 0.93,
                },
            },
            GpuMeshletRequestedFrames = 5,
            GpuMeshletProductionFrames = 4,
            GpuMeshletFallbackFrames = 1,
            GpuMeshletDispatchSkipped = 2,
            GpuMeshletTaskRecordsEmitted = 10_000,
            GpuMeshletTaskRecordsFrustumCulled = 1_000,
            GpuMeshletTaskRecordsConeCulled = 500,
            GpuMeshletTaskRecordsHiZCulled = 250,
            GpuMeshletExpansionOverflowCount = 3,
            GpuMeshletBufferBytesResident = 2_097_152,
            GpuMeshletLastVisibleMeshletCount = 512,
            GpuMeshletLastDispatchedMeshletCount = 384,
            GpuMeshletLastTaskRecordOverflowCount = 1,
            GpuMeshletLastDispatchMs = 0.625,
            GpuMeshletLastReadbackBytes = 12,
            GpuMeshletCacheHits = 8,
            GpuMeshletCacheMisses = 2,
            GpuMeshletCacheStale = 1,
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
            VulkanFrameLoop = new VulkanFrameLoopTelemetryData
            {
                FrameSampleTimingQueriesMs = 0.125,
                FrameDrainRetiredResourcesMs = 4.5,
                FrameAcquireBridgeSubmitMs = 0.25,
                FrameWaitSwapchainImageMs = 0.375,
                FrameResetDynamicUniformRingMs = 0.0625,
                FrameOpTotalCount = 101,
                FrameOpClearCount = 2,
                FrameOpMeshDrawCount = 33,
                FrameOpIndirectDrawCount = 7,
                FrameOpMeshTaskDispatchCount = 5,
                FrameOpBlitCount = 8,
                FrameOpComputeCount = 9,
                FrameOpSwapchainWriteCount = 4,
                FrameOpFboWriteCount = 49,
                FrameOpUniquePassCount = 6,
                FrameOpUniqueContextCount = 3,
                FrameOpUniqueTargetCount = 5,
                CommandBufferCleanReuseCount = 10,
                CommandBufferRecordCount = 11,
                CommandBufferForcedDirtyCount = 12,
                CommandBufferFrameOpSignatureDirtyCount = 13,
                CommandBufferPlannerDirtyCount = 14,
                CommandBufferProfilerDirtyCount = 15,
                CommandBufferDirtySummary = "forced,frame-ops,dirty:RecreateSwapchain",
                CommandChainsScheduled = 26,
                CommandChainsRecorded = 27,
                CommandChainsReused = 28,
                CommandChainsFrameDataRefreshed = 29,
                VolatileCommandChainsRecorded = 30,
                PrimaryCommandBuffersReused = 31,
                PrimaryCommandBuffersRecorded = 32,
                VisibilityPacketCount = 33,
                RenderPacketCount = 34,
                SecondaryCommandBufferCount = 35,
                CommandChainWorkerRecordMs = 1.125,
                RenderThreadWaitForChainWorkersMs = 0.375,
                FirstCommandChainStructuralDirtyReason = "structure:draw-count",
                FirstCommandChainDescriptorGenerationMismatch = "descriptor:g12->g13",
                FirstCommandChainResourcePlanRevisionMismatch = "resource-plan:r4->r5",
                RetiredDescriptorPoolCount = 16,
                RetiredPipelineCount = 17,
                RetiredFramebufferCount = 18,
                RetiredBufferCount = 19,
                RetiredBufferMemoryCount = 20,
                RetiredImageCount = 21,
                RetiredImageViewCount = 22,
                RetiredSamplerCount = 23,
                RetiredImageMemoryCount = 24,
                RetiredImageBytes = 25_165_824,
            },
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
            CpuSpatialTreeMode = "Bvh",
            CpuSpatialTreeNodeCount = 31,
            CpuSpatialTreeItemCount = 128,
            CpuSpatialTreeRootItemCount = 4,
            CpuSpatialTreeMaxNodeItemCount = 8,
            CpuSpatialTreeMaxDepth = 5,
            CpuSpatialTreeUnboundedItemCount = 4,
            CpuSpatialTreeCollectMs = 1.25,
            CpuSpatialTreeMaxCollectMs = 0.75,
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
        clone.GpuCpuFallbackEvents.ShouldBe(2);
        clone.GpuCpuFallbackRecoveredCommands.ShouldBe(17);
        clone.ForbiddenGpuFallbackEvents.ShouldBe(1);
        clone.GpuMappedBuffers.ShouldBe(3);
        clone.GpuReadbackBytes.ShouldBe(4096);
        clone.ShadowAtlasSolve.ElapsedMilliseconds.ShouldBe(1.25);
        clone.ShadowAtlasSolve.ClassifiedRequestCount.ShouldBe(12);
        clone.ShadowAtlasSolve.DirectionalRequestCount.ShouldBe(4);
        clone.ShadowAtlasSolve.SpotRequestCount.ShouldBe(2);
        clone.ShadowAtlasSolve.PointRequestCount.ShouldBe(6);
        clone.ShadowAtlasSolve.DepthRequestCount.ShouldBe(10);
        clone.ShadowAtlasSolve.Variance2RequestCount.ShouldBe(1);
        clone.ShadowAtlasSolve.ExponentialVariance4RequestCount.ShouldBe(1);
        clone.ShadowAtlasSolve.BalancedSolveAttemptCount.ShouldBe(3);
        clone.ShadowAtlasSolve.FailedCandidateCount.ShouldBe(2);
        clone.ShadowAtlasSolve.DemotionCount.ShouldBe(5);
        clone.ShadowAtlasSolve.StickyDemotionCount.ShouldBe(1);
        clone.ShadowAtlasSolve.DirectionalGroupDemotionCount.ShouldBe(4);
        clone.ShadowAtlasSolve.PriorReserveHitCount.ShouldBe(7);
        clone.ShadowAtlasSolve.PriorReserveMissCount.ShouldBe(2);
        clone.ShadowAtlasSolve.PriorSubBlockHitCount.ShouldBe(1);
        clone.ShadowAtlasSolve.PriorSubBlockMissCount.ShouldBe(1);
        clone.ShadowAtlasSolve.PageAllocationAttemptCount.ShouldBe(8);
        clone.ShadowAtlasSolve.PageAllocationSuccessCount.ShouldBe(6);
        clone.ShadowAtlasSolve.PageCreateAttemptCount.ShouldBe(1);
        clone.ShadowAtlasSolve.PageCreateSuccessCount.ShouldBe(1);
        clone.ShadowAtlasSolve.PageClearCount.ShouldBe(2);
        clone.ShadowAtlasSolve.DirectionalGroupSeedCount.ShouldBe(1);
        clone.ShadowAtlasSolve.DirectionalGroupMemberCount.ShouldBe(4);
        clone.ShadowAtlasSolve.PointGroupSeedCount.ShouldBe(1);
        clone.ShadowAtlasSolve.PointGroupMemberCount.ShouldBe(6);
        clone.ShadowAtlasSolve.LastFailureReason.ShouldBe("PageBudgetExceeded");
        clone.RenderProfilerV2.ProfileCaptureSchemaVersion.ShouldBe(2);
        var rendererState = clone.RenderProfilerV2.RendererState;
        rendererState.IndirectCountCalls.ShouldBe(6);
        rendererState.ShaderProgramSwitches.ShouldBe(7);
        rendererState.ProgramPipelineSwitches.ShouldBe(8);
        rendererState.VaoBinds.ShouldBe(9);
        rendererState.VaoBindSkips.ShouldBe(10);
        rendererState.ArrayBufferBinds.ShouldBe(11);
        rendererState.ElementArrayBufferBinds.ShouldBe(12);
        rendererState.DrawIndirectBufferBinds.ShouldBe(13);
        rendererState.ParameterBufferBinds.ShouldBe(14);
        rendererState.SsboBinds.ShouldBe(15);
        rendererState.UboBinds.ShouldBe(16);
        rendererState.TextureBinds.ShouldBe(17);
        rendererState.TextureBindSkips.ShouldBe(18);
        rendererState.TextureUnitSwitches.ShouldBe(19);
        rendererState.UniformCalls.ShouldBe(20);
        rendererState.SamplerUniformCalls.ShouldBe(21);
        rendererState.BufferUploadBytes.ShouldBe(22_000);
        rendererState.BarrierCalls.ShouldBe(23);
        rendererState.BarrierAll.ShouldBe(24);
        rendererState.BarrierCommand.ShouldBe(25);
        rendererState.BarrierBufferUpdate.ShouldBe(26);
        rendererState.BarrierShaderStorage.ShouldBe(27);
        rendererState.BarrierTextureFetch.ShouldBe(28);
        rendererState.BarrierTextureUpdate.ShouldBe(29);
        rendererState.BarrierFramebuffer.ShouldBe(30);
        rendererState.TimestampQueryCount.ShouldBe(31);
        rendererState.TimestampQueryReadbackBytes.ShouldBe(32_000);
        rendererState.TimestampDenseModeFrames.ShouldBe(1);
        rendererState.RedundantStateSkips.ShouldBe(33);
        rendererState.CpuDirectDrawCalls.ShouldBe(34);
        rendererState.GpuIndirectDrawCalls.ShouldBe(35);
        rendererState.GpuMeshletDrawCalls.ShouldBe(36);
        rendererState.UnknownStrategyDrawCalls.ShouldBe(37);
        rendererState.ActiveTextureBindingRung.ShouldBe("bindless-material-table");
        rendererState.ActiveStereoMode.ShouldBe("multiview");
        rendererState.ActiveSubmissionStrategy.ShouldBe("GpuIndirectZeroReadback");
        rendererState.ActiveRenderBackend.ShouldBe("OpenGL");
        rendererState.ValidationLayersEnabled.ShouldBeFalse();
        rendererState.DebugOutputEnabled.ShouldBeTrue();
        rendererState.GpuTimestampsDenseMode.ShouldBeTrue();

        var sceneAssets = clone.RenderProfilerV2.SceneAssets;
        sceneAssets.VisibleRendererCount.ShouldBe(38);
        sceneAssets.VisibleSubmeshCount.ShouldBe(39);
        sceneAssets.VisibleTriangleCount.ShouldBe(40_000);
        sceneAssets.MaterialSlotCount.ShouldBe(41);
        sceneAssets.ActiveMaterialCount.ShouldBe(42);
        sceneAssets.TextureCount.ShouldBe(43);
        sceneAssets.ResidentTextureMemoryBytes.ShouldBe(44_000);
        sceneAssets.TextureUploadJobs.ShouldBe(45);
        sceneAssets.TextureUploadBytes.ShouldBe(46_000);
        sceneAssets.TextureUploadMs.ShouldBe(0.46);
        sceneAssets.ShaderVariantsRequested.ShouldBe(47);
        sceneAssets.ShaderVariantsWarming.ShouldBe(48);
        sceneAssets.ShaderVariantsLinked.ShouldBe(49);
        sceneAssets.ShaderVariantsFailed.ShouldBe(1);
        sceneAssets.ShaderVariantsLoadedFromDiskCache.ShouldBe(50);
        sceneAssets.ShaderVariantsGeneratedThisRun.ShouldBe(51);
        sceneAssets.SkinnedRendererCount.ShouldBe(52);
        sceneAssets.BoneMatrixUploadBytes.ShouldBe(53_000);
        sceneAssets.BlendshapeWeightUploadBytes.ShouldBe(54_000);
        sceneAssets.BlendshapeActiveListUploadBytes.ShouldBe(54_050);
        sceneAssets.BlendshapeDeltaBytes.ShouldBe(54_075);
        sceneAssets.SkinningCoreInfluenceBytes.ShouldBe(54_100);
        sceneAssets.SkinningSpillHeaderBytes.ShouldBe(54_200);
        sceneAssets.SkinningSpillEntryBytes.ShouldBe(54_300);
        sceneAssets.SkinPaletteUploadBytes.ShouldBe(54_400);
        sceneAssets.SkinningComputeDispatchCount.ShouldBe(55);
        sceneAssets.BlendshapeComputeDispatchCount.ShouldBe(56);
        sceneAssets.SkippedSkinningComputeDispatchCount.ShouldBe(56_100);
        sceneAssets.SkippedBlendshapeComputeDispatchCount.ShouldBe(56_150);
        sceneAssets.ReusedSkinnedOutputBufferCount.ShouldBe(56_200);
        sceneAssets.LiveSkinningShaderPermutationCount.ShouldBe(56_300);
        sceneAssets.BlendshapeAuthoredShapeCount.ShouldBe(56_400);
        sceneAssets.BlendshapeActiveShapeCount.ShouldBe(56_500);
        sceneAssets.BlendshapeAffectedVertexCount.ShouldBe(56_600);
        sceneAssets.CompactedActiveBlendshapeCount.ShouldBe(56_700);
        sceneAssets.LiveBlendshapeShaderPermutationCount.ShouldBe(56_800);
        sceneAssets.AvatarSourceMeshCount.ShouldBe(57);
        sceneAssets.AvatarOptimizedLodCount.ShouldBe(58);
        sceneAssets.AvatarMeshletCount.ShouldBe(59);
        sceneAssets.AvatarVisibilityBufferCount.ShouldBe(60);
        sceneAssets.AvatarClusterVirtualizedCount.ShouldBe(61);
        sceneAssets.AvatarOctahedralImpostorCount.ShouldBe(62);
        sceneAssets.AvatarGaussianSplatCount.ShouldBe(63);
        sceneAssets.RenderAssetCostRows.Length.ShouldBe(1);
        sceneAssets.RenderAssetCostRows[0].SourceAssetIdentity.ShouldBe("Assets/Models/avatar.fbx");
        sceneAssets.RenderAssetCostRows[0].CookedVariantIdentity.ShouldBe("avatar:lod0:deferred");
        sceneAssets.RenderAssetCostRows[0].Triangles.ShouldBe(65_000);
        sceneAssets.RenderAssetCostRows[0].SkinnedDraws.ShouldBe(68);

        var gpuDriven = clone.RenderProfilerV2.GpuDriven;
        gpuDriven.GpuDrivenCulledCommandCount.ShouldBe(69);
        gpuDriven.GpuDrivenActiveBucketCount.ShouldBe(70);
        gpuDriven.GpuDrivenEmptyBucketSkips.ShouldBe(71);
        gpuDriven.GpuDrivenFullBucketScans.ShouldBe(72);
        gpuDriven.GpuDrivenMaterialScatterDispatches.ShouldBe(73);
        gpuDriven.GpuDrivenIndirectCommandGenerationMs.ShouldBe(0.74);
        gpuDriven.GpuDrivenGpuCullMs.ShouldBe(0.75);
        gpuDriven.GpuDrivenGpuSortCompactMs.ShouldBe(0.76);
        gpuDriven.GpuDrivenDelayedDrawCountBufferValue.ShouldBe(77);
        gpuDriven.GpuDrivenDelayedDiagnosticReadbackBytes.ShouldBe(78);
        gpuDriven.GpuDrivenDelayedDiagnosticReadbackCount.ShouldBe(79);
        gpuDriven.GpuCompactionOverflow.ShouldBe(80);
        gpuDriven.GpuActiveListOverflow.ShouldBe(81);
        gpuDriven.GpuBucketOverflow.ShouldBe(82);
        gpuDriven.GpuMeshletOverflow.ShouldBe(83);
        gpuDriven.GpuHiZMode.ShouldBe("two-phase-history-depth");
        gpuDriven.GpuHiZOnePhaseFrames.ShouldBe(84);
        gpuDriven.GpuHiZTwoPhaseFrames.ShouldBe(85);
        gpuDriven.GpuHiZPhaseOneDraws.ShouldBe(86);
        gpuDriven.GpuHiZPhaseTwoDraws.ShouldBe(87);
        gpuDriven.VisibilityPassDraws.ShouldBe(88);
        gpuDriven.VisibilityClassifiedPixels.ShouldBe(89);
        gpuDriven.VisibilityActiveMaterialTiles.ShouldBe(90);
        gpuDriven.VisibilityClassificationOverflow.ShouldBe(91);
        gpuDriven.VisibilityReconstructionMs.ShouldBe(0.92);
        gpuDriven.VisibilityMaterialShadingMs.ShouldBe(0.93);
        clone.GpuMeshletRequestedFrames.ShouldBe(5);
        clone.GpuMeshletProductionFrames.ShouldBe(4);
        clone.GpuMeshletFallbackFrames.ShouldBe(1);
        clone.GpuMeshletDispatchSkipped.ShouldBe(2);
        clone.GpuMeshletTaskRecordsEmitted.ShouldBe(10_000);
        clone.GpuMeshletTaskRecordsFrustumCulled.ShouldBe(1_000);
        clone.GpuMeshletTaskRecordsConeCulled.ShouldBe(500);
        clone.GpuMeshletTaskRecordsHiZCulled.ShouldBe(250);
        clone.GpuMeshletExpansionOverflowCount.ShouldBe(3);
        clone.GpuMeshletBufferBytesResident.ShouldBe(2_097_152);
        clone.GpuMeshletLastVisibleMeshletCount.ShouldBe(512);
        clone.GpuMeshletLastDispatchedMeshletCount.ShouldBe(384);
        clone.GpuMeshletLastTaskRecordOverflowCount.ShouldBe(1);
        clone.GpuMeshletLastDispatchMs.ShouldBe(0.625);
        clone.GpuMeshletLastReadbackBytes.ShouldBe(12);
        clone.GpuMeshletCacheHits.ShouldBe(8);
        clone.GpuMeshletCacheMisses.ShouldBe(2);
        clone.GpuMeshletCacheStale.ShouldBe(1);
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
        clone.VulkanFrameLoop.FrameOpTotalCount.ShouldBe(101);
        clone.VulkanFrameLoop.FrameSampleTimingQueriesMs.ShouldBe(0.125);
        clone.VulkanFrameLoop.FrameDrainRetiredResourcesMs.ShouldBe(4.5);
        clone.VulkanFrameLoop.FrameAcquireBridgeSubmitMs.ShouldBe(0.25);
        clone.VulkanFrameLoop.FrameWaitSwapchainImageMs.ShouldBe(0.375);
        clone.VulkanFrameLoop.FrameResetDynamicUniformRingMs.ShouldBe(0.0625);
        clone.VulkanFrameLoop.FrameOpClearCount.ShouldBe(2);
        clone.VulkanFrameLoop.FrameOpMeshDrawCount.ShouldBe(33);
        clone.VulkanFrameLoop.FrameOpIndirectDrawCount.ShouldBe(7);
        clone.VulkanFrameLoop.FrameOpMeshTaskDispatchCount.ShouldBe(5);
        clone.VulkanFrameLoop.FrameOpBlitCount.ShouldBe(8);
        clone.VulkanFrameLoop.FrameOpComputeCount.ShouldBe(9);
        clone.VulkanFrameLoop.FrameOpSwapchainWriteCount.ShouldBe(4);
        clone.VulkanFrameLoop.FrameOpFboWriteCount.ShouldBe(49);
        clone.VulkanFrameLoop.FrameOpUniquePassCount.ShouldBe(6);
        clone.VulkanFrameLoop.FrameOpUniqueContextCount.ShouldBe(3);
        clone.VulkanFrameLoop.FrameOpUniqueTargetCount.ShouldBe(5);
        clone.VulkanFrameLoop.CommandBufferCleanReuseCount.ShouldBe(10);
        clone.VulkanFrameLoop.CommandBufferRecordCount.ShouldBe(11);
        clone.VulkanFrameLoop.CommandBufferForcedDirtyCount.ShouldBe(12);
        clone.VulkanFrameLoop.CommandBufferFrameOpSignatureDirtyCount.ShouldBe(13);
        clone.VulkanFrameLoop.CommandBufferPlannerDirtyCount.ShouldBe(14);
        clone.VulkanFrameLoop.CommandBufferProfilerDirtyCount.ShouldBe(15);
        clone.VulkanFrameLoop.CommandBufferDirtySummary.ShouldBe("forced,frame-ops,dirty:RecreateSwapchain");
        clone.VulkanFrameLoop.CommandChainsScheduled.ShouldBe(26);
        clone.VulkanFrameLoop.CommandChainsRecorded.ShouldBe(27);
        clone.VulkanFrameLoop.CommandChainsReused.ShouldBe(28);
        clone.VulkanFrameLoop.CommandChainsFrameDataRefreshed.ShouldBe(29);
        clone.VulkanFrameLoop.VolatileCommandChainsRecorded.ShouldBe(30);
        clone.VulkanFrameLoop.PrimaryCommandBuffersReused.ShouldBe(31);
        clone.VulkanFrameLoop.PrimaryCommandBuffersRecorded.ShouldBe(32);
        clone.VulkanFrameLoop.VisibilityPacketCount.ShouldBe(33);
        clone.VulkanFrameLoop.RenderPacketCount.ShouldBe(34);
        clone.VulkanFrameLoop.SecondaryCommandBufferCount.ShouldBe(35);
        clone.VulkanFrameLoop.CommandChainWorkerRecordMs.ShouldBe(1.125);
        clone.VulkanFrameLoop.RenderThreadWaitForChainWorkersMs.ShouldBe(0.375);
        clone.VulkanFrameLoop.FirstCommandChainStructuralDirtyReason.ShouldBe("structure:draw-count");
        clone.VulkanFrameLoop.FirstCommandChainDescriptorGenerationMismatch.ShouldBe("descriptor:g12->g13");
        clone.VulkanFrameLoop.FirstCommandChainResourcePlanRevisionMismatch.ShouldBe("resource-plan:r4->r5");
        clone.VulkanFrameLoop.RetiredDescriptorPoolCount.ShouldBe(16);
        clone.VulkanFrameLoop.RetiredPipelineCount.ShouldBe(17);
        clone.VulkanFrameLoop.RetiredFramebufferCount.ShouldBe(18);
        clone.VulkanFrameLoop.RetiredBufferCount.ShouldBe(19);
        clone.VulkanFrameLoop.RetiredBufferMemoryCount.ShouldBe(20);
        clone.VulkanFrameLoop.RetiredImageCount.ShouldBe(21);
        clone.VulkanFrameLoop.RetiredImageViewCount.ShouldBe(22);
        clone.VulkanFrameLoop.RetiredSamplerCount.ShouldBe(23);
        clone.VulkanFrameLoop.RetiredImageMemoryCount.ShouldBe(24);
        clone.VulkanFrameLoop.RetiredImageBytes.ShouldBe(25_165_824);
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
        clone.CpuSpatialTreeMode.ShouldBe("Bvh");
        clone.CpuSpatialTreeNodeCount.ShouldBe(31);
        clone.CpuSpatialTreeItemCount.ShouldBe(128);
        clone.CpuSpatialTreeRootItemCount.ShouldBe(4);
        clone.CpuSpatialTreeMaxNodeItemCount.ShouldBe(8);
        clone.CpuSpatialTreeMaxDepth.ShouldBe(5);
        clone.CpuSpatialTreeUnboundedItemCount.ShouldBe(4);
        clone.CpuSpatialTreeCollectMs.ShouldBe(1.25);
        clone.CpuSpatialTreeMaxCollectMs.ShouldBe(0.75);
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
                    RootNodes = [new ProfilerNodeData { Name = "Tick", ElapsedMs = 8.0f, ScopeKind = ProfilerScopeKind.AlwaysOnHotPathLoop, Children = [] }]
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
        clone.Threads[0].RootNodes[0].ScopeKind.ShouldBe(ProfilerScopeKind.AlwaysOnHotPathLoop);
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
