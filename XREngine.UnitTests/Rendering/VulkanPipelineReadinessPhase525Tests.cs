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
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineVariantManifest.cs");

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
    public void PipelinePrewarmHappensBeforeCommandBufferBeginAndOptionalNodesDeferLocally()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");

        int manifest = source.IndexOf("GetOrBuildPipelineVariantManifest(", StringComparison.Ordinal);
        int begin = source.IndexOf("Api!.BeginCommandBuffer", manifest, StringComparison.Ordinal);
        manifest.ShouldBeGreaterThanOrEqualTo(0);
        begin.ShouldBeGreaterThan(manifest);
        source.ShouldContain("optionalPipelineDeferredOpIndices.Add(opIndex)");
        source.ShouldContain("if (optionalPipelineDeferredOpIndices.Contains(opIndex))");
        source.ShouldContain("Required graphics pipeline became pending after declared warmup");
    }

    [Test]
    public void PipelineOptionalityIsExplicitDefaultRequiredMetadata()
    {
        string metadata = ReadWorkspaceFile("XREngine.Runtime.Rendering/RenderGraph/RenderPassMetadata.cs");
        string builder = ReadWorkspaceFile("XREngine.Runtime.Rendering/RenderGraph/RenderPassBuilder.cs");
        string plan = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.VulkanRenderGraphPlan.cs");

        metadata.ShouldContain("public bool RequiresPipelineReady { get; private set; } = true;");
        builder.ShouldContain("AllowPipelineDeferral()");
        builder.ShouldContain("UpdatePipelineReadiness(required: false)");
        plan.ShouldContain("pass.RequiresPipelineReady");
    }

    [Test]
    public void WarmupManifestCacheIsBoundedAndOwnsCompletionState()
    {
        string manifest = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineVariantManifest.cs");
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");

        manifest.ShouldContain("MaxCachedPipelineVariantManifests = 64");
        manifest.ShouldContain("_pipelineVariantManifestCache.TryGetValue");
        manifest.ShouldContain("while (_pipelineVariantManifestCache.Count >= MaxCachedPipelineVariantManifests");
        recording.ShouldContain("pipelineVariantManifest.WarmupCompleted");
        recording.ShouldContain("pipelineVariantManifest.MarkWarmupCompleted()");
        recording.ShouldNotContain("_completedPipelineWarmupManifests");
    }

    [Test]
    public void PipelineManifestWarmupCompletionIsAnExplicitMonotonicLatch()
    {
        RenderPassMetadataCollection metadata = new();
        metadata.ForPass(1, "Opaque");
        VulkanRenderer.VulkanRenderGraphPlan plan =
            new VulkanRenderer.VulkanRenderGraphCompiler().Compile(metadata.Build()).Plan;
        VulkanRenderer.VulkanPipelineVariantManifest manifest =
            VulkanRenderer.VulkanPipelineVariantManifest.Build(
                plan,
                [],
                EMeshSubmissionStrategy.CpuDirect,
                dynamicRendering: true,
                recordingStructuralSignature: 17UL);

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

        VulkanRenderer.VulkanRenderGraphPlan plan =
            new VulkanRenderer.VulkanRenderGraphCompiler().Compile(metadata.Build()).Plan;

        plan.Passes.Single(pass => pass.PassIndex == 1).RequiresPipelineReady.ShouldBeTrue();
        plan.Passes.Single(pass => pass.PassIndex == 2).RequiresPipelineReady.ShouldBeFalse();
    }

    [Test]
    public void AsyncPipelinePublicationAvoidsGlobalInvalidationAndUsesTimelineRetirement()
    {
        string compileQueue = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineCompileQueue.cs");
        string cache = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Pipelines/VulkanGraphicsPipelineCache.cs");
        string retirement = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceRetirement.cs");

        compileQueue.ShouldNotContain("renderer.MarkCommandBuffersDirty()");
        cache.ShouldContain("_sharedGraphicsPipelineGeneration++");
        cache.ShouldContain("RetirePipeline(pipeline)");
        retirement.ShouldContain("CaptureVulkanRetirementTicket(");
        retirement.ShouldContain("IsVulkanRetirementReady(candidate.Ticket)");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            string candidate = Path.Combine(directory, relativePath);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate).Replace("\r\n", "\n");
            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new FileNotFoundException($"Unable to locate workspace file '{relativePath}'.");
    }
}
