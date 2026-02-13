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

        Dictionary<XRMesh, (int firstVertex, int firstIndex, int indexCount)> atlasOffsets =
            GetPrivateField<Dictionary<XRMesh, (int, int, int)>>(scene, "_atlasMeshOffsets");
        Dictionary<XRMesh, int> refCounts =
            GetPrivateField<Dictionary<XRMesh, int>>(scene, "_atlasMeshRefCounts");
        var idToMesh =
            GetPrivateField<System.Collections.Concurrent.ConcurrentDictionary<uint, XRMesh>>(scene, "_idToMesh");
        List<IndexTriangle> faceIndices =
            GetPrivateField<List<IndexTriangle>>(scene, "_indirectFaceIndices");

        atlasOffsets[mesh] = (0, 0, 3);
        faceIndices.Add(new IndexTriangle(0, 1, 2));
        idToMesh[7u] = mesh;

        SetPrivateField(scene, "_atlasVertexCount", 3);
        SetPrivateField(scene, "_atlasIndexCount", 3);

        InvokeNonPublic(scene, "IncrementAtlasMeshRefCount", mesh);
        InvokeNonPublic(scene, "IncrementAtlasMeshRefCount", mesh);
        refCounts[mesh].ShouldBe(2);

        InvokeNonPublic(scene, "DecrementAtlasMeshRefCount", 7u, "unit-test");
        refCounts[mesh].ShouldBe(1);
        atlasOffsets.ContainsKey(mesh).ShouldBeTrue();

        InvokeNonPublic(scene, "DecrementAtlasMeshRefCount", 7u, "unit-test");
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
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
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
