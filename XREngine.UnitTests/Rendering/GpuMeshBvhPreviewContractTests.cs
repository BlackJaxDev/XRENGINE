using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GpuMeshBvhPreviewContractTests
{
    [Test]
    public void MathBvhGpuPreview_QueuesWorkloadAndDrawsInLateDebugOverlay()
    {
        string componentSource = ReadWorkspaceFile("XREngine.Editor/Unit Tests/Math/MathBvhTestComponent.cs");
        string debugSource = ReadWorkspaceFile("XREngine.Editor/Unit Tests/Math/MathBvhTestComponent.Debug.cs");
        string rendererSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhDebugLineRenderer.cs");
        string queueSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhDebugOverlayQueue.cs");
        string overlaySource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_RenderDebugShapes.cs");
        string modelPreviewSource = ReadWorkspaceFile("XRENGINE/Scene/Components/Debug/Visualize/ModelBvhPreviewComponent.cs");

        componentSource.ShouldContain("=> Interlocked.Exchange(ref _gpuWorkQueued, 1);");
        componentSource.ShouldNotContain("Engine.EnqueueRenderThreadTask(");
        componentSource.ShouldNotContain("RenderedObjects[0].IsVisible = value;");

        int passScope = debugSource.IndexOf("PushRenderGraphPassIndex(", StringComparison.Ordinal);
        int workload = debugSource.IndexOf("ExecuteQueuedGpuWorkload();", StringComparison.Ordinal);
        int debugGate = debugSource.IndexOf("if (!DebugRenderEnabled)", StringComparison.Ordinal);
        passScope.ShouldBeGreaterThanOrEqualTo(0);
        workload.ShouldBeGreaterThan(passScope);
        debugGate.ShouldBeGreaterThan(workload);
        debugSource.ShouldContain("_gpuDebugRenderer.Queue(");
        debugSource.ShouldNotContain("_gpuDebugRenderer.Render(");
        modelPreviewSource.ShouldContain("_gpuRenderer.Queue(");
        modelPreviewSource.ShouldNotContain("_gpuRenderer.Render(");

        rendererSource.ShouldContain("ConditionalWeakTable<XRRenderPipelineInstance.RenderingState, GpuBvhDebugOverlayQueue>");
        rendererSource.ShouldContain("internal static void RenderQueued(");
        rendererSource.ShouldNotContain("PushRenderGraphPassIndex(");
        overlaySource.ShouldContain("GpuBvhDebugLineRenderer.RenderQueued(instance.RenderState);");
        queueSource.ShouldContain("(_pending, _rendering) = (_rendering, _pending);");
        queueSource.ShouldContain("ReferenceEquals(_pending[i].Renderer, request.Renderer)");

        string immediate = Slice(
            rendererSource,
            "private bool RenderImmediate(",
            "private static void ResetStencilState",
            StringComparison.Ordinal);
        int ensureResources = immediate.IndexOf("EnsureResources(visualizedLines)", StringComparison.Ordinal);
        int prepareRenderer = immediate.IndexOf("_linesRenderer!.TryPrepareForRendering(forceNoStereo: true)", StringComparison.Ordinal);
        int dispatch = immediate.IndexOf("_computeProgram.DispatchCompute(", StringComparison.Ordinal);
        int draw = immediate.IndexOf("_linesRenderer.Render(", StringComparison.Ordinal);
        ensureResources.ShouldBeGreaterThanOrEqualTo(0);
        prepareRenderer.ShouldBeGreaterThan(ensureResources);
        dispatch.ShouldBeGreaterThan(prepareRenderer);
        draw.ShouldBeGreaterThan(dispatch);
        rendererSource.ShouldContain("Name = \"GpuBvhDebugLines\"");
    }

    [Test]
    public void MeshBvhPreview_AccuracyInputControlsMaxLeafPrimitives()
    {
        string editorSource = ReadWorkspaceFile("XREngine.Editor/ComponentEditors/ModelComponentEditor.cs");
        string gpuMeshBvhSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuMeshBvh.cs");
        string renderableGpuBvhSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.GpuBvh.cs");

        editorSource.ShouldContain("public float MeshAccuracy = BvhPreviewMaxMeshAccuracy;");
        editorSource.ShouldContain("ImGui.InputFloat(\"Mesh Accuracy\", ref meshAccuracy, 0.01f, 0.1f, \"%.3f\")");
        editorSource.ShouldContain("uint maxLeafPrimitives = GetBvhMaxLeafPrimitives(state.MeshAccuracy);");
        editorSource.ShouldContain("mesh.HasCurrentGpuMeshBvhForMaxLeafPrimitives(maxLeafPrimitives)");
        editorSource.ShouldContain("mesh.PrepareGpuMeshBvh(realtimeSkinned, maxLeafPrimitives: maxLeafPrimitives)");
        editorSource.ShouldContain("return state.GpuRenderer.Queue(");
        editorSource.ShouldNotContain("return state.GpuRenderer.Render(");

        gpuMeshBvhSource.ShouldContain("public const uint DefaultMaxLeafPrimitives = 1u;");
        gpuMeshBvhSource.ShouldContain("public uint MaxLeafPrimitives => _tree.MaxLeafPrimitives;");
        gpuMeshBvhSource.ShouldContain("ConfigureTree(maxLeafPrimitives);");
        gpuMeshBvhSource.ShouldContain("_tree.MaxLeafPrimitives = Math.Max(1u, maxLeafPrimitives);");

        renderableGpuBvhSource.ShouldContain("HasCurrentGpuMeshBvhForMaxLeafPrimitives(uint maxLeafPrimitives)");
        renderableGpuBvhSource.ShouldContain("_gpuMeshBvh.MaxLeafPrimitives == clampedMaxLeafPrimitives");
    }

    [Test]
    public void GpuMeshBvhHoverBounds_UsesLastKnownGpuBoundsWithoutForcingPrepass()
    {
        string renderableGpuBvhSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.GpuBvh.cs");
        string renderableDebugSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Scene/Components/Mesh/RenderableMesh.Debug.cs");
        string boundsCalculatorSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/SkinnedMeshBoundsCalculator.cs");

        string requestBounds = Slice(
            renderableGpuBvhSource,
            "public bool TryGetGpuMeshBvhRequestWorldBounds(out AABB worldBounds)",
            "internal void ProcessPendingGpuMeshBvhRefresh()",
            StringComparison.Ordinal);

        requestBounds.ShouldContain("TryGetLastKnownGpuSkinnedWorldBounds(out worldBounds)");
        requestBounds.ShouldContain("return TryGetWorldBounds(out worldBounds);");
        requestBounds.ShouldNotContain("TryGetLiveGpuSkinnedWorldBounds");

        renderableDebugSource.ShouldContain("public bool TryGetLastKnownGpuSkinnedWorldBounds(out AABB bounds)");
        renderableDebugSource.ShouldContain("TryReadLastKnownGpuDebugBounds(this, out bounds)");

        string lastKnownRead = Slice(
            boundsCalculatorSource,
            "internal bool TryReadLastKnownGpuDebugBounds(RenderableMesh mesh, out AABB worldBounds)",
            "private bool TryPrepareStandaloneGpuDebugBounds",
            StringComparison.Ordinal);

        lastKnownRead.ShouldContain("SkinningPrepassDispatcher.Instance.TryReadSkinnedWorldBounds(renderer, out worldBounds)");
        lastKnownRead.ShouldNotContain("RunForGpuMeshBvh");
        lastKnownRead.ShouldNotContain("Run(renderer");
    }

    [Test]
    public void GpuMeshBvhLargeMortonSort_UsesStableRadixPipeline()
    {
        string dispatchSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.Dispatch.cs");
        string programsSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.Programs.cs");
        string histogramShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/RenderPipeline/OctreeGeneration/radix_morton_histogram.comp");
        string prefixShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/RenderPipeline/OctreeGeneration/radix_morton_prefix.comp");
        string scatterShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/RenderPipeline/OctreeGeneration/radix_morton_scatter.comp");

        dispatchSource.ShouldContain("for (uint shift = 0u; shift < 32u; shift += 8u)");
        dispatchSource.ShouldContain("histogramProgram.DispatchCompute");
        dispatchSource.ShouldContain("prefixProgram.DispatchCompute");
        dispatchSource.ShouldContain("scatterProgram.DispatchCompute");
        programsSource.ShouldContain("radix_morton_histogram.comp");
        programsSource.ShouldContain("radix_morton_prefix.comp");
        programsSource.ShouldContain("radix_morton_scatter.comp");
        histogramShader.ShouldContain("atomicAdd(histogram[digit], 1u)");
        prefixShader.ShouldContain("offsets[index] = running");
        scatterShader.ShouldContain("localRank += uint(digits[prior] == digit)");
        scatterShader.ShouldContain("outputObjects[outputIndex] = inputObjects[index]");
    }

    [Test]
    public void GpuMeshBvhStaticTriangles_ArePackedOnlyWhenInvalidated()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuMeshBvh.cs");
        string staticPack = Slice(
            source,
            "private bool PackStaticTriangles(XRMesh mesh, uint triangleCount)",
            "private static XRDataBuffer? GetOrCreateStorageView",
            StringComparison.Ordinal);

        staticPack.ShouldContain("if (_packedTrianglesUploaded)");
        staticPack.ShouldContain("return true;");
        source.ShouldContain("InvalidateStaticGeometryIfChanged(mesh);");
        source.ShouldContain("_staticGeometryRevision == revision");
        source.ShouldContain("ulong revision = source?.Revision ?? 0u;");
        source.ShouldContain("_packedTrianglesUploaded = false;");
        source.ShouldContain("_packedTrianglesUploaded = true;");
    }

    [Test]
    public void GpuMeshBvhClickPick_UsesMortonSortedPackedTriangles()
    {
        string gpuMeshBvhSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuMeshBvh.cs");
        string pickSource = ReadWorkspaceFile("XRENGINE/Rendering/XRWorldInstance.cs");
        string editorSource = ReadWorkspaceFile("XREngine.Editor/EditorFlyingCameraPawnComponent.cs");
        string glDataBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/BackendObjects/Buffers/GLDataBuffer.cs");
        string visualSceneSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/VisualScene3D.cs");
        string raycastDispatcherSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/BvhRaycastDispatcher.cs");
        string packShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/RenderPipeline/mesh_bvh_pack_triangles.comp");

        gpuMeshBvhSource.ShouldContain("PackedTriangleShaderPath");
        gpuMeshBvhSource.ShouldContain("public XRDataBuffer? PackedTriangleBuffer => _packedTriangleBuffer;");
        gpuMeshBvhSource.ShouldContain("program.BindBuffer(morton, 3);");
        gpuMeshBvhSource.ShouldContain("program.BindBuffer(_packedTriangleBuffer!, 4);");

        packShader.ShouldContain("uint sourceTriangleIndex = MortonData[sortedIndex * 2u + 1u];");
        packShader.ShouldContain("uvec4 TriangleIndices[];");
        packShader.ShouldContain("PackedTriangles[sortedIndex].extra = uvec4(sourceTriangleIndex, sourceTriangleIndex, 0u, 0u);");

        pickSource.ShouldContain("BvhRaycastRequest");
        pickSource.ShouldContain("TriangleBuffer = bvh.PackedTriangleBuffer");
        pickSource.ShouldContain("GpuMeshBvhPickCandidate");
        pickSource.ShouldContain("DispatchLatestGpuMeshBvhPick");
        pickSource.ShouldContain("FinishGpuMeshBvhPick(state, generation)");
        pickSource.ShouldContain("DispatchQueued");
        pickSource.ShouldContain("RaycastInFlight");
        pickSource.ShouldContain("state.Candidate is { IsComplete: false } pending");
        pickSource.ShouldNotContain("forceRebuild: mesh.HasGpuMeshBvhRefreshRequest");
        pickSource.ShouldNotContain("QueueGpuMeshBvhInteractionRefresh");

        // The pick setting is a boolean toggle and the CPU triangle-test fallback was removed.
        pickSource.ShouldContain("GpuMeshBvhClickPickEnabled");
        pickSource.ShouldNotContain("EnsureGpuMeshBvhClickPickCpuBvh");
        pickSource.ShouldNotContain("EMeshBvhClickPickMode");

        // The GPU readback resolves the exact face/edge/vertex for the requested hit mode.
        pickSource.ShouldContain("TryBuildPickResult(candidate.HitMode");

        string uploadRay = Slice(
            pickSource,
            "public void UploadRay(Vector3 origin, Vector3 direction, float maxDistance)",
            "[StructLayout(LayoutKind.Sequential)]",
            StringComparison.Ordinal);

        uploadRay.ShouldContain("RayBuffer.PushData();");
        uploadRay.ShouldNotContain("HitBuffer.PushData();");

        glDataBufferSource.ShouldContain("Api.GetNamedBufferParameter(id, BufferPNameARB.Size, out int allocatedSize);");
        glDataBufferSource.ShouldContain("if (allocatedSize > 0)");
        glDataBufferSource.ShouldContain("RecreateBuffer();");

        visualSceneSource.ShouldContain("BvhRaycasts.ProcessDispatches();");
        visualSceneSource.ShouldContain("BvhRaycasts.ProcessCompletions();");
        visualSceneSource.ShouldNotContain("BvhRaycasts.SetEnabled(false, \"initial settings disabled\")");
        visualSceneSource.ShouldNotContain("BvhRaycasts.SetEnabled(false, \"disabled by settings\")");

        raycastDispatcherSource.ShouldContain("public bool Enqueue(BvhRaycastRequest request)");
        raycastDispatcherSource.ShouldContain("program.Uniform(\"uRayCount\", request.RayCount);");
        raycastDispatcherSource.ShouldContain("program.Uniform(\"uRootIndex\", request.RootNodeIndex);");
        raycastDispatcherSource.ShouldContain("program.Uniform(\"uPacketWidth\", packetWidth);");
        raycastDispatcherSource.ShouldContain("program.Uniform(\"uUsePacketMode\", request.UsePacketMode ? 1u : 0u);");
        raycastDispatcherSource.ShouldContain("program.Uniform(\"uAnyHitMode\", request.AnyHit ? 1u : 0u);");
        raycastDispatcherSource.ShouldContain("program.Uniform(\"uMaxStackDepth\", request.MaxStackDepth ?? DefaultStackLimit);");
        raycastDispatcherSource.ShouldNotContain("program.Uniform(\"uRayCount\", (int)request.RayCount);");
        raycastDispatcherSource.ShouldNotContain("program.Uniform(\"uMaxStackDepth\", (int)(request.MaxStackDepth ?? DefaultStackLimit));");

        string preferencesSource = ReadWorkspaceFile("XRENGINE/Settings/EditorPreferences.cs");
        preferencesSource.ShouldContain("public bool GpuMeshBvhClickPickEnabled");
        preferencesSource.ShouldNotContain("enum EMeshBvhClickPickMode");

        editorSource.ShouldContain("RegisterPendingGpuSelection(gpuCandidate)");
        editorSource.ShouldContain("data is GpuMeshBvhPickCandidate gpuCandidate");
    }

    private static string Slice(string source, string startToken, string endToken, StringComparison comparison)
    {
        string normalized = source.Replace("\r\n", "\n");
        int start = normalized.IndexOf(startToken, comparison);
        start.ShouldBeGreaterThanOrEqualTo(0, $"Could not find start token '{startToken}'.");

        int end = normalized.IndexOf(endToken, start + startToken.Length, comparison);
        end.ShouldBeGreaterThan(start, $"Could not find end token '{endToken}' after '{startToken}'.");

        return normalized[start..end];
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string root = ResolveWorkspaceRoot();
        string fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(fullPath).ShouldBeTrue($"Expected workspace file to exist: {relativePath}");
        return File.ReadAllText(fullPath).Replace("\r\n", "\n");
    }

    private static string ResolveWorkspaceRoot()
    {
        string? directory = TestContext.CurrentContext.TestDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            if (File.Exists(Path.Combine(directory, "XRENGINE.slnx")) ||
                File.Exists(Path.Combine(directory, "XRENGINE.sln")))
            {
                return directory;
            }

            directory = Directory.GetParent(directory)?.FullName;
        }

        return Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "../../../../"));
    }
}
