using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanRenderQueryTests
{
    private static VulkanQueryCapabilities FullCapabilities => VulkanQueryCapabilities.Unsupported with
    {
        OcclusionQueryPreciseAdvertised = true,
        OcclusionQueryPreciseEnabled = true,
        PipelineStatisticsAdvertised = true,
        PipelineStatisticsEnabled = true,
        InheritedQueriesAdvertised = true,
        InheritedQueriesEnabled = true,
        HostQueryResetAdvertised = true,
        HostQueryResetEnabled = true,
        Synchronization2Enabled = true,
        GraphicsQueueFamily = 2u,
        GraphicsTimestampValidBits = 48u,
        GraphicsSupportedStageMask = ulong.MaxValue,
        TimestampPeriodNanoseconds = 1.25,
        TransformFeedbackExtensionEnabled = true,
        TransformFeedbackCommandsLoaded = true,
        TransformFeedbackQueriesEnabled = true,
        MaxTransformFeedbackStreams = 4u,
        PrimitivesGeneratedExtensionAdvertised = true,
        PrimitivesGeneratedExtensionEnabled = true,
        PrimitivesGeneratedQueryEnabled = true,
        PrimitivesGeneratedNonZeroStreamsEnabled = true,
        MeshShaderExtensionEnabled = true,
        MeshShaderCommandsLoaded = true,
        MeshShaderQueriesEnabled = true,
    };

    [Test]
    public void BooleanOcclusionModes_MapToSameNonPreciseVulkanSemantics()
    {
        VulkanQueryPlan any = VulkanQueryDescriptorMapper.Map(
            RenderQueryDescriptor.BooleanOcclusion,
            FullCapabilities,
            2u);
        VulkanQueryPlan conservative = VulkanQueryDescriptorMapper.Map(
            RenderQueryDescriptor.ConservativeOcclusion,
            FullCapabilities,
            2u);

        any.Supported.ShouldBeTrue();
        conservative.Supported.ShouldBeTrue();
        any.QueryType.ShouldBe(QueryType.Occlusion);
        conservative.QueryType.ShouldBe(QueryType.Occlusion);
        any.ControlFlags.ShouldBe(QueryControlFlags.None);
        conservative.ControlFlags.ShouldBe(QueryControlFlags.None);
        any.ResultLayout.Aggregation.ShouldBe(ERenderQueryAggregation.AnyNonZero);
        any.ResultLayout.QueryCount.ShouldBe(2u);
        any.ResultLayout.NativeValuesPerQuery.ShouldBe(2u);
    }

    [Test]
    public void ExactOcclusion_RequiresEnabledPreciseFeature()
    {
        VulkanQueryCapabilities disabled = FullCapabilities with { OcclusionQueryPreciseEnabled = false };
        VulkanQueryPlan rejected = VulkanQueryDescriptorMapper.Map(RenderQueryDescriptor.ExactOcclusion, disabled, 1u);
        VulkanQueryPlan supported = VulkanQueryDescriptorMapper.Map(RenderQueryDescriptor.ExactOcclusion, FullCapabilities, 3u);

        rejected.Supported.ShouldBeFalse();
        rejected.UnsupportedReason!.ShouldContain("advertised but was not selected");
        supported.ControlFlags.ShouldBe(QueryControlFlags.PreciseBit);
        supported.ResultLayout.Aggregation.ShouldBe(ERenderQueryAggregation.Sum);
        supported.ResultLayout.QueryCount.ShouldBe(3u);
    }

    [Test]
    public void PipelineStatistics_UseExactMaskAndVulkanBitOrder()
    {
        RenderQueryDescriptor descriptor = new(
            ERenderQueryKind.PipelineStatistics,
            Statistics: ERenderPipelineStatistics.InputAssemblyVertices |
                ERenderPipelineStatistics.FragmentShaderInvocations |
                ERenderPipelineStatistics.ComputeShaderInvocations);

        VulkanQueryPlan plan = VulkanQueryDescriptorMapper.Map(descriptor, FullCapabilities);

        plan.Supported.ShouldBeTrue();
        plan.PipelineStatistics.ShouldBe((QueryPipelineStatisticFlags)(uint)descriptor.Statistics);
        plan.ResultLayout.ValuesPerQuery.ShouldBe(3u);
        plan.ResultLayout.AvailabilityValueOffset.ShouldBe(3);
        plan.ResultLayout.GetField(0u).ShouldBe(ERenderQueryField.InputAssemblyVertices);
        plan.ResultLayout.GetField(1u).ShouldBe(ERenderQueryField.FragmentShaderInvocations);
        plan.ResultLayout.GetField(2u).ShouldBe(ERenderQueryField.ComputeShaderInvocations);
    }

    [Test]
    public void PipelineStatistics_RejectZeroMaskAndUnsupportedMeshCounters()
    {
        VulkanQueryPlan zero = VulkanQueryDescriptorMapper.Map(
            new RenderQueryDescriptor(ERenderQueryKind.PipelineStatistics),
            FullCapabilities);
        VulkanQueryPlan mesh = VulkanQueryDescriptorMapper.Map(
            new RenderQueryDescriptor(
                ERenderQueryKind.PipelineStatistics,
                Statistics: ERenderPipelineStatistics.MeshShaderInvocations),
            FullCapabilities with { MeshShaderQueriesEnabled = false });

        zero.Supported.ShouldBeFalse();
        zero.UnsupportedReason!.ShouldContain("nonzero");
        mesh.Supported.ShouldBeFalse();
        mesh.UnsupportedReason!.ShouldContain("meshShaderQueries");
    }

    [Test]
    public void PipelineStatistics_RejectUnknownBitsAndDecodeAllCountersInOrder()
    {
        VulkanQueryPlan unknown = VulkanQueryDescriptorMapper.Map(
            new RenderQueryDescriptor(
                ERenderQueryKind.PipelineStatistics,
                Statistics: (ERenderPipelineStatistics)(1u << 31)),
            FullCapabilities);
        VulkanQueryPlan all = VulkanQueryDescriptorMapper.Map(
            new RenderQueryDescriptor(
                ERenderQueryKind.PipelineStatistics,
                Statistics: ERenderPipelineStatistics.All),
            FullCapabilities);

        unknown.Supported.ShouldBeFalse();
        unknown.UnsupportedReason!.ShouldContain("unknown counter bits");
        all.ResultLayout.ValuesPerQuery.ShouldBe(13u);
        for (uint index = 0u; index < 13u; index++)
            all.ResultLayout.GetField(index).ShouldNotBe(ERenderQueryField.None);
        all.ResultLayout.GetField(13u).ShouldBe(ERenderQueryField.None);
    }

    [Test]
    public void TransformFeedback_UsesTwoValueLayoutAndValidatesStream()
    {
        RenderQueryDescriptor descriptor = new(ERenderQueryKind.TransformFeedback, StreamIndex: 3u);
        VulkanQueryPlan plan = VulkanQueryDescriptorMapper.Map(descriptor, FullCapabilities);
        VulkanQueryPlan invalid = VulkanQueryDescriptorMapper.Map(
            descriptor with { StreamIndex = 4u },
            FullCapabilities);

        plan.Supported.ShouldBeTrue();
        plan.Provider.ShouldBe(EVulkanQueryRecordingProvider.TransformFeedbackIndexed);
        plan.ResultLayout.ValuesPerQuery.ShouldBe(2u);
        plan.ResultLayout.NativeValueCount.ShouldBe(3u);
        plan.ResultLayout.NativeStrideBytes.ShouldBe(24u);
        plan.ResultLayout.GetField(0u).ShouldBe(ERenderQueryField.PrimitivesWritten);
        plan.ResultLayout.GetField(1u).ShouldBe(ERenderQueryField.PrimitivesNeeded);
        invalid.Supported.ShouldBeFalse();
    }

    [Test]
    public void TransformFeedback_RejectsStorageWithoutBothValuesAndAvailability()
    {
        VulkanQueryPlan plan = VulkanQueryDescriptorMapper.Map(
            new RenderQueryDescriptor(ERenderQueryKind.TransformFeedback),
            FullCapabilities);

        plan.ResultLayout.NativeValueCount.ShouldBe(3u);
        plan.ResultLayout.FitsNativeResult(2).ShouldBeFalse();
        plan.ResultLayout.FitsNativeResult(3).ShouldBeTrue();
    }

    [Test]
    public void PrimitivesGenerated_ReportsExactMissingCapability()
    {
        RenderQueryDescriptor descriptor = new(ERenderQueryKind.PrimitivesGenerated, StreamIndex: 1u);
        VulkanQueryPlan missingExtension = VulkanQueryDescriptorMapper.Map(
            descriptor,
            FullCapabilities with { PrimitivesGeneratedExtensionEnabled = false });
        VulkanQueryPlan missingStream = VulkanQueryDescriptorMapper.Map(
            descriptor,
            FullCapabilities with { PrimitivesGeneratedNonZeroStreamsEnabled = false });

        missingExtension.Supported.ShouldBeFalse();
        missingExtension.UnsupportedReason!.ShouldContain("advertised but not enabled");
        missingStream.Supported.ShouldBeFalse();
        missingStream.UnsupportedReason!.ShouldContain("Nonzero streams");
    }

    [Test]
    public void SpecializedFamilies_RequireRealSubsystemOwners()
    {
        VulkanQueryPlan acceleration = VulkanQueryDescriptorMapper.Map(
            new RenderQueryDescriptor(
                ERenderQueryKind.AccelerationStructureProperty,
                Property: ERenderQueryProperty.CompactedSize),
            FullCapabilities);
        VulkanQueryPlan performance = VulkanQueryDescriptorMapper.Map(
            new RenderQueryDescriptor(
                ERenderQueryKind.PerformanceCounter,
                ProviderValueCount: 4u),
            FullCapabilities);
        VulkanQueryPlan video = VulkanQueryDescriptorMapper.Map(
            new RenderQueryDescriptor(ERenderQueryKind.VideoResultStatus),
            FullCapabilities);

        acceleration.UnsupportedReason!.ShouldContain(nameof(ERenderQueryReadStatus.SubsystemUnavailable));
        performance.UnsupportedReason!.ShouldContain(nameof(ERenderQueryReadStatus.SubsystemUnavailable));
        video.UnsupportedReason!.ShouldContain(nameof(ERenderQueryReadStatus.SubsystemUnavailable));
    }

    [Test]
    public void TimestampMath_MasksWrapsAndConvertsToNanoseconds()
    {
        RenderQueryTimestampMath.MaskTicks(0x1FFul, 8u).ShouldBe(0xFFul);
        RenderQueryTimestampMath.DeltaTicks(250ul, 5ul, 8u).ShouldBe(11ul);
        RenderQueryTimestampMath.TicksToNanoseconds(8ul, 1.25).ShouldBe(10ul);
    }

    [Test]
    public void TimestampAndElapsed_UseOneAndTwoContiguousSlots()
    {
        VulkanQueryPlan timestamp = VulkanQueryDescriptorMapper.Map(RenderQueryDescriptor.Timestamp, FullCapabilities);
        VulkanQueryPlan elapsed = VulkanQueryDescriptorMapper.Map(RenderQueryDescriptor.ElapsedTime, FullCapabilities);

        timestamp.ResultLayout.QueryCount.ShouldBe(1u);
        elapsed.ResultLayout.QueryCount.ShouldBe(2u);
        elapsed.ResultLayout.Aggregation.ShouldBe(ERenderQueryAggregation.TimestampDelta);
        RenderQueryDescriptor.ElapsedTime.ResolveQueryCount(8u).ShouldBe(2u);
    }

    [Test]
    public void TimestampStages_RequireOneSupportedBitAndTimestampCapableQueue()
    {
        VulkanQueryCapabilities oneStage = FullCapabilities with { GraphicsSupportedStageMask = 0x10000ul };

        VulkanQueryDescriptorMapper.IsTimestampStageSupported(0x10000ul, oneStage).ShouldBeTrue();
        VulkanQueryDescriptorMapper.IsTimestampStageSupported(0x10001ul, oneStage).ShouldBeFalse();
        VulkanQueryDescriptorMapper.IsTimestampStageSupported(0x1ul, oneStage).ShouldBeFalse();
        VulkanQueryDescriptorMapper.IsTimestampStageSupported(
            0x10000ul,
            oneStage with { GraphicsTimestampValidBits = 0u }).ShouldBeFalse();
    }

    [Test]
    public void ResultLayout_Computes32And64BitNativeSizes()
    {
        RenderQueryResultLayout layout32 = new(
            ERenderQueryKind.TransformFeedback,
            2u,
            3u,
            1u,
            2,
            ERenderQueryIntegerWidth.UInt32,
            ERenderQueryAggregation.ProviderDefined);
        RenderQueryResultLayout layout64 = layout32 with { IntegerWidth = ERenderQueryIntegerWidth.UInt64 };

        layout32.NativeValueCount.ShouldBe(9u);
        layout32.NativeStrideBytes.ShouldBe(12u);
        layout32.NativeSizeBytes.ShouldBe(36u);
        layout64.NativeStrideBytes.ShouldBe(24u);
        layout64.NativeSizeBytes.ShouldBe(72u);
    }

    [Test]
    public void OpenGlAndVulkanShareBooleanAndTimestampSemantics()
    {
        RenderQueryResultLayout glBoolean = GLRenderQuery.CreateResultLayout(RenderQueryDescriptor.BooleanOcclusion);
        RenderQueryResultLayout glTimestamp = GLRenderQuery.CreateResultLayout(RenderQueryDescriptor.Timestamp);
        VulkanQueryPlan vkBoolean = VulkanQueryDescriptorMapper.Map(RenderQueryDescriptor.BooleanOcclusion, FullCapabilities);
        VulkanQueryPlan vkTimestamp = VulkanQueryDescriptorMapper.Map(RenderQueryDescriptor.Timestamp, FullCapabilities);

        glBoolean.Aggregation.ShouldBe(vkBoolean.ResultLayout.Aggregation);
        glBoolean.GetField(0u).ShouldBe(vkBoolean.ResultLayout.GetField(0u));
        glTimestamp.GetField(0u).ShouldBe(vkTimestamp.ResultLayout.GetField(0u));
        glTimestamp.IntegerWidth.ShouldBe(vkTimestamp.ResultLayout.IntegerWidth);
    }

    [Test]
    public void OpenGlAndVulkanShareExactElapsedAndPrimitiveResultFields()
    {
        RenderQueryDescriptor primitive = new(ERenderQueryKind.PrimitivesGenerated);
        RenderQueryResultLayout glExact = GLRenderQuery.CreateResultLayout(RenderQueryDescriptor.ExactOcclusion);
        RenderQueryResultLayout glElapsed = GLRenderQuery.CreateResultLayout(RenderQueryDescriptor.ElapsedTime);
        RenderQueryResultLayout glPrimitive = GLRenderQuery.CreateResultLayout(primitive);
        VulkanQueryPlan vkExact = VulkanQueryDescriptorMapper.Map(RenderQueryDescriptor.ExactOcclusion, FullCapabilities);
        VulkanQueryPlan vkElapsed = VulkanQueryDescriptorMapper.Map(RenderQueryDescriptor.ElapsedTime, FullCapabilities);
        VulkanQueryPlan vkPrimitive = VulkanQueryDescriptorMapper.Map(primitive, FullCapabilities);

        glExact.Aggregation.ShouldBe(vkExact.ResultLayout.Aggregation);
        glExact.GetField(0u).ShouldBe(vkExact.ResultLayout.GetField(0u));
        glElapsed.GetField(0u).ShouldBe(vkElapsed.ResultLayout.GetField(0u));
        glPrimitive.GetField(0u).ShouldBe(vkPrimitive.ResultLayout.GetField(0u));
        GLRenderQuery.CreateResultLayout(new(ERenderQueryKind.TransformFeedback)).ValueCount.ShouldBe(0u);
    }

    [Test]
    public void SlotAllocator_UsesContiguousRangesAndNeverReusesPendingRange()
    {
        RenderQuerySlotAllocator allocator = new(8u);
        allocator.TryAllocate(3u, out uint first).ShouldBeTrue();
        allocator.TryAllocate(2u, out uint second).ShouldBeTrue();
        first.ShouldBe(0u);
        second.ShouldBe(3u);
        allocator.Allocated.ShouldBe(5u);
        allocator.Release(first, 3u).ShouldBeTrue();
        allocator.TryAllocate(4u, out uint third).ShouldBeFalse();
        allocator.Release(second, 2u).ShouldBeTrue();
        allocator.TryAllocate(4u, out third).ShouldBeTrue();
        third.ShouldBe(0u);
        allocator.HighWater.ShouldBe(5u);
    }

    [Test]
    public void ArenaPolicy_GrowsDeterministicallyAndStopsAtBound()
    {
        VulkanQueryArenaPolicy.ResolveChunkCapacity(1u).ShouldBe(256u);
        VulkanQueryArenaPolicy.ResolveChunkCapacity(256u).ShouldBe(256u);
        VulkanQueryArenaPolicy.ResolveChunkCapacity(257u).ShouldBe(512u);
        VulkanQueryArenaPolicy.CanGrow(15).ShouldBeTrue();
        VulkanQueryArenaPolicy.CanGrow(16).ShouldBeFalse();
        VulkanQueryArenaPolicy.CanGrow(-1).ShouldBeFalse();
    }

    [Test]
    public void SlotAllocator_SteadyStateDoesNotAllocateManagedMemory()
    {
        RenderQuerySlotAllocator allocator = new(64u);
        allocator.TryAllocate(4u, out uint first).ShouldBeTrue();
        allocator.Release(first, 4u).ShouldBeTrue();

        long before = GC.GetAllocatedBytesForCurrentThread();
        bool succeeded = true;
        for (int iteration = 0; iteration < 1000; iteration++)
        {
            succeeded &= allocator.TryAllocate(4u, out first);
            succeeded &= allocator.Release(first, 4u);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        succeeded.ShouldBeTrue();
        (after - before).ShouldBe(0L);
    }

    [Test]
    public void SlotStateMachine_RejectsReuseBeforeAvailability()
    {
        RenderQuerySlotStateMachine.IsTransitionValid(ERenderQuerySlotState.Allocated, ERenderQuerySlotState.ResetRecorded).ShouldBeTrue();
        RenderQuerySlotStateMachine.IsTransitionValid(ERenderQuerySlotState.ResetRecorded, ERenderQuerySlotState.Recording).ShouldBeTrue();
        RenderQuerySlotStateMachine.IsTransitionValid(ERenderQuerySlotState.Recording, ERenderQuerySlotState.Ended).ShouldBeTrue();
        RenderQuerySlotStateMachine.IsTransitionValid(ERenderQuerySlotState.Ended, ERenderQuerySlotState.Submitted).ShouldBeTrue();
        RenderQuerySlotStateMachine.IsTransitionValid(ERenderQuerySlotState.Submitted, ERenderQuerySlotState.ResetRecorded).ShouldBeFalse();
        RenderQuerySlotStateMachine.IsTransitionValid(ERenderQuerySlotState.Submitted, ERenderQuerySlotState.Available).ShouldBeTrue();
    }

    [Test]
    public void Tickets_DistinguishEpochPoolRangeAndSubmission()
    {
        RenderQueryTicket first = new(10ul, 2u, 8u, 2u, 40ul);
        RenderQueryTicket newer = first with { Epoch = 11ul, SubmissionValue = 41ul };

        first.IsValid.ShouldBeTrue();
        newer.ShouldNotBe(first);
        newer.FirstQuery.ShouldBe(first.FirstQuery);
        newer.PoolIdentity.ShouldBe(first.PoolIdentity);
    }
}
