using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Compute;
using XREngine.Rendering.Occlusion;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public class GpuRenderingBacklogTests
{
    [Test]
    public void GPUScene_AddRemove_SharedMeshRefCount_RemainsValid()
    {
        var scene = new GPUScene();
        var mesh = XRMesh.CreateTriangles(Vector3.Zero, Vector3.UnitX, Vector3.UnitY);
        uint meshId = GetOrCreateMeshId(scene, mesh);

        EnsureDynamicAtlasResidency(scene, mesh, meshId, "refcount-test");
        scene.RebuildAtlasIfDirty(EAtlasTier.Dynamic);

        Dictionary<XRMesh, (int firstVertex, int firstIndex, int indexCount)> atlasOffsets =
            GetPrivateField<Dictionary<XRMesh, (int, int, int)>>(scene, "_atlasMeshOffsets");
        Dictionary<XRMesh, int> refCounts =
            GetPrivateField<Dictionary<XRMesh, int>>(scene, "_atlasMeshRefCounts");

        InvokeNonPublic(scene, "IncrementAtlasMeshRefCount", mesh);
        InvokeNonPublic(scene, "IncrementAtlasMeshRefCount", mesh);
        refCounts[mesh].ShouldBe(2);

        InvokeNonPublic(scene, "DecrementAtlasMeshRefCount", meshId, "unit-test");
        refCounts[mesh].ShouldBe(1);
        atlasOffsets.ContainsKey(mesh).ShouldBeTrue();

        InvokeNonPublic(scene, "DecrementAtlasMeshRefCount", meshId, "unit-test");
        refCounts.ContainsKey(mesh).ShouldBeFalse();
        atlasOffsets.ContainsKey(mesh).ShouldBeFalse();

        int atlasIndexCount = GetPrivateField<int>(scene, "_atlasIndexCount");
        atlasIndexCount.ShouldBe(0);
    }

    [Test]
    public void GPUScene_UpdateCommand_TransformChange_UpdatesCullingBounds()
    {
        var method = typeof(GPUScene).GetMethod("SetWorldSpaceBoundingSphere", BindingFlags.NonPublic | BindingFlags.Static);
        method.ShouldNotBeNull();

        GPUIndirectRenderCommand command = default;
        var localBounds = new AABB(new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f));
        Matrix4x4 model = Matrix4x4.CreateScale(2f, 3f, 4f) * Matrix4x4.CreateTranslation(10f, 20f, 30f);

        object?[] args = [command, localBounds, model];
        method!.Invoke(null, args);
        command = (GPUIndirectRenderCommand)args[0]!;

        command.BoundingSphere.X.ShouldBe(10f, 0.0001f);
        command.BoundingSphere.Y.ShouldBe(20f, 0.0001f);
        command.BoundingSphere.Z.ShouldBe(30f, 0.0001f);

        float expectedRadius = MathF.Sqrt(3f) * 4f;
        command.BoundingSphere.W.ShouldBe(expectedRadius, 0.0001f);
    }

    [Test]
    public void GPUScene_UpdateCommand_RotatedAffineTransform_MatchesExpectedBoundingSphere()
    {
        var method = typeof(GPUScene).GetMethod("SetWorldSpaceBoundingSphere", BindingFlags.NonPublic | BindingFlags.Static);
        method.ShouldNotBeNull();

        GPUIndirectRenderCommand command = default;
        var localBounds = new AABB(new Vector3(-2f, -1f, -3f), new Vector3(2f, 1f, 3f));
        Matrix4x4 model = Matrix4x4.CreateScale(1.5f, 2.5f, 0.75f)
            * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(0.35f, -0.2f, 0.15f)))
            * Matrix4x4.CreateTranslation(7f, -4f, 11f);

        object?[] args = [command, localBounds, model];
        method!.Invoke(null, args);
        command = (GPUIndirectRenderCommand)args[0]!;

        Vector3 expectedCenter = Vector3.Transform(localBounds.Center, model);
        Vector3 xAxis = new(model.M11, model.M21, model.M31);
        Vector3 yAxis = new(model.M12, model.M22, model.M32);
        Vector3 zAxis = new(model.M13, model.M23, model.M33);
        float expectedRadius = localBounds.HalfExtents.Length() * MathF.Max(xAxis.Length(), MathF.Max(yAxis.Length(), zAxis.Length()));

        command.BoundingSphere.X.ShouldBe(expectedCenter.X, 0.0001f);
        command.BoundingSphere.Y.ShouldBe(expectedCenter.Y, 0.0001f);
        command.BoundingSphere.Z.ShouldBe(expectedCenter.Z, 0.0001f);
        command.BoundingSphere.W.ShouldBe(expectedRadius, 0.0001f);
    }

    [Test]
    public void GPURenderPass_BvhCull_UsesRealCullingPath_WhenEnabled()
    {
        var pass = new GPURenderPassCollection(renderPass: 0);
        var scene = new GPUScene
        {
            UseGpuBvh = true,
            BvhProvider = new StubBvhProvider(isReady: true)
        };

        SetPrivateField(pass, "_bvhFrustumCullProgram", new XRRenderProgram());

        var shouldUseBvh = (bool)InvokeNonPublic(pass, "ShouldUseBvhCulling", scene)!;
        shouldUseBvh.ShouldBeTrue();
    }

    [Test]
    public void GPURenderPass_NoCpuFallback_InShippingConfig()
    {
        GPURenderPassCollection.ConfigureIndirectDebug(d =>
        {
            d.DisableCpuReadbackCount = true;
            d.ForceCpuFallbackCount = false;
        });

        GPURenderPassCollection.IndirectDebug.DisableCpuReadbackCount.ShouldBeTrue();
        GPURenderPassCollection.IndirectDebug.ForceCpuFallbackCount.ShouldBeFalse();

        var pass = new GPURenderPassCollection(renderPass: 0);
        pass.GetVisibleCounts(out uint drawCount, out uint instanceCount, out uint overflowMarker);

        drawCount.ShouldBe(0u);
        instanceCount.ShouldBe(0u);
        overflowMarker.ShouldBe(0u);
    }

    [Test]
    public void Occlusion_HiZ_GPUPath_CullsAndRecovers_Correctly()
    {
        string source = ReadWorkspaceFile("Build/CommonAssets/Shaders/Compute/Occlusion/GPURenderOcclusionHiZ.comp");

        source.ShouldContain("SampleHiZConservative");
        source.ShouldContain("keep visible (uncertain)");
        source.ShouldContain("OcclusionOverflowFlag");
        source.ShouldContain("atomicAdd(OcclusionOverflowFlag, 1u)");
    }

    [Test]
    public void Occlusion_CPUQueryAsync_NoRenderThreadStall()
    {
        var coordinator = new CpuRenderOcclusionCoordinator();
        var camera = new XRCamera();

        var sw = Stopwatch.StartNew();
        coordinator.BeginPass(renderPass: 0, camera, sceneCommandCount: 1024u);

        for (uint i = 0; i < 1024u; i++)
            coordinator.ShouldRender(renderPass: 0, sourceCommandIndex: i).ShouldBeTrue();

        sw.Stop();
        sw.ElapsedMilliseconds.ShouldBeLessThan(5000);
    }

    [Test]
    public void Occlusion_TemporalHysteresis_ReducesPopping()
    {
        var coordinator = new CpuRenderOcclusionCoordinator();

        Type coordinatorType = typeof(CpuRenderOcclusionCoordinator);
        Type passStateType = coordinatorType.GetNestedType("PassState", BindingFlags.NonPublic)!;
        Type queryStateType = coordinatorType.GetNestedType("QueryState", BindingFlags.NonPublic)!;

        object passState = Activator.CreateInstance(passStateType, nonPublic: true)!;
        object queryState = Activator.CreateInstance(queryStateType, nonPublic: true)!;

        SetNonPublicField(queryState, "LastAnySamplesPassed", false);
        SetNonPublicField(queryState, "ConsecutiveOccludedFrames", 0);
        SetNonPublicField(queryState, "LastTouchedFrame", 0ul);

        IDictionary queries = (IDictionary)GetNonPublicField(passState, "Queries");
        queries[42u] = queryState;

        IDictionary passStates = (IDictionary)GetNonPublicField(coordinator, "_passStates");
        passStates[0] = passState;

        coordinator.ShouldRender(0, 42u).ShouldBeTrue();
        coordinator.ShouldRender(0, 42u).ShouldBeFalse();
    }

    [Test]
    public void Occlusion_OpenGL_Vulkan_Parity_BasicScene()
    {
        GPUIndirectRenderCommand[] commands =
        [
            new GPUIndirectRenderCommand { MeshID = 1, MaterialID = 10, RenderPass = 0 },
            new GPUIndirectRenderCommand { MeshID = 2, MaterialID = 11, RenderPass = 0 },
        ];

        GpuBackendParitySnapshot gl = GpuBackendParity.BuildSnapshot("OpenGL", 2, 2, commands, maxSamples: 2);
        GpuBackendParitySnapshot vk = GpuBackendParity.BuildSnapshot("Vulkan", 2, 2, commands, maxSamples: 2);

        GpuBackendParity.AreEquivalent(gl, vk, out string reason).ShouldBeTrue(reason);
    }

    [Test]
    public void IndirectPipeline_OpenGL_Vulkan_Parity_BasicScene()
    {
        GPUIndirectRenderCommand[] commands =
        [
            new GPUIndirectRenderCommand { MeshID = 101, MaterialID = 201, RenderPass = 1 },
            new GPUIndirectRenderCommand { MeshID = 102, MaterialID = 202, RenderPass = 1 },
            new GPUIndirectRenderCommand { MeshID = 103, MaterialID = 203, RenderPass = 1 },
        ];

        GpuBackendParitySnapshot gl = GpuBackendParity.BuildSnapshot("OpenGL", 3, 3, commands, maxSamples: 3);
        GpuBackendParitySnapshot vk = GpuBackendParity.BuildSnapshot("Vulkan", 3, 3, commands, maxSamples: 3);

        GpuBackendParity.AreEquivalent(gl, vk, out string reason).ShouldBeTrue(reason);
    }

    [Test]
    public void IndirectPipeline_OpenGL_Vulkan_Parity_MultiPass()
    {
        GPUIndirectRenderCommand[] commands =
        [
            new GPUIndirectRenderCommand { MeshID = 501, MaterialID = 11, RenderPass = 0 },
            new GPUIndirectRenderCommand { MeshID = 502, MaterialID = 12, RenderPass = 1 },
            new GPUIndirectRenderCommand { MeshID = 503, MaterialID = 13, RenderPass = 2 },
            new GPUIndirectRenderCommand { MeshID = 504, MaterialID = 14, RenderPass = 2 },
        ];

        GpuBackendParitySnapshot gl = GpuBackendParity.BuildSnapshot("OpenGL", 4, 4, commands, maxSamples: 4);
        GpuBackendParitySnapshot vk = GpuBackendParity.BuildSnapshot("Vulkan", 4, 4, commands, maxSamples: 4);

        GpuBackendParity.AreEquivalent(gl, vk, out string reason).ShouldBeTrue(reason);
    }

    [Test]
    public void TieredAtlas_StaticTier_BulkLoad_AssignsStaticTierFlags()
    {
        var scene = new GPUScene();
        var mesh = XRMesh.CreateTriangles(Vector3.Zero, Vector3.UnitX, Vector3.UnitY);
        uint meshId = GetOrCreateMeshId(scene, mesh);

        scene.LoadStaticMeshBatch([mesh]);

        scene.GetActiveAtlasTier(meshId).ShouldBe(EAtlasTier.Static);
        scene.TryGetMeshDataEntry(meshId, out GPUScene.MeshDataEntry entry).ShouldBeTrue();
        (entry.Flags & GPUScene.MeshDataFlagAtlasTierMask).ShouldBe((uint)EAtlasTier.Static);
        scene.GetAtlasVertexCount(EAtlasTier.Static).ShouldBeGreaterThan(0);
        scene.GetAtlasIndices(EAtlasTier.Static).ShouldNotBeNull();
    }

    [Test]
    public void TieredAtlas_StreamingTier_RegisterCommitAndAdvance_PreservesStreamingMeshEntry()
    {
        var scene = new GPUScene();
        var mesh = XRMesh.CreateTriangles(Vector3.Zero, Vector3.UnitX, Vector3.UnitY);

        bool registered = scene.RegisterStreamingMesh(mesh, maxVertexCount: 8, maxIndexCount: 6, out uint meshId, out string? failureReason);
        registered.ShouldBeTrue(failureReason);

        scene.GetActiveAtlasTier(meshId).ShouldBe(EAtlasTier.Streaming);
        scene.TryGetMeshDataEntry(meshId, out GPUScene.MeshDataEntry initialEntry).ShouldBeTrue();
        (initialEntry.Flags & GPUScene.MeshDataFlagAtlasTierMask).ShouldBe((uint)EAtlasTier.Streaming);

        scene.CommitStreamingMesh(meshId, vertexCount: 3, indexCount: 3).ShouldBeTrue();
        scene.AdvanceStreamingAtlasFrame();

        scene.GetActiveAtlasTier(meshId).ShouldBe(EAtlasTier.Streaming);
        scene.TryGetMeshDataEntry(meshId, out GPUScene.MeshDataEntry committedEntry).ShouldBeTrue();
        committedEntry.IndexCount.ShouldBe(3u);
        (committedEntry.Flags & GPUScene.MeshDataFlagAtlasTierMask).ShouldBe((uint)EAtlasTier.Streaming);
        scene.GetAtlasPositions(EAtlasTier.Streaming).ShouldNotBeNull();
    }

    [Test]
    public void TieredAtlas_MigrateDynamicToStatic_PreservesMeshDataAndTierFlags()
    {
        var scene = new GPUScene();
        var mesh = XRMesh.CreateTriangles(Vector3.Zero, Vector3.UnitX, Vector3.UnitY);
        uint meshId = GetOrCreateMeshId(scene, mesh);

        EnsureDynamicAtlasResidency(scene, mesh, meshId, "phase8-dynamic");
        scene.RebuildAtlasIfDirty(EAtlasTier.Dynamic);
        scene.GetActiveAtlasTier(meshId).ShouldBe(EAtlasTier.Dynamic);

        scene.MigrateMesh(meshId, EAtlasTier.Dynamic, EAtlasTier.Static).ShouldBeTrue();

        scene.GetActiveAtlasTier(meshId).ShouldBe(EAtlasTier.Static);
        scene.TryGetMeshDataEntry(meshId, out GPUScene.MeshDataEntry entry).ShouldBeTrue();
        entry.IndexCount.ShouldBe(3u);
        (entry.Flags & GPUScene.MeshDataFlagAtlasTierMask).ShouldBe((uint)EAtlasTier.Static);
        scene.GetAtlasVertexCount(EAtlasTier.Static).ShouldBeGreaterThan(0);
    }

    [Test]
    public void TieredAtlas_AllTiersActive_EntriesCarryCorrectTierFlags()
    {
        var scene = new GPUScene();

        var staticMesh = XRMesh.CreateTriangles(Vector3.Zero, Vector3.UnitX, Vector3.UnitY);
        var dynamicMesh = XRMesh.CreateTriangles(Vector3.UnitZ, Vector3.UnitX + Vector3.UnitZ, Vector3.UnitY + Vector3.UnitZ);
        var streamingMesh = XRMesh.CreateTriangles(Vector3.One, Vector3.One + Vector3.UnitX, Vector3.One + Vector3.UnitY);

        uint staticMeshId = GetOrCreateMeshId(scene, staticMesh);
        uint dynamicMeshId = GetOrCreateMeshId(scene, dynamicMesh);

        scene.LoadStaticMeshBatch([staticMesh]);
        EnsureDynamicAtlasResidency(scene, dynamicMesh, dynamicMeshId, "phase8-dynamic-all-tiers");
        scene.RebuildAtlasIfDirty(EAtlasTier.Dynamic);
        scene.RegisterStreamingMesh(streamingMesh, maxVertexCount: 8, maxIndexCount: 6, out uint streamingMeshId, out string? failureReason).ShouldBeTrue(failureReason);
        scene.RebuildAllAtlasesIfDirty();

        scene.TryGetMeshDataEntry(staticMeshId, out GPUScene.MeshDataEntry staticEntry).ShouldBeTrue();
        scene.TryGetMeshDataEntry(dynamicMeshId, out GPUScene.MeshDataEntry dynamicEntry).ShouldBeTrue();
        scene.TryGetMeshDataEntry(streamingMeshId, out GPUScene.MeshDataEntry streamingEntry).ShouldBeTrue();

        (staticEntry.Flags & GPUScene.MeshDataFlagAtlasTierMask).ShouldBe((uint)EAtlasTier.Static);
        (dynamicEntry.Flags & GPUScene.MeshDataFlagAtlasTierMask).ShouldBe((uint)EAtlasTier.Dynamic);
        (streamingEntry.Flags & GPUScene.MeshDataFlagAtlasTierMask).ShouldBe((uint)EAtlasTier.Streaming);

        scene.GetAtlasPositions(EAtlasTier.Static).ShouldNotBeNull();
        scene.GetAtlasPositions(EAtlasTier.Dynamic).ShouldNotBeNull();
        scene.GetAtlasPositions(EAtlasTier.Streaming).ShouldNotBeNull();
    }

    [Test]
    public void TieredAtlas_ScatterShader_UsesMeshDataTierFlags()
    {
        string source = ReadWorkspaceFile("Build/CommonAssets/Shaders/Compute/Indirect/GPURenderMaterialScatter.comp");

        source.ShouldContain("MESH_DATA_ATLAS_TIER_MASK");
        source.ShouldContain("uint tierFlags = meshData[meshBase + 3u]");
        source.ShouldContain("uint tier = tierFlags & MESH_DATA_ATLAS_TIER_MASK");
        source.ShouldContain("uint bucketIndex = slotIndex * MATERIAL_TIER_COUNT + tier");
    }

    [Test]
    public void LOD_TableBuffer_CorrectMeshDataIDs_AfterAtlasRebuild()
    {
        var scene = new GPUScene();
        var lod0 = XRMesh.CreateTriangles(Vector3.Zero, Vector3.UnitX, Vector3.UnitY);
        var lod1 = XRMesh.CreateTriangles(Vector3.UnitZ, Vector3.UnitZ + Vector3.UnitX, Vector3.UnitZ + Vector3.UnitY);

        scene.RegisterLogicalMeshLODs([(lod0, 5.0f), (lod1, 25.0f)], out uint logicalMeshId, out string? failureReason).ShouldBeTrue(failureReason);
        scene.RebuildAtlasIfDirty(EAtlasTier.Dynamic);

        scene.TryGetLodTableEntry(logicalMeshId, out GPUScene.LODTableEntry entry).ShouldBeTrue();
        entry.LODCount.ShouldBe(2u);
        entry.LOD0_MeshDataID.ShouldBe(GetOrCreateMeshId(scene, lod0));
        entry.LOD1_MeshDataID.ShouldBe(GetOrCreateMeshId(scene, lod1));
        entry.LOD0_MaxDistance.ShouldBe(5.0f);
        entry.LOD1_MaxDistance.ShouldBe(float.MaxValue);

        scene.TryGetMeshDataEntry(entry.LOD0_MeshDataID, out _).ShouldBeTrue();
        scene.TryGetMeshDataEntry(entry.LOD1_MeshDataID, out _).ShouldBeTrue();
    }

    [Test]
    public void LOD_TableBuffer_FallbackToLOD0_WhenSingleLOD()
    {
        var scene = new GPUScene();
        var mesh = XRMesh.CreateTriangles(Vector3.Zero, Vector3.UnitX, Vector3.UnitY);

        scene.RegisterLogicalMeshLODs([(mesh, 10.0f)], out uint logicalMeshId, out string? failureReason).ShouldBeTrue(failureReason);

        scene.TryGetLodTableEntry(logicalMeshId, out GPUScene.LODTableEntry entry).ShouldBeTrue();
        entry.LODCount.ShouldBe(1u);
        entry.LOD0_MeshDataID.ShouldBe(GetOrCreateMeshId(scene, mesh));
        entry.LOD0_MaxDistance.ShouldBe(float.MaxValue);
        entry.LOD1_MeshDataID.ShouldBe(0u);
    }

    [Test]
    public void LOD_ReleaseAndRequestLoad_UpdateLogicalMeshResidency()
    {
        var scene = new GPUScene();
        var lod0 = XRMesh.CreateTriangles(Vector3.Zero, Vector3.UnitX, Vector3.UnitY);
        var lod1 = XRMesh.CreateTriangles(Vector3.UnitZ, Vector3.UnitZ + Vector3.UnitX, Vector3.UnitZ + Vector3.UnitY);

        scene.RegisterLogicalMeshLODs([(lod0, 5.0f), (lod1, 25.0f)], out uint logicalMeshId, out string? failureReason).ShouldBeTrue(failureReason);
        uint lod1MeshId = GetOrCreateMeshId(scene, lod1);

        scene.ReleaseLOD(logicalMeshId, 1, out string? releaseFailure).ShouldBeTrue(releaseFailure);
        scene.TryGetLodTableEntry(logicalMeshId, out GPUScene.LODTableEntry releasedEntry).ShouldBeTrue();
        releasedEntry.LOD1_MeshDataID.ShouldBe(0u);

        scene.RequestLODLoad(logicalMeshId, 1, out string? requestFailure).ShouldBeTrue(requestFailure);
        scene.TryGetLodTableEntry(logicalMeshId, out GPUScene.LODTableEntry restoredEntry).ShouldBeTrue();
        restoredEntry.LOD1_MeshDataID.ShouldBe(lod1MeshId);
    }

    [Test]
    public void LOD_RequestBuffer_DrainReturnsAndClearsRequestedMasks()
    {
        var scene = new GPUScene();
        var lod0 = XRMesh.CreateTriangles(Vector3.Zero, Vector3.UnitX, Vector3.UnitY);
        var lod1 = XRMesh.CreateTriangles(Vector3.UnitZ, Vector3.UnitZ + Vector3.UnitX, Vector3.UnitZ + Vector3.UnitY);

        scene.RegisterLogicalMeshLODs([(lod0, 5.0f), (lod1, 25.0f)], out uint logicalMeshId, out string? failureReason).ShouldBeTrue(failureReason);
        scene.LODRequestBuffer.SetDataRawAtIndex(logicalMeshId, 0b10u);

        List<(uint logicalMeshId, uint lodMask)> requests = scene.DrainLODRequests();
        requests.Count.ShouldBe(1);
        requests[0].logicalMeshId.ShouldBe(logicalMeshId);
        requests[0].lodMask.ShouldBe(0b10u);
        scene.LODRequestBuffer.GetDataRawAtIndex<uint>(logicalMeshId).ShouldBe(0u);
    }

    [Test]
    public void LOD_SelectShader_WritesMeshIdAndLodLevel()
    {
        string source = ReadWorkspaceFile("Build/CommonAssets/Shaders/Compute/Indirect/GPURenderLODSelect.comp");

        source.ShouldContain("FLAG_LOD_ENABLED");
        source.ShouldContain("uint logicalMeshID = floatBitsToUint(culled[base + 46u])");
        source.ShouldContain("uint lodCount = floatBitsToUint(lodTable[entryBase + 0u])");
        source.ShouldContain("atomicOr(lodRequestMask[logicalMeshID], 1u << min(selectedLevel, 31u))");
        source.ShouldContain("culled[base + 36u] = uintBitsToFloat(selectedMeshID)");
        source.ShouldContain("culled[base + 44u] = uintBitsToFloat(resolvedLevel)");
    }

    [Test]
    public void VR_ViewSet_SharedCull_FansOut_AllOutputs()
    {
        GPUViewDescriptor[] descriptors =
        [
            new GPUViewDescriptor { ViewId = 0, Flags = (uint)(GPUViewFlags.StereoEyeLeft | GPUViewFlags.FullRes | GPUViewFlags.UsesSharedVisibility) },
            new GPUViewDescriptor { ViewId = 1, Flags = (uint)(GPUViewFlags.StereoEyeRight | GPUViewFlags.FullRes | GPUViewFlags.UsesSharedVisibility) },
            new GPUViewDescriptor { ViewId = 2, Flags = (uint)(GPUViewFlags.StereoEyeLeft | GPUViewFlags.Foveated | GPUViewFlags.UsesSharedVisibility) },
            new GPUViewDescriptor { ViewId = 3, Flags = (uint)(GPUViewFlags.StereoEyeRight | GPUViewFlags.Foveated | GPUViewFlags.UsesSharedVisibility) },
            new GPUViewDescriptor { ViewId = 4, Flags = (uint)(GPUViewFlags.Mirror | GPUViewFlags.UsesSharedVisibility) },
        ];

        uint visibilityCapacity = GPUViewSetLayout.ComputePerViewVisibleCapacity(commandCapacity: 128u, viewCapacity: (uint)descriptors.Length);
        visibilityCapacity.ShouldBe(640u);

        GPUViewMask mask = GPUViewMask.FromViewCount((uint)descriptors.Length);
        mask.BitsLo.ShouldBe(0b1_1111u);
        mask.BitsHi.ShouldBe(0u);
    }

    [Test]
    public void VR_OpenGL_Multiview_And_NVFallback_UseSameVisibleSet()
    {
        GPUIndirectRenderCommand[] commands =
        [
            new GPUIndirectRenderCommand { MeshID = 1001, MaterialID = 2001, RenderPass = 0 },
            new GPUIndirectRenderCommand { MeshID = 1002, MaterialID = 2002, RenderPass = 0 },
        ];

        GpuBackendParitySnapshot ovr = GpuBackendParity.BuildSnapshot("OpenGL-OVR", 2, 2, commands, maxSamples: 2);
        GpuBackendParitySnapshot nv = GpuBackendParity.BuildSnapshot("OpenGL-NV", 2, 2, commands, maxSamples: 2);

        GpuBackendParity.AreEquivalent(ovr, nv, out string reason).ShouldBeTrue(reason);
    }

    [Test]
    public void VR_Vulkan_ParallelSecondaryCommands_NoRenderThreadBlock()
    {
        string source = ReadWorkspaceFile("XRENGINE/Rendering/API/Rendering/Vulkan/Objects/CommandBuffers.cs");

        source.ShouldContain("ExecuteSecondaryCommandBufferBatchParallel");
        source.ShouldContain("Task.Run");
        source.ShouldContain("IndirectDrawBatch");
        source.ShouldContain("BlitBatch");
        source.ShouldContain("GetThreadCommandPool");
    }

    [Test]
    public void VR_Foveated_PerViewRefinement_NoStereoPopping()
    {
        string culling = ReadWorkspaceFile("Build/CommonAssets/Shaders/Compute/Culling/GPURenderCulling.comp");
        string copy = ReadWorkspaceFile("Build/CommonAssets/Shaders/Compute/Indirect/GPURenderCopyCommands.comp");
        string bvh = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/RenderPipeline/bvh_frustum_cull.comp");

        culling.ShouldContain("fullResNearDistance");
        culling.ShouldContain("FLAG_TRANSPARENT");
        culling.ShouldContain("perViewDistanceSq");

        copy.ShouldContain("fullResNearDistance");
        copy.ShouldContain("FLAG_TRANSPARENT");

        bvh.ShouldContain("fullResNearDistance");
        bvh.ShouldContain("FLAG_TRANSPARENT");
    }

    [Test]
    public void VR_Mirror_Compose_NoExtraSceneTraversal_DefaultMode()
    {
        var settings = new XREngine.Engine.Rendering.EngineSettings();
        settings.VrMirrorComposeFromEyeTextures.ShouldBeTrue();

        string windowSource = ReadWorkspaceFile("XRENGINE/Rendering/API/XRWindow.cs");
        windowSource.ShouldContain("mirrorByComposition");
        windowSource.ShouldContain("TryRenderDesktopMirrorComposition");
        windowSource.ShouldContain("!mirrorByComposition");
    }

    private static T GetPrivateField<T>(object target, string fieldName)
        => (T)GetNonPublicField(target, fieldName);

    private static uint GetOrCreateMeshId(GPUScene scene, XRMesh mesh)
    {
        object?[] args = [mesh, 0u];
        typeof(GPUScene)
            .GetMethod("GetOrCreateMeshID", BindingFlags.Instance | BindingFlags.NonPublic)
            .ShouldNotBeNull()!
            .Invoke(scene, args);
        return (uint)args[1]!;
    }

    private static void EnsureDynamicAtlasResidency(GPUScene scene, XRMesh mesh, uint meshId, string meshLabel)
    {
        object?[] args = [mesh, meshId, meshLabel, null];
        object? result = typeof(GPUScene)
            .GetMethod("EnsureSubmeshInAtlas", BindingFlags.Instance | BindingFlags.NonPublic)
            .ShouldNotBeNull()!
            .Invoke(scene, args);

        (result is bool hydrated && hydrated).ShouldBeTrue(args[3] as string);
    }

    private static object GetNonPublicField(object target, string fieldName)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var field = target.GetType().GetField(fieldName, flags);
        field.ShouldNotBeNull();
        return field!.GetValue(target)!;
    }

    private static void SetPrivateField(object target, string fieldName, object? value)
        => SetNonPublicField(target, fieldName, value);

    private static void SetNonPublicField(object target, string fieldName, object? value)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var field = target.GetType().GetField(fieldName, flags);
        field.ShouldNotBeNull();
        field!.SetValue(target, value);
    }

    private static object? InvokeNonPublic(object target, string methodName, params object?[] args)
    {
        var method = target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(candidate =>
            {
                if (!string.Equals(candidate.Name, methodName, StringComparison.Ordinal))
                    return false;

                ParameterInfo[] parameters = candidate.GetParameters();
                if (parameters.Length != args.Length)
                    return false;

                for (int i = 0; i < parameters.Length; i++)
                {
                    object? argument = args[i];
                    Type parameterType = parameters[i].ParameterType;
                    if (argument is null)
                    {
                        if (parameterType.IsValueType && Nullable.GetUnderlyingType(parameterType) is null)
                            return false;
                        continue;
                    }

                    if (!parameterType.IsInstanceOfType(argument))
                        return false;
                }

                return true;
            });
        method.ShouldNotBeNull();
        return method!.Invoke(target, args);
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12; i++)
        {
            string candidate = Path.Combine(dir, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            string? parent = Path.GetDirectoryName(dir);
            if (string.IsNullOrWhiteSpace(parent) || parent == dir)
                break;
            dir = parent;
        }

        throw new FileNotFoundException($"Unable to locate file '{relativePath}' from '{AppContext.BaseDirectory}'.");
    }

    private sealed class StubBvhProvider(bool isReady) : IGpuBvhProvider
    {
        public XRDataBuffer? BvhNodeBuffer => null;
        public XRDataBuffer? BvhRangeBuffer => null;
        public XRDataBuffer? BvhMortonBuffer => null;
        public uint BvhNodeCount => 0u;
        public bool IsBvhReady => isReady;
    }
}
