using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering.RenderGraph;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanPipelineReadinessPhase525Tests
{
    [Test]
    public void CompiledPlanBuildsImmutablePipelineVariantManifestAcrossRequiredAxes()
    {
        // The manifest contract, its compatibility hashing, and the
        // requirement record are separate files.
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineVariantManifest.Contract.cs") +
            ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineVariantManifest.Compatibility.cs") +
            ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineVariantRequirement.cs");

        source.ShouldContain("plan.CompatibilityIdentity");
        source.ShouldContain("EMeshSubmissionStrategy SubmissionStrategy");
        source.ShouldContain("bool Shadow");
        source.ShouldContain("bool Velocity");
        source.ShouldContain("bool EditorId");
        source.ShouldContain("bool MaterialOverride");
        source.ShouldContain("bool Stereo");
        source.ShouldContain("bool Multiview");
        source.ShouldContain("bool DynamicRendering");
        source.ShouldContain("bool LegacyRenderPass");
        source.ShouldContain("planPass?.RequiresPipelineReady ?? true");
        source.ShouldNotContain("IsOptionalPipelineNode");
        source.ShouldNotContain("renderer.GetHashCode()");
    }

    [Test]
    public void PipelinePrewarmHappensBeforeCommandBufferBeginAndPendingDrawsDeferLocally()
    {
        // Primary recording builds the variant manifest and admits its
        // pipelines in the preparation phase. Only then does it begin the
        // command buffer. Optional draws defer locally. A pending required
        // pipeline defers the whole recording before vkBeginCommandBuffer.
        string lifecycleSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.cs");
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Preparation.cs");
        string primaryRecordingSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/VulkanCommandRuntime.PrimaryRecording.cs");

        int prepare = lifecycleSource.IndexOf("TryPreparePrimaryFrameData(", StringComparison.Ordinal);
        int encoding = lifecycleSource.IndexOf("PreparePrimaryCommandEncoding(ref recordingState);", prepare, StringComparison.Ordinal);
        prepare.ShouldBeGreaterThanOrEqualTo(0);
        encoding.ShouldBeGreaterThan(prepare);

        int manifest = source.IndexOf("ResourceRuntime.PipelineManager.GetOrBuildVariantManifest(", StringComparison.Ordinal);
        int admit = source.IndexOf("if (!TryAdmitPrimaryGraphicsPipelines(", manifest, StringComparison.Ordinal);
        int beginEncoding = source.IndexOf("private void PreparePrimaryCommandEncoding(", StringComparison.Ordinal);
        int begin = source.IndexOf("ResetAndBeginPrimaryCommandBuffer(ref recordingState);", beginEncoding, StringComparison.Ordinal);
        manifest.ShouldBeGreaterThanOrEqualTo(0);
        admit.ShouldBeGreaterThan(manifest);
        beginEncoding.ShouldBeGreaterThanOrEqualTo(0);
        begin.ShouldBeGreaterThan(beginEncoding);

        source.ShouldContain(
            "recordingState.PipelineDeferredOperationIndices.Add(\n" +
            "                                opIndex);");
        source.ShouldContain(
            "if (recordingState.PipelineDeferredOperationIndices.Contains(\n" +
            "                            opIndex))");
        source.ShouldContain(
            "recordingState.PipelineDeferredOperationIndices.Contains(\n" +
            "                        opIndex) ||");
        source.ShouldContain("optionalRequirements.Add(requirementIndex);");
        source.ShouldContain("Graphics pipeline compilation is still pending before vkBeginCommandBuffer.");
        source.ShouldContain("Defer the whole sealed graph until");
        source.ShouldContain(
            "bool manifestReset = previousManifestIdentity != manifestIdentity ||\n" +
            "                pipelineRetired;");
        source.ShouldContain("ResourceRuntime.PipelineManager.SharedGraphicsPipelineRetirementGeneration;");
        primaryRecordingSource.ShouldContain("SharedGraphicsPipelineGeneration);");
        source.ShouldContain("pendingRequirements.Contains(requirementIndex) ||");
        source.ShouldNotContain("Vulkan.PipelineDrawDeferred.");
    }

    [Test]
    public void PipelineOptionalityIsExplicitDefaultRequiredMetadata()
    {
        string metadata = ReadWorkspaceFile("XREngine.Runtime.Rendering/RenderGraph/RenderPassMetadata.cs");
        string builder = ReadWorkspaceFile("XREngine.Runtime.Rendering/RenderGraph/RenderPassBuilder.cs");
        string plan = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanCompiledRenderGraphPlan.cs");

        metadata.ShouldContain("public bool RequiresPipelineReady { get; private set; } = true;");
        builder.ShouldContain("AllowPipelineDeferral()");
        builder.ShouldContain("UpdatePipelineReadiness(required: false)");
        plan.ShouldContain("pass.RequiresPipelineReady");
    }

    [Test]
    public void WarmupManifestCacheIsBoundedAndOwnsCompletionState()
    {
        string manifest = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineManager.cs");
        string contract = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineVariantManifest.Contract.cs");
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Preparation.cs");

        manifest.ShouldContain("MaxCachedPipelineVariantManifests = 64");
        manifest.ShouldContain("_pipelineVariantManifestCache.TryGetValue");
        manifest.ShouldContain("while (_pipelineVariantManifestCache.Count >= MaxCachedPipelineVariantManifests");
        // The manifest owns its warmup state. Recording reads and sets it for
        // the active native pipeline generation.
        contract.ShouldContain("public bool WarmupCompleted");
        contract.ShouldContain("public void MarkWarmupCompleted(long retirementGeneration)");
        recording.ShouldContain("pipelineVariantManifest.IsWarmForPipelineGeneration(");
        recording.ShouldContain("pipelineVariantManifest.MarkWarmupCompleted(pipelineRetirementGeneration)");
        recording.ShouldNotContain("_completedPipelineWarmupManifests");
        manifest.ShouldNotContain("_completedPipelineWarmupManifests");
    }

    [Test]
    public void PipelineManifestWarmupCompletionIsAnExplicitMonotonicLatch()
    {
        RenderPassMetadataCollection metadata = new();
        metadata.ForPass(1, "Opaque");
        VulkanCompiledRenderGraphPlan plan =
            new VulkanRenderGraphCompiler().Compile(metadata.Build()).Plan;
        VulkanPipelineVariantManifest manifest =
            VulkanPipelineVariantManifest.Build(
                plan,
                FrameOperationSequence.Empty,
                EMeshSubmissionStrategy.CpuDirect,
                dynamicRendering: true,
                recordingStructuralSignature: 17UL,
                renderGraphPlanSignature: plan.CompatibilityIdentity,
                framePlan: null);

        manifest.WarmupCompleted.ShouldBeFalse();
        manifest.MarkWarmupCompleted();
        manifest.WarmupCompleted.ShouldBeTrue();
        manifest.MarkWarmupCompleted();
        manifest.WarmupCompleted.ShouldBeTrue();
    }

    [Test]
    public void PipelineOptionalitySurvivesCompilationAsRequiredByDefaultAndExplicitWhenDeferred()
    {
        RenderPassMetadataCollection metadata = new();
        metadata.ForPass(1, "RequiredOpaque");
        metadata.ForPass(2, "OptionalOverlay").AllowPipelineDeferral();

        VulkanCompiledRenderGraphPlan plan =
            new VulkanRenderGraphCompiler().Compile(metadata.Build()).Plan;

        plan.Passes.Single(pass => pass.PassIndex == 1).RequiresPipelineReady.ShouldBeTrue();
        plan.Passes.Single(pass => pass.PassIndex == 2).RequiresPipelineReady.ShouldBeFalse();
    }

    [Test]
    public void AsyncPipelinePublicationAvoidsGlobalInvalidationAndUsesTimelineRetirement()
    {
        string compileQueue = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineCompileQueue.cs");
        string cache = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineManager.cs");
        string retirement = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Authority/VulkanResourceRuntime.cs");

        compileQueue.ShouldNotContain("renderer.MarkCommandBuffersDirty()");
        cache.ShouldContain("_sharedGraphicsPipelineGeneration++");
        cache.ShouldContain("RequireProgramServices().RetirePipeline(pipeline);");
        retirement.ShouldContain("tracker.FenceResourceRecordingAdmission(key, owner);");
        retirement.ShouldContain("VulkanRetirementTicket ticket;");
        retirement.ShouldContain("Lifetime.Tracker.IsRetirementReady(candidate.Ticket)");
    }

    private static string ReadWorkspaceFile(string relativePath)
        => SourceContractWorkspace.ReadFile(relativePath);
}
