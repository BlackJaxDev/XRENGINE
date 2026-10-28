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
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferState.cs");
        state.ShouldContain("RuntimeRenderingHostServices.Settings.EnableVulkanPrimaryCommandBufferReuse");
        state.ShouldContain("XREnvironment.GetBooleanOverride(");
        state.ShouldContain("XREngineEnvironmentVariables.VulkanPrimaryCommandBufferReuse");
        state.ShouldContain("(VulkanPrimaryCommandBufferReuseOverride ??\n             RuntimeRenderingHostServices.Settings.EnableVulkanPrimaryCommandBufferReuse)");
        state.ShouldNotContain("private static readonly bool VulkanPrimaryCommandBufferReuseEnabled");
    }

    [Test]
    public void CommandCache_TracksIndependentGenerationDomainsAndExactMissFields()
    {
        string state = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferState.cs");
        string generationDomains = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferGenerationDomains.cs");
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");

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
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string lowering = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "ComputeFrameOpFrameDataSignature",
            "FrameDataSignature");
        string frameOpContext = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/FrameOpContext.cs");
        string primaryRecording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/VulkanCommandRuntime.PrimaryRecording.cs");

        recording.ShouldContain("TryRefreshReusableCommandBufferFrameData");
        recording.ShouldContain("ComputeFrameOpFrameDataSignature");
        primaryRecording.ShouldContain("CommandBufferGenerationDomains generations = CaptureCommandBufferGenerationDomains(");
        primaryRecording.ShouldContain("owner.RecordedGenerations = generations;");
        frameOpContext.ShouldContain("ulong RecordingFingerprint");
        lowering.ShouldContain("ComputeFrameOpFrameDataSignature");
        lowering.ShouldContain("FrameDataSignature");
    }

    [Test]
    public void InlinePrimary_FrameDataChangeRecordsAgainWhileCommandChainsRemainReusable()
    {
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string primaryRecording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/VulkanCommandRuntime.PrimaryRecording.cs");

        // Only a command-chain primary is a reuse candidate. An inline primary records
        // again, so a camera pose change can never replay stale inline frame data.
        int chainsEnabled = primaryRecording.IndexOf("CommandChainsEnabledForCurrentRecording &&", StringComparison.Ordinal);
        chainsEnabled.ShouldBeGreaterThanOrEqualTo(0);
        int schedule = primaryRecording.IndexOf("input.CommandChainSchedule is not null &&", chainsEnabled, StringComparison.Ordinal);
        schedule.ShouldBeGreaterThan(chainsEnabled);
        primaryRecording.IndexOf("if (TryReuseCleanCommandChainPrimaryVariant(", schedule, StringComparison.Ordinal)
            .ShouldBeGreaterThan(schedule);
        primaryRecording.ShouldContain("!input.CommandChainSchedule.RequiresFreshPrimary &&");

        recording.ShouldContain("CameraPose: ResolveCameraPoseReplayGeneration(");
        recording.ShouldContain("ComputeCameraPoseGeneration");
        recording.ShouldContain("ResolveCameraPoseReplayGeneration");
        recording.ShouldContain("state.SettleInvalidationPending");
        recording.ShouldContain("Previous-camera matrices and temporal history converge");
    }

    [Test]
    public void VolatileSecondaryContentGeneration_DoesNotInvalidateItsCachedPrimary()
    {
        string lowering = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "internal static ulong ComputePrimaryCommandBufferGroupSignature(");
        int start = lowering.IndexOf(
            "ComputePrimaryCommandBufferGroupIdentity(\n            CommandChainSchedule schedule,",
            StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);
        int end = lowering.IndexOf("return new VulkanCommandIdentityComponents(", start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start);
        string method = lowering[start..end];

        method.ShouldContain("chain.RecordedArtifact.CreateReference()");
        method.ShouldContain("artifact.AddTo(ref nestedArtifacts);");
        method.ShouldNotContain("SecondaryCommandBufferGeneration");
    }

    [Test]
    public void Variants_ArePerTargetSlotAndBounded()
    {
        string allocation = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferAllocation.cs");
        string outputArtifacts = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.DesktopOutputArtifacts.cs");
        string frameData = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/FrameData/VulkanRenderer.CommandBufferReuse.FrameData.cs");
        string primaryRecording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/VulkanCommandRuntime.PrimaryRecording.cs");

        // Each swapchain target slot owns exactly one primary command artifact.
        outputArtifacts.ShouldContain("CommandBuffers.PrimaryOwners = new PrimaryCommandArtifactOwner[imageCount];");
        outputArtifacts.ShouldContain("CommandBuffers.PrimaryOwners[index] = new PrimaryCommandArtifactOwner(");
        frameData.ShouldContain("if (variant.FrameOpsSignature != frameOpsSignature ||");
        allocation.ShouldNotContain("reusableCommandChainPrimary");
        frameData.ShouldContain("variant.LastUsedFrameId = VulkanFrameCounter;");
        primaryRecording.ShouldContain("owner.LastUsedFrameId = input.FramePlan.RenderFrameId;");
        primaryRecording.ShouldContain("owner.RecordedGenerations = generations;");
    }

    [Test]
    public void QueryCadence_ReusesPrimaryWithExecutionOrderedResetEpoch()
    {
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string query = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Queries/VkRenderQuery.cs");

        recording.ShouldContain("PrepareQueryFrameOpsForCommandBufferReuse");
        recording.ShouldContain("ops.Stream.GetQuery(index).Query.PrepareForCommandBufferReuse(commandBuffer)");
        recording.ShouldNotContain("!VulkanPrimaryCommandBufferReuseEnabled || hasQueryFrameOps");
        recording.ShouldNotContain("primaryFrameStateDirtyReason = hasQueryFrameOps");
        query.ShouldContain("PrepareForCommandBufferReuse(CommandBuffer commandBuffer)");
        query.ShouldContain("Api!.CmdResetQueryPool(");
        query.ShouldNotContain("Api!.ResetQueryPool(Device");
        query.ShouldNotContain("ResetQueryPoolForCommandBufferReuse");
    }

    private static string ReadWorkspaceFile(string relativePath)
        => SourceContractWorkspace.ReadFile(relativePath);

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
