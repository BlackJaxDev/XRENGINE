using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using NUnit.Framework;
using Shouldly;
using XREngine;
using XREngine.Components;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Core.Files;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Scene;

[TestFixture]
public sealed class LightComponentYamlDeserializationTests : GpuTestBase
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
    public void YamlSerializer_RoundTrips_SceneNodeWithDirectionalLight_WhenScaleRestoresBeforeOwnerAttachment()
    {
        SceneNode original = new("DirectionalLightNode", new Transform());
        DirectionalLightComponent light = original.AddComponent<DirectionalLightComponent>()!;
        light.Scale = new Vector3(12.0f, 13.0f, 14.0f);

        string yaml = AssetManager.Serializer.Serialize(original);

        SceneNode? cloneNode = AssetManager.Deserializer.Deserialize<SceneNode>(yaml);
        cloneNode.ShouldNotBeNull();

        DirectionalLightComponent? clone = cloneNode!.GetComponent<DirectionalLightComponent>();
        clone.ShouldNotBeNull();
        clone!.SceneNode.ShouldBeSameAs(cloneNode);
        clone.Scale.ShouldBe(light.Scale);
    }

    [Test]
    public void YamlSerializer_RoundTrips_StandaloneDirectionalShadowSettings_WithoutRuntimeFramebuffer()
    {
        SceneNode original = new("StandaloneDirectionalLight", new Transform());
        DirectionalLightComponent light = original.AddComponent<DirectionalLightComponent>()!;
        light.CastsShadows = true;
        light.UseShadowAtlas = false;
        light.EnableCascadedShadows = false;
        light.EnableContactShadows = false;

        string yaml = AssetManager.Serializer.Serialize(original);

        yaml.ShouldContain("UseShadowAtlas: false");
        yaml.ShouldContain("EnableCascadedShadows: false");
        yaml.ShouldContain("EnableContactShadows: false");
        yaml.ShouldNotContain("ShadowMap:");

        SceneNode cloneNode = AssetManager.Deserializer
            .Deserialize<SceneNode>(yaml)
            .ShouldNotBeNull();
        DirectionalLightComponent clone = cloneNode
            .GetComponent<DirectionalLightComponent>()
            .ShouldNotBeNull();

        clone.CastsShadows.ShouldBeTrue();
        clone.UseShadowAtlas.ShouldBeFalse();
        clone.EnableCascadedShadows.ShouldBeFalse();
        clone.EnableContactShadows.ShouldBeFalse();
        clone.UsesDirectionalShadowAtlasForCurrentEncoding.ShouldBeFalse();
    }

    [Test]
    public void YamlSerializer_RoundTrips_NonCascadedCameraShadowMode()
    {
        SceneNode original = new("DesktopCamera", new Transform());
        CameraComponent camera = original.AddComponent<CameraComponent>()!;
        camera.DirectionalShadowRenderingMode = EDirectionalShadowRenderingMode.NonCascaded;

        string yaml = AssetManager.Serializer.Serialize(original);

        yaml.ShouldContain("DirectionalShadowRenderingMode: NonCascaded");
        yaml.ShouldNotContain("AntiAliasingModeOverride:");
        yaml.ShouldNotContain("ForwardPlusDebug");

        SceneNode cloneNode = AssetManager.Deserializer
            .Deserialize<SceneNode>(yaml)
            .ShouldNotBeNull();
        CameraComponent clone = cloneNode
            .GetComponent<CameraComponent>()
            .ShouldNotBeNull();

        clone.DirectionalShadowRenderingMode.ShouldBe(EDirectionalShadowRenderingMode.NonCascaded);
    }

    [Test]
    public void YamlSerializer_RoundTrips_SceneNodeWithSpotLight_WhenDistanceRestoresBeforeOwnerAttachment()
    {
        SceneNode original = new("SpotLightNode", new Transform());
        SpotLightComponent light = original.AddComponent<SpotLightComponent>()!;
        light.Distance = 42.0f;

        string yaml = AssetManager.Serializer.Serialize(original);

        SceneNode? cloneNode = AssetManager.Deserializer.Deserialize<SceneNode>(yaml);
        cloneNode.ShouldNotBeNull();

        SpotLightComponent? clone = cloneNode!.GetComponent<SpotLightComponent>();
        clone.ShouldNotBeNull();
        clone!.SceneNode.ShouldBeSameAs(cloneNode);
        clone.Distance.ShouldBe(42.0f);
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