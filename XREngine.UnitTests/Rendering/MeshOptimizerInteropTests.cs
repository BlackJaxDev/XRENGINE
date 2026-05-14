using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering;
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
        List<Vector3> positions = new(240 * 3);
        for (int i = 0; i < 240; i++)
        {
            float x = i % 24;
            float y = i / 24;
            float z = i * 0.001f;
            positions.Add(new Vector3(x, y, z));
            positions.Add(new Vector3(x + 0.4f, y, z + 0.0001f));
            positions.Add(new Vector3(x, y + 0.4f, z + 0.0002f));
        }

        XRMesh mesh = XRMesh.CreateTriangles(positions);
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
    public void Meshlets_AreRebuiltLazilyInsteadOfDuringGpuSceneSwap()
    {
        string gpuSceneSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene.cs").Replace("\r\n", "\n");
        string hybridSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/HybridRenderingManager.cs").Replace("\r\n", "\n");

        string swapBody = ExtractSwapCommandBuffersBody(gpuSceneSource);
        swapBody.ShouldNotContain("RebuildMeshletsFromUpdatingCommands");
        gpuSceneSource.ShouldContain("public bool RenderMeshlets(XRCamera camera, int renderPass)");
        hybridSource.ShouldContain("scene.RenderMeshlets(");
        hybridSource.ShouldContain("RenderCommandCollection.TestCpuSoftwareOcclusionForGpuSource");
    }

    [Test]
    public void MeshletTaskShaderConsumesCpuCommandVisibility()
    {
        string gpuSceneSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Commands/GPUScene.cs").Replace("\r\n", "\n");
        string meshletCollectionSource = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Meshlets/MeshletCollection.cs").Replace("\r\n", "\n");
        string taskShader = ReadWorkspaceFile("Build/CommonAssets/Shaders/Meshlets/MeshletCulling.task").Replace("\r\n", "\n");

        gpuSceneSource.ShouldContain("Func<GPUScene, uint, bool>? commandVisibility");
        meshletCollectionSource.ShouldContain("CommandVisibilityBuffer");
        meshletCollectionSource.ShouldContain("UseCpuCommandVisibility");
        taskShader.ShouldContain("uniform uint UseCpuCommandVisibility");
        taskShader.ShouldContain("buffer CommandVisibilityBuffer");
        taskShader.ShouldContain("commandVisibility[m.Meta.x] != 0u");
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
