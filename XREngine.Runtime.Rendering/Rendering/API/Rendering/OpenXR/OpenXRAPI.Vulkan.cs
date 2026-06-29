using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.KHR;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using XREngine;
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
    private static bool OpenXrVulkanMirrorFbo =>
        string.Equals(
            Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.OpenXrVulkanMirrorFbo),
            "1",
            StringComparison.Ordinal);

    private static bool OpenXrVulkanPrewarmEyes =>
        string.Equals(
            Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.OpenXrVulkanPrewarmEyes),
            "1",
            StringComparison.Ordinal);

    private static bool OpenXrVulkanSerialEyeSubmit =>
        string.Equals(
            Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.OpenXrVulkanSerialEyeSubmit),
            "1",
            StringComparison.Ordinal);

    private static readonly long[] VulkanSwapchainFormatPreferences =
    [
        (long)VkFormat.B8G8R8A8Srgb,
        (long)VkFormat.R8G8B8A8Srgb,
        (long)VkFormat.B8G8R8A8Unorm,
        (long)VkFormat.R8G8B8A8Unorm,
    ];

    private readonly long[] _vulkanOpenXrSwapchainFormats = new long[2];
    private readonly SwapchainUsageFlags[] _vulkanOpenXrSwapchainUsages = new SwapchainUsageFlags[2];
    private readonly int[] _vulkanOpenXrStartupPrewarmFramesRemaining = [3, 3];
    private readonly uint[] _vulkanOpenXrPrewarmWidths = new uint[2];
    private readonly uint[] _vulkanOpenXrPrewarmHeights = new uint[2];

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

        if (OpenXrVulkanMirrorFbo)
        {
            Debug.RenderingWarningEvery(
                "OpenXR.Vulkan.MirrorFboCompatibilityPath",
                TimeSpan.FromSeconds(30),
                "[OpenXR] Vulkan mirror-FBO compatibility path is enabled. Eye rendering will use the offscreen FBO command chain, which does not match the full deferred viewport lighting path. Set {0}=0 or leave it unset for direct swapchain rendering.",
                XREngineEnvironmentVariables.OpenXrVulkanMirrorFbo);
        }

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
        bool projectAllowsParallel = RuntimeRenderingHostServices.Current.EnableOpenXrVulkanParallelRendering;
        VrViewRenderModeResolution viewRenderMode = VrViewRenderModeResolver.Resolve(
            ERenderLibrary.Vulkan,
            RuntimeRenderingHostServices.Current.VrViewRenderMode,
            projectAllowsParallel);
        RecordSmokeViewRenderModeResolution(viewRenderMode);

        if (!viewRenderMode.IsSupported)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.ViewRenderMode.Unsupported.{viewRenderMode.RequestedMode}",
                TimeSpan.FromSeconds(5),
                "[OpenXR] {0}",
                viewRenderMode.Diagnostic ?? $"Unsupported VR.ViewRenderMode={viewRenderMode.RequestedMode}.");
        }
        else if (viewRenderMode.EffectiveMode == EVrViewRenderMode.ParallelCommandBufferRecording && supportsMultiQueue)
        {
            Debug.Vulkan("VR.ViewRenderMode=ParallelCommandBufferRecording selected; Vulkan reports multiple graphics queues.");
        }
        else
        {
            Debug.Vulkan(
                "VR.ViewRenderMode={0}; Vulkan multiple graphics queues supported={1}; OpenXR Vulkan parallel gate={2}.",
                viewRenderMode.EffectiveMode,
                supportsMultiQueue,
                projectAllowsParallel);
        }

        _ = TryResolveOpenXrFoveation(ERenderLibrary.Vulkan, out _);

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

    private bool TryResolveOpenXrViewRenderModeForCurrentBackend(out VrViewRenderModeResolution resolution)
    {
        ERenderLibrary backend = Window?.Renderer is VulkanRenderer
            ? ERenderLibrary.Vulkan
            : ERenderLibrary.OpenGL;

        resolution = VrViewRenderModeResolver.Resolve(
            backend,
            RuntimeRenderingHostServices.Current.VrViewRenderMode,
            RuntimeRenderingHostServices.Current.EnableOpenXrVulkanParallelRendering);
        RecordSmokeViewRenderModeResolution(resolution);

        if (resolution.IsSupported)
            return true;

        Debug.RenderingWarningEvery(
            $"OpenXR.ViewRenderMode.Unsupported.{backend}.{resolution.RequestedMode}",
            TimeSpan.FromSeconds(5),
            "[OpenXR] Unsupported VR.ViewRenderMode={0} for backend {1}. {2}",
            resolution.RequestedMode,
            backend,
            resolution.Diagnostic ?? "No fallback was applied.");
        RecordSmokeFailureOnce(
            $"Unsupported VR.ViewRenderMode={resolution.RequestedMode} for backend {backend}. {resolution.Diagnostic ?? "No fallback was applied."}");
        return false;
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
        if (OpenXrVulkanMirrorFbo)
            EnsureVulkanEyeMirrorTargets(renderer, width, height);
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
            if (VulkanCaptureEyeOutputs)
            {
                Debug.VulkanEvery(
                    $"OpenXR.Vulkan.EyeRenderState.{GetHashCode()}.{viewIndex}",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] Vulkan eye render state eye={0} viewport={1}x{2} internal={3}x{4} mirror={5}x{6} camera='{7}' cameraParent='{8}' world='{9}'",
                    viewIndex,
                    eyeViewport.Width,
                    eyeViewport.Height,
                    eyeViewport.InternalWidth,
                    eyeViewport.InternalHeight,
                    width,
                    height,
                    eyeCamera.Transform.SceneNode?.Name ?? "<unnamed camera>",
                    eyeCamera.Transform.Parent?.SceneNode?.Name ?? "<no parent>",
                    _openXrFrameWorld.TargetWorldName ?? "<unnamed world>");
            }

            VkExtent2D extent = new(width, height);
            VkImage eyeImage = new(images[imageIndex].Image);
            if (!OpenXrVulkanMirrorFbo)
            {
                bool directRendered = renderer.TryRenderOpenXrEyeSwapchain(
                    eyeImage,
                    (VkFormat)selectedFormat,
                    extent,
                    resourcePlannerStateIndex: (int)viewIndex,
                    openXrViewIndex: viewIndex,
                    openXrImageIndex: imageIndex,
                    foveation: CreateOpenXrEyeFoveationContext(viewIndex),
                    emitFrameOps: () =>
                    {
                        eyeViewport.Render(null, _openXrFrameWorld, eyeCamera, shadowPass: false, forcedMaterial: null);
                    });
                if (!directRendered)
                    return false;

                PublishVulkanEyeSwapchain(renderer, eyeImage, (VkFormat)selectedFormat, extent, viewIndex, imageIndex, width, height);
                MarkVulkanEyeResourceWarmupComplete(viewIndex);
                return true;
            }

            int eyeTargetIndex = (int)Math.Min(viewIndex, 1u);
            XRFrameBuffer? mirrorFbo = _vulkanEyeMirrorFbos[eyeTargetIndex];
            XRTexture2D? mirrorColor = _vulkanEyeMirrorColors[eyeTargetIndex];
            if (mirrorFbo is null || mirrorColor is null)
                return LogVulkanEyeRenderNotReady(viewIndex, imageIndex, "no Vulkan mirror FBO");

            bool mirrorRendered = renderer.TryRenderOpenXrEyeMirrorFrameBuffer(
                mirrorFbo,
                extent,
                resourcePlannerStateIndex: (int)viewIndex,
                openXrViewIndex: viewIndex,
                openXrImageIndex: imageIndex,
                emitFrameOps: () =>
                {
                    eyeViewport.Render(mirrorFbo, _openXrFrameWorld, eyeCamera, shadowPass: false, forcedMaterial: null);
                });
            if (!mirrorRendered)
                return false;

            bool submitted = renderer.TryBlitTextureToOpenXrSwapchainImage(
                mirrorColor,
                eyeImage,
                (VkFormat)selectedFormat,
                extent,
                $"eye {viewIndex} swapchain image {imageIndex}");
            if (!submitted)
                return false;

            PublishVulkanEyeMirror(renderer, mirrorColor, viewIndex, imageIndex, width, height);
            MarkVulkanEyeResourceWarmupComplete(viewIndex);
            return true;
        }
        finally
        {
            renderer.Active = previousRendererActive;
            AbstractRenderer.Current = previous;
        }
    }

    private void EnsureVulkanEyeMirrorTargets(AbstractRenderer renderer, uint width, uint height)
    {
        width = Math.Max(1u, width);
        height = Math.Max(1u, height);

        if (_vulkanEyeMirrorFbos[0] is not null &&
            _vulkanEyeMirrorFbos[1] is not null &&
            _vulkanEyeMirrorWidth == width &&
            _vulkanEyeMirrorHeight == height)
        {
            return;
        }

        DestroyVulkanEyeMirrorTargets();

        _vulkanEyeMirrorWidth = width;
        _vulkanEyeMirrorHeight = height;
        CreateVulkanEyeMirrorTarget(renderer, 0, width, height, "Left");
        CreateVulkanEyeMirrorTarget(renderer, 1, width, height, "Right");
    }

    private void CreateVulkanEyeMirrorTarget(
        AbstractRenderer renderer,
        int eyeIndex,
        uint width,
        uint height,
        string eyeName)
    {
        XRTexture2D color = XRTexture2D.CreateFrameBufferTexture(
            width,
            height,
            EPixelInternalFormat.Rgba8,
            EPixelFormat.Rgba,
            EPixelType.UnsignedByte,
            EFrameBufferAttachment.ColorAttachment0);
        color.Resizable = true;
        color.MinFilter = ETexMinFilter.Linear;
        color.MagFilter = ETexMagFilter.Linear;
        color.UWrap = ETexWrapMode.ClampToEdge;
        color.VWrap = ETexWrapMode.ClampToEdge;
        color.Name = $"OpenXRVulkan{eyeName}EyeMirrorColor";

        XRRenderBuffer depth = new(width, height, ERenderBufferStorage.Depth24Stencil8, EFrameBufferAttachment.DepthStencilAttachment)
        {
            Name = $"OpenXRVulkan{eyeName}EyeMirrorDepth"
        };

        XRFrameBuffer fbo = new(
            (color, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (depth, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            Name = $"OpenXRVulkan{eyeName}EyeMirrorFBO"
        };

        _vulkanEyeMirrorColors[eyeIndex] = color;
        _vulkanEyeMirrorDepths[eyeIndex] = depth;
        _vulkanEyeMirrorFbos[eyeIndex] = fbo;

        renderer.GetOrCreateAPIRenderObject(color, generateNow: true);
        renderer.GetOrCreateAPIRenderObject(depth, generateNow: true);
        renderer.GetOrCreateAPIRenderObject(fbo, generateNow: true);
    }

    private void DestroyVulkanEyeMirrorTargets()
    {
        for (int i = 0; i < 2; i++)
        {
            try
            {
                _vulkanEyeMirrorFbos[i]?.Destroy();
                _vulkanEyeMirrorDepths[i]?.Destroy();
                _vulkanEyeMirrorColors[i]?.Destroy();
            }
            catch
            {
                // Best-effort cleanup.
            }

            _vulkanEyeMirrorFbos[i] = null;
            _vulkanEyeMirrorDepths[i] = null;
            _vulkanEyeMirrorColors[i] = null;
        }

        _vulkanEyeMirrorWidth = 0;
        _vulkanEyeMirrorHeight = 0;
    }

    private bool TryRenderVulkanEyesBatch(CompositionLayerProjectionView* projectionViews, out bool handled)
    {
        handled = false;
        if (_gl is not null)
            return false;
        if (Window?.Renderer is not VulkanRenderer renderer)
            return false;
        if (_viewCount != 2)
            return false;
        if (OpenXrDebugRenderRightThenLeft || OpenXrVulkanSerialEyeSubmit)
            return false;

        if (!TryResolveOpenXrViewRenderModeForCurrentBackend(out VrViewRenderModeResolution modeResolution))
        {
            handled = true;
            return false;
        }

        if (modeResolution.EffectiveMode == EVrViewRenderMode.SequentialViews)
            return false;

        uint leftWidth = _viewConfigViews[0].RecommendedImageRectWidth;
        uint leftHeight = _viewConfigViews[0].RecommendedImageRectHeight;
        uint rightWidth = _viewConfigViews[1].RecommendedImageRectWidth;
        uint rightHeight = _viewConfigViews[1].RecommendedImageRectHeight;
        if (leftWidth != rightWidth || leftHeight != rightHeight)
            return false;

        handled = true;
        uint leftImageIndex = 0;
        uint rightImageIndex = 0;
        bool leftAcquired = false;
        bool rightAcquired = false;
        int frameNo = Volatile.Read(ref _openXrPendingFrameNumber);

        try
        {
            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.Prewarm"))
            {
                if (ShouldPrewarmVulkanEyeResources(0))
                    PrewarmVulkanEyeResources(0);
                if (ShouldPrewarmVulkanEyeResources(1))
                    PrewarmVulkanEyeResources(1);
            }

            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.AcquireWaitLeft"))
            {
                if (!AcquireAndWaitOpenXrEyeImage(0, ref leftImageIndex, ref leftAcquired, frameNo))
                    return false;
            }

            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.AcquireWaitRight"))
            {
                if (!AcquireAndWaitOpenXrEyeImage(1, ref rightImageIndex, ref rightAcquired, frameNo))
                    return false;
            }

            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.RenderSwapchains"))
            {
                bool rendered = modeResolution.EffectiveMode switch
                {
                    EVrViewRenderMode.SinglePassStereo => TryRenderVulkanEyeSinglePassStereoToSwapchains(
                        renderer,
                        leftImageIndex,
                        rightImageIndex),
                    EVrViewRenderMode.ParallelCommandBufferRecording => TryRenderVulkanEyeParallelCommandBufferRecordingToSwapchains(
                        renderer,
                        leftImageIndex,
                        rightImageIndex),
                    _ => TryRenderVulkanEyeBatchToSwapchains(
                        renderer,
                        leftImageIndex,
                        rightImageIndex,
                        modeResolution.EffectiveMode),
                };

                if (!rendered)
                    return false;
            }

            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.FillProjectionViews"))
            {
                FillProjectionView(0, projectionViews);
                FillProjectionView(1, projectionViews);
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"OpenXR Vulkan batched eye render failed: {ex.Message}");
            return false;
        }
        finally
        {
            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.ReleaseEyes"))
            {
                ReleaseOpenXrEyeImageIfAcquired(1, rightAcquired, frameNo);
                ReleaseOpenXrEyeImageIfAcquired(0, leftAcquired, frameNo);
            }
        }
    }

    private bool AcquireAndWaitOpenXrEyeImage(
        uint viewIndex,
        ref uint imageIndex,
        ref bool acquired,
        int frameNo)
    {
        var acquireInfo = new SwapchainImageAcquireInfo
        {
            Type = StructureType.SwapchainImageAcquireInfo
        };

        var acquireResult = CheckResult(Api.AcquireSwapchainImage(_swapchains[viewIndex], in acquireInfo, ref imageIndex), "xrAcquireSwapchainImage");
        if (acquireResult != Result.Success)
            return false;

        acquired = true;
        RecordSmokeEyeAcquire(viewIndex);

        if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
            Debug.Out($"OpenXR[{frameNo}] Eye{viewIndex}: Acquire(batch) => {acquireResult} imageIndex={imageIndex}");

        var waitInfo = new SwapchainImageWaitInfo
        {
            Type = StructureType.SwapchainImageWaitInfo,
            Timeout = long.MaxValue
        };

        var waitResult = CheckResult(Api.WaitSwapchainImage(_swapchains[viewIndex], in waitInfo), "xrWaitSwapchainImage");
        if (waitResult != Result.Success)
            return false;

        RecordSmokeEyeWait(viewIndex);

        if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
            Debug.Out($"OpenXR[{frameNo}] Eye{viewIndex}: Wait(batch) => {waitResult}");

        return true;
    }

    private void ReleaseOpenXrEyeImageIfAcquired(uint viewIndex, bool acquired, int frameNo)
    {
        if (!acquired)
            return;

        var releaseInfo = new SwapchainImageReleaseInfo { Type = StructureType.SwapchainImageReleaseInfo };
        var releaseResult = CheckResult(Api.ReleaseSwapchainImage(_swapchains[viewIndex], in releaseInfo), "xrReleaseSwapchainImage");
        if (releaseResult == Result.Success)
            RecordSmokeEyeRelease(viewIndex);
        if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
            Debug.Out($"OpenXR[{frameNo}] Eye{viewIndex}: Release(batch) => {releaseResult}");
    }

    private bool TryRenderVulkanEyeSinglePassStereoToSwapchains(
        VulkanRenderer renderer,
        uint leftImageIndex,
        uint rightImageIndex)
    {
        Debug.VulkanEvery(
            $"OpenXR.Vulkan.ViewRenderMode.SinglePassStereo.{GetHashCode()}",
            TimeSpan.FromSeconds(2),
            "[OpenXR] VR.ViewRenderMode=SinglePassStereo selected; using explicit stereo compatibility path over per-eye OpenXR swapchains.");

        using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.SinglePassStereo.RenderSwapchains"))
            return TryRenderVulkanEyeBatchToSwapchains(
                renderer,
                leftImageIndex,
                rightImageIndex,
                EVrViewRenderMode.SinglePassStereo);
    }

    private bool TryRenderVulkanEyeParallelCommandBufferRecordingToSwapchains(
        VulkanRenderer renderer,
        uint leftImageIndex,
        uint rightImageIndex)
    {
        Debug.VulkanEvery(
            $"OpenXR.Vulkan.ViewRenderMode.ParallelCommandBufferRecording.{GetHashCode()}",
            TimeSpan.FromSeconds(2),
            "[OpenXR] VR.ViewRenderMode=ParallelCommandBufferRecording selected; left/right eye primary recording uses the explicit worker-backed parallel path.");

        using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.ParallelCommandBufferRecording.RenderSwapchains"))
            return TryRenderVulkanEyeBatchToSwapchains(
                renderer,
                leftImageIndex,
                rightImageIndex,
                EVrViewRenderMode.ParallelCommandBufferRecording);
    }

    private bool TryRenderVulkanEyeBatchToSwapchains(
        VulkanRenderer renderer,
        uint leftImageIndex,
        uint rightImageIndex,
        EVrViewRenderMode viewRenderMode)
    {
        SwapchainImageVulkan2KHR* leftImages = _swapchainImagesVK[0];
        SwapchainImageVulkan2KHR* rightImages = _swapchainImagesVK[1];
        if (leftImages == null || rightImages == null)
            return false;
        if (leftImageIndex >= _swapchainImageCounts[0] || rightImageIndex >= _swapchainImageCounts[1])
            return false;

        uint width = _viewConfigViews[0].RecommendedImageRectWidth;
        uint height = _viewConfigViews[0].RecommendedImageRectHeight;
        using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.EnsureTargets"))
        {
            if (OpenXrVulkanMirrorFbo)
                EnsureVulkanEyeMirrorTargets(renderer, width, height);
            EnsureOpenXrPreviewTargets(renderer, width, height);
        }

        if (OpenXrDebugClearOnly)
        {
            bool leftCleared = renderer.TryClearOpenXrSwapchainImage(
                new VkImage(leftImages[leftImageIndex].Image),
                new VkExtent2D(width, height),
                new ColorF4(1f, 0f, 0f, 1f));
            bool rightCleared = renderer.TryClearOpenXrSwapchainImage(
                new VkImage(rightImages[rightImageIndex].Image),
                new VkExtent2D(width, height),
                new ColorF4(0f, 1f, 0f, 1f));
            return leftCleared && rightCleared;
        }

        if (_openXrFrameWorld is null)
            return LogVulkanEyeRenderNotReady(0, leftImageIndex, "no frame world");
        if (_openXrLeftEyeCamera is null || _openXrRightEyeCamera is null)
            return LogVulkanEyeRenderNotReady(0, leftImageIndex, "no eye cameras");

        EnsureOpenXrViewports(width, height);

        XRViewport? leftViewport = _openXrLeftViewport;
        XRViewport? rightViewport = _openXrRightViewport;
        XRCamera leftCamera = _openXrLeftEyeCamera;
        XRCamera rightCamera = _openXrRightEyeCamera;
        if (leftViewport is null || rightViewport is null)
            return LogVulkanEyeRenderNotReady(0, leftImageIndex, "no eye viewport/camera");

        leftViewport.WorldInstanceOverride = _openXrFrameWorld;
        rightViewport.WorldInstanceOverride = _openXrFrameWorld;

        long leftFormat = _vulkanOpenXrSwapchainFormats[0];
        long rightFormat = _vulkanOpenXrSwapchainFormats[1];
        if (leftFormat == 0 || rightFormat == 0)
            return LogVulkanEyeRenderNotReady(0, leftImageIndex, "no selected Vulkan swapchain format");

        var previous = AbstractRenderer.Current;
        bool previousRendererActive = renderer.Active;
        try
        {
            renderer.Active = true;
            AbstractRenderer.Current = renderer;

            if (VulkanCaptureEyeOutputs)
            {
                Debug.VulkanEvery(
                    $"OpenXR.Vulkan.EyeBatchRenderState.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] Vulkan eye batch render state leftImage={0} rightImage={1} extent={2}x{3} leftCamera='{4}' leftParent='{5}' rightCamera='{6}' rightParent='{7}' world='{8}'",
                    leftImageIndex,
                    rightImageIndex,
                    width,
                    height,
                    leftCamera.Transform.SceneNode?.Name ?? "<unnamed camera>",
                    leftCamera.Transform.Parent?.SceneNode?.Name ?? "<no parent>",
                    rightCamera.Transform.SceneNode?.Name ?? "<unnamed camera>",
                    rightCamera.Transform.Parent?.SceneNode?.Name ?? "<no parent>",
                    _openXrFrameWorld.TargetWorldName ?? "<unnamed world>");
            }

            VkExtent2D extent = new(width, height);
            VkImage leftImage = new(leftImages[leftImageIndex].Image);
            VkImage rightImage = new(rightImages[rightImageIndex].Image);

            if (OpenXrVulkanMirrorFbo)
            {
                XRFrameBuffer? leftMirrorFbo = _vulkanEyeMirrorFbos[0];
                XRFrameBuffer? rightMirrorFbo = _vulkanEyeMirrorFbos[1];
                XRTexture2D? leftMirrorColor = _vulkanEyeMirrorColors[0];
                XRTexture2D? rightMirrorColor = _vulkanEyeMirrorColors[1];
                if (leftMirrorFbo is null || rightMirrorFbo is null || leftMirrorColor is null || rightMirrorColor is null)
                    return LogVulkanEyeRenderNotReady(0, leftImageIndex, "no Vulkan per-eye mirror FBOs");

                var leftMirrorRequest = new VulkanRenderer.OpenXrEyeMirrorRenderRequest(
                    leftMirrorFbo,
                    extent,
                    ResourcePlannerStateIndex: 0,
                    OpenXrViewIndex: 0,
                    OpenXrImageIndex: leftImageIndex,
                    EmitFrameOps: () =>
                    {
                        ApplyOpenXrEyePoseForRenderThread(0);
                        RenderOpenXrVulkanMirrorViewport(leftViewport, leftMirrorFbo, leftCamera);
                    });

                var rightMirrorRequest = new VulkanRenderer.OpenXrEyeMirrorRenderRequest(
                    rightMirrorFbo,
                    extent,
                    ResourcePlannerStateIndex: 1,
                    OpenXrViewIndex: 1,
                    OpenXrImageIndex: rightImageIndex,
                    EmitFrameOps: () =>
                    {
                        ApplyOpenXrEyePoseForRenderThread(1);
                        RenderOpenXrVulkanMirrorViewport(rightViewport, rightMirrorFbo, rightCamera);
                    });

                var leftPublishRequest = new VulkanRenderer.OpenXrEyeMirrorPublishRequest(
                    leftMirrorColor,
                    leftImage,
                    (VkFormat)leftFormat,
                    extent,
                    _previewLeftEyeTexture,
                    $"left eye swapchain image {leftImageIndex}",
                    FlipPreviewY: false);
                var rightPublishRequest = new VulkanRenderer.OpenXrEyeMirrorPublishRequest(
                    rightMirrorColor,
                    rightImage,
                    (VkFormat)rightFormat,
                    extent,
                    _previewRightEyeTexture,
                    $"right eye swapchain image {rightImageIndex}",
                    FlipPreviewY: false);

                bool published = renderer.TryRenderAndPublishOpenXrEyeMirrorFrameBuffers(
                    leftMirrorRequest,
                    rightMirrorRequest,
                    leftPublishRequest,
                    rightPublishRequest,
                    out bool leftPreviewCopied,
                    out bool rightPreviewCopied);
                if (!published)
                    return false;

                LogVulkanEyeMirrorPublish(leftMirrorColor, _previewLeftEyeTexture, 0, leftImageIndex, width, height, leftPreviewCopied);
                LogVulkanEyeMirrorPublish(rightMirrorColor, _previewRightEyeTexture, 1, rightImageIndex, width, height, rightPreviewCopied);
                if (leftPreviewCopied)
                    RecordSmokeDesktopMirrorComposed();
                if (rightPreviewCopied)
                    RecordSmokeDesktopMirrorComposed();
                MarkVulkanEyeResourceWarmupComplete(0);
                MarkVulkanEyeResourceWarmupComplete(1);
                return true;
            }

            var leftRequest = new VulkanRenderer.OpenXrEyeSwapchainRenderRequest(
                leftImage,
                (VkFormat)leftFormat,
                extent,
                ResourcePlannerStateIndex: 0,
                OpenXrViewIndex: 0,
                OpenXrImageIndex: leftImageIndex,
                Foveation: CreateOpenXrEyeFoveationContext(0),
                EmitFrameOps: () =>
                {
                    ApplyOpenXrEyePoseForRenderThread(0);
                    leftViewport.Render(null, _openXrFrameWorld, leftCamera, shadowPass: false, forcedMaterial: null);
                });

            var rightRequest = new VulkanRenderer.OpenXrEyeSwapchainRenderRequest(
                rightImage,
                (VkFormat)rightFormat,
                extent,
                ResourcePlannerStateIndex: 1,
                OpenXrViewIndex: 1,
                OpenXrImageIndex: rightImageIndex,
                Foveation: CreateOpenXrEyeFoveationContext(1),
                EmitFrameOps: () =>
                {
                    ApplyOpenXrEyePoseForRenderThread(1);
                    rightViewport.Render(null, _openXrFrameWorld, rightCamera, shadowPass: false, forcedMaterial: null);
                });

            bool directRendered;
            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.RenderDirectSwapchains"))
            {
                directRendered = viewRenderMode switch
                {
                    EVrViewRenderMode.SinglePassStereo => renderer.TryRenderOpenXrEyeSwapchainsSinglePassStereo(leftRequest, rightRequest),
                    EVrViewRenderMode.ParallelCommandBufferRecording => renderer.TryRenderOpenXrEyeSwapchainsParallelCommandBufferRecording(leftRequest, rightRequest),
                    _ => renderer.TryRenderOpenXrEyeSwapchains(leftRequest, rightRequest),
                };
            }
            if (!directRendered)
                return false;

            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.PublishLeft"))
                PublishVulkanEyeSwapchain(renderer, leftImage, (VkFormat)leftFormat, extent, 0, leftImageIndex, width, height);
            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.PublishRight"))
                PublishVulkanEyeSwapchain(renderer, rightImage, (VkFormat)rightFormat, extent, 1, rightImageIndex, width, height);
            MarkVulkanEyeResourceWarmupComplete(0);
            MarkVulkanEyeResourceWarmupComplete(1);
            return true;
        }
        finally
        {
            renderer.Active = previousRendererActive;
            AbstractRenderer.Current = previous;
        }
    }

    private void RenderOpenXrVulkanMirrorViewport(
        XRViewport viewport,
        XRFrameBuffer targetFrameBuffer,
        XRCamera camera)
    {
        RuntimeEngine.Rendering.State.PushMirrorPass();
        try
        {
            viewport.Render(targetFrameBuffer, _openXrFrameWorld, camera, shadowPass: false, forcedMaterial: null);
        }
        finally
        {
            RuntimeEngine.Rendering.State.PopMirrorPass();
        }
    }

    private bool ShouldPrewarmVulkanEyeResources(uint viewIndex)
    {
        if (OpenXrVulkanPrewarmEyes)
            return true;

        int index = (int)Math.Min(viewIndex, 1u);
        uint width = _viewConfigViews[viewIndex].RecommendedImageRectWidth;
        uint height = _viewConfigViews[viewIndex].RecommendedImageRectHeight;
        if (_vulkanOpenXrPrewarmWidths[index] != width ||
            _vulkanOpenXrPrewarmHeights[index] != height)
        {
            _vulkanOpenXrPrewarmWidths[index] = width;
            _vulkanOpenXrPrewarmHeights[index] = height;
            _vulkanOpenXrStartupPrewarmFramesRemaining[index] = 3;
        }

        return _vulkanOpenXrStartupPrewarmFramesRemaining[index] > 0;
    }

    private void MarkVulkanEyeResourceWarmupComplete(uint viewIndex)
    {
        if (OpenXrVulkanPrewarmEyes)
            return;

        int index = (int)Math.Min(viewIndex, 1u);
        if (_vulkanOpenXrStartupPrewarmFramesRemaining[index] > 0)
            _vulkanOpenXrStartupPrewarmFramesRemaining[index]--;
    }

    private void PublishVulkanEyeMirror(
        VulkanRenderer renderer,
        XRTexture2D? sourceTexture,
        uint viewIndex,
        uint imageIndex,
        uint width,
        uint height)
    {
        if (sourceTexture is null)
            return;

        XRTexture2D? previewTexture = viewIndex == 0 ? _previewLeftEyeTexture : _previewRightEyeTexture;
        bool copiedPreview = renderer.TryCopyOpenXrEyeMirrorTexture(
            sourceTexture,
            previewTexture,
            $"preview eye {viewIndex}",
            flipY: false);
        bool copiedDesktopMirror = false;
        if (ShouldCopyVulkanEyeToDesktopMirror(viewIndex))
        {
            EnsureViewportMirrorTargets(renderer, width, height);
            copiedDesktopMirror = renderer.TryCopyOpenXrEyeMirrorTexture(
                sourceTexture,
                _viewportMirrorColor,
                $"desktop mirror eye {viewIndex}",
                flipY: false);
        }

        if (VulkanCaptureEyeOutputs)
            LogVulkanEyeMirrorPublish(sourceTexture, previewTexture, viewIndex, imageIndex, width, height, copiedPreview, copiedDesktopMirror);

        if (copiedPreview || copiedDesktopMirror)
            RecordSmokeDesktopMirrorComposed();
    }

    private static void LogVulkanEyeMirrorPublish(
        XRTexture2D? sourceTexture,
        XRTexture2D? previewTexture,
        uint viewIndex,
        uint imageIndex,
        uint width,
        uint height,
        bool previewCopied,
        bool desktopMirrorCopied = false)
    {
        if (!VulkanCaptureEyeOutputs)
            return;

        Debug.Vulkan(
            "[OpenXR] Vulkan eye mirror eye={0} swapchainImage={1} source='{2}' previewCopied={3} desktopMirrorCopied={4} previewFlippedY=False preview='{5}' extent={6}x{7}",
            viewIndex,
            imageIndex,
            sourceTexture?.Name ?? "<none>",
            previewCopied,
            desktopMirrorCopied,
            previewTexture?.Name ?? "<none>",
            width,
            height);
    }

    private void PublishVulkanEyeSwapchain(
        VulkanRenderer renderer,
        VkImage sourceImage,
        VkFormat sourceFormat,
        VkExtent2D sourceExtent,
        uint viewIndex,
        uint imageIndex,
        uint width,
        uint height)
    {
        XRTexture2D? previewTexture = viewIndex == 0 ? _previewLeftEyeTexture : _previewRightEyeTexture;
        bool shouldCopyPreview = ShouldCopyDirectVulkanEyeSwapchainPreview();
        bool copiedPreview = shouldCopyPreview &&
            renderer.TryCopyOpenXrEyeSwapchainImageToTexture(
                sourceImage,
                sourceFormat,
                sourceExtent,
                previewTexture,
                $"preview eye {viewIndex}",
                flipY: false);
        bool copiedDesktopMirror = false;
        if (ShouldCopyVulkanEyeToDesktopMirror(viewIndex))
        {
            EnsureViewportMirrorTargets(renderer, width, height);
            copiedDesktopMirror = renderer.TryCopyOpenXrEyeSwapchainImageToTexture(
                sourceImage,
                sourceFormat,
                sourceExtent,
                _viewportMirrorColor,
                $"desktop mirror eye {viewIndex}",
                flipY: false);
        }

        if (VulkanCaptureEyeOutputs)
        {
            Debug.Vulkan(
                "[OpenXR] Vulkan eye swapchain eye={0} swapchainImage={1} source=0x{2:X} previewCopied={3} desktopMirrorCopied={4} previewFlippedY=False preview='{5}' mirror='{6}' extent={7}x{8} path=direct-swapchain",
                viewIndex,
                imageIndex,
                sourceImage.Handle,
                copiedPreview,
                copiedDesktopMirror,
                previewTexture?.Name ?? "<none>",
                _viewportMirrorColor?.Name ?? "<none>",
                width,
                height);
        }

        if (copiedPreview || copiedDesktopMirror)
            RecordSmokeDesktopMirrorComposed();
    }

    private static bool ShouldCopyDirectVulkanEyeSwapchainPreview()
        => VulkanCaptureEyeOutputs ||
           RuntimeRenderingHostServices.Current.VrCopyEyePreviewTextures ||
           (RuntimeRenderingHostServices.Current.RenderWindowsWhileInVR &&
            RuntimeRenderingHostServices.Current.VrMirrorComposeFromEyeTextures);

    private static bool ShouldCopyVulkanEyeToDesktopMirror(uint viewIndex)
        => viewIndex == 0 &&
           RuntimeRenderingHostServices.Current.RenderWindowsWhileInVR &&
           RuntimeRenderingHostServices.Current.VrMirrorComposeFromEyeTextures;

    private void PrewarmVulkanEyeResources(uint viewIndex)
    {
        if (Window?.Renderer is not VulkanRenderer renderer)
            return;

        if (_openXrFrameWorld is null || _openXrLeftEyeCamera is null || _openXrRightEyeCamera is null)
            return;

        uint width = _viewConfigViews[viewIndex].RecommendedImageRectWidth;
        uint height = _viewConfigViews[viewIndex].RecommendedImageRectHeight;
        if (OpenXrVulkanMirrorFbo)
            EnsureVulkanEyeMirrorTargets(renderer, width, height);
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

            if (!OpenXrVulkanMirrorFbo)
            {
                renderer.PrewarmOpenXrEyeSwapchainResources(
                    (VkFormat)selectedFormat,
                    new VkExtent2D(width, height),
                    (int)viewIndex,
                    () =>
                    {
                        eyeViewport.Render(null, _openXrFrameWorld, eyeCamera, shadowPass: false, forcedMaterial: null);
                    });
                return;
            }

            int eyeTargetIndex = (int)Math.Min(viewIndex, 1u);
            XRFrameBuffer? mirrorFbo = _vulkanEyeMirrorFbos[eyeTargetIndex];
            if (mirrorFbo is null)
                return;

            renderer.PrewarmOpenXrEyeMirrorFrameBufferResources(
                mirrorFbo,
                new VkExtent2D(width, height),
                (int)viewIndex,
                () =>
                {
                    eyeViewport.Render(mirrorFbo, _openXrFrameWorld, eyeCamera, shadowPass: false, forcedMaterial: null);
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
