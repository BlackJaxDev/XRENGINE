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
        // std430 BoundsWorkItem: two vec4 values and four uint values.
        Marshal.SizeOf<GPUPhysicsChainDispatcher.PhysicsChainGpuBoundsWorkItem>().ShouldBe(48);
        Marshal.SizeOf<GPUPhysicsChainDispatcher.PhysicsChainGpuBoundsCopyItem>().ShouldBe(8);
        Marshal.SizeOf<XREngine.Rendering.AdvancedGpuBoundsPatchRoute>().ShouldBe(32);
    }

    [Test]
    public void BoundsShader_EnvelopesCurrentAndPreviousPositionsWithInfluenceRadius()
    {
        string source = ReadWorkspaceFile(
            "Build/CommonAssets/Shaders/Compute/PhysicsChain/PhysicsChainBounds.comp");

        // The shader bounds the mesh-local box with the current and the previous
        // skin palettes, then pads the result with the material effect radius.
        source.ShouldContain("IncludePaletteBox(CurrentPaletteRows[row]");
        source.ShouldContain("IncludePaletteBox(PreviousPaletteRows[row]");
        source.ShouldContain("float effectPadding = item.LocalMinimum.w;");
        source.ShouldContain("finalMinimum -= numericMargin + vec3(effectPadding);");
        source.ShouldContain("finalMaximum += numericMargin + vec3(effectPadding);");
        source.ShouldContain("item.BoundsSlot * 2u");
        source.ToLowerInvariant().ShouldNotContain("readback");
    }

    [Test]
    public void Dispatcher_PublishesStableBoundsSlotsDirectlyToGpuScene()
    {
        string source = ReadWorkspaceFile(
            "XREngine.Runtime.Rendering/Rendering/PhysicsCompute/GPUPhysicsChainDispatcher.Bounds.cs");
        string copyShader = ReadWorkspaceFile(
            "Build/CommonAssets/Shaders/Compute/PhysicsChain/PhysicsChainBoundsToScene.comp");

        source.ShouldContain("_gpuBoundsSlotAllocator.Acquire(key, 1u)");
        source.ShouldContain("scene.CaptureRendererCommandIndices(routes)");
        source.ShouldContain("scene.SetRendererOwnsGpuCopiedAabb(binding.Renderer, true)");
        source.ShouldContain("scene.CommandAabbBuffer");
        source.ShouldContain("UsesCpuReadback: false");
        source.ShouldNotContain("WaitForGpu");
        copyShader.ShouldContain("ChainBoundsBits[source].xyz");
        copyShader.ShouldContain("ChainBoundsBits[source + 1u].xyz");
        copyShader.ShouldContain("isnan");
        copyShader.ShouldContain("isinf");
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
