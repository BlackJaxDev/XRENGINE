using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Assimp;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class ImportedDeferredMaterialTests
{
    private IRuntimeShaderServices? _previousServices;

    [SetUp]
    public void SetUp()
    {
        _previousServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new TestRuntimeShaderServices();
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeShaderServices.Current = _previousServices;
    }

    [Test]
    public void MakeMaterialDefault_PbrInputs_SelectDeferredPbrShaderAndTextureLayout()
    {
        XRTexture2D albedo = new();
        XRTexture2D normal = new();
        XRTexture2D metallic = new();
        XRTexture2D roughness = new();

        XRMaterial material = ModelImporter.MakeMaterialDefault(
            [albedo, normal, metallic, roughness],
            [
                CreateSlot("albedo.png", TextureType.BaseColor),
                CreateSlot("normal.png", TextureType.NormalCamera),
                CreateSlot("metallic.png", TextureType.Metalness),
                CreateSlot("roughness.png", TextureType.Roughness),
            ],
            "PbrMaterial");

        material.RenderPass.ShouldBe((int)EDefaultRenderPass.OpaqueDeferred);
        Path.GetFileName(material.FragmentShaders[0].Source?.FilePath ?? material.FragmentShaders[0].FilePath ?? string.Empty)
            .ShouldBe("TexturedNormalMetallicRoughnessDeferred.fs");
        material.Textures.Count.ShouldBe(4);
        material.Textures[0].ShouldBeSameAs(albedo);
        material.Textures[1].ShouldBeSameAs(normal);
        material.Textures[2].ShouldBeSameAs(metallic);
        material.Textures[3].ShouldBeSameAs(roughness);
        material.Parameter<ShaderFloat>("Metallic")?.Value.ShouldBe(1.0f);
        material.Parameter<ShaderFloat>("Roughness")?.Value.ShouldBe(1.0f);
    }

    [Test]
    public void MakeMaterialDeferred_NormalCameraTexturesCountAsNormalMaps()
    {
        XRTexture2D albedo = new();
        XRTexture2D normal = new();

        XRMaterial material = ModelImporter.MakeMaterialDeferred(
            [albedo, normal],
            [
                CreateSlot("albedo.png", TextureType.BaseColor),
                CreateSlot("normal.png", TextureType.NormalCamera),
            ],
            "NormalCameraMaterial");

        Path.GetFileName(material.FragmentShaders[0].Source?.FilePath ?? material.FragmentShaders[0].FilePath ?? string.Empty)
            .ShouldBe("TexturedNormalDeferred.fs");
        material.Textures.Count.ShouldBe(2);
        material.Textures[0].ShouldBeSameAs(albedo);
        material.Textures[1].ShouldBeSameAs(normal);
    }

    [Test]
    public void MakeMaterialDeferred_TexturedMaterialsSeedBaseColorParameter()
    {
        XRTexture2D albedo = new();

        XRMaterial material = ModelImporter.MakeMaterialDeferred(
            [albedo],
            [CreateSlot("albedo.png", TextureType.Diffuse)],
            "DiffuseMaterial");

        material.Parameter<ShaderVector3>("BaseColor")?.Value.ShouldBe(Vector3.One);
    }

    [Test]
    public void MakeMaterialDeferred_NormalMappedMaterialsSeedBaseColorParameter()
    {
        XRTexture2D albedo = new();
        XRTexture2D normal = new();

        XRMaterial material = ModelImporter.MakeMaterialDeferred(
            [albedo, normal],
            [
                CreateSlot("albedo.png", TextureType.BaseColor),
                CreateSlot("normal.png", TextureType.NormalCamera),
            ],
            "NormalCameraMaterial");

        material.Parameter<ShaderVector3>("BaseColor")?.Value.ShouldBe(Vector3.One);
    }

    [Test]
    public void MakeMaterialDeferred_NormalMappedLegacyMaterialsUseForwardEquivalentRoughnessFallback()
    {
        XRTexture2D albedo = new();
        XRTexture2D normal = new();

        XRMaterial material = ModelImporter.MakeMaterialDeferred(
            [albedo, normal],
            [
                CreateSlot("albedo.png", TextureType.BaseColor),
                CreateSlot("normal.png", TextureType.NormalCamera),
            ],
            "NormalCameraMaterial");

        material.Parameter<ShaderFloat>("Roughness")?.Value.ShouldBe(
            ModelImporter.ConvertLegacyShininessToDeferredRoughness(32.0f),
            0.0001f);
    }

    [Test]
    public void ResolveImportedDeferredRoughness_UsesImportedLegacyShininess()
    {
        Dictionary<string, List<MaterialProperty>> properties = new()
        {
            ["$mat.shininess"] = [new MaterialProperty("$mat.shininess", 128.0f)],
        };

        ModelImporter.ResolveImportedDeferredRoughness(properties, hasRoughnessTexture: false).ShouldBe(
            ModelImporter.ConvertLegacyShininessToDeferredRoughness(128.0f),
            0.0001f);
    }

    [Test]
    public void ResolveImportedHeightMapScale_UsesImportedBumpScaling()
    {
        Dictionary<string, List<MaterialProperty>> properties = new()
        {
            ["$mat.bumpscaling"] = [new MaterialProperty("$mat.bumpscaling", 0.35f)],
        };

        // Assimp bumpscaling (0.35) is multiplied by the engine scale (1.5) to produce
        // visible normals from the Sobel 3x3 height-to-normal reconstruction.
        ModelImporter.ResolveImportedHeightMapScale(properties, isHeightMap: true).ShouldBe(0.35f * 1.5f, 0.0001f);
        ModelImporter.ResolveImportedHeightMapScale(properties, isHeightMap: false).ShouldBe(0.0f);
    }

    [Test]
    public void MakeMaterialDeferred_HeightTextureNamedLikeNormalMap_UsesNormalMapMode()
    {
        XRTexture2D albedo = new();
        XRTexture2D surfaceDetail = new();

        XRMaterial material = ModelImporter.MakeMaterialDeferred(
            [albedo, surfaceDetail],
            [
                CreateSlot("albedo.png", TextureType.BaseColor),
                CreateSlot("body_NormalMap.png", TextureType.Height),
            ],
            "LegacyBumpMaterial");

        material.Parameter<ShaderInt>("NormalMapMode")?.Value.ShouldBe(0);
        material.Parameter<ShaderFloat>("HeightMapScale")?.Value.ShouldBe(0.0f);
        Path.GetFileName(material.FragmentShaders[0].Source?.FilePath ?? material.FragmentShaders[0].FilePath ?? string.Empty)
            .ShouldBe("TexturedNormalDeferred.fs");
    }

    [Test]
    public void MakeMaterialDeferred_HeightTextureWithoutNormalHint_UsesHeightMapMode()
    {
        XRTexture2D albedo = new();
        XRTexture2D surfaceDetail = new();

        XRMaterial material = ModelImporter.MakeMaterialDeferred(
            [albedo, surfaceDetail],
            [
                CreateSlot("albedo.png", TextureType.BaseColor),
                CreateSlot("stone_height.png", TextureType.Height),
            ],
            "HeightMaterial");

        material.Parameter<ShaderInt>("NormalMapMode")?.Value.ShouldBe(1);
        // No Assimp bumpscaling → authored scale defaults to 1.0, multiplied by engine scale (1.5).
        material.Parameter<ShaderFloat>("HeightMapScale")?.Value.ShouldBe(1.5f, 0.0001f);
        Path.GetFileName(material.FragmentShaders[0].Source?.FilePath ?? material.FragmentShaders[0].FilePath ?? string.Empty)
            .ShouldBe("TexturedNormalDeferred.fs");
    }

    [Test]
    public void ResolveTransparencyMode_DiffuseTextureWithCutoutAlphaDefaultsToMasked()
    {
        XRTexture2D albedo = CreateDiffuseTextureWithAlpha(255, 255, 0, 0);

        ModelImporter.ResolveTransparencyMode(
            [albedo],
            [CreateSlot("lion.png", TextureType.Diffuse)])
            .ShouldBe(ETransparencyMode.Masked);
    }

    [Test]
    public void ResolveTransparencyMode_FullyOpaqueDiffuseAlphaRemainsOpaque()
    {
        XRTexture2D albedo = CreateDiffuseTextureWithAlpha(255, 255, 255, 255);

        ModelImporter.ResolveTransparencyMode(
            [albedo],
            [CreateSlot("albedo.png", TextureType.Diffuse)])
            .ShouldBe(ETransparencyMode.Opaque);
    }

    [Test]
    public void MakeMaterialDeferred_DiffuseCutoutAlphaWithoutOpacityMapUsesDeferredAlphaShader()
    {
        XRTexture2D albedo = CreateDiffuseTextureWithAlpha(255, 255, 0, 0);
        XRTexture2D normal = new();

        XRMaterial material = ModelImporter.MakeMaterialDeferred(
            [albedo, normal],
            [
                CreateSlot("lion.png", TextureType.Diffuse),
                CreateSlot("lion_bump.png", TextureType.Height),
            ],
            "LionMaterial");

        Path.GetFileName(material.FragmentShaders[0].Source?.FilePath ?? material.FragmentShaders[0].FilePath ?? string.Empty)
            .ShouldBe("TexturedNormalAlphaDeferred.fs");
        material.Textures.Count.ShouldBe(3);
        material.Textures[0].ShouldBeSameAs(albedo);
        material.Textures[1].ShouldBeSameAs(normal);
        material.Textures[2].ShouldBeSameAs(albedo);
        material.TransparencyMode.ShouldBe(ETransparencyMode.Masked);
        material.Parameter<ShaderFloat>("AlphaCutoff").ShouldNotBeNull();
    }

    [Test]
    public void MakeMaterialDeferred_SpecularTexturesSelectDeferredSpecularShader()
    {
        XRTexture2D albedo = new();
        XRTexture2D specular = new();

        XRMaterial material = ModelImporter.MakeMaterialDeferred(
            [albedo, specular],
            [
                CreateSlot("albedo.png", TextureType.Diffuse),
                CreateSlot("specular.png", TextureType.Specular),
            ],
            "SpecularMaterial");

        Path.GetFileName(material.FragmentShaders[0].Source?.FilePath ?? material.FragmentShaders[0].FilePath ?? string.Empty)
            .ShouldBe("TexturedSpecDeferred.fs");
        material.Textures.Count.ShouldBe(2);
        material.Textures[0].ShouldBeSameAs(albedo);
        material.Textures[1].ShouldBeSameAs(specular);
        material.Parameter<ShaderFloat>("Specular")?.Value.ShouldBe(1.0f);
    }

    [Test]
    public void MakeMaterialDeferred_OpacityMaskTexturesSelectDeferredAlphaMaskShader()
    {
        XRTexture2D albedo = new();
        XRTexture2D alphaMask = new();

        XRMaterial material = ModelImporter.MakeMaterialDeferred(
            [albedo, alphaMask],
            [
                CreateSlot("albedo.png", TextureType.Diffuse),
                CreateSlot("mask.png", TextureType.Opacity),
            ],
            "AlphaMaskMaterial");

        Path.GetFileName(material.FragmentShaders[0].Source?.FilePath ?? material.FragmentShaders[0].FilePath ?? string.Empty)
            .ShouldBe("TexturedAlphaDeferred.fs");
        material.Textures.Count.ShouldBe(2);
        material.Textures[0].ShouldBeSameAs(albedo);
        material.Textures[1].ShouldBeSameAs(alphaMask);
        material.TransparencyMode.ShouldBe(ETransparencyMode.Masked);
        material.Parameter<ShaderFloat>("AlphaCutoff").ShouldNotBeNull();
    }

    [Test]
    public void MakeMaterialDeferred_NormalSpecularOpacityTexturesSelectDeferredLegacyMaskedShader()
    {
        XRTexture2D albedo = new();
        XRTexture2D normal = new();
        XRTexture2D specular = new();
        XRTexture2D alphaMask = new();

        XRMaterial material = ModelImporter.MakeMaterialDeferred(
            [albedo, normal, specular, alphaMask],
            [
                CreateSlot("albedo.png", TextureType.Diffuse),
                CreateSlot("normal.png", TextureType.NormalCamera),
                CreateSlot("specular.png", TextureType.Specular),
                CreateSlot("mask.png", TextureType.Opacity),
            ],
            "LegacyMaskedMaterial");

        Path.GetFileName(material.FragmentShaders[0].Source?.FilePath ?? material.FragmentShaders[0].FilePath ?? string.Empty)
            .ShouldBe("TexturedNormalSpecAlphaDeferred.fs");
        material.Textures.Count.ShouldBe(4);
        material.Textures[0].ShouldBeSameAs(albedo);
        material.Textures[1].ShouldBeSameAs(normal);
        material.Textures[2].ShouldBeSameAs(specular);
        material.Textures[3].ShouldBeSameAs(alphaMask);
        material.TransparencyMode.ShouldBe(ETransparencyMode.Masked);
    }

    [Test]
    public void PrefabSource_CreateMaterial_DelegatesToDeferredImporterFactory()
    {
        string source = ReadWorkspaceFile("XRENGINE/Scene/Prefabs/XRPrefabSource.cs").Replace("\r\n", "\n");

        source.ShouldContain("=> ModelImporter.MakeMaterialDeferred(textureList, textures, name);");
    }

    private static TextureSlot CreateSlot(string filePath, TextureType textureType)
        => new(filePath, textureType, 0, default, 0, 1.0f, default, default, default, 0);

    private static XRTexture2D CreateDiffuseTextureWithAlpha(params byte[] alphaValues)
    {
        XRTexture2D texture = new(2u, 2u, EPixelInternalFormat.Rgba8, EPixelFormat.Rgba, EPixelType.UnsignedByte, allocateData: false)
        {
            AlphaAsTransparency = true,
        };

        byte[] pixels = new byte[alphaValues.Length * 4];
        for (int i = 0; i < alphaValues.Length; i++)
        {
            int pixelIndex = i * 4;
            pixels[pixelIndex] = 255;
            pixels[pixelIndex + 1] = 255;
            pixels[pixelIndex + 2] = 255;
            pixels[pixelIndex + 3] = alphaValues[i];
        }

        texture.Mipmaps[0].Data = new DataSource(pixels);
        return texture;
    }

    private static string ReadWorkspaceFile(string relativePath)
    {
        string fullPath = ResolveWorkspacePath(relativePath);
        File.Exists(fullPath).ShouldBeTrue($"Expected workspace file does not exist: {fullPath}");
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

    private sealed class TestRuntimeShaderServices : IRuntimeShaderServices
    {
        public T? LoadAsset<T>(string filePath) where T : XRAsset, new()
            => new T();

        public T LoadEngineAsset<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath) where T : XRAsset, new()
            => CreateShaderAsset<T>(relativePath);

        public Task<T> LoadEngineAssetAsync<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath) where T : XRAsset, new()
            => Task.FromResult(CreateShaderAsset<T>(relativePath));

        public void LogWarning(string message)
        {
        }

        private static T CreateShaderAsset<T>(string relativePath) where T : XRAsset, new()
        {
            if (typeof(T) == typeof(XRShader))
            {
                TextFile source = TextFile.FromText("void main() {}\n");
                source.FilePath = relativePath;

                XRShader shader = new(EShaderType.Fragment, source)
                {
                    FilePath = relativePath,
                };

                return (T)(XRAsset)shader;
            }

            return new T();
        }
    }
}