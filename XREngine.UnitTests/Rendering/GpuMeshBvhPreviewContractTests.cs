using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GpuMeshBvhPreviewContractTests
{
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
    public void GpuMeshBvhLargeMortonSort_AlternatesTileDirectionForBitonicMerge()
    {
        string dispatchSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.Dispatch.cs");
        string tileSortShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/RenderPipeline/OctreeGeneration/sort_morton_tiles.comp");
        string mergeShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Scene3D/RenderPipeline/OctreeGeneration/merge_morton.comp");

        dispatchSource.ShouldContain("tileProgram.DispatchCompute(paddedCount / 1024u");
        tileSortShader.ShouldContain("uint i = base + tid;");
        tileSortShader.ShouldContain("bool up = ((i & k) == 0u);");
        tileSortShader.ShouldNotContain("bool up = ((tid & k) == 0u);");
        mergeShader.ShouldContain("bool up = ((i & K) == 0u);");
    }

    [Test]
    public void GpuMeshBvhClickPick_UsesMortonSortedPackedTriangles()
    {
        string gpuMeshBvhSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuMeshBvh.cs");
        string pickSource = ReadWorkspaceFile("XRENGINE/Rendering/XRWorldInstance.cs");
        string editorSource = ReadWorkspaceFile("XREngine.Editor/EditorFlyingCameraPawnComponent.cs");
        string glDataBufferSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenGL/Types/Buffers/GLDataBuffer.cs");
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
