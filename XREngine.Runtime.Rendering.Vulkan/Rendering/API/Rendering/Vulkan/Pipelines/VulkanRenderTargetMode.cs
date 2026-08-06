using System;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using XREngine;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const string VulkanRenderTargetModeEnvVar = XREngineEnvironmentVariables.VkRenderTargetMode;

    internal bool UseDynamicRenderingRenderTargets => _deviceContext.MutableCapabilities._useDynamicRenderingRenderTargets;
    public EVulkanRenderTargetMode RequestedRenderTargetMode => _outputRuntime._requestedRenderTargetMode;
    public EVulkanRenderTargetMode EffectiveRenderTargetMode => _deviceContext.MutableCapabilities._useDynamicRenderingRenderTargets
        ? EVulkanRenderTargetMode.DynamicRendering
        : EVulkanRenderTargetMode.LegacyRenderPass;

    private void ResolveRenderTargetMode()
    {
        _outputRuntime._requestedRenderTargetMode = ResolveRequestedRenderTargetMode();

        if (_outputRuntime._requestedRenderTargetMode == EVulkanRenderTargetMode.DynamicRendering && !SupportsDynamicRendering)
        {
            throw new InvalidOperationException(
                $"Vulkan dynamic rendering was explicitly requested by render settings or {VulkanRenderTargetModeEnvVar}=DynamicRendering, but VK_KHR_dynamic_rendering/Vulkan 1.3 dynamicRendering is unavailable.");
        }

        _deviceContext.MutableCapabilities._useDynamicRenderingRenderTargets = _outputRuntime._requestedRenderTargetMode switch
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

        Span<Format> colorFormats = colorCount == 0
            ? []
            : stackalloc Format[colorCount];
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
