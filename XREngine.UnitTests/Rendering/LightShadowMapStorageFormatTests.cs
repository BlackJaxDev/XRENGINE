using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class LightShadowMapStorageFormatTests
{
    private IRuntimeRenderingHostServices _previousRenderingHostServices = RuntimeRenderingHostServices.Current;
    private IRuntimeShaderServices? _previousShaderServices;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _previousRenderingHostServices = RuntimeRenderingHostServices.Current;
        _previousShaderServices = RuntimeShaderServices.Current;
        RuntimeRenderingHostServices.Current = TestRenderingHostServices.Create(
            _previousRenderingHostServices,
            new TestRenderPipeline());
        RuntimeShaderServices.Current = new GltfImportTestUtilities.TestRuntimeShaderServices();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        RuntimeRenderingHostServices.Current = _previousRenderingHostServices;
        RuntimeShaderServices.Current = _previousShaderServices;
    }

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

        XRMaterial material = light.GetShadowMapMaterial(64u, 64u);
        XRTexture2D rasterDepth = FindAttachmentTexture<XRTexture2D>(material, EFrameBufferAttachment.DepthAttachment);
        XRTexture2D shadowMap = FindShadowMapTexture<XRTexture2D>(material);

        rasterDepth.SizedInternalFormat.ShouldBe(ESizedInternalFormat.DepthComponent32f);
        rasterDepth.Mipmaps[0].InternalFormat.ShouldBe(EPixelInternalFormat.DepthComponent32f);
        rasterDepth.Mipmaps[0].PixelFormat.ShouldBe(EPixelFormat.DepthComponent);
        rasterDepth.Mipmaps[0].PixelType.ShouldBe(EPixelType.Float);
        (rasterDepth.Name ?? string.Empty).ShouldContain(".PrimaryRasterDepth");
        shadowMap.SizedInternalFormat.ShouldBe(ESizedInternalFormat.R16f);
        shadowMap.FrameBufferAttachment.ShouldBe(EFrameBufferAttachment.ColorAttachment0);
        (shadowMap.Name ?? string.Empty).ShouldContain(".PrimaryColor");
        light.CascadedShadowMapTexture.ShouldNotBeNull();
        light.CascadedShadowMapTexture!.SizedInternalFormat.ShouldBe(ESizedInternalFormat.R16f);
        (light.CascadedShadowMapTexture.Name ?? string.Empty).ShouldContain(".Cascade.ColorArray");
        light.GetCascadeFrameBuffer(0).ShouldNotBeNull();
        (light.GetCascadeFrameBuffer(0)!.Name ?? string.Empty).ShouldContain(".Cascade.Layer0Fbo");

        bool previousIsVulkan = RuntimeEngine.Rendering.State.IsVulkan;
        try
        {
            RuntimeEngine.Rendering.State.IsVulkan = true;
            light.CascadedShadowReceiverTexture.ShouldNotBeNull();
            light.CascadedShadowReceiverTexture!.SizedInternalFormat.ShouldBe(ESizedInternalFormat.DepthComponent32f);
            (light.CascadedShadowReceiverTexture.Name ?? string.Empty).ShouldContain(".Cascade.RasterDepthArray");
        }
        finally
        {
            RuntimeEngine.Rendering.State.IsVulkan = previousIsVulkan;
        }
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

    private static TTexture FindAttachmentTexture<TTexture>(XRMaterial material, EFrameBufferAttachment attachment)
        where TTexture : XRTexture
    {
        foreach (XRTexture? texture in material.Textures)
            if (texture is TTexture typed && texture.FrameBufferAttachment == attachment)
                return typed;

        Assert.Fail($"{attachment} texture of type {typeof(TTexture).Name} was not found.");
        return null!;
    }

    private class TestRenderingHostServices : DispatchProxy
    {
        public IRuntimeRenderingHostServices Inner { get; set; } = null!;
        public IRuntimeRenderPipelineHost DefaultPipeline { get; set; } = null!;

        public static IRuntimeRenderingHostServices Create(
            IRuntimeRenderingHostServices inner,
            IRuntimeRenderPipelineHost defaultPipeline)
        {
            IRuntimeRenderingHostServices proxy = Create<IRuntimeRenderingHostServices, TestRenderingHostServices>();
            TestRenderingHostServices state = (TestRenderingHostServices)(object)proxy;
            state.Inner = inner;
            state.DefaultPipeline = defaultPipeline;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod is null)
                return null;

            if (targetMethod.Name == nameof(IRuntimeRenderingHostServices.CreateDefaultRenderPipeline))
                return DefaultPipeline;

            return targetMethod.Invoke(Inner, args);
        }
    }

    private sealed class TestRenderPipeline : RenderPipeline
    {
        protected override Lazy<XRMaterial> InvalidMaterialFactory => new(() => new XRMaterial());

        protected override ViewportRenderCommandContainer GenerateCommandChain()
            => new(this);

        protected override Dictionary<int, IComparer<RenderCommand>?> GetPassIndicesAndSorters()
            => [];
    }
}
