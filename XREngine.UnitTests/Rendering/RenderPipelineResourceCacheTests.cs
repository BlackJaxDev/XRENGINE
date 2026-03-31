using System.Reflection;
using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RenderPipelineResourceCacheTests
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly MethodInfo CreateDeferredGBufferFboMethod = typeof(DefaultRenderPipeline)
        .GetMethod("CreateDeferredGBufferFBO", InstanceFlags)
        ?? throw new InvalidOperationException("Could not locate CreateDeferredGBufferFBO.");

    private static readonly MethodInfo CreatePostProcessOutputFboMethod = typeof(DefaultRenderPipeline)
        .GetMethod("CreatePostProcessOutputFBO", InstanceFlags)
        ?? throw new InvalidOperationException("Could not locate CreatePostProcessOutputFBO.");

    [Test]
    public void CacheOrCreateTexture_IgnoresVariableAliasWithSameName()
    {
        XRRenderPipelineInstance instance = new();

        XRTexture2D aliasTexture = CreateColorTexture("AliasedTexture");
        instance.Variables.SetTexture("AliasedTexture", aliasTexture);

        XRTexture? createdTexture = null;
        VPRC_CacheOrCreateTexture command = new();
        command.SetOptions(
            "AliasedTexture",
            () => createdTexture = CreateColorTexture("AliasedTexture"),
            null,
            null);

        using var _ = Engine.Rendering.State.PushRenderingPipeline(instance);
        command.ExecuteIfShould();

        instance.Resources.TryGetTexture("AliasedTexture", out XRTexture? cachedTexture).ShouldBeTrue();
        cachedTexture.ShouldNotBeNull();
        createdTexture.ShouldNotBeNull();
        cachedTexture.ShouldBeSameAs(createdTexture);
        cachedTexture.ShouldNotBeSameAs(aliasTexture);
    }

    [Test]
    public void CacheOrCreateFbo_IgnoresVariableAliasWithSameName()
    {
        XRRenderPipelineInstance instance = new();

        XRFrameBuffer aliasFbo = new();
        instance.Variables.SetFrameBuffer("AliasedFbo", aliasFbo);

        XRFrameBuffer? createdFbo = null;
        VPRC_CacheOrCreateFBO command = new();
        command.SetOptions(
            "AliasedFbo",
            () => createdFbo = new XRFrameBuffer(),
            null);

        using var _ = Engine.Rendering.State.PushRenderingPipeline(instance);
        command.ExecuteIfShould();

        instance.Resources.TryGetFrameBuffer("AliasedFbo", out XRFrameBuffer? cachedFbo).ShouldBeTrue();
        cachedFbo.ShouldNotBeNull();
        createdFbo.ShouldNotBeNull();
        cachedFbo.ShouldBeSameAs(createdFbo);
        cachedFbo.ShouldNotBeSameAs(aliasFbo);
    }

    [Test]
    public void DeferredGBufferFbo_RecreatesConcreteNonAttachableTexture()
    {
        XRRenderPipelineInstance instance = new();
        DefaultRenderPipeline pipeline = new();

        XRTexture3D staleTexture = new(1, 1, 1)
        {
            Name = DefaultRenderPipeline.AlbedoOpacityTextureName,
            SamplerName = DefaultRenderPipeline.AlbedoOpacityTextureName,
        };
        instance.SetTexture(staleTexture);

        using var _ = Engine.Rendering.State.PushRenderingPipeline(instance);
        XRFrameBuffer fbo = InvokePrivateFbo(pipeline, CreateDeferredGBufferFboMethod);

        instance.Resources.TryGetTexture(DefaultRenderPipeline.AlbedoOpacityTextureName, out XRTexture? recreatedTexture).ShouldBeTrue();
        recreatedTexture.ShouldNotBeNull();
        recreatedTexture.ShouldNotBeSameAs(staleTexture);
        recreatedTexture.ShouldBeOfType<XRTexture2D>();
        fbo.Targets.ShouldNotBeNull();
        fbo.Targets![0].Target.ShouldBeSameAs(recreatedTexture);
    }

    [Test]
    public void PostProcessOutputFbo_CreatesMissingConcreteTexture()
    {
        XRRenderPipelineInstance instance = new();
        DefaultRenderPipeline pipeline = new();

        using var _ = Engine.Rendering.State.PushRenderingPipeline(instance);
        XRFrameBuffer fbo = InvokePrivateFbo(pipeline, CreatePostProcessOutputFboMethod);

        instance.Resources.TryGetTexture(DefaultRenderPipeline.PostProcessOutputTextureName, out XRTexture? createdTexture).ShouldBeTrue();
        createdTexture.ShouldNotBeNull();
        createdTexture.ShouldBeOfType<XRTexture2D>();
        fbo.Targets.ShouldNotBeNull();
        fbo.Targets![0].Target.ShouldBeSameAs(createdTexture);
    }

    private static XRFrameBuffer InvokePrivateFbo(DefaultRenderPipeline pipeline, MethodInfo method)
        => method.Invoke(pipeline, null).ShouldBeOfType<XRFrameBuffer>();

    private static XRTexture2D CreateColorTexture(string name)
    {
        XRTexture2D texture = XRTexture2D.CreateFrameBufferTexture(
            8,
            8,
            EPixelInternalFormat.Rgba8,
            EPixelFormat.Rgba,
            EPixelType.UnsignedByte,
            EFrameBufferAttachment.ColorAttachment0);
        texture.Name = name;
        texture.SamplerName = name;
        return texture;
    }

    private static XRTexture2DView CreateColorTextureView(string name)
    {
        XRTexture2D sourceTexture = CreateColorTexture($"{name}_Source");
        return new XRTexture2DView(
            sourceTexture,
            0u,
            1u,
            ESizedInternalFormat.Rgba8,
            false,
            false)
        {
            Name = name,
            SamplerName = name,
        };
    }
}