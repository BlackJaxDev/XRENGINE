using System;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using XREngine;

namespace XREngine.Rendering.Vulkan;

public unsafe partial class VulkanRenderer
{
    private const string VulkanRenderTargetModeEnvVar = XREngineEnvironmentVariables.VkRenderTargetMode;

    private EVulkanRenderTargetMode _requestedRenderTargetMode = EVulkanRenderTargetMode.Auto;
    private bool _useDynamicRenderingRenderTargets;

    internal bool UseDynamicRenderingRenderTargets => _useDynamicRenderingRenderTargets;
    public EVulkanRenderTargetMode RequestedRenderTargetMode => _requestedRenderTargetMode;
    public EVulkanRenderTargetMode EffectiveRenderTargetMode => _useDynamicRenderingRenderTargets
        ? EVulkanRenderTargetMode.DynamicRendering
        : EVulkanRenderTargetMode.LegacyRenderPass;

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
            LayerCount = ResolveDynamicRenderingLayerCount(layerCount, viewMask);
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

    internal readonly struct DynamicRenderingAttachmentPlan
    {
        public DynamicRenderingAttachmentPlan(
            Image image,
            ImageView imageView,
            Format format,
            ImageAspectFlags aspectMask,
            ImageLayout initialLayout,
            ImageLayout renderingLayout,
            ImageLayout finalLayout,
            AttachmentLoadOp loadOp,
            AttachmentStoreOp storeOp,
            ClearValue clearValue,
            ImageView resolveImageView = default,
            ResolveModeFlags resolveMode = default,
            ImageLayout resolveImageLayout = ImageLayout.Undefined)
        {
            Image = image;
            ImageView = imageView;
            Format = format;
            AspectMask = aspectMask;
            InitialLayout = initialLayout;
            RenderingLayout = renderingLayout;
            FinalLayout = finalLayout;
            LoadOp = loadOp;
            StoreOp = storeOp;
            ClearValue = clearValue;
            ResolveImageView = resolveImageView;
            ResolveMode = resolveMode;
            ResolveImageLayout = resolveImageLayout;
        }

        public Image Image { get; }
        public ImageView ImageView { get; }
        public Format Format { get; }
        public ImageAspectFlags AspectMask { get; }
        public ImageLayout InitialLayout { get; }
        public ImageLayout RenderingLayout { get; }
        public ImageLayout FinalLayout { get; }
        public AttachmentLoadOp LoadOp { get; }
        public AttachmentStoreOp StoreOp { get; }
        public ClearValue ClearValue { get; }
        public ImageView ResolveImageView { get; }
        public ResolveModeFlags ResolveMode { get; }
        public ImageLayout ResolveImageLayout { get; }
        public bool HasResolveAttachment => ResolveMode != default && ResolveImageView.Handle != 0;

        public DynamicRenderingAttachmentPlan WithResolve(in DynamicRenderingAttachmentPlan resolveAttachment, ResolveModeFlags resolveMode)
            => new(
                Image,
                ImageView,
                Format,
                AspectMask,
                InitialLayout,
                RenderingLayout,
                FinalLayout,
                LoadOp,
                StoreOp,
                ClearValue,
                resolveAttachment.ImageView,
                resolveMode,
                resolveAttachment.RenderingLayout);

        public RenderingAttachmentInfo ToRenderingAttachmentInfo()
            => new()
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = ImageView,
                ImageLayout = RenderingLayout,
                ResolveMode = ResolveMode,
                ResolveImageView = ResolveImageView,
                ResolveImageLayout = ResolveImageLayout,
                LoadOp = LoadOp,
                StoreOp = StoreOp,
                ClearValue = ClearValue,
            };
    }

    internal readonly ref struct DynamicRenderingLocalReadPlan
    {
        public DynamicRenderingLocalReadPlan(
            ReadOnlySpan<uint> colorAttachmentLocations,
            ReadOnlySpan<uint> colorInputAttachmentIndices,
            uint? depthInputAttachmentIndex = null,
            uint? stencilInputAttachmentIndex = null)
        {
            ColorAttachmentLocations = colorAttachmentLocations;
            ColorInputAttachmentIndices = colorInputAttachmentIndices;
            DepthInputAttachmentIndex = depthInputAttachmentIndex;
            StencilInputAttachmentIndex = stencilInputAttachmentIndex;
        }

        public ReadOnlySpan<uint> ColorAttachmentLocations { get; }
        public ReadOnlySpan<uint> ColorInputAttachmentIndices { get; }
        public uint? DepthInputAttachmentIndex { get; }
        public uint? StencilInputAttachmentIndex { get; }
        public bool Enabled =>
            ColorAttachmentLocations.Length > 0 ||
            ColorInputAttachmentIndices.Length > 0 ||
            DepthInputAttachmentIndex.HasValue ||
            StencilInputAttachmentIndex.HasValue;
    }

    internal readonly ref struct DynamicRenderingScopePlan
    {
        public DynamicRenderingScopePlan(
            Rect2D renderArea,
            uint layerCount,
            uint viewMask,
            ReadOnlySpan<DynamicRenderingAttachmentPlan> colorAttachments,
            DynamicRenderingAttachmentPlan depthAttachment,
            bool hasDepthAttachment,
            DynamicRenderingAttachmentPlan stencilAttachment,
            bool hasStencilAttachment,
            bool depthStencilReadOnly,
            DynamicRenderingFormatSignature formatSignature,
            SampleCountFlags sampleCount)
            : this(
                renderArea,
                layerCount,
                viewMask,
                colorAttachments,
                depthAttachment,
                hasDepthAttachment,
                stencilAttachment,
                hasStencilAttachment,
                depthStencilReadOnly,
                formatSignature,
                sampleCount,
                default)
        {
        }

        public DynamicRenderingScopePlan(
            Rect2D renderArea,
            uint layerCount,
            uint viewMask,
            ReadOnlySpan<DynamicRenderingAttachmentPlan> colorAttachments,
            DynamicRenderingAttachmentPlan depthAttachment,
            bool hasDepthAttachment,
            DynamicRenderingAttachmentPlan stencilAttachment,
            bool hasStencilAttachment,
            bool depthStencilReadOnly,
            DynamicRenderingFormatSignature formatSignature,
            SampleCountFlags sampleCount,
            DynamicRenderingLocalReadPlan localRead)
        {
            RenderArea = renderArea;
            LayerCount = ResolveDynamicRenderingLayerCount(layerCount, viewMask);
            ViewMask = viewMask;
            ColorAttachments = colorAttachments;
            DepthAttachment = depthAttachment;
            HasDepthAttachment = hasDepthAttachment;
            StencilAttachment = stencilAttachment;
            HasStencilAttachment = hasStencilAttachment;
            DepthStencilReadOnly = depthStencilReadOnly;
            FormatSignature = formatSignature;
            SampleCount = sampleCount;
            LocalRead = localRead;
        }

        public Rect2D RenderArea { get; }
        public uint LayerCount { get; }
        public uint ViewMask { get; }
        public ReadOnlySpan<DynamicRenderingAttachmentPlan> ColorAttachments { get; }
        public DynamicRenderingAttachmentPlan DepthAttachment { get; }
        public bool HasDepthAttachment { get; }
        public DynamicRenderingAttachmentPlan StencilAttachment { get; }
        public bool HasStencilAttachment { get; }
        public bool DepthStencilReadOnly { get; }
        public DynamicRenderingFormatSignature FormatSignature { get; }
        public DynamicRenderingFormatSignature SemanticSignature => FormatSignature;
        public SampleCountFlags SampleCount { get; }
        public DynamicRenderingLocalReadPlan LocalRead { get; }
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
