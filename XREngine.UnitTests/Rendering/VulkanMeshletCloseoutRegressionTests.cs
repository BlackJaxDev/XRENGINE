using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanMeshletCloseoutRegressionTests
{
    [Test]
    public void DensePrimaryRecorder_ConsumesPlannedNonGraphicsSecondaryRanges()
    {
        string source = SourceContractWorkspace.ReadExactFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Operations.cs");
        string dispatch = Slice(
            source,
            "private int RecordTypedPrimaryOperation(",
            "private bool TryRecordPlannedNonGraphicsSecondaryRange(");

        int secondaryRange = dispatch.IndexOf("TryRecordPlannedNonGraphicsSecondaryRange(", StringComparison.Ordinal);
        int inlineSwitch = dispatch.IndexOf("return header.OpCode switch", StringComparison.Ordinal);
        secondaryRange.ShouldBeGreaterThanOrEqualTo(0);
        secondaryRange.ShouldBeLessThan(inlineSwitch);
        dispatch.ShouldContain("return lastSecondaryOperationIndex;");

        string bridge = Slice(
            source,
            "private bool TryRecordPlannedNonGraphicsSecondaryRange(",
            "private int RecordTextureUploadPayload(");
        bridge.ShouldContain("EVulkanPrimaryPlanNodeKind.ComputeDispatch or");
        bridge.ShouldContain("EVulkanPrimaryPlanNodeKind.ComputeDispatchIndirect or");
        bridge.ShouldContain("EVulkanPrimaryPlanNodeKind.BufferCopy or");
        bridge.ShouldContain("EVulkanPrimaryPlanNodeKind.MemoryBarrier or");
        bridge.ShouldContain("EVulkanPrimaryPlanNodeKind.Query");
        bridge.ShouldContain("TryRecordSecondaryBucket(");
        bridge.ShouldContain("lastOperationIndex = info.OperationIndex + bucket.Count - 1;");
    }

    [Test]
    public void PreparedWorkerRecording_CapturesDescriptorRequirementsBeforeEndingAndAbandonsFailures()
    {
        string source = SourceContractWorkspace.ReadExactFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.PreparedWorkerRecording.cs");
        int capture = source.IndexOf("CaptureSecondaryDescriptorSetImageRequirements(", StringComparison.Ordinal);
        int end = source.IndexOf("encoder.End(secondary)", StringComparison.Ordinal);
        int catchBlock = source.IndexOf("catch", end, StringComparison.Ordinal);
        int abandon = source.IndexOf("encoder.Abandon(secondary)", catchBlock, StringComparison.Ordinal);

        capture.ShouldBeGreaterThanOrEqualTo(0);
        capture.ShouldBeLessThan(end);
        catchBlock.ShouldBeGreaterThan(end);
        abandon.ShouldBeGreaterThan(catchBlock);
    }

    [Test]
    public void Shutdown_RetiresWorkerArtifactsBeforePoolsAndUsesDescriptorLifetimeAuthority()
    {
        string desktop = SourceContractWorkspace.ReadExactFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.DesktopOutputArtifacts.cs");
        desktop.IndexOf("DestroyIndexedCommandChainCaches();", StringComparison.Ordinal)
            .ShouldBeLessThan(desktop.IndexOf("RetireArtifacts(CommandBuffers.Buffers", StringComparison.Ordinal));

        string pools = SourceContractWorkspace.ReadExactFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Allocation/VulkanRenderer.CommandPool.cs");
        int cancelWorkers = pools.IndexOf("CancelCommandChainRecordingWorkers();", StringComparison.Ordinal);
        int destroyCaches = pools.IndexOf("DestroyCommandChainCaches();", cancelWorkers, StringComparison.Ordinal);
        int destroyWorkers = pools.IndexOf("DestroyCommandChainRecordingWorkers();", destroyCaches, StringComparison.Ordinal);
        int retirePools = pools.IndexOf("HashSet<ulong> destroyed", destroyWorkers, StringComparison.Ordinal);
        cancelWorkers.ShouldBeGreaterThanOrEqualTo(0);
        cancelWorkers.ShouldBeLessThan(destroyCaches);
        destroyCaches.ShouldBeLessThan(destroyWorkers);
        destroyWorkers.ShouldBeLessThan(retirePools);

        string imgui = SourceContractWorkspace.ReadExactFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/UI/VulkanImGuiFontAtlasResources.cs");
        imgui.ShouldContain("DescriptorLifetime.RetireDescriptorPool(");
        imgui.ShouldContain("DestroyDescriptorSetLayout(");
        imgui.ShouldNotContain("DestroyDescriptorPool(target.Device");
        imgui.ShouldNotContain("DestroyDescriptorSetLayout(target.Device");
    }

    [Test]
    public void ForcedRerecord_BypassesCleanPrimaryReuse()
    {
        string source = SourceContractWorkspace.ReadExactFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/VulkanCommandRuntime.PrimaryRecording.cs");
        string reuseGate = Slice(
            source,
            "if (owner is not null &&",
            "TryPreparePrimaryReuseFrameDataCohort(");

        reuseGate.ShouldContain("VulkanPrimaryCommandBufferReuseEnabled");
        reuseGate.ShouldContain("!CommandChainBenchmarkForceRerecord");
        reuseGate.ShouldContain("!input.CommandChainSchedule.RequiresFreshPrimary");
        reuseGate.ShouldContain("!input.Policy.FreshSerialRecording");
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start);
        return source[start..end];
    }
}
