using System.Runtime.InteropServices;
using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Compute;

namespace XREngine.UnitTests.Physics;

[TestFixture]
public sealed class PhysicsChainGpuBoundsContractTests
{
    [Test]
    public void BoundsRecords_MatchShaderStd430Layout()
    {
        Marshal.SizeOf<GPUPhysicsChainDispatcher.PhysicsChainGpuBoundsWorkItem>().ShouldBe(16);
        Marshal.SizeOf<GPUPhysicsChainDispatcher.PhysicsChainGpuBoundsCopyItem>().ShouldBe(8);
    }

    [Test]
    public void BoundsShader_EnvelopesCurrentAndPreviousPositionsWithInfluenceRadius()
    {
        string source = ReadWorkspaceFile(
            "Build/CommonAssets/Shaders/Compute/PhysicsChain/PhysicsChainBounds.comp");

        source.ShouldContain("particle.Position - expansion");
        source.ShouldContain("particle.Position + expansion");
        source.ShouldContain("particle.PrevPosition - expansion");
        source.ShouldContain("particle.PrevPosition + expansion");
        source.ShouldContain("item.BoundsSlot * 2u");
        source.ToLowerInvariant().ShouldNotContain("readback");
    }

    [Test]
    public void Dispatcher_PublishesStableBoundsSlotsDirectlyToGpuScene()
    {
        string source = ReadWorkspaceFile(
            "XRENGINE/Rendering/Compute/GPUPhysicsChainDispatcher.Bounds.cs");
        string copyShader = ReadWorkspaceFile(
            "Build/CommonAssets/Shaders/Compute/PhysicsChain/PhysicsChainBoundsToScene.comp");

        source.ShouldContain("_gpuBoundsSlotAllocator.Acquire(key, 1u)");
        source.ShouldContain("scene.TryGetCommandIndicesForRenderer");
        source.ShouldContain("scene.SetRendererOwnsGpuAabb(binding.Renderer, true)");
        source.ShouldContain("scene.CommandAabbBuffer");
        source.ShouldContain("UsesCpuReadback: false");
        source.ShouldNotContain("WaitForGpu");
        copyShader.ShouldContain("SceneBoundsBits[target] = ChainBoundsBits[source]");
        copyShader.ShouldContain("SceneBoundsBits[target + 1u] = ChainBoundsBits[source + 1u]");
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
