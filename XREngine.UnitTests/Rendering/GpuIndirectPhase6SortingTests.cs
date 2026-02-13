using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GpuIndirectPhase6SortingTests
{
    [Test]
    public void SortDomainPolicy_ResolvesExpectedDomains_ByRenderPass()
    {
        GpuSortPolicy.ResolveSortDomain(
            (int)EDefaultRenderPass.OpaqueForward,
            EGpuSortDomainPolicy.OpaqueFrontToBackTransparentBackToFront)
            .ShouldBe(EGpuSortDomain.OpaqueFrontToBack);

        GpuSortPolicy.ResolveSortDomain(
            (int)EDefaultRenderPass.TransparentForward,
            EGpuSortDomainPolicy.OpaqueFrontToBackTransparentBackToFront)
            .ShouldBe(EGpuSortDomain.TransparentBackToFront);

        GpuSortPolicy.ResolveSortDomain(
            (int)EDefaultRenderPass.OnTopForward,
            EGpuSortDomainPolicy.OpaqueFrontToBackTransparentBackToFront)
            .ShouldBe(EGpuSortDomain.MaterialStateGrouping);
    }

    [Test]
    public void DistanceSortKey_Encoding_IsDeterministic_AndDirectionAware()
    {
        uint ascNear = GpuSortPolicy.EncodeDistanceSortKey(10.0f, GPUSortDirection.Ascending);
        uint ascFar = GpuSortPolicy.EncodeDistanceSortKey(100.0f, GPUSortDirection.Ascending);
        ascNear.ShouldBeLessThan(ascFar);

        uint descNear = GpuSortPolicy.EncodeDistanceSortKey(10.0f, GPUSortDirection.Descending);
        uint descFar = GpuSortPolicy.EncodeDistanceSortKey(100.0f, GPUSortDirection.Descending);
        descNear.ShouldBeGreaterThan(descFar);

        GpuSortPolicy.EncodeDistanceSortKey(42.5f, GPUSortDirection.Ascending)
            .ShouldBe(GpuSortPolicy.EncodeDistanceSortKey(42.5f, GPUSortDirection.Ascending));
    }

    [Test]
    public void Phase6_HostAndShaderContracts_ArePresent()
    {
        string hostSource = ReadWorkspaceFile("XRENGINE/Rendering/Commands/GPURenderPassCollection.IndirectAndMaterials.cs");
        hostSource.ShouldContain("GpuSortPolicy.ResolveSortDomain(RenderPass, Engine.Rendering.Settings.GpuSortDomainPolicy)");
        hostSource.ShouldContain("_buildKeysComputeShader.Uniform(\"SortDomain\"");
        hostSource.ShouldContain("_buildKeysComputeShader.Uniform(\"SortDirection\"");

        string shaderSource = ReadWorkspaceFile("Build/CommonAssets/Shaders/Compute/GPURenderBuildKeys.comp");
        shaderSource.ShouldContain("uniform int SortDomain;");
        shaderSource.ShouldContain("uniform int SortDirection;");
        shaderSource.ShouldContain("SORT_DOMAIN_TRANSPARENT_BACK_TO_FRONT");
        shaderSource.ShouldContain("EncodeDistanceKey(renderDistance)");

        string batchShaderSource = ReadWorkspaceFile("Build/CommonAssets/Shaders/Compute/GPURenderBuildBatches.comp");
        batchShaderSource.ShouldContain("uniform int RadixSortThreshold;");
        batchShaderSource.ShouldContain("SortKeysRadix(uint total)");
        batchShaderSource.ShouldContain("layout(std430, binding = 14) buffer SortScratchBuffer");
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
