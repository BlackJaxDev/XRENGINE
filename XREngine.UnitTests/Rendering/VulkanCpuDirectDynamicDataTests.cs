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
    public void VulkanFrameDataProfiling_ExposesCurrentRecordingAndPublicationStages()
    {
        Enum.GetValues<EVulkanCpuStage>().ShouldContain(EVulkanCpuStage.FrameDataRefresh);
        Enum.GetValues<EVulkanCpuStage>().ShouldContain(EVulkanCpuStage.FrameDataManifest);
        Enum.GetValues<EVulkanCpuStage>().ShouldContain(EVulkanCpuStage.FrameDataDescriptorValidation);
        Enum.GetValues<EVulkanCpuStage>().ShouldContain(EVulkanCpuStage.CommandChainPacketLowering);
    }

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
    public void VulkanFrameDataArena_TracksMappedReservationsAndPublishesProfilerGauges()
    {
        string arena = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/Resources/Buffers/VulkanMappedFrameArena.cs");
        string profileCapture = ReadWorkspaceFile("XREngine/Engine/Engine.ProfileCapture.cs");

        arena.ShouldContain("internal bool TryReserve(");
        arena.ShouldContain("internal bool TryWriteIfChanged<T>");
        arena.ShouldContain("chunk.DirtyRange.Include(slice.Offset, slice.Length)");
        arena.ShouldContain("RecordVulkanMeshFrameDataGauges(");
        profileCapture.ShouldContain("vulkan_mesh_frame_data_reservations");
        profileCapture.ShouldContain("vulkan_mesh_frame_data_recording_leases");
    }

    [Test]
    public void DebugLinesKeepPowerOfTwoClientCapacityAndUseSubrangeUploads()
    {
        string debugLines = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Physics/DebugVisualization/InstancedDebugVisualizer.cs");

        debugLines.ShouldContain("_debugLinesBuffer.Resize(elementCount, true, true)");
        debugLines.ShouldContain("_debugLinesBuffer.CommitDirtyBytes(0u, _lineDirtyBytes)");
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
            ulong resolved = VkDataBuffer.ResolveResizableBufferCapacity(capacity, requiredBytes);
            if (resolved != capacity)
                publishedGenerations++;
            capacity = resolved;
        }

        capacity.ShouldBe(512UL);
        publishedGenerations.ShouldBe(2);

        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering.Vulkan/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs");
        source.ShouldContain("requiredByteSize > _bufferSize");
        source.ShouldNotContain("_bufferSize != Data.Length");
        source.ShouldContain("if (replacesExistingBacking)");
        source.ShouldContain("Renderer.MarkCommandBuffersDirty(\"VkDataBufferRecreated\")");
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
