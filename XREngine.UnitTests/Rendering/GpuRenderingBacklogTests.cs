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
using XREngine.Rendering.Info;
using XREngine.Rendering.Occlusion;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public class GpuRenderingBacklogTests
{
    private IRuntimeShaderServices? _previousShaderServices;

    [SetUp]
    public void SetUp()
    {
        _previousShaderServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new GltfImportTestUtilities.TestRuntimeShaderServices();
    }

    [TearDown]
    public void TearDown()
        => RuntimeShaderServices.Current = _previousShaderServices;

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
        Vector3 xAxis = new(model.M11, model.M12, model.M13);
        Vector3 yAxis = new(model.M21, model.M22, model.M23);
        Vector3 zAxis = new(model.M31, model.M32, model.M33);
        float expectedRadius = localBounds.HalfExtents.Length() * MathF.Max(xAxis.Length(), MathF.Max(yAxis.Length(), zAxis.Length()));

        command.BoundingSphere.X.ShouldBe(expectedCenter.X, 0.0001f);
        command.BoundingSphere.Y.ShouldBe(expectedCenter.Y, 0.0001f);
        command.BoundingSphere.Z.ShouldBe(expectedCenter.Z, 0.0001f);
        command.BoundingSphere.W.ShouldBe(expectedRadius, 0.0001f);
    }

    [Test]
    public void GPUScene_CullingBounds_UseRenderInfoBasisWhenAvailable()
    {
        var method = typeof(GPUScene).GetMethod("ComputeRenderCullingBoundsGpu", BindingFlags.NonPublic | BindingFlags.Static);
        method.ShouldNotBeNull();

        var owner = new TestRenderable();
        RenderInfo3D renderInfo = RenderInfo3D.New(owner, new RenderCommandMesh3D(0));
        renderInfo.LocalCullingVolume = new AABB(new Vector3(1f, -2f, -3f), new Vector3(3f, 2f, 3f));
        renderInfo.CullingOffsetMatrix = Matrix4x4.CreateScale(2f, 3f, 4f) * Matrix4x4.CreateTranslation(10f, 20f, 30f);

        var fallbackBounds = new AABB(new Vector3(-100f, -100f, -100f), new Vector3(100f, 100f, 100f));
        Matrix4x4 fallbackMatrix = Matrix4x4.CreateTranslation(-500f, -500f, -500f);
        object?[] args = [renderInfo, fallbackBounds, fallbackMatrix, 7u];

        BoundsGpu bounds = (BoundsGpu)method!.Invoke(null, args)!;

        bounds.BoundingSphere.X.ShouldBe(14f, 0.0001f);
        bounds.BoundingSphere.Y.ShouldBe(20f, 0.0001f);
        bounds.BoundingSphere.Z.ShouldBe(30f, 0.0001f);
        bounds.BoundingSphere.W.ShouldBe(MathF.Sqrt(1f + 4f + 9f) * 4f, 0.0001f);
        bounds.BoundsVersion.ShouldBe(7u);

        bounds.AabbMin.X.ShouldBe(12f, 0.0001f);
        bounds.AabbMin.Y.ShouldBe(14f, 0.0001f);
        bounds.AabbMin.Z.ShouldBe(18f, 0.0001f);
        bounds.AabbMax.X.ShouldBe(16f, 0.0001f);
        bounds.AabbMax.Y.ShouldBe(26f, 0.0001f);
        bounds.AabbMax.Z.ShouldBe(42f, 0.0001f);
    }

    [Test]
    public void RenderCommandMesh3D_SwapBuffers_PublishesCullingOverrideBeforeCallbacks()
    {
        RenderCommandMesh3D command = new(0);
        AABB expectedBounds = new(new Vector3(1f, 2f, 3f), new Vector3(4f, 5f, 6f));
        AABB? observedBounds = null;

        command.WorldCullingVolumeOverride = expectedBounds;
        command.OnSwapBuffers += swappedCommand => observedBounds = swappedCommand.CullingVolume;

        command.SwapBuffers();

        AABB bounds = observedBounds.ShouldNotBeNull();
        bounds.Min.X.ShouldBe(expectedBounds.Min.X, 0.0001f);
        bounds.Min.Y.ShouldBe(expectedBounds.Min.Y, 0.0001f);
        bounds.Min.Z.ShouldBe(expectedBounds.Min.Z, 0.0001f);
        bounds.Max.X.ShouldBe(expectedBounds.Max.X, 0.0001f);
        bounds.Max.Y.ShouldBe(expectedBounds.Max.Y, 0.0001f);
        bounds.Max.Z.ShouldBe(expectedBounds.Max.Z, 0.0001f);
    }

    [Test]
    public void RenderCommandMesh3D_TryGetWorldCullingVolumeOverride_PrefersDirtyCollectSideBounds()
    {
        RenderCommandMesh3D command = new(0)
        {
            WorldCullingVolumeOverride = new AABB(new Vector3(-10f), new Vector3(-9f)),
        };
        command.SwapBuffers();

        AABB expectedBounds = new(new Vector3(1f, 2f, 3f), new Vector3(4f, 5f, 6f));
        command.WorldCullingVolumeOverride = expectedBounds;

        command.TryGetWorldCullingVolumeOverride(out AABB bounds).ShouldBeTrue();
        bounds.Min.X.ShouldBe(expectedBounds.Min.X, 0.0001f);
        bounds.Min.Y.ShouldBe(expectedBounds.Min.Y, 0.0001f);
        bounds.Min.Z.ShouldBe(expectedBounds.Min.Z, 0.0001f);
        bounds.Max.X.ShouldBe(expectedBounds.Max.X, 0.0001f);
        bounds.Max.Y.ShouldBe(expectedBounds.Max.Y, 0.0001f);
        bounds.Max.Z.ShouldBe(expectedBounds.Max.Z, 0.0001f);
    }

    [Test]
    public void RenderCommandMesh3D_CullingVolume_ReturnsNullForForcedUnboundedSkinnedCommand()
    {
        bool previous = RenderDiagnosticsFlags.ForceSkinnedUnbounded;
        RenderDiagnosticsFlags.SetForceSkinnedUnbounded(true);
        try
        {
            Transform bone = new();
            Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)> weights = new()
            {
                [bone] = (1.0f, Matrix4x4.Identity),
            };
            XRMesh mesh = new(
                [
                    new Vertex(new Vector3(-0.05f, -0.05f, 0.0f), weights),
                    new Vertex(new Vector3(0.05f, -0.05f, 0.0f), weights),
                    new Vertex(new Vector3(0.0f, 0.05f, 0.0f), weights),
                ],
                new List<ushort> { 0, 1, 2 });
            mesh.RebuildSkinningBuffersFromVertices();

            RenderCommandMesh3D command = new(0)
            {
                Mesh = new XRMeshRenderer { Mesh = mesh },
                WorldCullingVolumeOverride = new AABB(new Vector3(-1.0f), new Vector3(1.0f)),
            };
            command.SwapBuffers();

            command.CullingVolume.ShouldBeNull();
        }
        finally
        {
            RenderDiagnosticsFlags.SetForceSkinnedUnbounded(previous);
        }
    }

    [Test]
    public void GpuFrustumShaders_UseAabbBoundsForVisibilityRejection()
    {
        string classicCull = ReadWorkspaceFile("Build/CommonAssets/Shaders/Compute/Culling/GPURenderCulling.comp");
        string soaCull = ReadWorkspaceFile("Build/CommonAssets/Shaders/Compute/Culling/GPURenderCullingSoA.comp");
        string bvhCull = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/RenderPipeline/bvh_frustum_cull.comp");

        classicCull.ShouldContain("FrustumAabbVisible(bounds.AabbMin.xyz, bounds.AabbMax.xyz)");
        soaCull.ShouldContain("FrustumAabbVisible(bounds.AabbMin.xyz, bounds.AabbMax.xyz)");
        bvhCull.ShouldContain("AabbVisible(bounds.AabbMin.xyz, bounds.AabbMax.xyz)");
    }

    [Test]
    public void FrustumCullToBuffer_UsesAabbIntersectionFallback()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_FrustumCullToBuffer.cs");

        source.ShouldContain("ClassifyWorldBounds(frustum, worldBounds)");
        source.ShouldContain("frustum.Intersects(worldBounds.GetAABB(transformed: true))");
        source.ShouldContain("RenderDiagnosticsFlags.ForceSkinnedUnbounded");
        source.ShouldContain("UsesDeformedMesh(meshCommand)");
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
            coordinator.ShouldRender(0, camera, i, out _).ShouldBe(ECpuOcclusionDecision.Visible);

        sw.Stop();
        sw.ElapsedMilliseconds.ShouldBeLessThan(5000);
    }

    [Test]
    public void Occlusion_TemporalHysteresis_ReducesPopping()
    {
        var coordinator = new CpuRenderOcclusionCoordinator();
        var camera = new XRCamera();

        Type coordinatorType = typeof(CpuRenderOcclusionCoordinator);
        Type queryStateType = coordinatorType.GetNestedType("QueryState", BindingFlags.NonPublic)!;

        coordinator.BeginPass(0, camera, sceneCommandCount: 1u);
        object passState = InvokeNonPublic(coordinator, "GetPassState", 0, camera).ShouldNotBeNull();
        object queryState = Activator.CreateInstance(queryStateType, nonPublic: true)!;

        SetNonPublicField(queryState, "LastAnySamplesPassed", false);
        SetNonPublicField(queryState, "ConsecutiveOccludedFrames", 0);
        SetNonPublicField(queryState, "LastTouchedFrame", 0ul);

        IDictionary queries = (IDictionary)GetNonPublicField(passState, "Queries");
        queries[43u] = queryState;

        SetNonPublicField(queryState, "LastDecidedFrameId", ulong.MaxValue);
        coordinator.ShouldRender(0, camera, 43u, out _).ShouldBe(ECpuOcclusionDecision.Visible);
        SetNonPublicField(queryState, "LastDecidedFrameId", ulong.MaxValue);
        coordinator.ShouldRender(0, camera, 43u, out _).ShouldBe(ECpuOcclusionDecision.Skip);
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

        scene.RegisterLogicalMeshLODs([(lod0, 96.0f), (lod1, 0.0f)], out uint logicalMeshId, out string? failureReason).ShouldBeTrue(failureReason);
        scene.RebuildAtlasIfDirty(EAtlasTier.Dynamic);

        scene.TryGetLodTableEntry(logicalMeshId, out GPUScene.LODTableEntry entry).ShouldBeTrue();
        entry.LODCount.ShouldBe(2u);
        entry.LOD0_MeshDataID.ShouldBe(GetOrCreateMeshId(scene, lod0));
        entry.LOD1_MeshDataID.ShouldBe(GetOrCreateMeshId(scene, lod1));
        entry.LOD0_MinProjectedRadiusPixels.ShouldBe(96.0f);
        entry.LOD1_MinProjectedRadiusPixels.ShouldBe(0.0f);

        scene.TryGetMeshDataEntry(entry.LOD0_MeshDataID, out _).ShouldBeTrue();
        scene.TryGetMeshDataEntry(entry.LOD1_MeshDataID, out _).ShouldBeTrue();
    }

    [Test]
    public void LOD_TableBuffer_FallbackToLOD0_WhenSingleLOD()
    {
        var scene = new GPUScene();
        var mesh = XRMesh.CreateTriangles(Vector3.Zero, Vector3.UnitX, Vector3.UnitY);

        scene.RegisterLogicalMeshLODs([(mesh, 64.0f)], out uint logicalMeshId, out string? failureReason).ShouldBeTrue(failureReason);

        scene.TryGetLodTableEntry(logicalMeshId, out GPUScene.LODTableEntry entry).ShouldBeTrue();
        entry.LODCount.ShouldBe(1u);
        entry.LOD0_MeshDataID.ShouldBe(GetOrCreateMeshId(scene, mesh));
        entry.LOD0_MinProjectedRadiusPixels.ShouldBe(0.0f);
        entry.LOD1_MeshDataID.ShouldBe(0u);
    }

    [Test]
    public void LOD_ReleaseAndRequestLoad_UpdateLogicalMeshResidency()
    {
        var scene = new GPUScene();
        var lod0 = XRMesh.CreateTriangles(Vector3.Zero, Vector3.UnitX, Vector3.UnitY);
        var lod1 = XRMesh.CreateTriangles(Vector3.UnitZ, Vector3.UnitZ + Vector3.UnitX, Vector3.UnitZ + Vector3.UnitY);

        scene.RegisterLogicalMeshLODs([(lod0, 96.0f), (lod1, 0.0f)], out uint logicalMeshId, out string? failureReason).ShouldBeTrue(failureReason);
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

        scene.RegisterLogicalMeshLODs([(lod0, 96.0f), (lod1, 0.0f)], out uint logicalMeshId, out string? failureReason).ShouldBeTrue(failureReason);
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
        source.ShouldContain("const uint COMMAND_LOGICAL_MESH_ID = 14u;");
        source.ShouldContain("uint logicalMeshID = floatBitsToUint(culled[base + COMMAND_LOGICAL_MESH_ID])");
        source.ShouldContain("uint lodCount = floatBitsToUint(lodTable[entryBase + 0u])");
        source.ShouldContain("atomicOr(lodRequestMask[logicalMeshID], 1u << min(selectedLevel, 31u))");
        source.ShouldContain("culled[base + COMMAND_MESH_ID] = uintBitsToFloat(selectedMeshID)");
        source.ShouldContain("culled[base + COMMAND_LOD_LEVEL] = uintBitsToFloat(resolvedLevel)");
    }

    [Test]
    public void LOD_SelectShader_UsesProjectedScreenSpaceRadius()
    {
        string shaderSource = ReadWorkspaceFile("Build/CommonAssets/Shaders/Compute/Indirect/GPURenderLODSelect.comp");
        string passSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPURenderPassCollection/GPURenderPassCollection.IndirectAndMaterials.cs");
        string lodSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Models/Meshes/SubMeshLOD.cs");
        string gpuSceneSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene/GPUScene.AtlasManagement.cs");

        shaderSource.ShouldContain("uniform vec2 ProjectionScale;");
        shaderSource.ShouldContain("uniform vec2 ViewportSize;");
        shaderSource.ShouldContain("float ComputeProjectedScreenRadius(vec3 center, float radius)");
        shaderSource.ShouldContain("projectedScreenRadius >= ReadMinProjectedRadius(entryBase, lodLevel)");
        shaderSource.ShouldNotContain("ReadMaxDistance");
        shaderSource.ShouldNotContain("distanceToCamera <= ReadMaxDistance");

        passSource.ShouldContain("_lodSelectComputeShader.Uniform(\"ProjectionScale\", ResolveLodProjectionScale(camera));");
        passSource.ShouldContain("_lodSelectComputeShader.Uniform(\"ViewportSize\", ResolveLodViewportSize());");
        passSource.ShouldContain("RuntimeEngine.Rendering.State.RenderArea");
        lodSource.ShouldContain("public float MinProjectedScreenRadiusPixels");
        gpuSceneSource.ShouldContain("ResolveMinProjectedRadiusPixels(lod, lodMeshes.Count)");
        gpuSceneSource.ShouldContain("DefaultLod0MinProjectedRadiusPixels");
        gpuSceneSource.ShouldNotContain("lodMeshes.Add((lodMesh, lod.MaxVisibleDistance))");
    }

    [Test]
    public void LOD_ProjectRadiusSelection_DifferentiatesLargeNearAndSmallNearMeshes()
    {
        Vector2 projectionScale = Vector2.One;
        Vector2 viewportSize = new(1280.0f, 720.0f);
        float cameraDistance = 10.0f;
        float lod0MinProjectedRadius = 80.0f;

        float largeProjectedRadius = ComputeProjectedRadiusPixels(2.0f, cameraDistance, projectionScale, viewportSize);
        float smallProjectedRadius = ComputeProjectedRadiusPixels(0.25f, cameraDistance, projectionScale, viewportSize);

        largeProjectedRadius.ShouldBeGreaterThan(lod0MinProjectedRadius);
        smallProjectedRadius.ShouldBeLessThan(lod0MinProjectedRadius);
        SelectProjectedLodLevel(largeProjectedRadius, [lod0MinProjectedRadius, 0.0f]).ShouldBe(0u);
        SelectProjectedLodLevel(smallProjectedRadius, [lod0MinProjectedRadius, 0.0f]).ShouldBe(1u);

        static float ComputeProjectedRadiusPixels(float radius, float distanceToCenter, Vector2 projectionScale, Vector2 viewportSize)
        {
            float pixelScaleX = MathF.Abs(projectionScale.X) * MathF.Max(viewportSize.X, 1.0f) * 0.5f;
            float pixelScaleY = MathF.Abs(projectionScale.Y) * MathF.Max(viewportSize.Y, 1.0f) * 0.5f;
            float pixelScale = MathF.Max(pixelScaleX, pixelScaleY);
            return MathF.Max(radius, 0.0f) * pixelScale / MathF.Max(distanceToCenter, 0.001f);
        }

        static uint SelectProjectedLodLevel(float projectedScreenRadius, ReadOnlySpan<float> minProjectedRadii)
        {
            int maxLevel = Math.Min(minProjectedRadii.Length, 4);
            for (int lodLevel = 0; lodLevel < maxLevel; lodLevel++)
            {
                if (lodLevel == maxLevel - 1 || projectedScreenRadius >= minProjectedRadii[lodLevel])
                    return (uint)lodLevel;
            }

            return 0u;
        }
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
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs");

        source.ShouldContain("ExecuteSecondaryCommandBufferBatchParallel");
        source.ShouldContain("Task.Run");
        source.ShouldContain("IndirectDrawBatch");
        source.ShouldContain("DispatchCommandChainRecordingWorkers");
        source.ShouldContain("GetThreadCommandPool");
    }

    [Test]
    public void Vulkan_GpuPipelineProfilerToggle_DoesNotInstrumentMainRenderCommandBuffers()
    {
        string commandBuffers = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs")
            .Replace("\r\n", "\n");
        string frameTiming = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameTiming.cs")
            .Replace("\r\n", "\n");
        string gpuProfiler = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/RenderPipelineGpuProfiler.cs")
            .Replace("\r\n", "\n");
        string engineStats = ReadWorkspaceFile("XREngine/Engine/Subclasses/Rendering/Engine.Rendering.Stats.cs")
            .Replace("\r\n", "\n");
        string engineSettings = ReadWorkspaceFile("XREngine/Engine/Engine.Settings.cs")
            .Replace("\r\n", "\n");
        string renderCommands = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/RenderCommands/RenderCommandCollection.cs")
            .Replace("\r\n", "\n");
        string imguiRenderer = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Generic/AbstractRenderer.cs")
            .Replace("\r\n", "\n");
        string uiBatchCollector = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/UI/UIBatchCollector.cs")
            .Replace("\r\n", "\n");
        string viewportCommand = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/ViewportRenderCommand.cs")
            .Replace("\r\n", "\n");

        frameTiming.ShouldContain("private const bool EnableVulkanGpuProfilerCommandBufferInstrumentation = false;");
        frameTiming.ShouldContain("Vulkan GPU pipeline command timing is quarantined");
        frameTiming.ShouldContain("internal static string VulkanGpuProfilerCommandTimingStatusMessage");
        frameTiming.ShouldContain("if (!IsVulkanGpuProfilerCommandBufferInstrumentationEnabled)\n            return false;");
        frameTiming.ShouldContain("RecordBackendGpuTimingStatus(");
        gpuProfiler.ShouldContain("public bool ShouldInstrumentCommandScopes\n        => IsProfilingActive && AbstractRenderer.Current is OpenGLRenderer;");
        gpuProfiler.ShouldContain("VulkanRenderer.VulkanGpuProfilerCommandTimingStatusMessage");
        gpuProfiler.ShouldContain("if (!ShouldInstrumentCommandScopes)\n            return default;");
        gpuProfiler.ShouldContain("!ShouldInstrumentCommandScopes)\n            return false;");
        engineStats.ShouldContain("private static bool IsDebugOutputEnabled()");
        engineStats.ShouldNotContain("IsDebugOutputEnabled()\n                    => XREngine.Rendering.RenderDiagnosticsFlags.GLDebug ||\n                       Engine.EffectiveSettings.EnableGpuIndirectDebugLogging ||\n                       Engine.EditorPreferences.Debug.EnableGpuRenderPipelineProfiling;");
        engineSettings.ShouldContain("bool appliedNarrowProfilerChange = normalizedPropertyName is not null &&");
        engineSettings.ShouldContain("IsProfilerOnlyEditorDebugProperty(normalizedPropertyName) &&");
        engineSettings.ShouldContain("TryUpdateEffectiveEditorDebugProperty(normalizedPropertyName)");
        engineSettings.ShouldContain("ApplyEditorPreferencesRuntimeSideEffects(normalizedPropertyName);");
        engineSettings.ShouldContain("propertyName == nameof(EditorDebugOptions.EnableGpuRenderPipelineProfiling)");
        engineSettings.ShouldContain("propertyName == nameof(EditorDebugOptions.ProfilerPanelPaused)");

        renderCommands.ShouldContain("if (!profiler.ShouldInstrumentCommandScopes || ShouldSkipGpuScope(command))");
        imguiRenderer.ShouldContain("bool profilingActive = profiler.ShouldInstrumentCommandScopes;");
        uiBatchCollector.ShouldContain("bool profileGpu = profiler.ShouldInstrumentCommandScopes;");
        viewportCommand.ShouldContain("using var gpuScope = gpuProfiler.ShouldInstrumentCommandScopes");

        commandBuffers.ShouldContain("bool gpuPipelineProfilingActive =\n                IsVulkanGpuProfilerCommandBufferInstrumentationEnabled &&\n                RenderPipelineGpuProfiler.Instance.IsProfilingActive;");
        commandBuffers.ShouldContain("bool gpuProfilerCommandBufferStateDirty = IsVulkanGpuProfilerCommandBufferStateDirty");
        commandBuffers.ShouldContain("ClearVulkanGpuProfilerPendingQueries();\n                MarkCommandBufferVariantsDirty(imageIndex);");
        commandBuffers.ShouldContain("UpdateVulkanGpuProfilerCommandBufferState(");

        frameTiming.ShouldContain("private void ClearVulkanGpuProfilerPendingQueries()");
        frameTiming.ShouldContain("Array.Fill(_vulkanGpuProfilerPendingQueryCounts, 0);");
        frameTiming.ShouldContain("Array.Fill(_vulkanGpuProfilerQueryReady, false);");
        frameTiming.ShouldContain("_vulkanGpuProfilerCommandBufferInstrumented = null;");
    }

    [Test]
    public void Vulkan_ImGuiOverlay_UsesExplicitSwapchainLayoutHandoff()
    {
        string drawingCore = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Frame/VulkanRenderer.FrameLoop.cs")
            .Replace("\r\n", "\n");
        string commandBuffers = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/Commands/VulkanRenderer.CommandBufferRecording.cs")
            .Replace("\r\n", "\n");
        string imgui = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/Vulkan/UI/VulkanRenderer.ImGui.cs")
            .Replace("\r\n", "\n");

        drawingCore.ShouldContain("TryConsumeRenderableImGuiOverlaySnapshot(out imguiOverlaySnapshot)");
        drawingCore.ShouldContain("bool preserveSwapchainForImGuiOverlay = hasPendingImGuiOverlay && UseDynamicRenderingRenderTargets;");
        drawingCore.ShouldContain("EnsureCommandBufferRecorded(\n                        imageIndex,\n                        preserveSwapchainForImGuiOverlay,\n                        out swapchainLayoutAfterScene)");
        drawingCore.ShouldContain("TryRecordImGuiOverlayCommandBuffer(\n                            imageIndex,\n                            imguiOverlaySnapshot,\n                            swapchainLayoutAfterScene,");

        commandBuffers.ShouldContain("public bool PreserveSwapchainForOverlay { get; set; }");
        commandBuffers.ShouldContain("public ImageLayout RecordedSwapchainFinalLayout { get; set; } = ImageLayout.PresentSrcKhr;");
        commandBuffers.ShouldContain("variant.PreserveSwapchainForOverlay == preserveSwapchainForOverlay");
        commandBuffers.ShouldContain("int expectedPresentTransitions = preserveSwapchainForOverlay ? 0 : 1;");

        imgui.ShouldContain("ImageLayout initialSwapchainLayout");
        imgui.ShouldContain("initialSwapchainLayout,\n                ImageLayout.ColorAttachmentOptimal");
        imgui.ShouldNotContain("imageIndex,\n                ImageLayout.PresentSrcKhr,\n                ImageLayout.ColorAttachmentOptimal");
        imgui.ShouldNotContain("oldLayout == newLayout");
    }

    [Test]
    public void EditorProfilerPreferenceChanges_UseNarrowRenderPreferenceApply()
    {
        string engineSettings = ReadWorkspaceFile("XREngine/Engine/Engine.Settings.cs")
            .Replace("\r\n", "\n");
        string mcpSettings = ReadWorkspaceFile("XREngine.Editor/Mcp/Actions/EditorMcpActions.Settings.cs")
            .Replace("\r\n", "\n");

        engineSettings.ShouldContain("UpdateEffectiveEditorPreferences(e.PropertyName);");
        engineSettings.ShouldContain("UpdateEffectiveEditorPreferences(ResolveEditorPreferencesOverrideChangedPropertyName(sender, e.PropertyName));");
        engineSettings.ShouldContain("UpdateEffectiveEditorPreferences(propertyName);");
        engineSettings.ShouldContain("private static string? NormalizeEditorPreferenceChangePropertyName(string? propertyName)");
        engineSettings.ShouldContain("propertyName = propertyName[(dotIndex + 1)..];");
        engineSettings.ShouldContain("propertyName.EndsWith(OverrideSuffix, StringComparison.Ordinal)");
        engineSettings.ShouldContain("bool appliedNarrowProfilerChange = normalizedPropertyName is not null &&");
        engineSettings.ShouldContain("IsProfilerOnlyEditorDebugProperty(normalizedPropertyName) &&");
        engineSettings.ShouldContain("TryUpdateEffectiveEditorDebugProperty(normalizedPropertyName);");
        engineSettings.ShouldContain("ApplyEditorPreferencesRuntimeSideEffects(normalizedPropertyName);");
        engineSettings.ShouldContain("Rendering.ApplyEditorPreferencesChange(normalizedPropertyName);");
        engineSettings.ShouldContain("nameof(EditorDebugOptions.EnableGpuRenderPipelineProfiling)");
        engineSettings.ShouldContain("nameof(EditorDebugOptions.ProfilerPanelPaused)");
        engineSettings.ShouldContain("private static bool TryUpdateEffectiveEditorDebugProperty(string propertyName)");
        engineSettings.ShouldNotContain("ApplyEditorPreferencesRuntimeSideEffects();\n                Rendering.ApplyEditorPreferencesChange(null);\n                ApplyAudioPreferences();");

        mcpSettings.ShouldContain("Engine.RefreshEffectiveEditorPreferences(propertyName);");
        mcpSettings.ShouldNotContain("Engine.RefreshEffectiveEditorPreferences();");
    }

    [Test]
    public void RenderPipeline_CommandChainRebuilds_SuppressIntermediateStructureNotifications()
    {
        string containerSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/ViewportRenderCommandContainer.cs")
            .Replace("\r\n", "\n");
        string pipelineSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipeline.cs")
            .Replace("\r\n", "\n");

        containerSource.ShouldContain("internal static IDisposable SuppressStructureChangeNotifications()");
        containerSource.ShouldContain("if (StructureChangeNotificationsSuppressed)\n                return;");
        pipelineSource.ShouldContain("protected void InitializeCommandChain()");
        pipelineSource.ShouldContain("protected void RebuildCommandChain()");
        pipelineSource.ShouldContain("using (ViewportRenderCommandContainer.SuppressStructureChangeNotifications())\n            CommandChain = GenerateCommandChain();\n\n        NotifyCommandChainStructureChanged();");

        string pipelineDirectory = Path.Combine(
            GltfImportTestUtilities.ResolveWorkspaceRoot(),
            "XREngine.Runtime.Rendering",
            "Rendering",
            "Pipelines");
        string[] directAssignments = Directory
            .GetFiles(pipelineDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.EndsWith("XRRenderPipeline.cs", StringComparison.Ordinal))
            .Where(static path => File.ReadAllText(path).Contains("CommandChain = GenerateCommandChain();", StringComparison.Ordinal))
            .ToArray();

        directAssignments.ShouldBeEmpty();
    }

    [Test]
    public void RenderPipelineResourceKey_ExcludesVolatileVulkanFeatureProfileState()
    {
        string keySource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Resources/Records/ResourceGenerationKey.cs");
        string profileSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Resources/Records/RenderPipelineResourceProfile.cs");
        string instanceSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/XRRenderPipelineInstance.cs");

        keySource.ShouldNotContain("UseVulkanSafeFeatureProfile");
        profileSource.ShouldNotContain("UseVulkanSafeFeatureProfile");
        instanceSource.ShouldNotContain("VulkanFeatureProfile.IsActive");
        instanceSource.ShouldNotContain("vulkanSafe:");
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

        string windowSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/XRWindow.cs");
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
        string workspaceRoot = GltfImportTestUtilities.ResolveWorkspaceRoot();
        string path = Path.Combine(workspaceRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(path))
            return File.ReadAllText(path);

        throw new FileNotFoundException($"Unable to locate file '{relativePath}' from '{workspaceRoot}'.");
    }

    private sealed class StubBvhProvider(bool isReady) : IGpuBvhProvider
    {
        public XRDataBuffer? BvhNodeBuffer => null;
        public XRDataBuffer? BvhRangeBuffer => null;
        public XRDataBuffer? BvhMortonBuffer => null;
        public uint BvhNodeCount => 0u;
        public bool IsBvhReady => isReady;
    }

    private sealed class TestRenderable : IRenderable
    {
        public RenderInfo[] RenderedObjects { get; set; } = [];
    }
}
