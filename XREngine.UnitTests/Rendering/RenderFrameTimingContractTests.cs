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