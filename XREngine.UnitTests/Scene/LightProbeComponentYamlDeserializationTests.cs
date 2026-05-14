using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using NUnit.Framework;
using Shouldly;
using XREngine;
using XREngine.Components.Capture;
using XREngine.Components.Capture.Lights;
using XREngine.Core.Files;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Scene;

[TestFixture]
public sealed class LightProbeComponentYamlDeserializationTests : GpuTestBase
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
    public void YamlSerializer_RoundTrips_SceneNodeWithLightProbePreview_WhenPreviewDisplayRestoresBeforeOwnerAttachment()
    {
        SceneNode original = new("LightProbeNode", new Transform());
        LightProbeComponent probe = original.AddComponent<LightProbeComponent>()!;
        probe.PreviewEnabled = true;
        probe.PreviewDisplay = LightProbeComponent.ERenderPreview.Irradiance;

        string yaml = AssetManager.Serializer.Serialize(original);

        SceneNode? cloneNode = AssetManager.Deserializer.Deserialize<SceneNode>(yaml);
        cloneNode.ShouldNotBeNull();

        LightProbeComponent? clone = cloneNode!.GetComponent<LightProbeComponent>();
        clone.ShouldNotBeNull();
        clone!.SceneNode.ShouldBeSameAs(cloneNode);
        clone.PreviewEnabled.ShouldBeTrue();
        clone.PreviewDisplay.ShouldBe(LightProbeComponent.ERenderPreview.Irradiance);
    }

    [Test]
    public void GridSpawnerApplyDefaults_DisabledPreview_DoesNotLoadPreviewShader()
    {
        ClearEngineShaderLoadTaskCache();
        RuntimeShaderServices.Current = new ThrowingRuntimeShaderServices();

        SceneNode spawnerNode = new("Spawner", new Transform());
        LightProbeGridSpawnerComponent spawner = spawnerNode.AddComponent<LightProbeGridSpawnerComponent>()!;
        spawner.PreviewProbes = false;
        spawner.ReleaseTransientEnvironmentTexturesAfterCapture = false;

        SceneNode probeNode = new("Probe", new Transform());
        LightProbeComponent probe = probeNode.AddComponent<LightProbeComponent>()!;

        MethodInfo applyDefaults = typeof(LightProbeGridSpawnerComponent).GetMethod(
            "ApplyDefaults",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        applyDefaults.Invoke(spawner, new object[] { probe });

        probe.PreviewEnabled.ShouldBeFalse();
        probe.AutoShowPreviewOnSelect.ShouldBeTrue();
    }

    private static void ClearEngineShaderLoadTaskCache()
    {
        FieldInfo cacheField = typeof(XREngine.Rendering.Models.Materials.ShaderHelper).GetField(
            "EngineShaderLoadTasks",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        object cache = cacheField.GetValue(null)!;
        cache.GetType().GetMethod("Clear")!.Invoke(cache, null);
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

    private sealed class ThrowingRuntimeShaderServices : IRuntimeShaderServices
    {
        public T? LoadAsset<T>(string filePath) where T : XRAsset, new()
            => throw new AssertionException($"Unexpected shader asset load for '{filePath}'.");

        public T LoadEngineAsset<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath) where T : XRAsset, new()
            => throw new AssertionException($"Unexpected engine shader load for '{relativePath}'.");

        public Task<T> LoadEngineAssetAsync<T>(JobPriority priority, bool bypassJobThread, string assetRoot, string relativePath) where T : XRAsset, new()
            => throw new AssertionException($"Unexpected async engine shader load for '{relativePath}'.");

        public void LogWarning(string message)
        {
        }
    }
}
