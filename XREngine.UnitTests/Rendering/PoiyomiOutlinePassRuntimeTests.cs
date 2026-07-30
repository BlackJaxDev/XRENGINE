using NUnit.Framework;
using Shouldly;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Vulkan;
using XREngine.Runtime.Bootstrap;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
[NonParallelizable]
public sealed class PoiyomiOutlinePassRuntimeTests
{
    private IRuntimeShaderServices? _previousShaderServices;
    private IRuntimeRenderingHostServices? _previousRenderingServices;

    [SetUp]
    public void SetUp()
    {
        _previousShaderServices = RuntimeShaderServices.Current;
        _previousRenderingServices = RuntimeRenderingHostServices.Current;
        RuntimeShaderServices.Current = new PoiyomiRuntimeShaderServices();
        RuntimeRenderingHostServices.Current = RuntimeRenderingBootstrap.CreateEngineHostServices();
    }

    [TearDown]
    public void TearDown()
    {
        RuntimeShaderServices.Current = _previousShaderServices;
        RuntimeRenderingHostServices.Current = _previousRenderingServices!;
    }

    [Test]
    public void OutlineVariant_SharesAuthoredStateAndUsesCanonicalMonoAndVrShaders()
    {
        RenderingParameters outlineOptions = new()
        {
            CullMode = ECullMode.Front,
        };
        XRMaterial source = new()
        {
            Name = "Poiyomi outline runtime contract",
            RenderPass = (int)EDefaultRenderPass.OpaqueForward,
            PassSet = new MaterialPassSet
            {
                Passes =
                [
                    new MaterialPassDefinition
                    {
                        Identity = EMaterialPassIdentity.Base,
                        Order = 500,
                        RenderPass = (int)EDefaultRenderPass.OpaqueForward,
                    },
                    new MaterialPassDefinition
                    {
                        Identity = EMaterialPassIdentity.Outline,
                        Order = 600,
                        RenderPass = (int)EDefaultRenderPass.OpaqueForward,
                        VariantMacros = ["XRENGINE_OUTLINE_PASS"],
                        RenderOptions = outlineOptions,
                    },
                ],
            },
        };
        source.SetShader(EShaderType.Fragment, ShaderHelper.UberFragForward(), coerceShaderType: true);
        source.SetUberFeatureEnabled("outline", true);
        source.PrepareUberVariantImmediately().ShouldBeTrue(source.UberVariantStatus.FailureReason);

        XRMaterial outline = source.OutlinePassVariant.ShouldNotBeNull();

        outline.RenderOptions.ShouldBeSameAs(outlineOptions);
        outline.Parameters.ShouldBeSameAs(source.Parameters);
        outline.Textures.ShouldBeSameAs(source.Textures);
        outline.VertexShaders.Count.ShouldBe(3);
        foreach (XRShader vertexShader in outline.VertexShaders)
            vertexShader.Source.Text.ShouldNotBeNull().ShouldContain("#define XRENGINE_OUTLINE_PASS");
        string fragmentSource = outline.FragmentShaders.Single().Source.Text.ShouldNotBeNull();
        fragmentSource.ShouldContain("canonical alpha and dissolve coverage path");
        fragmentSource.ShouldContain("vec2 outlineUv =");
        fragmentSource.ShouldContain("_OutlineTexture");
        fragmentSource.ShouldContain("_OutlineMask");

        byte[] fragmentSpirv = VulkanShaderCompiler.Compile(
            outline.FragmentShaders.Single(),
            out string fragmentEntryPoint,
            out _,
            out string? rewrittenFragment);
        fragmentSpirv.Length.ShouldBeGreaterThan(20);
        fragmentEntryPoint.ShouldBe("main");
        rewrittenFragment.ShouldNotBeNullOrWhiteSpace();
        rewrittenFragment.ShouldNotContain("undefined variable", Case.Insensitive);

        byte[] vertexSpirv = VulkanShaderCompiler.Compile(
            outline.VertexShaders[0],
            out string vertexEntryPoint,
            out _,
            out string? rewrittenVertex);
        vertexSpirv.Length.ShouldBeGreaterThan(20);
        vertexEntryPoint.ShouldBe("main");
        rewrittenVertex.ShouldNotBeNullOrWhiteSpace();
        rewrittenVertex.ShouldNotContain("undefined variable", Case.Insensitive);
    }
}
