using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanMeshOperationRequestQueueLaneTests
{
    [Test]
    public void ResolveLane_CountsSceneGeometryContextsInTheMainSceneLane()
    {
        // The enums are internal, so a public test method cannot take them as
        // parameters. The test reads the expected lanes from this table instead.
        (EVulkanFrameOpContextKind Kind, EVulkanMeshRequestLane Lane)[] expectations =
        [
            (EVulkanFrameOpContextKind.MainViewport, EVulkanMeshRequestLane.MainScene),
            (EVulkanFrameOpContextKind.OpenXrEye, EVulkanMeshRequestLane.MainScene),
            (EVulkanFrameOpContextKind.SceneCapture, EVulkanMeshRequestLane.MainScene),
            (EVulkanFrameOpContextKind.LightProbeCapture, EVulkanMeshRequestLane.MainScene),
            (EVulkanFrameOpContextKind.DiagnosticCapture, EVulkanMeshRequestLane.MainScene),
            (EVulkanFrameOpContextKind.Shadow, EVulkanMeshRequestLane.Shadow),
            (EVulkanFrameOpContextKind.UiPreview, EVulkanMeshRequestLane.Ui),
            (EVulkanFrameOpContextKind.OpenXrMirror, EVulkanMeshRequestLane.TerminalComposition),
            (EVulkanFrameOpContextKind.Unknown, EVulkanMeshRequestLane.TerminalComposition),
        ];

        foreach ((EVulkanFrameOpContextKind kind, EVulkanMeshRequestLane lane) in expectations)
        {
            VulkanMeshRenderRequest request = CreateRequest(kind);
            VulkanMeshOperationRequestQueue.ResolveLane(in request).ShouldBe(lane, kind.ToString());
        }
    }

    [Test]
    public void SceneCaptureCohortLargerThanTerminalLane_IsAcceptedWithoutCapacityFailure()
    {
        // A mirror capture of New Sponza submits about 484 mesh requests, which is
        // more than the terminal lane holds.
        const int requestCount = VulkanMeshOperationRequestQueue.TerminalCompositionCapacity * 2;
        VulkanMeshOperationRequestQueue queue = new();
        VulkanMeshRenderRequest request = CreateRequest(EVulkanFrameOpContextKind.SceneCapture);

        for (int index = 0; index < requestCount; index++)
            queue.TryEnqueue(in request).ShouldBe(
                VulkanMeshOperationRequestQueue.EMeshRequestScheduleResult.Scheduled);

        VulkanMeshRenderRequest[] destination =
            new VulkanMeshRenderRequest[VulkanMeshOperationRequestQueue.Capacity];
        int drained = queue.DrainTo(
            destination,
            foregroundRequired: true,
            out int acceptedRequestCount,
            out int capacityExceededCount,
            out VulkanMeshRequestLaneCapacityFailure capacityFailure);

        drained.ShouldBe(requestCount);
        acceptedRequestCount.ShouldBe(requestCount);
        capacityExceededCount.ShouldBe(0);
        capacityFailure.HasFailure.ShouldBeFalse();
    }

    private static VulkanMeshRenderRequest CreateRequest(EVulkanFrameOpContextKind contextKind)
        => default(VulkanMeshRenderRequest) with
        {
            Context = default(FrameOpContext) with { ContextKind = contextKind },
        };
}
