using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Meshlets;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class MeshOptimizerInteropTests
{
    [Test]
    public void OptimizeMeshletLevel_UsesAvailableNativeExportWithoutThrowing()
    {
        uint[] meshletVertices = [0u, 1u, 2u];
        byte[] meshletTriangles = [0, 1, 2];

        Assert.DoesNotThrow(() => MeshOptimizerNative.OptimizeMeshletLevel(meshletVertices.AsSpan(), meshletTriangles.AsSpan(), level: 4));
    }

    [Test]
    public void BuildMeshlets_WithManyTinyTriangles_HandlesPaddedTriangleOffsets()
    {
        XRMesh mesh = CreateManyTinyTriangleMesh("PaddedTriangleOffsets", 240);
        MeshletBuildResult result = MeshOptimizerIntegration.BuildMeshlets(
            mesh,
            new MeshletGenerationSettings
            {
                Enabled = true,
                BuildMode = MeshletBuildMode.Dense,
                MaxVertices = 64u,
                MaxTriangles = 124u,
                OptimizeMeshlets = true,
                ComputeBounds = true,
            });

        result.Meshlets.Length.ShouldBeGreaterThan(1);
        result.TriangleIndices.Length.ShouldBe(result.Stats.TriangleByteCount);
        result.Meshlets.Any(static meshlet => meshlet.TriangleOffset % 3u != 0u)
            .ShouldBeTrue("meshlet triangle offsets must preserve meshoptimizer byte offsets, including padding between meshlets.");

        foreach (Meshlet meshlet in result.Meshlets)
        {
            long lastTriangleByte = (long)meshlet.TriangleOffset + (long)meshlet.TriangleCount * 3L;
            lastTriangleByte.ShouldBeLessThanOrEqualTo(result.TriangleIndices.Length);
        }
    }

    [Test]
    public void BuildMeshlets_WritesCookedPayloadWithConeFreshnessAndSettings()
    {
        XRMesh mesh = CreateManyTinyTriangleMesh("CookedMeshletPayload", 240);
        MeshletGenerationSettings meshletSettings = CreateEnabledDenseSettings();
        MeshLodGenerationSettings lodSettings = new()
        {
            Enabled = true,
            AdditionalLodCount = 3,
            FirstLodIndexRatio = 0.45f,
            TargetError = 0.02f,
            ProtectAttributeSeams = true,
        };
        const string sourceMeshIdentity = "Assets/Models/Phase2/CookedMeshletPayload";

        MeshletBuildResult result = MeshOptimizerIntegration.BuildMeshlets(mesh, meshletSettings, lodSettings, sourceMeshIdentity);
        MeshletPayload payload = result.Payload;
        mesh.MeshletPayload = payload;

        payload.GenerationEnabled.ShouldBeTrue();
        payload.SourceMeshIdentity.ShouldBe(sourceMeshIdentity);
        payload.MeshOptimizerVersionKey.ShouldBe(MeshOptimizerIntegration.MeshOptimizerVersionKey);
        payload.MeshletSettings.BuildMode.ShouldBe(MeshletBuildMode.Dense);
        payload.MeshletSettings.MaxVertices.ShouldBe(meshletSettings.MaxVertices);
        payload.LodSettings.Enabled.ShouldBeTrue();
        payload.LodSettings.AdditionalLodCount.ShouldBe(lodSettings.AdditionalLodCount);
        payload.SourceMeshHash.ShouldNotBe(0UL);
        payload.MeshletSettingsHash.ShouldNotBe(0UL);
        payload.LodSettingsHash.ShouldNotBe(0UL);
        payload.FreshnessHash.ShouldNotBe(0UL);
        payload.Meshlets.Length.ShouldBe(result.Meshlets.Length);
        payload.VertexIndices.ShouldBe(result.VertexIndices);
        payload.TriangleIndices.ShouldBe(result.TriangleIndices);
        payload.Stats.ShouldBe(result.Stats);
        payload.Meshlets.Any(static descriptor => descriptor.BoundsSphere.W > 0.0f).ShouldBeTrue();
        payload.IsFreshFor(mesh, meshletSettings, lodSettings, sourceMeshIdentity).ShouldBeTrue();

        byte[] cookedPayload = RuntimeCookedBinarySerializer.ExecuteWithMemoryPackSuppressed(
            () => RuntimeCookedBinarySerializer.Serialize(mesh));
        XRMesh clone = RuntimeCookedBinarySerializer.ExecuteWithMemoryPackSuppressed(
            () => RuntimeCookedBinarySerializer.Deserialize(typeof(XRMesh), cookedPayload) as XRMesh).ShouldNotBeNull();

        MeshletPayload clonePayload = clone.MeshletPayload.ShouldNotBeNull();
        clonePayload.GenerationEnabled.ShouldBe(payload.GenerationEnabled);
        clonePayload.SourceMeshIdentity.ShouldBe(payload.SourceMeshIdentity);
        clonePayload.MeshOptimizerVersionKey.ShouldBe(payload.MeshOptimizerVersionKey);
        clonePayload.SourceVertexCount.ShouldBe(payload.SourceVertexCount);
        clonePayload.SourceTriangleCount.ShouldBe(payload.SourceTriangleCount);
        clonePayload.SourceMeshHash.ShouldBe(payload.SourceMeshHash);
        clonePayload.MeshletSettingsHash.ShouldBe(payload.MeshletSettingsHash);
        clonePayload.LodSettingsHash.ShouldBe(payload.LodSettingsHash);
        clonePayload.FreshnessHash.ShouldBe(payload.FreshnessHash);
        clonePayload.MeshletSettings.ShouldBe(payload.MeshletSettings);
        clonePayload.LodSettings.ShouldBe(payload.LodSettings);
        clonePayload.Meshlets.ShouldBe(payload.Meshlets);
        clonePayload.VertexIndices.ShouldBe(payload.VertexIndices);
        clonePayload.TriangleIndices.ShouldBe(payload.TriangleIndices);
        clonePayload.Vertices.Length.ShouldBe(payload.Vertices.Length);
        clonePayload.Stats.ShouldBe(payload.Stats);
        clonePayload.IsFreshFor(clone, meshletSettings, lodSettings, sourceMeshIdentity).ShouldBeTrue();
    }

    [Test]
    public void MeshletCollection_AddMesh_UsesFreshCachedPayloadWithoutMeshoptimizerBuild()
    {
        XRMesh mesh = CreateManyTinyTriangleMesh("CachedMeshletCollectionMesh", 240);
        MeshletGenerationSettings settings = CreateEnabledDenseSettings();

        mesh.GetOrCreateMeshletPayload(settings);
        mesh.MeshletPayload.ShouldNotBeNull().HasMeshlets.ShouldBeTrue();

        MeshOptimizerIntegration.ResetMeshletBuildDiagnosticsForTests();
        using MeshletCollection collection = new();
        collection.AddMesh(mesh, instanceID: 7u, materialID: 3u, renderPass: 0, Matrix4x4.Identity, settings);

        MeshOptimizerIntegration.MeshletBuildInvocationCount.ShouldBe(0);
    }

    [Test]
    public void MeshletCollection_AddMesh_MissingCachedPayloadDoesNotBuildMeshlets()
    {
        XRMesh mesh = CreateManyTinyTriangleMesh("MissingCachedMeshletCollectionMesh", 240);
        MeshletGenerationSettings settings = CreateEnabledDenseSettings();

        MeshOptimizerIntegration.ResetMeshletBuildDiagnosticsForTests();
        using MeshletCollection collection = new();
        collection.AddMesh(mesh, instanceID: 8u, materialID: 4u, renderPass: 0, Matrix4x4.Identity, settings);

        mesh.MeshletPayload.ShouldBeNull();
        MeshOptimizerIntegration.MeshletBuildInvocationCount.ShouldBe(0);
    }

    [Test]
    public void DisabledMeshletSettings_CreatesEmptyManifestPayloadWithoutBuild()
    {
        XRMesh mesh = CreateManyTinyTriangleMesh("DisabledMeshletPayload", 12);
        MeshletGenerationSettings settings = new()
        {
            Enabled = false,
            BuildMode = MeshletBuildMode.Dense,
        };

        MeshOptimizerIntegration.ResetMeshletBuildDiagnosticsForTests();
        MeshletPayload payload = mesh.GetOrCreateMeshletPayload(settings, lodSettings: null, sourceMeshIdentity: "disabled-source");

        payload.GenerationEnabled.ShouldBeFalse();
        payload.MeshletSettings.Enabled.ShouldBeFalse();
        payload.SourceMeshIdentity.ShouldBe("disabled-source");
        payload.Meshlets.ShouldBeEmpty();
        payload.VertexIndices.ShouldBeEmpty();
        payload.TriangleIndices.ShouldBeEmpty();
        payload.Vertices.ShouldBeEmpty();
        payload.Stats.ShouldBe(new MeshOptimizerMeshletStats(0, 0, 0, 0));
        payload.FreshnessHash.ShouldNotBe(0UL);
        payload.IsFreshFor(mesh, settings, lodSettings: null, sourceMeshIdentity: "disabled-source").ShouldBeTrue();
        MeshOptimizerIntegration.MeshletBuildInvocationCount.ShouldBe(0);
    }

    [Test]
    public void Meshlets_AreRebuiltLazilyInsteadOfDuringGpuSceneSwap()
    {
        string gpuSceneSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.CommandBuffers.cs").Replace("\r\n", "\n");
        string hybridSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs").Replace("\r\n", "\n");

        string swapBody = ExtractSwapCommandBuffersBody(gpuSceneSource);
        swapBody.ShouldNotContain("RebuildMeshletsFromUpdatingCommands");
        gpuSceneSource.ShouldNotContain("RebuildMeshletsFromUpdatingCommands");
        gpuSceneSource.ShouldContain("RebuildDebugMeshletCollectionFromUpdatingCommands");
        gpuSceneSource.ShouldContain("public bool RenderMeshlets(XRCamera camera, int renderPass)");
        hybridSource.ShouldContain("TryGetMeshletExpansionInputs");
        hybridSource.ShouldNotContain("scene.RenderMeshlets(");
    }

    [Test]
    public void GPUScene_RegistersCachedMeshletPayloadIntoSceneOwnedBuffers()
    {
        XRMesh mesh = CreateManyTinyTriangleMesh("SceneOwnedMeshletStorage", 240);
        MeshletGenerationSettings settings = CreateEnabledDenseSettings();
        MeshletPayload payload = mesh.GetOrCreateMeshletPayload(settings);
        payload.HasMeshlets.ShouldBeTrue();

        GPUScene scene = new();
        try
        {
            scene.RegisterLogicalMeshLODs([(mesh, 0.0f)], out uint logicalMeshId, out string? failureReason)
                .ShouldBeTrue(failureReason);
            scene.TryGetLodTableEntry(logicalMeshId, out GPUScene.LODTableEntry lodEntry).ShouldBeTrue();
            scene.TryGetMeshletRange(lodEntry.LOD0_MeshDataID, out GPUScene.GpuMeshletRange range).ShouldBeTrue();

            range.MeshletCount.ShouldBe((uint)payload.Meshlets.Length);
            scene.MeshletDescriptorCount.ShouldBe(payload.Meshlets.Length);
            scene.MeshletVertexIndexCount.ShouldBe(payload.VertexIndices.Length);
            scene.MeshletTriangleIndexByteCount.ShouldBe(payload.TriangleIndices.Length);
            scene.MeshletDescriptorBuffer.ElementCount.ShouldBeGreaterThanOrEqualTo(range.MeshletOffset + range.MeshletCount);
            scene.MeshletVertexIndexBuffer.ElementCount.ShouldBeGreaterThanOrEqualTo(range.VertexIndexOffset + (uint)payload.VertexIndices.Length);
            scene.MeshletTriangleIndexBuffer.ElementCount.ShouldBeGreaterThanOrEqualTo(range.TriangleIndexOffset + (uint)payload.TriangleIndices.Length);

            GPUScene.GpuMeshletDescriptor descriptor = scene.MeshletDescriptorBuffer.GetDataRawAtIndex<GPUScene.GpuMeshletDescriptor>(range.MeshletOffset);
            CpuMeshletDescriptor expected = payload.Meshlets[0];
            descriptor.BoundsSphere.ShouldBe(expected.BoundsSphere);
            descriptor.VertexOffset.ShouldBe(expected.VertexOffset + range.VertexIndexOffset);
            descriptor.TriangleByteOffset.ShouldBe(expected.TriangleOffset + range.TriangleIndexOffset);
            descriptor.VertexCount.ShouldBe(expected.VertexCount);
            descriptor.TriangleCount.ShouldBe(expected.TriangleCount);
            descriptor.Cone.ShouldBe(expected.Cone);
            descriptor.ConeApex.ShouldBe(expected.ConeApex);
            descriptor.PackedCone.ShouldBe(expected.PackedCone);
            scene.MeshletVertexIndexBuffer.GetDataRawAtIndex<uint>(range.VertexIndexOffset).ShouldBe(payload.VertexIndices[0]);
            scene.MeshletTriangleIndexBuffer.GetDataRawAtIndex<byte>(range.TriangleIndexOffset).ShouldBe(payload.TriangleIndices[0]);

            int descriptorCount = scene.MeshletDescriptorCount;
            scene.RegisterLogicalMeshLODs([(mesh, 0.0f)], out _, out failureReason)
                .ShouldBeTrue(failureReason);
            scene.MeshletDescriptorCount.ShouldBe(descriptorCount);
        }
        finally
        {
            scene.Destroy();
        }
    }

    [Test]
    public void GPUScene_MissingMeshletPayloadRegistersEmptyMeshletRange()
    {
        XRMesh mesh = CreateManyTinyTriangleMesh("SceneOwnedEmptyMeshletRange", 24);

        GPUScene scene = new();
        try
        {
            scene.RegisterLogicalMeshLODs([(mesh, 0.0f)], out uint logicalMeshId, out string? failureReason)
                .ShouldBeTrue(failureReason);
            scene.TryGetLodTableEntry(logicalMeshId, out GPUScene.LODTableEntry lodEntry).ShouldBeTrue();
            scene.TryGetMeshletRange(lodEntry.LOD0_MeshDataID, out GPUScene.GpuMeshletRange range).ShouldBeTrue();

            range.MeshletCount.ShouldBe(0u);
            range.HasMeshlets.ShouldBeFalse();
            range.RequiresTraditionalIndirectFallback.ShouldBeTrue();
            scene.HasRenderableMeshlets(lodEntry.LOD0_MeshDataID).ShouldBeFalse();
            scene.RequiresTraditionalIndirectForMeshlets(lodEntry.LOD0_MeshDataID).ShouldBeTrue();
            range.MeshletOffset.ShouldBe(0u);
            range.VertexIndexOffset.ShouldBe(0u);
            range.TriangleIndexOffset.ShouldBe(0u);
            scene.MeshletDescriptorCount.ShouldBe(0);
        }
        finally
        {
            scene.Destroy();
        }
    }

    [Test]
    public void GPUScene_RuntimeMeshletRepairBuildsMissingResidentPayloadForMeshletDispatch()
    {
        XRMesh mesh = CreateManyTinyTriangleMesh("RuntimeMeshletRepair", 240);

        GPUScene scene = new();
        try
        {
            scene.RegisterLogicalMeshLODs([(mesh, 0.0f)], out uint logicalMeshId, out string? failureReason)
                .ShouldBeTrue(failureReason);
            scene.TryGetLodTableEntry(logicalMeshId, out GPUScene.LODTableEntry lodEntry).ShouldBeTrue();
            scene.TryGetMeshletRange(lodEntry.LOD0_MeshDataID, out GPUScene.GpuMeshletRange initialRange).ShouldBeTrue();
            initialRange.MeshletCount.ShouldBe(0u);
            mesh.MeshletPayload.ShouldBeNull();

            MeshOptimizerIntegration.ResetMeshletBuildDiagnosticsForTests();
            scene.EnsureRuntimeMeshletPayloadsForMeshletDispatch().ShouldBe(1u);

            MeshletPayload payload = mesh.MeshletPayload.ShouldNotBeNull();
            payload.HasMeshlets.ShouldBeTrue();
            MeshOptimizerIntegration.MeshletBuildInvocationCount.ShouldBe(1L);
            scene.TryGetMeshletRange(lodEntry.LOD0_MeshDataID, out GPUScene.GpuMeshletRange repairedRange).ShouldBeTrue();
            repairedRange.MeshletCount.ShouldBe((uint)payload.Meshlets.Length);
            scene.HasRenderableMeshlets(lodEntry.LOD0_MeshDataID).ShouldBeTrue();

            scene.EnsureRuntimeMeshletPayloadsForMeshletDispatch().ShouldBe(0u);
            MeshOptimizerIntegration.MeshletBuildInvocationCount.ShouldBe(1L);
        }
        finally
        {
            scene.Destroy();
        }
    }

    [Test]
    public void GPUScene_TracksMeshletRangesPerLodMeshDataId()
    {
        MeshletGenerationSettings settings = CreateEnabledDenseSettings();
        XRMesh lod0 = CreateManyTinyTriangleMesh("SceneOwnedLod0Meshlets", 240);
        XRMesh lod1 = CreateManyTinyTriangleMesh("SceneOwnedLod1Meshlets", 96);
        MeshletPayload lod0Payload = lod0.GetOrCreateMeshletPayload(settings);
        MeshletPayload lod1Payload = lod1.GetOrCreateMeshletPayload(settings);

        GPUScene scene = new();
        try
        {
            scene.RegisterLogicalMeshLODs([(lod0, 96.0f), (lod1, 0.0f)], out uint logicalMeshId, out string? failureReason)
                .ShouldBeTrue(failureReason);
            scene.TryGetLodTableEntry(logicalMeshId, out GPUScene.LODTableEntry lodEntry).ShouldBeTrue();
            lodEntry.LODCount.ShouldBe(2u);
            lodEntry.LOD0_MeshDataID.ShouldNotBe(lodEntry.LOD1_MeshDataID);

            scene.TryGetMeshletRange(lodEntry.LOD0_MeshDataID, out GPUScene.GpuMeshletRange lod0Range).ShouldBeTrue();
            scene.TryGetMeshletRange(lodEntry.LOD1_MeshDataID, out GPUScene.GpuMeshletRange lod1Range).ShouldBeTrue();

            lod0Range.MeshletCount.ShouldBe((uint)lod0Payload.Meshlets.Length);
            lod1Range.MeshletCount.ShouldBe((uint)lod1Payload.Meshlets.Length);
            lod1Range.MeshletOffset.ShouldBe(lod0Range.MeshletOffset + lod0Range.MeshletCount);
            lod1Range.VertexIndexOffset.ShouldBe(lod0Range.VertexIndexOffset + (uint)lod0Payload.VertexIndices.Length);
            lod1Range.TriangleIndexOffset.ShouldBe(lod0Range.TriangleIndexOffset + (uint)lod0Payload.TriangleIndices.Length);
            scene.TryValidateResidentLodMeshletRanges(logicalMeshId, out uint missingMeshDataId, out int missingLodLevel).ShouldBeTrue();
            missingMeshDataId.ShouldBe(0u);
            missingLodLevel.ShouldBe(-1);
        }
        finally
        {
            scene.Destroy();
        }
    }

    [Test]
    public void GPUScene_LodTableResidentLodsMayUseMeshletOrTraditionalIndirectRanges()
    {
        MeshletGenerationSettings settings = CreateEnabledDenseSettings();
        XRMesh lod0 = CreateManyTinyTriangleMesh("Phase4Lod0Meshlets", 240);
        XRMesh lod1 = CreateManyTinyTriangleMesh("Phase4Lod1TraditionalFallback", 24);
        MeshletPayload lod0Payload = lod0.GetOrCreateMeshletPayload(settings);

        GPUScene scene = new();
        try
        {
            scene.RegisterLogicalMeshLODs([(lod0, 96.0f), (lod1, 0.0f)], out uint logicalMeshId, out string? failureReason)
                .ShouldBeTrue(failureReason);

            scene.TryValidateResidentLodMeshletRanges(logicalMeshId, out uint missingMeshDataId, out int missingLodLevel).ShouldBeTrue();
            missingMeshDataId.ShouldBe(0u);
            missingLodLevel.ShouldBe(-1);

            scene.TryGetLodTableEntry(logicalMeshId, out GPUScene.LODTableEntry lodEntry).ShouldBeTrue();
            scene.TryGetMeshletRange(lodEntry.LOD0_MeshDataID, out GPUScene.GpuMeshletRange lod0Range).ShouldBeTrue();
            scene.TryGetMeshletRange(lodEntry.LOD1_MeshDataID, out GPUScene.GpuMeshletRange lod1Range).ShouldBeTrue();

            lod0Range.MeshletCount.ShouldBe((uint)lod0Payload.Meshlets.Length);
            lod0Range.HasMeshlets.ShouldBeTrue();
            lod1Range.MeshletCount.ShouldBe(0u);
            lod1Range.RequiresTraditionalIndirectFallback.ShouldBeTrue();
        }
        finally
        {
            scene.Destroy();
        }
    }

    [Test]
    public void GPUScene_ProductionMeshletDataIsSceneOwnedBySourceContract()
    {
        string gpuSceneSource =
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.CommandBuffers.cs").Replace("\r\n", "\n") +
            "\n" +
            ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.AddRemove.cs").Replace("\r\n", "\n");

        gpuSceneSource.ShouldContain("public XRDataBuffer MeshletRangeBuffer");
        gpuSceneSource.ShouldContain("public XRDataBuffer MeshletDescriptorBuffer");
        gpuSceneSource.ShouldContain("public XRDataBuffer MeshletVertexIndexBuffer");
        gpuSceneSource.ShouldContain("public XRDataBuffer MeshletTriangleIndexBuffer");
        gpuSceneSource.ShouldContain("private void EnsureMeshletRangeForMesh");
        gpuSceneSource.ShouldContain("EnsureMeshletRangeForMesh(meshID, mesh)");
        gpuSceneSource.ShouldContain("Debug/compatibility meshlet collection");
        gpuSceneSource.ShouldContain("RebuildDebugMeshletCollectionFromUpdatingCommands");
        gpuSceneSource.ShouldNotContain("RebuildMeshletsFromUpdatingCommands");
    }

    [Test]
    public void GPURenderLODSelect_WritesSelectedMeshIdAndLodForMeshletExpansion()
    {
        string lodShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Compute/Indirect/GPURenderLODSelect.comp").Replace("\r\n", "\n");

        lodShader.ShouldContain("const uint COMMAND_MESH_ID = 4u;");
        lodShader.ShouldContain("const uint COMMAND_LOD_LEVEL = 12u;");
        lodShader.ShouldContain("const uint COMMAND_DRAW_ID = 19u;");
        lodShader.ShouldContain("uint drawID = floatBitsToUint(culled[base + COMMAND_DRAW_ID]);");
        lodShader.ShouldContain("culled[base + COMMAND_MESH_ID] = uintBitsToFloat(selectedMeshID);");
        lodShader.ShouldContain("culled[base + COMMAND_LOD_LEVEL] = uintBitsToFloat(resolvedLevel);");
        lodShader.ShouldNotContain("culled[base + COMMAND_DRAW_ID] =");
    }

    [Test]
    public void GPURenderPass_MeshletExpansionInputsUseGpuVisibilityAndSceneBuffers()
    {
        string passSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.Core.cs").Replace("\r\n", "\n");
        string hybridSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs").Replace("\r\n", "\n");
        string resourcesSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURendering/Resources/GPUMeshletResources.cs").Replace("\r\n", "\n");

        passSource.ShouldContain("public bool TryGetMeshletExpansionInputs");
        passSource.ShouldContain("_culledSceneToRenderBuffer");
        passSource.ShouldContain("_culledCountBuffer");
        passSource.ShouldContain("_culledHotCommandBuffer");
        passSource.ShouldContain("scene.DrawMetadataBuffer");
        passSource.ShouldContain("scene.MeshDataBuffer");
        passSource.ShouldContain("scene.MeshletRangeBuffer");
        passSource.ShouldContain("scene.LodTransitionBuffer");
        hybridSource.ShouldContain("renderPasses.TryGetMeshletExpansionInputs(scene, out GpuMeshletExpansionInputs inputs)");
        hybridSource.ShouldNotContain("RenderCommandCollection.TestCpuSoftwareOcclusionForGpuSource");
        resourcesSource.ShouldContain("public readonly struct GpuMeshletExpansionInputs");
    }

    [Test]
    public void MeshletLodTransitionPolicy_UsesFlaggedTaskRecords()
    {
        GPUMeshletLayout.MeshletTaskRecordUIntCount.ShouldBe(4u);
        GPUMeshletLayout.MeshletTaskRecordStride.ShouldBe(16u);
        GPUMeshletLayout.MeshletTaskPreviousLodFlag.ShouldBe(0x80000000u);
        GPUMeshletLayout.MeshletTaskMeshletIndexMask.ShouldBe(0x7FFFFFFFu);
    }

    [Test]
    public void GPURenderExpandMeshlets_ExpandsVisibleCommandsWithOverflowGuards()
    {
        string shader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Compute/Indirect/GPURenderExpandMeshlets.comp").Replace("\r\n", "\n");

        shader.ShouldContain("layout(std430, binding = 0) readonly buffer VisibleCommandsBuffer");
        shader.ShouldContain("layout(std430, binding = 9) buffer VisibleMeshletTaskBuffer");
        shader.ShouldContain("layout(std430, binding = 10) buffer VisibleMeshletTaskCountBuffer");
        shader.ShouldContain("layout(std430, binding = 11) buffer MeshletDispatchIndirectBuffer");
        shader.ShouldContain("layout(std430, binding = 12) buffer MeshletExpansionOverflowFlagBuffer");
        shader.ShouldContain("layout(std430, binding = 14) buffer MeshletDispatchCountBuffer");
        shader.ShouldContain("uint observed = atomicCompSwap(MeshletTaskCount, current, current + 1u);");
        shader.ShouldContain("atomicMax(MeshletDispatchX, current + 1u);");
        shader.ShouldContain("MeshletDispatchCount = 1u;");
        shader.ShouldContain("if (range.MeshletCount == 0u)");
        shader.ShouldContain("atomicExchange(MeshletExpansionOverflowFlag, 1u);");
        shader.ShouldContain("meshletTasks[taskIndex].MeshletIndex = meshletIndex;");
        shader.ShouldContain("meshletTasks[taskIndex].DrawID = drawID;");
        shader.ShouldContain("meshletTasks[taskIndex].TransformID = transformID;");
        shader.ShouldContain("meshletTasks[taskIndex].MaterialID = materialID;");
    }

    [Test]
    public void GPURenderExpandMeshlets_PreservesDrawMetadataAndPreviousLodRecords()
    {
        string shader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Compute/Indirect/GPURenderExpandMeshlets.comp").Replace("\r\n", "\n");

        shader.ShouldContain("DrawMetadata meta = Draws[drawID];");
        shader.ShouldContain("transformID = meta.TransformID;");
        shader.ShouldContain("materialID = meta.MaterialID;");
        shader.ShouldContain("ExpandMeshletRange(meshID, drawID, transformID, materialID, false);");
        shader.ShouldContain("uint previousMeshID = lodTransitions[transitionBase + 0u];");
        shader.ShouldContain("bool transitionActive = (transitionFlags & LOD_TRANSITION_ACTIVE) != 0u && previousMeshID != 0u && transitionProgress < 1.0;");
        shader.ShouldContain("ExpandMeshletRange(previousMeshID, drawID, transformID, materialID, true);");
        shader.ShouldContain("meshletIndex |= MESHLET_TASK_PREVIOUS_LOD_FLAG;");
    }

    [Test]
    public void GPURenderPass_MeshletExpansionDispatchOwnsGpuOutputBuffers()
    {
        string passCore = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.Core.cs").Replace("\r\n", "\n");
        string passInit = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.ShadersAndInit.cs").Replace("\r\n", "\n");
        string passIndirect = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs").Replace("\r\n", "\n");
        string resetShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Compute/Indirect/GPURenderResetCounters.comp").Replace("\r\n", "\n");

        passCore.ShouldContain("public XRDataBuffer? VisibleMeshletTaskBuffer");
        passCore.ShouldContain("public XRDataBuffer? VisibleMeshletTaskCountBuffer");
        passCore.ShouldContain("public XRDataBuffer? MeshletDispatchIndirectBuffer");
        passCore.ShouldContain("public XRDataBuffer? MeshletDispatchCountBuffer");
        passCore.ShouldContain("public XRDataBuffer? MeshletExpansionOverflowFlagBuffer");
        passCore.ShouldContain("public bool MeshletExpansionPreparedThisFrame");
        passInit.ShouldContain("Compute/Indirect/GPURenderExpandMeshlets.comp");
        passInit.ShouldContain("EnsureMeshletExpansionBuffers(capacity)");
        passInit.ShouldContain("EBufferTarget.DrawIndirectBuffer");
        passIndirect.ShouldContain("SelectVisibleCommandLods(scene, camera);\n            ExpandVisibleMeshlets(scene);\n            ClassifyTransparencyDomains(scene);");
        passIndirect.ShouldContain("TryGetMeshletExpansionInputs(scene, out GpuMeshletExpansionInputs inputs)");
        passIndirect.ShouldContain("_expandMeshletsComputeShader.DispatchCompute(dispatchGroups, 1, 1, postExpandBarrier);");
        resetShader.ShouldContain("MeshletTaskCount = 0u;");
        resetShader.ShouldContain("MeshletDispatchX = 0u;");
        resetShader.ShouldContain("MeshletDispatchY = 1u;");
        resetShader.ShouldContain("MeshletDispatchCount = 0u;");
        resetShader.ShouldContain("MeshletExpansionOverflow = 0u;");
    }

    [Test]
    public void MeshTaskIndirectCountDispatch_UsesBackendCountPathAndShaderGate()
    {
        string rendererSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Generic/AbstractRenderer.cs").Replace("\r\n", "\n");
        string vulkanSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/Meshlets/VulkanRenderer.Meshlets.cs").Replace("\r\n", "\n");
        string vulkanExtensions = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanExtensions.cs").Replace("\r\n", "\n");
        string vulkanLogicalDevice = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Bootstrap/VulkanRenderer.LogicalDevice.cs").Replace("\r\n", "\n");
        string vulkanCommandBuffers = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs").Replace("\r\n", "\n");
        string openGlSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Features/Meshlets/OpenGLRenderer.Meshlets.cs").Replace("\r\n", "\n");

        rendererSource.ShouldContain("TryDrawMeshTasksIndirectCount(");
        rendererSource.ShouldContain("SupportsProductionMeshletShaders()");
        rendererSource.ShouldContain("SupportsIndirectCountMeshTaskDispatch() &&\n               SupportsProductionMeshletShaders()");
        rendererSource.ShouldContain("Mesh-task indirect commands must use");
        rendererSource.ShouldContain("EBufferTarget.DrawIndirectBuffer");

        vulkanExtensions.ShouldContain("private ExtMeshShader? _extMeshShader;");
        vulkanLogicalDevice.ShouldContain("\"VK_EXT_mesh_shader\"");
        vulkanLogicalDevice.ShouldContain("PhysicalDeviceMeshShaderFeaturesEXT");
        vulkanLogicalDevice.ShouldContain("Api!.TryGetDeviceExtension(instance, device, out _extMeshShader)");
        vulkanSource.ShouldContain("SupportsVulkanMeshTaskIndirectCount");
        vulkanCommandBuffers.ShouldContain("CmdDrawMeshTasksIndirectCount");

        openGlSource.ShouldContain("glMultiDrawMeshTasksIndirectCountEXT");
        openGlSource.ShouldContain("EMeshShaderDialect.OpenGLEXT");
        openGlSource.ShouldContain("SupportsIndirectCountDraw()");
    }

    [Test]
    public void MeshletTaskShader_ConsumesTaskRecordsAndSceneCullingInputs()
    {
        string shader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Meshlets/MeshletCulling.task").Replace("\r\n", "\n");

        shader.ShouldContain("struct GpuMeshletTaskRecord");
        shader.ShouldContain("layout(std430, binding = 9) readonly buffer VisibleMeshletTaskBuffer");
        shader.ShouldContain("layout(std430, binding = 10) readonly buffer VisibleMeshletTaskCountBuffer");
        shader.ShouldContain("layout(std430, binding = 12) readonly buffer DrawMetadataBuffer");
        shader.ShouldContain("layout(std430, binding = 19) readonly buffer TransformBuffer");
        shader.ShouldContain("record = meshletTasks[taskIndex];");
        shader.ShouldContain("meshletIndex = record.MeshletIndex & MESHLET_TASK_MESHLET_INDEX_MASK;");
        shader.ShouldContain("vec4 worldSphere = TransformSphere(meshlet, transformID);");
        shader.ShouldContain("bool FrustumCulled(vec4 sphere)");
        shader.ShouldContain("bool ConeBackfaceCulled(in GpuMeshletDescriptor meshlet, uint transformID)");
        shader.ShouldContain("layout(binding = 0) uniform sampler2D HiZDepth;");
        shader.ShouldContain("bool HiZOccluded(vec4 sphere)");
        shader.ShouldContain("MESHLET_PASS_DEPTH_PREPASS");
        shader.ShouldContain("MESHLET_PASS_SHADOW_DEPTH");
        shader.ShouldContain("MESHLET_PASS_OPAQUE");
        shader.ShouldContain("MESHLET_PASS_MASKED");
        shader.ShouldContain("MESHLET_PASS_TRANSPARENT");
        shader.ShouldContain("MESHLET_PASS_VELOCITY");
        shader.ShouldContain("MESHLET_PASS_STEREO");
        shader.ShouldContain("gl_TaskCountNV = visible ? 1u : 0u;");
        shader.ShouldNotContain("gl_WorkGroupID.x * GROUP_SIZE");
        shader.ShouldNotContain("CommandVisibilityBuffer");
    }

    [Test]
    public void MeshletMeshShaders_UseAtlasStreamsAndMaterialCompatibleInterpolants()
    {
        string staticShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Meshlets/MeshletRender.mesh").Replace("\r\n", "\n");
        string skinnedShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Meshlets/MeshletRenderSkinned.mesh").Replace("\r\n", "\n");

        staticShader.ShouldContain("layout(std430, binding = 3) readonly buffer MeshDataBuffer");
        staticShader.ShouldContain("layout(std430, binding = 5) readonly buffer MeshletDescriptorBuffer");
        staticShader.ShouldContain("layout(std430, binding = 6) readonly buffer MeshletVertexIndexBuffer");
        staticShader.ShouldContain("layout(std430, binding = 7) readonly buffer MeshletTriangleIndexBuffer");
        staticShader.ShouldContain("layout(std430, binding = 12) readonly buffer DrawMetadataBuffer");
        staticShader.ShouldContain("layout(std430, binding = 13) readonly buffer AtlasPositionBuffer");
        staticShader.ShouldContain("layout(std430, binding = 14) readonly buffer AtlasNormalBuffer");
        staticShader.ShouldContain("layout(std430, binding = 15) readonly buffer AtlasTangentBuffer");
        staticShader.ShouldContain("layout(std430, binding = 18) readonly buffer AtlasUV0Buffer");
        staticShader.ShouldContain("layout(std430, binding = 19) readonly buffer TransformBuffer");
        staticShader.ShouldContain("layout(std430, binding = 20) readonly buffer PrevTransformBuffer");
        staticShader.ShouldContain("layout(location = 0) out vec3 FragPos[];");
        staticShader.ShouldContain("layout(location = 1) out vec3 FragNorm[];");
        staticShader.ShouldContain("layout(location = 2) out vec3 FragTan[];");
        staticShader.ShouldContain("layout(location = 3) out vec3 FragBinorm[];");
        staticShader.ShouldContain("layout(location = 4) out vec2 FragUV0[];");
        staticShader.ShouldContain("layout(location = 12) out vec4 FragColor0[];");
        staticShader.ShouldContain("layout(location = 20) out vec3 FragPosLocal[];");
        staticShader.ShouldContain("layout(location = 21) out float FragTransformId[];");
        staticShader.ShouldContain("layout(location = 23) flat out uint XreFragLodTransitionRole[];");
        staticShader.ShouldContain("layout(location = 24) flat out uint XRE_FragMaterialId[];");
        staticShader.ShouldContain("layout(location = 25) out vec3 PrevFragPos[];");
        staticShader.ShouldContain("layout(location = 26) flat out uint XRE_FragStateClassId[];");
        staticShader.ShouldContain("XRE_FragStateClassId[tid] = draw.StateClassID;");
        staticShader.ShouldContain("uint atlasVertexIndex = meshData.FirstVertex + localVertexIndex;");
        staticShader.ShouldContain("gl_PrimitiveIndicesNV[tri * 3u + 0u] = ReadTriIndex(baseByte + 0u);");

        skinnedShader.ShouldContain("layout(std430, binding = 21) readonly buffer MeshletSkinningBuffer");
        skinnedShader.ShouldContain("layout(std430, binding = 22) readonly buffer SkinningPaletteBuffer");
        skinnedShader.ShouldContain("layout(std430, binding = 23) readonly buffer SkinningMetaBuffer");
        skinnedShader.ShouldContain("uint ResolveSkinBase(uint skinID)");
        skinnedShader.ShouldContain("void ApplySkinning(");
        skinnedShader.ShouldContain("ApplySkinning(atlasVertexIndex, ResolveSkinBase(draw.SkinID), localPosition, localNormal, skinnedTangent);");
    }

    [Test]
    public void DiagnosticMeshletShaders_UseInterfaceBlockForNvMeshVaryings()
    {
        string meshShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Meshlets/MeshletRenderDiagnostic.mesh").Replace("\r\n", "\n");
        string fragmentShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Meshlets/MeshletShading.fs").Replace("\r\n", "\n");

        meshShader.ShouldContain("layout(location = 0) out MeshletVertex");
        meshShader.ShouldContain("} OUT[];");
        meshShader.ShouldNotContain("layout(location = 0) out vec3 out_worldPos[];");
        meshShader.ShouldNotContain("layout(location = 1) out vec3 out_normal[];");
        meshShader.ShouldNotContain("layout(location = 2) out vec2 out_texCoord[];");
        meshShader.ShouldNotContain("layout(location = 3) out vec4 out_tangent[];");
        meshShader.ShouldContain("OUT[tid].worldPos = wpos.xyz;");
        meshShader.ShouldContain("OUT[tid].meshletIndex = meshletIndex;");

        fragmentShader.ShouldContain("layout(location = 0) in MeshletVertex");
        fragmentShader.ShouldContain("} IN;");
        fragmentShader.ShouldContain("Material material = materials[IN.materialID];");
        fragmentShader.ShouldContain("vec3 V = normalize(cameraPosition - IN.worldPos);");
        fragmentShader.ShouldNotContain("in_worldPos");
        fragmentShader.ShouldNotContain("in_meshletIndex");
    }

    [Test]
    public void MeshletExtVariants_ImplementGlExtMeshShaderContract()
    {
        string taskShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Meshlets/MeshletCullingExt.task").Replace("\r\n", "\n");
        string staticShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Meshlets/MeshletRenderExt.mesh").Replace("\r\n", "\n");
        string skinnedShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Meshlets/MeshletRenderSkinnedExt.mesh").Replace("\r\n", "\n");

        // Task shader: EXT extension, taskPayloadSharedEXT, EmitMeshTasksEXT in place of gl_TaskCountNV.
        taskShader.ShouldContain("#extension GL_EXT_mesh_shader : require");
        taskShader.ShouldNotContain("#extension GL_NV_mesh_shader");
        taskShader.ShouldContain("taskPayloadSharedEXT TaskPayload OUT;");
        taskShader.ShouldContain("EmitMeshTasksEXT(visible ? 1u : 0u, 1u, 1u);");
        taskShader.ShouldNotContain("gl_TaskCountNV");
        taskShader.ShouldNotContain("taskNV out");
        // Same scene/culling input bindings as the NV task shader.
        taskShader.ShouldContain("layout(std430, binding = 9) readonly buffer VisibleMeshletTaskBuffer");
        taskShader.ShouldContain("layout(std430, binding = 10) readonly buffer VisibleMeshletTaskCountBuffer");
        taskShader.ShouldContain("layout(std430, binding = 12) readonly buffer DrawMetadataBuffer");
        taskShader.ShouldContain("layout(std430, binding = 19) readonly buffer TransformBuffer");

        foreach (string shader in new[] { staticShader, skinnedShader })
        {
            shader.ShouldContain("#extension GL_EXT_mesh_shader : require");
            shader.ShouldNotContain("#extension GL_NV_mesh_shader");
            shader.ShouldContain("taskPayloadSharedEXT TaskPayload IN;");
            shader.ShouldNotContain("taskNV in");
            shader.ShouldContain("SetMeshOutputsEXT(meshlet.VertexCount, meshlet.TriangleCount);");
            shader.ShouldNotContain("gl_PrimitiveCountNV");
            shader.ShouldContain("gl_MeshVerticesEXT[tid].gl_Position = ViewProjectionMatrix * worldPosition;");
            shader.ShouldNotContain("gl_MeshVerticesNV");
            shader.ShouldContain("gl_PrimitiveTriangleIndicesEXT[tri] = uvec3(");
            shader.ShouldNotContain("gl_PrimitiveIndicesNV");
            shader.ShouldContain("layout(triangles, max_vertices = 64, max_primitives = 126) out;");
            // Atlas / scene bindings must match the NV variant so the runtime can share UBO/SSBO setup.
            shader.ShouldContain("layout(std430, binding = 13) readonly buffer AtlasPositionBuffer");
            shader.ShouldContain("layout(std430, binding = 14) readonly buffer AtlasNormalBuffer");
            shader.ShouldContain("layout(std430, binding = 15) readonly buffer AtlasTangentBuffer");
            shader.ShouldContain("layout(std430, binding = 18) readonly buffer AtlasUV0Buffer");
            shader.ShouldContain("layout(std430, binding = 19) readonly buffer TransformBuffer");
            shader.ShouldContain("layout(std430, binding = 20) readonly buffer PrevTransformBuffer");
            shader.ShouldContain("layout(location = 26) flat out uint XRE_FragStateClassId[];");
            shader.ShouldContain("XRE_FragStateClassId[tid] = draw.StateClassID;");
        }

        // Skinned-only bindings still appear in the EXT skinned variant.
        skinnedShader.ShouldContain("layout(std430, binding = 21) readonly buffer MeshletSkinningBuffer");
        skinnedShader.ShouldContain("layout(std430, binding = 22) readonly buffer SkinningPaletteBuffer");
        skinnedShader.ShouldContain("layout(std430, binding = 23) readonly buffer SkinningMetaBuffer");
        skinnedShader.ShouldContain("void ApplySkinning(");
    }

    [Test]
    public void GpuMeshletPhase8_RuntimeWiresMaterialTableDispatchAndExplicitFallbacks()
    {
        string hybridSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs").Replace("\r\n", "\n");
        string passCore = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.Core.cs").Replace("\r\n", "\n");
        string openGlSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Features/Meshlets/OpenGLRenderer.Meshlets.cs").Replace("\r\n", "\n");
        string vulkanSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Features/Meshlets/VulkanRenderer.Meshlets.cs").Replace("\r\n", "\n");

        hybridSource.ShouldContain("TryRenderMeshletMaterialTable(");
        hybridSource.ShouldContain("EnsureMeshletMaterialTableProgram(");
        hybridSource.ShouldContain("TryGetMeshletShaderPaths(");
        hybridSource.ShouldContain("EMeshShaderDialect.OpenGLNV");
        hybridSource.ShouldContain("\"Meshlets/MeshletCulling.task\"");
        hybridSource.ShouldContain("\"Meshlets/MeshletRender.mesh\"");
        hybridSource.ShouldContain("EMeshShaderDialect.OpenGLEXT");
        hybridSource.ShouldContain("EMeshShaderDialect.VulkanEXT");
        hybridSource.ShouldContain("\"Meshlets/MeshletCullingExt.task\"");
        hybridSource.ShouldContain("\"Meshlets/MeshletRenderExt.mesh\"");
        hybridSource.ShouldContain("VisibleMeshletTaskBuffer");
        hybridSource.ShouldContain("VisibleMeshletTaskCountBuffer");
        hybridSource.ShouldContain("MeshletDispatchIndirectBuffer");
        hybridSource.ShouldContain("MeshletDispatchCountBuffer");
        hybridSource.ShouldContain("TryDrawMeshTasksIndirectCount(");
        hybridSource.ShouldContain("scene.MaterialStateBuffer.BindTo(program, MeshletMaterialStateSsboBinding);");
        hybridSource.ShouldContain("materialTableBuffer.BindTo(program, MaterialTableSsboBinding);");
        hybridSource.ShouldContain("materialTextureHandleBuffer?.BindTo(program, MaterialTextureHandleTableSsboBinding);");
        hybridSource.ShouldContain("WarnMeshletMaterialFallback(");
        hybridSource.ShouldContain("skipping traditional mesh fallback");
        hybridSource.ShouldContain("renderPasses.TryGetHiZDepthPyramidForMeshlets(");
        hybridSource.ShouldContain("program.Uniform(\"HiZViewProjectionMatrix\"");
        hybridSource.ShouldContain("program.Uniform(\"HiZValid\", hiZAvailable ? 1u : 0u);");
        hybridSource.ShouldContain("program.Sampler(\"HiZDepth\", hiZDepthPyramid");
        hybridSource.ShouldContain("scene.SkinnedCommandCount != 0u");
        hybridSource.ShouldContain("Scene-owned skinned meshlet vertex-weight buffers are not wired yet");
        hybridSource.ShouldNotContain("MeshletCollection");
        hybridSource.ShouldNotContain("Rendering.Meshlets");

        passCore.ShouldContain("bool meshlet = strategy.IsAnyMeshletStrategy();");
        passCore.ShouldContain("_passEnableZeroReadbackMaterialScatter = zeroReadback || instrumented || meshlet;");

        openGlSource.ShouldContain("public override bool SupportsProductionMeshletShaders()");
        openGlSource.ShouldContain("=> MeshShaderDialect == EMeshShaderDialect.OpenGLEXT;");
        openGlSource.ShouldNotContain("production task/mesh shader sources are not wired yet");
        vulkanSource.ShouldContain("public override bool SupportsProductionMeshletShaders()");
        vulkanSource.ShouldContain("=> MeshShaderDialect == EMeshShaderDialect.VulkanEXT;");
        vulkanSource.ShouldNotContain("production task/mesh shader sources are not wired yet");
    }

    [Test]
    public void GpuMeshletPhase8_MaterialStateAndPassCoverageContractsAreExplicit()
    {
        string hybridSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs").Replace("\r\n", "\n");
        string staticShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Meshlets/MeshletRender.mesh").Replace("\r\n", "\n");
        string skinnedShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Meshlets/MeshletRenderSkinned.mesh").Replace("\r\n", "\n");
        string staticExtShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Meshlets/MeshletRenderExt.mesh").Replace("\r\n", "\n");
        string skinnedExtShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Meshlets/MeshletRenderSkinnedExt.mesh").Replace("\r\n", "\n");

        hybridSource.ShouldContain("struct MaterialStateGpu");
        hybridSource.ShouldContain("MaterialStateBuffer");
        hybridSource.ShouldContain("XRE_LoadMaterialState");
        hybridSource.ShouldContain("state.TransparencyMode == XRE_TRANSPARENCY_MASKED");
        hybridSource.ShouldContain("state.TransparencyMode == XRE_TRANSPARENCY_ALPHA_TO_COVERAGE");
        hybridSource.ShouldContain("SampleBindlessTexture(material.AlbedoHandleIndex");
        hybridSource.ShouldContain("IsMeshletMaterialTableDirectPassSupported");
        hybridSource.ShouldContain("GetMeshletAllowedStateClassMask");
        hybridSource.ShouldContain("EGpuMaterialStateClass.OpaqueDeferred");
        hybridSource.ShouldContain("EGpuMaterialStateClass.OpaqueForward");
        hybridSource.ShouldContain("EGpuMaterialStateClass.AlphaTested");
        hybridSource.ShouldContain("EGpuMaterialStateClass.Transparent");

        foreach (string shader in new[] { staticShader, skinnedShader, staticExtShader, skinnedExtShader })
        {
            shader.ShouldContain("layout(location = 24) flat out uint XRE_FragMaterialId[];");
            shader.ShouldContain("layout(location = 25) out vec3 PrevFragPos[];");
            shader.ShouldContain("layout(location = 26) flat out uint XRE_FragStateClassId[];");
            shader.ShouldContain("XRE_FragMaterialId[tid] = IN.Record.MaterialID != 0u ? IN.Record.MaterialID : draw.MaterialID;");
            shader.ShouldContain("XRE_FragStateClassId[tid] = draw.StateClassID;");
            shader.ShouldContain("PrevFragPos[tid] = previousWorldPosition.xyz;");
        }
    }

    [Test]
    public void GpuMeshletPhase9_DiagnosticsCountersAndStructuredEventsAreWired()
    {
        string hybridSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs").Replace("\r\n", "\n");
        string passCore = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.Core.cs").Replace("\r\n", "\n");
        string passIndirect = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs").Replace("\r\n", "\n");
        string gpuSceneSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.AddRemove.cs").Replace("\r\n", "\n");
        string statsSource = ReadWorkspaceFile("XRENGINE/Engine/Subclasses/Rendering/Engine.Rendering.Stats.GpuMeshlets.cs").Replace("\r\n", "\n");
        string taskShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Meshlets/MeshletCulling.task").Replace("\r\n", "\n");
        string extTaskShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Meshlets/MeshletCullingExt.task").Replace("\r\n", "\n");

        hybridSource.ShouldContain("RecordGpuMeshletStrategyRequested");
        hybridSource.ShouldContain("RecordGpuMeshletProductionFrame");
        hybridSource.ShouldContain("RecordGpuMeshletFallback");
        hybridSource.ShouldContain("Meshlet.BackendSelected");
        hybridSource.ShouldContain("Meshlet.BackendUnsupported");

        passIndirect.ShouldContain("RecordGpuMeshletDispatchSkipped");
        passIndirect.ShouldContain("RecordGpuMeshletExpansionOverflow");
        passIndirect.ShouldContain("RecordGpuMeshletTaskStats");
        passIndirect.ShouldContain("Meshlet.DispatchSkipped");
        passIndirect.ShouldContain("Meshlet.ExpandOverflow");
        passCore.ShouldContain("RecordGpuMeshletInstrumentation");

        gpuSceneSource.ShouldContain("RecordGpuMeshletCacheHit");
        gpuSceneSource.ShouldContain("RecordGpuMeshletCacheMiss");
        gpuSceneSource.ShouldContain("RecordGpuMeshletCacheStale");
        gpuSceneSource.ShouldContain("Meshlet.SceneBufferUpload");
        gpuSceneSource.ShouldContain("Meshlet.CacheMissing");
        gpuSceneSource.ShouldContain("Meshlet.CacheStale");
        gpuSceneSource.ShouldContain("EnsureRuntimeMeshletPayloadsForMeshletDispatch");
        gpuSceneSource.ShouldContain("Meshlet.RuntimePayloadBuilt");

        statsSource.ShouldContain("GpuMeshletRequestedFrames");
        statsSource.ShouldContain("GpuMeshletProductionFrames");
        statsSource.ShouldContain("GpuMeshletFallbackFrames");
        statsSource.ShouldContain("GpuMeshletTaskRecordsFrustumCulled");
        statsSource.ShouldContain("GpuMeshletBufferBytesResident");
        statsSource.ShouldContain("GpuMeshletCacheStale");

        foreach (string shader in new[] { taskShader, extTaskShader })
        {
            shader.ShouldContain("layout(std430, binding = 21) buffer MeshletStatsBuffer");
            shader.ShouldContain("uniform uint StatsEnabled;");
            shader.ShouldContain("uniform mat4 HiZViewProjectionMatrix;");
            shader.ShouldContain("HiZViewProjectionMatrix * vec4(sphere.xyz, 1.0)");
            shader.ShouldContain("atomicAdd(MeshletTaskRecordsEmitted, 1u);");
            shader.ShouldContain("atomicAdd(MeshletTaskRecordsFrustumCulled, 1u);");
            shader.ShouldContain("atomicAdd(MeshletTaskRecordsConeCulled, 1u);");
            shader.ShouldContain("atomicAdd(MeshletTaskRecordsHiZCulled, 1u);");
        }
    }

    [Test]
    public void DefaultPipelines_WireMeshletDebugDisplayCommand()
    {
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline.cs").Replace("\r\n", "\n");
        string pipeline2Source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Types/DefaultRenderPipeline2.CommandChain.cs").Replace("\r\n", "\n");

        pipelineSource.ShouldContain("c.Add<VPRC_RenderMeshletDebugDisplay>();");
        pipeline2Source.ShouldContain("c.Add<VPRC_RenderMeshletDebugDisplay>();");
    }

    [Test]
    public void GpuMeshletProductionPath_DoesNotUseReadbackHelpersOrCpuFallbackRenderer()
    {
        string hybridSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs").Replace("\r\n", "\n");
        string directBody = ExtractSourceBetween(
            hybridSource,
            "private bool TryRenderMeshletMaterialTable(",
            "private XRRenderProgram? EnsureMeshletMaterialTableProgram(");

        directBody.ShouldContain("TryDrawMeshTasksIndirectCount(");
        directBody.ShouldContain("RecordGpuMeshletProductionFrame");
        directBody.ShouldNotContain("GetDataRawAtIndex");
        directBody.ShouldNotContain("MapBufferData");
        directBody.ShouldNotContain("ReadUInt(");
        directBody.ShouldNotContain("ReadActiveMaterialTierBuckets");
        directBody.ShouldNotContain("BuildIndirectCommandsCpu");
        directBody.ShouldNotContain("RenderTraditional(");
    }

    [Test]
    public void GpuMeshletSelectedPath_DoesNotFallThroughToTraditionalIndirectRendering()
    {
        string hybridSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs").Replace("\r\n", "\n");
        string renderBody = ExtractSourceBetween(
            hybridSource,
            "public void Render(\n            GPURenderPassCollection renderPasses,",
            "private static void LogIndirectPath(");
        string meshletGuard = ExtractSourceBetween(
            renderBody,
            "bool meshletStrategy = renderPasses.MeshSubmissionStrategy.IsAnyMeshletStrategy();",
            "if (renderPasses.MeshSubmissionStrategy == EMeshSubmissionStrategy.GpuIndirectZeroReadback");

        meshletGuard.ShouldContain("TryRenderMeshletMaterialTable(");
        meshletGuard.ShouldContain("WarnMeshletMaterialFallback(");
        meshletGuard.ShouldContain("return;");
        meshletGuard.ShouldNotContain("RenderZeroReadback");
        meshletGuard.ShouldNotContain("RenderTraditional");

        string zeroReadbackScatterGuard = ExtractSourceBetween(
            renderBody,
            "if (renderPasses.MeshSubmissionStrategy == EMeshSubmissionStrategy.GpuIndirectZeroReadback",
            "// Material map from scene (ID -> XRMaterial)");
        zeroReadbackScatterGuard.ShouldNotContain("IsAnyMeshletStrategy");
    }

    [Test]
    public void MeshletDiagnosticDirectDispatch_UsesDedicatedLegacyShaders()
    {
        string collectionSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Meshlets/MeshletCollection.cs").Replace("\r\n", "\n");

        collectionSource.ShouldContain("MeshletCullingDiagnostic.task");
        collectionSource.ShouldContain("MeshletRenderDiagnostic.mesh");
        collectionSource.ShouldNotContain("Path.Combine(\"Meshlets\", \"MeshletCulling.task\")");
        collectionSource.ShouldNotContain("Path.Combine(\"Meshlets\", \"MeshletRender.mesh\")");
    }

    [Test]
    public void VulkanIndirectBuffers_CanAlsoBeWrittenByCompute()
    {
        string allocatorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Resources/VulkanResourceAllocator.cs").Replace("\r\n", "\n");
        string dataBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/BackendObjects/Buffers/VkDataBuffer.cs").Replace("\r\n", "\n");

        allocatorSource.ShouldContain("EBufferTarget.DrawIndirectBuffer => BufferUsageFlags.IndirectBufferBit | BufferUsageFlags.StorageBufferBit");
        allocatorSource.ShouldContain("EBufferTarget.DispatchIndirectBuffer => BufferUsageFlags.IndirectBufferBit | BufferUsageFlags.StorageBufferBit");
        dataBufferSource.ShouldContain("EBufferTarget.DrawIndirectBuffer => BufferUsageFlags.IndirectBufferBit | BufferUsageFlags.StorageBufferBit");
        dataBufferSource.ShouldContain("EBufferTarget.DispatchIndirectBuffer => BufferUsageFlags.IndirectBufferBit | BufferUsageFlags.StorageBufferBit");
    }

    private static string ExtractSwapCommandBuffersBody(string source)
    {
        const string startMarker = "public void SwapCommandBuffers()";
        const string endMarker = "private static XRDataBuffer MakeCommandsInputBuffer()";

        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);

        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start);

        return source[start..end];
    }

    private static string ExtractSourceBetween(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0);

        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        end.ShouldBeGreaterThan(start);

        return source[start..end];
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string fullPath = ResolveWorkspacePath(relativePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected file does not exist: {fullPath}");
        return File.ReadAllText(fullPath);
    }

    private static MeshletGenerationSettings CreateEnabledDenseSettings()
        => new()
        {
            Enabled = true,
            BuildMode = MeshletBuildMode.Dense,
            MaxVertices = 64u,
            MaxTriangles = 124u,
            OptimizeMeshlets = true,
            ComputeBounds = true,
        };

    private static XRMesh CreateManyTinyTriangleMesh(string meshName, int triangleCount)
    {
        List<Vector3> positions = new(triangleCount * 3);
        for (int i = 0; i < triangleCount; i++)
        {
            float x = i % 24;
            float y = i / 24;
            float z = i * 0.001f;
            positions.Add(new Vector3(x, y, z));
            positions.Add(new Vector3(x + 0.4f, y, z + 0.0001f));
            positions.Add(new Vector3(x, y + 0.4f, z + 0.0002f));
        }

        XRMesh mesh = XRMesh.CreateTriangles(positions);
        mesh.Name = meshName;
        return mesh;
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
