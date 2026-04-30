using NUnit.Framework;
using Shouldly;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data.Rendering;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class LightShadowMapStorageFormatTests
{
    [Test]
    public void SpotLight_UsesSelectedColorShadowStorageFormat()
    {
        SpotLightComponent light = new()
        {
            ShadowMapStorageFormat = EShadowMapStorageFormat.R16UNorm,
        };

        XRTexture2D shadowMap = FindShadowMapTexture<XRTexture2D>(light.GetShadowMapMaterial(64u, 64u));

        shadowMap.SizedInternalFormat.ShouldBe(ESizedInternalFormat.R16);
        shadowMap.Mipmaps[0].InternalFormat.ShouldBe(EPixelInternalFormat.R16);
        shadowMap.Mipmaps[0].PixelFormat.ShouldBe(EPixelFormat.Red);
        shadowMap.Mipmaps[0].PixelType.ShouldBe(EPixelType.UnsignedShort);
    }

    [Test]
    public void PointLight_UsesSelectedColorShadowStorageFormat()
    {
        PointLightComponent light = new()
        {
            ShadowMapStorageFormat = EShadowMapStorageFormat.R32Float,
        };

        XRTextureCube shadowMap = FindShadowMapTexture<XRTextureCube>(light.GetShadowMapMaterial(64u, 64u));

        shadowMap.SizedInternalFormat.ShouldBe(ESizedInternalFormat.R32f);
        shadowMap.Mipmaps[0].Sides[0].InternalFormat.ShouldBe(EPixelInternalFormat.R32f);
        shadowMap.Mipmaps[0].Sides[0].PixelFormat.ShouldBe(EPixelFormat.Red);
        shadowMap.Mipmaps[0].Sides[0].PixelType.ShouldBe(EPixelType.Float);
    }

    [Test]
    public void DirectionalLight_UsesSelectedDepthShadowStorageFormat()
    {
        DirectionalLightComponent light = new();

        light.ShadowMapStorageFormat.ShouldBe(EShadowMapStorageFormat.Depth24);
        light.ShadowMapStorageFormat = EShadowMapStorageFormat.Depth32Float;

        XRTexture2D shadowMap = FindShadowMapTexture<XRTexture2D>(light.GetShadowMapMaterial(64u, 64u));

        shadowMap.SizedInternalFormat.ShouldBe(ESizedInternalFormat.DepthComponent32f);
        shadowMap.Mipmaps[0].InternalFormat.ShouldBe(EPixelInternalFormat.DepthComponent32f);
        shadowMap.Mipmaps[0].PixelFormat.ShouldBe(EPixelFormat.DepthComponent);
        shadowMap.Mipmaps[0].PixelType.ShouldBe(EPixelType.Float);
        light.CascadedShadowMapTexture.ShouldNotBeNull();
        light.CascadedShadowMapTexture!.SizedInternalFormat.ShouldBe(ESizedInternalFormat.DepthComponent32f);
    }

    [Test]
    public void Lights_RejectStorageFormatsFromTheWrongShadowPath()
    {
        SpotLightComponent spotLight = new()
        {
            ShadowMapStorageFormat = EShadowMapStorageFormat.Depth24,
        };
        spotLight.ShadowMapStorageFormat.ShouldBe(EShadowMapStorageFormat.R16Float);

        DirectionalLightComponent directionalLight = new()
        {
            ShadowMapStorageFormat = EShadowMapStorageFormat.RG16Float,
        };
        directionalLight.ShadowMapStorageFormat.ShouldBe(EShadowMapStorageFormat.Depth24);
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
