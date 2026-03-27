using NUnit.Framework;
using Shouldly;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class ForwardAmbientOcclusionShaderTests : GpuTestBase
{
    private static readonly string[] LitForwardShaderPaths =
    [
        "Common/LitColoredForward.fs",
        "Common/LitColoredForwardWeightedOit.fs",
        "Common/LitTexturedForward.fs",
        "Common/LitTexturedForwardWeightedOit.fs",
        "Common/LitTexturedAlphaForward.fs",
        "Common/LitTexturedAlphaForwardWeightedOit.fs",
        "Common/LitTexturedNormalForward.fs",
        "Common/LitTexturedNormalForwardWeightedOit.fs",
        "Common/LitTexturedNormalAlphaForward.fs",
        "Common/LitTexturedNormalAlphaForwardWeightedOit.fs",
        "Common/LitTexturedNormalSpecForward.fs",
        "Common/LitTexturedNormalSpecForwardWeightedOit.fs",
        "Common/LitTexturedNormalSpecAlphaForward.fs",
        "Common/LitTexturedNormalSpecAlphaForwardWeightedOit.fs",
        "Common/LitTexturedSilhouettePOMForward.fs",
        "Common/LitTexturedSilhouettePOMForwardWeightedOit.fs",
        "Common/LitTexturedSpecForward.fs",
        "Common/LitTexturedSpecForwardWeightedOit.fs",
        "Common/LitTexturedSpecAlphaForward.fs",
        "Common/LitTexturedSpecAlphaForwardWeightedOit.fs",
    ];

    [Test]
    public void AmbientOcclusionSamplingSnippet_DeclaresSharedForwardAoContract()
    {
        string source = LoadShaderSource("Snippets/AmbientOcclusionSampling.glsl");

        source.ShouldContain("uniform sampler2D AmbientOcclusionTexture;");
        source.ShouldContain("uniform sampler2DArray AmbientOcclusionTextureArray;");
        source.ShouldContain("uniform bool AmbientOcclusionArrayEnabled;");
        source.ShouldContain("XRENGINE_GetForwardViewIndex()");
        source.ShouldContain("uniform bool AmbientOcclusionEnabled;");
        source.ShouldContain("float XRENGINE_SampleAmbientOcclusion()");
    }

    [Test]
    public void LitForwardShaders_UseSharedAmbientOcclusionSnippet()
    {
        foreach (string path in LitForwardShaderPaths)
        {
            string source = LoadShaderSource(path);

            source.ShouldContain("#pragma snippet \"AmbientOcclusionSampling\"");
            source.ShouldContain("XRENGINE_SampleAmbientOcclusion()");
            source.ShouldNotContain("float AmbientOcclusion = 1.0;");
            source.ShouldNotContain("MatSpecularIntensity, 1.0)");
        }
    }
}