using System;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal unsafe readonly struct DynamicRenderingFormatSignature : IEquatable<DynamicRenderingFormatSignature>
{
    // Vulkan implementations must support at least eight color attachments. The
    // engine deliberately caps render-target signatures at that portable limit so
    // compatibility keys remain pure values and never allocate in draw recording.
    internal const int MaxColorAttachmentCount = 8;

    [InlineArray(MaxColorAttachmentCount)]
    private struct ColorFormatStorage
    {
        private Format _element0;
    }

    private readonly ColorFormatStorage _colorFormats;
    private readonly byte _colorAttachmentCount;

    public DynamicRenderingFormatSignature(
        ReadOnlySpan<Format> colorFormats,
        Format depthAttachmentFormat,
        Format stencilAttachmentFormat,
        uint viewMask = 0u,
        uint layerCount = 1u)
    {
        if ((uint)colorFormats.Length > MaxColorAttachmentCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(colorFormats),
                colorFormats.Length,
                $"Dynamic rendering supports at most {MaxColorAttachmentCount} engine color attachments per target.");
        }

        ColorFormatStorage storage = default;
        for (int i = 0; i < colorFormats.Length; i++)
            storage[i] = colorFormats[i];

        _colorFormats = storage;
        _colorAttachmentCount = checked((byte)colorFormats.Length);
        DepthAttachmentFormat = depthAttachmentFormat;
        StencilAttachmentFormat = stencilAttachmentFormat;
        ViewMask = viewMask;
        LayerCount = VulkanDynamicRenderingUtilities.ResolveLayerCount(layerCount, viewMask);
    }

    public uint ColorAttachmentCount => _colorAttachmentCount;
    public Format DepthAttachmentFormat { get; }
    public Format StencilAttachmentFormat { get; }
    public uint ViewMask { get; }
    public uint LayerCount { get; }
    public Format FirstColorAttachmentFormat => _colorAttachmentCount > 0 ? _colorFormats[0] : Format.Undefined;

    public Format GetColorAttachmentFormat(uint index)
    {
        if (index >= _colorAttachmentCount)
            return Format.Undefined;

        return _colorFormats[(int)index];
    }

    public void CopyColorAttachmentFormats(Format* destination, uint count)
    {
        if (destination is null || count == 0)
            return;

        uint available = Math.Min(count, ColorAttachmentCount);
        for (uint i = 0; i < available; i++)
            destination[i] = GetColorAttachmentFormat(i);
    }

    public string DescribeColorFormats()
    {
        if (_colorAttachmentCount == 0)
            return Format.Undefined.ToString();

        System.Text.StringBuilder builder = new();
        builder.Append(GetColorAttachmentFormat(0));
        for (uint i = 1; i < _colorAttachmentCount; i++)
            builder.Append(',').Append(GetColorAttachmentFormat(i));
        return builder.ToString();
    }

    public bool Equals(DynamicRenderingFormatSignature other)
    {
        if (DepthAttachmentFormat != other.DepthAttachmentFormat ||
            StencilAttachmentFormat != other.StencilAttachmentFormat ||
            ViewMask != other.ViewMask ||
            LayerCount != other.LayerCount ||
            ColorAttachmentCount != other.ColorAttachmentCount)
        {
            return false;
        }

        uint count = ColorAttachmentCount;
        for (uint i = 0; i < count; i++)
        {
            if (GetColorAttachmentFormat(i) != other.GetColorAttachmentFormat(i))
                return false;
        }

        return true;
    }

    public override bool Equals(object? obj)
        => obj is DynamicRenderingFormatSignature other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        uint count = ColorAttachmentCount;
        hash.Add(count);
        for (uint i = 0; i < count; i++)
            hash.Add((int)GetColorAttachmentFormat(i));
        hash.Add((int)DepthAttachmentFormat);
        hash.Add((int)StencilAttachmentFormat);
        hash.Add(ViewMask);
        hash.Add(LayerCount);
        return hash.ToHashCode();
    }
}
