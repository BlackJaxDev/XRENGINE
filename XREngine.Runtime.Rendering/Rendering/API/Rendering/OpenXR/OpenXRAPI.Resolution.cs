using System;
using System.Threading;
using XREngine;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;
using Debug = XREngine.Debug;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    private readonly record struct OpenXrEyeResolutionSettingsSnapshot(
        EOpenXrEyeResolutionPreset Preset,
        float Scale,
        uint CustomWidth,
        uint CustomHeight)
    {
        public override string ToString()
            => $"{Preset} scale={Scale:F2} custom={CustomWidth}x{CustomHeight}";
    }

    internal readonly record struct OpenXrEyeSwapchainExtent(
        uint Width,
        uint Height,
        uint RequestedWidth,
        uint RequestedHeight,
        uint BaseWidth,
        uint BaseHeight,
        uint RecommendedWidth,
        uint RecommendedHeight,
        uint MaxWidth,
        uint MaxHeight,
        EOpenXrEyeResolutionPreset Preset,
        float Scale,
        bool ExceedsRuntimeMax,
        string Source);

    internal static OpenXrEyeSwapchainExtent ResolveOpenXrEyeSwapchainExtentForSettings(
        EOpenXrEyeResolutionPreset preset,
        float scale,
        uint customWidth,
        uint customHeight,
        uint recommendedWidth,
        uint recommendedHeight,
        uint maxWidth,
        uint maxHeight)
    {
        float requiredScale = RequireOpenXrEyeResolutionScale(scale);

        uint baseWidth;
        uint baseHeight;
        string source;
        switch (preset)
        {
            case EOpenXrEyeResolutionPreset.RuntimeRecommended:
                if (recommendedWidth == 0u || recommendedHeight == 0u)
                {
                    throw new InvalidOperationException(
                        $"OpenXR runtime recommended eye resolution is invalid: {recommendedWidth}x{recommendedHeight}.");
                }

                baseWidth = recommendedWidth;
                baseHeight = recommendedHeight;
                source = "OpenXR runtime recommended";
                break;
            case EOpenXrEyeResolutionPreset.ValveIndex:
                baseWidth = 1440u;
                baseHeight = 1600u;
                source = "Valve Index";
                break;
            case EOpenXrEyeResolutionPreset.QuestPro:
                baseWidth = 1800u;
                baseHeight = 1920u;
                source = "Quest Pro";
                break;
            case EOpenXrEyeResolutionPreset.BigscreenBeyond2:
                baseWidth = 2560u;
                baseHeight = 2560u;
                source = "Bigscreen Beyond 2";
                break;
            case EOpenXrEyeResolutionPreset.Custom:
                if (customWidth == 0u || customHeight == 0u)
                {
                    throw new InvalidOperationException(
                        $"OpenXR custom eye resolution requires non-zero CustomWidth and CustomHeight, got {customWidth}x{customHeight}.");
                }

                baseWidth = customWidth;
                baseHeight = customHeight;
                source = "Custom";
                break;
            default:
                throw new InvalidOperationException($"Unsupported OpenXR eye resolution preset '{preset}'.");
        }

        uint requestedWidth = ScaleDimension(baseWidth, requiredScale);
        uint requestedHeight = ScaleDimension(baseHeight, requiredScale);
        uint resolvedWidth = requestedWidth;
        uint resolvedHeight = requestedHeight;
        bool clampedWidth = maxWidth > 0 && resolvedWidth > maxWidth;
        bool clampedHeight = maxHeight > 0 && resolvedHeight > maxHeight;
        if (clampedWidth)
            resolvedWidth = maxWidth;
        if (clampedHeight)
            resolvedHeight = maxHeight;

        bool exceedsRuntimeMax =
            clampedWidth ||
            clampedHeight;

        return new(
            resolvedWidth,
            resolvedHeight,
            requestedWidth,
            requestedHeight,
            baseWidth,
            baseHeight,
            recommendedWidth,
            recommendedHeight,
            maxWidth,
            maxHeight,
            preset,
            requiredScale,
            exceedsRuntimeMax,
            source);
    }

    private OpenXrEyeSwapchainExtent ResolveOpenXrEyeSwapchainExtent(uint viewIndex)
    {
        int index = (int)Math.Min(viewIndex, (uint)_viewConfigViews.Length - 1u);
        var viewConfig = _viewConfigViews[index];
        IRuntimeRenderingHostServices settings = RuntimeRenderingHostServices.Current;
        return ResolveOpenXrEyeSwapchainExtentForSettings(
            settings.OpenXrEyeResolutionPreset,
            settings.OpenXrEyeResolutionScale,
            settings.OpenXrCustomEyeResolutionWidth,
            settings.OpenXrCustomEyeResolutionHeight,
            viewConfig.RecommendedImageRectWidth,
            viewConfig.RecommendedImageRectHeight,
            viewConfig.MaxImageRectWidth,
            viewConfig.MaxImageRectHeight);
    }

    private uint GetOpenXrSwapchainWidth(uint viewIndex)
    {
        int index = (int)Math.Min(viewIndex, (uint)_swapchainWidths.Length - 1u);
        uint width = _swapchainWidths[index];
        return width != 0 ? width : ResolveOpenXrEyeSwapchainExtent(viewIndex).Width;
    }

    private uint GetOpenXrSwapchainHeight(uint viewIndex)
    {
        int index = (int)Math.Min(viewIndex, (uint)_swapchainHeights.Length - 1u);
        uint height = _swapchainHeights[index];
        return height != 0 ? height : ResolveOpenXrEyeSwapchainExtent(viewIndex).Height;
    }

    private void RecordOpenXrSwapchainExtent(uint viewIndex, uint width, uint height)
    {
        int index = (int)Math.Min(viewIndex, (uint)_swapchainWidths.Length - 1u);
        _swapchainWidths[index] = width;
        _swapchainHeights[index] = height;
        RecordAppliedOpenXrEyeResolutionSettings();
    }

    private void SubscribeOpenXrRenderSettingsChanged()
    {
        if (_renderSettingsChangedSubscribed)
            return;

        RuntimeEngine.Rendering.SettingsChanged += HandleOpenXrRenderSettingsChanged;
        _renderSettingsChangedSubscribed = true;
    }

    private void UnsubscribeOpenXrRenderSettingsChanged()
    {
        if (!_renderSettingsChangedSubscribed)
            return;

        RuntimeEngine.Rendering.SettingsChanged -= HandleOpenXrRenderSettingsChanged;
        _renderSettingsChangedSubscribed = false;
    }

    private void HandleOpenXrRenderSettingsChanged()
    {
        OpenXrEyeResolutionSettingsSnapshot current = CaptureCurrentOpenXrEyeResolutionSettings();
        OpenXrEyeResolutionSettingsSnapshot applied = CaptureAppliedOpenXrEyeResolutionSettings();
        if (OpenXrEyeResolutionSettingsMatch(current, applied))
            return;

        if (!_runtimeMonitoringEnabled)
        {
            Debug.Out($"[OpenXR] Eye resolution settings changed from {applied} to {current}; runtime monitoring is disabled, so the next OpenXR startup will apply the new extent.");
            return;
        }

        if (!HasCreatedOpenXrSwapchains())
        {
            Debug.Out($"[OpenXR] Eye resolution settings changed from {applied} to {current}; no OpenXR swapchains exist yet, so the next session creation will apply the new extent.");
            return;
        }

        QueueOpenXrEyeResolutionSessionRecreate(current, applied);
    }

    private void QueueOpenXrEyeResolutionSessionRecreate(
        OpenXrEyeResolutionSettingsSnapshot current,
        OpenXrEyeResolutionSettingsSnapshot applied)
    {
        if (Interlocked.Exchange(ref _openXrEyeResolutionRecreateQueued, 1) != 0)
            return;

        string reason = $"OpenXR eye resolution changed from {applied} to {current}.";
        Debug.LogWarning($"[OpenXR] {reason} Recreating OpenXR instance/session resources so the runtime receives new swapchain and view-configuration dimensions.");

        bool scheduled = false;
        try
        {
            RuntimeRenderingHostServices.Current.InvokeRenderThreadTask(
                () =>
                {
                    try
                    {
                        RecreateOpenXrSessionResourcesForEyeResolution(reason);
                        return true;
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _openXrEyeResolutionRecreateQueued, 0);
                    }
                },
                "OpenXR.EyeResolution.RecreateSessionResources",
                RenderThreadJobKind.RequiresGraphicsContext);
            scheduled = true;
        }
        finally
        {
            if (!scheduled)
                Interlocked.Exchange(ref _openXrEyeResolutionRecreateQueued, 0);
        }
    }

    private void RecreateOpenXrSessionResourcesForEyeResolution(string reason)
    {
        if (!_runtimeMonitoringEnabled)
            return;

        if (!HasCreatedOpenXrSwapchains())
            return;

        Debug.Out($"[OpenXR] Recreating session resources for eye resolution change. Reason={reason}");
        _intentionalOpenXrRecreateBackoffBypassUntilUtc =
            DateTime.UtcNow + _intentionalOpenXrRecreateBackoffBypassDuration;
        Volatile.Write(ref _pendingXrFrame, 0);
        Volatile.Write(ref _pendingXrFrameCollected, 0);
        Volatile.Write(ref _framePrepared, 0);
        Volatile.Write(ref _frameSkipRender, 0);
        _sessionBegun = false;

        if (Window?.Renderer is VulkanRenderer vulkanRenderer)
            vulkanRenderer.ResetOpenXrRenderingResourcesForRuntimeRecreate(reason);

        TearDownSessionResourcesWithCurrentContext(destroyInstance: true);
        string serviceReason = $"OpenXR eye resolution change: {reason}";
        if (!RuntimeRenderingHostServices.Current.TryEnsureOpenXrRuntimeService(serviceReason))
        {
            throw new InvalidOperationException(
                $"OpenXR runtime service did not accept the requested eye-resolution change. Reason={reason}");
        }

        Debug.Out($"OpenXR runtime service ensured. Reason={serviceReason}");
        ResetOpenXrProbeFailureState();
        _nextProbeUtc = DateTime.UtcNow;
        SetRuntimeState(OpenXrRuntimeState.DesktopOnly);
    }

    private bool HasCreatedOpenXrSwapchains()
    {
        for (int i = 0; i < _swapchains.Length; i++)
        {
            if (_swapchains[i].Handle != 0)
                return true;
        }

        return false;
    }

    private void RecordAppliedOpenXrEyeResolutionSettings()
    {
        OpenXrEyeResolutionSettingsSnapshot current = CaptureCurrentOpenXrEyeResolutionSettings();
        _appliedOpenXrEyeResolutionPreset = current.Preset;
        _appliedOpenXrEyeResolutionScale = current.Scale;
        _appliedOpenXrCustomEyeResolutionWidth = current.CustomWidth;
        _appliedOpenXrCustomEyeResolutionHeight = current.CustomHeight;
    }

    private OpenXrEyeResolutionSettingsSnapshot CaptureCurrentOpenXrEyeResolutionSettings()
    {
        IRuntimeRenderingHostServices settings = RuntimeRenderingHostServices.Current;
        return new(
            settings.OpenXrEyeResolutionPreset,
            NormalizeOpenXrEyeResolutionScale(settings.OpenXrEyeResolutionScale),
            settings.OpenXrCustomEyeResolutionWidth,
            settings.OpenXrCustomEyeResolutionHeight);
    }

    private OpenXrEyeResolutionSettingsSnapshot CaptureAppliedOpenXrEyeResolutionSettings()
        => new(
            _appliedOpenXrEyeResolutionPreset,
            NormalizeOpenXrEyeResolutionScale(_appliedOpenXrEyeResolutionScale),
            _appliedOpenXrCustomEyeResolutionWidth,
            _appliedOpenXrCustomEyeResolutionHeight);

    private static bool OpenXrEyeResolutionSettingsMatch(
        OpenXrEyeResolutionSettingsSnapshot left,
        OpenXrEyeResolutionSettingsSnapshot right)
        => left.Preset == right.Preset &&
           Math.Abs(left.Scale - right.Scale) <= 0.0001f &&
           left.CustomWidth == right.CustomWidth &&
           left.CustomHeight == right.CustomHeight;

    private static float NormalizeOpenXrEyeResolutionScale(float scale)
        => RequireOpenXrEyeResolutionScale(scale);

    private static float RequireOpenXrEyeResolutionScale(float scale)
    {
        if (!float.IsFinite(scale) || scale < 0.1f || scale > 2.0f)
        {
            throw new InvalidOperationException(
                $"OpenXR eye resolution scale must be finite and in the inclusive range [0.1, 2.0], got {scale}.");
        }

        return scale;
    }

    private void LogOpenXrEyeSwapchainExtent(string backend, uint viewIndex, OpenXrEyeSwapchainExtent extent)
    {
        string maxText = extent.MaxWidth > 0 && extent.MaxHeight > 0
            ? $"{extent.MaxWidth}x{extent.MaxHeight}"
            : "<unspecified>";
        Debug.Out(
            $"OpenXR {backend} view[{viewIndex}] swapchain size: {extent.Width}x{extent.Height} " +
            $"source={extent.Source} preset={extent.Preset} scale={extent.Scale:F2} " +
            $"requested={extent.RequestedWidth}x{extent.RequestedHeight} base={extent.BaseWidth}x{extent.BaseHeight} recommended={extent.RecommendedWidth}x{extent.RecommendedHeight} " +
            $"max={maxText} exceedsRuntimeMax={extent.ExceedsRuntimeMax}");

        if (!extent.ExceedsRuntimeMax)
            return;

        Debug.LogWarning(
            $"[OpenXR] {backend} eye {viewIndex} requested {extent.RequestedWidth}x{extent.RequestedHeight} from {extent.Source} at {extent.Scale:F2}x, " +
            $"which exceeds reported runtime max {maxText}. Clamping swapchain extent to {extent.Width}x{extent.Height}.");
    }

    private static uint ScaleDimension(uint value, float scale)
    {
        double scaled = Math.Round(value * (double)scale, MidpointRounding.AwayFromZero);
        if (scaled < 1.0)
            return 1u;
        if (scaled > uint.MaxValue)
        {
            throw new OverflowException(
                $"OpenXR eye resolution dimension {value} scaled by {scale} exceeds UInt32.MaxValue.");
        }

        return (uint)scaled;
    }
}
