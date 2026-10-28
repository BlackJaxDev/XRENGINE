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
        // The pipeline manager owns the cache fields. The compile queue passes
        // the isolated background cache to every worker compile.
        string queue = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineCompileQueue.cs");
        string manager = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineManager.cs");
        string cache = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineCache.cs");

        queue.ShouldContain(
            "() => CreateGraphicsPipelineOnWorker(\n" +
            "                        request,\n" +
            "                        BackgroundPipelineCache),");
        queue.ShouldContain("() => CreateComputePipelineOnWorker(request, BackgroundPipelineCache),");
        queue.ShouldNotContain("ActivePipelineCache");
        queue.ShouldNotContain("pipelineCache: default");
        manager.ShouldContain("internal PipelineCache _backgroundPipelineCache;");
        manager.ShouldContain(
            "Pipeline pipeline = CreateGraphicsPipelineFromRequest(\n" +
            "                request,\n" +
            "                backgroundPipelineCache,\n" +
            "                backgroundCompile: true);");
        manager.ShouldContain("PublishBackgroundPipelineCache(elapsedMs);");
        cache.ShouldContain("internal void PublishBackgroundPipelineCache(double compileMilliseconds)");
        cache.ShouldContain("MergePipelineCaches");
        cache.ShouldContain("VulkanPipelineFailOnCompileRequiredFlag");
        cache.ShouldContain("VulkanPipelineCompileRequiredResult");
        cache.ShouldContain("EVulkanPipelineTelemetryEvent.CompileRequired");
        cache.ShouldContain("EVulkanDriverPipelineCacheOutcome.PersistedHit");
        cache.ShouldContain("EVulkanDriverPipelineCacheOutcome.RuntimeHit");
        // Each cache probe publishes telemetry. A log line for each probe
        // serialized dense cold views, so the probe does not log.
        cache.ShouldContain("EVulkanPipelineTelemetryEvent.CreationCompleted,");
        cache.ShouldNotContain("cache probe completed");
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
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.Compute.cs");
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
        string manager = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineManager.cs");

        // The bounded backlog is a small multiple of the worker count. A
        // foreground-required compile is not rejected at capacity.
        source.ShouldContain("activeJobCount >= capacity");
        source.ShouldContain("int capacity = Math.Clamp(workerCount * 8, 8, 64);");
        source.ShouldContain("if (activeJobCount >= capacity && !foregroundRequired)");
        source.ShouldContain("EVulkanPipelineTelemetryEvent.QueueRejected");
        source.ShouldContain("_vulkanGraphicsPipelineProgramCompileJobs.TryGetValue(");
        source.ShouldContain("another cold pipeline for program");
        source.ShouldContain("VulkanPipelineCompileQuarantineSeconds");
        source.ShouldContain("[Vulkan][PipelineWatchdog]");
        source.ShouldContain("return 1;");
        source.ShouldContain("task = compileTask.Enqueue(");
        source.ShouldContain("Interlocked.Increment(ref _vulkanPipelineCompileActivityGeneration);");
        source.ShouldContain("BackgroundPipelineCache),");
        source.ShouldContain("manager.PublishCompletedGraphicsPipelineCompile(completedJob);");
        manager.ShouldContain("PublishBackgroundPipelineCache(elapsedMs);");
        manager.ShouldContain(
            "_vulkanGraphicsPipelineCompileJobs.TryRemove(\n" +
            "                completedJob.Request.CompileKey,\n" +
            "                out _);");
        manager.ShouldContain(
            "Pipeline published = StoreSharedGraphicsPipeline(\n" +
            "                        completedJob.Request.Key,\n" +
            "                        result.Pipeline);");
        manager.ShouldContain("_supersededSharedGraphicsPipelines.Enqueue(result.Pipeline);");

        string pipelineCache = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineCache.cs");
        manager.ShouldContain("internal PipelineCache _backgroundPipelineCache;");
        pipelineCache.ShouldContain("RequireApi().CreatePipelineCache(");
        pipelineCache.ShouldContain("CreateNativePipelineCache(initialData, out _backgroundPipelineCache)");
        pipelineCache.ShouldContain("RequireApi().MergePipelineCaches(");
        pipelineCache.ShouldContain("RequireApi().DestroyPipelineCache(RequireDeviceContext().Device, _backgroundPipelineCache, null);");
        pipelineCache.ShouldContain("compileMilliseconds >= 1_000.0");

        string task = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineCompileTask.cs");
        task.ShouldContain("internal Task<T> Enqueue<T>(\n        Func<T> compile,\n        bool foregroundRequired,");
        task.ShouldContain("TaskCompletionSource<T>");
        task.ShouldContain("_worker = new Thread(WorkerMain)");
        task.ShouldContain("IsBackground = true");
        task.ShouldContain("ThreadPriority.BelowNormal");
        task.ShouldContain("TaskCreationOptions.RunContinuationsAsynchronously");
        task.ShouldContain("_queuedWork.Wait();");
        task.ShouldContain("if (!TryDequeue(out WorkItem work))");
    }

    [Test]
    public void ShaderAndProgramMutation_DrainNativePipelineCompilesBeforeDestroyingDependencies()
    {
        // The pipeline manager owns the mutation and dependency leases. The
        // program wrapper port gives shaders and programs one entry point.
        string queue = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineCompileQueue.cs");
        string port = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VulkanProgramWrapperPort.cs");
        string factory = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanGraphicsPipelineFactory.cs");
        string shader = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkShader.cs");
        string program = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.Layouts.cs");
        string request = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Pipelines/VkMeshRenderer.GraphicsPipelineBuildRequest.cs");
        string pipeline = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");
        string preparation = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Preparation.cs");
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Reuse/Dependencies/VulkanRenderer.CommandBufferReuse.Dependencies.cs");

        queue.ShouldContain("internal VulkanPipelineCompilationMutationLease AcquireCompilationMutationLease(");
        queue.ShouldContain("_vulkanPipelineCompileDependencyMutationActive");
        queue.ShouldContain("_vulkanPipelineCompileDependencyGeneration");
        queue.ShouldContain(
            "request.DependencyGeneration !=");
        queue.ShouldContain("DrainPipelineCompileJobs(jobs, reason);");
        queue.ShouldContain("job.PublicationTask.Wait();");
        queue.ShouldContain("internal VulkanPipelineCompilationDependencyLease AcquireCompilationDependencyLease()");
        port.ShouldContain("internal void ExecuteWithPipelineCompilationQuiesced(");
        port.ShouldContain("VulkanPipelineCompilationMutationScope.ForShader(shaderModule));");
        port.ShouldContain("VulkanPipelineCompilationMutationScope.ForProgram(program));");
        request.ShouldContain(
            "public long DependencyGeneration { get; } = dependencyGeneration;");
        factory.ShouldContain(
            "if (!manager.IsCompilationDependencyGenerationCurrent(\n" +
            "\t\t\t\trequest.DependencyGeneration))");
        pipeline.ShouldContain(
            "BackendContext.Resources.PipelineManager.AcquireCompilationDependencyLease();");
        pipeline.ShouldContain(
            "stage.Module.Handle == 0 || stage.PName is null");
        preparation.ShouldContain(
            "if (!_program.Link(MeshRenderer?.GenerateAsync ?? false))");
        preparation.ShouldContain(
            "ObserveActiveProgramLinkGeneration(_program, replacingSameInterface);");
        preparation.ShouldContain(
            "if (_program.LinkGeneration != preparedProgramLinkGeneration)");
        recording.ShouldContain(
            "programHash.Add(draw.PreparedProgram?.LinkGeneration ?? 0UL);");

        int shaderBarrier = shader.IndexOf(
            "private void Invalidate()", StringComparison.Ordinal);
        int shaderFrameSwapDispatch = shader.IndexOf(
            "Scheduling.EnqueueFrameSwapTask(", shaderBarrier, StringComparison.Ordinal);
        int shaderInvalidationBarrier = shader.IndexOf(
            "ExecuteWithPipelineCompilationQuiesced", shaderBarrier, StringComparison.Ordinal);
        int shaderInvalidationEvent = shader.IndexOf(
            "ShaderInvalidated?.Invoke(this)", shaderInvalidationBarrier, StringComparison.Ordinal);
        shaderBarrier.ShouldBeGreaterThanOrEqualTo(0);
        shaderFrameSwapDispatch.ShouldBeGreaterThan(shaderBarrier);
        shaderInvalidationBarrier.ShouldBeGreaterThan(shaderFrameSwapDispatch);
        shaderInvalidationBarrier.ShouldBeGreaterThan(shaderBarrier);
        shaderInvalidationEvent.ShouldBeGreaterThan(shaderInvalidationBarrier);

        int shaderDestroyBarrier = shader.IndexOf(
            "private void DestroyShaderResources()", StringComparison.Ordinal);
        int shaderDestroyQuiesce = shader.IndexOf(
            "ExecuteWithPipelineCompilationQuiesced(", shaderDestroyBarrier, StringComparison.Ordinal);
        int shaderDestroy = shader.IndexOf(
            "DestroyShaderModule", shaderDestroyQuiesce, StringComparison.Ordinal);
        shaderDestroyBarrier.ShouldBeGreaterThanOrEqualTo(0);
        shaderDestroyQuiesce.ShouldBeGreaterThan(shaderDestroyBarrier);
        shaderDestroy.ShouldBeGreaterThan(shaderDestroyQuiesce);

        int programBarrier = program.IndexOf(
            "private void DestroyLayouts()", StringComparison.Ordinal);
        int programMutation = program.IndexOf(
            "AcquirePipelineCompilationMutationLease(", programBarrier, StringComparison.Ordinal);
        int layoutDestroy = program.IndexOf(
            "DestroyLayoutsAfterPipelineCompileDrain", programMutation, StringComparison.Ordinal);
        programBarrier.ShouldBeGreaterThanOrEqualTo(0);
        programMutation.ShouldBeGreaterThan(programBarrier);
        layoutDestroy.ShouldBeGreaterThan(programMutation);
    }

    [Test]
    public async Task AsyncCompileTask_NeverInvokesNativeCompileOnTheCallingThread()
    {
        using VulkanPipelineCompileTask compileTask = new();
        int callingThreadId = Environment.CurrentManagedThreadId;
        int compileThreadId = callingThreadId;

        int result = await compileTask.Enqueue(
            () =>
            {
                compileThreadId = Environment.CurrentManagedThreadId;
                return 42;
            },
            foregroundRequired: false,
            out _);

        result.ShouldBe(42);
        compileThreadId.ShouldNotBe(callingThreadId);
    }

    [Test]
    public void SharedPipelineLibraries_ReserveBeforeEnteringTheDriver()
    {
        string manager = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanPipelineManager.cs");
        string pipeline = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Pipelines/VulkanGraphicsPipelineFactory.cs");

        manager.ShouldContain("_sharedGraphicsPipelineLibraryCreations.Add(key)");
        manager.ShouldContain("TryGetOrReserveSharedGraphicsPipelineLibrary");
        manager.ShouldContain("CompleteSharedGraphicsPipelineLibraryCreation");
        manager.ShouldContain("CancelSharedGraphicsPipelineLibraryCreation");

        int reserve = pipeline.IndexOf(
            "manager.TryGetOrReserveSharedGraphicsPipelineLibrary(", StringComparison.Ordinal);
        reserve.ShouldBeGreaterThanOrEqualTo(0);
        int create = pipeline.IndexOf(
            "manager.CreateGraphicsPipelineWithCachePolicy(", reserve, StringComparison.Ordinal);
        int complete = pipeline.IndexOf(
            "manager.CompleteSharedGraphicsPipelineLibraryCreation(key, library);", create, StringComparison.Ordinal);
        int cancel = pipeline.IndexOf(
            "manager.CancelSharedGraphicsPipelineLibraryCreation(key);", complete, StringComparison.Ordinal);
        create.ShouldBeGreaterThan(reserve);
        complete.ShouldBeGreaterThan(create);
        cancel.ShouldBeGreaterThan(complete);
        pipeline.ShouldContain("VulkanPipelineCompilationDeferredException");
    }

    [Test]
    public void EveryMeshRecordingPath_PrewarmsBeforeBeginningCommandRecording()
    {
        // Workers record only prepared draw state. The render thread admits
        // graphics pipelines before it begins the primary or the dynamic-UI
        // secondary command buffer.
        string workerSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.PreparedWorkerRecording.cs");
        string method = Slice(
            workerSource,
            "internal unsafe void RecordPreparedMeshCommandChain(",
            "private static ref readonly VkPreparedMeshDraw GetPreparedCommandChainDraw(");

        int begin = method.IndexOf("BeginTrackedCommandBuffer(", StringComparison.Ordinal);
        int record = method.IndexOf("VkMeshRenderer.RecordPreparedMeshDrawState(", StringComparison.Ordinal);
        begin.ShouldBeGreaterThanOrEqualTo(0);
        record.ShouldBeGreaterThan(begin);
        method.ShouldContain("A prewarmed Vulkan command-chain draw became unavailable during secondary recording.");
        method.ShouldNotContain("TryPrewarmGraphicsPipelinesForRecording");
        method.ShouldContain("chain.State = CommandChainState.NotReady;");
        method.ShouldContain("chain.DirtyReason |= CommandChainDirtyReason.PipelineGeneration;");

        string secondarySource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Secondary/VulkanRenderer.SecondaryCommandBuffers.cs");
        string dynamicUiMethod = Slice(
            secondarySource,
            "private unsafe bool RecordDynamicUiBatchTextSecondaryCommandBuffer(",
            "internal static ulong ComputeCommandChainUniformSlotSignature(");
        int dynamicUiPrewarm = dynamicUiMethod.IndexOf("TryPrewarmGraphicsPipelinesForRecording", StringComparison.Ordinal);
        int dynamicUiBegin = dynamicUiMethod.IndexOf("BeginTrackedCommandBuffer(", StringComparison.Ordinal);
        dynamicUiPrewarm.ShouldBeGreaterThanOrEqualTo(0);
        dynamicUiBegin.ShouldBeGreaterThan(dynamicUiPrewarm);
        dynamicUiMethod.ShouldContain("Dynamic-UI secondary recording deferred before vkBeginCommandBuffer");

        string lifecycleSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.cs");
        string lifecycle = Slice(
            lifecycleSource,
            "private bool RecordCommandBufferLifecycle(",
            "internal static bool ShouldRefreshUnwrittenSwapchainForPresent(");
        int primaryPrepare = lifecycle.IndexOf("TryPreparePrimaryFrameData(", StringComparison.Ordinal);
        int primaryEncoding = lifecycle.IndexOf("PreparePrimaryCommandEncoding(ref recordingState);", StringComparison.Ordinal);
        primaryPrepare.ShouldBeGreaterThanOrEqualTo(0);
        primaryEncoding.ShouldBeGreaterThan(primaryPrepare);

        string preparationSource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/Recording/Primary/VulkanRenderer.CommandBufferRecording.Primary.Preparation.cs");
        string frameData = Slice(
            preparationSource,
            "private bool TryPreparePrimaryFrameData(",
            "frameDataManifest = recordingScratch.MeshFrameDataManifest;\n            if (!TryAdmitPrimaryGraphicsPipelines(");
        frameData.ShouldContain("ResourceRuntime.PipelineManager.GetOrBuildVariantManifest(");
        string requirement = Slice(
            preparationSource,
            "private bool TryPreparePrimaryPipelineRequirement(",
            "private bool TryPreparePrimaryMeshTaskPipelineRequirement(");
        requirement.ShouldContain("meshRenderer.TryPrewarmGraphicsPipelinesForRecording(");
        string encoding = Slice(
            preparationSource,
            "private void PreparePrimaryCommandEncoding(",
            "recordingState.InitialPrimaryContextOperationIndex = -1;");
        encoding.ShouldContain("ResetAndBeginPrimaryCommandBuffer(ref recordingState);");
        preparationSource.ShouldContain("Graphics pipeline compilation is still pending before vkBeginCommandBuffer.");

        string pipeline = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");
        pipeline.ShouldNotContain("materializeKnownWarmCache");
        pipeline.ShouldContain("BackendContext.Resources.PipelineManager.TryEnqueueGraphicsPipelineCompile(");
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

    /// <summary>Returns the source text from the start marker up to the end marker.</summary>
    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Missing start marker '{startMarker}'.");
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start, $"Missing end marker '{endMarker}'.");
        return source[start..end];
    }
}
