using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Models.Materials;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class GpuTransparencyPhase05ScaffoldTests
{
    [Test]
    public void TransparencyClassification_ResolvesExpectedDomains()
    {
        GpuTransparencyClassification.ResolveDomain(ETransparencyMode.Opaque)
            .ShouldBe(EGpuTransparencyDomain.OpaqueOrOther);

        GpuTransparencyClassification.ResolveDomain(ETransparencyMode.Masked)
            .ShouldBe(EGpuTransparencyDomain.Masked);

        GpuTransparencyClassification.ResolveDomain(ETransparencyMode.AlphaToCoverage)
            .ShouldBe(EGpuTransparencyDomain.Masked);

        GpuTransparencyClassification.ResolveDomain(ETransparencyMode.AlphaBlend)
            .ShouldBe(EGpuTransparencyDomain.TransparentApproximate);

        GpuTransparencyClassification.ResolveDomain(ETransparencyMode.WeightedBlendedOit)
            .ShouldBe(EGpuTransparencyDomain.TransparentApproximate);

        GpuTransparencyClassification.ResolveDomain(ETransparencyMode.PerPixelLinkedList)
            .ShouldBe(EGpuTransparencyDomain.TransparentExact);

        GpuTransparencyClassification.ResolveDomain(ETransparencyMode.DepthPeeling)
            .ShouldBe(EGpuTransparencyDomain.TransparentExact);
    }

    [Test]
    public void Phase05_HostAndShaderContracts_ArePresent()
    {
        string hostSource = ReadWorkspaceFile("XRENGINE/Rendering/Commands/GPURenderPassCollection.IndirectAndMaterials.cs");
        hostSource.ShouldContain("ClassifyTransparencyDomains(scene);");
        hostSource.ShouldContain("Engine.Rendering.Stats.RecordGpuTransparencyDomainCounts(");
        hostSource.ShouldContain("GPUTransparencyBindings.ClassifyDomainCounts");

        string sceneSource = ReadWorkspaceFile("XRENGINE/Rendering/Commands/GPUScene.cs");
        sceneSource.ShouldContain("AllLoadedTransparencyMetadataBuffer");
        sceneSource.ShouldContain("GPUTransparencyMetadata.FromMaterial");

        string shaderSource = ReadWorkspaceFile("Build/CommonAssets/Shaders/Compute/Indirect/GPURenderClassifyTransparencyDomains.comp");
        shaderSource.ShouldContain("layout(std430, binding = 6) buffer TransparencyDomainCounts");
        shaderSource.ShouldContain("maskedVisibleIndices[outIndex] = logicalIdx;");
        shaderSource.ShouldContain("approximateVisibleIndices[outIndex] = logicalIdx;");
        shaderSource.ShouldContain("exactVisibleIndices[outIndex] = logicalIdx;");
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