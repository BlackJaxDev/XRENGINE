using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class VulkanPhysicsChainParityContractTests
{
    private const string VulkanAdapterPath = "XREngine.Runtime.Rendering.Vulkan/Rendering/PhysicsCompute/VulkanPhysicsChainComputeBackend.cs";
    private const string VulkanRendererPath = "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.cs";
    private const string OrderedComputeApiPath = "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.OrderedComputeApi.cs";
    private const string DispatcherPath = "XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.cs";

    [Test]
    public void Dispatcher_ResolvesBackendAdapterThroughStableCapability()
    {
        string factory = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/PhysicsChainComputeBackendFactory.cs");
        string dispatcher = ReadWorkspaceFile(DispatcherPath);

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
        string adapter = ReadWorkspaceFile(VulkanAdapterPath);
        string renderer = ReadWorkspaceFile(VulkanRendererPath);
        string work = ReadWorkspaceFile(OrderedComputeApiPath)
            + ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.FrameOperationApi.cs");
        string recorder = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.NativeRecordingServices.cs");

        adapter.ShouldContain("_renderer.TryDispatchComputeIndirect(");
        adapter.ShouldContain("_renderer.TryEnqueuePhysicsChainBufferCopy(");
        adapter.ShouldContain("_renderer.TryCompleteOrderedComputePass(");
        adapter.ShouldContain("_renderer.InsertGpuFence()");
        // The physics-chain copy goes into the ordered frame-loop queue with its authoring lease.
        ShouldContainCode(renderer,
            "return _frameLoop.TryEnqueueBufferCopy(source, sourceOffset, destination, destinationOffset, byteCount, label, authoringLease);");
        work.ShouldContain("ComputeDispatchIndirectOp");
        work.ShouldContain("BufferCopyOp");
        ShouldContainCode(work, "queue.EnqueuePrepared(VulkanFrameOperationSemantics.Prepare(operation, passIndex));");
        ShouldContainCode(work,
            "queue.EnqueuePrepared(VulkanFrameOperationSemantics.Prepare(CreateMemoryBarrierOperation(passIndex, mask, context), passIndex));");
        ShouldContainCode(work, "=> MemoryBarrierOp.Rent(passIndex, mask, context);");
        recorder.ShouldContain("RecordComputeDispatchIndirectOp");
        recorder.ShouldContain("RecordBufferCopyOp");
        recorder.ShouldContain("RegisterSubmissionMarker");
    }

    [Test]
    public void VulkanBackend_UsesTransactionalGroupEnqueueAndFailsAbortedMarkers()
    {
        string adapter = ReadWorkspaceFile(VulkanAdapterPath);
        string dispatcher = ReadWorkspaceFile(DispatcherPath);
        string recorder = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.NativeRecordingServices.cs");
        string commandBufferReuse = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/FrameData/VulkanRenderer.CommandBufferReuse.FrameData.cs");
        string primaryRecording = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/VulkanCommandRuntime.PrimaryRecording.cs");
        string recordingPreparation = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanFrameLoop.PrimaryRecordingPreparation.cs");
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

        // An aborted primary recording fails the static and dynamic UI markers.
        ShouldContainCode(recordingPreparation, "if (!submissionMarkersTransferred)");
        ShouldContainCode(recordingPreparation, "acceptedPlan.SettleUnsubmittedSubmissionMarkers();");
        ShouldContainCode(recordingPreparation,
            "FailPreparedSubmissionMarkers(staticOperations.AsSpan(0, staticOperationCount), dynamicUiOperations.AsSpan(0, dynamicUiOperationCount));");
        ShouldContainCode(recordingPreparation, "VulkanCommandSynchronizationState.FailUnsubmittedSubmissionMarkers(staticOperations);");
        ShouldContainCode(recordingPreparation, "VulkanCommandSynchronizationState.FailUnsubmittedSubmissionMarkers(dynamicUiOperations);");
        ShouldContainCode(primaryRecording, "FailSubmissionMarkersForCommandBuffer(input.PrimaryCommandBuffer);");
        ShouldContainCode(preflightPolicy, "VulkanCommandSynchronizationState.FailUnsubmittedSubmissionMarkers(droppedOps);");

        // A reused command buffer fails its old markers and registers the markers of the new frame.
        ShouldContainCode(commandBufferReuse,
            "_commandRuntime.PrepareSubmissionMarkersForCommandBufferReuse(commandBuffer, ops, dynamicUiBatchTextOps);");
        ShouldContainCode(recorder, "existing[markerIndex].Fail(EGpuFenceFailureSite.CommandBufferReuse,");
        ShouldContainCode(recorder, "RegisterSubmissionMarkersNoLock(commandBuffer, frameOps);");
        ShouldContainCode(recorder, "RegisterSubmissionMarkersNoLock(commandBuffer, dynamicUiFrameOps);");

        // A rolled-back ordered batch fails each captured submission marker.
        string queue = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/FrameOps/VulkanFrameOperationQueue.cs");
        string rollback = SliceMethod(queue, "internal void RollbackOrderedBatch()", "internal bool RequiredOrderedBatchHasWriter(");
        rollback.ShouldContain("if (capture.Buffer[index] is SubmissionMarkerOp marker)");
        rollback.ShouldContain("marker.Fence.Fail(EGpuFenceFailureSite.QueueRollback,");
        markers.ShouldContain("marker.Fence.Fail(EGpuFenceFailureSite.UnsubmittedMarker,");

        markers.ShouldContain("RentTimelineGpuFence()");
        markers.ShouldContain("_timelineGpuFencePool");
        markers.ShouldContain("markers.Clear()");

        // A marker that does not reach submission becomes failed, so its producer does not wait for it.
        using VulkanTimelineGpuFence marker = new();
        FrameOp[] abortedOperations =
        [
            new SubmissionMarkerOp((int)EDefaultRenderPass.PreRender, marker, "PhysicsChain.AbortedMarker", default),
        ];
        marker.SubmissionStatus.ShouldBe(EGpuFenceSubmissionStatus.AwaitingSubmission);
        VulkanCommandSynchronizationState.FailUnsubmittedSubmissionMarkers(abortedOperations);
        marker.SubmissionStatus.ShouldBe(EGpuFenceSubmissionStatus.Failed);
        marker.Poll().ShouldBe(EGpuFenceStatus.Failed);
    }

    [Test]
    public void VulkanCapabilities_FollowOperationalRendererState()
    {
        string adapter = ReadWorkspaceFile(VulkanAdapterPath);
        string renderer = ReadWorkspaceFile(VulkanRendererPath);
        string runtime = ReadWorkspaceFile(OrderedComputeApiPath);
        string work = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Compute/VulkanOrderedComputeProducer.cs");

        adapter.ShouldContain("_renderer.SupportsOrderedComputeWork");
        adapter.ShouldContain("? SupportedCapabilities");
        adapter.ShouldContain(": default");
        ShouldContainCode(renderer, "public bool SupportsOrderedComputeWork => _commandRuntime.SupportsOrderedComputeWork;");
        ShouldContainCode(runtime, "internal bool SupportsOrderedComputeWork => VulkanOrderedComputeProducer.Supports(DeviceContext, this);");
        work.ShouldContain("deviceContext.IsOperational");
        work.ShouldContain("deviceContext.GraphicsQueue.Handle != 0");
        work.ShouldContain("commandRuntime.Synchronization._graphicsTimelineSemaphore.Handle != 0");
        work.ShouldContain("deviceContext.QueueFamilies.GraphicsFamilySupportsCompute");
    }

    [Test]
    public void SelectiveReadback_ReusesPerSlotAdaptersAfterWarmup()
    {
        string dispatcher = ReadWorkspaceFile(DispatcherPath);
        string resources = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/PhysicsChainSelectiveReadbackSlotResources.cs");

        dispatcher.ShouldContain("resources.StagingSource");
        dispatcher.ShouldContain("resources.Fence");
        dispatcher.ShouldContain("source.Reset(backend, staging, plan.ByteCount)");
        // The slot owns the GPU fence. The reused fence adapter only borrows it.
        dispatcher.ShouldContain("resources.CompletionFence = gpuFence;");
        dispatcher.ShouldContain("fence.Reset(gpuFence, ownsFence: false)");
        dispatcher.ShouldNotContain("new PhysicsChainMappedReadbackStagingSource");
        dispatcher.ShouldNotContain("new PhysicsChainGpuReadbackFence");
        resources.ShouldContain("StagingSource { get; } = new()");
        resources.ShouldContain("Fence { get; } = new()");
        resources.ShouldContain("internal XRGpuFence? CompletionFence;");
        resources.ShouldContain("CompletionFence?.Dispose();");
    }

    [Test]
    public void VulkanOrderedWork_ClassifiesOutsidePassSubmissionAsPreRender()
    {
        const int preRenderPass = (int)EDefaultRenderPass.PreRender;
        const int opaqueForwardPass = (int)EDefaultRenderPass.OpaqueForward;
        RenderPassMetadataCollection metadataBuilder = new();
        metadataBuilder.ForPass(preRenderPass, nameof(EDefaultRenderPass.PreRender), ERenderGraphPassStage.Graphics);
        metadataBuilder.ForPass(opaqueForwardPass, nameof(EDefaultRenderPass.OpaqueForward), ERenderGraphPassStage.Graphics);
        IReadOnlyCollection<RenderPassMetadata> metadata = metadataBuilder.Build();

        // Work outside a render-graph pass has no current pass index. It goes to PreRender.
        VulkanCommandRuntime.ResolveOrderedPrimaryWorkPassIndex("ComputeDispatchIndirect", int.MinValue, null)
            .ShouldBe(preRenderPass);
        VulkanCommandRuntime.ResolveOrderedPrimaryWorkPassIndex("ComputeSubmissionMarker", int.MinValue, metadata)
            .ShouldBe(preRenderPass);
        // Work inside a pass stays in that pass.
        VulkanCommandRuntime.ResolveOrderedPrimaryWorkPassIndex("ComputeBufferCopy", opaqueForwardPass, metadata)
            .ShouldBe(opaqueForwardPass);

        string work = ReadWorkspaceFile(OrderedComputeApiPath);
        string frameLoop = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/Authority/VulkanFrameLoop.FeatureOperations.cs");

        // Each ordered physics-chain operation gets its pass from the same resolver.
        foreach (string operationName in new[] { "ComputeDispatchIndirect", "ComputeBufferCopy", "ComputeMemoryBarrier", "ComputeSubmissionMarker" })
        {
            ShouldContainCode(work,
                $"ResolveOrderedPrimaryWorkPassIndex(\"{operationName}\", currentPassIndex, context.PassMetadata);");
        }

        ShouldContainCode(frameLoop,
            "internal ERendererComputeEnqueueStatus TryCompleteOrderedComputePass(EMemoryBarrierMask mask, string label) => _commandRuntime.TryEnqueueOrderedComputeBarrier(_frameOperationQueue, mask, label, RuntimeEngine.Rendering.State.CurrentRenderGraphPassIndex,");
    }

    [Test]
    public void Readback_UsesTransferToHostBarrierAndDestinationUsage()
    {
        string barrier = ReadWorkspaceFile("XREngine.Runtime.Rendering/Commands/EMemoryBarrierMask.cs");
        string dispatcher = ReadWorkspaceFile(DispatcherPath);
        string buffers = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs");

        barrier.ShouldContain("GpuReadback");
        dispatcher.ShouldContain("EMemoryBarrierMask.GpuReadback");
        buffers.ShouldContain("EBufferUsage.StreamRead or EBufferUsage.DynamicRead or EBufferUsage.StaticRead => BufferUsageFlags.TransferDstBit");
    }

    [Test]
    public void DeferredArenaRetirement_RearmsMarkersDroppedBeforeSubmission()
    {
        string dispatcher = ReadWorkspaceFile(DispatcherPath);
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
        string bridge = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/PhysicsCompute/RuntimePhysicsChainRenderingBridge.cs");
        string dispatcher = ReadWorkspaceFile(DispatcherPath);

        // Batched and standalone chains use the same renderer-neutral submission.
        component.ShouldContain("RuntimePhysicsChainRendering.Current.Execute(this, new PhysicsChainGpuDispatchSnapshot(");
        component.ShouldContain("loopCount, timeVar,");
        component.ShouldNotContain("OpenGLRenderer");
        component.ShouldNotContain("RawGL");
        component.ShouldNotContain("FenceSync");
        bridge.ShouldContain("GPUPhysicsChainDispatcher.Instance.SubmitData(source, snapshot,");
        bridge.ShouldNotContain("OpenGLRenderer");
        bridge.ShouldNotContain("RawGL");
        bridge.ShouldNotContain("FenceSync");
        dispatcher.ShouldContain("component.UseBatchedDispatcher ? 0 : request.RequestId");
        dispatcher.ShouldContain("_currentDispatchGroupIsBatched = key.DispatchIsolationKey == 0");
        dispatcher.ShouldContain("entry.IsBatched");
        dispatcher.ShouldContain("RecordCpuReadbackBytes(bytes.Length, isBatched)");
    }

    private static string ReadWorkspaceFile(string relativePath)
        => SourceContractWorkspace.ReadFile(relativePath);

    /// <summary>
    /// Checks that the source contains the code. The check ignores white space,
    /// so a changed line wrap does not break the contract.
    /// </summary>
    private static void ShouldContainCode(string source, string code)
        => RemoveWhiteSpace(source).ShouldContain(RemoveWhiteSpace(code));

    private static string SliceMethod(string source, string startToken, string endToken)
    {
        int start = source.IndexOf(startToken, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);
        int end = source.IndexOf(endToken, start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start);
        return source[start..end];
    }

    private static string RemoveWhiteSpace(string text)
        => new(text.Where(static character => !char.IsWhiteSpace(character)).ToArray());
}
