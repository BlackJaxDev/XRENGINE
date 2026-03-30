using System.Threading.Tasks;
using System;
using System.IO;
using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class MaterialRenderPassContractTests
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
    public void CreateLitTextureMaterial_DeferredHelper_UsesOpaqueDeferredPass()
    {
        XRMaterial material = XRMaterial.CreateLitTextureMaterial(deferred: true);

        material.RenderPass.ShouldBe((int)EDefaultRenderPass.OpaqueDeferred);
    }

    [Test]
    public void CreateLitTextureMaterial_ForwardHelper_UsesOpaqueForwardPass()
    {
        XRMaterial material = XRMaterial.CreateLitTextureMaterial(deferred: false);

        material.RenderPass.ShouldBe((int)EDefaultRenderPass.OpaqueForward);
    }

    [Test]
    public void CreateLitTextureMaterial_TextureOverload_DeferredHelper_UsesOpaqueDeferredPass()
    {
        XRMaterial material = XRMaterial.CreateLitTextureMaterial(new XRTexture2D(), deferred: true);

        material.RenderPass.ShouldBe((int)EDefaultRenderPass.OpaqueDeferred);
    }

    [Test]
    public void PrefabSource_MaterialFactory_DelegatesToDeferredImporterFactory()
    {
        string source = ReadWorkspaceFile("XRENGINE/Scene/Prefabs/XRPrefabSource.cs").Replace("\r\n", "\n");

        source.ShouldContain("=> ModelImporter.MakeMaterialDeferred(textureList, textures, name);");
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
            => CreateShaderAsset<T>();

        public Task<T> LoadEngineAssetAsync<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath) where T : XRAsset, new()
            => Task.FromResult(CreateShaderAsset<T>());

        public void LogWarning(string message)
        {
        }

        private static T CreateShaderAsset<T>() where T : XRAsset, new()
        {
            if (typeof(T) == typeof(XRShader))
            {
                XRShader shader = new(EShaderType.Fragment, new TextFile("void main() {}"));
                return (T)(XRAsset)shader;
            }

            return new T();
        }
    }
}