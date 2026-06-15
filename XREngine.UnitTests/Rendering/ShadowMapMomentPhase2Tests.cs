using NUnit.Framework;
using Shouldly;
using System.IO;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
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
    public void PointLight_Evsm4ShadowMaterialUsesMomentCubemapAndRadialWriter()
    {
        PointLightComponent light = new()
        {
            ShadowMapEncoding = EShadowMapEncoding.ExponentialVariance4,
        };

        XRMaterial material = light.GetShadowMapMaterial(64u, 64u);
        XRTextureCube shadowMap = FindShadowMapTexture<XRTextureCube>(material);

        shadowMap.SizedInternalFormat.ShouldBe(ESizedInternalFormat.Rgba16f);
        shadowMap.MinFilter.ShouldBe(ETexMinFilter.Linear);
        shadowMap.MagFilter.ShouldBe(ETexMagFilter.Linear);
        string? shaderSource = material.FragmentShaders[0].Source.Text;
        shaderSource.ShouldNotBeNull();
        shaderSource!.ShouldContain("XRENGINE_EncodeShadowMoments");
        shaderSource.ShouldContain("length(FragPos - LightPos)");
    }

    [Test]
    public void DirectionalLight_Evsm2ShadowMaterialKeepsDepthAndAddsMomentColorTarget()
    {
        DirectionalLightComponent light = new()
        {
            ShadowMapEncoding = EShadowMapEncoding.ExponentialVariance2,
        };

        XRMaterial material = light.GetShadowMapMaterial(64u, 64u);
        XRTexture2D rasterDepth = FindAttachmentTexture<XRTexture2D>(material, EFrameBufferAttachment.DepthAttachment);
        XRTexture2D shadowMap = FindShadowMapTexture<XRTexture2D>(material);

        rasterDepth.SizedInternalFormat.ShouldBe(ESizedInternalFormat.DepthComponent24);
        rasterDepth.SamplerName.ShouldBe("ShadowRasterDepth");
        shadowMap.SizedInternalFormat.ShouldBe(ESizedInternalFormat.Rg16f);
        shadowMap.FrameBufferAttachment.ShouldBe(EFrameBufferAttachment.ColorAttachment0);
        string? shaderSource = material.FragmentShaders[0].Source.Text;
        shaderSource.ShouldNotBeNull();
        shaderSource!.ShouldContain("ShadowDepthSourceMode");
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
    public void SpotLight_MomentMipmapsUseMipmapFilterAndAutoGeneration()
    {
        SpotLightComponent light = new()
        {
            ShadowMapEncoding = EShadowMapEncoding.Variance2,
            ShadowMomentUseMipmaps = true,
        };

        XRTexture2D shadowMap = FindShadowMapTexture<XRTexture2D>(light.GetShadowMapMaterial(64u, 64u));

        shadowMap.MinFilter.ShouldBe(ETexMinFilter.LinearMipmapLinear);
        shadowMap.AutoGenerateMipmaps.ShouldBeTrue();
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
        source.ShouldContain("vec4 XRENGINE_FetchShadowMoments2DArray");
        source.ShouldContain("float XRENGINE_SampleShadowMoment2DArray");
        source.ShouldContain("float XRENGINE_SampleShadowMomentCube");
        source.ShouldContain("float XRENGINE_ResolveShadowMomentMipLevel");
        source.ShouldContain("textureLod(shadowMap, uv");
        source.ShouldContain("textureLod(shadowMap, sampleCoord");
        source.ShouldContain("textureLod(shadowMap, sampleDirection");
    }

    [Test]
    public void SpotReceiverShaders_BranchToMomentSamplingForNonDepthEncoding()
    {
        string deferredSpot = LoadShaderSource("Scene3D/DeferredLightingSpot.fs");
        string forwardLighting = LoadShaderSource("Snippets/ForwardLighting.glsl");

        deferredSpot.ShouldContain("ShadowMapEncoding != XRENGINE_SHADOW_ENCODING_DEPTH");
        deferredSpot.ShouldContain("XRENGINE_SampleShadowMoment2D(");
        deferredSpot.ShouldContain("ShadowMomentDepthParams.w != 0.0f");
        deferredSpot.ShouldContain("ShadowMomentFilterParams.x");
        deferredSpot.ShouldContain("* contact");
        forwardLighting.ShouldContain("ivec4 shadowI2 = shadowData.Packed2");
        forwardLighting.ShouldContain("vec4 shadowF4 = shadowData.Params4");
        forwardLighting.ShouldContain("shadowI2.y != 0");
        forwardLighting.ShouldContain("XRENGINE_SampleShadowMoment2D(");
    }

    [Test]
    public void PointAndDirectionalReceiverShaders_BranchToMomentSamplingForNonDepthEncoding()
    {
        string deferredPoint = LoadShaderSource("Scene3D/DeferredLightingPoint.fs");
        string deferredDirectional = LoadShaderSource("Scene3D/DeferredLightingDir.fs");
        string forwardLighting = LoadShaderSource("Snippets/ForwardLighting.glsl");
        string volumetricFog = LoadShaderSource("Scene3D/VolumetricFog/VolumetricFogScatter.fs");

        deferredPoint.ShouldContain("ShadowMapEncoding != XRENGINE_SHADOW_ENCODING_DEPTH");
        deferredPoint.ShouldContain("XRENGINE_SampleShadowMomentCube(");
        deferredDirectional.ShouldContain("XRENGINE_SampleShadowMoment2D(");
        deferredDirectional.ShouldContain("XRENGINE_SampleShadowMoment2DArray(");
        forwardLighting.ShouldContain("DirectionalShadowMapEncoding");
        forwardLighting.ShouldContain("XRENGINE_SampleShadowMoment2DArray(");
        forwardLighting.ShouldContain("XRENGINE_SampleShadowMomentCube(");
        volumetricFog.ShouldContain("ShadowMapEncoding != XRENGINE_SHADOW_ENCODING_DEPTH");
        volumetricFog.ShouldContain("XRENGINE_SampleShadowMoment2D(");
    }

    [Test]
    public void DirectionalCascadeMomentPath_UsesColorArrayDepthAttachmentAndScalarBlend()
    {
        string cascadeSource = LoadRepoSource(Path.Combine("XREngine.Runtime.Rendering", "Scene", "Components", "Lights", "Types", "DirectionalLightComponent.CascadeShadows.cs"));
        string forwardLighting = LoadShaderSource("Snippets/ForwardLighting.glsl");
        string deferredDirectional = LoadShaderSource("Scene3D/DeferredLightingDir.fs");

        cascadeSource.ShouldContain("private XRTexture2DArray? _cascadeRasterDepthTexture;");
        cascadeSource.ShouldContain("momentEncoding ? EFrameBufferAttachment.ColorAttachment0 : EFrameBufferAttachment.DepthAttachment");
        cascadeSource.ShouldContain("(_cascadeRasterDepthTexture!, EFrameBufferAttachment.DepthAttachment");
        cascadeSource.ShouldContain("GenerateCascadeMomentShadowMipmapsIfNeeded();");

        forwardLighting.ShouldContain("XRENGINE_SampleShadowMoment2DArray(");
        forwardLighting.ShouldContain("float shadow0 = XRENGINE_ReadCascadeShadowMapDir");
        forwardLighting.ShouldContain("float shadow1 = XRENGINE_ReadCascadeShadowMapDir");
        forwardLighting.ShouldContain("return mix(shadow0, shadow1, t);");
        forwardLighting.ShouldNotContain("mix(moment");

        deferredDirectional.ShouldContain("XRENGINE_SampleShadowMoment2DArray(");
        deferredDirectional.ShouldContain("float s0 = ReadCascadeShadowMap");
        deferredDirectional.ShouldContain("float s1 = ReadCascadeShadowMap");
        deferredDirectional.ShouldContain("shadow = mix(s0, s1, t);");
        deferredDirectional.ShouldNotContain("mix(moment");
    }

    [Test]
    public void DirectionalMomentAtlas_UsesResolvedEncodingAndKeepsCascades()
    {
        string directionalSource = LoadRepoSource(Path.Combine("XREngine.Runtime.Rendering", "Scene", "Components", "Lights", "Types", "DirectionalLightComponent.cs"));
        string shadowCollectionSource = LoadRepoSource(Path.Combine("XREngine.Runtime.Rendering", "Rendering", "Lights3DCollection.Shadows.cs"));
        string deferredBindings = LoadRepoSource(Path.Combine("XREngine.Runtime.Rendering", "Rendering", "Pipelines", "Commands", "Features", "VPRC_LightCombinePass.cs"));

        directionalSource.ShouldContain("EShadowMapEncoding.ExponentialVariance4");
        shadowCollectionSource.ShouldContain("EShadowMapEncoding encoding = shadowFormat.Encoding;");
        shadowCollectionSource.ShouldContain("encoding: encoding");
        shadowCollectionSource.ShouldContain("renderCascades = needsCascadeAtlas;");

        deferredBindings.ShouldContain("useDirectionalShadowAtlas = directionalLight.UsesDirectionalShadowAtlasForCurrentEncoding && directionalLight.CastsShadows;");
        deferredBindings.ShouldNotContain("!directionalMomentSingleMap &&");
        deferredBindings.ShouldContain("directionalHasShadowMap |= !useDirectionalShadowAtlas && hasShadowMap;");
        deferredBindings.ShouldContain("materialProgram.Sampler(\"ShadowMap\", !useDirectionalShadowAtlas && selectedShadowMap is XRTexture2D shadow2D ? shadow2D : DummyShadowMap, 4);");
    }

    [Test]
    public void ShadowAtlasMomentPath_UsesEncodingSpecificPagesAndMomentArraySampling()
    {
        string atlasManager = LoadRepoSource(Path.Combine("XREngine.Runtime.Rendering", "Rendering", "Shadows", "ShadowAtlasManager.cs"));
        string forwardBindings = LoadRepoSource(Path.Combine("XREngine.Runtime.Rendering", "Rendering", "Lights3DCollection.ForwardLighting.cs"));
        string deferredBindings = LoadRepoSource(Path.Combine("XREngine.Runtime.Rendering", "Rendering", "Pipelines", "Commands", "Features", "VPRC_LightCombinePass.cs"));
        string forwardLighting = LoadShaderSource("Snippets/ForwardLighting.glsl");
        string deferredPoint = LoadShaderSource("Scene3D/DeferredLightingPoint.fs");
        string deferredSpot = LoadShaderSource("Scene3D/DeferredLightingSpot.fs");

        atlasManager.ShouldContain("GetEncodingState(allocation.AtlasKind, request.Encoding)");
        atlasManager.ShouldContain("GetEncodingState(group.AtlasKind, group.Encoding)");
        atlasManager.ShouldContain("ShadowAtlas_{AtlasKind}_{Encoding}");
        atlasManager.ShouldNotContain("request.Encoding != EShadowMapEncoding.Depth");
        atlasManager.ShouldNotContain("group.Encoding != EShadowMapEncoding.Depth");

        forwardBindings.ShouldContain("TryGetPageTexture(EShadowAtlasKind.Point, pointAtlasEncoding");
        forwardBindings.ShouldContain("TryGetPageTexture(EShadowAtlasKind.Spot, spotAtlasEncoding");
        forwardBindings.ShouldContain("TryGetPageTexture(EShadowAtlasKind.Directional, directionalAtlasEncoding");
        deferredBindings.ShouldContain("TryGetPageTexture(allocation.AtlasKind, shadowFormat.Encoding");
        deferredBindings.ShouldContain("TryGetPageTexture(EShadowAtlasKind.Directional, shadowFormat.Encoding");

        forwardLighting.ShouldContain("PointLightShadowAtlas");
        forwardLighting.ShouldContain("SpotLightShadowAtlas");
        forwardLighting.ShouldContain("DirectionalShadowAtlas");
        deferredPoint.ShouldContain("XRENGINE_SampleShadowMoment2DArray(");
        deferredSpot.ShouldContain("XRENGINE_SampleShadowMoment2DArray(");
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

    private static TTexture FindAttachmentTexture<TTexture>(XRMaterial material, EFrameBufferAttachment attachment)
        where TTexture : XRTexture
    {
        foreach (XRTexture? texture in material.Textures)
            if (texture is TTexture typed && texture.FrameBufferAttachment == attachment)
                return typed;

        Assert.Fail($"{attachment} texture of type {typeof(TTexture).Name} was not found.");
        return null!;
    }

    private static string LoadRepoSource(string relativePath)
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            string candidate = Path.Combine(dir, relativePath);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);

            dir = Path.GetDirectoryName(dir) ?? dir;
        }

        Assert.Inconclusive($"Repository source file not found: {relativePath}");
        return string.Empty;
    }
}
