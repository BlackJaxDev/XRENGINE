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
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs");

        source.ShouldContain("public override bool IsGenerated => _vkBuffer.HasValue");
        source.ShouldNotContain("public override bool IsGenerated { get; }");
        source.ShouldContain("public ulong UploadedByteCount => _uploadedByteCount;");
        source.ShouldContain("public bool HasPendingUpload => _hasPendingUpload;");
        source.ShouldContain("public bool IsReadyForRendering => IsGenerated && !_hasPendingUpload");
        source.ShouldContain("private bool ShouldDisposeAfterUpload()");
        source.ShouldContain("if (Data.ShouldMap)");
        source.ShouldContain("if (Data.ActivelyMapping.Count > 0)");
        source.ShouldContain("EnsureStorageAllocatedForGpuUse();");
        source.ShouldContain("GPUSideSource?.Dispose();");
    }

    [Test]
    public void VkDataBuffer_SourceContracts_EventSubscriptionSymmetryMatchesOpenGl()
    {
        string glSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Buffers/GLDataBuffer.cs");
        string vkSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs");
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

        foreach (string eventBinding in eventBindings)
        {
            string linkPattern = $"Data.{eventBinding} +=";
            string unlinkPattern = $"Data.{eventBinding} -=";
            glSource.ShouldContain(linkPattern);
            vkSource.ShouldContain(linkPattern);
            glSource.ShouldContain(unlinkPattern);
            vkSource.ShouldContain(unlinkPattern);
        }
    }

    [Test]
    public void VkDataBuffer_SourceContracts_CoverMemoryFlagsFlushGrowthAndDiagnostics()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs");

        source.ShouldContain("private static MemoryPropertyFlags ResolveMemoryProperties(XRDataBuffer data)");
        source.ShouldContain("MemoryPropertyFlags.HostCachedBit");
        source.ShouldContain("EBufferMapRangeFlags.FlushExplicit");
        source.ShouldContain("EBufferMapRangeFlags.InvalidateRange");
        source.ShouldContain("EBufferMapStorageFlags.ClientStorage");
        source.ShouldContain("CanAllocateBufferVram(requestedAllocationBytes)");
        source.ShouldContain("_lastUploadRoute = \"SkippedVramBudget\"");
        source.ShouldContain("TracePushSubData(offset, clampedLength, \"immutable-no-dynstore-full-upload\")");
        source.ShouldContain("NormalizeMappedRange(offset, length, out ulong memoryOffset, out ulong mappedLength)");
        source.ShouldContain("Renderer.FlushBuffer(_vkMemory, memoryOffset, mappedLength)");
        source.ShouldContain("Renderer.InvalidateBuffer(_vkMemory, GetMappedMemoryOffset(0), _bufferSize)");
        source.ShouldContain("_lastUploadRoute = \"DeviceLocalGpuDecompression\"");
        source.ShouldContain("_lastUploadRoute = \"DeviceLocalCompressedFallbackMissingCpuData\"");
        source.ShouldContain("RecordUploadDiagnostics((long)_bufferSize, recreate: needsRecreate, fullUpload: true)");
        source.ShouldContain("RecordRendererStateCounter(ERendererProfilerCounter.BufferUploadBytes");
    }

    [Test]
    public void VkDataBuffer_SourceContracts_CoverBindingAndVulkanNativePaths()
    {
        string bufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs");
        string rendererProgramSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Resources/Shaders/XRRenderProgram.cs");
        string addressSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/Buffers/VulkanSceneDatabaseAddresses.cs");
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Pipeline.cs");
        string cleanupSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Cleanup.cs");
        string hybridSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs");
        string glBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Buffers/GLDataBuffer.cs");
        string barrierPlannerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/RenderGraph/VulkanBarrierPlanner.cs");
        string retirementSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.ResourceRetirement.cs");

        bufferSource.ShouldContain("program.TryResolveShaderStorageBufferBinding(blockName, out uint binding)");
        bufferSource.ShouldContain("program.TryResolveUniformBlockBinding(blockName, out binding)");
        bufferSource.ShouldContain("_resolvedProgramBindings[program] = binding;");
        bufferSource.ShouldContain("Data.BindingIndexOverride = blockIndex;");
        bufferSource.ShouldContain("Renderer.ResolveSceneDatabaseDeviceAddressStatus(Data, DeviceAddress)");
        bufferSource.ShouldContain("RecordBufferAllocationDiagnostics");
        bufferSource.ShouldContain("MemoryAllocator.ActiveVkAllocationCount");
        bufferSource.ShouldContain("Suballocated");
        bufferSource.ShouldContain("private readonly VulkanStagingManager _stagingManager = new();");
        bufferSource.ShouldContain("NormalizeMappedMemoryRange(vkMemory.Value, offset, length");
        bufferSource.ShouldContain("RecordTransferQueuePolicyDiagnostics");
        bufferSource.ShouldContain("memoryHeap=");
        bufferSource.ShouldContain("requirementsSize=");
        bufferSource.ShouldContain("alignment=");
        bufferSource.ShouldContain("trackedVramBudgetBytes=");

        rendererProgramSource.ShouldContain("TryResolveShaderStorageBufferBinding");
        rendererProgramSource.ShouldContain("BufferBlockDeclarationRegex");
        rendererProgramSource.ShouldContain("LayoutBindingRegex");

        addressSource.ShouldContain("fallback-descriptor-buffer-device-address-unsupported");
        addressSource.ShouldContain("resolved-device-address");
        addressSource.ShouldContain("RecordSceneDatabaseDeviceAddressConsumer");

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
        barrierPlannerSource.ShouldContain("AccessFlags.ShaderWriteBit");
        barrierPlannerSource.ShouldContain("AccessFlags.TransferWriteBit");
        barrierPlannerSource.ShouldContain("AccessFlags.IndirectCommandReadBit");

        retirementSource.ShouldContain("RetireBuffer(Silk.NET.Vulkan.Buffer buffer, DeviceMemory memory)");
        retirementSource.ShouldContain("_retiredBuffers[frameSlot].Add");
        retirementSource.ShouldContain("WaitForTimelineValue");
    }

    [Test]
    public void VkDataBuffer_SourceContracts_CoverSteadyStateCountersAndZeroReadbackTelemetry()
    {
        string bufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs");
        string runtimeSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Runtime/RuntimeEngine.cs");
        string telemetrySource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Buffers/XRBufferWriteTelemetry.cs");
        string stagingSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/Uploads/VulkanStagingManager.cs");

        bufferSource.ShouldContain("private readonly VulkanStagingManager _stagingManager = new();");
        bufferSource.ShouldContain("_lastUploadRoute = ResolveHostVisibleUploadRoute(_lastMemProps) + \"SubData\";");
        bufferSource.ShouldContain("MemoryPropertyFlags.HostVisibleBit");
        bufferSource.ShouldContain("MemoryPropertyFlags.HostCachedBit");
        bufferSource.ShouldContain("RuntimeEngine.Rendering.Stats.GpuReadback.RecordGpuReadbackBytes(count);");
        bufferSource.ShouldContain("XRBufferWriteTelemetry.RecordHostCachedReadback(count);");
        bufferSource.ShouldContain("RuntimeEngine.Rendering.Stats.RecordRendererStateCounter(ERendererProfilerCounter.BufferUploadBytes, clampedLength);");
        bufferSource.ShouldContain("RecordBufferAllocationDiagnostics");
        bufferSource.ShouldContain("RecordTransferQueuePolicyDiagnostics");

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

    private static string ReadWorkspaceFile(string relativePath)
    {
        string fullPath = ResolveWorkspacePath(relativePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected file does not exist: {fullPath}");
        return File.ReadAllText(fullPath);
    }

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
