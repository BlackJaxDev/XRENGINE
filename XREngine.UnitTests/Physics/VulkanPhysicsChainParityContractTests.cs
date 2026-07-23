using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class VulkanPhysicsChainParityContractTests
{
    [Test]
    public void Dispatcher_ResolvesOpenGlAndVulkanAdaptersThroughFactory()
    {
        string factory = ReadWorkspaceFile("XRENGINE/Rendering/Compute/PhysicsChainComputeBackendFactory.cs");
        string dispatcher = ReadWorkspaceFile("XRENGINE/Rendering/Compute/GPUPhysicsChainDispatcher.cs");

        factory.ShouldContain("OpenGLPhysicsChainComputeBackend.TryCreate");
        factory.ShouldContain("VulkanPhysicsChainComputeBackend.TryCreate");
        dispatcher.ShouldContain("PhysicsChainComputeBackendFactory.TryCreate");
        dispatcher.ShouldContain("Capabilities.SupportsCorePipeline");
    }

    [Test]
    public void VulkanBackend_UsesOrderedRendererOperations()
    {
        string adapter = ReadWorkspaceFile("XRENGINE/Rendering/Compute/VulkanPhysicsChainComputeBackend.cs");
        string work = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ComputeWork.cs");
        string recorder = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");

        adapter.ShouldContain("TryDispatchComputeIndirect");
        adapter.ShouldContain("TryEnqueueBufferCopy");
        adapter.ShouldContain("TryCompleteOrderedComputePass");
        adapter.ShouldContain("InsertGpuFence");
        work.ShouldContain("ComputeDispatchIndirectOp");
        work.ShouldContain("BufferCopyOp");
        work.ShouldContain("EnqueueFrameOp(new MemoryBarrierOp(passIndex, mask, context))");
        recorder.ShouldContain("RecordComputeDispatchIndirectOp");
        recorder.ShouldContain("RecordBufferCopyOp");
        recorder.ShouldContain("RegisterSubmissionMarker");
    }

    [Test]
    public void VulkanBackend_UsesTransactionalGroupEnqueueAndFailsAbortedMarkers()
    {
        string adapter = ReadWorkspaceFile("XRENGINE/Rendering/Compute/VulkanPhysicsChainComputeBackend.cs");
        string dispatcher = ReadWorkspaceFile("XRENGINE/Rendering/Compute/GPUPhysicsChainDispatcher.cs");
        string recorder = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string diagnostics = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.FrameOpDiagnostics.cs");
        string frameLoop = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.cs");
        string markers = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.SubmissionMarkers.cs");

        adapter.ShouldContain("TryBeginOrderedComputeBatch");
        adapter.ShouldContain("CommitOrderedComputeBatch");
        adapter.ShouldContain("RollbackOrderedComputeBatch");
        dispatcher.ShouldContain("backend.BeginBatch()");
        dispatcher.ShouldContain("backend.CommitBatch()");
        dispatcher.ShouldContain("backend.RollbackBatch()");
        recorder.ShouldContain("FailUnsubmittedSubmissionMarkers(ops, dynamicUiBatchTextOps)");
        diagnostics.ShouldContain("or SubmissionMarkerOp");
        frameLoop.ShouldContain("FailUnsubmittedSubmissionMarkers(droppedOps)");
        markers.ShouldContain("marker.Fence.Fail()");
        markers.ShouldContain("RentTimelineGpuFence()");
        markers.ShouldContain("_timelineGpuFencePool");
        markers.ShouldContain("markers.Clear()");
    }

    [Test]
    public void VulkanCapabilities_FollowOperationalRendererState()
    {
        string adapter = ReadWorkspaceFile("XRENGINE/Rendering/Compute/VulkanPhysicsChainComputeBackend.cs");
        string work = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ComputeWork.cs");

        adapter.ShouldContain("_renderer.SupportsOrderedComputeWork");
        adapter.ShouldContain("? SupportedCapabilities");
        adapter.ShouldContain(": default");
        work.ShouldContain("FamilyQueueIndices.GraphicsFamilySupportsCompute");
    }

    [Test]
    public void SelectiveReadback_ReusesPerSlotAdaptersAfterWarmup()
    {
        string dispatcher = ReadWorkspaceFile("XRENGINE/Rendering/Compute/GPUPhysicsChainDispatcher.cs");
        string resources = ReadWorkspaceFile("XRENGINE/Rendering/Compute/PhysicsChainSelectiveReadbackSlotResources.cs");

        dispatcher.ShouldContain("resources.StagingSource");
        dispatcher.ShouldContain("resources.Fence");
        dispatcher.ShouldContain("source.Reset(backend, staging, plan.ByteCount)");
        dispatcher.ShouldContain("fence.Reset(gpuFence)");
        dispatcher.ShouldNotContain("new PhysicsChainMappedReadbackStagingSource");
        dispatcher.ShouldNotContain("new PhysicsChainGpuReadbackFence");
        resources.ShouldContain("StagingSource { get; } = new()");
        resources.ShouldContain("Fence { get; } = new()");
    }

    [Test]
    public void VulkanOrderedWork_ClassifiesOutsidePassSubmissionAsPreRender()
    {
        string work = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ComputeWork.cs");
        string initialization = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Initialization.cs");

        work.ShouldContain("passIndex = (int)EDefaultRenderPass.PreRender;");
        work.ShouldContain("TryCompleteOrderedComputePass");
        initialization.ShouldContain("ResolveOrderedPrimaryWorkPassIndex(opName, context.PassMetadata)");
    }

    [Test]
    public void Readback_UsesTransferToHostBarrierAndDestinationUsage()
    {
        string barrier = ReadWorkspaceFile("XREngine.Runtime.Rendering/Commands/EMemoryBarrierMask.cs");
        string dispatcher = ReadWorkspaceFile("XRENGINE/Rendering/Compute/GPUPhysicsChainDispatcher.cs");
        string buffers = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs");

        barrier.ShouldContain("GpuReadback");
        dispatcher.ShouldContain("EMemoryBarrierMask.GpuReadback");
        buffers.ShouldContain("EBufferUsage.StreamRead or EBufferUsage.DynamicRead or EBufferUsage.StaticRead => BufferUsageFlags.TransferDstBit");
    }

    [Test]
    public void DeferredArenaRetirement_RearmsMarkersDroppedBeforeSubmission()
    {
        string dispatcher = ReadWorkspaceFile("XRENGINE/Rendering/Compute/GPUPhysicsChainDispatcher.cs");
        string deferredResource = ReadWorkspaceFile(
            "XRENGINE/Rendering/Compute/DeferredPhysicsChainArenaResource.cs");

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
        string component = ReadWorkspaceFile("XRENGINE/Scene/Components/Physics/PhysicsChainComponent.GPU.cs");
        string dispatcher = ReadWorkspaceFile("XRENGINE/Rendering/Compute/GPUPhysicsChainDispatcher.cs");

        component.ShouldContain("SubmitToBatchedDispatcher(loop, timeVar)");
        component.ShouldNotContain("OpenGLRenderer");
        component.ShouldNotContain("RawGL");
        component.ShouldNotContain("FenceSync");
        dispatcher.ShouldContain("component.UseBatchedDispatcher ? 0 : request.RequestId");
        dispatcher.ShouldContain("_currentDispatchGroupIsBatched = key.DispatchIsolationKey == 0");
        dispatcher.ShouldContain("entry.IsBatched");
        dispatcher.ShouldContain("RecordCpuReadbackBytes(bytes.Length, isBatched)");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string root = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(root) && !File.Exists(Path.Combine(root, "XRENGINE.slnx")))
            root = Directory.GetParent(root)?.FullName ?? string.Empty;

        File.Exists(Path.Combine(root, "XRENGINE.slnx")).ShouldBeTrue();
        return File.ReadAllText(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }
}
