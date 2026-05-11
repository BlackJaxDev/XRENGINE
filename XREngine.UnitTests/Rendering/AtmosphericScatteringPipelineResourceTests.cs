using System;
using System.IO;
using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AtmosphericScatteringPipelineResourceTests
{
    [TestCase("DefaultRenderPipeline")]
    [TestCase("DefaultRenderPipeline2")]
    public void Pipeline_DefinesAerialPerspectiveResourcesAndIdentityPredicates(string pipelineName)
    {
        string constants = LoadPipelineFile($"{pipelineName}.cs");
        string textures = LoadPipelineFile($"{pipelineName}.Textures.cs");
        string fbos = LoadPipelineFile($"{pipelineName}.FBOs.cs");

        constants.ShouldContain("AtmosphereColorTextureName");
        constants.ShouldContain("AtmosphereHalfDepthTextureName");
        constants.ShouldContain("AtmosphereHalfScatterTextureName");
        constants.ShouldContain("AtmosphereHalfTemporalTextureName");
        constants.ShouldContain("AtmosphereHalfHistoryTextureName");
        constants.ShouldContain("AtmosphereHalfDepthQuadFBOName");
        constants.ShouldContain("AtmosphereHalfScatterQuadFBOName");
        constants.ShouldContain("AtmosphereReprojectQuadFBOName");
        constants.ShouldContain("AtmosphereHistoryFBOName");
        constants.ShouldContain("AtmosphereUpscaleQuadFBOName");
        constants.ShouldContain("atmosphericScattering");

        textures.ShouldContain("EFrameBufferTextureFormat.Rgba16f");
        textures.ShouldContain("EFrameBufferTextureFormat.R32f");
        textures.ShouldContain("SamplerName = AtmosphereColorTextureName");
        textures.ShouldContain("SamplerName = AtmosphereHalfDepthTextureName");

        fbos.ShouldContain("CreateAtmosphereHalfDepthQuadFBO");
        fbos.ShouldContain("CreateAtmosphereHalfScatterQuadFBO");
        fbos.ShouldContain("CreateAtmosphereReprojectQuadFBO");
        fbos.ShouldContain("CreateAtmosphereHistoryFBO");
        fbos.ShouldContain("CreateAtmosphereUpscaleQuadFBO");
        fbos.ShouldContain("GetTexture<XRTexture>(AtmosphereColorTextureName)!, // binding 5");
        fbos.ShouldContain("GetTexture<XRTexture>(VolumetricFogColorTextureName)!, // binding 6");
    }

    [TestCase("DefaultRenderPipeline")]
    [TestCase("DefaultRenderPipeline2")]
    public void Pipeline_CommandChainRunsAtmosphereBeforeVolumetricFog(string pipelineName)
    {
        string commandChain = LoadPipelineFile($"{pipelineName}.CommandChain.cs");

        int appendAtmosphere = commandChain.IndexOf("AppendAtmosphericScattering(c);", StringComparison.Ordinal);
        int appendVolumetric = commandChain.IndexOf("AppendVolumetricFog(c);", StringComparison.Ordinal);

        appendAtmosphere.ShouldBeGreaterThanOrEqualTo(0);
        appendVolumetric.ShouldBeGreaterThan(appendAtmosphere);
        commandChain.ShouldContain("VPRC_AtmosphereHistoryPass");
        commandChain.ShouldContain("AtmosphereHalfDepthDownsample.fs");
        commandChain.ShouldContain("AtmosphereAerialPerspective.fs");
        commandChain.ShouldContain("AtmosphereReproject.fs");
        commandChain.ShouldContain("AtmosphereUpscale.fs");
    }

    [TestCase("DefaultRenderPipeline")]
    [TestCase("DefaultRenderPipeline2")]
    public void Pipeline_RecreatePredicatesCheckAttachmentIdentity(string pipelineName)
    {
        string source = LoadPipelineFile($"{pipelineName}.cs");

        source.ShouldContain("NeedsRecreateAtmosphereHalfDepthFbo");
        source.ShouldContain("NeedsRecreateAtmosphereHalfScatterFbo");
        source.ShouldContain("NeedsRecreateAtmosphereReprojectFbo");
        source.ShouldContain("NeedsRecreateAtmosphereHistoryFbo");
        source.ShouldContain("NeedsRecreateAtmosphereUpscaleFbo");
        source.ShouldContain("ReferenceEquals(targets[0].Target, GetTexture<XRTexture>(AtmosphereColorTextureName))");
        source.ShouldContain("ReferenceEquals(textures[0], GetTexture<XRTexture>(AtmosphereHalfDepthTextureName))");
    }

    private static string LoadPipelineFile(string fileName)
    {
        string fullPath = Path.Combine(ResolveRepoRoot(), "XREngine.Runtime.Rendering", "Rendering", "Pipelines", "Types", fileName);
        File.Exists(fullPath).ShouldBeTrue($"Pipeline file not found: {fullPath}");
        return File.ReadAllText(fullPath);
    }

    private static string ResolveRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "XRENGINE.slnx");
            if (File.Exists(candidate))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repo root from test base directory.");
    }
}
