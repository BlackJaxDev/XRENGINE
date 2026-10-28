using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class VulkanPhysicsChainParityContractTests
{
    [Test]
    public void Dispatcher_ResolvesBackendAdapterThroughStableCapability()
    {
        string factory = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/PhysicsChainComputeBackendFactory.cs");
        string dispatcher = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs");

        factory.ShouldContain("IPhysicsChainComputeBackendFactoryCapability");
        factory.ShouldContain("TryCreatePhysicsChainComputeBackend(out backend)");
        factory.ShouldNotContain("OpenGLPhysicsChainComputeBackend");
        factory.ShouldNotContain("VulkanPhysicsChainComputeBackend");
        dispatcher.ShouldContain("PhysicsChainComputeBackendFactory.TryCreate");
        dispatcher.ShouldContain("Capabilities.SupportsCorePipeline");
    }

    [Test]
    public void VulkanBackend_UsesOrderedRendererOperations()
    {
        string adapter = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/PhysicsCompute/VulkanPhysicsChainComputeBackend.cs");
        string work = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.OrderedComputeApi.cs")
            + ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.FrameOperationApi.cs");
        string recorder = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.NativeRecordingServices.cs");

        adapter.ShouldContain("TryDispatchComputeIndirect");
        adapter.ShouldContain("TryEnqueuePhysicsChainBufferCopy");
        adapter.ShouldContain("TryCompleteOrderedComputePass");
        adapter.ShouldContain("InsertGpuFence");
        work.ShouldContain("ComputeDispatchIndirectOp");
        work.ShouldContain("BufferCopyOp");
        work.ShouldContain("CreateMemoryBarrierOperation(passIndex, mask, context), passIndex)");
        work.ShouldContain("=> MemoryBarrierOp.Rent(passIndex, mask, context);");
        recorder.ShouldContain("RecordComputeDispatchIndirectOp");
        recorder.ShouldContain("RecordBufferCopyOp");
        recorder.ShouldContain("RegisterSubmissionMarker");
    }

    [Test]
    public void VulkanBackend_UsesTransactionalGroupEnqueueAndFailsAbortedMarkers()
    {
        string adapter = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/PhysicsCompute/VulkanPhysicsChainComputeBackend.cs");
        string dispatcher = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs");
        string preparation = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanFrameLoop.PrimaryRecordingPreparation.cs");
        string queue = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/FrameOps/VulkanFrameOperationQueue.cs");
        string preflightPolicy = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanRenderer.FrameLoop.Preflight.Policy.cs");
        string markers = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.SubmissionMarkerFences.cs")
            + ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.TrackedSubmission.cs")
            + ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandSynchronizationState.cs");

        adapter.ShouldContain("TryBeginOrderedComputeBatch");
        adapter.ShouldContain("CommitOrderedComputeBatch");
        adapter.ShouldContain("RollbackOrderedComputeBatch");
        dispatcher.ShouldContain("backend.BeginBatch()");
        dispatcher.ShouldContain("backend.CommitBatch()");
        dispatcher.ShouldContain("backend.RollbackBatch()");
        // A recording attempt that does not submit fails the markers of both operation streams.
        Compact(preparation).ShouldContain(Compact(
            "FailPreparedSubmissionMarkers(staticOperations.AsSpan(0, staticOperationCount), dynamicUiOperations.AsSpan(0, dynamicUiOperationCount));"));
        Compact(preparation).ShouldContain(Compact(
            "FailUnsubmittedSubmissionMarkers(staticOperations); VulkanCommandSynchronizationState.FailUnsubmittedSubmissionMarkers(dynamicUiOperations);"));
        // A rolled-back ordered batch fails each submission marker that it captured.
        string rollback = SliceMethod(queue, "internal void RollbackOrderedBatch()", "internal bool RequiredOrderedBatchHasWriter(");
        rollback.ShouldContain("if (capture.Buffer[index] is SubmissionMarkerOp marker)");
        rollback.ShouldContain("marker.Fence.Fail(EGpuFenceFailureSite.QueueRollback,");
        Compact(preflightPolicy).ShouldContain(Compact("FailUnsubmittedSubmissionMarkers(droppedOps)"));
        markers.ShouldContain("marker.Fence.Fail(EGpuFenceFailureSite.UnsubmittedMarker,");
        markers.ShouldContain("RentTimelineGpuFence()");
        markers.ShouldContain("_timelineGpuFencePool");
        markers.ShouldContain("markers.Clear()");
    }

    [Test]
    public void VulkanCapabilities_FollowOperationalRendererState()
    {
        string adapter = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/PhysicsCompute/VulkanPhysicsChainComputeBackend.cs");
        string work = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Compute/VulkanOrderedComputeProducer.cs");

        adapter.ShouldContain("_renderer.SupportsOrderedComputeWork");
        adapter.ShouldContain("? SupportedCapabilities");
        adapter.ShouldContain(": default");
        work.ShouldContain("deviceContext.QueueFamilies.GraphicsFamilySupportsCompute");
    }

    [Test]
    public void SelectiveReadback_ReusesPerSlotAdaptersAfterWarmup()
    {
        string dispatcher = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs");
        string resources = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/PhysicsChainSelectiveReadbackSlotResources.cs");

        dispatcher.ShouldContain("resources.StagingSource");
        dispatcher.ShouldContain("resources.Fence");
        dispatcher.ShouldContain("source.Reset(backend, staging, plan.ByteCount)");
        // The slot resources own the renderer fence, so the adapter does not dispose it.
        dispatcher.ShouldContain("resources.CompletionFence = gpuFence;");
        dispatcher.ShouldContain("fence.Reset(gpuFence, ownsFence: false)");
        dispatcher.ShouldNotContain("new PhysicsChainMappedReadbackStagingSource");
        dispatcher.ShouldNotContain("new PhysicsChainGpuReadbackFence");
        resources.ShouldContain("StagingSource { get; } = new()");
        resources.ShouldContain("Fence { get; } = new()");
    }

    [Test]
    public void VulkanOrderedWork_ClassifiesOutsidePassSubmissionAsPreRender()
    {
        string work = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.OrderedComputeApi.cs");
        string frameLoop = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.FeatureOperations.cs");

        // Work outside a render-graph pass has no pass index and goes to PreRender.
        Compact(work).ShouldContain(Compact(
            "int passIndex = currentPassIndex == int.MinValue ? (int)EDefaultRenderPass.PreRender : currentPassIndex;"));
        frameLoop.ShouldContain("TryCompleteOrderedComputePass");
        frameLoop.ShouldContain("_commandRuntime.TryEnqueueOrderedComputeBarrier(");
        Compact(work).ShouldContain(Compact(
            "int passIndex = ResolveOrderedPrimaryWorkPassIndex(\"ComputeMemoryBarrier\", currentPassIndex, context.PassMetadata);"));
    }

    [Test]
    public void Readback_UsesTransferToHostBarrierAndDestinationUsage()
    {
        string barrier = ReadWorkspaceFile("XREngine.Runtime.Rendering/Commands/EMemoryBarrierMask.cs");
        string dispatcher = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs");
        string buffers = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs");

        barrier.ShouldContain("GpuReadback");
        dispatcher.ShouldContain("EMemoryBarrierMask.GpuReadback");
        buffers.ShouldContain("EBufferUsage.StreamRead or EBufferUsage.DynamicRead or EBufferUsage.StaticRead => BufferUsageFlags.TransferDstBit");
    }

    [Test]
    public void DeferredArenaRetirement_RearmsMarkersDroppedBeforeSubmission()
    {
        string dispatcher = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs");
        string deferredResource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/PhysicsCompute/DeferredPhysicsChainArenaResource.cs");

        deferredResource.ShouldContain("IPhysicsChainComputeBackend Backend");
        deferredResource.ShouldContain("int FailedFenceRetryCount = 0");
        dispatcher.ShouldContain("entry.RetirementFence.Dispose();");
        dispatcher.ShouldContain("XRGpuFence? retryFence = entry.Backend.InsertFence();");
        dispatcher.ShouldContain("RetirementFence = retryFence");
        dispatcher.ShouldContain("FailedFenceRetryCount = retryCount");
        dispatcher.ShouldContain("Re-enqueued a failed arena retirement marker after an unsubmitted frame.");
    }

    [Test]
    public void StandaloneMode_IsAnIsolatedDispatcherGroupWithoutBackendSpecificCode()
    {
        string component = ReadWorkspaceFile("XREngine.Runtime.Core/Scene/Components/Physics/PhysicsChainComponent.GPU.cs");
        string dispatcher = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs");

        // The component submits one snapshot. The dispatcher selects the batched or isolated group.
        component.ShouldContain("RuntimePhysicsChainRendering.Current.Execute(this, new PhysicsChainGpuDispatchSnapshot(");
        component.ShouldContain("loopCount, timeVar, _gpuExecutionGeneration");
        component.ShouldNotContain("OpenGLRenderer");
        component.ShouldNotContain("RawGL");
        component.ShouldNotContain("FenceSync");
        dispatcher.ShouldContain("component.UseBatchedDispatcher ? 0 : request.RequestId");
        dispatcher.ShouldContain("_currentDispatchGroupIsBatched = key.DispatchIsolationKey == 0");
        dispatcher.ShouldContain("entry.IsBatched");
        dispatcher.ShouldContain("RecordCpuReadbackBytes(bytes.Length, isBatched)");
    }

    private static string ReadWorkspaceFile(string relativePath)
        => SourceContractWorkspace.ReadFile(relativePath);

    private static string SliceMethod(string source, string startToken, string endToken)
    {
        int start = source.IndexOf(startToken, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);
        int end = source.IndexOf(endToken, start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start);
        return source[start..end];
    }

    /// <summary>Removes all white space, so that line wraps do not change a source match.</summary>
    private static string Compact(string source)
    {
        var builder = new System.Text.StringBuilder(source.Length);
        foreach (char character in source)
            if (!char.IsWhiteSpace(character))
                builder.Append(character);
        return builder.ToString();
    }
}
