using NUnit.Framework;
using Shouldly;
using Silk.NET.Vulkan;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanPipelineCompilationP05Tests
{
    [Test]
    public void BackgroundGraphicsCompilation_UsesIsolatedPersistentCacheAndCompileRequiredProbe()
    {
        string queue = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineCompileQueue.cs");
        string cache = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineCache.cs");

        queue.ShouldContain("pipelineCache: BackgroundPipelineCache");
        queue.ShouldNotContain("pipelineCache: ActivePipelineCache");
        queue.ShouldNotContain("pipelineCache: default");
        cache.ShouldContain("private PipelineCache _backgroundPipelineCache;");
        cache.ShouldContain("PublishVulkanBackgroundPipelineCache");
        cache.ShouldContain("MergePipelineCaches");
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
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
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
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineCompileQueue.cs");

        source.ShouldContain("activeJobCount >= capacity");
        source.ShouldContain("int capacity = workerCount;");
        source.ShouldContain("EVulkanPipelineTelemetryEvent.QueueRejected");
        source.ShouldContain("_vulkanGraphicsPipelineProgramCompileJobs.ContainsKey");
        source.ShouldContain("another cold pipeline for program");
        source.ShouldContain("VulkanPipelineCompileQuarantineSeconds");
        source.ShouldContain("[Vulkan][PipelineWatchdog]");
        source.ShouldContain("return 1;");
        source.ShouldContain("VulkanPipelineCompileTask.RunAsync");
        source.ShouldContain("VulkanPipelineCompileActivityGeneration");
        source.ShouldContain("pipelineCache: BackgroundPipelineCache");
        source.ShouldContain("PublishVulkanBackgroundPipelineCache(elapsedMs)");
        source.ShouldContain("_vulkanGraphicsPipelineCompileJobs.TryRemove(");
        source.ShouldContain("completedJob.Request.CompileKey");
        source.ShouldContain("StoreOrRetireSharedGraphicsPipeline(pipelineKey, result.Pipeline)");

        string pipelineCache = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineCache.cs");
        pipelineCache.ShouldContain("private PipelineCache _backgroundPipelineCache;");
        pipelineCache.ShouldContain("Api.CreatePipelineCache(");
        pipelineCache.ShouldContain("Api!.MergePipelineCaches(");
        pipelineCache.ShouldContain("Api!.DestroyPipelineCache(device, _backgroundPipelineCache");
        pipelineCache.ShouldContain("compileMilliseconds >= 1_000.0");

        string task = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineCompileTask.cs");
        task.ShouldContain("await compileGate.WaitAsync()");
        task.ShouldContain("TaskCompletionSource<T>");
        task.ShouldContain("Thread worker = new");
        task.ShouldContain("IsBackground = true");
        task.ShouldContain("ThreadPriority.BelowNormal");
        task.ShouldContain("TaskCreationOptions.RunContinuationsAsynchronously");
        task.ShouldNotContain("compileGate.Wait();");
    }

    [Test]
    public void ShaderAndProgramMutation_DrainNativePipelineCompilesBeforeDestroyingDependencies()
    {
        string queue = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineCompileQueue.cs");
        string shader = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkShader.cs");
        string program = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        string request = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Records/Classes/VkMeshRenderer.GraphicsPipelineBuildRequest.cs");
        string pipeline = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");
        string preparation = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Preparation.cs");
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");

        queue.ShouldContain("ExecuteWithVulkanPipelineCompilationQuiesced");
        queue.ShouldContain("_vulkanPipelineCompileDependencyMutationActive");
        queue.ShouldContain("_vulkanPipelineCompileDependencyGeneration");
        queue.ShouldContain(
            "request.DependencyGeneration !=");
        queue.ShouldContain("DrainVulkanPipelineCompileJobs(jobs, reason)");
        queue.ShouldContain("job.PublicationTask.Wait()");
        queue.ShouldContain("CaptureVulkanPipelineCompilationDependencies");
        request.ShouldContain(
            "public long DependencyGeneration { get; } = dependencyGeneration;");
        pipeline.ShouldContain(
            "IsVulkanPipelineCompileDependencyGenerationCurrent(");
        pipeline.ShouldContain(
            "Renderer.CaptureVulkanPipelineCompilationDependencies(");
        pipeline.ShouldContain(
            "stage.Module.Handle == 0 || stage.PName is null");
        preparation.ShouldContain(
            "if (!_program.Link(MeshRenderer?.GenerateAsync ?? false))");
        preparation.ShouldContain(
            "ObserveActiveProgramLinkGeneration(_program);");
        pipeline.ShouldContain(
            "_program.LinkGeneration,");
        recording.ShouldContain(
            "programHash.Add(draw.PreparedProgram?.LinkGeneration ?? 0UL);");

        int shaderBarrier = shader.IndexOf(
            "private void Invalidate()", StringComparison.Ordinal);
        int shaderRenderThreadDispatch = shader.IndexOf(
            "RuntimeEngine.InvokeOnMainThread(Invalidate", shaderBarrier, StringComparison.Ordinal);
        int shaderInvalidationBarrier = shader.IndexOf(
            "ExecuteWithVulkanPipelineCompilationQuiesced", shaderBarrier, StringComparison.Ordinal);
        int shaderInvalidationEvent = shader.IndexOf(
            "ShaderInvalidated?.Invoke(this)", shaderInvalidationBarrier, StringComparison.Ordinal);
        shaderBarrier.ShouldBeGreaterThanOrEqualTo(0);
        shaderRenderThreadDispatch.ShouldBeGreaterThan(shaderBarrier);
        shaderInvalidationBarrier.ShouldBeGreaterThan(shaderRenderThreadDispatch);
        shaderInvalidationBarrier.ShouldBeGreaterThan(shaderBarrier);
        shaderInvalidationEvent.ShouldBeGreaterThan(shaderInvalidationBarrier);

        int shaderDestroyBarrier = shader.IndexOf(
            "private void DestroyShaderResources()", StringComparison.Ordinal);
        int shaderDestroy = shader.IndexOf(
            "DestroyShaderModule", shaderDestroyBarrier, StringComparison.Ordinal);
        shaderDestroyBarrier.ShouldBeGreaterThanOrEqualTo(0);
        shaderDestroy.ShouldBeGreaterThan(shaderDestroyBarrier);

        int programBarrier = program.IndexOf(
            "private void BuildProgramInterface()", StringComparison.Ordinal);
        int programMutation = program.IndexOf(
            "ExecuteWithVulkanPipelineCompilationQuiesced", programBarrier, StringComparison.Ordinal);
        int layoutDestroy = program.IndexOf(
            "DestroyLayoutsAfterPipelineCompileDrain", programMutation, StringComparison.Ordinal);
        programBarrier.ShouldBeGreaterThanOrEqualTo(0);
        programMutation.ShouldBeGreaterThan(programBarrier);
        layoutDestroy.ShouldBeGreaterThan(programMutation);
    }

    [Test]
    public async Task AsyncCompileTask_NeverInvokesNativeCompileOnTheCallingThread()
    {
        using SemaphoreSlim compileGate = new(initialCount: 1, maxCount: 1);
        int callingThreadId = Environment.CurrentManagedThreadId;
        int compileThreadId = callingThreadId;

        int result = await VulkanPipelineCompileTask.RunAsync(
            compileGate,
            () =>
            {
                compileThreadId = Environment.CurrentManagedThreadId;
                return 42;
            });

        result.ShouldBe(42);
        compileThreadId.ShouldNotBe(callingThreadId);
        compileGate.CurrentCount.ShouldBe(1);
    }

    [Test]
    public void SharedPipelineLibraries_ReserveBeforeEnteringTheDriver()
    {
        string cache = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanGraphicsPipelineLibraryCache.cs");
        string pipeline = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");

        cache.ShouldContain("_sharedGraphicsPipelineLibraryCreations.Add(key)");
        cache.ShouldContain("TryGetOrReserveSharedGraphicsPipelineLibrary");
        cache.ShouldContain("CompleteSharedGraphicsPipelineLibraryCreation");
        cache.ShouldContain("CancelSharedGraphicsPipelineLibraryCreation");

        int reserve = pipeline.IndexOf(
            "TryGetOrReserveSharedGraphicsPipelineLibrary(", StringComparison.Ordinal);
        int create = pipeline.IndexOf(
            "Renderer.CreateGraphicsPipelineWithCachePolicy(", reserve, StringComparison.Ordinal);
        reserve.ShouldBeGreaterThanOrEqualTo(0);
        create.ShouldBeGreaterThan(reserve);
        pipeline.ShouldContain("VulkanPipelineCompilationDeferredException");
    }

    [Test]
    public void EveryMeshRecordingPath_PrewarmsBeforeBeginningCommandRecording()
    {
        string secondarySource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.SecondaryCommandBuffers.cs");
        int methodStart = secondarySource.IndexOf("private void RecordScheduledMeshCommandChainWorker", StringComparison.Ordinal);
        int methodEnd = secondarySource.IndexOf("private bool TryRecordSecondaryBucket", methodStart, StringComparison.Ordinal);
        string method = secondarySource[methodStart..methodEnd];

        int begin = method.IndexOf("Api.BeginCommandBuffer(secondary", StringComparison.Ordinal);
        begin.ShouldBeGreaterThanOrEqualTo(0);
        method.ShouldContain("materialization is deliberately owned by the render thread before");
        method.ShouldNotContain("TryPrewarmGraphicsPipelinesForRecording");
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
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        int primaryMethodStart = primarySource.IndexOf("private bool TryRecordCommandBuffer", StringComparison.Ordinal);
        int primaryMethodEnd = primarySource.IndexOf("internal static bool ShouldRefreshUnwrittenSwapchainForPresent", primaryMethodStart, StringComparison.Ordinal);
        string primaryMethod = primarySource[primaryMethodStart..primaryMethodEnd];
        int primaryPrewarm = primaryMethod.IndexOf("TryPrewarmGraphicsPipelinesForRecording", StringComparison.Ordinal);
        int primaryBegin = primaryMethod.IndexOf("Api!.BeginCommandBuffer(commandBuffer", StringComparison.Ordinal);
        primaryPrewarm.ShouldBeGreaterThanOrEqualTo(0);
        primaryBegin.ShouldBeGreaterThan(primaryPrewarm);
        primaryMethod.ShouldContain("Graphics pipeline prewarm deferred before vkBeginCommandBuffer");

        string pipeline = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");
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
        => SourceContractWorkspace.ReadFile(relativePath);
}
