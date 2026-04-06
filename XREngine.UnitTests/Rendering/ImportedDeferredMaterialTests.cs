using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Assimp;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
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
        Console.Error.WriteLine($"[SETUP] Before: RuntimeShaderServices.Current type={RuntimeShaderServices.Current?.GetType().Name ?? "null"}");
        RuntimeShaderServices.Current = new TestRuntimeShaderServices();
        Console.Error.WriteLine($"[SETUP] After: RuntimeShaderServices.Current type={RuntimeShaderServices.Current?.GetType().Name ?? "null"}");
    }

    [TearDown]
    public void TearDown()
    {
        Console.Error.WriteLine($"[TEARDOWN] Before: RuntimeShaderServices.Current type={RuntimeShaderServices.Current?.GetType().Name ?? "null"}, restoring to {_previousServices?.GetType().Name ?? "null"}");
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

        var fs0 = material.FragmentShaders[0];
        Console.Error.WriteLine($"[DIAG] FragShader count={material.FragmentShaders.Count}");
        Console.Error.WriteLine($"[DIAG] fs0.Type={fs0.Type} fs0.Name='{fs0.Name}'");
        Console.Error.WriteLine($"[DIAG] fs0.FilePath='{fs0.FilePath}'");
        Console.Error.WriteLine($"[DIAG] fs0.Source is null={fs0.Source is null}");
        Console.Error.WriteLine($"[DIAG] fs0.Source?.FilePath='{fs0.Source?.FilePath}'");
        Console.Error.WriteLine($"[DIAG] fs0.Source?.Text?.Length={fs0.Source?.Text?.Length}");
        Console.Error.WriteLine($"[DIAG] fs0.SourceAsset type={fs0.SourceAsset?.GetType().Name} same as fs0={ReferenceEquals(fs0.SourceAsset, fs0)} same as material={ReferenceEquals(fs0.SourceAsset, material)}");
        Console.Error.WriteLine($"[DIAG] material.FilePath='{material.FilePath}'");
        Console.Error.WriteLine($"[DIAG] material.EmbeddedAssets.Count={material.EmbeddedAssets.Count}");
        Console.Error.WriteLine($"[DIAG] RuntimeShaderServices.Current type={RuntimeShaderServices.Current?.GetType().Name}");

        Path.GetFileName(material.FragmentShaders[0].Source?.FilePath ?? material.FragmentShaders[0].FilePath ?? string.Empty)
            .ShouldBe("TexturedNormalDeferred.fs");
        material.Textures.Count.ShouldBe(2);
        material.Textures[0].ShouldBeSameAs(albedo);
        material.Textures[1].ShouldBeSameAs(normal);
    }

    [Test]
    public void MakeMaterialDeferred_FilenameHintsRecoverPbrSlotsWhenAssimpTextureTypesAreAmbiguous()
    {
        XRTexture2D albedo = new();
        XRTexture2D normal = new();
        XRTexture2D metallic = new();
        XRTexture2D roughness = new();

        XRMaterial material = ModelImporter.MakeMaterialDeferred(
            [albedo, normal, metallic, roughness],
            [
                CreateSlot("wall_BaseColor.png", TextureType.Reflection),
                CreateSlot("wall_Normal.png", TextureType.Reflection),
                CreateSlot("wall_Metalness.png", TextureType.Reflection),
                CreateSlot("wall_Roughness.png", TextureType.Reflection),
            ],
            "RecoveredPbrMaterial");

        Path.GetFileName(material.FragmentShaders[0].Source?.FilePath ?? material.FragmentShaders[0].FilePath ?? string.Empty)
            .ShouldBe("TexturedNormalMetallicRoughnessDeferred.fs");
        material.Textures.Count.ShouldBe(4);
        material.Textures[0].ShouldBeSameAs(albedo);
        material.Textures[1].ShouldBeSameAs(normal);
        material.Textures[2].ShouldBeSameAs(metallic);
        material.Textures[3].ShouldBeSameAs(roughness);
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

    [Test]
    public void LoadTextures_MissingRootedTexturePathsFallBackToModelRelativeTexturesDirectory()
    {
        string tempDirectory = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"rooted-texture-{Guid.NewGuid():N}");
        string texturesDirectory = Path.Combine(tempDirectory, "textures");
        Directory.CreateDirectory(texturesDirectory);

        string modelPath = Path.Combine(tempDirectory, "scene.fbx");
        File.WriteAllText(modelPath, string.Empty);

        string localTexturePath = Path.Combine(texturesDirectory, "albedo.png");
        File.WriteAllBytes(localTexturePath, []);

        try
        {
            using var importer = new ModelImporter(modelPath, onCompleted: null, materialFactory: null)
            {
                MakeTextureAction = path => new XRTexture2D
                {
                    FilePath = path,
                    Name = Path.GetFileNameWithoutExtension(path),
                },
            };

            XRTexture[] textureList = importer.LoadTextures(
                modelPath,
                [CreateSlot(@"C:\Authoring\main_sponza\textures\albedo.png", TextureType.BaseColor)]);

            textureList.Length.ShouldBe(1);
            textureList[0].ShouldNotBeNull();
            textureList[0].ShouldBeOfType<XRTexture2D>();
            ((XRTexture2D)textureList[0]!).FilePath.ShouldBe(localTexturePath);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static TextureSlot CreateSlot(string filePath, TextureType textureType)
        => new(filePath, textureType, 0, default, 0, 1.0f, default, default, default, 0);

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