using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class VulkanCpuDirectDynamicDataTests
{
    [Test]
    public void DirtyRange_MergesValueOnlyUpdatesWithoutAllocation()
    {
        VulkanDynamicDataDirtyRange range = default;

        range.Include(128UL, 32UL);
        range.Include(64UL, 16UL);
        range.Include(144UL, 64UL);

        range.Offset.ShouldBe(64UL);
        range.Length.ShouldBe(144UL);
        range.Clear();
        range.IsEmpty.ShouldBeTrue();
    }

    [Test]
    public void CpuDirectDynamicRecord_HasStableShaderFacingStride()
        => VulkanCpuDirectDynamicData.Stride.ShouldBe(160);

    [Test]
    public void DefaultOpaquePassesUseStateBucketingWhileOrderedPassesRemainUnchanged()
    {
        string collection = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandCollection.cs");
        string defaultPipeline = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/Pipelines/Types/Default/DefaultRenderPipeline.cs");

        collection.ShouldContain("class OpaqueStateBucketRenderCommandSorter");
        collection.ShouldContain("command is not IRenderCommandMesh");
        defaultPipeline.ShouldContain("EDefaultRenderPass.OpaqueDeferred, _opaqueStateBucketSorter");
        defaultPipeline.ShouldContain("EDefaultRenderPass.OpaqueForward, _opaqueStateBucketSorter");
        defaultPipeline.ShouldContain("EDefaultRenderPass.MaskedForward, _nearToFarSorter");
        defaultPipeline.ShouldContain("EDefaultRenderPass.TransparentForward, _farToNearSorter");
        collection.ShouldContain("int RenderPass,");
        collection.ShouldContain("int PipelineIdentity,");
        collection.ShouldContain("int LayoutIdentity,");
        collection.ShouldContain("int MeshBindingIdentity)");
    }

    [Test]
    public void CpuDirectDynamicDataUsesMappedFrameSlotsAndTracksChangedByteRanges()
    {
        string arena = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/Buffers/VulkanDynamicUniformRingBuffer.cs");
        string uniforms = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VkMeshRenderer.Uniforms.cs");
        string dynamicData = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/MeshRendering/VulkanCpuDirectDynamicData.cs");

        arena.ShouldContain("TryCaptureCpuDirectDynamicData");
        arena.ShouldContain("TryReserveMeshFrameDataRange(");
        arena.ShouldContain("WriteIfChanged(offset, dynamicData)");
        arena.ShouldContain("_dirtyRange.Include(offset, size)");
        uniforms.ShouldContain("Renderer.TryCaptureCpuDirectDynamicData(this, frameIndex, drawUniformSlot, draw");
        dynamicData.ShouldContain("uint TransformId)");
    }

    [Test]
    public void DebugLinesKeepPowerOfTwoClientCapacityAndUseSubrangeUploads()
    {
        string debugLines = ReadWorkspaceFile("XRENGINE/Scene/Physics/Physx/InstancedDebugVisualizer.cs");

        debugLines.ShouldContain("_debugLinesBuffer.Resize(elementCount, true, true)");
        debugLines.ShouldContain("_debugLinesBuffer.PushSubData()");
    }

    [Test]
    public void DebugLineClientStorageChangesLogicalCountWithoutReallocatingInsideCapacity()
    {
        using XRDataBuffer lines = new(
            "LinesBuffer",
            EBufferTarget.ShaderStorageBuffer,
            elementCount: 100u,
            EComponentType.Float,
            componentCount: 4u,
            normalize: false,
            integral: false,
            alignClientSourceToPowerOf2: true)
        {
            Resizable = true,
        };
        DataSource initialSource = lines.ClientSideSource.ShouldNotBeNull();

        lines.Resize(110u, copyData: true, alignClientSourceToPowerOf2: true).ShouldBeFalse();
        lines.ElementCount.ShouldBe(110u);
        lines.ClientSideSource.ShouldBeSameAs(initialSource);

        lines.Resize(129u, copyData: true, alignClientSourceToPowerOf2: true).ShouldBeTrue();
        lines.ElementCount.ShouldBe(129u);
        lines.ClientSideSource.ShouldNotBeSameAs(initialSource);
    }

    [Test]
    public void VulkanResizableCapacityPublishesOnlyOnOverflow()
    {
        ulong capacity = 0UL;
        int publishedGenerations = 0;
        ulong[] logicalByteCounts = [160UL, 224UL, 128UL, 256UL, 257UL, 400UL, 512UL];

        foreach (ulong requiredBytes in logicalByteCounts)
        {
            ulong resolved = VulkanRenderer.VkDataBuffer.ResolveResizableBufferCapacity(capacity, requiredBytes);
            if (resolved != capacity)
                publishedGenerations++;
            capacity = resolved;
        }

        capacity.ShouldBe(512UL);
        publishedGenerations.ShouldBe(2);

        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs");
        source.ShouldContain("requiredByteSize > _bufferSize");
        source.ShouldNotContain("_bufferSize != Data.Length");
        source.ShouldContain("if (replacesExistingBacking)");
        source.ShouldContain("Renderer.MarkCommandBuffersDirty(\"VkDataBufferRecreated\")");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string path = Path.Combine(
            ResolveRepoRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Expected workspace file '{relativePath}'.");
        return File.ReadAllText(path).Replace("\r\n", "\n");
    }

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
