using Silk.NET.Core.Native;
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

    private static bool OpenXrVulkanTrueStereoOverride =>
        string.Equals(
            Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.OpenXrVulkanTrueStereo),
            "1",
            StringComparison.Ordinal);

    private static readonly long[] VulkanSwapchainFormatPreferences =
    [
        (long)VkFormat.R8G8B8A8Srgb,
        (long)VkFormat.R8G8B8A8Unorm,
        (long)VkFormat.B8G8R8A8Srgb,
        (long)VkFormat.B8G8R8A8Unorm,
    ];

    private readonly long[] _vulkanOpenXrSwapchainFormats = new long[RenderFrameViewSet.MaxViewCount];
    private readonly SwapchainUsageFlags[] _vulkanOpenXrSwapchainUsages = new SwapchainUsageFlags[RenderFrameViewSet.MaxViewCount];
    private readonly int[] _vulkanOpenXrStartupPrewarmFramesRemaining = CreateOpenXrStartupPrewarmFrameCounters();
    private readonly uint[] _vulkanOpenXrPrewarmWidths = new uint[RenderFrameViewSet.MaxViewCount];
    private readonly uint[] _vulkanOpenXrPrewarmHeights = new uint[RenderFrameViewSet.MaxViewCount];

    private static int[] CreateOpenXrStartupPrewarmFrameCounters()
    {
        int[] counters = new int[RenderFrameViewSet.MaxViewCount];
        Array.Fill(counters, 3);
        return counters;
    }

    private readonly record struct OpenXrStereoRenderTarget(
        XRFrameBuffer FrameBuffer,
        XRTexture2DArray ColorArrayTexture,
        XRTexture2DArray DepthArrayTexture,
        XRTexture2DArrayView LeftColorView,
        XRTexture2DArrayView RightColorView,
        XRTexture2D? LeftPreviewTexture,
        XRTexture2D? RightPreviewTexture,
        VkImage LeftSwapchainImage,
        VkImage RightSwapchainImage,
        VkFormat LeftSwapchainFormat,
        VkFormat RightSwapchainFormat,
        VkExtent2D Extent,
        uint LeftImageIndex,
        uint RightImageIndex,
        int FrameId);

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
        ValidateSessionVulkanGraphicsDevice(renderer, useEnable2Binding, vulkanExtension, vulkan2Extension);

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

    private void ValidateSessionVulkanGraphicsDevice(
        VulkanRenderer renderer,
        bool useEnable2Binding,
        KhrVulkanEnable? vulkanExtension,
        KhrVulkanEnable2? vulkan2Extension)
    {
        VkHandle requestedDevice = default;
        Result deviceResult;

        if (useEnable2Binding && vulkan2Extension is not null)
        {
            VulkanGraphicsDeviceGetInfoKHR deviceGetInfo = new()
            {
                Type = StructureType.VulkanGraphicsDeviceGetInfoKhr,
                SystemId = _systemId,
                VulkanInstance = new VkHandle(renderer.Instance.Handle)
            };

            deviceResult = vulkan2Extension.GetVulkanGraphicsDevice2(
                _instance,
                ref deviceGetInfo,
                ref requestedDevice);
        }
        else if (vulkanExtension is not null)
        {
            deviceResult = vulkanExtension.GetVulkanGraphicsDevice(
                _instance,
                _systemId,
                new VkHandle(renderer.Instance.Handle),
                ref requestedDevice);
        }
        else
        {
            throw new Exception("Cannot validate OpenXR Vulkan graphics device because no Vulkan OpenXR extension was loaded.");
        }

        if (deviceResult != Result.Success)
            throw new Exception($"Failed to query OpenXR Vulkan graphics device before session creation: {deviceResult}");

        if (requestedDevice.Handle == 0)
            throw new Exception("OpenXR runtime returned a zero Vulkan physical-device handle before session creation.");

        if ((nint)requestedDevice.Handle != (nint)renderer.PhysicalDevice.Handle)
        {
            throw new Exception(
                "OpenXR runtime-selected Vulkan physical device does not match the renderer device. " +
                $"Runtime=0x{(nuint)requestedDevice.Handle:X}, Renderer=0x{(nuint)renderer.PhysicalDevice.Handle:X}.");
        }

        Debug.Vulkan(
            "[OpenXR] Session instance confirmed Vulkan graphics device 0x{0:X} before xrCreateSession.",
            (nuint)requestedDevice.Handle);
    }

    private bool TryResolveOpenXrViewRenderModeForCurrentBackend(out VrViewRenderModeResolution resolution)
    {
        ERenderLibrary backend = Window?.Renderer is VulkanRenderer
            ? ERenderLibrary.Vulkan
            : ERenderLibrary.OpenGL;

        bool trueSinglePassStereoAvailable =
            RuntimeRenderingHostServices.Current.VrViewRenderMode == EVrViewRenderMode.SinglePassStereo &&
            backend == ERenderLibrary.Vulkan &&
            CanUseOpenXrTrueSinglePassStereo(out _);

        resolution = VrViewRenderModeResolver.Resolve(
            backend,
            RuntimeRenderingHostServices.Current.VrViewRenderMode,
            RuntimeRenderingHostServices.Current.EnableOpenXrVulkanParallelRendering,
            trueSinglePassStereoAvailable,
            rendersExternalSwapchainTargets: !trueSinglePassStereoAvailable);
        RecordSmokeViewRenderModeResolution(resolution);
        LogOpenXrViewRenderModeResolution(backend, resolution);

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

    public bool CanUseTrueSinglePassStereo
        => CanUseOpenXrTrueSinglePassStereo(out _);

    private bool CanUseOpenXrTrueSinglePassStereo(out string reason)
    {
        if (Window?.Renderer is not VulkanRenderer renderer)
        {
            reason = "renderer is not Vulkan";
            return false;
        }

        if (!renderer.UseDynamicRenderingRenderTargets)
        {
            reason = "Vulkan dynamic rendering is required for OpenXR true single-pass stereo multiview";
            return false;
        }

        if (OpenXrVulkanMirrorFbo)
        {
            reason = $"{XREngineEnvironmentVariables.OpenXrVulkanMirrorFbo}=1 forces per-eye mirror FBO compatibility";
            return false;
        }

        if (!OpenXrVulkanTrueStereoOverride)
        {
            reason = $"OpenXR Vulkan true single-pass stereo is disabled by default while the multiview staging path is stabilized; set {XREngineEnvironmentVariables.OpenXrVulkanTrueStereo}=1 to opt in for diagnostics";
            return false;
        }

        if (!OpenXrVulkanTrueStereoOverride && IsSteamVrOpenXrRuntime())
        {
            reason = $"SteamVR OpenXR Vulkan uses the per-eye compatibility path by default; the true-stereo publish path can be enabled for diagnostics with {XREngineEnvironmentVariables.OpenXrVulkanTrueStereo}=1";
            return false;
        }

        if (_viewCount != 2)
        {
            reason = $"view count is {_viewCount}, expected 2";
            return false;
        }

        if (!RuntimeEngine.Rendering.State.HasVulkanMultiView)
        {
            reason = "Vulkan multiview support is not available";
            return false;
        }

        uint leftWidth = GetOpenXrSwapchainWidth(0);
        uint leftHeight = GetOpenXrSwapchainHeight(0);
        uint rightWidth = GetOpenXrSwapchainWidth(1);
        uint rightHeight = GetOpenXrSwapchainHeight(1);
        if (leftWidth == 0 || leftHeight == 0 || rightWidth == 0 || rightHeight == 0)
        {
            reason = "OpenXR view dimensions are not initialized";
            return false;
        }

        if (leftWidth != rightWidth || leftHeight != rightHeight)
        {
            reason = $"OpenXR eye dimensions differ ({leftWidth}x{leftHeight} vs {rightWidth}x{rightHeight})";
            return false;
        }

        if (_vulkanOpenXrSwapchainFormats[0] == 0 || _vulkanOpenXrSwapchainFormats[1] == 0)
        {
            reason = "OpenXR Vulkan swapchain formats are not selected";
            return false;
        }

        if (!TryResolveVulkanStereoColorTextureFormat(
                (VkFormat)_vulkanOpenXrSwapchainFormats[0],
                (VkFormat)_vulkanOpenXrSwapchainFormats[1],
                out _,
                out _,
                out _,
                out reason))
        {
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryResolveVulkanStereoColorTextureFormat(
        VkFormat leftSwapchainFormat,
        VkFormat rightSwapchainFormat,
        out EPixelInternalFormat internalFormat,
        out EPixelFormat pixelFormat,
        out ESizedInternalFormat sizedFormat,
        out string reason)
    {
        internalFormat = EPixelInternalFormat.Rgba8;
        pixelFormat = EPixelFormat.Rgba;
        sizedFormat = ESizedInternalFormat.Rgba8;
        reason = string.Empty;

        if (leftSwapchainFormat != rightSwapchainFormat)
        {
            reason = $"OpenXR eye swapchain formats differ ({leftSwapchainFormat} vs {rightSwapchainFormat})";
            return false;
        }

        switch (leftSwapchainFormat)
        {
            case VkFormat.R8G8B8A8Srgb:
                internalFormat = EPixelInternalFormat.Srgb8Alpha8;
                sizedFormat = ESizedInternalFormat.Srgb8Alpha8;
                return true;
            case VkFormat.R8G8B8A8Unorm:
                internalFormat = EPixelInternalFormat.Rgba8;
                sizedFormat = ESizedInternalFormat.Rgba8;
                return true;
            default:
                reason = $"OpenXR true stereo staging does not have a native engine texture format for {leftSwapchainFormat}";
                return false;
        }
    }

    private void EnsureVulkanOpenXrPreviewTargets(VulkanRenderer renderer, uint width, uint height)
    {
        if (TryResolveVulkanStereoColorTextureFormat(
                (VkFormat)_vulkanOpenXrSwapchainFormats[0],
                (VkFormat)_vulkanOpenXrSwapchainFormats[1],
                out EPixelInternalFormat internalFormat,
                out _,
                out ESizedInternalFormat sizedFormat,
                out _))
        {
            EnsureOpenXrPreviewTargets(renderer, width, height, internalFormat, sizedFormat);
            return;
        }

        EnsureVulkanOpenXrPreviewTargets(renderer, width, height);
    }

    private void LogOpenXrViewRenderModeResolution(
        ERenderLibrary backend,
        VrViewRenderModeResolution resolution)
    {
        Debug.RenderingEvery(
            $"OpenXR.ViewRenderMode.Resolved.{backend}.{resolution.RequestedMode}.{resolution.EffectiveMode}",
            TimeSpan.FromSeconds(2),
            "[OpenXR] ViewRenderMode requested={0} effective={1} backend={2} supported={3} path={4} temporalHistoryPolicy={5} parallelGate={6} swapchainFormats={7} trueStereoMultiviewSupport={8}",
            resolution.RequestedMode,
            resolution.EffectiveMode,
            backend,
            resolution.IsSupported,
            resolution.EffectiveImplementationPath,
            resolution.TemporalHistoryPolicy,
            RuntimeRenderingHostServices.Current.EnableOpenXrVulkanParallelRendering,
            DescribeOpenXrSwapchainFormats(backend),
            DescribeOpenXrTrueStereoMultiviewSupport());
    }

    private string DescribeOpenXrSwapchainFormats(ERenderLibrary backend)
    {
        if (backend != ERenderLibrary.Vulkan)
            return "n/a";

        long left = _vulkanOpenXrSwapchainFormats[0];
        long right = _vulkanOpenXrSwapchainFormats[1];
        return left == 0 && right == 0
            ? "Vulkan(left=<unselected>,right=<unselected>)"
            : $"Vulkan(left={(VkFormat)left}/{left},right={(VkFormat)right}/{right})";
    }

    private string DescribeOpenXrTrueStereoMultiviewSupport()
    {
        _ = CanUseOpenXrTrueSinglePassStereo(out string reason);
        return $"available={string.IsNullOrWhiteSpace(reason)}," +
               $"reason={(string.IsNullOrWhiteSpace(reason) ? "<none>" : reason)}," +
               $"isStereoPass={RuntimeEngine.Rendering.State.IsStereoPass}," +
               $"vulkanMultiview={RuntimeEngine.Rendering.State.HasVulkanMultiView}," +
               $"ovrMultiview={RuntimeEngine.Rendering.State.HasOvrMultiViewExtension}," +
               $"dynamicRendering={(Window?.Renderer is VulkanRenderer renderer && renderer.UseDynamicRenderingRenderTargets)}," +
               $"override={OpenXrVulkanTrueStereoOverride}";
    }

    private static bool IsSteamVrOpenXrRuntime()
    {
        string? runtimePath = Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.XrRuntimeJson);
        if (string.IsNullOrWhiteSpace(runtimePath))
            runtimePath = TryGetOpenXRActiveRuntime();

        return !string.IsNullOrWhiteSpace(runtimePath) &&
               (runtimePath.Contains("steamvr", StringComparison.OrdinalIgnoreCase) ||
                runtimePath.Contains("steamxr", StringComparison.OrdinalIgnoreCase));
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

        InitializeOpenXrViewsForActiveConfiguration("OpenXR Vulkan");

        // Create swapchains for each view
        for (int i = 0; i < _viewCount; i++)
        {
            OpenXrEyeSwapchainExtent extent = ResolveOpenXrEyeSwapchainExtent((uint)i);
            LogOpenXrEyeSwapchainExtent("Vulkan", (uint)i, extent);
            uint width = extent.Width;
            uint height = extent.Height;
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
                            RecordOpenXrSwapchainExtent((uint)i, width, height);
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

        uint width = GetOpenXrSwapchainWidth(viewIndex);
        uint height = GetOpenXrSwapchainHeight(viewIndex);
        if (OpenXrVulkanMirrorFbo)
            EnsureVulkanEyeMirrorTargets(renderer, width, height);
        EnsureVulkanOpenXrPreviewTargets(renderer, width, height);

            if (OpenXrDebugClearOnly)
            {
                ColorF4 clearColor = IsLeftEyeLikeOpenXrView(viewIndex)
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

        EnsureOpenXrViewports(
            GetOpenXrSwapchainWidth(0),
            GetOpenXrSwapchainHeight(0),
            GetOpenXrSwapchainWidth(1),
            GetOpenXrSwapchainHeight(1));

        XRViewport? eyeViewport = GetOpenXrEyeViewport(viewIndex);
        XRCamera? eyeCamera = GetOpenXrEyeCamera(viewIndex);
        if (eyeViewport is null || eyeCamera is null)
            return LogVulkanEyeRenderNotReady(viewIndex, imageIndex, "no eye viewport/camera");
        EnsureOpenXrViewportExtent(eyeViewport, width, height);
        ValidateOpenXrEyeViewportExtent(eyeViewport, viewIndex, width, height);

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

            int eyeTargetIndex = IsLeftEyeLikeOpenXrView(viewIndex) ? 0 : 1;
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

    private bool TryEnsureVulkanStereoRenderTarget(
        VulkanRenderer renderer,
        uint width,
        uint height,
        VkImage leftSwapchainImage,
        VkImage rightSwapchainImage,
        VkFormat leftSwapchainFormat,
        VkFormat rightSwapchainFormat,
        uint leftImageIndex,
        uint rightImageIndex,
        out OpenXrStereoRenderTarget target)
    {
        target = default;
        width = Math.Max(width, 1u);
        height = Math.Max(height, 1u);

        EnsureOpenXrStereoViewport(width, height);

        if (_vulkanStereoFbo is null ||
            _vulkanStereoColorArray is null ||
            _vulkanStereoDepthArray is null ||
            _vulkanStereoLeftColorView is null ||
            _vulkanStereoRightColorView is null ||
            _vulkanStereoWidth != width ||
            _vulkanStereoHeight != height ||
            _vulkanStereoColorFormat != leftSwapchainFormat)
        {
            if (!TryResolveVulkanStereoColorTextureFormat(
                    leftSwapchainFormat,
                    rightSwapchainFormat,
                    out EPixelInternalFormat colorInternalFormat,
                    out EPixelFormat colorPixelFormat,
                    out ESizedInternalFormat colorSizedFormat,
                    out string formatReason))
            {
                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.TrueStereo.UnsupportedStagingFormat.{GetHashCode()}.{leftSwapchainFormat}.{rightSwapchainFormat}",
                    TimeSpan.FromSeconds(2),
                    "[OpenXR] Vulkan true stereo render target cannot be created for swapchain formats {0}/{1}: {2}",
                    leftSwapchainFormat,
                    rightSwapchainFormat,
                    formatReason);
                return false;
            }

            DestroyVulkanStereoRenderTarget();
            CreateVulkanStereoRenderTarget(
                renderer,
                width,
                height,
                leftSwapchainFormat,
                colorInternalFormat,
                colorPixelFormat,
                colorSizedFormat);
        }

        if (_vulkanStereoFbo is null ||
            _vulkanStereoColorArray is null ||
            _vulkanStereoDepthArray is null ||
            _vulkanStereoLeftColorView is null ||
            _vulkanStereoRightColorView is null)
        {
            return false;
        }

        target = new OpenXrStereoRenderTarget(
            _vulkanStereoFbo,
            _vulkanStereoColorArray,
            _vulkanStereoDepthArray,
            _vulkanStereoLeftColorView,
            _vulkanStereoRightColorView,
            _previewLeftEyeTexture,
            _previewRightEyeTexture,
            leftSwapchainImage,
            rightSwapchainImage,
            leftSwapchainFormat,
            rightSwapchainFormat,
            new VkExtent2D(width, height),
            leftImageIndex,
            rightImageIndex,
            Volatile.Read(ref _openXrPendingFrameNumber));
        return true;
    }

    private void EnsureOpenXrStereoViewport(uint width, uint height)
    {
        _openXrStereoViewport ??= new XRViewport(null)
        {
            AutomaticallyCollectVisible = false,
            AutomaticallySwapBuffers = false,
            AllowUIRender = false,
            SetRenderPipelineFromCamera = false,
            RendersToExternalSwapchainTarget = true
        };

        // The true single-pass stereo viewport renders into a renderer-owned array
        // staging FBO, but its extent is dictated by the OpenXR runtime. Treat it
        // as an external target so resource generation catches up synchronously
        // before the array layers are published to acquired swapchain images.
        _openXrStereoViewport.RendersToExternalSwapchainTarget = true;
        _openXrStereoViewport.CullWithFrustum = RuntimeEngine.Rendering.Settings.OpenXrCullWithFrustum;
        _openXrStereoViewport.SetFullScreen();
        if (_openXrStereoViewport.Width != (int)width || _openXrStereoViewport.Height != (int)height)
        {
            _openXrStereoViewport.Resize(width, height, setInternalResolution: false);
            _openXrStereoViewport.SetInternalResolution((int)width, (int)height, correctAspect: false);
        }
        else if (_openXrStereoViewport.InternalWidth != (int)width || _openXrStereoViewport.InternalHeight != (int)height)
        {
            _openXrStereoViewport.SetInternalResolution((int)width, (int)height, correctAspect: false);
        }
    }

    private void CreateVulkanStereoRenderTarget(
        VulkanRenderer renderer,
        uint width,
        uint height,
        VkFormat swapchainFormat,
        EPixelInternalFormat colorInternalFormat,
        EPixelFormat colorPixelFormat,
        ESizedInternalFormat colorSizedFormat)
    {
        XRTexture2DArray color = XRTexture2DArray.CreateFrameBufferTexture(
            2,
            width,
            height,
            colorInternalFormat,
            colorPixelFormat,
            EPixelType.UnsignedByte,
            EFrameBufferAttachment.ColorAttachment0);
        color.Name = "OpenXRVulkanStereoColorArray";
        color.Resizable = false;
        color.SizedInternalFormat = colorSizedFormat;
        color.OVRMultiViewParameters = new(0, 2u);
        color.MinFilter = ETexMinFilter.Linear;
        color.MagFilter = ETexMagFilter.Linear;

        XRTexture2DArray depth = XRTexture2DArray.CreateFrameBufferTexture(
            2,
            width,
            height,
            EPixelInternalFormat.DepthComponent24,
            EPixelFormat.DepthComponent,
            EPixelType.UnsignedInt,
            EFrameBufferAttachment.DepthAttachment);
        depth.Name = "OpenXRVulkanStereoDepthArray";
        depth.Resizable = false;
        depth.SizedInternalFormat = ESizedInternalFormat.DepthComponent24;
        depth.OVRMultiViewParameters = new(0, 2u);

        XRTexture2DArrayView leftView = new(color, 0u, 1u, 0u, 1u, colorSizedFormat, array: false, multisample: false)
        {
            Name = "OpenXRVulkanStereoLeftColorView"
        };
        XRTexture2DArrayView rightView = new(color, 0u, 1u, 1u, 1u, colorSizedFormat, array: false, multisample: false)
        {
            Name = "OpenXRVulkanStereoRightColorView"
        };

        XRFrameBuffer fbo = new(
            (color, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (depth, EFrameBufferAttachment.DepthAttachment, 0, -1))
        {
            Name = "OpenXRVulkanStereoFBO"
        };

        _vulkanStereoColorArray = color;
        _vulkanStereoDepthArray = depth;
        _vulkanStereoLeftColorView = leftView;
        _vulkanStereoRightColorView = rightView;
        _vulkanStereoFbo = fbo;
        _vulkanStereoWidth = width;
        _vulkanStereoHeight = height;
        _vulkanStereoColorFormat = swapchainFormat;

        renderer.GetOrCreateAPIRenderObject(color, generateNow: true);
        renderer.GetOrCreateAPIRenderObject(depth, generateNow: true);
        renderer.GetOrCreateAPIRenderObject(leftView, generateNow: true);
        renderer.GetOrCreateAPIRenderObject(rightView, generateNow: true);
        renderer.GetOrCreateAPIRenderObject(fbo, generateNow: true);
    }

    private void DestroyVulkanStereoRenderTarget()
    {
        try
        {
            _vulkanStereoFbo?.Destroy();
            _vulkanStereoLeftColorView?.Destroy();
            _vulkanStereoRightColorView?.Destroy();
            _vulkanStereoDepthArray?.Destroy();
            _vulkanStereoColorArray?.Destroy();
        }
        catch
        {
            // Best-effort cleanup.
        }

        _vulkanStereoFbo = null;
        _vulkanStereoLeftColorView = null;
        _vulkanStereoRightColorView = null;
        _vulkanStereoDepthArray = null;
        _vulkanStereoColorArray = null;
        _vulkanStereoWidth = 0;
        _vulkanStereoHeight = 0;
        _vulkanStereoColorFormat = VkFormat.Undefined;
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

        uint leftWidth = GetOpenXrSwapchainWidth(0);
        uint leftHeight = GetOpenXrSwapchainHeight(0);
        uint rightWidth = GetOpenXrSwapchainWidth(1);
        uint rightHeight = GetOpenXrSwapchainHeight(1);
        if (leftWidth != rightWidth || leftHeight != rightHeight)
            return false;

        handled = true;
        uint leftImageIndex = 0;
        uint rightImageIndex = 0;
        bool leftAcquired = false;
        bool rightAcquired = false;
        bool allowSequentialFallback = false;
        int frameNo = Volatile.Read(ref _openXrPendingFrameNumber);
        bool trueSinglePassStereo =
            modeResolution.EffectiveImplementationPath == EVrViewRenderImplementationPath.TrueSinglePassStereo;

        try
        {
            bool collectedTrueSinglePassStereo =
                Volatile.Read(ref _pendingXrFrameUsesTrueSinglePassStereo) != 0;
            if (collectedTrueSinglePassStereo != trueSinglePassStereo)
            {
                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.ViewRenderMode.FrameModeMismatch.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] Skipping OpenXR eye submission because visibility was collected for {0} but render mode is now {1}. The next collected frame will use the new mode.",
                    collectedTrueSinglePassStereo ? "true single-pass stereo" : "external per-eye rendering",
                    trueSinglePassStereo ? "true single-pass stereo" : modeResolution.EffectiveMode.ToString());
                return false;
            }

            if (trueSinglePassStereo)
                ReleaseOpenXrExternalEyeViewportPipelinesForTrueStereo();
            else
                ReleaseOpenXrStereoViewportPipelineForExternalEyes();

            if (!trueSinglePassStereo)
            {
                using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.Prewarm"))
                {
                    if (ShouldPrewarmVulkanEyeResources(0))
                        PrewarmVulkanEyeResources(0);
                    if (ShouldPrewarmVulkanEyeResources(1))
                        PrewarmVulkanEyeResources(1);
                }
            }

            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.AcquireWaitLeft"))
            {
                if (!AcquireAndWaitOpenXrEyeImage(0, ref leftImageIndex, ref leftAcquired, frameNo))
                {
                    allowSequentialFallback = true;
                    return false;
                }
            }

            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.AcquireWaitRight"))
            {
                if (!AcquireAndWaitOpenXrEyeImage(1, ref rightImageIndex, ref rightAcquired, frameNo))
                {
                    allowSequentialFallback = true;
                    return false;
                }
            }

            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.RenderSwapchains"))
            {
                bool rendered = modeResolution.EffectiveMode switch
                {
                    EVrViewRenderMode.SinglePassStereo => TryRenderVulkanEyeSinglePassStereoToSwapchains(
                        renderer,
                        leftImageIndex,
                        rightImageIndex,
                        modeResolution),
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
                {
                    allowSequentialFallback = true;
                    Debug.VulkanWarningEvery(
                        $"OpenXR.Vulkan.Batch.SequentialFallback.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[OpenXR] Vulkan batched eye render returned false; releasing acquired images and falling back to sequential eye rendering for this frame.");
                    return false;
                }
            }

            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.FillProjectionViews"))
            {
                FillProjectionView(0, projectionViews);
                FillProjectionView(1, projectionViews);
            }
            return true;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            allowSequentialFallback = true;
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.Batch.ExceptionSequentialFallback.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Vulkan batched eye render failed; releasing acquired images and falling back to sequential eye rendering for this frame. {0}",
                ex.Message);
            return false;
        }
        finally
        {
            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.ReleaseEyes"))
            {
                ReleaseOpenXrEyeImageIfAcquired(0, leftAcquired, frameNo);
                ReleaseOpenXrEyeImageIfAcquired(1, rightAcquired, frameNo);
            }

            if (allowSequentialFallback)
                handled = false;
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
        uint rightImageIndex,
        VrViewRenderModeResolution modeResolution)
    {
        if (modeResolution.EffectiveImplementationPath == EVrViewRenderImplementationPath.TrueSinglePassStereo)
        {
            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.TrueSinglePassStereo.RenderAndPublish"))
            {
                if (TryRenderVulkanTrueSinglePassStereoToSwapchains(renderer, leftImageIndex, rightImageIndex))
                    return true;
            }

            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.TrueSinglePassStereo.Skipped.{GetHashCode()}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] True SinglePassStereo did not render this frame; skipping eye submission instead of allocating per-eye fallback resources.");
            return false;
        }
        else
        {
            _ = CanUseOpenXrTrueSinglePassStereo(out string unavailableReason);
            Debug.VulkanEvery(
                $"OpenXR.Vulkan.ViewRenderMode.SinglePassStereo.Compatibility.{GetHashCode()}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] VR.ViewRenderMode=SinglePassStereo selected; using OpenXR per-eye swapchain compatibility path. TrueStereoUnavailable={0}",
                string.IsNullOrWhiteSpace(unavailableReason) ? "not selected" : unavailableReason);
        }

        using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.SinglePassStereo.RenderSwapchains"))
            return TryRenderVulkanEyeBatchToSwapchains(
                renderer,
                leftImageIndex,
                rightImageIndex,
                EVrViewRenderMode.SinglePassStereo);
    }

    private bool TryRenderVulkanTrueSinglePassStereoToSwapchains(
        VulkanRenderer renderer,
        uint leftImageIndex,
        uint rightImageIndex)
    {
        if (!CanUseOpenXrTrueSinglePassStereo(out string unavailableReason))
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.TrueSinglePassStereo.Unavailable.{GetHashCode()}",
                TimeSpan.FromSeconds(2),
                "[OpenXR] True SinglePassStereo is unavailable: {0}.",
                unavailableReason);
            return false;
        }

        SwapchainImageVulkan2KHR* leftImages = _swapchainImagesVK[0];
        SwapchainImageVulkan2KHR* rightImages = _swapchainImagesVK[1];
        if (leftImages == null || rightImages == null)
            return false;
        if (leftImageIndex >= _swapchainImageCounts[0] || rightImageIndex >= _swapchainImageCounts[1])
            return false;

        if (_openXrFrameWorld is null)
            return LogVulkanEyeRenderNotReady(0, leftImageIndex, "no frame world");
        if (_openXrLeftEyeCamera is null || _openXrRightEyeCamera is null)
            return LogVulkanEyeRenderNotReady(0, leftImageIndex, "no eye cameras");

        uint width = GetOpenXrSwapchainWidth(0);
        uint height = GetOpenXrSwapchainHeight(0);
        EnsureVulkanOpenXrPreviewTargets(renderer, width, height);
        EnsureOpenXrViewports(
            GetOpenXrSwapchainWidth(0),
            GetOpenXrSwapchainHeight(0),
            GetOpenXrSwapchainWidth(1),
            GetOpenXrSwapchainHeight(1));

        XRViewport? leftViewport = _openXrLeftViewport;
        XRViewport? rightViewport = _openXrRightViewport;
        XRCamera leftCamera = _openXrLeftEyeCamera;
        XRCamera rightCamera = _openXrRightEyeCamera;
        if (leftViewport is null || rightViewport is null)
            return LogVulkanEyeRenderNotReady(0, leftImageIndex, "no eye viewport");
        ValidateOpenXrEyeViewportExtent(leftViewport, 0, GetOpenXrSwapchainWidth(0), GetOpenXrSwapchainHeight(0));
        ValidateOpenXrEyeViewportExtent(rightViewport, 1, GetOpenXrSwapchainWidth(1), GetOpenXrSwapchainHeight(1));

        VkExtent2D extent = new(width, height);
        VkImage leftImage = new(leftImages[leftImageIndex].Image);
        VkImage rightImage = new(rightImages[rightImageIndex].Image);
        VkFormat leftFormat = (VkFormat)_vulkanOpenXrSwapchainFormats[0];
        VkFormat rightFormat = (VkFormat)_vulkanOpenXrSwapchainFormats[1];

        if (!TryEnsureVulkanStereoRenderTarget(
                renderer,
                width,
                height,
                leftImage,
                rightImage,
                leftFormat,
                rightFormat,
                leftImageIndex,
                rightImageIndex,
                out OpenXrStereoRenderTarget target))
        {
            return false;
        }

        XRViewport stereoViewport = _openXrStereoViewport!;
        stereoViewport.WorldInstanceOverride = _openXrFrameWorld;
        stereoViewport.Camera = leftCamera;

        RenderPipeline sourcePipeline =
            leftViewport.RenderPipelineInstance.AssignedPipeline ??
            leftCamera.RenderPipeline ??
            RuntimeEngine.Rendering.NewRenderPipeline(stereo: false);
        RenderPipeline stereoPipeline = GetOrCreateOpenXrStereoPipeline(sourcePipeline);
        if (!ReferenceEquals(stereoViewport.RenderPipeline, stereoPipeline))
            stereoViewport.RenderPipeline = stereoPipeline;

        stereoViewport.MeshRenderCommandsOverride = null;
        var stereoMeshCommands = stereoViewport.RenderPipelineInstance.MeshRenderCommands;
        int stereoRenderingCommands = stereoMeshCommands.GetRenderingCommandCount();
        if (stereoRenderingCommands == 0)
        {
            Debug.VulkanWarningEvery(
                $"OpenXR.Vulkan.TrueSinglePassStereo.NoRenderingCommands.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Vulkan true stereo render has no swapped mesh commands. pipeline='{0}' viewport={1}x{2}",
                stereoPipeline.DebugName,
                stereoViewport.InternalWidth,
                stereoViewport.InternalHeight);
        }

        leftCamera.RenderPipeline = stereoPipeline;
        rightCamera.RenderPipeline = stereoPipeline;
        CopyPostProcessState(sourcePipeline, stereoPipeline, leftCamera, leftCamera);
        CopyPostProcessState(sourcePipeline, stereoPipeline, rightCamera, rightCamera);

        ApplyOpenXrEyePoseForRenderThread(0);
        ApplyOpenXrEyePoseForRenderThread(1);

        if (VulkanCaptureEyeOutputs)
        {
            Debug.VulkanEvery(
                $"OpenXR.Vulkan.TrueStereoRenderState.{GetHashCode()}",
                TimeSpan.FromSeconds(1),
                "[OpenXR] Vulkan true stereo render state leftImage={0} rightImage={1} extent={2}x{3} color='{4}' depth='{5}' pipeline='{6}' world='{7}'",
                leftImageIndex,
                rightImageIndex,
                width,
                height,
                target.ColorArrayTexture.Name ?? "<unnamed color>",
                target.DepthArrayTexture.Name ?? "<unnamed depth>",
                stereoPipeline.DebugName,
                _openXrFrameWorld.TargetWorldName ?? "<unnamed world>");
        }

        var previousRenderer = AbstractRenderer.Current;
        bool previousRendererActive = renderer.Active;
        try
        {
            renderer.Active = true;
            AbstractRenderer.Current = renderer;

            var renderRequest = new VulkanRenderer.OpenXrEyeMirrorRenderRequest(
                target.FrameBuffer,
                target.Extent,
                ResourcePlannerStateIndex: 0,
                OpenXrViewIndex: 0,
                OpenXrImageIndex: leftImageIndex,
                EmitFrameOps: () =>
                {
                    stereoViewport.RenderStereo(target.FrameBuffer, leftCamera, rightCamera, _openXrFrameWorld);
                },
                RendersExternalSwapchainTarget: false);

            bool stereoRenderedAndPublished = renderer.TryRenderAndBlitTextureArrayLayersToOpenXrSwapchainImages(
                in renderRequest,
                stereoViewport.RenderPipelineInstance,
                target.ColorArrayTexture,
                target.LeftSwapchainImage,
                target.LeftSwapchainFormat,
                target.Extent,
                $"true stereo left eye swapchain image {leftImageIndex}",
                target.RightSwapchainImage,
                target.RightSwapchainFormat,
                target.Extent,
                $"true stereo right eye swapchain image {rightImageIndex}",
                flipY: false);

            if (!stereoRenderedAndPublished)
            {
                if (!stereoViewport.RenderPipelineInstance.SkippedResizeCatchUpThisFrame)
                {
                    Debug.VulkanWarningEvery(
                        $"OpenXR.Vulkan.TrueSinglePassStereo.RenderFailed.{GetHashCode()}",
                        TimeSpan.FromSeconds(1),
                        "[OpenXR] Vulkan true stereo render+publish failed. fbo='{0}' color='{1}' extent={2}x{3} pipeline='{4}'",
                        target.FrameBuffer.Name ?? "<unnamed FBO>",
                        target.ColorArrayTexture.Name ?? "<unnamed color>",
                        target.Extent.Width,
                        target.Extent.Height,
                        stereoPipeline.DebugName);
                    return false;
                }

                Debug.VulkanWarningEvery(
                    $"OpenXR.Vulkan.TrueSinglePassStereo.ResizeCatchUpSkipped.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] Vulkan true stereo render skipped during resource resize catch-up; not publishing unrendered stereo array layers. fbo='{0}' extent={1}x{2} pipeline='{3}'",
                    target.FrameBuffer.Name ?? "<unnamed FBO>",
                    target.Extent.Width,
                    target.Extent.Height,
                    stereoPipeline.DebugName);
                return false;
            }

            if (VulkanCaptureEyeOutputs || OpenXrDebugLifecycle || XREngine.Rendering.RenderDiagnosticsFlags.VkTraceSwapDraw)
            {
                Debug.VulkanEvery(
                    $"OpenXR.Vulkan.TrueSinglePassStereo.Rendered.{GetHashCode()}",
                    TimeSpan.FromSeconds(1),
                    "[OpenXR] Vulkan true stereo render+publish completed. fbo='{0}' color='{1}' depth='{2}' extent={3}x{4} leftImage={5} rightImage={6} pipeline='{7}' commands={8}",
                    target.FrameBuffer.Name ?? "<unnamed FBO>",
                    target.ColorArrayTexture.Name ?? "<unnamed color>",
                    target.DepthArrayTexture.Name ?? "<unnamed depth>",
                    target.Extent.Width,
                    target.Extent.Height,
                    leftImageIndex,
                    rightImageIndex,
                    stereoPipeline.DebugName,
                    stereoRenderingCommands);
            }

            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.TrueSinglePassStereo.PublishLeft"))
                PublishVulkanEyeMirror(renderer, target.LeftColorView, 0, leftImageIndex, width, height);
            using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.TrueSinglePassStereo.PublishRight"))
                PublishVulkanEyeMirror(renderer, target.RightColorView, 1, rightImageIndex, width, height);

            MarkVulkanEyeResourceWarmupComplete(0);
            MarkVulkanEyeResourceWarmupComplete(1);
            return true;
        }
        finally
        {
            AbstractRenderer.Current = previousRenderer;
            renderer.Active = previousRendererActive;
        }
    }

    private bool TryRenderVulkanEyeParallelCommandBufferRecordingToSwapchains(
        VulkanRenderer renderer,
        uint leftImageIndex,
        uint rightImageIndex)
    {
        Debug.VulkanEvery(
            $"OpenXR.Vulkan.ViewRenderMode.ParallelCommandBufferRecording.{GetHashCode()}",
            TimeSpan.FromSeconds(2),
            "[OpenXR] VR.ViewRenderMode=ParallelCommandBufferRecording selected; using the explicit worker-backed eye path with serialized shared Vulkan layout-state recording.");

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

        uint width = GetOpenXrSwapchainWidth(0);
        uint height = GetOpenXrSwapchainHeight(0);
        using (RuntimeRenderingHostServices.Current.StartProfileScope("OpenXR.Vulkan.Batch.EnsureTargets"))
        {
            if (OpenXrVulkanMirrorFbo)
                EnsureVulkanEyeMirrorTargets(renderer, width, height);
            EnsureVulkanOpenXrPreviewTargets(renderer, width, height);
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

        EnsureOpenXrViewports(
            GetOpenXrSwapchainWidth(0),
            GetOpenXrSwapchainHeight(0),
            GetOpenXrSwapchainWidth(1),
            GetOpenXrSwapchainHeight(1));

        XRViewport? leftViewport = _openXrLeftViewport;
        XRViewport? rightViewport = _openXrRightViewport;
        XRCamera leftCamera = _openXrLeftEyeCamera;
        XRCamera rightCamera = _openXrRightEyeCamera;
        if (leftViewport is null || rightViewport is null)
            return LogVulkanEyeRenderNotReady(0, leftImageIndex, "no eye viewport/camera");
        ValidateOpenXrEyeViewportExtent(leftViewport, 0, GetOpenXrSwapchainWidth(0), GetOpenXrSwapchainHeight(0));
        ValidateOpenXrEyeViewportExtent(rightViewport, 1, GetOpenXrSwapchainWidth(1), GetOpenXrSwapchainHeight(1));

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
                if (ShouldCopyVulkanEyeToDesktopMirror(0))
                {
                    EnsureViewportMirrorTargets(renderer, width, height);
                    bool desktopMirrorCopied = renderer.TryCopyOpenXrEyeMirrorTexture(
                        leftMirrorColor,
                        _viewportMirrorColor,
                        $"desktop mirror eye 0",
                        flipY: false);
                    LogVulkanEyeMirrorPublish(
                        leftMirrorColor,
                        _previewLeftEyeTexture,
                        0,
                        leftImageIndex,
                        width,
                        height,
                        leftPreviewCopied,
                        desktopMirrorCopied);
                    if (desktopMirrorCopied)
                        RecordSmokeDesktopMirrorComposed();
                }
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

        int index = (int)Math.Min(viewIndex, (uint)RenderFrameViewSet.MaxViewCount - 1u);
        uint width = GetOpenXrSwapchainWidth(viewIndex);
        uint height = GetOpenXrSwapchainHeight(viewIndex);
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

        int index = (int)Math.Min(viewIndex, (uint)RenderFrameViewSet.MaxViewCount - 1u);
        if (_vulkanOpenXrStartupPrewarmFramesRemaining[index] > 0)
            _vulkanOpenXrStartupPrewarmFramesRemaining[index]--;
    }

    private void PublishVulkanEyeMirror(
        VulkanRenderer renderer,
        XRTexture? sourceTexture,
        uint viewIndex,
        uint imageIndex,
        uint width,
        uint height)
    {
        if (sourceTexture is null)
            return;

        XRTexture2D? previewTexture = GetOpenXrPreviewTexture(viewIndex);
        bool shouldCopyPreview = ShouldCopyDirectVulkanEyeSwapchainPreview();
        bool copiedPreview = shouldCopyPreview &&
            renderer.TryCopyOpenXrEyeMirrorTexture(
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
        XRTexture? sourceTexture,
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
        XRTexture2D? previewTexture = GetOpenXrPreviewTexture(viewIndex);
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

        uint width = GetOpenXrSwapchainWidth(viewIndex);
        uint height = GetOpenXrSwapchainHeight(viewIndex);
        if (OpenXrVulkanMirrorFbo)
            EnsureVulkanEyeMirrorTargets(renderer, width, height);
        EnsureOpenXrViewports(
            GetOpenXrSwapchainWidth(0),
            GetOpenXrSwapchainHeight(0),
            GetOpenXrSwapchainWidth(1),
            GetOpenXrSwapchainHeight(1));

        XRViewport? eyeViewport = GetOpenXrEyeViewport(viewIndex);
        XRCamera? eyeCamera = GetOpenXrEyeCamera(viewIndex);
        if (eyeViewport is null || eyeCamera is null)
            return;
        EnsureOpenXrViewportExtent(eyeViewport, width, height);
        ValidateOpenXrEyeViewportExtent(eyeViewport, viewIndex, width, height);

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

            int eyeTargetIndex = IsLeftEyeLikeOpenXrView(viewIndex) ? 0 : 1;
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

    private static void ValidateOpenXrEyeViewportExtent(
        XRViewport viewport,
        uint viewIndex,
        uint expectedWidth,
        uint expectedHeight)
    {
        if (expectedWidth == 0 || expectedHeight == 0)
            throw new InvalidOperationException($"OpenXR eye {viewIndex} requires a non-zero swapchain extent.");
        if (expectedWidth > int.MaxValue || expectedHeight > int.MaxValue)
            throw new InvalidOperationException($"OpenXR eye {viewIndex} swapchain extent {expectedWidth}x{expectedHeight} exceeds supported viewport dimensions.");

        int width = (int)expectedWidth;
        int height = (int)expectedHeight;
        if (viewport.Width == width &&
            viewport.Height == height &&
            viewport.InternalWidth == width &&
            viewport.InternalHeight == height &&
            viewport.RendersToExternalSwapchainTarget &&
            viewport.Window is null)
        {
            return;
        }

        throw new InvalidOperationException(
            $"OpenXR eye {viewIndex} viewport does not exactly match the swapchain extent. " +
            $"Expected={width}x{height}; Viewport={viewport.Width}x{viewport.Height}; " +
            $"Internal={viewport.InternalWidth}x{viewport.InternalHeight}; " +
            $"ExternalTarget={viewport.RendersToExternalSwapchainTarget}; " +
            $"WindowAttached={viewport.Window is not null}.");
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
