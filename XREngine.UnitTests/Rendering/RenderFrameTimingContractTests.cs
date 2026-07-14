using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RenderFrameTimingContractTests
{
    [Test]
    public void WorldPreCollectVisible_AppliesRenderableMatricesBeforeCpuTreeSwap()
    {
        string source = ReadWorkspaceFile("XREngine/Rendering/XRWorldInstance.cs").Replace("\r\n", "\n");

        int preCollectStart = source.IndexOf("private void PreCollectVisible()", StringComparison.Ordinal);
        preCollectStart.ShouldBeGreaterThanOrEqualTo(0);

        int preCollectEnd = source.IndexOf("public void GlobalCollectVisible()", preCollectStart, StringComparison.Ordinal);
        preCollectEnd.ShouldBeGreaterThan(preCollectStart);

        string preCollectBody = source.Substring(preCollectStart, preCollectEnd - preCollectStart);

        int applyRenderMatrices = preCollectBody.IndexOf("ApplyRenderMatrixChanges();", StringComparison.Ordinal);
        int applyRenderableMatrices = preCollectBody.IndexOf("RenderableMesh.ProcessPendingRenderMatrixUpdates();", StringComparison.Ordinal);
        int swapCpuTree = preCollectBody.IndexOf("VisualScene.GlobalCollectVisible();", StringComparison.Ordinal);

        applyRenderMatrices.ShouldBeGreaterThanOrEqualTo(0);
        applyRenderableMatrices.ShouldBeGreaterThan(applyRenderMatrices);
        swapCpuTree.ShouldBeGreaterThan(applyRenderableMatrices);
    }

    [Test]
    public void VisualScene3D_GlobalCollectVisible_RefreshesSkinnedBoundsBeforeCpuTreeSwap()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/VisualScene3D.cs").Replace("\r\n", "\n");

        int collectStart = source.IndexOf("public override void GlobalCollectVisible()", StringComparison.Ordinal);
        collectStart.ShouldBeGreaterThanOrEqualTo(0);

        int collectEnd = source.IndexOf("public override void GlobalPreRender()", collectStart, StringComparison.Ordinal);
        collectEnd.ShouldBeGreaterThan(collectStart);

        string collectBody = source.Substring(collectStart, collectEnd - collectStart);

        int processRenderables = collectBody.IndexOf("ProcessPendingRenderableOperations();", StringComparison.Ordinal);
        int refreshSkinnedBounds = collectBody.IndexOf("RefreshSkinnedCpuCullingBounds();", StringComparison.Ordinal);
        int swapCpuTree = collectBody.IndexOf("ActiveCpuRenderTree.Swap();", StringComparison.Ordinal);

        processRenderables.ShouldBeGreaterThanOrEqualTo(0);
        refreshSkinnedBounds.ShouldBeGreaterThan(processRenderables);
        swapCpuTree.ShouldBeGreaterThan(refreshSkinnedBounds);
    }

    [Test]
    public void RuntimeWorld_GlobalPreCollectVisible_PublishesPendingMeshMatrices()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/XRWorldInstance.Runtime.cs").Replace("\r\n", "\n");

        int preCollectStart = source.IndexOf("public void GlobalPreCollectVisible()", StringComparison.Ordinal);
        preCollectStart.ShouldBeGreaterThanOrEqualTo(0);

        int preCollectEnd = source.IndexOf("public void GlobalPreRender()", preCollectStart, StringComparison.Ordinal);
        preCollectEnd.ShouldBeGreaterThan(preCollectStart);

        string preCollectBody = source.Substring(preCollectStart, preCollectEnd - preCollectStart);
        int publishMatrices = preCollectBody.IndexOf("RenderableMesh.ProcessPendingRenderMatrixUpdates();", StringComparison.Ordinal);
        int publishScene = preCollectBody.IndexOf("VisualScene.GlobalCollectVisible();", StringComparison.Ordinal);

        publishMatrices.ShouldBeGreaterThanOrEqualTo(0);
        publishScene.ShouldBeGreaterThan(publishMatrices);
    }

    [Test]
    public void OpenXrCollectVisible_PublishesWorldTransformsBeforeSpsCollection()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/API/Rendering/OpenXR/OpenXRAPI.FrameLifecycle.cs").Replace("\r\n", "\n");

        int collectStart = source.IndexOf("private void OpenXrCollectVisible()", StringComparison.Ordinal);
        collectStart.ShouldBeGreaterThanOrEqualTo(0);

        int collectEnd = source.IndexOf("private void CollectOpenXrEyeVisible", collectStart, StringComparison.Ordinal);
        collectEnd.ShouldBeGreaterThan(collectStart);

        string collectBody = source.Substring(collectStart, collectEnd - collectStart);
        int publishMatrices = collectBody.IndexOf("resolvedWorld.GlobalPreCollectVisible();", StringComparison.Ordinal);
        int collectSps = collectBody.IndexOf("CollectOpenXrTrueSinglePassStereoVisible(", StringComparison.Ordinal);

        publishMatrices.ShouldBeGreaterThanOrEqualTo(0);
        collectSps.ShouldBeGreaterThan(publishMatrices);
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string repoRoot = ResolveRepoRoot();
        string path = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Expected workspace file '{path}' to exist.");
        return File.ReadAllText(path);
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

        throw new DirectoryNotFoundException("Could not locate repository root from test directory.");
    }
}
