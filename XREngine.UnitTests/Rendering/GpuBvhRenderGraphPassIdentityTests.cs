using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.RenderGraph;

namespace XREngine.UnitTests.Rendering;

/// <summary>Regression coverage for GPU-BVH Vulkan render-graph pass identity.</summary>
[TestFixture]
public sealed class GpuBvhRenderGraphPassIdentityTests
{
    [Test]
    public void BuildAccelerationStructure_ResolvesItsRegisteredSyntheticComputePass()
    {
        RenderPassMetadata[] metadata =
        [
            new(4, "UnrelatedGraphics", ERenderGraphPassStage.Graphics),
            new(100_020, VPRC_BuildAccelerationStructure.RenderGraphPassName, ERenderGraphPassStage.Compute),
        ];

        bool resolved = VPRC_BuildAccelerationStructure.TryResolveRenderGraphPassIndex(metadata, out int passIndex);

        resolved.ShouldBeTrue();
        passIndex.ShouldBe(100_020);
    }

    [Test]
    public void BuildAccelerationStructure_DoesNotInventPassWhenMetadataIsMissing()
    {
        RenderPassMetadata[] metadata =
        [
            new(100_020, "DifferentComputePass", ERenderGraphPassStage.Compute),
        ];

        VPRC_BuildAccelerationStructure.TryResolveRenderGraphPassIndex(metadata, out int passIndex).ShouldBeFalse();
        passIndex.ShouldBe(int.MinValue);
    }

    [Test]
    public void BuildAccelerationStructure_ScopesEveryComputeProducerAndFailsVisibleOnVulkanMetadataLoss()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Pipelines/Commands/VPRC_BuildAccelerationStructure.cs");

        int resolve = source.IndexOf("int passIndex = ResolveRenderGraphPassIndex();", StringComparison.Ordinal);
        int scope = source.IndexOf("PushRenderGraphPassIndex(passIndex)", StringComparison.Ordinal);
        int skinnedBounds = source.IndexOf("RefreshAllSkinnedAabbs(gpuScene)", StringComparison.Ordinal);
        int bvhBuild = source.IndexOf("gpuScene.PrepareBvhForCulling(primitiveCount)", StringComparison.Ordinal);

        resolve.ShouldBeGreaterThanOrEqualTo(0);
        scope.ShouldBeGreaterThan(resolve);
        skinnedBounds.ShouldBeGreaterThan(scope);
        bvhBuild.ShouldBeGreaterThan(skinnedBounds);
        source.ShouldContain("passIndex == int.MinValue && AbstractRenderer.Current is VulkanRenderer");
        source.ShouldContain("Skipping acceleration-structure compute because no render-graph pass metadata was generated");
        source.ShouldContain("GetOrCreateSyntheticPass(RenderGraphPassName, ERenderGraphPassStage.Compute)");
    }

    [Test]
    public void GpuBvhPrograms_HaveStageSpecificDiagnosticNames()
    {
        string source = ReadWorkspaceFile("XREngine.Runtime.Rendering/Rendering/Compute/GpuBvhTree.Programs.cs");

        source.ShouldContain("Name = $\"GpuBvh.{Path.GetFileNameWithoutExtension(path)}\"");
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XRENGINE.slnx")))
                return File.ReadAllText(Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar)))
                    .Replace("\r\n", "\n");
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the XRENGINE workspace root.");
    }
}
