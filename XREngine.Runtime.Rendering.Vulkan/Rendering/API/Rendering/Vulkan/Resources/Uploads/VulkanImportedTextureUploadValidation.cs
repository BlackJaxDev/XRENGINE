using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Silk.NET.Vulkan;
using XREngine.Data.Rendering;
using Buffer = Silk.NET.Vulkan.Buffer;
using Format = Silk.NET.Vulkan.Format;
using Image = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.Vulkan;

internal static class VulkanImportedTextureUploadValidation
{
    public static bool TryValidateCopyRegions(
        string? textureName,
        ulong publicationToken,
        Extent3D extent,
        uint mipLevels,
        uint arrayLayers,
        IReadOnlyList<VulkanImportedTextureUploadStagingResource> stagingResources,
        out string? failureReason)
    {
        failureReason = null;
        if (extent.Width == 0 || extent.Height == 0 || extent.Depth == 0)
        {
            failureReason = BuildFailure(textureName, publicationToken, $"image extent is invalid ({extent.Width}x{extent.Height}x{extent.Depth})");
            return false;
        }

        if (mipLevels == 0)
        {
            failureReason = BuildFailure(textureName, publicationToken, "image has zero mip levels");
            return false;
        }

        if (arrayLayers == 0)
        {
            failureReason = BuildFailure(textureName, publicationToken, "image has zero array layers");
            return false;
        }

        for (int i = 0; i < stagingResources.Count; i++)
        {
            BufferImageCopy region = stagingResources[i].CopyRegion;
            uint mipLevel = region.ImageSubresource.MipLevel;
            if (mipLevel >= mipLevels)
            {
                failureReason = BuildFailure(
                    textureName,
                    publicationToken,
                    $"copy[{i}] targets mip {mipLevel}, but image has {mipLevels} mip levels");
                return false;
            }

            uint baseLayer = region.ImageSubresource.BaseArrayLayer;
            uint layerCount = region.ImageSubresource.LayerCount;
            if (layerCount == 0 || baseLayer >= arrayLayers || baseLayer + layerCount > arrayLayers)
            {
                failureReason = BuildFailure(
                    textureName,
                    publicationToken,
                    $"copy[{i}] targets layers {baseLayer}+{layerCount}, but image has {arrayLayers} layers");
                return false;
            }

            if (region.ImageOffset.X < 0 || region.ImageOffset.Y < 0 || region.ImageOffset.Z < 0)
            {
                failureReason = BuildFailure(
                    textureName,
                    publicationToken,
                    $"copy[{i}] has negative image offset ({region.ImageOffset.X},{region.ImageOffset.Y},{region.ImageOffset.Z})");
                return false;
            }

            if (region.ImageExtent.Width == 0 || region.ImageExtent.Height == 0 || region.ImageExtent.Depth == 0)
            {
                failureReason = BuildFailure(
                    textureName,
                    publicationToken,
                    $"copy[{i}] has invalid extent {region.ImageExtent.Width}x{region.ImageExtent.Height}x{region.ImageExtent.Depth}");
                return false;
            }

            uint mipWidth = ResolveMipDimension(extent.Width, mipLevel);
            uint mipHeight = ResolveMipDimension(extent.Height, mipLevel);
            uint mipDepth = ResolveMipDimension(extent.Depth, mipLevel);
            ulong copyMaxX = (ulong)(uint)region.ImageOffset.X + region.ImageExtent.Width;
            ulong copyMaxY = (ulong)(uint)region.ImageOffset.Y + region.ImageExtent.Height;
            ulong copyMaxZ = (ulong)(uint)region.ImageOffset.Z + region.ImageExtent.Depth;
            if (copyMaxX > mipWidth || copyMaxY > mipHeight || copyMaxZ > mipDepth)
            {
                failureReason = BuildFailure(
                    textureName,
                    publicationToken,
                    $"copy[{i}] extent {region.ImageExtent.Width}x{region.ImageExtent.Height}x{region.ImageExtent.Depth} at offset {region.ImageOffset.X},{region.ImageOffset.Y},{region.ImageOffset.Z} exceeds mip {mipLevel} extent {mipWidth}x{mipHeight}x{mipDepth} (base {extent.Width}x{extent.Height}x{extent.Depth}, mips={mipLevels})");
                return false;
            }
        }

        return true;
    }

    private static uint ResolveMipDimension(uint baseDimension, uint mipLevel)
    {
        uint value = Math.Max(baseDimension, 1u);
        for (uint i = 0; i < mipLevel && value > 1u; i++)
            value >>= 1;
        return Math.Max(value, 1u);
    }

    private static string BuildFailure(string? textureName, ulong publicationToken, string detail)
        => $"invalid imported texture upload copy regions for '{textureName ?? "<unnamed>"}' token={publicationToken}: {detail}.";
}
