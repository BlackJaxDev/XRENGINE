using Silk.NET.Vulkan;
using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using XREngine.Rendering.Vulkan;

namespace XREngine.Rendering.DLSS
{
    public static partial class NvidiaDlssManager
    {
        internal static class Native
        {
            private const string StreamlineLibrary = "sl.interposer.dll";
            private const ulong StreamlineSdkVersion = 0x0002000B0001FEDC;
            private const string StreamlineProjectId = "f61b5f80-6a02-4c83-8bb2-96ab8e33d2d7";
            private const uint FeatureDlss = 0;
            private const uint FeatureReflex = 3;
            private const uint FeaturePcl = 4;
            private const uint FeatureDlssG = 1000;
            private const uint BufferTypeDepth = 0;
            private const uint BufferTypeMotionVectors = 1;
            private const uint BufferTypeHudLessColor = 2;
            private const uint BufferTypeScalingInputColor = 3;
            private const uint BufferTypeScalingOutputColor = 4;
            private const uint BufferTypeExposure = 13;

            private static readonly object Sync = new();
            private static readonly SlLogMessageCallbackDelegate LogMessageCallbackDelegate = OnStreamlineLogMessage;
            private static readonly IntPtr LogMessageCallbackPtr = Marshal.GetFunctionPointerForDelegate(LogMessageCallbackDelegate);

            private static bool _initialized;
            private static bool _runtimeInitialized;
            private static bool _runtimeIncludesDlss;
            private static bool _runtimeIncludesFrameGeneration;
            private static bool _vulkanInfoInitialized;
            private static bool _featureFunctionsResolved;
            private static bool _frameGenerationFeatureFunctionsResolved;
            private static bool _frameGenerationRequirementsChecked;
            private static bool _frameGenerationRequirementsValid;
            private static bool _vulkanProxyFunctionsResolved;
            private static bool _reflexEnabled;
            private static int _activeBridgeSessions;
            private static int _activeNativeVulkanSessions;
            private static int _activeFrameGenerationProxySwapchains;
            private static nint _boundDeviceHandle;
            private static nint _boundInstanceHandle;
            private static nint _boundPhysicalDeviceHandle;
            private static IntPtr _libraryHandle;
            private static string? _lastError;
            private static string? _lastStreamlineMessage;
            private static string? _lastStreamlineWarningOrError;
            private static string? _frameGenerationRequirementsFailure;
            private static string? _terminalBridgeFailureReason;
            private static int _bridgeFailureGeneration;

            private static SlInitDelegate? _init;
            private static SlShutdownDelegate? _shutdown;
            private static SlGetFeatureRequirementsDelegate? _getFeatureRequirements;
            private static SlSetVulkanInfoDelegate? _setVulkanInfo;
            private static SlEvaluateFeatureDelegate? _evaluateFeature;
            private static SlAllocateResourcesDelegate? _allocateResources;
            private static SlFreeResourcesDelegate? _freeResources;
            private static SlSetTagForFrameDelegate? _setTagForFrame;
            private static SlSetConstantsDelegate? _setConstants;
            private static SlGetFeatureFunctionDelegate? _getFeatureFunction;
            private static SlGetNewFrameTokenDelegate? _getNewFrameToken;
            private static SlDlssSetOptionsDelegate? _setOptions;
            private static SlDlssGetOptimalSettingsDelegate? _getOptimalSettings;
            private static SlDlssGSetOptionsDelegate? _setFrameGenerationOptions;
            private static SlDlssGGetStateDelegate? _getFrameGenerationState;
            private static SlReflexSetOptionsDelegate? _setReflexOptions;
            private static SlPclSetMarkerDelegate? _setPclMarker;
            private static VkGetDeviceProcAddrProxyDelegate? _vkGetDeviceProcAddrProxy;
            private static VkCreateSwapchainKhrProxyDelegate? _vkCreateSwapchainProxy;
            private static VkDestroySwapchainKhrProxyDelegate? _vkDestroySwapchainProxy;
            private static VkGetSwapchainImagesKhrProxyDelegate? _vkGetSwapchainImagesProxy;
            private static VkAcquireNextImageKhrProxyDelegate? _vkAcquireNextImageProxy;
            private static VkQueuePresentKhrProxyDelegate? _vkQueuePresentProxy;
            private static VkDeviceWaitIdleProxyDelegate? _vkDeviceWaitIdleProxy;

            internal static bool IsAvailable
            {
                get
                {
                    lock (Sync)
                        return EnsureLibraryLoaded();
                }
            }

            internal static string? LastError
            {
                get
                {
                    lock (Sync)
                    {
                        EnsureLibraryLoaded();
                        return _lastError;
                    }
                }
            }

            internal static int BridgeFailureGeneration
            {
                get
                {
                    lock (Sync)
                        return _bridgeFailureGeneration;
                }
            }

            internal static bool HasTerminalBridgeFailure
            {
                get
                {
                    lock (Sync)
                        return !string.IsNullOrWhiteSpace(_terminalBridgeFailureReason);
                }
            }

            internal static bool IsTerminalBridgeFailureMessage(string? failureReason)
                => !string.IsNullOrWhiteSpace(failureReason)
                    && (failureReason.Contains("Streamline", StringComparison.OrdinalIgnoreCase)
                        || failureReason.Contains("slInit", StringComparison.OrdinalIgnoreCase)
                        || failureReason.Contains("slGetFeatureRequirements", StringComparison.OrdinalIgnoreCase)
                        || failureReason.Contains("slSetVulkanInfo", StringComparison.OrdinalIgnoreCase)
                        || failureReason.Contains("slAllocateResources", StringComparison.OrdinalIgnoreCase)
                        || failureReason.Contains("slDLSS", StringComparison.OrdinalIgnoreCase)
                        || failureReason.Contains("slGetNewFrameToken", StringComparison.OrdinalIgnoreCase)
                        || failureReason.Contains("slSetConstants", StringComparison.OrdinalIgnoreCase)
                        || failureReason.Contains("slEvaluateFeature", StringComparison.OrdinalIgnoreCase));

            internal readonly struct StreamlineQueueRequirements(uint graphicsQueues, uint computeQueues, uint opticalFlowQueues)
            {
                public uint GraphicsQueues { get; } = graphicsQueues;
                public uint ComputeQueues { get; } = computeQueues;
                public uint OpticalFlowQueues { get; } = opticalFlowQueues;
            }

            internal static bool TryGetRequiredVulkanRequirements(
                out string[] instanceExtensions,
                out string[] deviceExtensions,
                out string[] featureNames12,
                out string[] featureNames13,
                out StreamlineQueueRequirements queueRequirements,
                out string failureReason)
            {
                instanceExtensions = [];
                deviceExtensions = [];
                featureNames12 = [];
                featureNames13 = [];
                queueRequirements = default;
                failureReason = string.Empty;

                lock (Sync)
                {
                    if (!EnsureRuntimeInitialized(includeFrameGeneration: false, out failureReason))
                        return false;

                    StreamlineFeatureRequirements requirements = new()
                    {
                        Base = CreateBase(FeatureRequirementsStructType, 2),
                    };

                    StreamlineResult requirementsResult = _getFeatureRequirements!(FeatureDlss, ref requirements);
                    if (requirementsResult != StreamlineResult.Ok)
                    {
                        failureReason = $"slGetFeatureRequirements failed with {requirementsResult}.";
                        MarkTerminalBridgeFailure(failureReason);
                        return false;
                    }

                    if ((requirements.Flags & StreamlineFeatureRequirementFlags.VulkanSupported) == 0)
                    {
                        failureReason = "Streamline DLSS reported no Vulkan support for the current runtime configuration.";
                        MarkTerminalBridgeFailure(failureReason);
                        return false;
                    }

                    instanceExtensions = MarshalStringArray(requirements.VkInstanceExtensions, requirements.VkNumInstanceExtensions);
                    deviceExtensions = MarshalStringArray(requirements.VkDeviceExtensions, requirements.VkNumDeviceExtensions);
                    featureNames12 = MarshalStringArray(requirements.VkFeatures12, requirements.VkNumFeatures12);
                    featureNames13 = MarshalStringArray(requirements.VkFeatures13, requirements.VkNumFeatures13);
                    queueRequirements = new StreamlineQueueRequirements(
                        requirements.VkNumGraphicsQueuesRequired,
                        requirements.VkNumComputeQueuesRequired,
                        requirements.VkNumOpticalFlowQueuesRequired);
                    return true;
                }
            }

            internal static bool TryDispatchUpscale(
                XRViewport viewport,
                XRQuadFrameBuffer source,
                XRFrameBuffer? target,
                XRTexture? depth,
                XRTexture? motion,
                out int errorCode)
            {
                const string nativeVulkanDlssMessage =
                    "Native Vulkan DLSS upscale must be queued through VulkanRenderer.EnqueueDlssUpscale so Streamline records into the main frame command buffer.";
                errorCode = (int)StreamlineResult.ErrorFeatureMissing;
                _lastError = nativeVulkanDlssMessage;
                return false;
            }

            internal static bool TryCreateNativeVulkanSession(
                VulkanRenderer renderer,
                uint viewportId,
                out NativeVulkanSession? session,
                out string failureReason)
            {
                session = null;
                failureReason = string.Empty;

                lock (Sync)
                {
                    if (!EnsureNativeVulkanRuntime(renderer, includeFrameGeneration: false, out failureReason))
                        return false;

                    _activeNativeVulkanSessions++;
                }

                session = new NativeVulkanSession(viewportId);
                return true;
            }

            internal static bool TryRecordNativeVulkanUpscale(
                NativeVulkanSession session,
                CommandBuffer commandBuffer,
                in VulkanRenderer.VulkanStreamlineImage sourceColor,
                in VulkanRenderer.VulkanStreamlineImage depth,
                in VulkanRenderer.VulkanStreamlineImage motion,
                in VulkanRenderer.VulkanStreamlineImage outputColor,
                in VulkanRenderer.VulkanStreamlineImage? exposure,
                in VulkanUpscaleBridgeDispatchParameters parameters,
                out string failureReason)
                => session.Record(
                    commandBuffer,
                    sourceColor,
                    depth,
                    motion,
                    outputColor,
                    exposure,
                    in parameters,
                    out failureReason);

            internal static bool TryCreateNativeFrameGenerationSession(
                VulkanRenderer renderer,
                uint viewportId,
                out NativeFrameGenerationSession? session,
                out string failureReason)
            {
                session = null;
                failureReason = string.Empty;

                lock (Sync)
                {
                    if (!EnsureNativeVulkanRuntime(renderer, includeFrameGeneration: true, out failureReason, includeDlss: false)
                        || !ResolveFrameGenerationFeatureFunctions(out failureReason))
                    {
                        return false;
                    }

                    _activeNativeVulkanSessions++;
                }

                session = new NativeFrameGenerationSession(viewportId);
                return true;
            }

            internal static bool TryRecordNativeVulkanFrameGeneration(
                NativeFrameGenerationSession session,
                CommandBuffer commandBuffer,
                in VulkanRenderer.VulkanStreamlineImage depth,
                in VulkanRenderer.VulkanStreamlineImage motion,
                in VulkanRenderer.VulkanStreamlineImage hudlessColor,
                in VulkanUpscaleBridgeDispatchParameters parameters,
                out string failureReason)
                => session.Record(
                    commandBuffer,
                    depth,
                    motion,
                    hudlessColor,
                    in parameters,
                    out failureReason);

            internal static bool IsFrameGenerationAvailable(out string? error)
            {
                error = null;

                bool includeDlss = ShouldLoadDlssFeatureForFrameGenerationRuntime;
                if (includeDlss && !NvidiaDlssManager.RequiredRuntimeDllsAvailable)
                {
                    error = NvidiaDlssManager.RequiredRuntimeDllsUnavailableReason;
                    _lastError = error;
                    return false;
                }

                if (!NvidiaDlssManager.FrameGenerationRuntimeDllsAvailable)
                {
                    error = NvidiaDlssManager.FrameGenerationRuntimeDllsUnavailableReason;
                    _lastError = error;
                    return false;
                }

                lock (Sync)
                {
                    if (!EnsureRuntimeInitialized(includeFrameGeneration: true, out string failureReason, includeDlss)
                        || !ResolveFrameGenerationFeatureFunctions(out failureReason))
                    {
                        error = failureReason;
                        _lastError = error;
                        return false;
                    }

                    if (!EnsureFrameGenerationRequirements(out failureReason))
                    {
                        error = failureReason;
                        _lastError = error;
                        return false;
                    }

                }

                return true;
            }

            internal static bool TryDispatchFrameGeneration(
                XRViewport viewport,
                in VulkanUpscaleBridgeDispatchParameters parameters,
                in VulkanRenderer.VulkanStreamlineImage depth,
                in VulkanRenderer.VulkanStreamlineImage motion,
                in VulkanRenderer.VulkanStreamlineImage hudlessColor,
                ENvidiaDlssFrameGenerationMode mode,
                out int errorCode,
                out string? errorMessage)
            {
                errorCode = (int)StreamlineResult.ErrorFeatureMissing;
                errorMessage = null;

                if (!NvidiaDlssManager.FrameGenerationRuntimeDllsAvailable)
                {
                    errorMessage = NvidiaDlssManager.FrameGenerationRuntimeDllsUnavailableReason;
                    _lastError = errorMessage;
                    return false;
                }

                if (mode == ENvidiaDlssFrameGenerationMode.Off)
                {
                    errorMessage = "NVIDIA DLSS frame generation was requested with mode Off.";
                    _lastError = errorMessage;
                    return false;
                }

                if (viewport.Window?.Renderer is not VulkanRenderer renderer)
                {
                    errorMessage = "NVIDIA DLSS frame generation requires the Vulkan renderer.";
                    _lastError = errorMessage;
                    return false;
                }

                if (!renderer.StreamlineFrameGenerationSwapchainActive)
                {
                    errorMessage =
                        "NVIDIA DLSS frame generation was requested, but the Vulkan swapchain was not created through Streamline. " +
                        "The swapchain must be recreated with frame generation enabled so vkAcquireNextImageKHR and vkQueuePresentKHR are routed through Streamline.";
                    _lastError = errorMessage;
                    return false;
                }

                if (renderer.SwapchainImageFormat == Format.R16G16B16A16Sfloat)
                {
                    errorMessage =
                        "NVIDIA DLSS frame generation does not support FP16/scRGB backbuffers. " +
                        "Use an RGB10/UINT10 HDR10 swapchain format or disable HDR while DLSS-G is enabled.";
                    _lastError = errorMessage;
                    return false;
                }

                lock (Sync)
                {
                    if (!EnsureNativeVulkanRuntime(renderer, includeFrameGeneration: true, out string failureReason, includeDlss: renderer.StreamlineFrameGenerationSwapchainIncludesDlss)
                        || !ResolveFrameGenerationFeatureFunctions(out failureReason))
                    {
                        errorMessage = failureReason;
                        _lastError = errorMessage;
                        return false;
                    }

                    if (!EnsureFrameGenerationRequirements(out failureReason))
                    {
                        errorMessage = failureReason;
                        _lastError = errorMessage;
                        return false;
                    }

                    if (!EnsureReflexEnabled(out failureReason))
                    {
                        errorMessage = failureReason;
                        _lastError = errorMessage;
                        return false;
                    }

                    StreamlineViewportHandle streamlineViewport = CreateViewportHandle(viewport);
                    StreamlineDlssGOptions options = CreateFrameGenerationOptions(
                        renderer,
                        in parameters,
                        in depth,
                        in motion,
                        in hudlessColor,
                        mode);

                    StreamlineResult setOptionsResult = _setFrameGenerationOptions!(ref streamlineViewport, ref options);
                    if (setOptionsResult != StreamlineResult.Ok)
                    {
                        errorCode = (int)setOptionsResult;
                        errorMessage = DescribeFrameGenerationFailure("slDLSSGSetOptions", setOptionsResult, in options, in parameters, null);
                        _lastError = errorMessage;
                        return false;
                    }

                    StreamlineDlssGState state = new()
                    {
                        Base = CreateBase(DlssGStateStructType, 4),
                    };

                    StreamlineResult getStateResult = _getFrameGenerationState!(ref streamlineViewport, ref state, IntPtr.Zero);
                    if (getStateResult != StreamlineResult.Ok)
                    {
                        errorCode = (int)getStateResult;
                        errorMessage = DescribeFrameGenerationFailure("slDLSSGGetState", getStateResult, in options, in parameters, null);
                        _lastError = errorMessage;
                        return false;
                    }

                    if (state.Status != StreamlineDlssGStatus.Ok)
                    {
                        errorCode = (int)StreamlineResult.ErrorInvalidState;
                        errorMessage = DescribeFrameGenerationFailure(
                            "slDLSSGGetState",
                            StreamlineResult.ErrorInvalidState,
                            in options,
                            in parameters,
                            $"status={state.Status}, minDimension={state.MinWidthOrHeight}, maxFramesToGenerate={state.NumFramesToGenerateMax}, presentedSinceLastState={state.NumFramesActuallyPresented}");
                        _lastError = errorMessage;
                        return false;
                    }

                    if (state.NumFramesToGenerateMax != 0 && options.NumFramesToGenerate > state.NumFramesToGenerateMax)
                    {
                        errorCode = (int)StreamlineResult.ErrorInvalidParameter;
                        errorMessage = DescribeFrameGenerationFailure(
                            "slDLSSGGetState",
                            StreamlineResult.ErrorInvalidParameter,
                            in options,
                            in parameters,
                            $"requestedFramesToGenerate={options.NumFramesToGenerate}, maxFramesToGenerate={state.NumFramesToGenerateMax}");
                        _lastError = errorMessage;
                        return false;
                    }

                    errorCode = (int)StreamlineResult.Ok;
                    errorMessage = null;
                    return true;
                }
            }

            internal static bool TryDisableFrameGeneration(
                VulkanRenderer renderer,
                XRViewport viewport,
                out string failureReason)
            {
                failureReason = string.Empty;

                lock (Sync)
                {
                    if (!EnsureNativeVulkanRuntime(renderer, includeFrameGeneration: true, out failureReason, includeDlss: renderer.StreamlineFrameGenerationSwapchainIncludesDlss)
                        || !ResolveFrameGenerationFeatureFunctions(out failureReason))
                    {
                        return false;
                    }

                    StreamlineViewportHandle streamlineViewport = CreateViewportHandle(viewport);
                    StreamlineDlssGOptions options = new()
                    {
                        Base = CreateBase(DlssGOptionsStructType, 5),
                        Mode = StreamlineDlssGMode.Off,
                        NumFramesToGenerate = 1,
                    };

                    StreamlineResult result = _setFrameGenerationOptions!(ref streamlineViewport, ref options);
                    if (result == StreamlineResult.Ok)
                        return true;

                    failureReason = $"slDLSSGSetOptions(Off) failed with {result}.";
                    string? streamlineMessage = _lastStreamlineWarningOrError ?? _lastStreamlineMessage;
                    if (!string.IsNullOrWhiteSpace(streamlineMessage))
                        failureReason += $" Last Streamline message: {streamlineMessage}.";

                    _lastError = failureReason;
                    return false;
                }
            }

            internal static unsafe bool TryCreateProxySwapchain(
                VulkanRenderer renderer,
                ref SwapchainCreateInfoKHR createInfo,
                bool includeDlss,
                out SwapchainKHR swapchain,
                out Result result,
                out string failureReason)
            {
                swapchain = default;
                result = Result.ErrorInitializationFailed;

                if (includeDlss && !NvidiaDlssManager.RequiredRuntimeDllsAvailable)
                {
                    failureReason = NvidiaDlssManager.RequiredRuntimeDllsUnavailableReason;
                    _lastError = failureReason;
                    return false;
                }

                if (!EnsureFrameGenerationVulkanProxyReady(renderer, includeDlss, out failureReason))
                    return false;

                fixed (SwapchainCreateInfoKHR* createInfoPtr = &createInfo)
                {
                    fixed (SwapchainKHR* swapchainPtr = &swapchain)
                    {
                        result = _vkCreateSwapchainProxy!(renderer.Device, createInfoPtr, null, swapchainPtr);
                    }
                }

                if (result == Result.Success)
                    RegisterFrameGenerationProxySwapchain();

                return true;
            }

            internal static unsafe bool TryDestroyProxySwapchain(
                VulkanRenderer renderer,
                SwapchainKHR swapchain,
                out string failureReason)
            {
                try
                {
                    if (!EnsureFrameGenerationVulkanProxyReady(renderer, renderer.StreamlineFrameGenerationSwapchainIncludesDlss, out failureReason))
                        return false;

                    _vkDestroySwapchainProxy!(renderer.Device, swapchain, null);
                    return true;
                }
                finally
                {
                    ReleaseFrameGenerationProxySwapchain();
                }
            }

            internal static unsafe bool TryGetProxySwapchainImages(
                VulkanRenderer renderer,
                SwapchainKHR swapchain,
                ref uint imageCount,
                Image* images,
                out Result result,
                out string failureReason)
            {
                result = Result.ErrorInitializationFailed;

                if (!EnsureFrameGenerationVulkanProxyReady(renderer, renderer.StreamlineFrameGenerationSwapchainIncludesDlss, out failureReason))
                    return false;

                fixed (uint* imageCountPtr = &imageCount)
                    result = _vkGetSwapchainImagesProxy!(renderer.Device, swapchain, imageCountPtr, images);

                return true;
            }

            internal static unsafe bool TryAcquireProxyNextImage(
                VulkanRenderer renderer,
                SwapchainKHR swapchain,
                ulong timeout,
                Silk.NET.Vulkan.Semaphore semaphore,
                Fence fence,
                ref uint imageIndex,
                out Result result,
                out string failureReason)
            {
                result = Result.ErrorInitializationFailed;

                if (!EnsureFrameGenerationVulkanProxyReady(renderer, renderer.StreamlineFrameGenerationSwapchainIncludesDlss, out failureReason))
                    return false;

                fixed (uint* imageIndexPtr = &imageIndex)
                    result = _vkAcquireNextImageProxy!(renderer.Device, swapchain, timeout, semaphore, fence, imageIndexPtr);

                return true;
            }

            internal static unsafe bool TryQueueProxyPresent(
                VulkanRenderer renderer,
                Queue queue,
                ref PresentInfoKHR presentInfo,
                out Result result,
                out string failureReason)
            {
                result = Result.ErrorInitializationFailed;

                if (!EnsureFrameGenerationVulkanProxyReady(renderer, renderer.StreamlineFrameGenerationSwapchainIncludesDlss, out failureReason))
                    return false;

                fixed (PresentInfoKHR* presentInfoPtr = &presentInfo)
                    result = _vkQueuePresentProxy!(queue, presentInfoPtr);

                return true;
            }

            internal static bool TryMarkFrameGenerationPclMarker(
                VulkanRenderer renderer,
                StreamlinePclMarker marker,
                uint frameIndex,
                out string failureReason)
            {
                lock (Sync)
                {
                    if (!EnsureNativeVulkanRuntime(renderer, includeFrameGeneration: true, out failureReason, includeDlss: renderer.StreamlineFrameGenerationSwapchainIncludesDlss)
                        || !ResolveFrameGenerationFeatureFunctions(out failureReason))
                    {
                        return false;
                    }

                    StreamlineResult frameTokenResult = _getNewFrameToken!(out IntPtr frameToken, ref frameIndex);
                    if (frameTokenResult != StreamlineResult.Ok || frameToken == IntPtr.Zero)
                    {
                        failureReason = $"slGetNewFrameToken failed for DLSS-G PCL marker {marker} with {frameTokenResult}.";
                        _lastError = failureReason;
                        return false;
                    }

                    StreamlineResult markerResult = _setPclMarker!(marker, frameToken);
                    if (markerResult != StreamlineResult.Ok)
                    {
                        failureReason = $"slPCLSetMarker({marker}) failed with {markerResult}.";
                        string? streamlineMessage = _lastStreamlineWarningOrError ?? _lastStreamlineMessage;
                        if (!string.IsNullOrWhiteSpace(streamlineMessage))
                            failureReason += $" Last Streamline message: {streamlineMessage}.";

                        _lastError = failureReason;
                        return false;
                    }

                    failureReason = string.Empty;
                    return true;
                }
            }

            internal static bool TryCreateBridgeSession(
                VulkanUpscaleBridgeSidecar sidecar,
                uint viewportId,
                out BridgeSession? session,
                out string failureReason)
            {
                session = null;
                failureReason = string.Empty;

                lock (Sync)
                {
                    if (!EnsureBridgeRuntime(sidecar, out failureReason))
                        return false;

                    _activeBridgeSessions++;
                }

                session = new BridgeSession(sidecar, viewportId);
                return true;
            }

            private static unsafe bool EnsureNativeVulkanRuntime(
                VulkanRenderer renderer,
                bool includeFrameGeneration,
                out string failureReason,
                bool includeDlss = true)
            {
                failureReason = string.Empty;

                if (!EnsureRuntimeInitialized(includeFrameGeneration, out failureReason, includeDlss))
                    return false;

                if (_vulkanInfoInitialized && !MatchesBoundDevice(renderer))
                {
                    if (HasActiveRuntimeOwnersUnsafe)
                    {
                        failureReason = "Streamline runtime is already bound to a different Vulkan device.";
                        _lastError = failureReason;
                        return false;
                    }

                    ShutdownRuntimeUnsafe();
                    if (!EnsureRuntimeInitialized(includeFrameGeneration, out failureReason, includeDlss))
                        return false;
                }

                if (!_vulkanInfoInitialized)
                {
                    StreamlineVulkanInfo info = CreateVulkanInfo(renderer);
                    StreamlineResult setInfoResult = _setVulkanInfo!(ref info);
                    if (setInfoResult != StreamlineResult.Ok)
                    {
                        failureReason = $"slSetVulkanInfo failed with {setInfoResult}.";
                        MarkTerminalBridgeFailure(failureReason);
                        return false;
                    }

                    _vulkanInfoInitialized = true;
                    _boundDeviceHandle = renderer.Device.Handle;
                    _boundInstanceHandle = renderer.Instance.Handle;
                    _boundPhysicalDeviceHandle = renderer.PhysicalDevice.Handle;
                }

                if (includeDlss && !_featureFunctionsResolved && !ResolveFeatureFunctions(out failureReason))
                {
                    MarkTerminalBridgeFailure(failureReason);
                    return false;
                }

                if (includeFrameGeneration && !ResolveFrameGenerationFeatureFunctions(out failureReason))
                {
                    MarkTerminalBridgeFailure(failureReason);
                    return false;
                }

                return true;
            }

            private static unsafe bool EnsureBridgeRuntime(VulkanUpscaleBridgeSidecar sidecar, out string failureReason)
            {
                failureReason = string.Empty;

                if (!EnsureRuntimeInitialized(includeFrameGeneration: false, out failureReason))
                    return false;

                if (_vulkanInfoInitialized && !MatchesBoundDevice(sidecar))
                {
                    if (HasActiveRuntimeOwnersUnsafe)
                    {
                        failureReason = "Streamline runtime is already bound to a different Vulkan sidecar device.";
                        _lastError = failureReason;
                        return false;
                    }

                    ShutdownRuntimeUnsafe();
                    if (!EnsureRuntimeInitialized(includeFrameGeneration: false, out failureReason))
                        return false;
                }

                if (!_vulkanInfoInitialized)
                {
                    StreamlineVulkanInfo info = CreateVulkanInfo(sidecar);
                    StreamlineResult setInfoResult = _setVulkanInfo!(ref info);
                    if (setInfoResult != StreamlineResult.Ok)
                    {
                        failureReason = $"slSetVulkanInfo failed with {setInfoResult}.";
                        MarkTerminalBridgeFailure(failureReason);
                        return false;
                    }

                    _vulkanInfoInitialized = true;
                    _boundDeviceHandle = sidecar.Device.Handle;
                    _boundInstanceHandle = sidecar.Instance.Handle;
                    _boundPhysicalDeviceHandle = sidecar.PhysicalDevice.Handle;
                }

                if (!_featureFunctionsResolved && !ResolveFeatureFunctions(out failureReason))
                {
                    MarkTerminalBridgeFailure(failureReason);
                    return false;
                }

                return true;
            }

            private static unsafe bool EnsureRuntimeInitialized(
                bool includeFrameGeneration,
                out string failureReason,
                bool includeDlss = true)
            {
                failureReason = string.Empty;

                if (!string.IsNullOrWhiteSpace(_terminalBridgeFailureReason))
                {
                    failureReason = _terminalBridgeFailureReason;
                    _lastError = failureReason;
                    return false;
                }

                if (!EnsureLibraryLoaded())
                {
                    failureReason = _lastError ?? "Streamline could not be loaded.";
                    return false;
                }

                bool needsAdditionalFeatures =
                    _runtimeInitialized &&
                    ((includeDlss && !_runtimeIncludesDlss) ||
                     (includeFrameGeneration && !_runtimeIncludesFrameGeneration));
                if (needsAdditionalFeatures)
                {
                    if (HasActiveRuntimeOwnersUnsafe)
                    {
                        failureReason =
                            "Streamline was initialized without all requested features and active Streamline sessions are still using it. " +
                            "Recreate the renderer or disable/re-enable the DLSS pipeline so Streamline can reload with the required DLSS-G/DLSS/Reflex/PCL feature set.";
                        _lastError = failureReason;
                        return false;
                    }

                    ShutdownRuntimeUnsafe();
                }

                if (_runtimeInitialized)
                    return true;

                uint* features = stackalloc uint[4];
                uint featureCount = 0;
                if (includeDlss)
                    features[featureCount++] = FeatureDlss;
                if (includeFrameGeneration)
                {
                    features[featureCount++] = FeatureDlssG;
                    features[featureCount++] = FeatureReflex;
                    features[featureCount++] = FeaturePcl;
                }

                StreamlinePreferences preferences = CreatePreferences(features, featureCount);
                try
                {
                    StreamlineResult initResult = _init!(ref preferences, StreamlineSdkVersion);
                    if (initResult != StreamlineResult.Ok)
                    {
                        failureReason = $"slInit failed with {initResult}.";
                        MarkTerminalBridgeFailure(failureReason);
                        return false;
                    }

                    _runtimeInitialized = true;
                    _runtimeIncludesDlss = includeDlss;
                    _runtimeIncludesFrameGeneration = includeFrameGeneration;
                    _vulkanInfoInitialized = false;
                    _featureFunctionsResolved = false;
                    _frameGenerationFeatureFunctionsResolved = false;
                    _vulkanProxyFunctionsResolved = false;
                    _reflexEnabled = false;
                    return true;
                }
                finally
                {
                    if (preferences.PathsToPlugins != IntPtr.Zero)
                        Marshal.FreeHGlobal(preferences.PathsToPlugins);
                    if (preferences.PathToLogsAndData != IntPtr.Zero)
                        Marshal.FreeHGlobal(preferences.PathToLogsAndData);
                    if (preferences.EngineVersion != IntPtr.Zero)
                        Marshal.FreeHGlobal(preferences.EngineVersion);
                    if (preferences.ProjectId != IntPtr.Zero)
                        Marshal.FreeHGlobal(preferences.ProjectId);
                }
            }

            private static bool EnsureLibraryLoaded()
            {
                if (_initialized)
                    return _libraryHandle != IntPtr.Zero;

                _initialized = true;

                try
                {
                    if (!TryLoadRuntimeLibrary(StreamlineLibrary, out _libraryHandle) || _libraryHandle == IntPtr.Zero)
                    {
                        _lastError = $"{StreamlineLibrary} was not found in '{AppContext.BaseDirectory}' or on the native probing path.";
                        _libraryHandle = IntPtr.Zero;
                        return false;
                    }

                    if (!TryLoadExport("slInit", out _init)
                        || !TryLoadExport("slShutdown", out _shutdown)
                        || !TryLoadExport("slGetFeatureRequirements", out _getFeatureRequirements)
                        || !TryLoadExport("slSetVulkanInfo", out _setVulkanInfo)
                        || !TryLoadExport("slEvaluateFeature", out _evaluateFeature)
                        || !TryLoadExport("slAllocateResources", out _allocateResources)
                        || !TryLoadExport("slFreeResources", out _freeResources)
                        || !TryLoadExport("slSetTagForFrame", out _setTagForFrame)
                        || !TryLoadExport("slSetConstants", out _setConstants)
                        || !TryLoadExport("slGetFeatureFunction", out _getFeatureFunction)
                        || !TryLoadExport("slGetNewFrameToken", out _getNewFrameToken))
                    {
                        UnloadLibrary();
                        return false;
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    _lastError = ex.Message;
                    UnloadLibrary();
                    return false;
                }
            }

            private static bool ResolveFeatureFunctions(out string failureReason)
            {
                failureReason = string.Empty;

                if (_featureFunctionsResolved)
                    return true;

                if (!TryResolveFeatureFunction(FeatureDlss, "slDLSSSetOptions", out _setOptions)
                    || !TryResolveFeatureFunction(FeatureDlss, "slDLSSGetOptimalSettings", out _getOptimalSettings, required: false))
                {
                    failureReason = _lastError ?? "Failed to resolve one or more Streamline DLSS feature exports.";
                    return false;
                }

                _featureFunctionsResolved = true;
                return true;
            }

            private static bool ResolveFrameGenerationFeatureFunctions(out string failureReason)
            {
                failureReason = string.Empty;

                if (_frameGenerationFeatureFunctionsResolved)
                    return true;

                if (!_runtimeIncludesFrameGeneration)
                {
                    failureReason = "Streamline runtime was not initialized with DLSS-G, Reflex, and PCL.";
                    _lastError = failureReason;
                    return false;
                }

                if (!TryResolveFeatureFunction(FeatureDlssG, "slDLSSGSetOptions", out _setFrameGenerationOptions)
                    || !TryResolveFeatureFunction(FeatureDlssG, "slDLSSGGetState", out _getFrameGenerationState)
                    || !TryResolveFeatureFunction(FeatureReflex, "slReflexSetOptions", out _setReflexOptions)
                    || !TryResolveFeatureFunction(FeaturePcl, "slPCLSetMarker", out _setPclMarker))
                {
                    failureReason = _lastError ?? "Failed to resolve one or more Streamline DLSS-G/Reflex/PCL exports.";
                    return false;
                }

                _frameGenerationFeatureFunctionsResolved = true;
                return true;
            }

            private static bool EnsureFrameGenerationRequirements(out string failureReason)
            {
                if (_frameGenerationRequirementsChecked)
                {
                    failureReason = _frameGenerationRequirementsValid
                        ? string.Empty
                        : _frameGenerationRequirementsFailure ?? "NVIDIA DLSS frame generation requirements were not met.";
                    return _frameGenerationRequirementsValid;
                }

                _frameGenerationRequirementsChecked = true;
                _frameGenerationRequirementsValid = false;
                _frameGenerationRequirementsFailure = null;
                failureReason = string.Empty;

                if (_getFeatureRequirements is null)
                {
                    failureReason = "Streamline feature-requirements export was not resolved.";
                    _frameGenerationRequirementsFailure = failureReason;
                    _lastError = failureReason;
                    return false;
                }

                StreamlineFeatureRequirements requirements = new()
                {
                    Base = CreateBase(FeatureRequirementsStructType, 2),
                };

                StreamlineResult result = _getFeatureRequirements(FeatureDlssG, ref requirements);
                if (result != StreamlineResult.Ok)
                {
                    failureReason = $"slGetFeatureRequirements(DLSS-G) failed with {result}.";
                    AppendLastStreamlineMessage(ref failureReason);
                    _frameGenerationRequirementsFailure = failureReason;
                    _lastError = failureReason;
                    return false;
                }

                if ((requirements.Flags & StreamlineFeatureRequirementFlags.VulkanSupported) == 0)
                {
                    failureReason =
                        "NVIDIA DLSS frame generation reported no Vulkan support for the current runtime configuration. " +
                        $"OS detected={requirements.OsVersionDetected.Major}.{requirements.OsVersionDetected.Minor}.{requirements.OsVersionDetected.Build}, " +
                        $"OS required={requirements.OsVersionRequired.Major}.{requirements.OsVersionRequired.Minor}.{requirements.OsVersionRequired.Build}, " +
                        $"driver detected={requirements.DriverVersionDetected.Major}.{requirements.DriverVersionDetected.Minor}.{requirements.DriverVersionDetected.Build}, " +
                        $"driver required={requirements.DriverVersionRequired.Major}.{requirements.DriverVersionRequired.Minor}.{requirements.DriverVersionRequired.Build}, " +
                        $"flags={requirements.Flags}.";
                    _frameGenerationRequirementsFailure = failureReason;
                    _lastError = failureReason;
                    return false;
                }

                _frameGenerationRequirementsValid = true;
                return true;
            }

            private static void AppendLastStreamlineMessage(ref string message)
            {
                string? streamlineMessage = _lastStreamlineWarningOrError ?? _lastStreamlineMessage;
                if (!string.IsNullOrWhiteSpace(streamlineMessage))
                    message += $" Last Streamline message: {streamlineMessage}.";
            }

            private static bool TryResolveFeatureFunction<T>(uint feature, string name, out T? del, bool required = true) where T : Delegate
            {
                del = null;

                if (_libraryHandle == IntPtr.Zero)
                {
                    _lastError = $"Cannot resolve Streamline feature export '{name}' before the library is loaded.";
                    return false;
                }

                if (NativeLibrary.TryGetExport(_libraryHandle, name, out IntPtr directExport) && directExport != IntPtr.Zero)
                {
                    del = Marshal.GetDelegateForFunctionPointer<T>(directExport);
                    return true;
                }

                StreamlineResult featureResult = StreamlineResult.ErrorFeatureMissing;
                if (_getFeatureFunction is not null)
                {
                    featureResult = _getFeatureFunction(feature, name, out IntPtr functionPtr);
                    if (featureResult == StreamlineResult.Ok && functionPtr != IntPtr.Zero)
                    {
                        del = Marshal.GetDelegateForFunctionPointer<T>(functionPtr);
                        return true;
                    }
                }

                if (!required)
                    return true;

                _lastError = $"Failed to resolve Streamline feature export '{name}' ({featureResult}).";
                return false;
            }

            internal static bool ShouldLoadDlssFeatureForFrameGenerationRuntime
                => global::XREngine.RuntimeEngine.EffectiveSettings.EnableNvidiaDlss
                   || global::XREngine.Rendering.RenderPipeline.ResolveEffectiveAntiAliasingModeForFrame() == global::XREngine.EAntiAliasingMode.Dlaa;

            private static bool EnsureFrameGenerationVulkanProxyReady(VulkanRenderer renderer, bool includeDlss, out string failureReason)
            {
                lock (Sync)
                {
                    if (!EnsureNativeVulkanRuntime(renderer, includeFrameGeneration: true, out failureReason, includeDlss)
                        || !EnsureVulkanProxyFunctionsResolved(out failureReason))
                    {
                        return false;
                    }

                    return true;
                }
            }

            private static bool EnsureVulkanProxyFunctionsResolved(out string failureReason)
            {
                failureReason = string.Empty;

                if (_vulkanProxyFunctionsResolved)
                    return true;

                if (_libraryHandle == IntPtr.Zero)
                {
                    failureReason = "Cannot resolve Streamline Vulkan proxy exports before sl.interposer.dll is loaded.";
                    _lastError = failureReason;
                    return false;
                }

                if (!NativeLibrary.TryGetExport(_libraryHandle, "vkGetDeviceProcAddr", out IntPtr getDeviceProcAddrPtr)
                    || getDeviceProcAddrPtr == IntPtr.Zero)
                {
                    failureReason = "Streamline interposer did not export vkGetDeviceProcAddr; Vulkan DLSS-G cannot hook acquire/present.";
                    _lastError = failureReason;
                    return false;
                }

                _vkGetDeviceProcAddrProxy = Marshal.GetDelegateForFunctionPointer<VkGetDeviceProcAddrProxyDelegate>(getDeviceProcAddrPtr);

                if (!TryResolveVulkanDeviceProxyFunction("vkCreateSwapchainKHR", out _vkCreateSwapchainProxy)
                    || !TryResolveVulkanDeviceProxyFunction("vkDestroySwapchainKHR", out _vkDestroySwapchainProxy)
                    || !TryResolveVulkanDeviceProxyFunction("vkGetSwapchainImagesKHR", out _vkGetSwapchainImagesProxy)
                    || !TryResolveVulkanDeviceProxyFunction("vkAcquireNextImageKHR", out _vkAcquireNextImageProxy)
                    || !TryResolveVulkanDeviceProxyFunction("vkQueuePresentKHR", out _vkQueuePresentProxy)
                    || !TryResolveVulkanDeviceProxyFunction("vkDeviceWaitIdle", out _vkDeviceWaitIdleProxy))
                {
                    failureReason = _lastError ?? "Failed to resolve one or more Streamline Vulkan proxy functions.";
                    return false;
                }

                _vulkanProxyFunctionsResolved = true;
                return true;
            }

            private static unsafe bool TryResolveVulkanDeviceProxyFunction<T>(string name, out T? del) where T : Delegate
            {
                del = null;

                if (_vkGetDeviceProcAddrProxy is null || _boundDeviceHandle == 0)
                {
                    _lastError = $"Cannot resolve Vulkan proxy function '{name}' before Streamline Vulkan info is bound.";
                    return false;
                }

                IntPtr namePtr = Marshal.StringToHGlobalAnsi(name);
                try
                {
                    IntPtr functionPtr = _vkGetDeviceProcAddrProxy(new Device(_boundDeviceHandle), (byte*)namePtr);
                    if (functionPtr == IntPtr.Zero)
                    {
                        _lastError = $"Streamline vkGetDeviceProcAddr returned null for '{name}'.";
                        return false;
                    }

                    del = Marshal.GetDelegateForFunctionPointer<T>(functionPtr);
                    return true;
                }
                finally
                {
                    Marshal.FreeHGlobal(namePtr);
                }
            }

            private static bool EnsureReflexEnabled(out string failureReason)
            {
                failureReason = string.Empty;

                if (_reflexEnabled)
                    return true;

                if (_setReflexOptions is null)
                {
                    failureReason = "Streamline Reflex options export was not resolved.";
                    _lastError = failureReason;
                    return false;
                }

                StreamlineReflexOptions options = new()
                {
                    Base = CreateBase(ReflexOptionsStructType, 1),
                    Mode = StreamlineReflexMode.LowLatencyWithBoost,
                    FrameLimitUs = 0,
                    UseMarkersToOptimize = 0,
                    VirtualKey = 0,
                    IdThread = 0,
                };

                StreamlineResult result = _setReflexOptions(ref options);
                if (result != StreamlineResult.Ok)
                {
                    failureReason = $"slReflexSetOptions failed with {result}; DLSS frame generation requires Reflex to be active.";
                    string? streamlineMessage = _lastStreamlineWarningOrError ?? _lastStreamlineMessage;
                    if (!string.IsNullOrWhiteSpace(streamlineMessage))
                        failureReason += $" Last Streamline message: {streamlineMessage}.";

                    _lastError = failureReason;
                    return false;
                }

                _reflexEnabled = true;
                return true;
            }

            private static StreamlineViewportHandle CreateViewportHandle(XRViewport viewport)
            {
                uint viewportId = unchecked((uint)viewport.GetHashCode());
                if (viewportId == 0)
                    viewportId = 1;

                return new StreamlineViewportHandle
                {
                    Base = CreateBase(ViewportStructType, 1),
                    Value = viewportId,
                };
            }

            private static StreamlineDlssGOptions CreateFrameGenerationOptions(
                VulkanRenderer renderer,
                in VulkanUpscaleBridgeDispatchParameters parameters,
                in VulkanRenderer.VulkanStreamlineImage depth,
                in VulkanRenderer.VulkanStreamlineImage motion,
                in VulkanRenderer.VulkanStreamlineImage hudlessColor,
                ENvidiaDlssFrameGenerationMode mode)
            {
                Extent2D swapchainExtent = renderer.SwapchainExtent;
                uint colorWidth = Math.Max(1u, swapchainExtent.Width);
                uint colorHeight = Math.Max(1u, swapchainExtent.Height);
                uint inputWidth = Math.Max(1u, parameters.InputWidth);
                uint inputHeight = Math.Max(1u, parameters.InputHeight);

                StreamlineDlssGFlags flags = StreamlineDlssGFlags.None;
                if (inputWidth != colorWidth || inputHeight != colorHeight)
                    flags |= StreamlineDlssGFlags.DynamicResolutionEnabled;

                return new StreamlineDlssGOptions
                {
                    Base = CreateBase(DlssGOptionsStructType, 5),
                    Mode = StreamlineDlssGMode.On,
                    NumFramesToGenerate = ResolveFramesToGenerate(mode),
                    Flags = flags,
                    DynamicResWidth = inputWidth,
                    DynamicResHeight = inputHeight,
                    NumBackBuffers = Math.Max(1u, renderer.SwapchainImageCount),
                    MvecDepthWidth = inputWidth,
                    MvecDepthHeight = inputHeight,
                    ColorWidth = colorWidth,
                    ColorHeight = colorHeight,
                    ColorBufferFormat = (uint)renderer.SwapchainImageFormat,
                    MvecBufferFormat = (uint)motion.Format,
                    DepthBufferFormat = (uint)depth.Format,
                    HudLessBufferFormat = (uint)hudlessColor.Format,
                    UiBufferFormat = 0,
                    OnErrorCallback = IntPtr.Zero,
                    BReserved15 = StreamlineBoolean.Invalid,
                    QueueParallelismMode = StreamlineDlssGQueueParallelismMode.BlockPresentingClientQueue,
                    EnableUserInterfaceRecomposition = StreamlineBoolean.False,
                    DynamicTargetFrameRate = 0.0f,
                };
            }

            private static uint ResolveFramesToGenerate(ENvidiaDlssFrameGenerationMode mode)
                => mode switch
                {
                    ENvidiaDlssFrameGenerationMode.OneX => 1,
                    ENvidiaDlssFrameGenerationMode.TwoX => 2,
                    ENvidiaDlssFrameGenerationMode.ThreeX => 3,
                    _ => 0,
                };

            private static string DescribeFrameGenerationFailure(
                string apiName,
                StreamlineResult result,
                in StreamlineDlssGOptions options,
                in VulkanUpscaleBridgeDispatchParameters parameters,
                string? additionalContext)
            {
                string reason =
                    $"{apiName} failed with {result}. " +
                    $"mode={options.Mode}, framesToGenerate={options.NumFramesToGenerate}, flags={options.Flags}, " +
                    $"input={parameters.InputWidth}x{parameters.InputHeight}, output={parameters.OutputWidth}x{parameters.OutputHeight}, " +
                    $"backbuffer={options.ColorWidth}x{options.ColorHeight}, backbuffers={options.NumBackBuffers}, " +
                    $"formats color={options.ColorBufferFormat} depth={options.DepthBufferFormat} motion={options.MvecBufferFormat} hudless={options.HudLessBufferFormat}";

                if (!string.IsNullOrWhiteSpace(additionalContext))
                    reason += $", {additionalContext}";

                string? streamlineMessage = _lastStreamlineWarningOrError ?? _lastStreamlineMessage;
                if (!string.IsNullOrWhiteSpace(streamlineMessage))
                    reason += $". Last Streamline message: {streamlineMessage}";

                return reason + ".";
            }

            private static unsafe StreamlinePreferences CreatePreferences(uint* featuresToLoad, uint featureCount)
            {
                string runtimeDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                IntPtr runtimeDirectoryPtr = Marshal.StringToHGlobalUni(runtimeDirectory);
                IntPtr pluginPathArrayPtr = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(pluginPathArrayPtr, runtimeDirectoryPtr);

                // Struct version must be 3 because the layout includes v2 fields
                // (Engine/EngineVersion/ProjectId) and the v3 field (RenderApi). Sending
                // version 1 causes slInit to return ErrorInitNotCalled because the SDK
                // won't read past the v1 field boundary and will see uninitialized data
                // for RenderApi. Keep in sync with sl.h Preferences kStructVersion3.
                return new StreamlinePreferences
                {
                    Base = CreateBase(PreferencesStructType, 3),
                    ShowConsole = 0,
                    LogLevel = StreamlineLogLevel.Verbose,
                    PathsToPlugins = pluginPathArrayPtr,
                    NumPathsToPlugins = 1,
                    PathToLogsAndData = runtimeDirectoryPtr,
                    AllocateCallback = IntPtr.Zero,
                    ReleaseCallback = IntPtr.Zero,
                    LogMessageCallback = LogMessageCallbackPtr,
                    Flags = StreamlinePreferenceFlags.DisableCommandListStateTracking
                        | StreamlinePreferenceFlags.DisableDebugText
                        | StreamlinePreferenceFlags.UseManualHooking
                        | StreamlinePreferenceFlags.UseFrameBasedResourceTagging,
                    FeaturesToLoad = (IntPtr)featuresToLoad,
                    NumFeaturesToLoad = featureCount,
                    ApplicationId = 0,
                    Engine = StreamlineEngineType.Custom,
                    EngineVersion = Marshal.StringToHGlobalAnsi("XRENGINE"),
                    ProjectId = Marshal.StringToHGlobalAnsi(StreamlineProjectId),
                    RenderApi = StreamlineRenderApi.Vulkan,
                };
            }

            private static StreamlineVulkanInfo CreateVulkanInfo(VulkanUpscaleBridgeSidecar sidecar)
            {
                return new StreamlineVulkanInfo
                {
                    Base = CreateBase(VulkanInfoStructType, 3),
                    Device = sidecar.Device,
                    Instance = sidecar.Instance,
                    PhysicalDevice = sidecar.PhysicalDevice,
                    ComputeQueueIndex = sidecar.StreamlineComputeQueueIndex,
                    ComputeQueueFamily = sidecar.GraphicsQueueFamilyIndex,
                    GraphicsQueueIndex = sidecar.StreamlineGraphicsQueueIndex,
                    GraphicsQueueFamily = sidecar.GraphicsQueueFamilyIndex,
                    OpticalFlowQueueIndex = sidecar.StreamlineOpticalFlowQueueIndex,
                    OpticalFlowQueueFamily = 0,
                    UseNativeOpticalFlowMode = 0,
                    ComputeQueueCreateFlags = 0,
                    GraphicsQueueCreateFlags = 0,
                    OpticalFlowQueueCreateFlags = 0,
                };
            }

            private static StreamlineVulkanInfo CreateVulkanInfo(VulkanRenderer renderer)
            {
                VulkanRenderer.QueueFamilyIndices families = renderer.FamilyQueueIndices;
                uint graphicsFamily = families.GraphicsFamilyIndex ?? 0u;
                uint computeFamily = families.ComputeFamilyIndex ?? graphicsFamily;

                return new StreamlineVulkanInfo
                {
                    Base = CreateBase(VulkanInfoStructType, 3),
                    Device = renderer.Device,
                    Instance = renderer.Instance,
                    PhysicalDevice = renderer.PhysicalDevice,
                    ComputeQueueIndex = 0,
                    ComputeQueueFamily = computeFamily,
                    GraphicsQueueIndex = 0,
                    GraphicsQueueFamily = graphicsFamily,
                    OpticalFlowQueueIndex = 0,
                    OpticalFlowQueueFamily = 0,
                    UseNativeOpticalFlowMode = 0,
                    ComputeQueueCreateFlags = 0,
                    GraphicsQueueCreateFlags = 0,
                    OpticalFlowQueueCreateFlags = 0,
                };
            }

            private static void ReleaseBridgeRuntime()
            {
                lock (Sync)
                {
                    if (_activeBridgeSessions > 0)
                        _activeBridgeSessions--;

                    if (HasActiveRuntimeOwnersUnsafe)
                        return;

                    ShutdownRuntimeUnsafe();
                }
            }

            private static void ReleaseNativeVulkanRuntime()
            {
                lock (Sync)
                {
                    if (_activeNativeVulkanSessions > 0)
                        _activeNativeVulkanSessions--;

                    if (HasActiveRuntimeOwnersUnsafe)
                        return;

                    ShutdownRuntimeUnsafe();
                }
            }

            private static bool HasActiveRuntimeOwnersUnsafe
                => _activeBridgeSessions != 0
                   || _activeNativeVulkanSessions != 0
                   || _activeFrameGenerationProxySwapchains != 0;

            private static void RegisterFrameGenerationProxySwapchain()
            {
                lock (Sync)
                    _activeFrameGenerationProxySwapchains++;
            }

            private static void ReleaseFrameGenerationProxySwapchain()
            {
                lock (Sync)
                {
                    if (_activeFrameGenerationProxySwapchains > 0)
                        _activeFrameGenerationProxySwapchains--;

                    if (HasActiveRuntimeOwnersUnsafe)
                        return;

                    ShutdownRuntimeUnsafe();
                }
            }

            private static bool TryLoadExport<T>(string exportName, out T? del) where T : Delegate
            {
                del = null;

                if (_libraryHandle == IntPtr.Zero || !NativeLibrary.TryGetExport(_libraryHandle, exportName, out IntPtr exportPtr) || exportPtr == IntPtr.Zero)
                {
                    _lastError = $"Failed to load Streamline export '{exportName}'.";
                    return false;
                }

                del = Marshal.GetDelegateForFunctionPointer<T>(exportPtr);
                return true;
            }

            private static void UnloadLibrary()
            {
                if (_libraryHandle != IntPtr.Zero)
                {
                    NativeLibrary.Free(_libraryHandle);
                    _libraryHandle = IntPtr.Zero;
                }

                _init = null;
                _shutdown = null;
                _setVulkanInfo = null;
                _evaluateFeature = null;
                _allocateResources = null;
                _freeResources = null;
                _setTagForFrame = null;
                _setConstants = null;
                _getFeatureFunction = null;
                _getNewFrameToken = null;
                _getFeatureRequirements = null;
                _setOptions = null;
                _getOptimalSettings = null;
                _setFrameGenerationOptions = null;
                _getFrameGenerationState = null;
                _setReflexOptions = null;
                _setPclMarker = null;
                _vkGetDeviceProcAddrProxy = null;
                _vkCreateSwapchainProxy = null;
                _vkDestroySwapchainProxy = null;
                _vkGetSwapchainImagesProxy = null;
                _vkAcquireNextImageProxy = null;
                _vkQueuePresentProxy = null;
                _vkDeviceWaitIdleProxy = null;
            }

            private static bool MatchesBoundDevice(VulkanUpscaleBridgeSidecar sidecar)
                => _boundDeviceHandle == sidecar.Device.Handle
                    && _boundInstanceHandle == sidecar.Instance.Handle
                    && _boundPhysicalDeviceHandle == sidecar.PhysicalDevice.Handle;

            private static bool MatchesBoundDevice(VulkanRenderer renderer)
                => _boundDeviceHandle == renderer.Device.Handle
                    && _boundInstanceHandle == renderer.Instance.Handle
                    && _boundPhysicalDeviceHandle == renderer.PhysicalDevice.Handle;

            private static void MarkTerminalBridgeFailure(string failureReason)
            {
                if (string.IsNullOrWhiteSpace(failureReason))
                    return;

                lock (Sync)
                {
                    _lastError = failureReason;
                    if (string.Equals(_terminalBridgeFailureReason, failureReason, StringComparison.Ordinal))
                        return;

                    _terminalBridgeFailureReason = failureReason;
                    unchecked
                    {
                        _bridgeFailureGeneration++;
                    }
                }
            }

            private static void ShutdownRuntimeUnsafe()
            {
                if (_runtimeInitialized && _shutdown is not null)
                    _shutdown();

                _runtimeInitialized = false;
                _runtimeIncludesDlss = false;
                _runtimeIncludesFrameGeneration = false;
                _vulkanInfoInitialized = false;
                _featureFunctionsResolved = false;
                _frameGenerationFeatureFunctionsResolved = false;
                _frameGenerationRequirementsChecked = false;
                _frameGenerationRequirementsValid = false;
                _frameGenerationRequirementsFailure = null;
                _vulkanProxyFunctionsResolved = false;
                _reflexEnabled = false;
                _boundDeviceHandle = 0;
                _boundInstanceHandle = 0;
                _boundPhysicalDeviceHandle = 0;
            }

            private static string[] MarshalStringArray(IntPtr names, uint count)
            {
                if (names == IntPtr.Zero || count == 0)
                    return [];

                string[] results = new string[count];
                for (int index = 0; index < count; index++)
                {
                    IntPtr entry = Marshal.ReadIntPtr(names, index * IntPtr.Size);
                    results[index] = Marshal.PtrToStringAnsi(entry) ?? string.Empty;
                }

                return results;
            }

            private static StreamlineBaseStructure CreateBase(StreamlineStructType type, nuint version)
                => new()
                {
                    Next = IntPtr.Zero,
                    StructType = type,
                    StructVersion = version,
                };

            private static void OnStreamlineLogMessage(StreamlineLogType type, IntPtr messagePtr)
            {
                try
                {
                    string? message = Marshal.PtrToStringAnsi(messagePtr);
                    if (string.IsNullOrWhiteSpace(message))
                        return;

                    string formatted = $"[Streamline:{type}] {message}";
                    _lastStreamlineMessage = formatted;
                    if (type is StreamlineLogType.Warn or StreamlineLogType.Error)
                        _lastStreamlineWarningOrError = formatted;

                    global::XREngine.Debug.Log(
                        global::XREngine.ELogCategory.Rendering,
                        type == StreamlineLogType.Info ? global::XREngine.EOutputVerbosity.Verbose : global::XREngine.EOutputVerbosity.Normal,
                        debugOnly: false,
                        formatted);
                }
                catch
                {
                    // Native callbacks must never let managed exceptions escape.
                }
            }

            private static StreamlineStructType CreateStructType(uint data1, ushort data2, ushort data3, byte d4, byte d5, byte d6, byte d7, byte d8, byte d9, byte d10, byte d11)
                => new()
                {
                    Data1 = data1,
                    Data2 = data2,
                    Data3 = data3,
                    Data40 = d4,
                    Data41 = d5,
                    Data42 = d6,
                    Data43 = d7,
                    Data44 = d8,
                    Data45 = d9,
                    Data46 = d10,
                    Data47 = d11,
                };

            private static readonly StreamlineStructType PreferencesStructType = CreateStructType(0x1CA10965, 0xBF8E, 0x432B, 0x8D, 0xA1, 0x67, 0x16, 0xD8, 0x79, 0xFB, 0x14);
            private static readonly StreamlineStructType ViewportStructType = CreateStructType(0x171B6435, 0x9B3C, 0x4FC8, 0x99, 0x94, 0xFB, 0xE5, 0x25, 0x69, 0xAA, 0xA4);
            private static readonly StreamlineStructType ResourceStructType = CreateStructType(0x3A9D70CF, 0x2418, 0x4B72, 0x83, 0x91, 0x13, 0xF8, 0x72, 0x1C, 0x72, 0x61);
            private static readonly StreamlineStructType ResourceTagStructType = CreateStructType(0x4C6A5AAD, 0xB445, 0x496C, 0x87, 0xFF, 0x1A, 0xF3, 0x84, 0x5B, 0xE6, 0x53);
            private static readonly StreamlineStructType ConstantsStructType = CreateStructType(0xDCD35AD7, 0x4E4A, 0x4BAD, 0xA9, 0x0C, 0xE0, 0xC4, 0x9E, 0xB2, 0x3A, 0xFE);
            private static readonly StreamlineStructType VulkanInfoStructType = CreateStructType(0x0EED6FD5, 0x82CD, 0x43A9, 0xBD, 0xB5, 0x47, 0xA5, 0xBA, 0x2F, 0x45, 0xD6);
            private static readonly StreamlineStructType DlssOptionsStructType = CreateStructType(0x6AC826E4, 0x4C61, 0x4101, 0xA9, 0x2D, 0x63, 0x8D, 0x42, 0x10, 0x57, 0xB8);
            private static readonly StreamlineStructType DlssOptimalSettingsStructType = CreateStructType(0xEF1D0957, 0xFD58, 0x4DF7, 0xB5, 0x04, 0x8B, 0x69, 0xD8, 0xAA, 0x6B, 0x76);
            private static readonly StreamlineStructType DlssGOptionsStructType = CreateStructType(0xFAC5F1CB, 0x2DFD, 0x4F36, 0xA1, 0xE6, 0x3A, 0x9E, 0x86, 0x52, 0x56, 0xC5);
            private static readonly StreamlineStructType DlssGStateStructType = CreateStructType(0xCC8AC8E1, 0xA179, 0x44F5, 0x97, 0xFA, 0xE7, 0x41, 0x12, 0xF9, 0xBC, 0x61);
            private static readonly StreamlineStructType ReflexOptionsStructType = CreateStructType(0xF03AF81A, 0x6D0B, 0x4902, 0xA6, 0x51, 0xC4, 0x96, 0x5E, 0x21, 0x54, 0x34);
            private static readonly StreamlineStructType FeatureRequirementsStructType = CreateStructType(0x66714097, 0xAC6D, 0x4BC6, 0x89, 0x15, 0x1E, 0x0F, 0x55, 0xA6, 0xB6, 0x1F);

            internal sealed class NativeVulkanSession : IDisposable
            {
                private readonly StreamlineViewportHandle _viewport;
                private bool _disposed;
                private bool _resourcesAllocated;
                private bool _firstDispatch = true;

                public NativeVulkanSession(uint viewportId)
                {
                    _viewport = new StreamlineViewportHandle
                    {
                        Base = CreateBase(ViewportStructType, 1),
                        Value = viewportId,
                    };
                }

                public unsafe bool Record(
                    CommandBuffer commandBuffer,
                    in VulkanRenderer.VulkanStreamlineImage sourceColor,
                    in VulkanRenderer.VulkanStreamlineImage depthImage,
                    in VulkanRenderer.VulkanStreamlineImage motionImage,
                    in VulkanRenderer.VulkanStreamlineImage outputColor,
                    in VulkanRenderer.VulkanStreamlineImage? exposureImage,
                    in VulkanUpscaleBridgeDispatchParameters parameters,
                    out string failureReason)
                {
                    failureReason = string.Empty;

                    if (_disposed)
                    {
                        failureReason = "Native Vulkan Streamline session was already disposed.";
                        return false;
                    }

                    if (_freeResources is null
                        || _setTagForFrame is null
                        || _setConstants is null
                        || _evaluateFeature is null
                        || _getNewFrameToken is null
                        || _setOptions is null)
                    {
                        failureReason = "Streamline core exports are not fully initialized.";
                        MarkTerminalBridgeFailure(failureReason);
                        return false;
                    }

                    IntPtr commandBufferPtr = ToIntPtr(commandBuffer.Handle);
                    StreamlineViewportHandle viewport = _viewport;

                    StreamlineDlssOptions options = CreateDlssOptions(parameters);
                    StreamlineResult setOptionsResult = _setOptions(ref viewport, ref options);
                    if (setOptionsResult != StreamlineResult.Ok)
                    {
                        failureReason = $"slDLSSSetOptions failed with {setOptionsResult}.";
                        MarkTerminalBridgeFailure(failureReason);
                        return false;
                    }

                    StreamlineConstants constants = CreateConstants(parameters, _firstDispatch);
                    uint frameIndex = parameters.FrameIndex;
                    StreamlineResult frameTokenResult = _getNewFrameToken(out IntPtr frameToken, ref frameIndex);
                    if (frameTokenResult != StreamlineResult.Ok || frameToken == IntPtr.Zero)
                    {
                        failureReason = $"slGetNewFrameToken failed with {frameTokenResult}.";
                        MarkTerminalBridgeFailure(failureReason);
                        return false;
                    }

                    StreamlineResource colorInput = CreateResource(sourceColor, parameters.InputWidth, parameters.InputHeight);
                    StreamlineResource colorOutput = CreateResource(outputColor, parameters.OutputWidth, parameters.OutputHeight);
                    StreamlineResource depth = CreateResource(depthImage, parameters.InputWidth, parameters.InputHeight);
                    StreamlineResource motion = CreateResource(motionImage, parameters.InputWidth, parameters.InputHeight);
                    StreamlineResource exposure = parameters.HasExposureTexture && exposureImage.HasValue
                        ? CreateResource(exposureImage.Value, 1, 1)
                        : default;

                    StreamlineExtent inputExtent = new()
                    {
                        Top = 0,
                        Left = 0,
                        Width = parameters.InputWidth,
                        Height = parameters.InputHeight,
                    };
                    StreamlineExtent outputExtent = new()
                    {
                        Top = 0,
                        Left = 0,
                        Width = parameters.OutputWidth,
                        Height = parameters.OutputHeight,
                    };
                    StreamlineExtent exposureExtent = new()
                    {
                        Top = 0,
                        Left = 0,
                        Width = 1,
                        Height = 1,
                    };

                    bool frameGenerationRequested = NvidiaDlssManager.IsFrameGenerationRequested;
                    StreamlineResourceLifecycle lifecycle = frameGenerationRequested
                        ? StreamlineResourceLifecycle.ValidUntilPresent
                        : StreamlineResourceLifecycle.ValidUntilEvaluate;

                    StreamlineResourceTag* tags = stackalloc StreamlineResourceTag[6];
                    tags[0] = CreateResourceTag(&colorInput, BufferTypeScalingInputColor, lifecycle, inputExtent);
                    tags[1] = CreateResourceTag(&colorOutput, BufferTypeScalingOutputColor, lifecycle, outputExtent);
                    tags[2] = CreateResourceTag(&depth, BufferTypeDepth, lifecycle, inputExtent);
                    tags[3] = CreateResourceTag(&motion, BufferTypeMotionVectors, lifecycle, inputExtent);
                    uint tagCount = 4;
                    if (parameters.HasExposureTexture && exposureImage.HasValue)
                        tags[tagCount++] = CreateResourceTag(&exposure, BufferTypeExposure, lifecycle, exposureExtent);
                    if (frameGenerationRequested)
                        tags[tagCount++] = CreateResourceTag(&colorOutput, BufferTypeHudLessColor, StreamlineResourceLifecycle.ValidUntilPresent, outputExtent);

                    StreamlineResult tagResult = _setTagForFrame(frameToken, ref viewport, (IntPtr)tags, tagCount, commandBufferPtr);
                    if (tagResult != StreamlineResult.Ok)
                    {
                        failureReason = DescribeStreamlineFailure(
                            "slSetTagForFrame",
                            tagResult,
                            in parameters,
                            ref options,
                            $"tagCount={tagCount}");
                        MarkTerminalBridgeFailure(failureReason);
                        return false;
                    }

                    StreamlineResult constantsResult = _setConstants(ref constants, frameToken, ref viewport);
                    if (constantsResult != StreamlineResult.Ok)
                    {
                        failureReason = DescribeStreamlineFailure(
                            "slSetConstants",
                            constantsResult,
                            in parameters,
                            ref options,
                            $"reset={constants.Reset}");
                        MarkTerminalBridgeFailure(failureReason);
                        return false;
                    }

                    StreamlineViewportHandle viewportInput = viewport;
                    IntPtr* inputs = stackalloc IntPtr[1];
                    inputs[0] = (IntPtr)(&viewportInput);

                    StreamlineResult evaluateResult = _evaluateFeature(FeatureDlss, frameToken, (IntPtr)inputs, 1, commandBufferPtr);
                    if (evaluateResult != StreamlineResult.Ok)
                    {
                        failureReason = DescribeStreamlineFailure(
                            "slEvaluateFeature",
                            evaluateResult,
                            in parameters,
                            ref options,
                            "inputs=viewport");
                        MarkTerminalBridgeFailure(failureReason);
                        return false;
                    }

                    _resourcesAllocated = true;
                    _firstDispatch = false;
                    return true;
                }

                public void Dispose()
                {
                    if (_disposed)
                        return;

                    _disposed = true;

                    FreeAllocatedResources();
                    ReleaseNativeVulkanRuntime();
                }

                public void ResetResources()
                {
                    if (_disposed)
                        return;

                    FreeAllocatedResources();
                    _firstDispatch = true;
                }

                private void FreeAllocatedResources()
                {
                    if (_resourcesAllocated && _freeResources is not null)
                    {
                        StreamlineViewportHandle viewport = _viewport;
                        _freeResources(FeatureDlss, ref viewport);
                        _resourcesAllocated = false;
                    }
                }

                private static StreamlineDlssOptions CreateDlssOptions(in VulkanUpscaleBridgeDispatchParameters parameters)
                {
                    float preExposure = parameters.HasExposureTexture
                        ? 1.0f
                        : MathF.Max(parameters.ExposureScale, 0.0001f);

                    return new StreamlineDlssOptions
                    {
                        Base = CreateBase(DlssOptionsStructType, 3),
                        Mode = ResolveDlssMode(parameters),
                        OutputWidth = parameters.OutputWidth,
                        OutputHeight = parameters.OutputHeight,
                        Sharpness = parameters.DlssSharpness,
                        PreExposure = preExposure,
                        ExposureScale = 1.0f,
                        ColorBuffersHdr = parameters.OutputHdr ? StreamlineBoolean.True : StreamlineBoolean.False,
                        IndicatorInvertAxisX = StreamlineBoolean.False,
                        IndicatorInvertAxisY = StreamlineBoolean.False,
                        DlaaPreset = StreamlineDlssPreset.Default,
                        QualityPreset = StreamlineDlssPreset.Default,
                        BalancedPreset = StreamlineDlssPreset.Default,
                        PerformancePreset = StreamlineDlssPreset.Default,
                        UltraPerformancePreset = StreamlineDlssPreset.Default,
                        UltraQualityPreset = StreamlineDlssPreset.Default,
                        UseAutoExposure = StreamlineBoolean.False,
                        AlphaUpscalingEnabled = StreamlineBoolean.False,
                    };
                }

                private static StreamlineDlssMode ResolveDlssMode(in VulkanUpscaleBridgeDispatchParameters parameters)
                {
                    if (parameters.InputWidth == parameters.OutputWidth && parameters.InputHeight == parameters.OutputHeight)
                        return StreamlineDlssMode.Dlaa;

                    if (parameters.DlssQuality != EDlssQualityMode.Custom)
                    {
                        return parameters.DlssQuality switch
                        {
                            EDlssQualityMode.UltraPerformance => StreamlineDlssMode.UltraPerformance,
                            EDlssQualityMode.Performance => StreamlineDlssMode.MaxPerformance,
                            EDlssQualityMode.Balanced => StreamlineDlssMode.Balanced,
                            EDlssQualityMode.Quality => StreamlineDlssMode.MaxQuality,
                            EDlssQualityMode.UltraQuality => StreamlineDlssMode.MaxQuality,
                            _ => StreamlineDlssMode.MaxQuality,
                        };
                    }

                    float inputScale = parameters.OutputWidth == 0 ? 1.0f : parameters.InputWidth / (float)parameters.OutputWidth;
                    return inputScale switch
                    {
                        <= 0.40f => StreamlineDlssMode.UltraPerformance,
                        <= 0.54f => StreamlineDlssMode.MaxPerformance,
                        <= 0.62f => StreamlineDlssMode.Balanced,
                        _ => StreamlineDlssMode.MaxQuality,
                    };
                }

                private static string DescribeStreamlineFailure(
                    string apiName,
                    StreamlineResult result,
                    in VulkanUpscaleBridgeDispatchParameters parameters,
                    ref StreamlineDlssOptions options,
                    string? additionalContext)
                {
                    string reason =
                        $"{apiName} failed with {result}. " +
                        $"DLSS mode={options.Mode}, input={parameters.InputWidth}x{parameters.InputHeight}, " +
                        $"output={parameters.OutputWidth}x{parameters.OutputHeight}, hdr={parameters.OutputHdr}, " +
                        $"quality={parameters.DlssQuality}, exposureTexture={parameters.HasExposureTexture}";

                    if (!string.IsNullOrWhiteSpace(additionalContext))
                        reason += $", {additionalContext}";

                    string optimalSettings = DescribeOptimalSettings(ref options);
                    if (!string.IsNullOrWhiteSpace(optimalSettings))
                        reason += $". {optimalSettings}";

                    string? streamlineMessage = _lastStreamlineWarningOrError ?? _lastStreamlineMessage;
                    if (!string.IsNullOrWhiteSpace(streamlineMessage))
                        reason += $". Last Streamline message: {streamlineMessage}";

                    return reason + ".";
                }

                private static string DescribeOptimalSettings(ref StreamlineDlssOptions options)
                {
                    if (_getOptimalSettings is null)
                        return string.Empty;

                    StreamlineDlssOptimalSettings settings = new()
                    {
                        Base = CreateBase(DlssOptimalSettingsStructType, 1),
                    };

                    StreamlineResult result = _getOptimalSettings(ref options, ref settings);
                    if (result != StreamlineResult.Ok)
                        return $"slDLSSGetOptimalSettings failed with {result}";

                    return
                        $"DLSS optimal input={settings.OptimalRenderWidth}x{settings.OptimalRenderHeight}, " +
                        $"range={settings.RenderWidthMin}x{settings.RenderHeightMin}-{settings.RenderWidthMax}x{settings.RenderHeightMax}, " +
                        $"sharpness={settings.OptimalSharpness:0.###}";
                }

                private static StreamlineConstants CreateConstants(in VulkanUpscaleBridgeDispatchParameters parameters, bool firstDispatch)
                {
                    return new StreamlineConstants
                    {
                        Base = CreateBase(ConstantsStructType, 2),
                        CameraViewToClip = ToFloat4x4(parameters.CameraViewToClip),
                        ClipToCameraView = ToFloat4x4(parameters.ClipToCameraView),
                        ClipToLensClip = ToFloat4x4(Matrix4x4.Identity),
                        ClipToPrevClip = ToFloat4x4(parameters.ClipToPrevClip),
                        PrevClipToClip = ToFloat4x4(parameters.PrevClipToClip),
                        JitterOffset = new StreamlineFloat2(parameters.JitterOffsetX, parameters.JitterOffsetY),
                        MotionVectorScale = new StreamlineFloat2(parameters.MotionVectorScaleX, parameters.MotionVectorScaleY),
                        CameraPinholeOffset = new StreamlineFloat2(float.MaxValue, float.MaxValue),
                        CameraPosition = new StreamlineFloat3(parameters.CameraPosition.X, parameters.CameraPosition.Y, parameters.CameraPosition.Z),
                        CameraUp = new StreamlineFloat3(parameters.CameraUp.X, parameters.CameraUp.Y, parameters.CameraUp.Z),
                        CameraRight = new StreamlineFloat3(parameters.CameraRight.X, parameters.CameraRight.Y, parameters.CameraRight.Z),
                        CameraForward = new StreamlineFloat3(parameters.CameraForward.X, parameters.CameraForward.Y, parameters.CameraForward.Z),
                        CameraNear = parameters.CameraNear,
                        CameraFar = parameters.CameraFar,
                        CameraFov = parameters.CameraFovRadians,
                        CameraAspectRatio = parameters.CameraAspectRatio,
                        MotionVectorsInvalidValue = float.MaxValue,
                        DepthInverted = parameters.ReverseDepth ? StreamlineBoolean.True : StreamlineBoolean.False,
                        CameraMotionIncluded = StreamlineBoolean.True,
                        MotionVectors3D = StreamlineBoolean.False,
                        Reset = parameters.ResetHistory || firstDispatch ? StreamlineBoolean.True : StreamlineBoolean.False,
                        OrthographicProjection = parameters.IsOrthographic ? StreamlineBoolean.True : StreamlineBoolean.False,
                        MotionVectorsDilated = StreamlineBoolean.False,
                        MotionVectorsJittered = StreamlineBoolean.False,
                        MinRelativeLinearDepthObjectSeparation = 40.0f,
                    };
                }

                private static StreamlineResource CreateResource(in VulkanRenderer.VulkanStreamlineImage image, uint width, uint height)
                {
                    return new StreamlineResource
                    {
                        Base = CreateBase(ResourceStructType, 1),
                        Type = StreamlineResourceType.Texture2D,
                        Native = ToIntPtr(image.Image.Handle),
                        Memory = ToIntPtr(image.Memory.Handle),
                        View = ToIntPtr(image.View.Handle),
                        State = (uint)image.Layout,
                        Width = width,
                        Height = height,
                        NativeFormat = (uint)image.Format,
                        MipLevels = 1,
                        ArrayLayers = 1,
                        GpuVirtualAddress = 0,
                        Flags = 0,
                        Usage = (uint)image.Usage,
                        Reserved = 0,
                    };
                }

                private static unsafe StreamlineResourceTag CreateResourceTag(StreamlineResource* resource, uint bufferType, StreamlineResourceLifecycle lifecycle, StreamlineExtent extent)
                {
                    return new StreamlineResourceTag
                    {
                        Base = CreateBase(ResourceTagStructType, 1),
                        Resource = (IntPtr)resource,
                        Type = bufferType,
                        Lifecycle = lifecycle,
                        Extent = extent,
                    };
                }

                private static StreamlineFloat4x4 ToFloat4x4(Matrix4x4 matrix)
                {
                    return new StreamlineFloat4x4
                    {
                        Row0 = new StreamlineFloat4(matrix.M11, matrix.M12, matrix.M13, matrix.M14),
                        Row1 = new StreamlineFloat4(matrix.M21, matrix.M22, matrix.M23, matrix.M24),
                        Row2 = new StreamlineFloat4(matrix.M31, matrix.M32, matrix.M33, matrix.M34),
                        Row3 = new StreamlineFloat4(matrix.M41, matrix.M42, matrix.M43, matrix.M44),
                    };
                }
            }

            internal sealed class NativeFrameGenerationSession : IDisposable
            {
                private readonly StreamlineViewportHandle _viewport;
                private bool _disposed;
                private bool _firstDispatch = true;

                public NativeFrameGenerationSession(uint viewportId)
                {
                    _viewport = new StreamlineViewportHandle
                    {
                        Base = CreateBase(ViewportStructType, 1),
                        Value = viewportId,
                    };
                }

                public unsafe bool Record(
                    CommandBuffer commandBuffer,
                    in VulkanRenderer.VulkanStreamlineImage depthImage,
                    in VulkanRenderer.VulkanStreamlineImage motionImage,
                    in VulkanRenderer.VulkanStreamlineImage hudlessColorImage,
                    in VulkanUpscaleBridgeDispatchParameters parameters,
                    out string failureReason)
                {
                    failureReason = string.Empty;

                    if (_disposed)
                    {
                        failureReason = "Native Vulkan DLSS-G session was already disposed.";
                        return false;
                    }

                    if (_setTagForFrame is null || _setConstants is null || _getNewFrameToken is null)
                    {
                        failureReason = "Streamline core exports are not fully initialized for DLSS-G resource tagging.";
                        _lastError = failureReason;
                        return false;
                    }

                    if (depthImage.Width != parameters.InputWidth || depthImage.Height != parameters.InputHeight)
                    {
                        failureReason = $"DLSS-G depth extent mismatch: expected {parameters.InputWidth}x{parameters.InputHeight}, got {depthImage.Width}x{depthImage.Height}.";
                        _lastError = failureReason;
                        return false;
                    }

                    if (motionImage.Width != parameters.InputWidth || motionImage.Height != parameters.InputHeight)
                    {
                        failureReason = $"DLSS-G motion-vector extent mismatch: expected {parameters.InputWidth}x{parameters.InputHeight}, got {motionImage.Width}x{motionImage.Height}.";
                        _lastError = failureReason;
                        return false;
                    }

                    if (hudlessColorImage.Width != parameters.OutputWidth || hudlessColorImage.Height != parameters.OutputHeight)
                    {
                        failureReason = $"DLSS-G HUD-less color extent must match the backbuffer: expected {parameters.OutputWidth}x{parameters.OutputHeight}, got {hudlessColorImage.Width}x{hudlessColorImage.Height}.";
                        _lastError = failureReason;
                        return false;
                    }

                    StreamlineViewportHandle viewport = _viewport;
                    uint frameIndex = parameters.FrameIndex;
                    StreamlineResult frameTokenResult = _getNewFrameToken(out IntPtr frameToken, ref frameIndex);
                    if (frameTokenResult != StreamlineResult.Ok || frameToken == IntPtr.Zero)
                    {
                        failureReason = $"slGetNewFrameToken failed for DLSS-G with {frameTokenResult}.";
                        _lastError = failureReason;
                        return false;
                    }

                    StreamlineResource depth = CreateResource(depthImage, parameters.InputWidth, parameters.InputHeight);
                    StreamlineResource motion = CreateResource(motionImage, parameters.InputWidth, parameters.InputHeight);
                    StreamlineResource hudlessColor = CreateResource(hudlessColorImage, parameters.OutputWidth, parameters.OutputHeight);

                    StreamlineExtent inputExtent = new()
                    {
                        Top = 0,
                        Left = 0,
                        Width = parameters.InputWidth,
                        Height = parameters.InputHeight,
                    };
                    StreamlineExtent outputExtent = new()
                    {
                        Top = 0,
                        Left = 0,
                        Width = parameters.OutputWidth,
                        Height = parameters.OutputHeight,
                    };

                    StreamlineResourceTag* tags = stackalloc StreamlineResourceTag[3];
                    tags[0] = CreateResourceTag(&depth, BufferTypeDepth, StreamlineResourceLifecycle.ValidUntilPresent, inputExtent);
                    tags[1] = CreateResourceTag(&motion, BufferTypeMotionVectors, StreamlineResourceLifecycle.ValidUntilPresent, inputExtent);
                    tags[2] = CreateResourceTag(&hudlessColor, BufferTypeHudLessColor, StreamlineResourceLifecycle.ValidUntilPresent, outputExtent);

                    IntPtr commandBufferPtr = ToIntPtr(commandBuffer.Handle);
                    StreamlineResult tagResult = _setTagForFrame(frameToken, ref viewport, (IntPtr)tags, 3, commandBufferPtr);
                    if (tagResult != StreamlineResult.Ok)
                    {
                        failureReason = DescribeFailure("slSetTagForFrame", tagResult, in parameters);
                        _lastError = failureReason;
                        return false;
                    }

                    StreamlineConstants constants = CreateConstants(parameters, _firstDispatch);
                    StreamlineResult constantsResult = _setConstants(ref constants, frameToken, ref viewport);
                    if (constantsResult != StreamlineResult.Ok)
                    {
                        failureReason = DescribeFailure("slSetConstants", constantsResult, in parameters);
                        _lastError = failureReason;
                        return false;
                    }

                    _firstDispatch = false;
                    return true;
                }

                public void Dispose()
                {
                    if (_disposed)
                        return;

                    _disposed = true;
                    ReleaseNativeVulkanRuntime();
                }

                public void ResetHistory()
                {
                    if (!_disposed)
                        _firstDispatch = true;
                }

                private static string DescribeFailure(
                    string apiName,
                    StreamlineResult result,
                    in VulkanUpscaleBridgeDispatchParameters parameters)
                {
                    string reason =
                        $"{apiName} failed for DLSS-G with {result}. " +
                        $"input={parameters.InputWidth}x{parameters.InputHeight}, output={parameters.OutputWidth}x{parameters.OutputHeight}, " +
                        $"frame={parameters.FrameIndex}, reset={parameters.ResetHistory}.";

                    string? streamlineMessage = _lastStreamlineWarningOrError ?? _lastStreamlineMessage;
                    if (!string.IsNullOrWhiteSpace(streamlineMessage))
                        reason += $" Last Streamline message: {streamlineMessage}.";

                    return reason;
                }

                private static StreamlineConstants CreateConstants(in VulkanUpscaleBridgeDispatchParameters parameters, bool firstDispatch)
                {
                    return new StreamlineConstants
                    {
                        Base = CreateBase(ConstantsStructType, 2),
                        CameraViewToClip = ToFloat4x4(parameters.CameraViewToClip),
                        ClipToCameraView = ToFloat4x4(parameters.ClipToCameraView),
                        ClipToLensClip = ToFloat4x4(Matrix4x4.Identity),
                        ClipToPrevClip = ToFloat4x4(parameters.ClipToPrevClip),
                        PrevClipToClip = ToFloat4x4(parameters.PrevClipToClip),
                        JitterOffset = new StreamlineFloat2(parameters.JitterOffsetX, parameters.JitterOffsetY),
                        MotionVectorScale = new StreamlineFloat2(parameters.MotionVectorScaleX, parameters.MotionVectorScaleY),
                        CameraPinholeOffset = new StreamlineFloat2(float.MaxValue, float.MaxValue),
                        CameraPosition = new StreamlineFloat3(parameters.CameraPosition.X, parameters.CameraPosition.Y, parameters.CameraPosition.Z),
                        CameraUp = new StreamlineFloat3(parameters.CameraUp.X, parameters.CameraUp.Y, parameters.CameraUp.Z),
                        CameraRight = new StreamlineFloat3(parameters.CameraRight.X, parameters.CameraRight.Y, parameters.CameraRight.Z),
                        CameraForward = new StreamlineFloat3(parameters.CameraForward.X, parameters.CameraForward.Y, parameters.CameraForward.Z),
                        CameraNear = parameters.CameraNear,
                        CameraFar = parameters.CameraFar,
                        CameraFov = parameters.CameraFovRadians,
                        CameraAspectRatio = parameters.CameraAspectRatio,
                        MotionVectorsInvalidValue = float.MaxValue,
                        DepthInverted = parameters.ReverseDepth ? StreamlineBoolean.True : StreamlineBoolean.False,
                        CameraMotionIncluded = StreamlineBoolean.True,
                        MotionVectors3D = StreamlineBoolean.False,
                        Reset = parameters.ResetHistory || firstDispatch ? StreamlineBoolean.True : StreamlineBoolean.False,
                        OrthographicProjection = parameters.IsOrthographic ? StreamlineBoolean.True : StreamlineBoolean.False,
                        MotionVectorsDilated = StreamlineBoolean.False,
                        MotionVectorsJittered = StreamlineBoolean.False,
                        MinRelativeLinearDepthObjectSeparation = 40.0f,
                    };
                }

                private static StreamlineResource CreateResource(in VulkanRenderer.VulkanStreamlineImage image, uint width, uint height)
                {
                    return new StreamlineResource
                    {
                        Base = CreateBase(ResourceStructType, 1),
                        Type = StreamlineResourceType.Texture2D,
                        Native = ToIntPtr(image.Image.Handle),
                        Memory = ToIntPtr(image.Memory.Handle),
                        View = ToIntPtr(image.View.Handle),
                        State = (uint)image.Layout,
                        Width = width,
                        Height = height,
                        NativeFormat = (uint)image.Format,
                        MipLevels = 1,
                        ArrayLayers = 1,
                        GpuVirtualAddress = 0,
                        Flags = 0,
                        Usage = (uint)image.Usage,
                        Reserved = 0,
                    };
                }

                private static unsafe StreamlineResourceTag CreateResourceTag(
                    StreamlineResource* resource,
                    uint bufferType,
                    StreamlineResourceLifecycle lifecycle,
                    StreamlineExtent extent)
                {
                    return new StreamlineResourceTag
                    {
                        Base = CreateBase(ResourceTagStructType, 1),
                        Resource = (IntPtr)resource,
                        Type = bufferType,
                        Lifecycle = lifecycle,
                        Extent = extent,
                    };
                }

                private static StreamlineFloat4x4 ToFloat4x4(Matrix4x4 matrix)
                {
                    return new StreamlineFloat4x4
                    {
                        Row0 = new StreamlineFloat4(matrix.M11, matrix.M12, matrix.M13, matrix.M14),
                        Row1 = new StreamlineFloat4(matrix.M21, matrix.M22, matrix.M23, matrix.M24),
                        Row2 = new StreamlineFloat4(matrix.M31, matrix.M32, matrix.M33, matrix.M34),
                        Row3 = new StreamlineFloat4(matrix.M41, matrix.M42, matrix.M43, matrix.M44),
                    };
                }
            }

            internal sealed class BridgeSession : IDisposable
            {
                private readonly VulkanUpscaleBridgeSidecar _sidecar;
                private readonly StreamlineViewportHandle _viewport;
                private bool _disposed;
                private bool _resourcesAllocated;
                private bool _firstDispatch = true;

                public BridgeSession(VulkanUpscaleBridgeSidecar sidecar, uint viewportId)
                {
                    _sidecar = sidecar;
                    _viewport = new StreamlineViewportHandle
                    {
                        Base = CreateBase(ViewportStructType, 1),
                        Value = viewportId,
                    };
                }

                public unsafe bool Record(VulkanUpscaleBridgeFrameSlot slot, in VulkanUpscaleBridgeDispatchParameters parameters, out string failureReason)
                {
                    failureReason = string.Empty;

                    if (_disposed)
                    {
                        failureReason = "Streamline bridge session was already disposed.";
                        return false;
                    }

                    if (_freeResources is null
                        || _setTagForFrame is null
                        || _setConstants is null
                        || _evaluateFeature is null
                        || _getNewFrameToken is null
                        || _setOptions is null)
                    {
                        failureReason = "Streamline core exports are not fully initialized.";
                        MarkTerminalBridgeFailure(failureReason);
                        return false;
                    }

                    IntPtr commandBuffer = ToIntPtr(slot.CommandBuffer.Handle);
                    StreamlineViewportHandle viewport = _viewport;

                    StreamlineDlssOptions options = CreateDlssOptions(parameters);
                    StreamlineResult setOptionsResult = _setOptions(ref viewport, ref options);
                    if (setOptionsResult != StreamlineResult.Ok)
                    {
                        failureReason = $"slDLSSSetOptions failed with {setOptionsResult}.";
                        MarkTerminalBridgeFailure(failureReason);
                        return false;
                    }

                    StreamlineConstants constants = CreateConstants(parameters, _firstDispatch);
                    uint frameIndex = parameters.FrameIndex;
                    StreamlineResult frameTokenResult = _getNewFrameToken(out IntPtr frameToken, ref frameIndex);
                    if (frameTokenResult != StreamlineResult.Ok || frameToken == IntPtr.Zero)
                    {
                        failureReason = $"slGetNewFrameToken failed with {frameTokenResult}.";
                        MarkTerminalBridgeFailure(failureReason);
                        return false;
                    }

                    StreamlineResource colorInput = CreateResource(slot.SourceColor, parameters.InputWidth, parameters.InputHeight);
                    StreamlineResource colorOutput = CreateResource(slot.OutputColor, parameters.OutputWidth, parameters.OutputHeight);
                    StreamlineResource depth = CreateResource(slot.SourceDepth, parameters.InputWidth, parameters.InputHeight);
                    StreamlineResource motion = CreateResource(slot.SourceMotion, parameters.InputWidth, parameters.InputHeight);
                    StreamlineResource exposure = parameters.HasExposureTexture
                        ? CreateResource(slot.Exposure, 1, 1)
                        : default;

                    StreamlineExtent inputExtent = new()
                    {
                        Top = 0,
                        Left = 0,
                        Width = parameters.InputWidth,
                        Height = parameters.InputHeight,
                    };
                    StreamlineExtent outputExtent = new()
                    {
                        Top = 0,
                        Left = 0,
                        Width = parameters.OutputWidth,
                        Height = parameters.OutputHeight,
                    };
                    StreamlineExtent exposureExtent = new()
                    {
                        Top = 0,
                        Left = 0,
                        Width = 1,
                        Height = 1,
                    };

                    StreamlineResourceTag* tags = stackalloc StreamlineResourceTag[5];
                    tags[0] = CreateResourceTag(&colorInput, BufferTypeScalingInputColor, StreamlineResourceLifecycle.ValidUntilEvaluate, inputExtent);
                    tags[1] = CreateResourceTag(&colorOutput, BufferTypeScalingOutputColor, StreamlineResourceLifecycle.ValidUntilEvaluate, outputExtent);
                    tags[2] = CreateResourceTag(&depth, BufferTypeDepth, StreamlineResourceLifecycle.ValidUntilEvaluate, inputExtent);
                    tags[3] = CreateResourceTag(&motion, BufferTypeMotionVectors, StreamlineResourceLifecycle.ValidUntilEvaluate, inputExtent);
                    uint tagCount = 4;
                    if (parameters.HasExposureTexture)
                        tags[tagCount++] = CreateResourceTag(&exposure, BufferTypeExposure, StreamlineResourceLifecycle.ValidUntilEvaluate, exposureExtent);

                    StreamlineResult tagResult = _setTagForFrame(frameToken, ref viewport, (IntPtr)tags, tagCount, commandBuffer);
                    if (tagResult != StreamlineResult.Ok)
                    {
                        failureReason = DescribeStreamlineFailure(
                            "slSetTagForFrame",
                            tagResult,
                            in parameters,
                            ref options,
                            $"tagCount={tagCount}");
                        MarkTerminalBridgeFailure(failureReason);
                        return false;
                    }

                    StreamlineResult constantsResult = _setConstants(ref constants, frameToken, ref viewport);
                    if (constantsResult != StreamlineResult.Ok)
                    {
                        failureReason = DescribeStreamlineFailure(
                            "slSetConstants",
                            constantsResult,
                            in parameters,
                            ref options,
                            $"reset={constants.Reset}");
                        MarkTerminalBridgeFailure(failureReason);
                        return false;
                    }

                    StreamlineViewportHandle viewportInput = viewport;
                    IntPtr* inputs = stackalloc IntPtr[1];
                    inputs[0] = (IntPtr)(&viewportInput);

                    StreamlineResult evaluateResult = _evaluateFeature(FeatureDlss, frameToken, (IntPtr)inputs, 1, commandBuffer);
                    if (evaluateResult != StreamlineResult.Ok)
                    {
                        failureReason = DescribeStreamlineFailure(
                            "slEvaluateFeature",
                            evaluateResult,
                            in parameters,
                            ref options,
                            "inputs=viewport");
                        MarkTerminalBridgeFailure(failureReason);
                        return false;
                    }

                    _resourcesAllocated = true;

                    _sidecar.RecordTransitionImageLayout(slot.CommandBuffer, slot.SourceColor, ImageLayout.General, PipelineStageFlags.AllCommandsBit, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit);
                    _sidecar.RecordTransitionImageLayout(slot.CommandBuffer, slot.SourceDepth, ImageLayout.General, PipelineStageFlags.AllCommandsBit, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit);
                    _sidecar.RecordTransitionImageLayout(slot.CommandBuffer, slot.SourceMotion, ImageLayout.General, PipelineStageFlags.AllCommandsBit, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit);
                    if (parameters.HasExposureTexture)
                        _sidecar.RecordTransitionImageLayout(slot.CommandBuffer, slot.Exposure, ImageLayout.General, PipelineStageFlags.AllCommandsBit, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit);
                    _sidecar.RecordTransitionImageLayout(slot.CommandBuffer, slot.OutputColor, ImageLayout.General, PipelineStageFlags.AllCommandsBit, AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit);

                    _firstDispatch = false;
                    return true;
                }

                public void Dispose()
                {
                    if (_disposed)
                        return;

                    _disposed = true;

                    FreeAllocatedResources();
                    ReleaseBridgeRuntime();
                }

                public void ResetResources()
                {
                    if (_disposed)
                        return;

                    FreeAllocatedResources();
                    _firstDispatch = true;
                }

                private void FreeAllocatedResources()
                {
                    if (_resourcesAllocated && _freeResources is not null)
                    {
                        StreamlineViewportHandle viewport = _viewport;
                        _freeResources(FeatureDlss, ref viewport);
                        _resourcesAllocated = false;
                    }
                }

                private static StreamlineDlssOptions CreateDlssOptions(in VulkanUpscaleBridgeDispatchParameters parameters)
                {
                    float preExposure = parameters.HasExposureTexture
                        ? 1.0f
                        : MathF.Max(parameters.ExposureScale, 0.0001f);

                    return new StreamlineDlssOptions
                    {
                        Base = CreateBase(DlssOptionsStructType, 3),
                        Mode = ResolveDlssMode(parameters),
                        OutputWidth = parameters.OutputWidth,
                        OutputHeight = parameters.OutputHeight,
                        Sharpness = parameters.DlssSharpness,
                        PreExposure = preExposure,
                        ExposureScale = 1.0f,
                        ColorBuffersHdr = parameters.OutputHdr ? StreamlineBoolean.True : StreamlineBoolean.False,
                        IndicatorInvertAxisX = StreamlineBoolean.False,
                        IndicatorInvertAxisY = StreamlineBoolean.False,
                        DlaaPreset = StreamlineDlssPreset.Default,
                        QualityPreset = StreamlineDlssPreset.Default,
                        BalancedPreset = StreamlineDlssPreset.Default,
                        PerformancePreset = StreamlineDlssPreset.Default,
                        UltraPerformancePreset = StreamlineDlssPreset.Default,
                        UltraQualityPreset = StreamlineDlssPreset.Default,
                        UseAutoExposure = StreamlineBoolean.False,
                        AlphaUpscalingEnabled = StreamlineBoolean.False,
                    };
                }

                private static StreamlineDlssMode ResolveDlssMode(in VulkanUpscaleBridgeDispatchParameters parameters)
                {
                    if (parameters.InputWidth == parameters.OutputWidth && parameters.InputHeight == parameters.OutputHeight)
                        return StreamlineDlssMode.Dlaa;

                    if (parameters.DlssQuality != EDlssQualityMode.Custom)
                    {
                        return parameters.DlssQuality switch
                        {
                            EDlssQualityMode.UltraPerformance => StreamlineDlssMode.UltraPerformance,
                            EDlssQualityMode.Performance => StreamlineDlssMode.MaxPerformance,
                            EDlssQualityMode.Balanced => StreamlineDlssMode.Balanced,
                            EDlssQualityMode.Quality => StreamlineDlssMode.MaxQuality,
                            EDlssQualityMode.UltraQuality => StreamlineDlssMode.MaxQuality,
                            _ => StreamlineDlssMode.MaxQuality,
                        };
                    }

                    float inputScale = parameters.OutputWidth == 0 ? 1.0f : parameters.InputWidth / (float)parameters.OutputWidth;
                    return inputScale switch
                    {
                        <= 0.40f => StreamlineDlssMode.UltraPerformance,
                        <= 0.54f => StreamlineDlssMode.MaxPerformance,
                        <= 0.62f => StreamlineDlssMode.Balanced,
                        _ => StreamlineDlssMode.MaxQuality,
                    };
                }

                private static string DescribeStreamlineFailure(
                    string apiName,
                    StreamlineResult result,
                    in VulkanUpscaleBridgeDispatchParameters parameters,
                    ref StreamlineDlssOptions options,
                    string? additionalContext)
                {
                    string reason =
                        $"{apiName} failed with {result}. " +
                        $"DLSS mode={options.Mode}, input={parameters.InputWidth}x{parameters.InputHeight}, " +
                        $"output={parameters.OutputWidth}x{parameters.OutputHeight}, hdr={parameters.OutputHdr}, " +
                        $"quality={parameters.DlssQuality}, exposureTexture={parameters.HasExposureTexture}";

                    if (!string.IsNullOrWhiteSpace(additionalContext))
                        reason += $", {additionalContext}";

                    string optimalSettings = DescribeOptimalSettings(ref options);
                    if (!string.IsNullOrWhiteSpace(optimalSettings))
                        reason += $". {optimalSettings}";

                    string? streamlineMessage = _lastStreamlineWarningOrError ?? _lastStreamlineMessage;
                    if (!string.IsNullOrWhiteSpace(streamlineMessage))
                        reason += $". Last Streamline message: {streamlineMessage}";

                    return reason + ".";
                }

                private static string DescribeOptimalSettings(ref StreamlineDlssOptions options)
                {
                    if (_getOptimalSettings is null)
                        return string.Empty;

                    StreamlineDlssOptimalSettings settings = new()
                    {
                        Base = CreateBase(DlssOptimalSettingsStructType, 1),
                    };

                    StreamlineResult result = _getOptimalSettings(ref options, ref settings);
                    if (result != StreamlineResult.Ok)
                        return $"slDLSSGetOptimalSettings failed with {result}";

                    return
                        $"DLSS optimal input={settings.OptimalRenderWidth}x{settings.OptimalRenderHeight}, " +
                        $"range={settings.RenderWidthMin}x{settings.RenderHeightMin}-{settings.RenderWidthMax}x{settings.RenderHeightMax}, " +
                        $"sharpness={settings.OptimalSharpness:0.###}";
                }

                private static StreamlineConstants CreateConstants(in VulkanUpscaleBridgeDispatchParameters parameters, bool firstDispatch)
                {
                    return new StreamlineConstants
                    {
                        Base = CreateBase(ConstantsStructType, 2),
                        CameraViewToClip = ToFloat4x4(parameters.CameraViewToClip),
                        ClipToCameraView = ToFloat4x4(parameters.ClipToCameraView),
                        ClipToLensClip = ToFloat4x4(Matrix4x4.Identity),
                        ClipToPrevClip = ToFloat4x4(parameters.ClipToPrevClip),
                        PrevClipToClip = ToFloat4x4(parameters.PrevClipToClip),
                        JitterOffset = new StreamlineFloat2(parameters.JitterOffsetX, parameters.JitterOffsetY),
                        MotionVectorScale = new StreamlineFloat2(parameters.MotionVectorScaleX, parameters.MotionVectorScaleY),
                        CameraPinholeOffset = new StreamlineFloat2(float.MaxValue, float.MaxValue),
                        CameraPosition = new StreamlineFloat3(parameters.CameraPosition.X, parameters.CameraPosition.Y, parameters.CameraPosition.Z),
                        CameraUp = new StreamlineFloat3(parameters.CameraUp.X, parameters.CameraUp.Y, parameters.CameraUp.Z),
                        CameraRight = new StreamlineFloat3(parameters.CameraRight.X, parameters.CameraRight.Y, parameters.CameraRight.Z),
                        CameraForward = new StreamlineFloat3(parameters.CameraForward.X, parameters.CameraForward.Y, parameters.CameraForward.Z),
                        CameraNear = parameters.CameraNear,
                        CameraFar = parameters.CameraFar,
                        CameraFov = parameters.CameraFovRadians,
                        CameraAspectRatio = parameters.CameraAspectRatio,
                        MotionVectorsInvalidValue = float.MaxValue,
                        DepthInverted = parameters.ReverseDepth ? StreamlineBoolean.True : StreamlineBoolean.False,
                        CameraMotionIncluded = StreamlineBoolean.True,
                        MotionVectors3D = StreamlineBoolean.False,
                        Reset = parameters.ResetHistory || firstDispatch ? StreamlineBoolean.True : StreamlineBoolean.False,
                        OrthographicProjection = parameters.IsOrthographic ? StreamlineBoolean.True : StreamlineBoolean.False,
                        MotionVectorsDilated = StreamlineBoolean.False,
                        MotionVectorsJittered = StreamlineBoolean.False,
                        MinRelativeLinearDepthObjectSeparation = 40.0f,
                    };
                }

                private static StreamlineResource CreateResource(VulkanUpscaleBridgeSharedImage image, uint width, uint height)
                {
                    return new StreamlineResource
                    {
                        Base = CreateBase(ResourceStructType, 1),
                        Type = StreamlineResourceType.Texture2D,
                        Native = ToIntPtr(image.VulkanImage.Handle),
                        Memory = ToIntPtr(image.VulkanMemory.Handle),
                        View = ToIntPtr(image.VulkanImageView.Handle),
                        State = (uint)image.CurrentLayout,
                        Width = width,
                        Height = height,
                        NativeFormat = (uint)image.VulkanFormat,
                        MipLevels = 1,
                        ArrayLayers = 1,
                        GpuVirtualAddress = 0,
                        Flags = 0,
                        Usage = (uint)image.Usage,
                        Reserved = 0,
                    };
                }

                private static unsafe StreamlineResourceTag CreateResourceTag(StreamlineResource* resource, uint bufferType, StreamlineResourceLifecycle lifecycle, StreamlineExtent extent)
                {
                    return new StreamlineResourceTag
                    {
                        Base = CreateBase(ResourceTagStructType, 1),
                        Resource = (IntPtr)resource,
                        Type = bufferType,
                        Lifecycle = lifecycle,
                        Extent = extent,
                    };
                }

                private static StreamlineFloat4x4 ToFloat4x4(Matrix4x4 matrix)
                {
                    return new StreamlineFloat4x4
                    {
                        Row0 = new StreamlineFloat4(matrix.M11, matrix.M12, matrix.M13, matrix.M14),
                        Row1 = new StreamlineFloat4(matrix.M21, matrix.M22, matrix.M23, matrix.M24),
                        Row2 = new StreamlineFloat4(matrix.M31, matrix.M32, matrix.M33, matrix.M34),
                        Row3 = new StreamlineFloat4(matrix.M41, matrix.M42, matrix.M43, matrix.M44),
                    };
                }
            }

            private static IntPtr ToIntPtr(ulong handle)
                => unchecked((IntPtr)(nint)handle);

            private static IntPtr ToIntPtr(nint handle)
                => (IntPtr)handle;

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate StreamlineResult SlInitDelegate(ref StreamlinePreferences preferences, ulong sdkVersion);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate StreamlineResult SlShutdownDelegate();

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate StreamlineResult SlGetFeatureRequirementsDelegate(uint feature, ref StreamlineFeatureRequirements requirements);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate StreamlineResult SlSetVulkanInfoDelegate(ref StreamlineVulkanInfo info);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate StreamlineResult SlEvaluateFeatureDelegate(uint feature, IntPtr frameToken, IntPtr inputs, uint numInputs, IntPtr commandBuffer);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate StreamlineResult SlAllocateResourcesDelegate(IntPtr commandBuffer, uint feature, ref StreamlineViewportHandle viewport);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate StreamlineResult SlFreeResourcesDelegate(uint feature, ref StreamlineViewportHandle viewport);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate StreamlineResult SlSetTagForFrameDelegate(IntPtr frameToken, ref StreamlineViewportHandle viewport, IntPtr resourceTags, uint resourceTagCount, IntPtr commandBuffer);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate StreamlineResult SlSetConstantsDelegate(ref StreamlineConstants values, IntPtr frameToken, ref StreamlineViewportHandle viewport);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate StreamlineResult SlGetFeatureFunctionDelegate(uint feature, [MarshalAs(UnmanagedType.LPStr)] string functionName, out IntPtr function);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate StreamlineResult SlGetNewFrameTokenDelegate(out IntPtr token, ref uint frameIndex);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate StreamlineResult SlDlssSetOptionsDelegate(ref StreamlineViewportHandle viewport, ref StreamlineDlssOptions options);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate StreamlineResult SlDlssGetOptimalSettingsDelegate(ref StreamlineDlssOptions options, ref StreamlineDlssOptimalSettings settings);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate StreamlineResult SlDlssGSetOptionsDelegate(ref StreamlineViewportHandle viewport, ref StreamlineDlssGOptions options);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate StreamlineResult SlDlssGGetStateDelegate(ref StreamlineViewportHandle viewport, ref StreamlineDlssGState state, IntPtr options);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate StreamlineResult SlReflexSetOptionsDelegate(ref StreamlineReflexOptions options);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate StreamlineResult SlPclSetMarkerDelegate(StreamlinePclMarker marker, IntPtr frame);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate void SlLogMessageCallbackDelegate(StreamlineLogType type, IntPtr message);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private unsafe delegate IntPtr VkGetDeviceProcAddrProxyDelegate(Device device, byte* name);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private unsafe delegate Result VkCreateSwapchainKhrProxyDelegate(Device device, SwapchainCreateInfoKHR* createInfo, AllocationCallbacks* allocator, SwapchainKHR* swapchain);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private unsafe delegate void VkDestroySwapchainKhrProxyDelegate(Device device, SwapchainKHR swapchain, AllocationCallbacks* allocator);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private unsafe delegate Result VkGetSwapchainImagesKhrProxyDelegate(Device device, SwapchainKHR swapchain, uint* imageCount, Image* images);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private unsafe delegate Result VkAcquireNextImageKhrProxyDelegate(Device device, SwapchainKHR swapchain, ulong timeout, Silk.NET.Vulkan.Semaphore semaphore, Fence fence, uint* imageIndex);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private unsafe delegate Result VkQueuePresentKhrProxyDelegate(Queue queue, PresentInfoKHR* presentInfo);

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate Result VkDeviceWaitIdleProxyDelegate(Device device);

            private enum StreamlineResult
            {
                Ok = 0,
                ErrorIO,
                ErrorDriverOutOfDate,
                ErrorOSOutOfDate,
                ErrorOSDisabledHWS,
                ErrorDeviceNotCreated,
                ErrorNoSupportedAdapterFound,
                ErrorAdapterNotSupported,
                ErrorNoPlugins,
                ErrorVulkanAPI,
                ErrorDXGIAPI,
                ErrorD3DAPI,
                ErrorNRDAPI,
                ErrorNVAPI,
                ErrorReflexAPI,
                ErrorNGXFailed,
                ErrorJSONParsing,
                ErrorMissingProxy,
                ErrorMissingResourceState,
                ErrorInvalidIntegration,
                ErrorMissingInputParameter,
                ErrorNotInitialized,
                ErrorComputeFailed,
                ErrorInitNotCalled,
                ErrorExceptionHandler,
                ErrorInvalidParameter,
                ErrorMissingConstants,
                ErrorDuplicatedConstants,
                ErrorMissingOrInvalidAPI,
                ErrorCommonConstantsMissing,
                ErrorUnsupportedInterface,
                ErrorFeatureMissing,
                ErrorFeatureNotSupported,
                ErrorFeatureMissingHooks,
                ErrorFeatureFailedToLoad,
                ErrorFeatureWrongPriority,
                ErrorFeatureMissingDependency,
                ErrorFeatureManagerInvalidState,
                ErrorInvalidState,
                WarnOutOfVRAM,
            }

            private enum StreamlineLogLevel : uint
            {
                Off = 0,
                Default = 1,
                Verbose = 2,
            }

            private enum StreamlineLogType : uint
            {
                Info = 0,
                Warn = 1,
                Error = 2,
                Count = 3,
            }

            [Flags]
            private enum StreamlinePreferenceFlags : ulong
            {
                // Keep in sync with sl::PreferenceFlags in sl_core_types.h.
                DisableCommandListStateTracking = 1UL << 0,
                DisableDebugText = 1UL << 1,
                UseManualHooking = 1UL << 2,
                AllowOta = 1UL << 3,
                BypassOsVersionCheck = 1UL << 4,
                UseDxgiFactoryProxy = 1UL << 5,
                LoadDownloadedPlugins = 1UL << 6,
                UseFrameBasedResourceTagging = 1UL << 7,
            }

            [Flags]
            private enum StreamlineFeatureRequirementFlags : uint
            {
                D3D11Supported = 1u << 0,
                D3D12Supported = 1u << 1,
                VulkanSupported = 1u << 2,
                VSyncOffRequired = 1u << 3,
                HardwareSchedulingRequired = 1u << 4,
            }

            private enum StreamlineEngineType : uint
            {
                Custom = 0,
            }

            private enum StreamlineRenderApi : uint
            {
                D3D11 = 0,
                D3D12 = 1,
                Vulkan = 2,
            }

            private enum StreamlineBoolean : byte
            {
                False = 0,
                True = 1,
                Invalid = 2,
            }

            private enum StreamlineDlssMode : uint
            {
                Off = 0,
                MaxPerformance = 1,
                Balanced = 2,
                MaxQuality = 3,
                UltraPerformance = 4,
                UltraQuality = 5,
                Dlaa = 6,
            }

            private enum StreamlineDlssPreset : uint
            {
                Default = 0,
            }

            private enum StreamlineDlssGMode : uint
            {
                Off = 0,
                On = 1,
                Auto = 2,
                Dynamic = 3,
                Count = 4,
            }

            [Flags]
            private enum StreamlineDlssGFlags : uint
            {
                None = 0,
                ShowOnlyInterpolatedFrame = 1u << 0,
                DynamicResolutionEnabled = 1u << 1,
                RequestVramEstimate = 1u << 2,
                RetainResourcesWhenOff = 1u << 3,
                EnableFullscreenMenuDetection = 1u << 4,
            }

            private enum StreamlineDlssGQueueParallelismMode : uint
            {
                BlockPresentingClientQueue = 0,
                BlockNoClientQueues = 1,
                Count = 2,
            }

            [Flags]
            private enum StreamlineDlssGStatus : uint
            {
                Ok = 0,
                FailResolutionTooLow = 1u << 0,
                FailReflexNotDetectedAtRuntime = 1u << 1,
                FailHdrFormatNotSupported = 1u << 2,
                FailCommonConstantsInvalid = 1u << 3,
                FailGetCurrentBackBufferIndexNotCalled = 1u << 4,
                Reserved5 = 1u << 5,
            }

            private enum StreamlineReflexMode : uint
            {
                Off = 0,
                LowLatency = 1,
                LowLatencyWithBoost = 2,
            }

            internal enum StreamlinePclMarker : uint
            {
                SimulationStart = 0,
                SimulationEnd = 1,
                RenderSubmitStart = 2,
                RenderSubmitEnd = 3,
                PresentStart = 4,
                PresentEnd = 5,
                TriggerFlash = 7,
                PCLatencyPing = 8,
                OutOfBandRenderSubmitStart = 9,
                OutOfBandRenderSubmitEnd = 10,
                OutOfBandPresentStart = 11,
                OutOfBandPresentEnd = 12,
                ControllerInputSample = 13,
                DeltaTCalculation = 14,
                LateWarpPresentStart = 15,
                LateWarpPresentEnd = 16,
                CameraConstructed = 17,
                LateWarpRenderSubmitStart = 18,
                LateWarpRenderSubmitEnd = 19,
                VendorInternalAsyncPresentStart = 20,
                VendorInternalAsyncPresentEnd = 21,
                NumPresentsInBatch = 22,
            }

            private enum StreamlineResourceType : byte
            {
                Texture2D = 0,
            }

            private enum StreamlineResourceLifecycle
            {
                OnlyValidNow = 0,
                ValidUntilPresent = 1,
                ValidUntilEvaluate = 2,
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineStructType
            {
                public uint Data1;
                public ushort Data2;
                public ushort Data3;
                public byte Data40;
                public byte Data41;
                public byte Data42;
                public byte Data43;
                public byte Data44;
                public byte Data45;
                public byte Data46;
                public byte Data47;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineBaseStructure
            {
                public IntPtr Next;
                public StreamlineStructType StructType;
                // Keep in sync with sl::BaseStructure in sl_struct.h.
                public nuint StructVersion;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlinePreferences
            {
                public StreamlineBaseStructure Base;
                public byte ShowConsole;
                public StreamlineLogLevel LogLevel;
                public IntPtr PathsToPlugins;
                public uint NumPathsToPlugins;
                public IntPtr PathToLogsAndData;
                public IntPtr AllocateCallback;
                public IntPtr ReleaseCallback;
                public IntPtr LogMessageCallback;
                public StreamlinePreferenceFlags Flags;
                public IntPtr FeaturesToLoad;
                public uint NumFeaturesToLoad;
                public uint ApplicationId;
                public StreamlineEngineType Engine;
                public IntPtr EngineVersion;
                public IntPtr ProjectId;
                public StreamlineRenderApi RenderApi;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineVersion
            {
                public uint Major;
                public uint Minor;
                public uint Build;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineFeatureRequirements
            {
                public StreamlineBaseStructure Base;
                public StreamlineFeatureRequirementFlags Flags;
                public uint MaxNumCpuThreads;
                public uint MaxNumViewports;
                public uint NumRequiredTags;
                public IntPtr RequiredTags;
                public StreamlineVersion OsVersionDetected;
                public StreamlineVersion OsVersionRequired;
                public StreamlineVersion DriverVersionDetected;
                public StreamlineVersion DriverVersionRequired;
                public uint VkNumComputeQueuesRequired;
                public uint VkNumGraphicsQueuesRequired;
                public uint VkNumDeviceExtensions;
                public IntPtr VkDeviceExtensions;
                public uint VkNumInstanceExtensions;
                public IntPtr VkInstanceExtensions;
                public uint VkNumFeatures12;
                public IntPtr VkFeatures12;
                public uint VkNumFeatures13;
                public IntPtr VkFeatures13;
                public uint VkNumOpticalFlowQueuesRequired;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineVulkanInfo
            {
                public StreamlineBaseStructure Base;
                public Device Device;
                public Instance Instance;
                public PhysicalDevice PhysicalDevice;
                public uint ComputeQueueIndex;
                public uint ComputeQueueFamily;
                public uint GraphicsQueueIndex;
                public uint GraphicsQueueFamily;
                public uint OpticalFlowQueueIndex;
                public uint OpticalFlowQueueFamily;
                public byte UseNativeOpticalFlowMode;
                public uint ComputeQueueCreateFlags;
                public uint GraphicsQueueCreateFlags;
                public uint OpticalFlowQueueCreateFlags;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineViewportHandle
            {
                public StreamlineBaseStructure Base;
                public uint Value;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineExtent
            {
                public uint Top;
                public uint Left;
                public uint Width;
                public uint Height;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineResource
            {
                public StreamlineBaseStructure Base;
                public StreamlineResourceType Type;
                public IntPtr Native;
                public IntPtr Memory;
                public IntPtr View;
                public uint State;
                public uint Width;
                public uint Height;
                public uint NativeFormat;
                public uint MipLevels;
                public uint ArrayLayers;
                public ulong GpuVirtualAddress;
                public uint Flags;
                public uint Usage;
                public uint Reserved;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineResourceTag
            {
                public StreamlineBaseStructure Base;
                public IntPtr Resource;
                public uint Type;
                public StreamlineResourceLifecycle Lifecycle;
                public StreamlineExtent Extent;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineFloat2
            {
                public StreamlineFloat2(float x, float y)
                {
                    X = x;
                    Y = y;
                }

                public float X;
                public float Y;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineFloat3
            {
                public StreamlineFloat3(float x, float y, float z)
                {
                    X = x;
                    Y = y;
                    Z = z;
                }

                public float X;
                public float Y;
                public float Z;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineFloat4
            {
                public StreamlineFloat4(float x, float y, float z, float w)
                {
                    X = x;
                    Y = y;
                    Z = z;
                    W = w;
                }

                public float X;
                public float Y;
                public float Z;
                public float W;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineFloat4x4
            {
                public StreamlineFloat4 Row0;
                public StreamlineFloat4 Row1;
                public StreamlineFloat4 Row2;
                public StreamlineFloat4 Row3;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineConstants
            {
                public StreamlineBaseStructure Base;
                public StreamlineFloat4x4 CameraViewToClip;
                public StreamlineFloat4x4 ClipToCameraView;
                public StreamlineFloat4x4 ClipToLensClip;
                public StreamlineFloat4x4 ClipToPrevClip;
                public StreamlineFloat4x4 PrevClipToClip;
                public StreamlineFloat2 JitterOffset;
                public StreamlineFloat2 MotionVectorScale;
                public StreamlineFloat2 CameraPinholeOffset;
                public StreamlineFloat3 CameraPosition;
                public StreamlineFloat3 CameraUp;
                public StreamlineFloat3 CameraRight;
                public StreamlineFloat3 CameraForward;
                public float CameraNear;
                public float CameraFar;
                public float CameraFov;
                public float CameraAspectRatio;
                public float MotionVectorsInvalidValue;
                public StreamlineBoolean DepthInverted;
                public StreamlineBoolean CameraMotionIncluded;
                public StreamlineBoolean MotionVectors3D;
                public StreamlineBoolean Reset;
                public StreamlineBoolean OrthographicProjection;
                public StreamlineBoolean MotionVectorsDilated;
                public StreamlineBoolean MotionVectorsJittered;
                public float MinRelativeLinearDepthObjectSeparation;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineDlssOptions
            {
                public StreamlineBaseStructure Base;
                public StreamlineDlssMode Mode;
                public uint OutputWidth;
                public uint OutputHeight;
                public float Sharpness;
                public float PreExposure;
                public float ExposureScale;
                public StreamlineBoolean ColorBuffersHdr;
                public StreamlineBoolean IndicatorInvertAxisX;
                public StreamlineBoolean IndicatorInvertAxisY;
                private byte _padding;
                public StreamlineDlssPreset DlaaPreset;
                public StreamlineDlssPreset QualityPreset;
                public StreamlineDlssPreset BalancedPreset;
                public StreamlineDlssPreset PerformancePreset;
                public StreamlineDlssPreset UltraPerformancePreset;
                public StreamlineDlssPreset UltraQualityPreset;
                public StreamlineBoolean UseAutoExposure;
                public StreamlineBoolean AlphaUpscalingEnabled;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineDlssOptimalSettings
            {
                public StreamlineBaseStructure Base;
                public uint OptimalRenderWidth;
                public uint OptimalRenderHeight;
                public float OptimalSharpness;
                public uint RenderWidthMin;
                public uint RenderHeightMin;
                public uint RenderWidthMax;
                public uint RenderHeightMax;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineDlssGOptions
            {
                public StreamlineBaseStructure Base;
                public StreamlineDlssGMode Mode;
                public uint NumFramesToGenerate;
                public StreamlineDlssGFlags Flags;
                public uint DynamicResWidth;
                public uint DynamicResHeight;
                public uint NumBackBuffers;
                public uint MvecDepthWidth;
                public uint MvecDepthHeight;
                public uint ColorWidth;
                public uint ColorHeight;
                public uint ColorBufferFormat;
                public uint MvecBufferFormat;
                public uint DepthBufferFormat;
                public uint HudLessBufferFormat;
                public uint UiBufferFormat;
                public IntPtr OnErrorCallback;
                public StreamlineBoolean BReserved15;
                public StreamlineDlssGQueueParallelismMode QueueParallelismMode;
                public StreamlineBoolean EnableUserInterfaceRecomposition;
                public float DynamicTargetFrameRate;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineDlssGState
            {
                public StreamlineBaseStructure Base;
                public ulong EstimatedVramUsageInBytes;
                public StreamlineDlssGStatus Status;
                public uint MinWidthOrHeight;
                public uint NumFramesActuallyPresented;
                public uint NumFramesToGenerateMax;
                public StreamlineBoolean BReserved4;
                public StreamlineBoolean BIsVsyncSupportAvailable;
                public IntPtr InputsProcessingCompletionFence;
                public ulong LastPresentInputsProcessingCompletionFenceValue;
                public StreamlineBoolean BIsDynamicMfgSupported;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct StreamlineReflexOptions
            {
                public StreamlineBaseStructure Base;
                public StreamlineReflexMode Mode;
                public uint FrameLimitUs;
                public byte UseMarkersToOptimize;
                public ushort VirtualKey;
                public uint IdThread;
            }
        }
    }
}
