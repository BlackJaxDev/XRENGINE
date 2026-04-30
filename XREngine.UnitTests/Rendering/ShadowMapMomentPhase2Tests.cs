using NUnit.Framework;
using Shouldly;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Shadows;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class ShadowMapMomentPhase2Tests : GpuTestBase
{
    [Test]
    public void SpotLight_Evsm4ShadowMaterialUsesMomentTextureAndWriter()
    {
        SpotLightComponent light = new()
        {
            ShadowMapEncoding = EShadowMapEncoding.ExponentialVariance4,
        };

        XRMaterial material = light.GetShadowMapMaterial(64u, 64u);
        XRTexture2D shadowMap = FindShadowMapTexture<XRTexture2D>(material);

        shadowMap.SizedInternalFormat.ShouldBe(ESizedInternalFormat.Rgba16f);
        shadowMap.MinFilter.ShouldBe(ETexMinFilter.Linear);
        shadowMap.MagFilter.ShouldBe(ETexMagFilter.Linear);
        material.FragmentShaders.Count.ShouldBe(1);
        string? shaderSource = material.FragmentShaders[0].Source.Text;
        shaderSource.ShouldNotBeNull();
        shaderSource!.ShouldContain("EncodeEvsm4Moments");
    }

    [Test]
    public void SpotLight_VarianceShadowMaterialUsesRgMomentTexture()
    {
        SpotLightComponent light = new()
        {
            ShadowMapEncoding = EShadowMapEncoding.Variance2,
        };

        XRTexture2D shadowMap = FindShadowMapTexture<XRTexture2D>(light.GetShadowMapMaterial(64u, 64u));

        shadowMap.SizedInternalFormat.ShouldBe(ESizedInternalFormat.Rg16f);
        shadowMap.Mipmaps[0].PixelFormat.ShouldBe(EPixelFormat.Rg);
        shadowMap.Mipmaps[0].PixelType.ShouldBe(EPixelType.HalfFloat);
    }

    [Test]
    public void SpotLight_DepthShadowMaterialPreservesLegacyDepthWriter()
    {
        SpotLightComponent light = new();

        XRMaterial material = light.GetShadowMapMaterial(64u, 64u);
        XRTexture2D shadowMap = FindShadowMapTexture<XRTexture2D>(material);

        shadowMap.SizedInternalFormat.ShouldBe(ESizedInternalFormat.R16f);
        shadowMap.MinFilter.ShouldBe(ETexMinFilter.Nearest);
        shadowMap.MagFilter.ShouldBe(ETexMagFilter.Nearest);
        string? shaderSource = material.FragmentShaders[0].Source.Text;
        shaderSource.ShouldNotBeNull();
        shaderSource!.ShouldContain("Depth = gl_FragCoord.z;");
    }

    [Test]
    public void ShadowMomentSnippet_DeclaresWriterAndSamplerHelpers()
    {
        string source = LoadShaderSource("Snippets/ShadowMomentEncoding.glsl");

        source.ShouldContain("vec2 XRENGINE_EncodeVsmMoments");
        source.ShouldContain("vec2 XRENGINE_EncodeEvsm2Moments");
        source.ShouldContain("vec4 XRENGINE_EncodeEvsm4Moments");
        source.ShouldContain("void XRENGINE_WriteShadowCasterDepth");
        source.ShouldContain("float XRENGINE_ChebyshevUpperBound");
        source.ShouldContain("float XRENGINE_SampleShadowMoment2D");
    }

    [Test]
    public void SpotReceiverShaders_BranchToMomentSamplingForNonDepthEncoding()
    {
        string deferredSpot = LoadShaderSource("Scene3D/DeferredLightingSpot.fs");
        string forwardLighting = LoadShaderSource("Snippets/ForwardLighting.glsl");

        deferredSpot.ShouldContain("ShadowMapEncoding != XRENGINE_SHADOW_ENCODING_DEPTH");
        deferredSpot.ShouldContain("XRENGINE_SampleShadowMoment2D(");
        deferredSpot.ShouldContain("* contact");
        forwardLighting.ShouldContain("uniform ivec4 SpotLightShadowPacked2");
        forwardLighting.ShouldContain("uniform vec4 SpotLightShadowParams4");
        forwardLighting.ShouldContain("XRENGINE_SampleShadowMoment2D(");
    }

    [Test]
    public void SpotMomentFormatSelection_UsesEvsmClearSentinel()
    {
        SpotLightComponent light = new()
        {
            ShadowMapEncoding = EShadowMapEncoding.ExponentialVariance2,
            ShadowMomentPositiveExponent = 4.0f,
        };

        ShadowMapFormatSelection selection = light.ResolveShadowMapFormat(preferredStorageFormat: light.ShadowMapStorageFormat);

        selection.Encoding.ShouldBe(EShadowMapEncoding.ExponentialVariance2);
        selection.Format.StorageFormat.ShouldBe(EShadowMapStorageFormat.RG16Float);
        selection.ClearSentinel.ChannelCount.ShouldBe(2);
        selection.ClearSentinel[0].ShouldBe(MathF.Exp(4.0f), 0.001f);
        selection.ClearSentinel[1].ShouldBe(MathF.Exp(8.0f), 0.01f);
    }

    private static TTexture FindShadowMapTexture<TTexture>(XRMaterial material)
        where TTexture : XRTexture
    {
        foreach (XRTexture? texture in material.Textures)
            if (texture is TTexture typed && texture.SamplerName == "ShadowMap")
                return typed;

        Assert.Fail($"ShadowMap texture of type {typeof(TTexture).Name} was not found.");
        return null!;
    }
}
