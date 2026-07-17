using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanCoreHardeningPhase522Tests
{
    [Test]
    public void PrimaryReuse_IsANormalDefaultOnSettingWithAnEnvironmentDiagnosticOverride()
    {
        VulkanCommandRecordingSettings settings = new();
        settings.PrimaryCommandBufferReuseEnabled.ShouldBeTrue();

        string state = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferState.cs");
        state.ShouldContain("RuntimeRenderingHostServices.Current.EnableVulkanPrimaryCommandBufferReuse");
        state.ShouldContain("ReadOptionalBooleanEnvironmentOverride");
        state.ShouldContain("XREngineEnvironmentVariables.VulkanPrimaryCommandBufferReuse");
        state.ShouldNotContain("private static readonly bool VulkanPrimaryCommandBufferReuseEnabled");
    }

    [Test]
    public void CommandCache_TracksIndependentGenerationDomainsAndExactMissFields()
    {
        string state = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferState.cs");
        string generationDomains = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferGenerationDomains.cs");
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");

        generationDomains.ShouldContain("CommandBufferGenerationDomains(");
        foreach (string domain in new[]
                 {
                     "Structural", "FrameData", "CameraPose", "TargetSlot", "Descriptor",
                     "ResourceAllocation", "Query", "Overlay", "Profiler"
                 })
        {
            generationDomains.ShouldContain($"ulong {domain}");
        }

        recording.ShouldContain("DescribePrimaryReuseMiss(");
        recording.ShouldContain("structural-generation old=");
        recording.ShouldContain("frame-data-generation old=");
        recording.ShouldContain("descriptor-generation old=");
        recording.ShouldContain("resource-allocation-generation old=");
        recording.ShouldContain("overlay-generation old=");
        recording.ShouldContain("profiler-generation old=");
    }

    [Test]
    public void FrameDataRefresh_IsSeparateFromStructuralCommandRecording()
    {
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string lowering = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainLowering.cs");
        string plannerState = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs");

        recording.ShouldContain("TryRefreshReusableCommandBufferFrameData");
        recording.ShouldContain("ComputeFrameOpFrameDataSignature");
        recording.ShouldContain("variant.RecordedGenerations = currentGenerations");
        plannerState.ShouldContain("ulong RecordingFingerprint");
        lowering.ShouldContain("ComputeFrameOpFrameDataSignature");
        lowering.ShouldContain("FrameDataSignature");
    }

    [Test]
    public void InlinePrimary_FrameDataChangeRecordsAgainWhileCommandChainsRemainReusable()
    {
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");

        recording.ShouldContain("!usingCommandChains &&");
        recording.ShouldContain("variant.RecordedGenerations.CameraPose != currentGenerations.CameraPose");
        recording.ShouldContain("inline primary camera pose changed");
        recording.ShouldContain("ComputeCameraPoseGeneration");
        recording.ShouldContain("ResolveCameraPoseReplayGeneration");
        recording.ShouldContain("state.SettleInvalidationPending");
        recording.ShouldContain("Previous-camera matrices and temporal history converge");
    }

    [Test]
    public void VolatileSecondaryContentGeneration_DoesNotInvalidateItsCachedPrimary()
    {
        string lowering = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainLowering.cs");
        int start = lowering.IndexOf(
            "internal static ulong ComputePrimaryCommandBufferGroupSignature(\n        CommandChainSchedule schedule,",
            StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);
        int end = lowering.IndexOf("return hash.ToHash();", start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start);
        string method = lowering[start..end];

        method.ShouldContain("chain.SecondaryCommandBuffer.Handle");
        method.ShouldNotContain("SecondaryCommandBufferGeneration");
    }

    [Test]
    public void Variants_ArePerTargetSlotAndBounded()
    {
        string allocation = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferAllocation.cs");
        string state = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferState.cs");

        state.ShouldContain("PrimaryCommandBufferVariantCapacity = 64");
        allocation.ShouldContain("variants.Count < PrimaryCommandBufferVariantCapacity");
        allocation.ShouldContain("LastUsedFrameId");
        allocation.ShouldContain("RecordedGenerations = default");
    }

    [Test]
    public void QueryCadence_ReusesPrimaryWithExecutionOrderedResetEpoch()
    {
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string query = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Queries/VkRenderQuery.cs");

        recording.ShouldContain("PrepareQueryFrameOpsForCommandBufferReuse");
        recording.ShouldContain("queryOp.Query.PrepareForCommandBufferReuse(queryOp.QueryTarget)");
        recording.ShouldNotContain("!VulkanPrimaryCommandBufferReuseEnabled || hasQueryFrameOps");
        recording.ShouldNotContain("primaryFrameStateDirtyReason = hasQueryFrameOps");
        query.ShouldContain("PrepareForCommandBufferReuse(EQueryTarget target)");
        query.ShouldContain("Api!.CmdResetQueryPool(commandBuffer, _queryPool, 0, _queryPoolCapacity);");
        query.ShouldNotContain("Api!.ResetQueryPool(Device");
        query.ShouldNotContain("ResetQueryPoolForCommandBufferReuse");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string path = Path.Combine(
            ResolveRepoRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Expected workspace file '{relativePath}'.");
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

    private static string ResolveRepoRoot()
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "XRENGINE.slnx")))
                return directory;
            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test directory.");
    }
}
