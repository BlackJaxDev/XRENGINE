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
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.Recording.cs");

        string snapshotBlock = Slice(source,
            "Vulkan.FrameLifecycle.SnapshotImGuiOverlay",
            "attempt.PreserveSwapchainForImGuiOverlay");
        snapshotBlock.ShouldContain("attempt.Timing.SnapshotImGuiOverlay +=");
        snapshotBlock.ShouldNotContain("attempt.Timing.RecordCommandBuffer +=");

        string sceneBlock = Slice(source,
            "Vulkan.FrameLifecycle.RecordCommandBuffer",
            "attempt.ScenePrimaryRecordedThisFrame");
        sceneBlock.ShouldContain("TimeSpan elapsed =");
        sceneBlock.ShouldContain("Stopwatch.GetElapsedTime(stageStartTimestamp);");
        sceneBlock.ShouldContain("attempt.Timing.RecordSceneCommandBuffer += elapsed;");
        sceneBlock.ShouldContain("attempt.Timing.RecordCommandBuffer += elapsed;");
    }

    [Test]
    public void RecordingStages_ExposeTimingAllocationAndHighWaterTelemetry()
    {
        string stageSource = ReadWorkspaceFile("XREngine.Data/Rendering/VulkanTelemetryEnums.cs");
        string recordingSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string submissionSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Synchronization.cs");
        string statsSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.Vulkan.cs");
        string captureSource = ReadWorkspaceFile("XRENGINE/Engine/Engine.ProfileCapture.cs");
        string mcpSource = ReadWorkspaceFile(
            "XREngine.Editor/Mcp/Actions/EditorMcpActions.Profiler.cs");

        foreach (string stage in new[]
        {
            "FrameOpPreparation", "ResourcePlanning", "FrameDataRefresh", "PacketConstruction",
            "PrimaryRecording", "SecondaryRecording", "DescriptorPublication", "Submission",
            "FrameDataManifest", "DependencySnapshot", "ImageLayoutSnapshot", "CommandBufferReuse",
            "SubmissionPreparation", "SubmissionDiagnostics", "SubmissionImageStateValidation",
            "SubmissionResourceLifetimeValidation", "QueueSubmit", "SubmissionPublication",
            "CommandChainFastSignature", "CommandChainPacketLowering", "CommandChainScheduleEvaluation",
            "PrimaryFrameDataManifest", "PrimaryPrewarm", "PrimaryCommandEncoding",
            "PreparedDrawConstruction", "SecondaryMerge", "CommandDependencyComparison",
            "CommandDirtyPropagation", "CommandCacheScanning",
            "MeshDrawPreparation", "MeshDrawResourcePreparation",
            "MeshDrawBindingPreparation", "MeshDrawMaterialBindings",
            "MeshDrawBindingSnapshotCopy", "MeshDrawEnqueue",
            "FrameDataDescriptorValidation", "FrameDataEngineUniformUpload",
            "FrameDataAutoUniformUpload",
        })
            stageSource.ShouldContain(stage);

        recordingSource.ShouldContain("EVulkanCpuStage.FrameOpPreparation");
        recordingSource.ShouldContain("EVulkanCpuStage.ResourcePlanning");
        recordingSource.ShouldContain("EVulkanCpuStage.PacketConstruction");
        recordingSource.ShouldContain("EVulkanCpuStage.PrimaryRecording");
        recordingSource.ShouldContain("EVulkanCpuStage.FrameDataManifest");
        recordingSource.ShouldContain("EVulkanCpuStage.DependencySnapshot");
        recordingSource.ShouldContain("EVulkanCpuStage.ImageLayoutSnapshot");
        recordingSource.ShouldContain("EVulkanCpuStage.CommandBufferReuse");
        submissionSource.ShouldContain("EVulkanCpuStage.SubmissionPreparation");
        submissionSource.ShouldContain("EVulkanCpuStage.SubmissionDiagnostics");
        submissionSource.ShouldContain("EVulkanCpuStage.SubmissionImageStateValidation");
        submissionSource.ShouldContain("EVulkanCpuStage.SubmissionResourceLifetimeValidation");
        submissionSource.ShouldContain("EVulkanCpuStage.QueueSubmit");
        submissionSource.ShouldContain("EVulkanCpuStage.SubmissionPublication");
        recordingSource.ShouldContain("EVulkanCpuStage.CommandChainFastSignature");
        recordingSource.ShouldContain("EVulkanCpuStage.CommandChainPacketLowering");
        recordingSource.ShouldContain("EVulkanCpuStage.CommandChainScheduleEvaluation");
        recordingSource.ShouldContain("EVulkanCpuStage.PrimaryFrameDataManifest");
        recordingSource.ShouldContain("EVulkanCpuStage.PrimaryPrewarm");
        recordingSource.ShouldContain("EVulkanCpuStage.PrimaryCommandEncoding");
        recordingSource.ShouldContain("EVulkanCpuStage.PreparedDrawConstruction");
        recordingSource.ShouldContain("EVulkanCpuStage.SecondaryMerge");
        recordingSource.ShouldContain("EVulkanCpuStage.CommandDependencyComparison");
        recordingSource.ShouldContain("EVulkanCpuStage.CommandDirtyPropagation");
        recordingSource.ShouldContain("EVulkanCpuStage.CommandCacheScanning");
        statsSource.ShouldContain("VulkanCpuStageAllocationHighWaterBytes");
        statsSource.ShouldContain("VulkanCpuStageInvocationCount");
        statsSource.ShouldContain("VulkanCpuStageCumulativeMs");
        statsSource.ShouldContain("VulkanCpuStagePeakMs");
        captureSource.ShouldContain("vulkan_cpu_{name}_allocation_high_water_bytes");
        captureSource.ShouldContain("vulkan_cpu_{name}_process_invocation_count");
        captureSource.ShouldContain("vulkan_cpu_{name}_process_elapsed_ms");
        captureSource.ShouldContain("vulkan_cpu_{name}_process_peak_ms");
        captureSource.ShouldContain(
            "\"prepared_draw_construction\", EVulkanCpuStage.PreparedDrawConstruction");
        captureSource.ShouldContain(
            "\"secondary_recording\", EVulkanCpuStage.SecondaryRecording");
        captureSource.ShouldContain(
            "\"secondary_merge\", EVulkanCpuStage.SecondaryMerge");
        captureSource.ShouldContain(
            "\"primary_command_encoding\", EVulkanCpuStage.PrimaryCommandEncoding");
        captureSource.ShouldContain(
            "\"submission\", EVulkanCpuStage.Submission");
        captureSource.ShouldContain(
            "\"mesh_draw_material_bindings\", EVulkanCpuStage.MeshDrawMaterialBindings");
        captureSource.ShouldContain(
            "\"mesh_draw_binding_snapshot_copy\", EVulkanCpuStage.MeshDrawBindingSnapshotCopy");
        captureSource.ShouldContain(
            "\"frame_data_auto_uniform_upload\", EVulkanCpuStage.FrameDataAutoUniformUpload");
        mcpSource.ShouldContain(
            "prepared_draw_construction = VulkanCpuStage(EVulkanCpuStage.PreparedDrawConstruction)");
        mcpSource.ShouldContain(
            "secondary_merge = VulkanCpuStage(EVulkanCpuStage.SecondaryMerge)");
        mcpSource.ShouldContain(
            "command_dependency_comparison = VulkanCpuStage(EVulkanCpuStage.CommandDependencyComparison)");
        mcpSource.ShouldContain(
            "command_dirty_propagation = VulkanCpuStage(EVulkanCpuStage.CommandDirtyPropagation)");
        mcpSource.ShouldContain(
            "command_cache_scanning = VulkanCpuStage(EVulkanCpuStage.CommandCacheScanning)");
        mcpSource.ShouldContain(
            "process_invocation_count = VulkanStats.VulkanCpuStageInvocationCount(stage)");
        mcpSource.ShouldContain(
            "process_elapsed_ms = VulkanStats.VulkanCpuStageCumulativeMs(stage)");
        mcpSource.ShouldContain(
            "process_peak_ms = VulkanStats.VulkanCpuStagePeakMs(stage)");
    }

    [Test]
    public void CommandBufferRecycling_ExposesProcessLifetimeCounters()
    {
        string statsSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.Vulkan.Binding.cs");
        string captureSource = ReadWorkspaceFile(
            "XRENGINE/Engine/Engine.ProfileCapture.cs");
        string mcpSource = ReadWorkspaceFile(
            "XREngine.Editor/Mcp/Actions/EditorMcpActions.Profiler.cs");

        foreach (string counter in new[]
        {
            "VulkanProcessResetCommandBufferCalls",
            "VulkanProcessResetCommandPoolCalls",
            "VulkanProcessAllocateCommandBufferCalls",
            "VulkanProcessCommandBuffersAllocated",
            "VulkanProcessExecuteSecondaryCommandBufferCalls",
            "VulkanProcessSecondaryCommandBuffersInvoked",
            "VulkanProcessWorkerSecondaryCommandBufferResetCalls",
            "VulkanProcessWorkerSecondaryCommandBufferAllocations",
            "VulkanProcessWorkerSecondaryReplacementAllocations",
        })
        {
            statsSource.ShouldContain(counter);
            mcpSource.ShouldContain(counter);
        }

        foreach (string field in new[]
        {
            "vulkan_process_reset_command_buffer_calls",
            "vulkan_process_reset_command_pool_calls",
            "vulkan_process_allocate_command_buffer_calls",
            "vulkan_process_command_buffers_allocated",
            "vulkan_process_execute_secondary_command_buffer_calls",
            "vulkan_process_secondary_command_buffers_invoked",
            "vulkan_process_worker_secondary_command_buffer_reset_calls",
            "vulkan_process_worker_secondary_command_buffer_allocations",
            "vulkan_process_worker_secondary_replacement_allocations",
        })
            captureSource.ShouldContain(field);
    }

    [Test]
    public void FrequencyPublication_ReportsDirtyReuseAndByteCountsPerOwner()
    {
        string writeSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Uniforms.cs");
        string statsSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.Vulkan.Binding.cs");
        string captureSource = ReadWorkspaceFile(
            "XRENGINE/Engine/Engine.ProfileCapture.cs");
        string mcpSource = ReadWorkspaceFile(
            "XREngine.Editor/Mcp/Actions/EditorMcpActions.Profiler.cs");

        writeSource.ShouldContain(
            "RecordVulkanAutoUniformFrequencyPublication(");
        statsSource.ShouldContain(
            "GetVulkanAutoUniformFrequencyPublicationCount");
        statsSource.ShouldContain(
            "GetVulkanAutoUniformFrequencyReuseCount");
        statsSource.ShouldContain(
            "GetVulkanAutoUniformFrequencyPublishedBytes");
        statsSource.ShouldContain(
            "_lastFrameVulkanAutoUniformFrequencyPublications");
        captureSource.ShouldContain(
            "vulkan_auto_uniform_{name}_published_bytes");
        mcpSource.ShouldContain(
            "auto_uniform_frequency_publication = new");
        mcpSource.ShouldContain(
            "runtime_callback = VulkanFrequencyPublication(VulkanStats.VulkanBindingFrequencyRuntimeCallbackIndex)");
    }

    [Test]
    public void CommandRecordingCounts_CoverVisiblePreparedAndRetiredArtifacts()
    {
        string recordingSource = global::XREngine.UnitTests.SourceContractWorkspace.ReadPartialType(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string statsSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.Vulkan.Binding.cs");
        string captureSource = ReadWorkspaceFile(
            "XRENGINE/Engine/Engine.ProfileCapture.cs");
        string mcpSource = ReadWorkspaceFile(
            "XREngine.Editor/Mcp/Actions/EditorMcpActions.Profiler.cs");

        recordingSource.ShouldContain(
            "RecordVulkanVisibleMeshDrawCohort(");
        recordingSource.ShouldContain(
            "RecordVulkanPreparedMeshDraws(");
        recordingSource.ShouldContain(
            "RecordVulkanRecordedCommandArtifactRetirement(");
        statsSource.ShouldContain("VulkanVisibleMeshDraws");
        statsSource.ShouldContain("VulkanUniqueVisibleMaterials");
        statsSource.ShouldContain(
            "VulkanRecordedCommandArtifactRetirements");
        captureSource.ShouldContain("vulkan_visible_mesh_draws");
        captureSource.ShouldContain("vulkan_unique_visible_materials");
        captureSource.ShouldContain(
            "vulkan_recorded_command_artifact_retirements");
        mcpSource.ShouldContain("visible_mesh_draws");
        mcpSource.ShouldContain("unique_visible_materials");
        mcpSource.ShouldContain(
            "recorded_command_artifact_retirements");
    }

    [Test]
    public void PrimaryReuseMiss_ReusesPreparedCommandChainFastSignatureDuringLowering()
    {
        string recordingSource = SourceContractWorkspace.ReadVulkanRendererSource();
        string loweringSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainLowering.cs");

        recordingSource.ShouldContain(
            "out state.PreparedCommandChainFastScheduleSignature");
        recordingSource.ShouldContain(
            "out state.HasPreparedCommandChainFastScheduleSignature");
        recordingSource.ShouldContain(
            "? state.PreparedCommandChainFastScheduleSignature");
        loweringSource.ShouldContain(
            "ulong? preparedFastScheduleSignature = null");
        loweringSource.ShouldContain(
            "if (!preparedFastScheduleSignature.HasValue)");
    }

    [Test]
    public void PacketLowering_ReusesPreparedMeshDrawPackets()
    {
        string loweringSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainLowering.cs");

        loweringSource.ShouldContain("_commandChainDrawPacketScratch[0] = firstDraw;");
        loweringSource.ShouldContain("_commandChainDrawPacketScratch[runCount] = candidateDraw;");
        loweringSource.ShouldContain("DrawPacket draw = draws[i];");
        loweringSource.ShouldContain("MeshDrawOp => preparedMeshDraw");
        loweringSource.ShouldContain("? firstDraw.StructuralSignature");
    }

    [Test]
    public void NormalRecordingPath_UsesNumericDecisionReasonsWithoutFormattingDiagnosticStrings()
    {
        string source = SourceContractWorkspace.ReadVulkanRendererSource();
        string lowering = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainLowering.cs");

        source.ShouldContain("string? dirtyReason = VulkanFrameDiagnosticsTraceEnabled");
        source.ShouldContain("EVulkanCommandBufferDecisionReason.DescriptorGeneration");
        source.ShouldContain("state.CurrentGenerations.Structural");
        source.ShouldContain("state.CurrentGenerations.Descriptor");
        source.ShouldContain("swapchainSlot: state.CommandBufferImageSlot");
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

        throw new DirectoryNotFoundException("Could not locate the XRENGINE repository root.");
    }
}
