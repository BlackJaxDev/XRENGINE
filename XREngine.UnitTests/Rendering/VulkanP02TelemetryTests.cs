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
        // Stage scopes are owned by the renderer, the command runtime, and the frame loop.
        string recordingSource = string.Concat(
            SourceContractWorkspace.ReadVulkanRendererSource(),
            SourceContractWorkspace.ReadVulkanCommandRuntimeSource(),
            SourceContractWorkspace.ReadPartialType(
                "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/Loop/VulkanFrameLoop.PrimaryRecordingPreparation.cs"));
        string submissionSource = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "EVulkanCpuStage.SubmissionPreparation");
        string statsSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.Vulkan.cs");
        string statsTelemetrySource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.Vulkan.FrameTelemetry.cs");
        string captureSource = ReadWorkspaceFile("XREngine.Runtime.Host/Engine/Engine.ProfileCapture.cs");
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
        recordingSource.ShouldContain("EVulkanCpuStage.PacketConstruction");
        recordingSource.ShouldContain("EVulkanCpuStage.PrimaryRecording");
        recordingSource.ShouldContain("EVulkanCpuStage.FrameDataManifest");
        recordingSource.ShouldContain("EVulkanCpuStage.CommandBufferReuse");
        recordingSource.ShouldContain("EVulkanCpuStage.QueueSubmit");
        submissionSource.ShouldContain("EVulkanCpuStage.SubmissionPreparation");
        submissionSource.ShouldContain("EVulkanCpuStage.SubmissionDiagnostics");
        submissionSource.ShouldContain("EVulkanCpuStage.SubmissionPublication");
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
        // Stages without a runtime scope stay in the capture and MCP schemas, so the field set does not change.
        foreach (string stage in new[]
        {
            "ResourcePlanning", "DependencySnapshot", "ImageLayoutSnapshot",
            "SubmissionImageStateValidation", "SubmissionResourceLifetimeValidation",
            "CommandChainFastSignature",
        })
        {
            captureSource.ShouldContain($"EVulkanCpuStage.{stage}, ref first");
            mcpSource.ShouldContain($"= VulkanCpuStage(EVulkanCpuStage.{stage})");
        }

        statsTelemetrySource.ShouldContain(
            "public static VulkanCpuStageTelemetry GetVulkanCpuStageTelemetry(EVulkanCpuStage stage)");
        statsTelemetrySource.ShouldContain("Volatile.Read(ref _vulkanCpuStageInvocationCount[index])");
        mcpSource.ShouldContain(
            "VulkanCpuStageTelemetry telemetry = VulkanStats.GetVulkanCpuStageTelemetry(stage);");
        mcpSource.ShouldContain(
            "process_invocation_count = telemetry.InvocationCount");
        mcpSource.ShouldContain(
            "process_elapsed_ms = telemetry.CumulativeElapsed.TotalMilliseconds");
        mcpSource.ShouldContain(
            "process_peak_ms = telemetry.PeakElapsed.TotalMilliseconds");
    }

    [Test]
    public void CommandBufferRecycling_ExposesProcessLifetimeCounters()
    {
        string statsSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.Vulkan.Binding.cs");
        string captureSource = ReadWorkspaceFile(
            "XREngine.Runtime.Host/Engine/Engine.ProfileCapture.cs");
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
            "XREngine.Runtime.Host/Engine/Engine.ProfileCapture.cs");
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
            "XREngine.Runtime.Host/Engine/Engine.ProfileCapture.cs");
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
        // The prepared schedule identity replaces the old fast signature. The planner
        // must try the cached schedule before it lowers packets again.
        string planningSource = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "CommandChainScheduleCacheIdentity cacheIdentity = new(");
        string preparationSource = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "private VulkanPreparedPrimaryCommandInput PreparePrimaryCommandInput(");
        string cacheSource = SourceContractWorkspace.ReadVulkanSourcesContaining(
            "internal bool TryReuseCachedCommandChainSchedule(");

        int identityIndex = planningSource.IndexOf(
            "CommandChainScheduleCacheIdentity cacheIdentity = new(", StringComparison.Ordinal);
        int reuseIndex = planningSource.IndexOf(
            "if (TryReuseCachedCommandChainSchedule(", identityIndex, StringComparison.Ordinal);
        int loweringIndex = planningSource.IndexOf(
            "EVulkanCpuStage.CommandChainPacketLowering", reuseIndex, StringComparison.Ordinal);
        identityIndex.ShouldBeGreaterThanOrEqualTo(0);
        reuseIndex.ShouldBeGreaterThan(identityIndex);
        loweringIndex.ShouldBeGreaterThan(reuseIndex);
        planningSource.ShouldContain(
            "RecordCommandChainScheduleDecision(EVulkanCommandChainScheduleDecision.ReusedSchedule);\n            return cachedSchedule;");
        planningSource.ShouldContain("schedule.PublishCacheIdentity(in cacheIdentity);");

        string preparation = Slice(
            preparationSource,
            "private VulkanPreparedPrimaryCommandInput PreparePrimaryCommandInput(",
            "return new VulkanPreparedPrimaryCommandInput(");
        preparation.ShouldContain("_commandRuntime.TryBuildCommandChainSchedule(");
        preparation.ShouldContain("framePlan.StaticOperationSignature,");
        preparation.ShouldContain("preparedRecordingTarget: authority.RecordingTargetSnapshot,");
        preparation.ShouldContain("descriptorVersionSignature: framePlan.DescriptorVersionSignature);");

        cacheSource.ShouldContain("if (!identity.IsReusable || CommandChainBenchmarkForceRerecord ||");
    }

    [Test]
    public void PacketLowering_ReusesPreparedMeshDrawPackets()
    {
        string loweringSource = SourceContractWorkspace.ReadVulkanSourcesContaining("_commandChainDrawPacketScratch[0] = firstDraw;");

        loweringSource.ShouldContain("_commandChainDrawPacketScratch[0] = firstDraw;");
        loweringSource.ShouldContain("_commandChainDrawPacketScratch[runCount] = candidateDraw;");
        loweringSource.ShouldContain("DrawPacket draw = draws[i];");
        loweringSource.ShouldContain("preparedMeshDraw = firstDraw;");
        loweringSource.ShouldContain("firstDraw = preparedMeshDraw;");
        loweringSource.ShouldContain("? firstDraw.StructuralSignature");
    }

    [Test]
    public void NormalRecordingPath_UsesNumericDecisionReasonsWithoutFormattingDiagnosticStrings()
    {
        string lowering = SourceContractWorkspace.ReadVulkanSourcesContaining("if (traceCommandChains || CommandChainValidationEnabled)");

        lowering.ShouldContain("secondaryDecisionReasons |= EVulkanCommandBufferDecisionReason.DescriptorGeneration;");
        lowering.ShouldContain(
            "dirtyReason: null,\n            detailReasons: secondaryDecisionReasons,\n" +
            "            structuralSignature: lastPacket.StructuralSignature,\n" +
            "            descriptorGeneration: lastPacket.DescriptorSnapshot.DescriptorGeneration,\n" +
            "            swapchainSlot: unchecked((int)imageIndex));");
        lowering.ShouldContain("if (traceCommandChains || CommandChainValidationEnabled)\n                    firstStructuralDirtyReason ??= DescribeCommandChainDirtyReason");
        lowering.ShouldContain(
            "(traceCommandChains || CommandChainValidationEnabled))\n                    firstDescriptorMismatch ??= $\"chain=");
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
