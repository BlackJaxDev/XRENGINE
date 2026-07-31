using NUnit.Framework;
using Shouldly;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials.Textures;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanStablePacketAndDescriptorTests
{
    [Test]
    public void StableMeshPackets_StartAtTenDrawsAndRemainBounded()
    {
        VulkanRenderer.MinMeshDrawsPerRenderPacket.ShouldBe(10);
        VulkanRenderer.MaxMeshDrawsPerRenderPacket.ShouldBeGreaterThanOrEqualTo(
            VulkanRenderer.MinMeshDrawsPerRenderPacket);
    }

    [Test]
    public void CommandChainContainers_RebuildWithoutSteadyStateAllocations()
    {
        const int drawCount = VulkanRenderer.MaxMeshDrawsPerRenderPacket;
        const string targetName = "SteadyTarget";
        RenderViewKey viewKey = new(1, 2, 0, RenderViewKind.Main, 0, -1);
        DrawPacket[] draws = new DrawPacket[drawCount];
        for (int i = 0; i < draws.Length; i++)
        {
            draws[i] = new DrawPacket(
                i,
                RendererIdentity: 3,
                MeshIdentity: i + 4,
                MaterialIdentity: 5,
                ProgramIdentity: 6,
                InstanceCount: 1,
                Transparent: false,
                StructuralSignature: (ulong)(i + 7),
                FrameDataSignature: (ulong)(i + 8));
        }

        CommandChainKey[] chainKeys = new CommandChainKey[drawCount];
        for (int i = 0; i < chainKeys.Length; i++)
            chainKeys[i] = new CommandChainKey(0, viewKey, 9, 10, false, i);

        RenderPacket packet = new();
        RenderPassChainGroup group = new();
        CommandChainSchedule schedule = new();
        RenderPassChainGroup[] groups = [group];
        DescriptorBindingSnapshot descriptors = new(11, 3, 12);
        ResourcePlanSnapshot resources = new(13, 14, 15, 16);

        ResetContainers();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1_000; iteration++)
            ResetContainers();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0);
        packet.DrawCount.ShouldBe(drawCount);
        packet.GetDraw(drawCount - 1).OpIndex.ShouldBe(drawCount - 1);
        group.ChainKeys.Length.ShouldBe(drawCount);
        schedule.Groups.Length.ShouldBe(1);

        void ResetContainers()
        {
            packet.Reset(
                viewKey,
                passIndex: 9,
                targetIdentity: 10,
                targetName,
                RenderPacketVolatility.FrameDataOnly,
                draws,
                ReadOnlySpan<DispatchPacket>.Empty,
                descriptors,
                resources,
                structuralSignature: 17,
                frameDataSignature: 18,
                sourceStartIndex: 0,
                sourceCount: drawCount,
                dynamicOverlay: false);
            group.Reset(9, 10, targetName, chainKeys, 17, supportsSecondaryCommandBuffers: true, dynamicOverlay: false);
            schedule.Reset(17, 13, groups);
        }
    }

    [Test]
    public void BindingSnapshot_ResetReusesDictionaryStorageWithoutAllocating()
    {
        ComputeDispatchSnapshot snapshot = new();
        Dictionary<string, ProgramUniformValue> uniforms =
            new(StringComparer.Ordinal)
            {
                ["FrameValue"] = default,
            };
        Dictionary<uint, XRTexture> samplers = [];
        Dictionary<uint, string> samplerNames = [];
        Dictionary<string, XRTexture> samplersByName = new(StringComparer.Ordinal);
        Dictionary<uint, ProgramImageBinding> images = [];

        snapshot.Reset(uniforms, samplers, samplerNames, samplersByName, images);
        Dictionary<string, ProgramUniformValue> uniformStorage = snapshot.Uniforms;

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1_000; iteration++)
            snapshot.Reset(uniforms, samplers, samplerNames, samplersByName, images);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0);
        snapshot.Uniforms.ShouldBeSameAs(uniformStorage);
        snapshot.Uniforms.ContainsKey("FrameValue").ShouldBeTrue();
    }

    [Test]
    public void BindingSnapshot_MaterialPayloadIsReleasedWhenFrameContentChanges()
    {
        ComputeDispatchSnapshot snapshot = new();
        MaterialUniformBindingPayload payload = new(
            new Dictionary<string, ProgramUniformValue>(StringComparer.Ordinal)
            {
                ["BaseColor"] = default,
            });
        snapshot.SetMaterialUniformBindings(payload);

        snapshot.MaterialUniformBindings.ShouldBeSameAs(payload);

        snapshot.Reset(
            new Dictionary<string, ProgramUniformValue>(StringComparer.Ordinal),
            [],
            [],
            new Dictionary<string, XRTexture>(StringComparer.Ordinal),
            []);

        snapshot.MaterialUniformBindings.ShouldBeNull();
    }

    [Test]
    public void BindingSnapshot_RuntimeUniformNameSignatureTracksTopologyNotValues()
    {
        ComputeDispatchSnapshot snapshot = new();
        Dictionary<string, ProgramUniformValue> first =
            new(StringComparer.Ordinal)
            {
                ["ScopedValue"] = default,
            };
        snapshot.Reset(first, [], [], new Dictionary<string, XRTexture>(StringComparer.Ordinal), []);
        snapshot.PublishBindingLayoutSignatures();
        ulong baseline = snapshot.RuntimeUniformNameSignature;

        first["ScopedValue"] = new ProgramUniformValue(EShaderVarType._float, 42.0f);
        snapshot.Reset(first, [], [], new Dictionary<string, XRTexture>(StringComparer.Ordinal), []);
        snapshot.PublishBindingLayoutSignatures();
        snapshot.RuntimeUniformNameSignature.ShouldBe(baseline);

        first["AnotherScopedValue"] = default;
        snapshot.Reset(first, [], [], new Dictionary<string, XRTexture>(StringComparer.Ordinal), []);
        snapshot.PublishBindingLayoutSignatures();
        snapshot.RuntimeUniformNameSignature.ShouldNotBe(baseline);
    }

    [Test]
    public void CapturedDescriptorAllocation_AlwaysKeepsItsResourceFingerprintVariant()
    {
        const ulong resourceFingerprint = 0x123456789ABCDEF0UL;

        VkMeshRenderer.ResolveDescriptorAllocationResourceVariantFingerprint(
            allActiveSetsUpdateAfterBind: true,
            hasCapturedBindingSnapshot: true,
            resourceFingerprint).ShouldBe(resourceFingerprint);
        VkMeshRenderer.ResolveDescriptorAllocationResourceVariantFingerprint(
            allActiveSetsUpdateAfterBind: false,
            hasCapturedBindingSnapshot: true,
            resourceFingerprint).ShouldBe(resourceFingerprint);
        VkMeshRenderer.ResolveDescriptorAllocationResourceVariantFingerprint(
            allActiveSetsUpdateAfterBind: false,
            hasCapturedBindingSnapshot: false,
            resourceFingerprint).ShouldBe(resourceFingerprint);
        VkMeshRenderer.ResolveDescriptorAllocationResourceVariantFingerprint(
            allActiveSetsUpdateAfterBind: true,
            hasCapturedBindingSnapshot: false,
            resourceFingerprint).ShouldBe(0UL);
    }

    [Test]
    public void PublishedDescriptorSnapshots_DriveExactSamplingTransitions()
    {
        string drawing = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");

        drawing.ShouldContain("TryTransitionPreparedDescriptorImagesForSampling(");
        drawing.ShouldContain("TransitionPublishedDescriptorSetImagesForSampling(");
        recording.ShouldContain("_vulkanPublishedDescriptorSets.TryGetValue(");
        AssertOrdered(
            recording,
            "VulkanPublishedDescriptorImageReference published = snapshot.ImageReferences[i];",
            "TransitionDescriptorImageForSampling(commandBuffer, published.Reference.View, published.Reference.Layout, target);");
    }

    [Test]
    public void BindingSnapshot_NamedSamplerLookupUsesCapturedDictionary()
    {
        ComputeDispatchSnapshot snapshot = new();
        XRTexture2D texture = new();
        snapshot.SamplersByName["LightingTexture"] = texture;

        snapshot.TryGetSamplerTexture("LightingTexture", out XRTexture? resolved).ShouldBeTrue();
        resolved.ShouldBeSameAs(texture);

        snapshot.TryGetSamplerTexture("MissingTexture", out resolved).ShouldBeFalse();
        resolved.ShouldBeNull();
        snapshot.TryGetSamplerTexture(string.Empty, out resolved).ShouldBeFalse();
        resolved.ShouldBeNull();
    }

    [Test]
    public void FrameOpSignatureHasher_ReusesStableStringSignatureWithoutAllocating()
    {
        string value = string.Concat("Prepared", "Program", "Identity");
        FrameOpSignatureHasher warm = new();
        warm.Add(value);
        ulong expected = warm.ToHash();

        ulong actual = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1_000; iteration++)
        {
            FrameOpSignatureHasher hash = new();
            hash.Add(value);
            actual = hash.ToHash();
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        actual.ShouldBe(expected);
        allocated.ShouldBe(0);
        string equivalentValue = new(value.ToCharArray());
        FrameOpSignatureHasher equivalent = new();
        equivalent.Add(equivalentValue);
        equivalent.ToHash().ShouldBe(expected);
    }

    [Test]
    public void ProgramBindingCapture_IsAtomicAcrossUniformCallbacks()
    {
        string program = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        string capture = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.BindingCapture.cs");
        string meshRenderer = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        program.ShouldContain("internal BindingUpdateScope BeginBindingUpdate()");
        program.ShouldContain("TryResolveBindingWriteState(out BindingCaptureState? capture)");
        program.ShouldContain("TryGetActiveBindingCaptureState(out BindingCaptureState capture)");
        program.ShouldContain("private void ClearBindingsNoLock()");
        program.ShouldContain("private ComputeDispatchSnapshot CaptureComputeSnapshotNoLock()");
        program.ShouldContain("private bool HasBoundDescriptorResourcesNoLock()");
        program.ShouldContain("private void SetSamplerNoLock(");
        capture.ShouldNotContain("[ThreadStatic]");
        capture.ShouldContain("ThreadLocal<BindingCaptureWorkspace>");
        capture.ShouldContain("ReferenceEquals(state.Owner, this)");
        capture.ShouldContain("private sealed class BindingCaptureState");
        capture.ShouldContain("internal ComputeDispatchSnapshot? RentFrameSnapshot()");
        capture.ShouldNotContain("Monitor.Enter");
        meshRenderer.ShouldContain("using VkRenderProgram.BindingUpdateScope bindingUpdate = program.BeginBindingUpdate();");
    }

    [Test]
    public void ReusableFrameDataRefresh_UsesPrivateBindingCaptureForSnapshotlessDraw()
    {
        string drawing = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Drawing.cs");

        AssertOrdered(
            drawing,
            "draw.ProgramBindingSnapshot is null",
            "using VkRenderProgram.BindingUpdateScope bindingUpdate = activeProgram.BeginBindingUpdate();",
            "TryRefreshReusableCommandBufferFrameDataBindingsNoLock(",
            "NotifyDrawUniforms(material, programData, draw)",
            "UpdateAutoUniformBuffersForDraw(frameIndex, drawUniformSlot, material, draw)");
    }

    [Test]
    public void StableDeformationBuffers_BypassBufferStateLock()
    {
        string buffers = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Buffers.cs");
        int methodStart = buffers.IndexOf("private void EnsureRuntimeDeformationBuffersCurrent()", StringComparison.Ordinal);
        int methodEnd = buffers.IndexOf("/// <summary>", methodStart, StringComparison.Ordinal);
        methodStart.ShouldBeGreaterThanOrEqualTo(0);
        methodEnd.ShouldBeGreaterThan(methodStart);
        string method = buffers[methodStart..methodEnd];
        method.ShouldContain("if (!RuntimeDeformationBufferReferencesChanged())");
        method.IndexOf("if (!RuntimeDeformationBufferReferencesChanged())", StringComparison.Ordinal)
            .ShouldBeLessThan(method.IndexOf("lock (_bufferStateSync)", StringComparison.Ordinal));
    }

    [Test]
    public void BindingSnapshots_AreFramePooledAndOutOfFrameCapturesKeepOwnership()
    {
        string program = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        string snapshot = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Records/ComputeDispatchSnapshot.cs");
        string uniformArrayPool = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.FrameUniformArrayPool.cs");

        program.ShouldContain("RentFrameBindingSnapshot()");
        program.ShouldContain("RuntimeRenderingHostServices.FrameTiming.CurrentRenderPipelineContext");
        program.ShouldContain("RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId");
        program.ShouldContain("if (frameId == 0)");
        program.ShouldContain("_frameBindingSnapshotPoolCursor = 0;");
        program.ShouldNotContain("new Dictionary<string, ProgramUniformValue>(_uniformValues");
        program.ShouldNotContain("value.ToArray(), true");
        program.ShouldNotContain("value.Select(q =>");
        snapshot.ShouldContain("destination.EnsureCapacity(source.Count);");
        uniformArrayPool.ShouldContain("private sealed class FrameUniformArrayPool<T>");
        uniformArrayPool.ShouldContain("RuntimeRenderingHostServices.FrameTiming.CurrentRenderFrameId");
        uniformArrayPool.ShouldContain("values.CopyTo(snapshot);");
    }

    [Test]
    public void MeshDrawOp_ResetReusesTheLargeCapturedDrawStorageWithoutAllocating()
    {
        PendingMeshDraw firstDraw = default(PendingMeshDraw) with { Instances = 1u };
        PendingMeshDraw secondDraw = default(PendingMeshDraw) with { Instances = 2u };
        FrameOpContext context = default;
        MeshDrawOp op = new(1, null, firstDraw, context);
        ref readonly PendingMeshDraw drawRef = ref op.DrawRef;

        op.Reset(2, null, secondDraw, context, preserveSubmissionOrder: true);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1_000; iteration++)
            op.Reset(3, null, secondDraw, context, preserveSubmissionOrder: false);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0);
        op.PassIndex.ShouldBe(3);
        op.PreserveSubmissionOrder.ShouldBeFalse();
        drawRef.Instances.ShouldBe(2u);
    }

    [Test]
    public void DefaultPipelineFrameOps_ReuseSharedFrameBoundedStorage()
    {
        string frameOp = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Records/FrameOp.cs");
        string clearOp = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Records/ClearOp.cs");
        string computeOp = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/ComputeDispatchOp.cs");
        string barrierOp = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Records/MemoryBarrierOp.cs");
        string initialization = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.Initialization.cs");
        string frameOpApi = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/FrameOpApi.cs");
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string meshRenderer = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");
        string graphCompiler = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderGraphCompiler.cs");
        string openXr = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/OpenXR/VulkanRenderer.OpenXR.cs");
        string blitOp = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Records/BlitOp.cs");
        string publishOp = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Records/PublishFramebufferForSamplingOp.cs");

        frameOp.ShouldContain("protected static bool TryRentForCurrentFrame<T>");
        frameOp.ShouldContain("private static class FramePool<T>");
        frameOp.ShouldContain("public int PassIndex { get; internal set; }");
        frameOp.ShouldContain("public XRFrameBuffer? Target { get; internal set; }");
        frameOp.ShouldContain("public FrameOpContext Context { get; internal set; }");
        clearOp.ShouldContain("internal static ClearOp Rent(");
        computeOp.ShouldContain("internal static ComputeDispatchOp Rent(");
        barrierOp.ShouldContain("internal static MemoryBarrierOp Rent(");
        initialization.ShouldContain("EnqueueFrameOp(ComputeDispatchOp.Rent(");
        initialization.ShouldContain("EnqueueFrameOp(ClearOp.Rent(");
        frameOpApi.ShouldContain("EnqueueFrameOp(MemoryBarrierOp.Rent(");
        recording.ShouldNotContain("clear with { ClearColor = false }");
        meshRenderer.ShouldContain("op.PassIndex = validatedPassIndex;");
        meshRenderer.ShouldNotContain("with { PassIndex = validatedPassIndex }");
        graphCompiler.ShouldContain("ops[i].Context = firstSwapchainContext.Value;");
        openXr.ShouldContain("capturedOp.Context = context;");
        openXr.ShouldContain("op.Target = target;");
        openXr.ShouldNotContain("capturedOp with { Context = context }");
        openXr.ShouldNotContain("with { Target = target }");
        blitOp.ShouldContain("public XRFrameBuffer? InFbo { get; internal set; }");
        blitOp.ShouldContain("public XRFrameBuffer? OutFbo { get; internal set; }");
        publishOp.ShouldContain("public XRFrameBuffer FrameBuffer { get; internal set; }");
    }

    [Test]
    public void DefaultPipelineDescriptorAndPlannerScopes_ReuseScratchStorage()
    {
        string material = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Materials/VkMaterial.cs");
        string planner = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderer.ResourcePlannerState.cs");
        string program = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Programs/VkRenderProgram.cs");
        string frameOutputs = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.FrameOutputs.cs");
        string resourceAllocator = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/VulkanResourceAllocator.cs");
        string meshRenderer = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");

        material.ShouldContain("private sealed class DescriptorUpdateScratch");
        material.ShouldContain("Span<WriteDescriptorSet> writeSpan = CollectionsMarshal.AsSpan(writes);");
        material.ShouldContain("ReturnDescriptorUpdateScratch(scratch);");
        material.ShouldNotContain("WriteDescriptorSet[] writeArray =");
        material.ShouldContain("static (renderProgram, samplerName) =>");
        planner.ShouldContain("PooledExternalResourcePlannerReadbackScope.Rent(this, context)");
        planner.ShouldContain("private sealed class PooledExternalResourcePlannerReadbackScope");
        frameOutputs.ShouldContain("private static readonly Stack<OutputAccumulator> OutputAccumulatorPool");
        frameOutputs.ShouldContain("OutputAccumulatorPool.Push(output);");
        frameOutputs.ShouldContain("? OutputAccumulatorPool.Pop()");
        frameOutputs.ShouldContain("output.Reset(");
        resourceAllocator.ShouldContain("Dictionary<VulkanAliasGroupKey, VulkanPhysicalImageGroup>.ValueCollection EnumeratePhysicalGroups()");
        resourceAllocator.ShouldNotContain("IEnumerable<VulkanPhysicalImageGroup> EnumeratePhysicalGroups()");
        meshRenderer.ShouldContain("HashUniformValue(ref item, pair.Value);");
        meshRenderer.ShouldNotContain("HashUniformValue(ref item, pair.Value.Value);");

        int linkedFastPath = program.IndexOf("if (IsLinked &&", StringComparison.Ordinal);
        int stopwatchStart = program.IndexOf(
            "Stopwatch buildWatch = global::System.Diagnostics.Stopwatch.StartNew();",
            StringComparison.Ordinal);
        linkedFastPath.ShouldBeGreaterThanOrEqualTo(0);
        stopwatchStart.ShouldBeGreaterThan(linkedFastPath);
    }

    [Test]
    public void VulkanHotSettingsAndQueueOwnership_AvoidPerOperationWork()
    {
        OverrideableSetting<EVulkanGpuDrivenProfile> projectOverride =
            new(EVulkanGpuDrivenProfile.DevParity, hasOverride: true);

        OverrideableSettingExtensions.ResolveValueCascade(
            EVulkanGpuDrivenProfile.Auto,
            projectOverride,
            null).ShouldBe(EVulkanGpuDrivenProfile.DevParity);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1_000; iteration++)
        {
            _ = OverrideableSettingExtensions.ResolveValueCascade(
                EVulkanGpuDrivenProfile.Auto,
                projectOverride,
                null);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0);

        string effectiveSettings = ReadWorkspaceFile(
            "XRENGINE/Engine/Subclasses/Engine.EffectiveSettings.cs");
        string queueOverlap = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.QueueOverlap.cs");
        string diagnostics = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanDiagnosticOptions.cs");
        string dataBuffer = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs");

        effectiveSettings.ShouldContain("OverrideableSettingExtensions.ResolveValueCascade(");
        queueOverlap.ShouldContain("_queueOwnershipConfigCacheFrameId != frameId");
        queueOverlap.ShouldContain("ReferenceEquals(entry.PassMetadata, passMetadata)");
        queueOverlap.ShouldContain("bool advanceAdaptivePolicy");
        diagnostics.ShouldContain("public bool EnableCrashBreadcrumbs => HasFlag(EVulkanDiagnosticFlags.CrashBreadcrumbs);");
        diagnostics.ShouldNotContain("public bool EnableCrashBreadcrumbs => Flags.HasFlag(");
        dataBuffer.ShouldContain("ResolveHostVisibleSubDataUploadRoute(_lastMemProps)");
        dataBuffer.ShouldContain("TryGetBufferMemoryAllocation(buffer, out allocation)");
    }

    [Test]
    public void DefaultPipelineRemainingHotPaths_AvoidSuppressedLogArraysEnumBoxingAndDiagnosticStrings()
    {
        const string normalLogKey = nameof(DefaultPipelineRemainingHotPaths_AvoidSuppressedLogArraysEnumBoxingAndDiagnosticStrings) + ".Normal";
        const string warningLogKey = nameof(DefaultPipelineRemainingHotPaths_AvoidSuppressedLogArraysEnumBoxingAndDiagnosticStrings) + ".Warning";
        TimeSpan interval = TimeSpan.FromDays(1);
        _ = XREngine.Debug.ShouldLogEvery(normalLogKey, interval);
        _ = XREngine.Debug.ShouldLogEvery(warningLogKey, interval);
        XREngine.Debug.VulkanEvery(normalLogKey, interval, "Value={0}", 0);
        XREngine.Debug.VulkanWarningEvery(warningLogKey, interval, "Value={0}, active={1}", 0, true);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 1_000; iteration++)
        {
            XREngine.Debug.VulkanEvery(normalLogKey, interval, "Value={0}", iteration);
            XREngine.Debug.VulkanWarningEvery(
                warningLogKey,
                interval,
                "Value={0}, active={1}",
                iteration,
                true);
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        allocated.ShouldBe(0);

        string debug = ReadWorkspaceFile("XREngine.Runtime.Core/Core/Diagnostics/Debug.cs");
        string dataBuffer = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs");
        string forwardLighting = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Lights3DCollection.ForwardLighting.cs");
        string lights = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Lights3DCollection.cs");
        string compiler = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanRenderGraphCompiler.cs");
        string preparation = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Preparation.cs");
        string bufferPolicy = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Buffers/XRBufferMemoryPolicy.cs");
        string blit = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.Blit.cs");
        string renderState = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.RenderStateMutation.cs");
        string descriptorImageReference = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.VulkanDescriptorImageReference.cs");
        string materialState = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Materials/VkMaterial.ProgramDescriptorState.cs");
        string material = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Materials/VkMaterial.cs");

        debug.ShouldContain("public static void VulkanEvery<T>(");
        debug.ShouldContain("public static void VulkanWarningEvery<T1, T2>(");
        dataBuffer.ShouldNotContain(".HasFlag(");
        forwardLighting.ShouldContain("LogForwardShadowNoTextureReason(");
        forwardLighting.ShouldNotContain("reason != _lastForwardShadowNoTexReason");
        lights.ShouldContain("_lastForwardShadowNoTextureReasonKey");
        compiler.ShouldContain("private static readonly Comparison<FrameOpSortKey> FrameOpSortComparison");
        compiler.ShouldContain("sortKeys.AsSpan(0, opCount).Sort(FrameOpSortComparison)");
        preparation.ShouldNotContain(".HasFlag(");
        bufferPolicy.ShouldNotContain(".HasFlag(");
        blit.ShouldNotContain(".HasFlag(");
        renderState.ShouldContain("private int _indexedViewportScissorCount;");
        renderState.ShouldContain("_indexedViewportScissorCount = 0;");
        descriptorImageReference.ShouldContain("IEquatable<VulkanDescriptorImageReference>");
        descriptorImageReference.ShouldContain("View.Handle == other.View.Handle");
        materialState.ShouldContain("public required DescriptorBindingInfo[] Bindings");
        material.ShouldContain("for (int bindingIndex = 0; bindingIndex < stateBindings.Length; bindingIndex++)");
    }

    [Test]
    public void DefaultPipelineCpuHotPaths_ReuseReflectionScopesFrustaAndCullingStorage()
    {
        string editorUi = ReadWorkspaceFile(
            "XREngine.Editor/IMGUI/EditorImGuiUI.ImGui.cs");
        string pipelineInstance = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs");
        string viewport = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/XRViewport.cs");
        string renderingState = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/RenderingState.cs");
        string runtimeEngine = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Runtime/RuntimeEngine.cs");
        string engine = ReadWorkspaceFile(
            "XRENGINE/Engine/Engine.cs");
        string preparedFrustum = ReadWorkspaceFile(
            "XREngine.Data/Geometry/PreparedFrustum.cs");
        string camera = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Camera/XRCamera.cs");
        string shadowCollection = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Lights3DCollection.Shadows.cs");
        string aabb = ReadWorkspaceFile(
            "XREngine.Data/Geometry/AABB.cs");
        string box = ReadWorkspaceFile(
            "XREngine.Data/Geometry/Box.cs");
        string light = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Scene/Components/Lights/Types/LightComponent.cs");
        string changedArgs = ReadWorkspaceFile(
            "XREngine.Data/Core/Objects/XRPropertyChangedEventArgs.cs");

        editorUi.ShouldContain("ImGui.SetWindowCollapsed(windowName, true, ImGuiCond.Always);");
        editorUi.ShouldNotContain("typeof(ImGui).GetMethods");
        pipelineInstance.ShouldContain("_screenSpaceUiCommandGeneration != commandGeneration");
        pipelineInstance.ShouldContain("for (int commandIndex = 0; commandIndex < container.Count; commandIndex++)");
        viewport.ShouldContain("!_renderPipeline.ContainsScreenSpaceUiRenderCommand()");
        renderingState.ShouldContain("StateObject.New(PopRenderAreaAction, this)");
        renderingState.ShouldNotContain("StateObject.New(PopRenderArea)");
        runtimeEngine.ShouldContain("StateObject.New(PopRenderGraphPassAction, stack)");
        runtimeEngine.ShouldNotContain("new DisposableAction(");
        engine.ShouldContain("private sealed class PooledExternalProfilerScope");
        preparedFrustum.ShouldContain("public void UpdateTransformed(in Frustum frustum, in Matrix4x4 worldMatrix)");
        camera.ShouldContain("public PreparedFrustum PreparedWorldFrustum()");
        shadowCollection.ShouldContain("frusta.Add(cameras[i].PreparedWorldFrustum());");
        aabb.ShouldContain("public readonly void GetCorners(Span<Vector3> corners)");
        box.ShouldContain("Span<Vector3> corners = stackalloc Vector3[8];");
        light.ShouldContain("publishNotifications: false");
        changedArgs.ShouldContain("object? IXRPropertyChangedEventArgs.PreviousValue => PreviousValue;");
    }

    [Test]
    public void IndirectDrawStateCapabilityScope_IsAValueTypeToAvoidPerBucketAllocation()
        => typeof(IndirectDrawStateCapabilityScope).IsValueType.ShouldBeTrue();

    [Test]
    public void GpuIndirectCommandChains_KeepMutableArgumentStreamsOnPrimary()
    {
        VulkanRenderer.IndirectCommandChainSecondaryRecordingSafe.ShouldBeFalse();

        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        recording.ShouldContain("if (!IndirectCommandChainSecondaryRecordingSafe ||");
        recording.ShouldContain("RecordIndirectDrawIntoCommandBuffer(commandBuffer, indirectOp, opPassIndex, opIndex);");
        recording.ShouldContain("usedSecondary: false");
    }

    [Test]
    public void MutableGpuDrivenPrimaries_ReuseStableInlineTopology()
    {
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string diagnostics = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/FrameOpDiagnostics.cs");
        string markers = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.SubmissionMarkers.cs");
        string meshRenderer = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.cs");

        recording.ShouldContain("bool hasMutableGpuDrivenFrameOps = hasStaticFrameOps && HasMutableGpuDrivenFrameOps(ops);");
        recording.ShouldContain("!hasMutableGpuDrivenFrameOps &&");
        recording.ShouldContain("hasStaticFrameOps && !VulkanPrimaryCommandBufferReuseEnabled");
        recording.ShouldNotContain("\"mutable-gpu-driven-frame-ops\"");
        recording.ShouldContain("PrepareSubmissionMarkersForCommandBufferReuse(");
        (recording.Split("primaryCommandBuffersReused: 1", StringSplitOptions.None).Length - 1)
            .ShouldBeGreaterThanOrEqualTo(3);
        (recording.Split("primaryCommandBuffersRecorded: 1", StringSplitOptions.None).Length - 1)
            .ShouldBeGreaterThanOrEqualTo(1);
        diagnostics.ShouldContain("IndirectDrawOp or MeshTaskDispatchIndirectCountOp");
        diagnostics.ShouldNotContain("ComputeDispatchOp or IndirectDrawOp or MeshTaskDispatchIndirectCountOp");
        markers.ShouldContain("RegisterSubmissionMarkersForCommandBuffer");
        meshRenderer.ShouldNotContain("hash.Add(marker.Fence.GetHashCode());");
    }

    [Test]
    public void VulkanPrimaryReuse_IsEnabledAfterPublicationGenerationsAreKeyed()
    {
        VulkanRenderer.VulkanPrimaryCommandBufferReuseSafe.ShouldBeTrue();

        string state = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferState.cs");
        state.ShouldContain("VulkanPrimaryCommandBufferReuseSafe &&");
        state.ShouldContain("immutable dependency");
        state.ShouldContain("RuntimeRenderingHostServices.Settings.EnableVulkanPrimaryCommandBufferReuse");
    }

    [Test]
    public void MutableGpuDrivenFrames_BypassCommandChainSecondaries()
    {
        string lowering = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainLowering.cs");

        lowering.ShouldContain("ResolveMeshSubmissionStrategy().IsGpuZeroReadbackStrategy()");
        lowering.ShouldContain("HasMutableGpuDrivenFrameOps(staticOps) || HasMutableGpuDrivenFrameOps(volatileOps)");
        lowering.ShouldContain("Vulkan.CommandChains.MutableGpuFrameInline");
        lowering.ShouldContain("command-chain publication generations are tracked");
    }

    [Test]
    public void AsyncBackendCompile_IsExplicitAndOptIn()
    {
        XRRenderProgram program = new();
        program.AllowAsyncBackendCompile.ShouldBeFalse();

        program.AllowAsyncBackendCompile = true;

        program.AllowAsyncBackendCompile.ShouldBeTrue();
    }

    [TestCase(XRRenderProgram.EShaderProgramBackendStage.SourceQueued)]
    [TestCase(XRRenderProgram.EShaderProgramBackendStage.Compiling)]
    [TestCase(XRRenderProgram.EShaderProgramBackendStage.Linking)]
    [TestCase(XRRenderProgram.EShaderProgramBackendStage.QueueBackpressure)]
    public void IndirectProgramReadinessDeferral_IsNotAForbiddenFallback(
        XRRenderProgram.EShaderProgramBackendStage stage)
        => HybridRenderingManager.IsIndirectGraphicsProgramTerminalFailure(stage).ShouldBeFalse();

    [TestCase(XRRenderProgram.EShaderProgramBackendStage.BinaryUploadFailed)]
    [TestCase(XRRenderProgram.EShaderProgramBackendStage.Failed)]
    [TestCase(XRRenderProgram.EShaderProgramBackendStage.Abandoned)]
    public void IndirectProgramTerminalFailure_IsAForbiddenFallback(
        XRRenderProgram.EShaderProgramBackendStage stage)
        => HybridRenderingManager.IsIndirectGraphicsProgramTerminalFailure(stage).ShouldBeTrue();

    [Test]
    public void DescriptorChanges_HaveExplicitContentIdentityAndLayoutClasses()
    {
        RenderResourceChangeKind.FrameData.ShouldNotBe(RenderResourceChangeKind.CompatibleContentPublication);
        RenderResourceChangeKind.CompatibleContentPublication.ShouldNotBe(RenderResourceChangeKind.BindingIdentity);
        RenderResourceChangeKind.BindingIdentity.ShouldNotBe(RenderResourceChangeKind.StructuralLayout);
    }

    [Test]
    public void VulkanRecording_SharedPacketSecondariesUseSimultaneousUseAndExactInheritance()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string secondarySource = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.SecondaryCommandBuffers.cs");
        int workerStart = secondarySource.IndexOf("private void RecordScheduledMeshCommandChainWorker", StringComparison.Ordinal);
        int workerEnd = secondarySource.IndexOf("private bool TryRecordSecondaryBucket", workerStart, StringComparison.Ordinal);
        string worker = secondarySource[workerStart..workerEnd];

        source.ShouldContain("scheduledOpCount += chain.SourceCount;");
        source.ShouldContain("CmdExecuteCommandsTracked(commandBuffer, (uint)secondaryCount, secondaryPtr)");
        worker.ShouldContain("CommandBufferUsageFlags.RenderPassContinueBit | CommandBufferUsageFlags.SimultaneousUseBit");
        worker.ShouldContain("StoreCommandChainSecondaryInheritance(");
        source.ShouldContain("CommandChainSecondaryInheritanceMatches(");
        source.ShouldContain("ActiveMeshSecondaryInheritanceMatches(");
        worker.ShouldContain("using var plannerScope = EnterThreadResourcePlannerRuntimeStateScope(in plannerState);");
        worker.ShouldContain("batch.HasPlannerState[chainIndex]");
        worker.ShouldNotContain("_frameOpResourcePlannerReadbackLock");
        worker.IndexOf("EnterThreadResourcePlannerRuntimeStateScope(in plannerState)", StringComparison.Ordinal)
            .ShouldBeLessThan(worker.IndexOf("for (int drawIndex = 0; drawIndex < chain.SourceCount; drawIndex++)", StringComparison.Ordinal));
        worker.ShouldContain("chain.State = CommandChainState.Recorded;");
        worker.ShouldContain("A prewarmed Vulkan command-chain draw became unavailable during secondary recording.");
        worker.ShouldNotContain("bool pipelinesReady");
        source.ShouldContain("CommandBufferUsageFlags.RenderPassContinueBit | CommandBufferUsageFlags.OneTimeSubmitBit");
    }

    [Test]
    public void WorkerDispatch_RecordsOnlyCommandBuffersOwnedByThatWorkerPool()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainWorkers.cs");

        source.ShouldContain("public int[] RecordJobWorkerIndices = [];");
        source.ShouldContain("if (batch.RecordJobWorkerIndices[jobIndex] != worker.WorkerIndex)");
        source.ShouldContain("RecordScheduledMeshCommandChainWorker(batch, chainIndex);");
    }

    [Test]
    public void WorkerPoolAssignment_UsesStableMutableRendererAffinity()
    {
        var renderer = (VkMeshRenderer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
            typeof(VkMeshRenderer));
        VulkanMeshFrameDataFamilyKey firstFamily = new(
            2, EVulkanMeshFrameDataStreamKind.Primary, default,
            3, 4, 5, 6, 7, 8, false, false);
        VulkanMeshFrameDataRendererFamilyKey firstKey = new(renderer, firstFamily);
        VulkanMeshFrameDataRendererFamilyKey anotherFamilyOfSameRenderer = new(
            renderer,
            firstFamily with { ViewportIdentity = 99 });

        int first = VulkanRenderer.ResolveCommandChainRecordingWorkerIndex(firstKey, workerCount: 6);
        int afterOtherJobsDisappear = VulkanRenderer.ResolveCommandChainRecordingWorkerIndex(firstKey, workerCount: 6);

        first.ShouldBe(afterOtherJobsDisappear);
        VulkanRenderer.ResolveCommandChainRecordingWorkerIndex(anotherFamilyOfSameRenderer, workerCount: 6)
            .ShouldBe(first);
        first.ShouldBeInRange(0, 5);
        VulkanRenderer.ResolveCommandChainRecordingWorkerIndex(firstKey, workerCount: 1).ShouldBe(0);
    }

    [Test]
    public void CommandChainRendererFamily_MixedChainsRequireSerialRecording()
    {
        var firstRenderer = (VkMeshRenderer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
            typeof(VkMeshRenderer));
        var secondRenderer = (VkMeshRenderer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
            typeof(VkMeshRenderer));
        FrameOpContext firstContext = new(1, 2, null, null, null);
        FrameOpContext differentFamilyContext = firstContext with { ViewportIdentity = 3 };
        PendingMeshDraw firstDraw = default(PendingMeshDraw) with { Renderer = firstRenderer };
        PendingMeshDraw secondDraw = default(PendingMeshDraw) with { Renderer = secondRenderer };
        CommandChain chain = new(new CommandChainKey(
            0,
            new RenderViewKey(1, 2, 0, RenderViewKind.Main, 0, -1),
            0,
            0,
            false,
            1))
        {
            SourceStartIndex = 0,
            SourceCount = 2,
        };

        FrameOp[] homogeneousOps =
        [
            new MeshDrawOp(0, null, firstDraw, firstContext),
            new MeshDrawOp(0, null, firstDraw, firstContext),
        ];
        FrameOp[] mixedRendererOps =
        [
            homogeneousOps[0],
            new MeshDrawOp(0, null, secondDraw, firstContext),
        ];
        FrameOp[] mixedFamilyOps =
        [
            homogeneousOps[0],
            new MeshDrawOp(0, null, firstDraw, differentFamilyContext),
        ];

        VulkanRenderer.TryResolveCommandChainRecordingRendererFamily(
                homogeneousOps,
                chain,
                frameDataSlot: 0,
                EVulkanMeshFrameDataStreamKind.Primary,
                out VulkanMeshFrameDataRendererFamilyKey rendererFamily)
            .ShouldBeTrue();
        rendererFamily.Renderer.ShouldBeSameAs(firstRenderer);
        VulkanRenderer.TryResolveCommandChainRecordingRendererFamily(
                mixedRendererOps,
                chain,
                frameDataSlot: 0,
                EVulkanMeshFrameDataStreamKind.Primary,
                out _)
            .ShouldBeFalse();
        VulkanRenderer.TryResolveCommandChainRecordingRendererFamily(
                mixedFamilyOps,
                chain,
                frameDataSlot: 0,
                EVulkanMeshFrameDataStreamKind.Primary,
                out _)
            .ShouldBeFalse();
    }

    [Test]
    public void WorkerDispatch_UsesStablePoolCapacityAndSerializesOwnershipConflicts()
    {
        string workers = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainWorkers.cs");
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");

        workers.ShouldContain("workerCount = workers.Length;");
        workers.ShouldContain("TryAssignCommandChainRecordingWorker(");
        workers.ShouldContain("batch.TryGetRendererOwner(renderer, out int rendererOwner)");
        workers.ShouldContain("CommandChainWorkerWaitTimeoutMilliseconds");
        workers.ShouldContain("batch.ActiveWorkerMask");
        workers.ShouldContain("_commandChainRecordingWorkerCountdown.Reset(activeWorkerCount);");
        recording.ShouldContain("TryAssignCommandChainRecordingWorker(");
        recording.ShouldContain("schedulingConflictCount++");
        recording.ShouldContain("recordJobWorkerIndices[jobIndex] < 0");
        recording.IndexOf("MarkCommandChainSecondaryCommandBufferInvalid(chain);", StringComparison.Ordinal)
            .ShouldBeLessThan(recording.IndexOf("DispatchCommandChainRecordingWorkers(batch, workers, workerCount)", StringComparison.Ordinal));
    }

    [Test]
    public void CommandBufferReuse_GuardsNativeResetAndReplacesPendingSecondaries()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");
        string lowering = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainLowering.cs");
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string secondaries = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.SecondaryCommandBuffers.cs");

        lifetime.ShouldContain("private bool CanResetVulkanCommandBuffer(");
        lifetime.IndexOf("CanResetVulkanCommandBuffer(commandBuffer, out string reason)", StringComparison.Ordinal)
            .ShouldBeLessThan(lifetime.IndexOf("return Api!.ResetCommandBuffer(commandBuffer, 0);", StringComparison.Ordinal));
        lifetime.ShouldContain("commandRecord.Pins.HasRecordedReferences");
        lifetime.ShouldContain("commandRecord.Pins.RecordedReferenceCount");
        lowering.ShouldContain("CanResetVulkanCommandBuffer(secondary, out _)");
        recording.ShouldNotContain("Api!.ResetCommandBuffer(");
        secondaries.ShouldNotContain("Api!.ResetCommandBuffer(");
        secondaries.ShouldContain("TryEnsureMutableDynamicUiSecondaryCommandBuffer(");
        secondaries.ShouldContain("DeferSecondaryCommandBufferFree(imageIndex, pool, previous);");
    }

    [Test]
    public void CompatiblePublication_StillInvalidatesCommandBuffersThatRecordedAnUpdatedSet()
    {
        string lowering = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainLowering.cs");
        string descriptors = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Descriptors/VulkanRenderer.DescriptorSets.cs");
        string pipeline = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs");

        lowering.ShouldContain("RenderResourceChangeKind.CompatibleContentPublication");
        descriptors.ShouldContain("TryCaptureDescriptorUpdateInvalidations_NoLock(");
        descriptors.ShouldContain("InvalidateCachedCommandBuffersByHandle(");
        descriptors.ShouldContain("setState.UsesUpdateAfterBind");
        pipeline.ShouldContain("ClassifyTextureBindingChange");
        pipeline.ShouldContain("RenderResourceChangeKind.StructuralLayout");
    }

    [Test]
    public void MeshDescriptorRefresh_SkipsUnchangedBindingsBeforeNativeUpdate()
    {
        string descriptors = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs");
        string allocation = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Records/Classes/VkMeshRenderer.DescriptorAllocation.cs");
        string key = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Records/Structs/VkMeshRenderer.DescriptorWriteKey.cs");

        allocation.ShouldContain("Dictionary<DescriptorWriteKey, ulong> DescriptorWriteSignatures");
        key.ShouldContain("ulong DescriptorSetHandle");
        descriptors.ShouldContain("ComputeDescriptorBufferInfoSignature(");
        descriptors.ShouldContain("ComputeDescriptorImageInfoSignature(");
        descriptors.ShouldContain("ComputeDescriptorTexelBufferSignature(");
        AssertOrdered(
            descriptors,
            "if (DescriptorWriteMatches(allocation, bufferKey, bufferSignature))",
            "bufferMap.Add((writes.Count, bufferStart, binding, descriptorCount));");
        AssertOrdered(
            descriptors,
            "if (DescriptorWriteMatches(allocation, imageKey, imageSignature))",
            "imageMap.Add((writes.Count, imageStart, binding, descriptorCount));");
        AssertOrdered(
            descriptors,
            "if (DescriptorWriteMatches(allocation, texelKey, texelSignature))",
            "texelMap.Add((writes.Count, texelStart, binding, descriptorCount));");
        AssertOrdered(
            descriptors,
            "Renderer.TryUpdateDescriptorSetsTracked",
            "allocation.DescriptorWriteSignatures[signatures[signatureIndex].key]");
    }

    [Test]
    public void DescriptorAllocationIdentity_UsesImmutableResourcesOnlyWithoutUpdateAfterBind()
    {
        VkMeshRenderer.DescriptorAllocationKey immutableIdentity = new(
            LayoutFingerprint: 11,
            SchemaFingerprint: 12,
            ProgramBindingId: 13,
            DescriptorFrameSlotCount: 3,
            SetCount: 4,
            MaterialIdentity: 5,
            MaterialBindingLayoutVersion: 6,
            ViewFamilyIdentity: 7,
            DrawUniformSlot: 8,
            BindingIdentityFingerprint: 9,
            ImmutableResourceFingerprint: 20);
        VkMeshRenderer.DescriptorAllocationKey changedContent = immutableIdentity with
        {
            ImmutableResourceFingerprint = 21,
        };
        VkMeshRenderer.DescriptorAllocationKey changedBinding = immutableIdentity with
        {
            BindingIdentityFingerprint = 10,
        };
        VkMeshRenderer.DescriptorAllocationKey changedProgram = immutableIdentity with
        {
            ProgramBindingId = 14,
        };
        VkMeshRenderer.DescriptorAllocationKey updateAfterBindIdentity = immutableIdentity with
        {
            ImmutableResourceFingerprint = 0,
        };
        VkMeshRenderer.DescriptorAllocationKey sameUpdateAfterBindIdentity = updateAfterBindIdentity with { };

        changedContent.ShouldNotBe(immutableIdentity);
        changedBinding.ShouldNotBe(immutableIdentity);
        changedProgram.ShouldNotBe(immutableIdentity);
        sameUpdateAfterBindIdentity.ShouldBe(updateAfterBindIdentity);
    }

    [Test]
    public void CapturedDescriptorReuse_RefreshesNonUpdateAfterBindSetsOnlyAfterTheirSlotCompletes()
    {
        string descriptors = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Descriptors.cs");
        string state = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferState.cs");

        descriptors.ShouldContain("bool allowCompletedDescriptorSlotRefresh = bindingSnapshot is null &&");
        descriptors.ShouldContain(
            "refreshFrameIndex is { } completedFrameIndex &&");
        descriptors.ShouldContain("Renderer.CanUpdateCompletedDescriptorFrameSlot(completedFrameIndex)");
        descriptors.ShouldContain("!Renderer.CanUpdateCompletedDescriptorFrameSlot(frameIndex)");
        descriptors.ShouldContain("captured descriptor frame slot {frameIndex} is still in flight");
        descriptors.ShouldContain("recordDescriptorTableGeneration: false");
        descriptors.ShouldContain("if (recordDescriptorTableGeneration)");
        descriptors.ShouldNotContain("captured descriptor allocation is immutable and requires a new resource snapshot");
        state.ShouldContain("internal bool CanUpdateCompletedDescriptorFrameSlot(int frameDataSlot)");
        state.ShouldContain("_swapchainImageTimelineValues");
        state.ShouldContain("_frameSlotTimelineValues");
        state.ShouldContain("HasTimelineValueCompleted(_graphicsTimelineSemaphore, completionValue)");
    }

    [Test]
    public void CompatiblePublication_UpdatesOnlyTheCompletedDescriptorSlot()
    {
        const ulong previousResource = 41;
        const ulong publishedResource = 42;
        ulong[] slotFingerprints = [previousResource, previousResource, previousResource];

        for (int completedSlot = 0; completedSlot < slotFingerprints.Length; completedSlot++)
        {
            VkMaterial.DescriptorSlotRequiresPublication(
                    slotFingerprints,
                    completedSlot,
                    publishedResource)
                .ShouldBeTrue();

            slotFingerprints[completedSlot] = publishedResource;
            for (int occupiedSlot = completedSlot + 1; occupiedSlot < slotFingerprints.Length; occupiedSlot++)
                slotFingerprints[occupiedSlot].ShouldBe(previousResource);
        }

        slotFingerprints.ShouldAllBe(static value => value == publishedResource);
    }

    [Test]
    public void MaterialDescriptorPublication_IsPerSlotAndWorkerSafe()
    {
        string material = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Materials/VkMaterial.cs");

        material.ShouldContain("lock (_stateSync)");
        material.ShouldContain("UpdateFrameDescriptorSet(state, resolvedFrame)");
        material.ShouldContain("state.SlotResourceFingerprints[resolvedFrame] = resourceFingerprint;");
        material.ShouldNotContain("UpdateDescriptorSets(state)");
    }

    [Test]
    public void DescriptorContents_AreSnapshottedPerSubmissionNotBakedIntoCommandDependencies()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");

        lifetime.ShouldContain("commandLifetime.RefreshTouchedDependencies();");
        lifetime.ShouldContain("TryAppendSubmittedDescriptorDependency_NoLock");
        lifetime.ShouldContain("ResourceKey(ObjectType.Image, backingImageHandle)");
        lifetime.ShouldNotContain("batch.RecordDependency(snapshot.References[i])");
        lifetime.ShouldNotContain("TrackVulkanCommandBufferResource_NoLock(commandBufferHandle, pair.First");
    }

    [Test]
    public void DescriptorSubmissionDependencyRefresh_UsesPersistentLookupInsteadOfQuadraticScan()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");

        lifetime.ShouldContain(
            "Dictionary<VulkanResourceLifetimeKey, ulong> _vulkanSubmissionDependencyGenerationsScratch");
        lifetime.ShouldContain("touchedGenerations.Clear();");
        lifetime.ShouldContain(
            "touchedGenerations.TryGetValue(key, out ulong trackedGeneration)");
        lifetime.ShouldNotContain("for (int i = 0; i < touched.Count; i++)");
        lifetime.ShouldNotContain("new Dictionary<VulkanResourceLifetimeKey, ulong>(touched.Count)");
    }

    [Test]
    public void DescriptorLayoutTracking_PreservesSecondaryExecutionAndFirstUseInvariants()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");

        lifetime.ShouldContain("lifetime.Level = allocateInfo.Level;");
        lifetime.ShouldContain("lifetime.Level == CommandBufferLevel.Secondary;");
        lifetime.ShouldContain("RecordSecondaryDescriptorImageLayoutRequirements(commandBuffer, descriptorSet, snapshotToValidate);");
        lifetime.ShouldContain("ValidateVulkanDescriptorImageLayouts(commandBuffer, descriptorSet, snapshotToValidate);");
        lifetime.ShouldContain("private bool RecordSecondaryDescriptorImageLayoutRequirement(");
        lifetime.ShouldContain("ImageLayout requiredLayout = reference.Type == DescriptorType.StorageImage");

        int transitionStart = recording.IndexOf(
            "private void TransitionDescriptorImageForSampling(",
            StringComparison.Ordinal);
        int transitionEnd = recording.IndexOf(
            "private bool IsImageRangeAttachedToFrameBuffer(",
            transitionStart,
            StringComparison.Ordinal);
        transitionStart.ShouldBeGreaterThanOrEqualTo(0);
        transitionEnd.ShouldBeGreaterThan(transitionStart);
        string transition = recording[transitionStart..transitionEnd];
        AssertOrdered(
            transition,
            "GetCurrentVulkanResourceGeneration(",
            "if (resourceGeneration == 0)",
            "priorState = VulkanImageAccessState.Undefined with",
            "CmdPipelineBarrierTracked(");
    }

    [Test]
    public void DescriptorSubmission_AllowsOnlyAlreadyRecordedPendingRetirementGenerations()
    {
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");
        int methodStart = lifetime.IndexOf(
            "private bool TryAppendSubmittedDescriptorDependency_NoLock(",
            StringComparison.Ordinal);
        int methodEnd = lifetime.IndexOf("\n    private ", methodStart + 1, StringComparison.Ordinal);
        methodStart.ShouldBeGreaterThanOrEqualTo(0);
        methodEnd.ShouldBeGreaterThan(methodStart);
        string method = lifetime[methodStart..methodEnd];

        AssertOrdered(
            method,
            "if ((resource.State & EVulkanResourceLifetimeState.Destroyed) != 0)",
            "if (touchedGenerations.TryGetValue(key, out ulong trackedGeneration))",
            "else",
            "if ((resource.State & EVulkanResourceLifetimeState.PendingRetirement) != 0)");
        method.ShouldContain("touched.Add(new KeyValuePair<VulkanResourceLifetimeKey, ulong>(key, resource.Generation));");
    }

    [Test]
    public void StreamingDescriptorRefresh_RejectsStaleResourceAndLayoutGenerations()
    {
        string material = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Materials/VkMaterial.cs");
        string synchronization = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.Synchronization.cs");

        int reusableStart = material.IndexOf(
            "internal bool TryGetValidatedReusableMaterialDescriptorSet(",
            StringComparison.Ordinal);
        int reusableEnd = material.IndexOf(
            "\n\t\t\tinternal static bool DescriptorSlotRequiresPublication(",
            reusableStart,
            StringComparison.Ordinal);
        reusableStart.ShouldBeGreaterThanOrEqualTo(0);
        reusableEnd.ShouldBeGreaterThan(reusableStart);
        string reusable = material[reusableStart..reusableEnd];
        AssertOrdered(
            reusable,
            "ulong currentResourceFingerprint = ComputeResourceFingerprint(program);",
            "state.ResourceFingerprint != currentResourceFingerprint",
            "state.SlotResourceFingerprints[resolvedFrame] != state.ResourceFingerprint");

        int publicationStart = synchronization.IndexOf(
            "private void PublishRecordedImageLayouts(",
            StringComparison.Ordinal);
        int publicationEnd = synchronization.IndexOf(
            "\n    private void AdvanceCompletedImageLayouts(",
            publicationStart,
            StringComparison.Ordinal);
        publicationStart.ShouldBeGreaterThanOrEqualTo(0);
        publicationEnd.ShouldBeGreaterThan(publicationStart);
        string publication = synchronization[publicationStart..publicationEnd];
        AssertOrdered(
            publication,
            "ulong currentGeneration = GetCurrentVulkanResourceGeneration(",
            "if (pair.Value.ResourceGeneration != 0 &&",
            "currentGeneration != pair.Value.ResourceGeneration)",
            "continue;",
            "state.Submitted = pair.Value;");
    }

    [Test]
    public void DefaultPipelineCameraMotionHotPaths_ReuseSchedulesCollectionsAndDiagnosticStorage()
    {
        string lowering = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandChainLowering.cs");
        string recording = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/CommandBuffers/VulkanRenderer.CommandBufferRecording.cs");
        string lifetime = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceLifetimeTracking.cs");
        string registry = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Resources/RenderResourceRegistry.cs");
        string renderToWindow = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderToWindow.cs");
        string forwardPlus = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/Features/VPRC_ForwardPlusLightCullingPass.cs");
        string renderCommands = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandCollection.cs");
        string collectionContext = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/VisualScene3D.CollectionContext.cs");
        string debugDrawing = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Runtime/RuntimeEngine.Rendering.Debug.cs");
        string components = ReadWorkspaceFile(
            "XREngine.Runtime.Core/Scene/SceneNode.Components.cs");
        string icons = ReadWorkspaceFile(
            "XREngine.Editor/IMGUI/EditorImGuiUI.Icons.cs");
        string hierarchy = ReadWorkspaceFile(
            "XREngine.Editor/IMGUI/EditorImGuiUI.HierarchyPanel.cs");
        string profiler = ReadWorkspaceFile(
            "XRENGINE/Engine/Subclasses/Engine.CodeProfiler.cs");
        string preferences = ReadWorkspaceFile(
            "XRENGINE/Settings/EditorPreferences.cs");
        string imageBackedTexture = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Textures/VkImageBackedTexture.cs");
        string viewport = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/XRViewport.cs");
        string window = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs");
        string events = ReadWorkspaceFile(
            "XREngine.Data/Core/Events/XREvent.cs");
        string uiInput = ReadWorkspaceFile(
            "XREngine.Runtime.InputIntegration/Scene/Components/Pawns/UICanvasInputComponent.cs");
        string collection2D = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/VisualScene2D.CollectionContext.cs");
        string editorJobs = ReadWorkspaceFile(
            "XREngine.Editor/EditorJobTracker.cs");
        string directionalCascades = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Scene/Components/Lights/Types/DirectionalLightComponent.CascadeShadows.cs");
        string vulkanMeshPipeline = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");
        string generatedProgramState = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/Records/Structs/VkMeshRenderer.GeneratedProgramState.cs");
        string meshRendererBase = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/XRMeshRenderer.cs");
        string engine = ReadWorkspaceFile(
            "XRENGINE/Engine/Engine.cs");
        string renderingHost = ReadWorkspaceFile(
            "XREngine.Runtime.Bootstrap/RenderingHost/Engine.RuntimeRenderingHostServices.cs");
        string profilerDumps = ReadWorkspaceFile(
            "XREngine.Editor/ProfilerDiagnosticDumps.cs");
        string eventBase = ReadWorkspaceFile(
            "XREngine.Data/Core/Events/XREventBase.cs");

        lowering.ShouldNotContain("ResourcePlanRevisionChanged");
        lowering.ShouldContain("Build the replacement command-chain");
        recording.ShouldContain("private static void MergeBarrierScope(");
        recording.ShouldNotContain("mask.HasFlag(");

        registry.ShouldContain("private XRFrameBuffer[] _cachedFrameBufferInstances = [];");
        registry.ShouldContain("internal XRFrameBuffer[] GetFrameBufferInstanceSnapshot()");
        renderToWindow.ShouldContain("instance.Resources.GetFrameBufferInstanceSnapshot()");
        renderToWindow.ShouldContain("_cachedRenderGraphPassName");

        int buildLights = forwardPlus.IndexOf("BuildLocalLights(world.Lights);", StringComparison.Ordinal);
        int resolveDepth = forwardPlus.IndexOf(
            "ActivePipelineInstance.GetTexture<XRTexture>(DepthViewTexture)",
            StringComparison.Ordinal);
        buildLights.ShouldBeGreaterThanOrEqualTo(0);
        resolveDepth.ShouldBeGreaterThan(buildLights);
        forwardPlus.ShouldContain("private readonly List<ForwardPlusLocalLight> _localLightsScratch = [];");
        forwardPlus.ShouldContain("if (lightCount == 0)");

        renderCommands.ShouldContain("private readonly Comparison<Entry> _entryComparison;");
        renderCommands.ShouldContain("_entries.Sort(_entryComparison);");
        collectionContext.ShouldContain("private static readonly Action<RenderInfo3D> CollectRenderCommandsCallback");
        collectionContext.ShouldContain("[ThreadStatic]");
        debugDrawing.ShouldContain("public readonly List<(Vector3 pos, ColorF4 color)> Points = [];");
        debugDrawing.ShouldNotContain("ConcurrentBag<");

        components.ShouldContain("for (int i = 0; i < ComponentsInternal.Count; ++i)");
        components.ShouldNotContain("ComponentsInternal.FirstOrDefault");
        components.ShouldNotContain("ComponentsInternal.LastOrDefault");
        icons.ShouldContain("private readonly record struct IconCacheKey");
        icons.ShouldNotContain("BuildIconCacheKey");

        int drawEntryStart = hierarchy.IndexOf("private static bool DrawSceneNodeEntry(", StringComparison.Ordinal);
        int drawEntryEnd = hierarchy.IndexOf("private static void QueueHierarchyReparent(", drawEntryStart, StringComparison.Ordinal);
        hierarchy[drawEntryStart..drawEntryEnd].ShouldNotContain("EnqueueSceneEdit(() =>");

        lifetime.ShouldContain("private readonly ThreadLocal<HashSet<ulong>> _vulkanChangedDescriptorSetsScratch");
        lifetime.ShouldNotContain("state.IndexedReferences.UnionWith(currentReferences)");
        profiler.ShouldContain("private bool _enableComponentTiming = false;");
        preferences.ShouldContain("[DefaultValue(false)]");

        imageBackedTexture.ShouldContain("entry.AttachmentViews.Clear();");
        imageBackedTexture.ShouldContain("private sealed class PhysicalImageViewCacheEntry(");
        viewport.ShouldContain("_swapBuffersProfileName ??=");
        window.ShouldContain("for (int i = 0; i < viewports.Count; i++)");
        window.ShouldNotContain("StartProfileScope($\"XRViewport.Render[");
        window.ShouldNotContain("StartProfileScope($\"XRViewport.RenderToFBO[");
        events.ShouldNotContain("WithProfiling(\"XREvent.Invoke\", InvokeInternal)");
        events.ShouldContain("IDisposable? sample = BeginProfiling(\"XREvent.Invoke\")");
        uiInput.ShouldContain("_intersectionCollectionScratch");
        uiInput.ShouldNotContain(".Union(UIElementIntersections)");
        uiInput.ShouldNotContain("LastUIElementIntersections.ToArray()");
        collection2D.ShouldContain("private static readonly Action<RenderInfo2D> CollectRenderCommandsCallback");
        editorJobs.ShouldContain("if (_cachedSnapshotRevision == _snapshotRevision)");
        editorJobs.ShouldNotContain(".OrderByDescending(");
        directionalCascades.ShouldContain("private static ReusableBoxVolume GetCascadeCullVolumeScratch");

        vulkanMeshPipeline.ShouldContain("_programStateCache.TryGetValue(programState");
        vulkanMeshPipeline.ShouldContain("_programStateCache[programState] = entry;");
        generatedProgramState.ShouldContain("ReferenceEquals(VersionKindLabel, other.VersionKindLabel)");
        meshRendererBase.ShouldContain("_versionKindLabel ??= ResolveVersionKindLabel()");

        renderingHost.ShouldContain("Engine.StartPooledProfilerScope(");
        engine.ShouldContain("PooledExternalProfilerScope.Rent(Profiler.Start(sampleName))");
        profilerDumps.ShouldContain("finally");
        profilerDumps.ShouldContain("Engine.Profiler.EnableFrameLogging = false;");
        eventBase.ShouldContain("private Dictionary<(string Prefix, TListener Listener, int Index), string>? _listenerProfilingNames;");
        eventBase.ShouldContain("_listenerProfilingNames ??= [];");
    }

    private static void AssertOrdered(string source, params string[] markers)
    {
        int previous = -1;
        foreach (string marker in markers)
        {
            int index = source.IndexOf(marker, previous + 1, StringComparison.Ordinal);
            index.ShouldBeGreaterThan(
                previous,
                $"Expected '{marker}' after the previous binding-refresh stage.");
            previous = index;
        }
    }

    private static string ReadWorkspaceFile(string relativePath)
        => SourceContractWorkspace.ReadFile(relativePath);
}
