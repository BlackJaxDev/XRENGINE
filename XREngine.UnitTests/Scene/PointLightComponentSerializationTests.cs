using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using Shouldly;
using XREngine;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Core.Files;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Scene;

[TestFixture]
public sealed class PointLightComponentSerializationTests : GpuTestBase
{
    private IRuntimeShaderServices? _previousShaderServices;

    [SetUp]
    public void SetUp()
    {
        _previousShaderServices = RuntimeShaderServices.Current;
        RuntimeShaderServices.Current = new FileSystemRuntimeShaderServices(ShaderBasePath);
    }

    [TearDown]
    public void TearDown()
        => RuntimeShaderServices.Current = _previousShaderServices;

    [Test]
    public void CookedBinarySerializer_RoundTrips_Unattached_PointLightComponent()
    {
        PointLightComponent original = new(12.5f, 3.0f);

        byte[] bytes = CookedBinarySerializer.Serialize(original);
        bytes.Length.ShouldBeGreaterThan(0);

        PointLightComponent? clone = CookedBinarySerializer.Deserialize(typeof(PointLightComponent), bytes) as PointLightComponent;

        clone.ShouldNotBeNull();
        clone!.Radius.ShouldBe(12.5f);
        clone.Brightness.ShouldBe(3.0f);
        clone.ShadowCameras.Length.ShouldBe(6);
    }

    [Test]
    public void CookedBinarySerializer_RoundTrips_SceneNodeWithPointLight_RebindsShadowCameraParents()
    {
        SceneNode original = new("PointLightNode", new Transform());
        PointLightComponent light = original.AddComponent<PointLightComponent>()!;
        light.Radius = 18.0f;
        light.Brightness = 2.5f;

        byte[] bytes = CookedBinarySerializer.Serialize(original);
        bytes.Length.ShouldBeGreaterThan(0);

        SceneNode? cloneNode = CookedBinarySerializer.Deserialize(typeof(SceneNode), bytes) as SceneNode;
        cloneNode.ShouldNotBeNull();

        PointLightComponent? clone = cloneNode!.GetComponent<PointLightComponent>();
        clone.ShouldNotBeNull();
        clone!.Radius.ShouldBe(18.0f);
        clone.Brightness.ShouldBe(2.5f);
        clone.ShadowCameras.Length.ShouldBe(6);
        clone.ShadowCameras[0].Transform.Parent.ShouldNotBeNull();
        clone.ShadowCameras[0].Transform.Parent!.Parent.ShouldBeSameAs(clone.Transform);
    }

    private sealed class FileSystemRuntimeShaderServices(string shaderBasePath) : IRuntimeShaderServices
    {
        private readonly string _shaderBasePath = shaderBasePath;

        public T? LoadAsset<T>(string filePath) where T : XRAsset, new()
            => typeof(T) == typeof(XRShader) ? (T?)(object?)LoadShader(filePath) : default;

        public T LoadEngineAsset<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath) where T : XRAsset, new()
        {
            if (typeof(T) != typeof(XRShader))
                throw new NotSupportedException($"Test shader services only support {nameof(XRShader)} assets, not '{typeof(T)}'.");

            string fullPath = Path.Combine(_shaderBasePath, relativePath);
            return (T)(XRAsset)LoadShader(fullPath);
        }

        public Task<T> LoadEngineAssetAsync<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath) where T : XRAsset, new()
            => Task.FromResult(LoadEngineAsset<T>(priority, bypassJobThread, assetRoot, relativePath));

        public void LogWarning(string message)
        {
        }

        private static XRShader LoadShader(string fullPath)
        {
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("Shader file not found for test runtime services.", fullPath);

            string source = File.ReadAllText(fullPath);
            TextFile text = TextFile.FromText(source);
            text.FilePath = fullPath;
            text.Name = Path.GetFileName(fullPath);

            return new XRShader(XRShader.ResolveType(Path.GetExtension(fullPath)), text)
            {
                Name = Path.GetFileNameWithoutExtension(fullPath),
            };
        }
    }
}