using NUnit.Framework;
using Shouldly;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Resources.Authoring;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class AuthoredRenderResourceTests
{
    private static readonly RenderPipelineResourceProfile Profile = RenderPipelineResourceProfile.Empty with
    {
        InternalWidth = 128u,
        InternalHeight = 64u,
        DisplayWidth = 128u,
        DisplayHeight = 64u,
    };

    [Test]
    public void AuthoredTexture_MatchesCodeDeclaredTexture()
    {
        AuthoredRenderResource authored = new()
        {
            Name = "HDRColor",
            Kind = EAuthoredRenderResourceKind.Texture,
            Format = ESizedInternalFormat.Rgba16f,
            Size = AuthoredSizePolicy.Internal(0.5f),
            Layers = 1u,
        };
        RenderPipelineResourceLayoutBuilder authoredBuilder = new(Profile);
        AuthoredRenderResourceFactory.Declare(authoredBuilder, authored, static _ => null);
        RenderPipelineResourceLayout authoredLayout = authoredBuilder.Build(Profile);

        RenderPipelineResourceLayout codeLayout = new RenderPipelineResourceLayoutBuilder(Profile)
            .Texture("HDRColor")
            .Size(RenderResourceSizePolicy.Internal(0.5f))
            .Usage(RenderPipelineResourceUsage.SampledTexture | RenderPipelineResourceUsage.ColorAttachment)
            .Format(EPixelInternalFormat.Rgba16f, EPixelFormat.Rgba, EPixelType.HalfFloat)
            .SizedFormat(ESizedInternalFormat.Rgba16f)
            .Add()
            .Build(Profile);

        authoredLayout.TryGet("HDRColor", out RenderPipelineResourceSpec? authoredSpec).ShouldBeTrue();
        codeLayout.TryGet("HDRColor", out RenderPipelineResourceSpec? codeSpec).ShouldBeTrue();
        ((TextureSpec)authoredSpec!).ToDescriptor().ShouldBe(((TextureSpec)codeSpec!).ToDescriptor());
        RenderPipelineResourceLayout.AreSpecsStructurallyEquivalent(authoredSpec, codeSpec).ShouldBeTrue();
    }

    [Test]
    public void AuthoredFrameBuffer_DeclaresItsAttachments()
    {
        AuthoredRenderResource[] resources =
        [
            new() { Name = "Color", Kind = EAuthoredRenderResourceKind.Texture, Format = ESizedInternalFormat.Rgba8 },
            new() { Name = "Depth", Kind = EAuthoredRenderResourceKind.Texture, Format = ESizedInternalFormat.Depth24Stencil8 },
            new()
            {
                Name = "SceneFBO",
                Kind = EAuthoredRenderResourceKind.FrameBuffer,
                Attachments =
                [
                    new() { ResourceName = "Color", Attachment = EFrameBufferAttachment.ColorAttachment0 },
                    new() { ResourceName = "Depth", Attachment = EFrameBufferAttachment.DepthStencilAttachment },
                ],
            },
        ];
        RenderPipelineResourceLayoutBuilder builder = new(Profile);
        AuthoredRenderResourceFactory.Declare(builder, resources, static _ => null);
        RenderPipelineResourceLayout layout = builder.Build(Profile);

        layout.TryGet("SceneFBO", out RenderPipelineResourceSpec? spec).ShouldBeTrue();
        FrameBufferSpec fbo = spec.ShouldBeOfType<FrameBufferSpec>();
        fbo.Attachments.Count.ShouldBe(2);
        layout.TryGet("Depth", out RenderPipelineResourceSpec? depth).ShouldBeTrue();
        depth!.Usage.HasFlag(RenderPipelineResourceUsage.DepthStencilAttachment).ShouldBeTrue();
    }

    [Test]
    public void AuthoredTexture_WithZeroLayersFollowsTheStereoProfile()
    {
        RenderPipelineResourceProfile stereo = Profile with { Stereo = true, ViewCount = 2u };
        RenderPipelineResourceLayoutBuilder builder = new(stereo);
        AuthoredRenderResourceFactory.Declare(builder, new AuthoredRenderResource { Name = "Color", Format = ESizedInternalFormat.Rgba8 }, static _ => null);

        builder.Build(stereo).TryGet("Color", out RenderPipelineResourceSpec? spec).ShouldBeTrue();
        TextureSpec texture = spec.ShouldBeOfType<TextureSpec>();
        texture.Layers.ShouldBe(2u);
        texture.StereoCompatible.ShouldBeTrue();
    }

    [Test]
    public void NonPortableFormat_IsRejected()
    {
        RenderPipelineResourceLayoutBuilder builder = new(Profile);

        Should.Throw<ArgumentException>(() => AuthoredRenderResourceFactory.Declare(
            builder,
            new AuthoredRenderResource { Name = "Rgb", Format = ESizedInternalFormat.Rgb16f },
            static _ => null));
        PortableRenderFormats.IsPortable(ESizedInternalFormat.Rgb16f).ShouldBeFalse();
        PortableRenderFormats.IsUnpackedThreeChannel(ESizedInternalFormat.Rgb16f).ShouldBeTrue();
    }

    [Test]
    public void LayoutHash_ChangesWhenAFormatChanges()
    {
        List<AuthoredRenderResource> resources = [new() { Name = "Color", Format = ESizedInternalFormat.Rgba8 }];
        ulong before = AuthoredRenderResourceFactory.ComputeLayoutHash(resources);
        AuthoredRenderResourceFactory.ComputeLayoutHash(resources).ShouldBe(before);

        resources[0].Format = ESizedInternalFormat.Rgba16f;

        AuthoredRenderResourceFactory.ComputeLayoutHash(resources).ShouldNotBe(before);
        AuthoredRenderResourceFactory.ComputeLayoutHash([]).ShouldBe(0UL);
    }

    [Test]
    public void AuthoredResource_RoundTripsThroughYaml()
    {
        AuthoredRenderResource original = new()
        {
            Name = "SceneFBO",
            Kind = EAuthoredRenderResourceKind.FrameBuffer,
            Size = AuthoredSizePolicy.Window(0.25f),
            Attachments = [new() { ResourceName = "Color", Attachment = EFrameBufferAttachment.ColorAttachment1, MipLevel = 2 }],
        };

        string yaml = AssetManager.Serializer.Serialize(original, typeof(AuthoredRenderResource));
        AuthoredRenderResource? clone = AssetManager.Deserializer.Deserialize<AuthoredRenderResource>(yaml);

        clone.ShouldNotBeNull();
        clone!.Kind.ShouldBe(EAuthoredRenderResourceKind.FrameBuffer);
        clone.Size.SizeClass.ShouldBe(RenderResourceSizeClass.WindowResolution);
        clone.Size.ScaleX.ShouldBe(0.25f);
        clone.Attachments.ShouldHaveSingleItem().Attachment.ShouldBe(EFrameBufferAttachment.ColorAttachment1);
        AuthoredRenderResourceFactory.ComputeLayoutHash([clone]).ShouldBe(AuthoredRenderResourceFactory.ComputeLayoutHash([original]));
    }
}
