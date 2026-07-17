using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanPipelineCompilationP05Tests
{
    [Test]
    public void BackgroundGraphicsCompilation_UsesSharedPipelineCacheAndCompileRequiredProbe()
    {
        string queue = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineCompileQueue.cs");
        string cache = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineCache.cs");

        queue.ShouldContain("pipelineCache: ActivePipelineCache");
        queue.ShouldNotContain("pipelineCache: default");
        cache.ShouldContain("VulkanPipelineFailOnCompileRequiredFlag");
        cache.ShouldContain("VulkanPipelineCompileRequiredResult");
        cache.ShouldContain("EVulkanPipelineTelemetryEvent.CompileRequired");
        cache.ShouldContain("EVulkanDriverPipelineCacheOutcome.PersistedHit");
        cache.ShouldContain("EVulkanDriverPipelineCacheOutcome.RuntimeHit");
        cache.ShouldContain("Background graphics pipeline cache probe completed");
    }

    [Test]
    public void PersistedPipelineIdentities_UseDeterministicHashing()
    {
        VulkanStableHash64 first = new(schemaVersion: 2);
        first.Add("shader-artifact");
        first.Add(17u);
        first.Add(true);

        VulkanStableHash64 second = new(schemaVersion: 2);
        second.Add("shader-artifact");
        second.Add(17u);
        second.Add(true);

        VulkanStableHash64 changed = new(schemaVersion: 2);
        changed.Add("shader-artifact");
        changed.Add(18u);
        changed.Add(true);

        second.Value.ShouldBe(first.Value);
        changed.Value.ShouldNotBe(first.Value);

        string program = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        int graphicsStart = program.IndexOf("public ulong ComputeGraphicsPipelineFingerprint()", StringComparison.Ordinal);
        int artifactStart = program.IndexOf("private string ComputeProgramArtifactFingerprint()", graphicsStart, StringComparison.Ordinal);
        string persistedFingerprints = program[graphicsStart..artifactStart];
        persistedFingerprints.ShouldContain("VulkanStableHash64");
        persistedFingerprints.ShouldNotContain("HashCode");
    }

    [Test]
    public void AsyncCompileQueue_IsBoundedAndPublishesCompletedPipelines()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineCompileQueue.cs");

        source.ShouldContain("activeJobCount >= capacity");
        source.ShouldContain("EVulkanPipelineTelemetryEvent.QueueRejected");
        source.ShouldContain("_vulkanGraphicsPipelineCompileJobs.TryRemove(compileKey");
        source.ShouldContain("StoreOrRetireSharedGraphicsPipeline(pipelineKey, result.Pipeline)");
    }

    [Test]
    public void EveryMeshRecordingPath_PrewarmsBeforeBeginningCommandRecording()
    {
        string secondarySource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.SecondaryCommandBuffers.cs");
        int methodStart = secondarySource.IndexOf("private void RecordScheduledMeshCommandChainWorker", StringComparison.Ordinal);
        int methodEnd = secondarySource.IndexOf("private bool TryRecordSecondaryBucket", methodStart, StringComparison.Ordinal);
        string method = secondarySource[methodStart..methodEnd];

        int prewarm = method.IndexOf("TryPrewarmGraphicsPipelinesForRecording", StringComparison.Ordinal);
        int begin = method.IndexOf("Api.BeginCommandBuffer(secondary", StringComparison.Ordinal);
        prewarm.ShouldBeGreaterThanOrEqualTo(0);
        begin.ShouldBeGreaterThan(prewarm);
        method.ShouldContain("chain.State = CommandChainState.NotReady;");
        method.ShouldContain("chain.DirtyReason |= CommandChainDirtyReason.PipelineGeneration;");

        int dynamicUiMethodStart = secondarySource.IndexOf(
            "private bool RecordDynamicUiBatchTextSecondaryCommandBuffer", StringComparison.Ordinal);
        int dynamicUiMethodEnd = secondarySource.IndexOf(
            "private bool TryRecordDynamicUiBatchTextOverlayCommandBuffer", dynamicUiMethodStart, StringComparison.Ordinal);
        string dynamicUiMethod = secondarySource[dynamicUiMethodStart..dynamicUiMethodEnd];
        int dynamicUiPrewarm = dynamicUiMethod.IndexOf("TryPrewarmGraphicsPipelinesForRecording", StringComparison.Ordinal);
        int dynamicUiBegin = dynamicUiMethod.IndexOf("Api!.BeginCommandBuffer(secondaryCommandBuffer", StringComparison.Ordinal);
        dynamicUiPrewarm.ShouldBeGreaterThanOrEqualTo(0);
        dynamicUiBegin.ShouldBeGreaterThan(dynamicUiPrewarm);

        string primarySource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        int primaryMethodStart = primarySource.IndexOf("private bool TryRecordCommandBuffer", StringComparison.Ordinal);
        int primaryMethodEnd = primarySource.IndexOf("internal static bool ShouldRefreshUnwrittenSwapchainForPresent", primaryMethodStart, StringComparison.Ordinal);
        string primaryMethod = primarySource[primaryMethodStart..primaryMethodEnd];
        int primaryPrewarm = primaryMethod.IndexOf("TryPrewarmGraphicsPipelinesForRecording", StringComparison.Ordinal);
        int primaryBegin = primaryMethod.IndexOf("Api!.BeginCommandBuffer(commandBuffer", StringComparison.Ordinal);
        primaryPrewarm.ShouldBeGreaterThanOrEqualTo(0);
        primaryBegin.ShouldBeGreaterThan(primaryPrewarm);
        primaryMethod.ShouldContain("Graphics pipeline prewarm deferred before vkBeginCommandBuffer");

        string pipeline = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");
        pipeline.ShouldNotContain("materializeKnownWarmCache");
        pipeline.ShouldContain("TryEnqueueVulkanGraphicsPipelineCompile");
    }

    [Test]
    public void CompatibleImportedTexturePublication_DoesNotChangePipelineIdentity()
    {
        VulkanPipelinePrewarmEntry beforePublication = CreateGraphicsEntry(
            meshName: "ImportedMesh",
            materialName: "Material.BeforeStreaming");
        VulkanPipelinePrewarmEntry afterPublication = CreateGraphicsEntry(
            meshName: "ImportedMesh",
            materialName: "Material.AfterStreaming");

        afterPublication.Key.ShouldBe(beforePublication.Key);
    }

    [Test]
    public void MotionVectorAndMaterialLayoutVariants_HaveDistinctPipelineIdentities()
    {
        VulkanPipelinePrewarmEntry mainPass = CreateGraphicsEntry();
        VulkanPipelinePrewarmEntry motionVectorPass = CreateGraphicsEntry(passMetadataHash: 0x302);
        VulkanPipelinePrewarmEntry changedMaterialLayout = CreateGraphicsEntry(descriptorLayoutHash: 0x203);
        VulkanPipelinePrewarmEntry changedShader = CreateGraphicsEntry(programPipelineHash: 0x103);
        VulkanPipelinePrewarmEntry changedFixedFunctionState = CreateGraphicsEntry(fixedFunctionStateHash: 0x502);

        motionVectorPass.Key.ShouldNotBe(mainPass.Key);
        changedMaterialLayout.Key.ShouldNotBe(mainPass.Key);
        changedShader.Key.ShouldNotBe(mainPass.Key);
        changedFixedFunctionState.Key.ShouldNotBe(mainPass.Key);
    }

    [Test]
    public void WarmDatabase_ReloadsOnlyForMatchingVersionedDeviceProfile()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory, nameof(VulkanPipelineCompilationP05Tests));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"prewarm-{Guid.NewGuid():N}.json");
        const string deviceProfile = "v5_vendor_device_driver_api_features";

        try
        {
            VulkanPipelinePrewarmEntry startup = CreateGraphicsEntry();
            VulkanPipelinePrewarmEntry motionVectors = CreateGraphicsEntry(passMetadataHash: 0x302);
            VulkanPipelinePrewarmDatabase cold = VulkanPipelinePrewarmDatabase.LoadOrCreate(path, deviceProfile);
            cold.WasKnownAtStartup(startup.Key).ShouldBeFalse();
            cold.Record(startup).ShouldBeTrue();
            cold.Record(motionVectors).ShouldBeTrue();
            cold.WasKnownAtStartup(startup.Key).ShouldBeFalse();
            cold.Save(path);

            VulkanPipelinePrewarmDatabase warm = VulkanPipelinePrewarmDatabase.LoadOrCreate(path, deviceProfile);
            warm.EntryCount.ShouldBe(2);
            warm.Contains(startup.Key).ShouldBeTrue();
            warm.Contains(motionVectors.Key).ShouldBeTrue();
            warm.WasKnownAtStartup(startup.Key).ShouldBeTrue();

            VulkanPipelinePrewarmDatabase incompatible = VulkanPipelinePrewarmDatabase.LoadOrCreate(path, deviceProfile + "_new_driver");
            incompatible.EntryCount.ShouldBe(0);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static VulkanPipelinePrewarmEntry CreateGraphicsEntry(
        string meshName = "Mesh",
        string materialName = "Material",
        ulong programPipelineHash = 0x101,
        ulong vertexLayoutHash = 0x201,
        ulong descriptorLayoutHash = 0x202,
        ulong passMetadataHash = 0x301,
        ulong featureProfileHash = 0x401,
        ulong fixedFunctionStateHash = 0x501)
        => VulkanPipelinePrewarmDatabase.CreateGraphicsEntry(
            passIndex: 1,
            passName: "OpaqueForward",
            pipelineName: "DefaultPipeline",
            meshName,
            materialName,
            programName: "DefaultProgram",
            effectName: "DefaultEffect",
            PrimitiveTopology.TriangleList,
            useDynamicRendering: true,
            renderPassSignature: "dynamic:rgba16f:d32",
            colorAttachmentFormats: "R16G16B16A16Sfloat",
            depthAttachmentFormat: "D32Sfloat",
            programPipelineHash,
            vertexLayoutHash,
            descriptorLayoutHash,
            passMetadataHash,
            featureProfileHash,
            fixedFunctionStateHash,
            SampleCountFlags.Count1Bit,
            depthTestEnabled: true,
            blendEnabled: false,
            alphaToCoverageEnabled: false,
            ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            featureProfile: "Standard");

    private static string ReadWorkspaceFile(string relativePath)
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "XRENGINE.slnx")))
                return File.ReadAllText(Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar)));

            directory = Directory.GetParent(directory)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from test directory.");
    }
}
