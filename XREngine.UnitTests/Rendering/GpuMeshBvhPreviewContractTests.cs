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