using System;
using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Enforces the destination-local raster and layered-attachment contract shared by
/// fullscreen post-process commands that do not use <see cref="VPRC_RenderQuadToFBO"/>.
/// </summary>
internal static class VPRCFullscreenPassContract
{
    public static void ValidateAndLog(
        XRRenderPipelineInstance instance,
        string passName,
        XRFrameBuffer destination,
        XRTexture source,
        bool stereo,
        BoundingRectangle? destinationRegion = null)
    {
        if (destination.Width == 0u || destination.Height == 0u)
            throw new InvalidOperationException($"Fullscreen pass '{passName}' has an empty destination '{destination.Name}'.");

        BoundingRectangle expectedRegion = destinationRegion ?? new BoundingRectangle(
            0,
            0,
            checked((int)destination.Width),
            checked((int)destination.Height));
        if (expectedRegion.X != 0 ||
            expectedRegion.Y != 0 ||
            expectedRegion.Width <= 0 ||
            expectedRegion.Height <= 0)
        {
            throw new InvalidOperationException(
                $"Fullscreen pass '{passName}' has an invalid destination-local region for '{destination.Name}': {expectedRegion}.");
        }

        BoundingRectangle area = instance.RenderState.CurrentRenderRegion;
        int destinationWidth = expectedRegion.Width;
        int destinationHeight = expectedRegion.Height;
        if (area.X != 0 ||
            area.Y != 0 ||
            area.Width != destinationWidth ||
            area.Height != destinationHeight)
        {
            throw new InvalidOperationException(
                $"Fullscreen pass '{passName}' screen-region mismatch for '{destination.Name}'. " +
                $"Expected=(0,0,{destinationWidth},{destinationHeight}) Actual={area}.");
        }

        uint attachmentLayers = ValidateDestinationAttachments(
            passName,
            destination,
            destinationWidth,
            destinationHeight,
            stereo);
        uint sourceLayers = ResolveTextureLayerCount(source);
        if (stereo)
        {
            ValidateStereoTexture(passName, "source", source, sourceLayers);
            if (attachmentLayers != 2u)
            {
                throw new InvalidOperationException(
                    $"Stereo fullscreen pass '{passName}' destination '{destination.Name}' exposes {attachmentLayers} layer(s); expected 2.");
            }
        }

        if (!RenderDiagnosticsFlags.DiagPostProcess)
            return;

        Vector3 sourceSize = source.WidthHeightDepth;
        Debug.RenderingEvery(
            $"FullscreenRegion.{instance.InstanceId}.{passName}.{destination.Name}",
            TimeSpan.FromSeconds(1),
            "[PostProcessDiag] Fullscreen pass={0} source={1} sourceExtent={2}x{3} sourceLayers={4} " +
            "destination={5} destinationExtent={6}x{7} renderArea=(0,0,{6},{7}) " +
            "viewport=(0,0,{6},{7}) scissor=(0,0,{6},{7}) attachmentLayers={8} viewMask=0x{9:X} " +
            "screenOrigin=(0,0) screenSize=({6},{7}) uv=(fragCoord-screenOrigin)/destinationExtent->[0,1]",
            passName,
            source.Name ?? source.GetType().Name,
            Math.Max(1, (int)MathF.Round(sourceSize.X)),
            Math.Max(1, (int)MathF.Round(sourceSize.Y)),
            sourceLayers,
            destination.Name ?? destination.GetType().Name,
            destinationWidth,
            destinationHeight,
            attachmentLayers,
            stereo ? 0x3u : 0u);
    }

    private static uint ValidateDestinationAttachments(
        string passName,
        XRFrameBuffer destination,
        int destinationWidth,
        int destinationHeight,
        bool stereo)
    {
        if (destination.Targets is not { Length: > 0 } targets)
            throw new InvalidOperationException($"Fullscreen pass '{passName}' destination '{destination.Name}' has no attachments.");

        uint attachmentLayers = 1u;
        bool foundColorAttachment = false;
        for (int i = 0; i < targets.Length; i++)
        {
            var (target, attachment, mipLevel, layerIndex) = targets[i];
            if (attachment is < EFrameBufferAttachment.ColorAttachment0 or > EFrameBufferAttachment.ColorAttachment7)
                continue;

            foundColorAttachment = true;
            uint layers = ResolveAttachmentLayerCount(target);
            attachmentLayers = Math.Max(attachmentLayers, layers);
            ValidateAttachmentExtent(
                passName,
                destination,
                target,
                mipLevel,
                destinationWidth,
                destinationHeight);

            if (!stereo)
                continue;

            if (layerIndex != -1)
            {
                throw new InvalidOperationException(
                    $"Stereo fullscreen pass '{passName}' selects layer {layerIndex} on destination '{destination.Name}'.");
            }

            if (target is not XRTexture texture)
                throw new InvalidOperationException($"Stereo fullscreen pass '{passName}' destination attachment is not a texture.");

            ValidateStereoTexture(passName, "destination", texture, layers);
        }

        if (!foundColorAttachment)
            throw new InvalidOperationException($"Fullscreen pass '{passName}' destination '{destination.Name}' has no color attachment.");

        return attachmentLayers;
    }

    private static void ValidateAttachmentExtent(
        string passName,
        XRFrameBuffer destination,
        IFrameBufferAttachement attachment,
        int mipLevel,
        int destinationWidth,
        int destinationHeight)
    {
        if (attachment is not XRTexture texture)
            return;

        Vector3 baseSize = texture.WidthHeightDepth;
        int mip = Math.Max(mipLevel, 0);
        int width = Math.Max(1, (int)MathF.Round(baseSize.X) >> mip);
        int height = Math.Max(1, (int)MathF.Round(baseSize.Y) >> mip);
        if (width != destinationWidth || height != destinationHeight)
        {
            throw new InvalidOperationException(
                $"Fullscreen pass '{passName}' destination '{destination.Name}' attachment extent mismatch. " +
                $"AttachmentMip={mipLevel} Extent={width}x{height} DestinationRegion={destinationWidth}x{destinationHeight} " +
                $"FBOBaseExtent={destination.Width}x{destination.Height}.");
        }
    }

    private static void ValidateStereoTexture(string passName, string role, XRTexture texture, uint layerCount)
    {
        XRTexture viewedTexture = texture is XRTextureViewBase view
            ? view.GetViewedTexture()
            : texture;
        if (layerCount != 2u ||
            viewedTexture is not XRTexture2DArray array ||
            array.Depth != 2u ||
            array.OVRMultiViewParameters is not { Offset: 0, NumViews: 2u })
        {
            throw new InvalidOperationException(
                $"Stereo fullscreen pass '{passName}' {role} is not a complete two-layer multiview texture. " +
                $"Texture={texture.GetType().Name} Layers={layerCount}.");
        }
    }

    private static uint ResolveAttachmentLayerCount(IFrameBufferAttachement attachment)
        => attachment switch
        {
            XRTextureViewBase view => Math.Max(view.NumLayers, 1u),
            XRTexture2DArray array => Math.Max(array.Depth, 1u),
            _ => 1u,
        };

    private static uint ResolveTextureLayerCount(XRTexture texture)
        => texture switch
        {
            XRTextureViewBase view => Math.Max(view.NumLayers, 1u),
            XRTexture2DArray array => Math.Max(array.Depth, 1u),
            _ => 1u,
        };
}
