using NUnit.Framework;
using Shouldly;
using XREngine.Rendering.Vulkan;
using XREngine.Runtime.Bootstrap;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class UberEffectsVulkanCompilationTests
{
    private IRuntimeShaderServices? _previousServices;
    private IRuntimeRenderingHostServices? _previousHost;

    [SetUp]
    public void SetUp()
    {
        _previousServices = RuntimeShaderServices.Current;
        _previousHost = RuntimeRenderingHostServices.Current;
        RuntimeShaderServices.Current = new UberRuntimeShaderServices();
        RuntimeRenderingHostServices.Current = RuntimeRenderingBootstrap.CreateEngineHostServices();
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeShaderServices.Current = _previousServices;
        RuntimeRenderingHostServices.Current = _previousHost!;
    }

    [Test]
    public void EffectsFragmentAndMonoVertex_CompileToSpirv()
    {
        XRMaterial material = new()
        {
            Parameters = ModelAssetImporter.CreateDefaultForwardPlusUberShaderParameters(),
            RenderOptions = ModelAssetImporter.CreateForwardPlusUberShaderRenderOptions(),
        };
        material.Shaders.Add(ShaderHelper.UberFragForward());
        material.EnsureUberStateInitialized();
        foreach (string feature in new[]
        {
            "outline",
            "dissolve",
            "extended-effects",
            "audiolink",
            "environment-lighting",
            "view-context",
            "vertex-effects",
        })
            material.SetUberFeatureEnabled(feature, true);
        material.PrepareUberVariantImmediately().ShouldBeTrue();

        byte[] fragmentSpirv = VulkanShaderCompiler.Compile(
            material.FragmentShaders.Single(),
            out string fragmentEntry,
            out _,
            out _);
        fragmentEntry.ShouldBe("main");
        fragmentSpirv.Length.ShouldBeGreaterThan(0);

        XRShader vertex = ShaderHelper.LoadEngineShader(
            Path.Combine("Uber", "UberShader.vert"),
            EShaderType.Vertex);
        byte[] vertexSpirv = VulkanShaderCompiler.Compile(
            vertex,
            out string vertexEntry,
            out _,
            out _);
        vertexEntry.ShouldBe("main");
        vertexSpirv.Length.ShouldBeGreaterThan(0);
    }
}
