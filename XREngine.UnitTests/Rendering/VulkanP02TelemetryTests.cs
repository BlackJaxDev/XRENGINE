using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanP02TelemetryTests
{
    [Test]
    public void SceneRecordingTiming_IsCapturedBeforeOverlayTimestampReuse()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.cs");

        string snapshotBlock = Slice(source,
            "Vulkan.FrameLifecycle.SnapshotImGuiOverlay",
            "bool preserveSwapchainForImGuiOverlay");
        snapshotBlock.ShouldContain("snapshotImGuiOverlayTime +=");
        snapshotBlock.ShouldNotContain("recordCommandBufferTime +=");

        string sceneBlock = Slice(source,
            "Vulkan.FrameLifecycle.RecordCommandBuffer",
            "bool scenePrimaryRecordedThisFrame");
        sceneBlock.ShouldContain("TimeSpan sceneRecordElapsed = Stopwatch.GetElapsedTime(stageStartTimestamp);");
        sceneBlock.ShouldContain("recordSceneCommandBufferTime += sceneRecordElapsed;");
        sceneBlock.ShouldContain("recordCommandBufferTime += sceneRecordElapsed;");
    }

    [Test]
    public void RecordingStages_ExposeTimingAllocationAndHighWaterTelemetry()
    {
        string stageSource = ReadWorkspaceFile("XREngine.Data/Rendering/VulkanTelemetryEnums.cs");
        string recordingSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string statsSource = ReadWorkspaceFile(
            "XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Stats.Vulkan.cs");
        string captureSource = ReadWorkspaceFile("XRENGINE/Engine/Engine.ProfileCapture.cs");

        foreach (string stage in new[]
        {
            "FrameOpPreparation", "ResourcePlanning", "FrameDataRefresh", "PacketConstruction",
            "PrimaryRecording", "SecondaryRecording", "DescriptorPublication", "Submission",
        })
            stageSource.ShouldContain(stage);

        recordingSource.ShouldContain("EVulkanCpuStage.FrameOpPreparation");
        recordingSource.ShouldContain("EVulkanCpuStage.ResourcePlanning");
        recordingSource.ShouldContain("EVulkanCpuStage.PacketConstruction");
        recordingSource.ShouldContain("EVulkanCpuStage.PrimaryRecording");
        statsSource.ShouldContain("VulkanCpuStageAllocationHighWaterBytes");
        captureSource.ShouldContain("vulkan_cpu_{name}_allocation_high_water_bytes");
    }

    [Test]
    public void NormalRecordingPath_UsesNumericDecisionReasonsWithoutFormattingDiagnosticStrings()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string lowering = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainLowering.cs");

        source.ShouldContain("string? dirtyReason = VulkanFrameDiagnosticsTraceEnabled");
        source.ShouldContain("EVulkanCommandBufferDecisionReason.DescriptorGeneration");
        source.ShouldContain("structuralSignature: currentGenerations.Structural");
        source.ShouldContain("descriptorGeneration: currentGenerations.Descriptor");
        source.ShouldContain("swapchainSlot: commandBufferImageSlot");
        lowering.ShouldContain("if (traceCommandChains || CommandChainValidationEnabled)\n                    firstStructuralDirtyReason ??= DescribeCommandChainDirtyReason");
    }

    private static string Slice(string source, string start, string end)
    {
        int startIndex = source.IndexOf(start, StringComparison.Ordinal);
        startIndex.ShouldBeGreaterThanOrEqualTo(0);
        int endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        endIndex.ShouldBeGreaterThan(startIndex);
        return source[startIndex..endIndex];
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string path = Path.Combine(ResolveRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
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

        throw new DirectoryNotFoundException("Could not locate the XRENGINE repository root.");
    }
}
