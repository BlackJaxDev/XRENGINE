using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class ShaderRuntimeServicesTests
{
    private IRuntimeShaderServices? _previousServices;

    [SetUp]
    public void SetUp()
    {
        _previousServices = RuntimeShaderServices.Current;
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeShaderServices.Current = _previousServices;
    }

    [Test]
    public void Reload_UsesRuntimeShaderServicesAssetLoader()
    {
        XRShader loaded = new(EShaderType.Compute, new TextFile("void main() {}"))
        {
            GenerateAsync = true,
        };
        TestRuntimeShaderServices services = new()
        {
            LoadedAsset = loaded,
        };
        RuntimeShaderServices.Current = services;

        XRShader shader = new();

        shader.Reload("Test.asset");

        services.LoadAssetCallCount.ShouldBe(1);
        services.LastLoadedAssetPath.ShouldBe("Test.asset");
        shader.Type.ShouldBe(EShaderType.Compute);
        shader.Source.ShouldBeSameAs(loaded.Source);
        shader.GenerateAsync.ShouldBeTrue();
    }

    [Test]
    public async Task ShaderHelper_LoadsEngineShadersThroughRuntimeServices()
    {
        XRShader syncShader = new();
        XRShader asyncShader = new();
        TestRuntimeShaderServices services = new()
        {
            LoadedEngineAsset = syncShader,
            LoadedEngineAssetAsync = asyncShader,
        };
        RuntimeShaderServices.Current = services;

        XRShader syncResult = ShaderHelper.LoadEngineShader("Common/Test.frag");
        XRShader asyncResult = await ShaderHelper.LoadEngineShaderAsync("Common/Test.comp");

        syncResult.ShouldBeSameAs(syncShader);
        asyncResult.ShouldBeSameAs(asyncShader);
        syncResult.Type.ShouldBe(EShaderType.Fragment);
        asyncResult.Type.ShouldBe(EShaderType.Compute);
        services.LoadEngineAssetCallCount.ShouldBe(1);
        services.LoadEngineAssetAsyncCallCount.ShouldBe(1);
        services.LastEngineAssetRoot.ShouldBe("Shaders");
        services.LastEngineAssetRelativePath.ShouldBe("Common/Test.comp");
    }

    private sealed class TestRuntimeShaderServices : IRuntimeShaderServices
    {
        public XRShader? LoadedAsset { get; set; }

        public XRShader? LoadedEngineAsset { get; set; }

        public XRShader? LoadedEngineAssetAsync { get; set; }

        public int LoadAssetCallCount { get; private set; }

        public int LoadEngineAssetCallCount { get; private set; }

        public int LoadEngineAssetAsyncCallCount { get; private set; }

        public string? LastLoadedAssetPath { get; private set; }

        public string? LastEngineAssetRoot { get; private set; }

        public string? LastEngineAssetRelativePath { get; private set; }

        public T? LoadAsset<T>(string filePath) where T : XRAsset, new()
        {
            LoadAssetCallCount++;
            LastLoadedAssetPath = filePath;
            return LoadedAsset as T;
        }

        public T LoadEngineAsset<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath) where T : XRAsset, new()
        {
            LoadEngineAssetCallCount++;
            LastEngineAssetRoot = assetRoot;
            LastEngineAssetRelativePath = relativePath;
            return (LoadedEngineAsset as T)!;
        }

        public Task<T> LoadEngineAssetAsync<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath) where T : XRAsset, new()
        {
            LoadEngineAssetAsyncCallCount++;
            LastEngineAssetRoot = assetRoot;
            LastEngineAssetRelativePath = relativePath;
            return Task.FromResult((LoadedEngineAssetAsync as T)!);
        }

        public void LogWarning(string message)
        {
        }
    }
}
