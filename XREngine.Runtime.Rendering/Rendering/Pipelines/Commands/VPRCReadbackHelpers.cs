using System;
using System.Numerics;

namespace XREngine.Rendering.Pipelines.Commands;

internal static class VPRCReadbackHelpers
{
    public static bool TryReadTextureMip(
        XRTexture texture,
        int mipLevel,
        int layerIndex,
        out float[] rgbaFloats,
        out int width,
        out int height,
        out string failure)
    {
        rgbaFloats = [];
        width = 0;
        height = 0;

        AbstractRenderer? renderer = AbstractRenderer.Current;
        if (renderer is null)
        {
            failure = "Texture readback requires an active renderer.";
            return false;
        }

        if (!renderer.TryReadTextureMipRgbaFloat(texture, mipLevel, layerIndex, out float[]? rgba, out width, out height, out failure) ||
            rgba is null)
        {
            rgbaFloats = [];
            return false;
        }

        rgbaFloats = rgba;
        return true;
    }

    public static bool TryReadPixel(
        float[] rgbaFloats,
        int width,
        int height,
        int pixelX,
        int pixelY,
        out Vector4 rgba)
    {
        rgba = Vector4.Zero;
        if (pixelX < 0 || pixelX >= width || pixelY < 0 || pixelY >= height)
            return false;

        int index = ((pixelY * width) + pixelX) * 4;
        if (index < 0 || index + 3 >= rgbaFloats.Length)
            return false;

        rgba = new Vector4(
            rgbaFloats[index + 0],
            rgbaFloats[index + 1],
            rgbaFloats[index + 2],
            rgbaFloats[index + 3]);
        return true;
    }

    public static bool TryCropRegion(
        float[] rgbaFloats,
        int sourceWidth,
        int sourceHeight,
        int x,
        int y,
        int width,
        int height,
        out float[] cropped,
        out int croppedWidth,
        out int croppedHeight)
    {
        cropped = [];
        croppedWidth = 0;
        croppedHeight = 0;

        if (!TryNormalizeRegion(sourceWidth, sourceHeight, x, y, width, height, out int clampedX, out int clampedY, out croppedWidth, out croppedHeight))
            return false;

        cropped = new float[croppedWidth * croppedHeight * 4];
        for (int row = 0; row < croppedHeight; row++)
        {
            int srcIndex = (((clampedY + row) * sourceWidth) + clampedX) * 4;
            int dstIndex = row * croppedWidth * 4;
            Array.Copy(rgbaFloats, srcIndex, cropped, dstIndex, croppedWidth * 4);
        }

        return true;
    }

    public static bool TryNormalizeRegion(
        int sourceWidth,
        int sourceHeight,
        int x,
        int y,
        int width,
        int height,
        out int clampedX,
        out int clampedY,
        out int clampedWidth,
        out int clampedHeight)
    {
        clampedX = Math.Clamp(x, 0, Math.Max(0, sourceWidth - 1));
        clampedY = Math.Clamp(y, 0, Math.Max(0, sourceHeight - 1));

        int desiredWidth = width <= 0 ? sourceWidth - clampedX : width;
        int desiredHeight = height <= 0 ? sourceHeight - clampedY : height;

        clampedWidth = Math.Clamp(desiredWidth, 0, sourceWidth - clampedX);
        clampedHeight = Math.Clamp(desiredHeight, 0, sourceHeight - clampedY);
        return clampedWidth > 0 && clampedHeight > 0;
    }

    public static byte[] ToBytes(float[] values)
    {
        byte[] bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static byte[] ToBytes(uint[] values)
    {
        byte[] bytes = new byte[values.Length * sizeof(uint)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}