using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.KHR;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using XREngine.Data.Colors;
using XREngine.Rendering;
using XREngine.Rendering.Vulkan;
using Debug = XREngine.Debug;
using VkExtent2D = Silk.NET.Vulkan.Extent2D;
using VkFormat = Silk.NET.Vulkan.Format;
using VkImage = Silk.NET.Vulkan.Image;

namespace XREngine.Rendering.API.Rendering.OpenXR;

internal sealed class OpenXrGraphicsSessionException(Result result, string message) : Exception(message)
{
    public Result Result { get; } = result;
}

public unsafe partial class OpenXRAPI
{
    private static readonly long[] VulkanSwapchainFormatPreferences =
    [
        (long)VkFormat.B8G8R8A8Srgb,
        (long)VkFormat.R8G8B8A8Srgb,
        (long)VkFormat.B8G8R8A8Unorm,
        (long)VkFormat.R8G8B8A8Unorm,
    ];

    private readonly long[] _vulkanOpenXrSwapchainFormats = new long[2];
    private readonly SwapchainUsageFlags[] _vulkanOpenXrSwapchainUsages = new SwapchainUsageFlags[2];

    /// <summary>
    /// Creates an OpenXR session using Vulkan graphics binding.
    /// </summary>
    /// <exception cref="Exception">Thrown when session creation fails.</exception>
    internal void CreateVulkanSession()
    {
        if (Window is null)
            throw new Exception("Window is null");

        var requirements = new GraphicsRequirementsVulkanKHR
        {
            Type = StructureType.GraphicsRequirementsVulkanKhr
        };

        if (Window.Renderer is not VulkanRenderer renderer)
            throw new Exception("Renderer is not a VulkanRenderer.");

        KhrVulkanEnable? vulkanExtension = null;
        string? vulkanLoadError = null;
        try
        {
            if (!Api.TryGetInstanceExtension<KhrVulkanEnable>(null, _instance, out var loadedVulkan))
                vulkanLoadError = "XR_KHR_vulkan_enable was not returned by the Silk.NET extension loader.";
            else
                vulkanExtension = loadedVulkan;
        }
        catch (Exception ex)
        {
            vulkanLoadError = ex.Message;
        }

        KhrVulkanEnable2? vulkan2Extension = null;
        string? vulkan2LoadError = null;
        try
        {
            if (!Api.TryGetInstanceExtension<KhrVulkanEnable2>(null, _instance, out var loadedVulkan2))
                vulkan2LoadError = "XR_KHR_vulkan_enable2 was not returned by the Silk.NET extension loader.";
            else
                vulkan2Extension = loadedVulkan2;
        }
        catch (Exception ex)
        {
            vulkan2LoadError = ex.Message;
        }

        bool useEnable2Binding = renderer.UsesOpenXrVulkanEnable2Creation;
        if (useEnable2Binding && vulkan2Extension is null)
            throw new Exception($"Vulkan renderer was created through XR_KHR_vulkan_enable2, but the OpenXR session instance could not load it: {vulkan2LoadError}");

        if (!useEnable2Binding && vulkanExtension is null && vulkan2Extension is not null)
            useEnable2Binding = true;

        if (useEnable2Binding && vulkan2Extension is not null)
        {
            if (vulkan2Extension.GetVulkanGraphicsRequirements2(_instance, _systemId, ref requirements) != Result.Success)
                throw new Exception("Failed to get Vulkan graphics requirements through XR_KHR_vulkan_enable2");
        }
        else if (vulkanExtension is not null)
        {
            if (vulkanExtension.GetVulkanGraphicsRequirements(_instance, _systemId, ref requirements) != Result.Success)
                throw new Exception("Failed to get Vulkan graphics requirements through XR_KHR_vulkan_enable");
        }
        else
        {
            throw new Exception($"Failed to get Vulkan OpenXR extension. XR_KHR_vulkan_enable2: {vulkan2LoadError ?? "<not tried>"}; XR_KHR_vulkan_enable: {vulkanLoadError ?? "<not tried>"}");
        }

        Debug.Vulkan($"Vulkan requirements: Min {requirements.MinApiVersionSupported}, Max {requirements.MaxApiVersionSupported}");

        // Get the primary graphics queue family index
        var graphicsFamilyIndex = renderer.FamilyQueueIndices.GraphicsFamilyIndex!.Value;

        // Check if multiple graphics queues are supported
        bool supportsMultiQueue = renderer.SupportsMultipleGraphicsQueues();
        bool projectAllowsParallel = (RuntimeEngine.GameSettings as IVRGameStartupSettings)?.EnableOpenXrVulkanParallelRendering ?? true;

        if (supportsMultiQueue && projectAllowsParallel)
        {
            Debug.Vulkan("Multiple graphics queues are supported - enabling parallel eye rendering");
            _parallelRenderingEnabled = true;
        }
        else
        {
            if (!projectAllowsParallel)
                Debug.Vulkan("OpenXR Vulkan parallel eye rendering disabled by game startup settings.");
            else
                Debug.Vulkan("Multiple graphics queues not supported - using single queue rendering");

            _parallelRenderingEnabled = false;
        }

        GraphicsBindingVulkanKHR vkBinding = default;
        void* graphicsBinding = null;
        if ((useEnable2Binding && vulkan2Extension is not null) || vulkanExtension is not null)
        {
            // XR_KHR_vulkan_enable2 aliases XrGraphicsBindingVulkan2KHR to XrGraphicsBindingVulkanKHR.
            // Silk.NET 2.23 exposes a distinct StructureType for Vulkan2; Monado expects the aliased KHR type.
            vkBinding = new GraphicsBindingVulkanKHR
            {
                Type = StructureType.GraphicsBindingVulkanKhr,
                Instance = new(renderer.Instance.Handle),
                PhysicalDevice = new(renderer.PhysicalDevice.Handle),
                Device = new(renderer.Device.Handle),
                QueueFamilyIndex = graphicsFamilyIndex,
                QueueIndex = 0 // Main queue for session
            };
            graphicsBinding = &vkBinding;
        }

        var createInfo = new SessionCreateInfo
        {
            Type = StructureType.SessionCreateInfo,
            SystemId = _systemId,
            Next = graphicsBinding
        };
        var result = CheckResult(Api.CreateSession(_instance, ref createInfo, ref _session), "xrCreateSession");
        if (result != Result.Success)
        {
            string bindingExtension = useEnable2Binding ? "XR_KHR_vulkan_enable2" : "XR_KHR_vulkan_enable";
            string message = $"Failed to create Vulkan OpenXR session: {result}. " +
                $"BindingExtension={bindingExtension}, XrSystemId={_systemId}, " +
                $"VkInstance={renderer.Instance.Handle}, VkPhysicalDevice={renderer.PhysicalDevice.Handle}, VkDevice={renderer.Device.Handle}, " +
                $"QueueFamilyIndex={graphicsFamilyIndex}, QueueIndex=0, " +
                $"RuntimeMinVulkan={requirements.MinApiVersionSupported}, RuntimeMaxVulkan={requirements.MaxApiVersionSupported}.";

            if (result == Result.ErrorGraphicsDeviceInvalid)
                message += " The OpenXR runtime rejected the active Vulkan device; the Vulkan renderer must be created with the runtime-required OpenXR Vulkan instance/device extensions or through the XR_KHR_vulkan_enable2 creation path.";

            throw new OpenXrGraphicsSessionException(result, message);
        }
    }

    /// <summary>
    /// Initializes Vulkan swapchains for stereo rendering
    /// </summary>
    internal unsafe void InitializeVulkanSwapchains(VulkanRenderer renderer)
    {
        uint formatCount = 0;
        var formatResult = Api.EnumerateSwapchainFormats(_session, 0, ref formatCount, null);
        if (formatResult != Result.Success || formatCount == 0)
            throw new Exception($"Failed to enumerate OpenXR swapchain formats for Vulkan. Result={formatResult}, Count={formatCount}");

        var formats = new long[formatCount];
        fixed (long* formatsPtr = formats)
        {
            formatResult = Api.EnumerateSwapchainFormats(_session, formatCount, ref formatCount, formatsPtr);
        }
        if (formatResult != Result.Success || formatCount == 0)
            throw new Exception($"Failed to enumerate OpenXR swapchain formats for Vulkan. Result={formatResult}, Count={formatCount}");

        var supportedFormatsLog = string.Join(", ", Array.ConvertAll(formats, FormatVulkanSwapchainFormatForLog));
        Debug.Out($"OpenXR Vulkan supported swapchain formats: {supportedFormatsLog}");

        // Get view configuration
        var viewConfigType = ViewConfigurationType.PrimaryStereo;
        _viewCount = 0;
        Api.EnumerateViewConfigurationView(_instance, _systemId, viewConfigType, 0, ref _viewCount, null);

        if (_viewCount != 2)
        {
            throw new Exception($"Expected 2 views, got {_viewCount}");
        }

        _views = new View[_viewCount];
        for (int i = 0; i < _views.Length; i++)
            _views[i].Type = StructureType.View;

        for (int i = 0; i < _viewConfigViews.Length; i++)
            _viewConfigViews[i].Type = StructureType.ViewConfigurationView;

        fixed (ViewConfigurationView* viewConfigViewsPtr = _viewConfigViews)
        {
            Api.EnumerateViewConfigurationView(_instance, _systemId, viewConfigType, _viewCount, ref _viewCount, viewConfigViewsPtr);
        }

        for (int i = 0; i < _viewCount; i++)
        {
            uint rw = _viewConfigViews[i].RecommendedImageRectWidth;
            uint rh = _viewConfigViews[i].RecommendedImageRectHeight;
            Debug.Out($"OpenXR Vulkan view[{i}] recommended size: {rw}x{rh}, samples={_viewConfigViews[i].RecommendedSwapchainSampleCount}");

            if (rw == 0 || rh == 0)
                throw new Exception($"OpenXR runtime reported an invalid recommended image rect size for Vulkan view {i}: {rw}x{rh}. Cannot create swapchains.");
        }

        // Create swapchains for each view
        for (int i = 0; i < _viewCount; i++)
        {
            uint width = _viewConfigViews[i].RecommendedImageRectWidth;
            uint height = _viewConfigViews[i].RecommendedImageRectHeight;
            uint recommendedSamples = _viewConfigViews[i].RecommendedSwapchainSampleCount;

            Result lastResult = Result.Success;
            bool created = false;
            long createdFormat = 0;
            uint createdSamples = 0;

            SwapchainUsageFlags preferredUsage =
                SwapchainUsageFlags.ColorAttachmentBit |
                SwapchainUsageFlags.TransferDstBit |
                SwapchainUsageFlags.TransferSrcBit;
            SwapchainUsageFlags fallbackUsage =
                SwapchainUsageFlags.ColorAttachmentBit |
                SwapchainUsageFlags.TransferDstBit;

            foreach (long format in GetPreferredVulkanSwapchainFormats(formats))
            {
                foreach (var usage in new[]
                {
                    preferredUsage | SwapchainUsageFlags.SampledBit,
                    preferredUsage,
                    fallbackUsage | SwapchainUsageFlags.SampledBit,
                    fallbackUsage
                })
                {
                    foreach (uint samples in recommendedSamples > 1 ? [recommendedSamples, 1u] : new[] { 1u })
                    {
                        var swapchainCreateInfo = new SwapchainCreateInfo
                        {
                            Type = StructureType.SwapchainCreateInfo,
                            UsageFlags = usage,
                            Format = format,
                            SampleCount = samples,
                            Width = width,
                            Height = height,
                            FaceCount = 1,
                            ArraySize = 1,
                            MipCount = 1
                        };

                        fixed (Swapchain* swapchainPtr = &_swapchains[i])
                        {
                            lastResult = Api.CreateSwapchain(_session, in swapchainCreateInfo, swapchainPtr);
                        }

                        if (lastResult == Result.Success)
                        {
                            Debug.Out($"OpenXR Vulkan swapchain[{i}] created. Format={FormatVulkanSwapchainFormatForLog(format)}, Samples={samples}, Usage={usage}, Size={width}x{height}");
                            createdFormat = format;
                            _vulkanOpenXrSwapchainUsages[i] = usage;
                            createdSamples = samples;
                            created = true;
                            break;
                        }
                    }

                    if (created)
                        break;
                }

                if (created)
                    break;
            }

            if (!created)
                throw new Exception($"Failed to create Vulkan swapchain for view {i}. LastResult={lastResult}, RequiredUsage={preferredUsage}, RecommendedSamples={recommendedSamples}, Size={width}x{height}, SupportedFormats={supportedFormatsLog}");

            _vulkanOpenXrSwapchainFormats[i] = createdFormat;

            // Get swapchain images
            uint imageCount = 0;
            var enumerateResult = Api.EnumerateSwapchainImages(_swapchains[i], 0, &imageCount, null);
            if (enumerateResult != Result.Success || imageCount == 0)
                throw new Exception($"Failed to query Vulkan swapchain image count for view {i}. Result={enumerateResult}, Count={imageCount}");

            _swapchainImagesVK[i] = (SwapchainImageVulkan2KHR*)Marshal.AllocHGlobal((int)imageCount * sizeof(SwapchainImageVulkan2KHR));

            for (uint j = 0; j < imageCount; j++)
                _swapchainImagesVK[i][j].Type = StructureType.SwapchainImageVulkan2Khr;

            enumerateResult = Api.EnumerateSwapchainImages(_swapchains[i], imageCount, &imageCount, (SwapchainImageBaseHeader*)_swapchainImagesVK[i]);
            if (enumerateResult != Result.Success || imageCount == 0)
                throw new Exception($"Failed to enumerate Vulkan swapchain images for view {i}. Result={enumerateResult}, Count={imageCount}");

            _swapchainImageCounts[i] = imageCount;
            RecordSmokeSwapchain(
                "Vulkan",
                i,
                width,
                height,
                createdFormat,
                createdSamples,
                imageCount);

            Console.WriteLine($"Created Vulkan swapchain {i} with {imageCount} images ({width}x{height})");
        }
        RecordSmokeSwapchainsCreated();
    }

    private bool TryRenderVulkanEye(uint viewIndex, uint imageIndex)
    {
        if (Window?.Renderer is not VulkanRenderer renderer)
            return false;

        SwapchainImageVulkan2KHR* images = _swapchainImagesVK[viewIndex];
        if (images == null || imageIndex >= _swapchainImageCounts[viewIndex])
            return false;

        uint width = _viewConfigViews[viewIndex].RecommendedImageRectWidth;
        uint height = _viewConfigViews[viewIndex].RecommendedImageRectHeight;
        EnsureViewportMirrorTargets(renderer, width, height);
        EnsureOpenXrPreviewTargets(renderer, width, height);
        if (OpenXrDebugClearOnly)
        {
            ColorF4 clearColor = viewIndex == 0
                ? new ColorF4(1f, 0f, 0f, 1f)
                : new ColorF4(0f, 1f, 0f, 1f);

            VkImage debugImage = new(images[imageIndex].Image);
            bool debugCleared = renderer.TryClearOpenXrSwapchainImage(
                debugImage,
                new VkExtent2D(width, height),
                clearColor);
            if (debugCleared)
                PublishVulkanEyeMirror(renderer, viewIndex, imageIndex, debugImage, width, height);

            return debugCleared;
        }

        if (_openXrFrameWorld is null)
            return LogVulkanEyeRenderNotReady(viewIndex, imageIndex, "no frame world");
        if (_openXrLeftEyeCamera is null || _openXrRightEyeCamera is null)
            return LogVulkanEyeRenderNotReady(viewIndex, imageIndex, "no eye cameras");

        EnsureOpenXrViewports(width, height);

        XRViewport? eyeViewport = viewIndex == 0 ? _openXrLeftViewport : _openXrRightViewport;
        XRCamera? eyeCamera = viewIndex == 0 ? _openXrLeftEyeCamera : _openXrRightEyeCamera;
        if (eyeViewport is null || eyeCamera is null)
            return LogVulkanEyeRenderNotReady(viewIndex, imageIndex, "no eye viewport/camera");

        eyeViewport.WorldInstanceOverride = _openXrFrameWorld;

        long selectedFormat = viewIndex < _vulkanOpenXrSwapchainFormats.Length
            ? _vulkanOpenXrSwapchainFormats[viewIndex]
            : 0;
        if (selectedFormat == 0)
            return LogVulkanEyeRenderNotReady(viewIndex, imageIndex, "no selected Vulkan swapchain format");

        var previous = AbstractRenderer.Current;
        bool previousRendererActive = renderer.Active;
        try
        {
            renderer.Active = true;
            AbstractRenderer.Current = renderer;

            ApplyOpenXrEyePoseForRenderThread(viewIndex);

            VkImage eyeImage = new(images[imageIndex].Image);
            bool rendered = renderer.TryRenderOpenXrEyeSwapchain(
                eyeImage,
                (VkFormat)selectedFormat,
                new VkExtent2D(width, height),
                (int)viewIndex,
                () =>
                {
                    eyeViewport.Render(null, _openXrFrameWorld, eyeCamera, shadowPass: false, forcedMaterial: null);
                });
            if (rendered)
                PublishVulkanEyeMirror(renderer, viewIndex, imageIndex, eyeImage, width, height);

            return rendered;
        }
        finally
        {
            renderer.Active = previousRendererActive;
            AbstractRenderer.Current = previous;
        }
    }

    private void PublishVulkanEyeMirror(VulkanRenderer renderer, uint viewIndex, uint imageIndex, VkImage image, uint width, uint height)
    {
        if (viewIndex >= _vulkanOpenXrSwapchainUsages.Length ||
            (_vulkanOpenXrSwapchainUsages[viewIndex] & SwapchainUsageFlags.TransferSrcBit) == 0)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.Mirror.NoTransferSrc.{GetHashCode()}.{viewIndex}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] Vulkan swapchain view {0} image {1} was created without TransferSrc usage; desktop eye mirror cannot be updated from the runtime image.",
                viewIndex,
                imageIndex);
            return;
        }

        long selectedFormat = viewIndex < _vulkanOpenXrSwapchainFormats.Length
            ? _vulkanOpenXrSwapchainFormats[viewIndex]
            : 0;
        if (selectedFormat == 0)
            return;

        XRTexture2D? previewTexture = viewIndex == 0 ? _previewLeftEyeTexture : _previewRightEyeTexture;
        VkExtent2D extent = new(width, height);
        bool copiedPreview = renderer.TryCopyOpenXrEyeSwapchainImageToTexture(
            image,
            (VkFormat)selectedFormat,
            extent,
            previewTexture,
            $"preview eye {viewIndex}");

        bool copiedDesktopMirror = renderer.TryCopyOpenXrEyeSwapchainImageToTexture(
            image,
            (VkFormat)selectedFormat,
            extent,
            _viewportMirrorColor,
            $"desktop mirror eye {viewIndex}");

        if (copiedPreview || copiedDesktopMirror)
            RecordSmokeDesktopMirrorComposed();
    }

    private void PrewarmVulkanEyeResources(uint viewIndex)
    {
        if (Window?.Renderer is not VulkanRenderer renderer)
            return;

        if (_openXrFrameWorld is null || _openXrLeftEyeCamera is null || _openXrRightEyeCamera is null)
            return;

        uint width = _viewConfigViews[viewIndex].RecommendedImageRectWidth;
        uint height = _viewConfigViews[viewIndex].RecommendedImageRectHeight;
        EnsureOpenXrViewports(width, height);

        XRViewport? eyeViewport = viewIndex == 0 ? _openXrLeftViewport : _openXrRightViewport;
        XRCamera? eyeCamera = viewIndex == 0 ? _openXrLeftEyeCamera : _openXrRightEyeCamera;
        if (eyeViewport is null || eyeCamera is null)
            return;

        long selectedFormat = viewIndex < _vulkanOpenXrSwapchainFormats.Length
            ? _vulkanOpenXrSwapchainFormats[viewIndex]
            : 0;
        if (selectedFormat == 0)
            return;

        eyeViewport.WorldInstanceOverride = _openXrFrameWorld;

        var previous = AbstractRenderer.Current;
        bool previousRendererActive = renderer.Active;
        try
        {
            renderer.Active = true;
            AbstractRenderer.Current = renderer;

            ApplyOpenXrEyePoseForRenderThread(viewIndex);

            renderer.PrewarmOpenXrEyeSwapchainResources(
                (VkFormat)selectedFormat,
                new VkExtent2D(width, height),
                (int)viewIndex,
                () =>
                {
                    eyeViewport.Render(null, _openXrFrameWorld, eyeCamera, shadowPass: false, forcedMaterial: null);
                });
        }
        finally
        {
            renderer.Active = previousRendererActive;
            AbstractRenderer.Current = previous;
        }
    }

    private static IEnumerable<long> GetPreferredVulkanSwapchainFormats(long[] available)
    {
        foreach (long preferred in VulkanSwapchainFormatPreferences)
        {
            if (Array.IndexOf(available, preferred) >= 0)
                yield return preferred;
        }

        foreach (long format in available)
        {
            if (Array.IndexOf(VulkanSwapchainFormatPreferences, format) < 0)
                yield return format;
        }
    }

    private static string FormatVulkanSwapchainFormatForLog(long format)
        => Enum.IsDefined(typeof(VkFormat), (VkFormat)format)
            ? $"{(VkFormat)format}({format})"
            : $"0x{format:X}";

    private bool LogVulkanEyeRenderNotReady(uint viewIndex, uint imageIndex, string reason)
    {
        Debug.VulkanWarningEvery(
            $"OpenXR.VulkanEyeRender.NotReady.{reason}.{GetHashCode()}",
            TimeSpan.FromSeconds(1),
            "[OpenXR] Vulkan eye {0} image {1} render skipped: {2}.",
            viewIndex,
            imageIndex,
            reason);
        return false;
    }
}
