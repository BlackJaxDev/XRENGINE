using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VkDataBufferParityContractTests
{
    [Test]
    public void XRRenderProgram_ResolvesSsboAndUboBlockBindingsFromShaderSource()
    {
        var shader = new XRShader(
            EShaderType.Compute,
            TextFile.FromText("""
                #version 460
                layout(local_size_x = 64) in;
                layout(std430, set = 0, binding = 7) readonly buffer DrawMetadataBuffer { uint Draws[]; };
                layout(std140, binding = 3) uniform CameraBlock { mat4 ViewProjection; };
                void main() { }
                """));

        var program = new XRRenderProgram(linkNow: false, separable: false, shader);

        program.TryResolveShaderStorageBufferBinding("DrawMetadataBuffer", out uint ssboBinding).ShouldBeTrue();
        ssboBinding.ShouldBe(7u);

        program.TryResolveUniformBlockBinding("CameraBlock", out uint uboBinding).ShouldBeTrue();
        uboBinding.ShouldBe(3u);

        program.TryResolveShaderStorageBufferBinding("MissingBlock", out _).ShouldBeFalse();
    }

    [Test]
    public void VkDataBuffer_SourceContracts_CoverGenerationMappingAndReadiness()
    {
        string source = ReadVkDataBufferSource();

        source.ShouldContain("public override bool IsGenerated => _vkBuffer.HasValue");
        source.ShouldNotContain("public override bool IsGenerated { get; }");
        source.ShouldContain("public ulong UploadedByteCount => _uploadedByteCount;");
        source.ShouldContain("public bool HasPendingUpload => _hasPendingUpload;");
        source.ShouldContain("public bool IsReadyForRendering => IsGenerated && !_hasPendingUpload");
        source.ShouldContain("private bool ShouldDisposeAfterUpload()");
        source.ShouldContain("=> Data.DisposeOnPush &&");
        source.ShouldContain("if (ShouldDisposeAfterUpload())\n                    Data.Dispose();");
        source.ShouldContain("if (Data.ShouldMap)");
        source.ShouldContain("if (Data.ActivelyMapping.Count > 0)");
        source.ShouldContain("EnsureStorageAllocatedForGpuUse();");
    }

    [Test]
    public void VkDataBuffer_SourceContracts_EventSubscriptionSymmetryMatchesOpenGl()
    {
        string glSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Buffers/GLDataBuffer.cs");
        string vkSource = ReadVkDataBufferSource();
        string dataSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Buffers/XRDataBuffer.cs");
        string[] eventBindings =
        [
            "PushDataRequested",
            "PushSubDataRequested",
            "FlushRequested",
            "FlushRangeRequested",
            "SetBlockNameRequested",
            "SetBlockIndexRequested",
            "BindRequested",
            "UnbindRequested",
            "MapBufferDataRequested",
            "UnmapBufferDataRequested",
            "BindSSBORequested",
        ];

        // The data buffer dispatches each backend operation directly to the API buffer of
        // its render owner. The request events are observer notifications only, so neither
        // backend subscribes to them.
        foreach (string backendSource in new[] { glSource, vkSource })
        {
            backendSource.ShouldContain("protected override void UnlinkData() { }");
            backendSource.ShouldContain("protected override void LinkData() { }");
            backendSource.ShouldContain("XRDataBuffer dispatches backend operations directly to its exact render owner.");
        }

        foreach (string eventBinding in eventBindings)
        {
            string linkPattern = $"Data.{eventBinding} +=";
            string unlinkPattern = $"Data.{eventBinding} -=";
            glSource.ShouldNotContain(linkPattern);
            vkSource.ShouldNotContain(linkPattern);
            glSource.ShouldNotContain(unlinkPattern);
            vkSource.ShouldNotContain(unlinkPattern);
            dataSource.ShouldContain($"{eventBinding}?.Invoke(");
        }

        dataSource.ShouldContain("apiBuffer.PushData();");
        dataSource.ShouldContain("apiBuffer.PushSubData(offset, length);");
        dataSource.ShouldContain("GetApiBufferForOwnerFirstUse().MapBufferData();");
        dataSource.ShouldContain("GetApiBufferForOwnerFirstUse().UnmapBufferData();");
        dataSource.ShouldContain("GetApiBufferForOwnerFirstUse().SetUniformBlockName(program, blockName);");
        dataSource.ShouldContain("GetApiBufferForOwnerFirstUse().SetBlockIndex(blockIndex);");
        dataSource.ShouldContain("GetApiBufferForOwnerFirstUse().Bind();");
        dataSource.ShouldContain("GetApiBufferForOwnerFirstUse().Unbind();");
        dataSource.ShouldContain("GetApiBufferForOwnerFirstUse().FlushRange(offset, length);");
        dataSource.ShouldContain("GetApiBufferForOwnerFirstUse().Flush();");
        dataSource.ShouldContain("GetApiBufferForOwnerFirstUse().BindSSBO(program, index);");
    }

    [Test]
    public void VkDataBuffer_SourceContracts_CoverMemoryFlagsFlushGrowthAndDiagnostics()
    {
        string source = ReadVkDataBufferSource();
        string bufferService = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Buffers/VulkanBufferResourceService.cs");

        source.ShouldContain("private static MemoryPropertyFlags ResolveMemoryProperties(XRDataBuffer data, ulong byteCount)");
        source.ShouldContain("MemoryPropertyFlags.HostCachedBit");
        source.ShouldContain("EBufferMapRangeFlags.FlushExplicit");
        source.ShouldContain("EBufferMapRangeFlags.Read");
        source.ShouldContain("EBufferMapStorageFlags.ClientStorage");
        source.ShouldContain("CanAllocateBufferVram(requestedAllocationBytes)");
        source.ShouldContain("_lastUploadRoute = \"SkippedVramBudget\"");
        source.ShouldContain("TracePushSubData(offset, clampedLength, \"immutable-no-dynstore-full-upload\")");
        source.ShouldContain("NormalizeMappedRange(offset, length, out ulong bufferOffset, out ulong mappedLength)");
        source.ShouldContain("BackendContext.Resources.Buffers.Flush(BackendContext, in slice);");
        source.ShouldContain("_lastUploadRoute = \"DeviceLocalGpuDecompression\"");
        source.ShouldContain("_lastUploadRoute = \"DeviceLocalCompressedFallbackMissingCpuData\"");
        source.ShouldContain("RecordUploadDiagnostics((long)_bufferSize, recreate: needsRecreate, fullUpload: true)");
        source.ShouldContain("RecordRendererStateCounter(ERendererProfilerCounter.BufferUploadBytes");

        // Mapped leases own flush and invalidation of non-coherent memory ranges.
        bufferService.ShouldContain("internal void Flush(VulkanBackendObjectContext context, in VulkanMappedMemorySlice slice)");
        bufferService.ShouldContain("context.Api.FlushMappedMemoryRanges(context.Device, 1, ref range)");
        bufferService.ShouldContain("if (!write && !slice.IsCoherent)");
        bufferService.ShouldContain("context.Api.InvalidateMappedMemoryRanges(context.Device, 1, ref range)");
    }

    [Test]
    public void VkDataBuffer_SourceContracts_CoverBindingAndVulkanNativePaths()
    {
        string bufferSource = ReadVkDataBufferSource();
        string rendererProgramSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Resources/Shaders/XRRenderProgram.cs");
        string addressSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Buffers/VulkanBufferResourceService.cs");
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");
        string cleanupSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Cleanup.cs");
        string hybridSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");
        string glBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.OpenGL/Rendering/API/Rendering/OpenGL/BackendObjects/Buffers/GLDataBuffer.cs");
        string barrierPlannerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanBarrierPlanner.cs");
        string barrierUsageSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanBarrierUsageMapper.cs");
        string retirementSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceRetirement.cs");
        string transferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.BufferTransfer.cs");

        bufferSource.ShouldContain("program.TryResolveShaderStorageBufferBinding(blockName, out uint binding)");
        bufferSource.ShouldContain("program.TryResolveUniformBlockBinding(blockName, out binding)");
        bufferSource.ShouldContain("_resolvedProgramBindings[program] = binding;");
        bufferSource.ShouldContain("Data.BindingIndexOverride = blockIndex;");
        bufferSource.ShouldContain("BackendContext.Resources.Buffers.ResolveDeviceAddressStatus(BackendContext, Data, DeviceAddress)");
        bufferSource.ShouldContain("TrackCurrentBufferVramAllocation();");
        bufferSource.ShouldContain("BackendContext.Resources.Buffers.TryGetAllocation(_vkBuffer.Value, out VulkanMemoryAllocation allocation)");
        bufferSource.ShouldContain("VulkanBufferResourceService.IsDeviceLocalVramAllocation(allocation.Properties)");
        bufferSource.ShouldContain("BackendContext.Resources.TryAcquireSynchronousFrameDataArenaLease(");
        bufferSource.ShouldContain("BackendContext.Resources.Buffers.TryCreateMappedSlice(");
        bufferSource.ShouldContain("BackendContext.Resources.SynchronousCommands.CopyBuffer(");
        bufferSource.ShouldContain("memProps=");
        bufferSource.ShouldContain("deviceAddressStatus=");
        bufferSource.ShouldContain("Projected={2} bytes, Budget={3} bytes");

        rendererProgramSource.ShouldContain("TryResolveShaderStorageBufferBinding");
        rendererProgramSource.ShouldContain("BufferBlockDeclarationRegex");
        rendererProgramSource.ShouldContain("LayoutBindingRegex");

        addressSource.ShouldContain("fallback-descriptor-buffer-device-address-unsupported");
        addressSource.ShouldContain("resolved-device-address");
        addressSource.ShouldContain("internal void RecordDeviceAddressConsumer(");
        addressSource.ShouldContain("RecordAllocationTelemetry(properties, checked((long)requirements.Size));");

        pipelineSource.ShouldContain("WarnMissingVertexAttribute");
        pipelineSource.ShouldContain("buffer.Data.Normalize");
        pipelineSource.ShouldContain("instanceDivisor");

        cleanupSource.ShouldContain("Format.R8G8B8A8Unorm");
        cleanupSource.ShouldContain("Format.R16G16B16A16SNorm");

        hybridSource.ShouldContain("GL_EXT_buffer_reference");
        hybridSource.ShouldContain("SceneDatabaseDrawMetadataAddressUniform");
        hybridSource.ShouldContain("TryBindSceneDatabaseDeviceAddressUniforms");
        hybridSource.ShouldContain("XRE_DrawMetadataBufferRef");
        hybridSource.ShouldContain("XRE_TransformBufferRef");

        glBufferSource.ShouldContain("public uint UploadedByteCount => _lastPushedLength;");
        glBufferSource.ShouldContain("[GLBufferUpload]");
        glBufferSource.ShouldContain("bindingReady");
        glBufferSource.ShouldContain("readbackReady");
        glBufferSource.ShouldContain("RecordMappedReadbackBytes");

        barrierPlannerSource.ShouldContain("ShouldTrackBuffer");
        barrierPlannerSource.ShouldContain("ERenderPassResourceType.VertexBuffer");
        barrierPlannerSource.ShouldContain("ERenderPassResourceType.IndexBuffer");
        barrierPlannerSource.ShouldContain("ERenderPassResourceType.IndirectBuffer");
        // The usage mapper owns the access masks that the barrier planner uses.
        barrierPlannerSource.ShouldContain("VulkanBarrierUsageMapper.ResolveAccess(");
        barrierUsageSource.ShouldContain("AccessFlags.ShaderWriteBit");
        barrierUsageSource.ShouldContain("AccessFlags.TransferWriteBit");
        barrierUsageSource.ShouldContain("AccessFlags.IndirectCommandReadBit");

        // Buffer retirement is owned by the buffer resource service and drained per frame slot.
        retirementSource.ShouldContain("RetireBuffer(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory)");
        retirementSource.ShouldContain("ResourceRuntime.Buffers.Retire(buffer, memory,");
        retirementSource.ShouldContain("WaitForTimelineValue");
        addressSource.ShouldContain("lifetime.Retirement.Buffers[frameSlot].Add(new RetiredBuffer(buffer, memory, ticket));");

        transferSource.ShouldContain("RecordTransferQueuePolicyDiagnostics(");
    }

    [Test]
    public void VkDataBuffer_SourceContracts_CoverSteadyStateCountersAndZeroReadbackTelemetry()
    {
        string bufferSource = ReadVkDataBufferSource();
        string runtimeSource =
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.Forwarders.cs") +
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.Vulkan.cs") +
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/Statistics/RuntimeEngine.Rendering.Stats.Readback.cs");
        string telemetrySource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Buffers/XRBufferWriteTelemetry.cs");
        string stagingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Uploads/VulkanStagingManager.cs");
        string transferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Commands/Authority/VulkanCommandRuntime.BufferTransfer.cs");

        // Steady-state device-local uploads reuse the persistent frame-data arena
        // instead of a new staging allocation for each upload.
        bufferSource.ShouldContain("private bool UploadDeviceLocalRangeFromFrameDataArena(");
        bufferSource.ShouldContain("using VulkanSynchronousFrameDataArenaLease ownedLease = lease;");
        bufferSource.ShouldContain("_lastUploadRoute = \"DeviceLocalSubDataFrameDataArena\";");
        bufferSource.ShouldContain("_lastUploadRoute = ResolveHostVisibleSubDataUploadRoute(_lastMemProps);");
        bufferSource.ShouldContain("MemoryPropertyFlags.HostVisibleBit");
        bufferSource.ShouldContain("MemoryPropertyFlags.HostCachedBit");
        bufferSource.ShouldContain("RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuReadbackBytes(count);");
        bufferSource.ShouldContain("XRBufferWriteTelemetry.RecordHostCachedReadback(count);");
        bufferSource.ShouldContain("RuntimeEngine.Rendering.Stats.RecordRendererStateCounter(ERendererProfilerCounter.BufferUploadBytes, clampedLength);");
        bufferSource.ShouldContain("RuntimeEngine.Rendering.Stats.Vram.AddBufferAllocation(_allocatedVRAMBytes);");
        transferSource.ShouldContain("RecordTransferQueuePolicyDiagnostics(");

        runtimeSource.ShouldContain("RecordRendererStateCounter");
        runtimeSource.ShouldContain("RecordVulkanDescriptorFallback");
        runtimeSource.ShouldContain("RecordVulkanDescriptorBindingFailure");
        runtimeSource.ShouldContain("RecordGpuReadbackBytes");

        telemetrySource.ShouldContain("RecordZeroReadbackViolation");
        telemetrySource.ShouldContain("ZeroReadbackViolations");
        telemetrySource.ShouldContain("RecordUpload");
        telemetrySource.ShouldContain("AppendProfilerSummary");

        stagingSource.ShouldContain("TryTakeReusable");
        stagingSource.ShouldContain("Return");
        stagingSource.ShouldContain("Trim");
        stagingSource.ShouldContain("HostCachedBit");
    }

    /// <summary>Reads every partial source file of <c>VkDataBuffer</c>.</summary>
    private static string ReadVkDataBufferSource()
        => SourceContractWorkspace.ReadPartialType(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs");

    private static string ReadWorkspaceFile(string relativePath)
        => SourceContractWorkspace.ReadFile(relativePath);

    private static string ResolveWorkspacePath(string relativePath)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not resolve workspace path for '{relativePath}' from test base directory '{AppContext.BaseDirectory}'.");
    }
}
