using System;
using Silk.NET.Vulkan;
using XREngine;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const string VulkanRenderTargetModeEnvVar = XREngineEnvironmentVariables.VkRenderTargetMode;

    private EVulkanRenderTargetMode _requestedRenderTargetMode = EVulkanRenderTargetMode.Auto;
    private bool _useDynamicRenderingRenderTargets;

    internal bool UseDynamicRenderingRenderTargets => _useDynamicRenderingRenderTargets;

    private void ResolveRenderTargetMode()
    {
        _requestedRenderTargetMode = ResolveRequestedRenderTargetMode();

        if (_requestedRenderTargetMode == EVulkanRenderTargetMode.DynamicRendering && !SupportsDynamicRendering)
        {
            throw new InvalidOperationException(
                $"Vulkan dynamic rendering was explicitly requested by render settings or {VulkanRenderTargetModeEnvVar}=DynamicRendering, but VK_KHR_dynamic_rendering/Vulkan 1.3 dynamicRendering is unavailable.");
        }

        _useDynamicRenderingRenderTargets = _requestedRenderTargetMode switch
        {
            EVulkanRenderTargetMode.LegacyRenderPass => false,
            EVulkanRenderTargetMode.DynamicRendering => true,
            _ => SupportsDynamicRendering,
        };
    }

    private static EVulkanRenderTargetMode ResolveRequestedRenderTargetMode()
    {
        string? envValue = Environment.GetEnvironmentVariable(VulkanRenderTargetModeEnvVar);
        return string.IsNullOrWhiteSpace(envValue)
            ? RuntimeEngine.EffectiveSettings.VulkanRenderTargetMode
            : ParseRenderTargetMode(envValue);
    }

    private static EVulkanRenderTargetMode ParseRenderTargetMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return EVulkanRenderTargetMode.Auto;

        return value.Trim().ToLowerInvariant() switch
        {
            "auto" => EVulkanRenderTargetMode.Auto,
            "dynamic" or "dynamicrendering" or "dynamic-rendering" => EVulkanRenderTargetMode.DynamicRendering,
            "legacy" or "renderpass" or "render-pass" or "legacyrenderpass" or "legacy-render-pass" => EVulkanRenderTargetMode.LegacyRenderPass,
            _ => throw new InvalidOperationException(
                $"Unsupported {VulkanRenderTargetModeEnvVar} value '{value}'. Expected Auto, DynamicRendering, or LegacyRenderPass."),
        };
    }

    internal readonly struct DynamicRenderingFormatSignature : IEquatable<DynamicRenderingFormatSignature>
    {
        private readonly Format[]? _colorFormats;

        public DynamicRenderingFormatSignature(
            ReadOnlySpan<Format> colorFormats,
            Format depthAttachmentFormat,
            Format stencilAttachmentFormat,
            uint viewMask = 0u,
            uint layerCount = 1u)
        {
            _colorFormats = colorFormats.Length == 0 ? null : colorFormats.ToArray();
            DepthAttachmentFormat = depthAttachmentFormat;
            StencilAttachmentFormat = stencilAttachmentFormat;
            ViewMask = viewMask;
            LayerCount = ResolveDynamicRenderingLayerCount(layerCount, viewMask);
        }

        public uint ColorAttachmentCount => (uint)(_colorFormats?.Length ?? 0);
        public Format DepthAttachmentFormat { get; }
        public Format StencilAttachmentFormat { get; }
        public uint ViewMask { get; }
        public uint LayerCount { get; }
        public Format FirstColorAttachmentFormat => _colorFormats is { Length: > 0 } ? _colorFormats[0] : Format.Undefined;

        public Format GetColorAttachmentFormat(uint index)
        {
            if (_colorFormats is null || index >= _colorFormats.Length)
                return Format.Undefined;

            return _colorFormats[index];
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
            if (_colorFormats is null || _colorFormats.Length == 0)
                return Format.Undefined.ToString();

            return string.Join(",", _colorFormats);
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

    private static uint ResolveDynamicRenderingLayerCount(uint framebufferLayers, uint viewMask)
        => viewMask == 0u ? Math.Max(framebufferLayers, 1u) : 1u;

    private static DynamicRenderingFormatSignature CreateSwapchainDynamicRenderingFormatSignature(Format colorFormat, Format depthFormat)
    {
        Span<Format> colorFormats = stackalloc Format[1];
        colorFormats[0] = colorFormat;

        return new DynamicRenderingFormatSignature(
            colorFormats,
            depthFormat,
            HasStencilComponent(depthFormat) ? depthFormat : Format.Undefined);
    }

    private static DynamicRenderingFormatSignature CreateSwapchainColorOnlyDynamicRenderingFormatSignature(Format colorFormat)
    {
        Span<Format> colorFormats = stackalloc Format[1];
        colorFormats[0] = colorFormat;

        return new DynamicRenderingFormatSignature(
            colorFormats,
            Format.Undefined,
            Format.Undefined);
    }

    private static DynamicRenderingFormatSignature CreateDynamicRenderingFormatSignature(
        FrameBufferAttachmentSignature[] signatures,
        uint viewMask = 0u,
        uint layerCount = 1u)
    {
        int colorCount = 0;
        Format depthFormat = Format.Undefined;
        Format stencilFormat = Format.Undefined;

        for (int i = 0; i < signatures.Length; i++)
        {
            FrameBufferAttachmentSignature signature = signatures[i];
            if (signature.Role == AttachmentRole.Color)
            {
                colorCount++;
                continue;
            }

            if ((signature.AspectMask & ImageAspectFlags.DepthBit) != 0)
                depthFormat = signature.Format;
            if ((signature.AspectMask & ImageAspectFlags.StencilBit) != 0)
                stencilFormat = signature.Format;
        }

        Format[] colorFormats = colorCount == 0 ? [] : new Format[colorCount];
        int colorIndex = 0;
        for (int i = 0; i < signatures.Length; i++)
        {
            FrameBufferAttachmentSignature signature = signatures[i];
            if (signature.Role == AttachmentRole.Color)
                colorFormats[colorIndex++] = signature.Format;
        }

        return new DynamicRenderingFormatSignature(colorFormats, depthFormat, stencilFormat, viewMask, layerCount);
    }

    private static string BuildDynamicRenderingSignature(in DynamicRenderingFormatSignature signature)
        => string.Join(
            "|",
            "RenderPass:DynamicRendering",
            $"colors={signature.DescribeColorFormats()}",
            $"depth={signature.DepthAttachmentFormat}",
            $"stencil={signature.StencilAttachmentFormat}",
            $"viewMask=0x{signature.ViewMask:X}",
            $"layers={signature.LayerCount}");
}
