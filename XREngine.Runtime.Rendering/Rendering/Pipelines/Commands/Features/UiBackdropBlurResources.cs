using System;
using XREngine.Data.Rendering;
using XREngine.Rendering.Resources;
using XREngine.Rendering.Shaders.Compilation;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Shared resource-layout helper that registers the 3-mip Dual-Kawase UI backdrop blur pyramid
/// and scratch targets in any <see cref="RenderPipeline"/> (mono or stereo multiview) without
/// coupling backdrop blur to a specific pipeline implementation.
/// </summary>
public static class UiBackdropBlurResources
{
    public const string OutputTextureName = "UiBackdropBlurTexture";
    public const string HalfTextureName = OutputTextureName;
    public const string HalfScratchTextureName = "UiBackdropBlurHalfScratchTexture";
    public const string QuarterScratchTextureName = "UiBackdropBlurQuarterScratchTexture";

    public const string Mip0FboName = "UiBackdropBlurMip0FBO";
    public const string Mip1FboName = "UiBackdropBlurMip1FBO";
    public const string Mip2FboName = "UiBackdropBlurMip2FBO";
    public const string HalfScratchFboName = "UiBackdropBlurHalfScratchFBO";
    public const string QuarterScratchFboName = "UiBackdropBlurQuarterScratchFBO";

    public const uint MaxPyramidMipCount = 3u;

    /// <summary>
    /// Encodes the active <see cref="EUiBackdropBlurFormat"/> into two feature bits so pipelines
    /// rebuild their blur targets automatically when the user switches format at runtime.
    /// </summary>
    public static ulong GetFormatFeatureBits(int shift)
        => ((ulong)RuntimeEngine.Rendering.Settings.UiBackdropBlurFormat & 0x3UL) << shift;

    /// <summary>
    /// Resolves the legal mip count (1 to 3) for a half-resolution backdrop blur texture extent.
    /// </summary>
    public static uint ResolveMipLevelCount(uint halfWidth, uint halfHeight)
        => (uint)(Math.Min((int)MaxPyramidMipCount - 1, XRTexture.GetSmallestMipmapLevel(
            Math.Max(halfWidth, 1u),
            Math.Max(halfHeight, 1u))) + 1);

    /// <summary>
    /// Alias for <see cref="DeclareResources"/> matching the design contract naming.
    /// </summary>
    public static void AddToLayout(RenderPipelineResourceLayoutBuilder builder, RenderPipeline pipeline)
        => DeclareResources(builder, pipeline);

    /// <summary>
    /// Declares the 3-mip display-scaled UI backdrop blur output texture (mono <see cref="XRTexture2D"/>
    /// or layered stereo <see cref="XRTexture2DArray"/>) plus scratch downsample targets and per-mip FBOs.
    /// Skips cooked WebGPU profiles where the desktop GLSL Dual-Kawase pass does not execute.
    /// </summary>
    public static void DeclareResources(RenderPipelineResourceLayoutBuilder builder, RenderPipeline pipeline)
    {
        if (WebPipelineRasterProgram.IsActive)
            return;

        EUiBackdropBlurFormat blurFormat = RuntimeEngine.Rendering.Settings.UiBackdropBlurFormat;
        ResolveFormats(
            blurFormat,
            out EPixelInternalFormat internalFormat,
            out EPixelFormat pixelFormat,
            out EPixelType pixelType,
            out ESizedInternalFormat sizedFormat);

        RenderPipelineResourceProfile profile = builder.Profile;
        bool stereo = profile.Stereo || profile.ViewCount > 1u;
        uint layerCount = stereo ? Math.Max(profile.ViewCount, 2u) : 1u;

        RenderResourceSizePolicy halfSize = RenderResourceSizePolicy.Window(0.5f);
        RenderResourceSizePolicy quarterSize = RenderResourceSizePolicy.Window(0.25f);
        RenderResourceSizePolicy eighthSize = RenderResourceSizePolicy.Window(0.125f);

        (uint halfW, uint halfH) = halfSize.Resolve(profile);
        halfW = Math.Max(1u, halfW);
        halfH = Math.Max(1u, halfH);
        uint mipCount = ResolveMipLevelCount(halfW, halfH);

        // 3-mip output pyramid sampled by UIQuadBatched.fs via hardware trilinear textureLod:
        // mip 0 (1/2) = mild blur, mip 1 (1/4) = medium Dual-Kawase blur, mip 2 (1/8) = deep Apple glass blur.
        var outputBuilder = builder.Texture(OutputTextureName)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Size(halfSize)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.SampledTexture)
            .Format(internalFormat, pixelFormat, pixelType)
            .SizedFormat(sizedFormat)
            .Layers(layerCount)
            .StereoCompatible(stereo)
            .Factory(() => CreateBlurTexture(
                OutputTextureName,
                halfSize,
                profile,
                stereo,
                layerCount,
                mipCount,
                internalFormat,
                pixelFormat,
                pixelType,
                sizedFormat));

        if (mipCount > 1u)
        {
            outputBuilder.Mips(new RenderResourceMipPolicy(
                0u,
                mipCount,
                AutoGenerateMipmaps: false,
                RequireImmutableStorage: true));
        }

        outputBuilder.Add();

        // Single-mip scratch targets for DS1 (1/2) and DS2 (1/4) so every pass writes cleanly
        // without simultaneous read/write hazards on the 3-mip output pyramid.
        DeclareScratchLevel(
            builder,
            profile,
            stereo,
            layerCount,
            HalfScratchTextureName,
            HalfScratchFboName,
            halfSize,
            internalFormat,
            pixelFormat,
            pixelType,
            sizedFormat);

        DeclareScratchLevel(
            builder,
            profile,
            stereo,
            layerCount,
            QuarterScratchTextureName,
            QuarterScratchFboName,
            quarterSize,
            internalFormat,
            pixelFormat,
            pixelType,
            sizedFormat);

        // Per-mip FBOs targeting mip 0 (1/2), mip 1 (1/4), and mip 2 (1/8) of UiBackdropBlurTexture.
        DeclareOutputMipFbo(builder, Mip0FboName, halfSize, 0, stereo);
        if (mipCount >= 2u)
            DeclareOutputMipFbo(builder, Mip1FboName, quarterSize, 1, stereo);
        if (mipCount >= 3u)
            DeclareOutputMipFbo(builder, Mip2FboName, eighthSize, 2, stereo);
    }

    private static void DeclareScratchLevel(
        RenderPipelineResourceLayoutBuilder builder,
        RenderPipelineResourceProfile profile,
        bool stereo,
        uint layerCount,
        string textureName,
        string fboName,
        RenderResourceSizePolicy sizePolicy,
        EPixelInternalFormat internalFormat,
        EPixelFormat pixelFormat,
        EPixelType pixelType,
        ESizedInternalFormat sizedFormat)
    {
        builder.Texture(textureName)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Size(sizePolicy)
            .Usage(RenderPipelineResourceUsage.ColorAttachment | RenderPipelineResourceUsage.SampledTexture)
            .Format(internalFormat, pixelFormat, pixelType)
            .SizedFormat(sizedFormat)
            .Layers(layerCount)
            .StereoCompatible(stereo)
            .Factory(() => CreateBlurTexture(
                textureName,
                sizePolicy,
                profile,
                stereo,
                layerCount,
                mipCount: 1u,
                internalFormat,
                pixelFormat,
                pixelType,
                sizedFormat))
            .Add();

        builder.FrameBuffer(fboName)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Size(sizePolicy)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, textureName, mipLevel: 0, layerIndex: -1)
            .Factory(() => CreateBlurFrameBuffer(textureName, fboName, mipLevel: 0, stereo))
            .Add();
    }

    private static void DeclareOutputMipFbo(
        RenderPipelineResourceLayoutBuilder builder,
        string fboName,
        RenderResourceSizePolicy sizePolicy,
        int mipLevel,
        bool stereo)
    {
        builder.FrameBuffer(fboName)
            .Lifetime(RenderResourceLifetime.Persistent)
            .Size(sizePolicy)
            .Usage(RenderPipelineResourceUsage.ColorAttachment)
            .Color(0, OutputTextureName, mipLevel: mipLevel, layerIndex: -1)
            .Factory(() => CreateBlurFrameBuffer(OutputTextureName, fboName, mipLevel, stereo))
            .Add();
    }

    private static XRTexture CreateBlurTexture(
        string textureName,
        RenderResourceSizePolicy sizePolicy,
        RenderPipelineResourceProfile profile,
        bool stereo,
        uint layerCount,
        uint mipCount,
        EPixelInternalFormat internalFormat,
        EPixelFormat pixelFormat,
        EPixelType pixelType,
        ESizedInternalFormat sizedFormat)
    {
        (uint width, uint height) = sizePolicy.Resolve(profile);
        width = Math.Max(1u, width);
        height = Math.Max(1u, height);
        int maxMipIndex = Math.Max(0, (int)mipCount - 1);

        if (stereo)
        {
            XRTexture2DArray texArray = XRTexture2DArray.CreateFrameBufferTexture(
                layerCount,
                width,
                height,
                internalFormat,
                pixelFormat,
                pixelType,
                EFrameBufferAttachment.ColorAttachment0);
            texArray.Resizable = false;
            texArray.SizedInternalFormat = sizedFormat;
            texArray.AutoGenerateMipmaps = false;
            texArray.LargestMipmapLevel = 0;
            texArray.SmallestAllowedMipmapLevel = maxMipIndex;
            texArray.OVRMultiViewParameters = new(0, layerCount);
            if (mipCount > 1u)
                ConfigureArrayMipChain(texArray, mipCount, internalFormat, pixelFormat, pixelType);
            texArray.MinFilter = mipCount > 1u ? ETexMinFilter.LinearMipmapLinear : ETexMinFilter.Linear;
            texArray.MagFilter = ETexMagFilter.Linear;
            texArray.UWrap = ETexWrapMode.ClampToEdge;
            texArray.VWrap = ETexWrapMode.ClampToEdge;
            texArray.SamplerName = textureName;
            texArray.Name = textureName;
            return texArray;
        }

        XRTexture2D tex = XRTexture2D.CreateFrameBufferTexture(
            width,
            height,
            internalFormat,
            pixelFormat,
            pixelType,
            EFrameBufferAttachment.ColorAttachment0);
        tex.Resizable = mipCount == 1u;
        tex.SizedInternalFormat = sizedFormat;
        tex.AutoGenerateMipmaps = false;
        tex.LargestMipmapLevel = 0;
        tex.SmallestAllowedMipmapLevel = maxMipIndex;
        if (mipCount > 1u)
            Configure2DMipChain(tex, mipCount, internalFormat, pixelFormat, pixelType);
        tex.MinFilter = mipCount > 1u ? ETexMinFilter.LinearMipmapLinear : ETexMinFilter.Linear;
        tex.MagFilter = ETexMagFilter.Linear;
        tex.UWrap = ETexWrapMode.ClampToEdge;
        tex.VWrap = ETexWrapMode.ClampToEdge;
        tex.SamplerName = textureName;
        tex.Name = textureName;
        return tex;
    }

    private static void Configure2DMipChain(
        XRTexture2D texture,
        uint mipCount,
        EPixelInternalFormat internalFormat,
        EPixelFormat pixelFormat,
        EPixelType pixelType)
    {
        int count = checked((int)mipCount);
        Mipmap2D[] mipmaps = new Mipmap2D[count];
        uint w = texture.Width;
        uint h = texture.Height;
        for (int i = 0; i < count; i++)
        {
            mipmaps[i] = new Mipmap2D(w, h, internalFormat, pixelFormat, pixelType, allocateData: false);
            w = Math.Max(1u, w >> 1);
            h = Math.Max(1u, h >> 1);
        }

        texture.Mipmaps = mipmaps;
        texture.AutoGenerateMipmaps = false;
    }

    private static void ConfigureArrayMipChain(
        XRTexture2DArray texture,
        uint mipCount,
        EPixelInternalFormat internalFormat,
        EPixelFormat pixelFormat,
        EPixelType pixelType)
    {
        foreach (XRTexture2D layer in texture.Textures)
            Configure2DMipChain(layer, mipCount, internalFormat, pixelFormat, pixelType);
    }

    private static XRFrameBuffer CreateBlurFrameBuffer(
        string textureName,
        string fboName,
        int mipLevel,
        bool stereo)
    {
        XRTexture? tex = RenderPipeline.GetTexture<XRTexture>(textureName);
        if (tex is not IFrameBufferAttachement attachment)
            throw new InvalidOperationException($"UI backdrop blur texture '{textureName}' was not realized before framebuffer '{fboName}'.");

        return new XRFrameBuffer((attachment, EFrameBufferAttachment.ColorAttachment0, mipLevel, -1))
        {
            ForceOvrMultiview = stereo,
            Name = fboName,
        };
    }

    private static void ResolveFormats(
        EUiBackdropBlurFormat format,
        out EPixelInternalFormat internalFormat,
        out EPixelFormat pixelFormat,
        out EPixelType pixelType,
        out ESizedInternalFormat sizedFormat)
    {
        switch (format)
        {
            case EUiBackdropBlurFormat.Rgba16f:
                internalFormat = EPixelInternalFormat.Rgba16f;
                pixelFormat = EPixelFormat.Rgba;
                pixelType = EPixelType.HalfFloat;
                sizedFormat = ESizedInternalFormat.Rgba16f;
                break;
            case EUiBackdropBlurFormat.R11fG11fB10f:
                internalFormat = EPixelInternalFormat.R11fG11fB10f;
                pixelFormat = EPixelFormat.Rgb;
                pixelType = EPixelType.Float;
                sizedFormat = ESizedInternalFormat.R11fG11fB10f;
                break;
            default:
                internalFormat = EPixelInternalFormat.Rgba8;
                pixelFormat = EPixelFormat.Rgba;
                pixelType = EPixelType.UnsignedByte;
                sizedFormat = ESizedInternalFormat.Rgba8;
                break;
        }
    }
}
