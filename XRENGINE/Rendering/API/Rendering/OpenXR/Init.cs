using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.OpenGL;
using Silk.NET.OpenXR;
using Silk.NET.OpenXR.Extensions.KHR;
using Silk.NET.Windowing;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using XREngine;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;
using XREngine.Scene.Transforms;
using Debug = XREngine.Debug;

/// <summary>
/// Provides an implementation of XR functionality using the OpenXR standard.
/// Handles initialization, session management, swapchain creation, and frame rendering.
/// </summary>
public unsafe partial class OpenXRAPI : XRBase
{
    // NOTE: This class is split across multiple files in this folder.
    // - Init.cs: engine integration, session creation, swapchains, frame lifecycle
    // - Instance.cs / Extensions.cs / Validation.cs: OpenXR instance + extension plumbing
    // - OpenXRAPI.NativeLoader.cs: OpenXR loader DLL resolution
    // - OpenXRAPI.OpenGL.Wgl.cs: WGL helpers for OpenGL binding diagnostics

    public OpenXRAPI()
    {
        EnsureOpenXRLoaderResolutionConfigured();
        try
        {
            Api = XR.GetApi();
        }
        catch (FileNotFoundException ex)
        {
            Debug.LogWarning($"OpenXR loader was not found. BaseDir='{AppContext.BaseDirectory}', CWD='{Environment.CurrentDirectory}', ActiveRuntime='{TryGetOpenXRActiveRuntime() ?? "<unknown>"}'. {ex.Message}");
            throw;
        }
    }

    #region Lifetime

    ~OpenXRAPI()
    {
        CleanUp();
        Api?.Dispose();
    }

    #endregion

    #region Core OpenXR state

    /// <summary>
    /// The system ID used to identify the XR system.
    /// </summary>
    private ulong _systemId;

    /// <summary>
    /// The OpenXR session handle.
    /// </summary>
    private Session _session;

    /// <summary>
    /// The associated window for rendering.
    /// </summary>
    private XRWindow? _window;

    /// <summary>
    /// The number of views (1 for AR phone rendering, 2 for stereo rendering, or 4 for fovated rendering).
    /// </summary>
    private uint _viewCount;

    private Space _appSpace;
    private View[] _views = new View[2];
    private FrameState _frameState;
    private GL? _gl;
    private System.Action? _deferredOpenGlInit;

    private bool _sessionBegun;

    #endregion

    #region Engine camera + viewport integration

    private XRViewport? _openXrLeftViewport;
    private XRViewport? _openXrRightViewport;
    private XRCamera? _openXrLeftEyeCamera;
    private XRCamera? _openXrRightEyeCamera;

    private readonly object _openXrEyePoseLock = new();
    private Matrix4x4 _openXrLeftEyeLocalPose = Matrix4x4.Identity;
    private Matrix4x4 _openXrRightEyeLocalPose = Matrix4x4.Identity;
    private TransformBase? _openXrLocomotionRoot;

    // OpenXR renders each eye in a separate pipeline execution (stereoPass=false).
    // IMPORTANT: The OpenXR eye viewports must NOT share the desktop viewport's RenderPipeline instance.
    // Sharing a pipeline instance across viewports of different sizes can cause constant FBO/cache churn.
    // We therefore create a dedicated pipeline instance for OpenXR and (when applicable) force it to be non-stereo.
    private RenderPipeline? _openXrNonStereoPipelineOverride;
    private RenderPipeline? _openXrNonStereoPipelineOverrideSource;

    /// <summary>
    /// Copies post-process stage parameter values from <paramref name="sourceCamera"/> to <paramref name="destinationCamera"/>.
    /// Best-effort only: failures should not crash the render thread.
    /// </summary>
    private static void CopyPostProcessState(RenderPipeline sourcePipeline, RenderPipeline destinationPipeline, XRCamera sourceCamera, XRCamera destinationCamera)
    {
        try
        {
            var srcState = sourceCamera.PostProcessStates.GetOrCreateState(sourcePipeline);
            var dstState = destinationCamera.PostProcessStates.GetOrCreateState(destinationPipeline);

            foreach (var (stageKey, srcStage) in srcState.Stages)
            {
                if (dstState.GetStage(stageKey) is not { } dstStage)
                    continue;

                foreach (var (param, value) in srcStage.Values)
                    dstStage.SetValue<object?>(param, value);
            }

            // Some cameras use an explicit post-process material override; keep it consistent.
            destinationCamera.PostProcessMaterial = sourceCamera.PostProcessMaterial;
        }
        catch
        {
            // Best-effort only; falling back to defaults is preferable to crashing the render thread.
        }
    }

    private XRWorldInstance? _openXrFrameWorld;
    private XRCamera? _openXrFrameBaseCamera;

    #endregion

    #region Frame lifecycle (thread handoff)

    /// <summary>
    /// Returns a dedicated, per-eye (non-stereo) pipeline instance for OpenXR rendering.
    /// OpenXR viewports must not share the desktop pipeline instance to avoid FBO/cache churn.
    /// </summary>
    private RenderPipeline? SelectOpenXrRenderPipeline(RenderPipeline? sourcePipeline)
    {
        if (sourcePipeline is null)
            return null;

        // Cache one dedicated OpenXR pipeline per distinct source pipeline reference.
        // Even if the source pipeline is already non-stereo, we still do NOT reuse it.
        if (ReferenceEquals(_openXrNonStereoPipelineOverrideSource, sourcePipeline) && _openXrNonStereoPipelineOverride is not null)
            return _openXrNonStereoPipelineOverride;

        _openXrNonStereoPipelineOverrideSource = sourcePipeline;

        RenderPipeline dedicated;
        try
        {
            // Most common case: mirror the default pipeline but force non-stereo for per-eye rendering.
            if (sourcePipeline is DefaultRenderPipeline defaultPipeline)
            {
                dedicated = new DefaultRenderPipeline(stereo: false)
                {
                    IsShadowPass = defaultPipeline.IsShadowPass,
                };
            }
            else
            {
                // Best-effort: create a separate instance of the same pipeline type.
                // If the pipeline type has no public parameterless ctor, fall back to the engine default.
                dedicated = (RenderPipeline?)Activator.CreateInstance(sourcePipeline.GetType())
                           ?? new DefaultRenderPipeline(stereo: false);

                dedicated.IsShadowPass = sourcePipeline.IsShadowPass;
            }
        }
        catch
        {
            dedicated = new DefaultRenderPipeline(stereo: false);
        }

        _openXrNonStereoPipelineOverride = dedicated;

        if (sourcePipeline is DefaultRenderPipeline srcDp && srcDp.Stereo)
            Debug.Out("OpenXR: source pipeline is Stereo=true; using dedicated non-stereo pipeline for per-eye rendering.");
        else
            Debug.Out("OpenXR: using dedicated per-eye pipeline (separate from desktop pipeline instance).");

        return _openXrNonStereoPipelineOverride;
    }

    // Frame lifecycle is split across the engine threads:
    // - Render thread: PollEvents + EndFrame (previous) + WaitFrame/BeginFrame/LocateViews (next)
    // - CollectVisible thread: CollectVisible (build per-eye command buffers)
    // - CollectVisible thread: SwapBuffers (sync point; publishes buffers to render thread)
    // - Render thread: Acquire/Wait/Render/Release swapchain images + EndFrame
    // NOTE: Do not mark these as 'volatile' because we pass them by 'ref' to Volatile/Interlocked APIs,
    // and C# does not treat 'ref volatile-field' as volatile (CS0420). Use Volatile.Read/Write and Interlocked instead.
    private int _framePrepared;
    private int _frameSkipRender;
    private bool _timerHooksInstalled;

    // Pending OpenXR frame (WaitFrame/BeginFrame done; views located) awaiting engine CollectVisible+SwapBuffers.
    private int _pendingXrFrame;
    private int _pendingXrFrameCollected;

    private int _openXrPendingFrameNumber;
    private int _openXrLifecycleFrameIndex;

    private long _openXrPrepareTimestamp;
    private long _openXrCollectTimestamp;
    private long _openXrSwapTimestamp;

    private int _openXrDebugFrameIndex;
    private const int OpenXrDebugLogEveryNFrames = 60;

    // Debug toggles (keep as consts so they're unmissable and zero-overhead when off)
    private const bool OpenXrDebugGl = true;
    private const bool OpenXrDebugClearOnly = false;
    private const bool OpenXrDebugLifecycle = true;
    private const bool OpenXrDebugRenderRightThenLeft = true;

    private static bool ShouldLogLifecycle(int frameNumber)
        => frameNumber == 1 || (frameNumber % OpenXrDebugLogEveryNFrames) == 0;

    private static double MsSince(long startTimestamp)
    {
        if (startTimestamp == 0)
            return -1;
        long dt = Stopwatch.GetTimestamp() - startTimestamp;
        return (double)dt * 1000.0 / Stopwatch.Frequency;
    }

    private nint _openXrSessionHdc;
    private nint _openXrSessionHglrc;
    private string _openXrSessionGlBindingTag = string.Empty;

    #endregion

    #region Mirror blit (desktop viewport)

    private XRTexture2D? _viewportMirrorColor;
    private XRRenderBuffer? _viewportMirrorDepth;
    private XRFrameBuffer? _viewportMirrorFbo;
    private uint _viewportMirrorWidth;
    private uint _viewportMirrorHeight;

    private uint _blitReadFbo;
    private uint _blitDrawFbo;

    private nint _blitFboHglrc;

    #endregion

    #region Public API + window binding

    /// <summary>
    /// Gets the OpenXR API instance.
    /// </summary>
    public XR Api { get; private set; }

    /// <summary>
    /// Gets or sets the window associated with this XR session.
    /// Setting a new window triggers initialization or cleanup as appropriate.
    /// </summary>
    public XRWindow? Window
    {
        get => _window;
        set => SetField(ref _window, value);
    }

    /// <summary>
    /// Called before a property changes to perform any necessary cleanup.
    /// </summary>
    /// <typeparam name="T">The type of the property.</typeparam>
    /// <param name="propName">The name of the property.</param>
    /// <param name="field">The current value of the property.</param>
    /// <param name="new">The new value of the property.</param>
    /// <returns>Whether the property change should proceed.</returns>
    protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
    {
        bool change = base.OnPropertyChanging(propName, field, @new);
        if (change)
        {
            switch (propName)
            {
                case nameof(Window):
                    if (field is not null)
                        CleanUp();
                    break;
            }
        }
        return change;
    }

    /// <summary>
    /// Called after a property changes to perform any necessary initialization.
    /// </summary>
    /// <typeparam name="T">The type of the property.</typeparam>
    /// <param name="propName">The name of the property.</param>
    /// <param name="prev">The previous value of the property.</param>
    /// <param name="field">The new value of the property.</param>
    protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
    {
        base.OnPropertyChanged(propName, prev, field);
        switch (propName)
        {
            case nameof(Window):
                if (field is not null)
                    Initialize();
                break;
        }
    }

    /// <summary>
    /// Creates an OpenXR session using Vulkan graphics binding.
    /// </summary>
    /// <exception cref="Exception">Thrown when session creation fails.</exception>
    private void CreateVulkanSession()
    {
        if (Window is null)
            throw new Exception("Window is null");

        var requirements = new GraphicsRequirementsVulkanKHR
        {
            Type = StructureType.GraphicsRequirementsVulkanKhr
        };

        if (!Api.TryGetInstanceExtension<KhrVulkanEnable>("", _instance, out var vulkanExtension))
            throw new Exception("Failed to get Vulkan extension");

        if (vulkanExtension.GetVulkanGraphicsRequirements(_instance, _systemId, ref requirements) != Result.Success)
            throw new Exception("Failed to get Vulkan graphics requirements");

        Debug.Out($"Vulkan requirements: Min {requirements.MinApiVersionSupported}, Max {requirements.MaxApiVersionSupported}");

        if (Window.Renderer is not VulkanRenderer renderer)
            throw new Exception("Renderer is not a VulkanRenderer.");

        // Get the primary graphics queue family index
        var graphicsFamilyIndex = renderer.FamilyQueueIndices.GraphicsFamilyIndex!.Value;

        // Check if multiple graphics queues are supported
        bool supportsMultiQueue = renderer.SupportsMultipleGraphicsQueues();
        if (supportsMultiQueue)
        {
            Debug.Out("Multiple graphics queues are supported - enabling parallel eye rendering");
            _parallelRenderingEnabled = true;

            // Store secondary queue for right eye rendering
            // Note: This assumes VulkanRenderer has been modified to expose this functionality
            //_secondaryQueue = renderer.GetSecondaryGraphicsQueue();
        }
        else
        {
            Debug.Out("Multiple graphics queues not supported - using single queue rendering");
            _parallelRenderingEnabled = false;
        }

        var vkBinding = new GraphicsBindingVulkanKHR
        {
            Type = StructureType.GraphicsBindingVulkanKhr,
            Instance = new(renderer.Instance.Handle),
            PhysicalDevice = new(renderer.PhysicalDevice.Handle),
            Device = new(renderer.Device.Handle),
            QueueFamilyIndex = graphicsFamilyIndex,
            QueueIndex = 0 // Main queue for session
        };
        var createInfo = new SessionCreateInfo
        {
            Type = StructureType.SessionCreateInfo,
            SystemId = _systemId,
            Next = &vkBinding
        };
        var result = Api.CreateSession(_instance, ref createInfo, ref _session);
        if (result != Result.Success)
            throw new Exception($"Failed to create session: {result}");
    }

    /// <summary>
    /// Flag indicating if parallel rendering is enabled
    /// </summary>
    private bool _parallelRenderingEnabled = false;

    /// <summary>
    /// Secondary queue for right eye rendering when parallel rendering is enabled
    /// </summary>
    private object? _secondaryQueue = null;

    /// <summary>
    /// Renders a frame for both eyes in the XR device.
    /// This implementation supports parallel rendering of eyes when available.
    /// </summary>
    /// <param name="renderCallback">Callback function to render content to each eye's texture.</param>
    public void RenderFrame(DelRenderToFBO? renderCallback)
    {
        // Render thread: only submit if the CollectVisible thread prepared a frame.
        if (!_sessionBegun)
            return;

        if (Interlocked.Exchange(ref _framePrepared, 0) == 0)
            return;

        int frameNo = Volatile.Read(ref _openXrPendingFrameNumber);
        if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
        {
            double msSinceLocate = MsSince(_openXrPrepareTimestamp);
            double msSinceCollect = MsSince(_openXrCollectTimestamp);
            double msSinceSwap = MsSince(_openXrSwapTimestamp);
            Debug.Out($"OpenXR[{frameNo}] Render: begin viewCount={_viewCount} skipRender={Volatile.Read(ref _frameSkipRender)} " +
                      $"dt(Locate={msSinceLocate:F1}ms Collect={msSinceCollect:F1}ms Swap={msSinceSwap:F1}ms)");
        }

        if (Volatile.Read(ref _frameSkipRender) != 0)
        {
            var frameEndInfoNoLayers = new FrameEndInfo
            {
                Type = StructureType.FrameEndInfo,
                DisplayTime = _frameState.PredictedDisplayTime,
                EnvironmentBlendMode = EnvironmentBlendMode.Opaque,
                LayerCount = 0,
                Layers = null
            };
            var endResult = Api.EndFrame(_session, in frameEndInfoNoLayers);
            if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
                Debug.Out($"OpenXR[{frameNo}] Render: EndFrame(no layers) => {endResult}");

            Volatile.Write(ref _pendingXrFrame, 0);
            Volatile.Write(ref _pendingXrFrameCollected, 0);
            return;
        }

        var projectionViews = stackalloc CompositionLayerProjectionView[(int)_viewCount];
        for (uint i = 0; i < _viewCount; i++)
            projectionViews[i] = default;

        renderCallback ??= RenderViewportsToSwapchain;

        bool allEyesRendered = true;
        // NOTE: OpenXR swapchain acquire/wait/release is safest when done serially.
        // Parallelizing via Task.Run can break GL context ownership and runtime expectations.
        if (OpenXrDebugRenderRightThenLeft)
        {
            for (int i = (int)_viewCount - 1; i >= 0; i--)
                allEyesRendered &= RenderEye((uint)i, renderCallback, projectionViews);
        }
        else
        {
            for (uint i = 0; i < _viewCount; i++)
                allEyesRendered &= RenderEye(i, renderCallback, projectionViews);
        }

        if (!allEyesRendered)
        {
            var frameEndInfoNoLayers = new FrameEndInfo
            {
                Type = StructureType.FrameEndInfo,
                DisplayTime = _frameState.PredictedDisplayTime,
                EnvironmentBlendMode = EnvironmentBlendMode.Opaque,
                LayerCount = 0,
                Layers = null
            };
            var endResult = Api.EndFrame(_session, in frameEndInfoNoLayers);
            if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
                Debug.Out($"OpenXR[{frameNo}] Render: EndFrame(no layers; eye failure) => {endResult}");

            Volatile.Write(ref _pendingXrFrame, 0);
            Volatile.Write(ref _pendingXrFrameCollected, 0);
            return;
        }

        var layer = new CompositionLayerProjection
        {
            Type = StructureType.CompositionLayerProjection,
            Next = null,
            LayerFlags = 0,
            Space = _appSpace,
            ViewCount = _viewCount,
            Views = projectionViews
        };

        var layers = stackalloc CompositionLayerBaseHeader*[1];
        layers[0] = (CompositionLayerBaseHeader*)&layer;
        var frameEndInfo = new FrameEndInfo
        {
            Type = StructureType.FrameEndInfo,
            DisplayTime = _frameState.PredictedDisplayTime,
            EnvironmentBlendMode = EnvironmentBlendMode.Opaque,
            LayerCount = 1,
            Layers = layers
        };

        var endFrameResult = Api.EndFrame(_session, in frameEndInfo);
        if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
            Debug.Out($"OpenXR[{frameNo}] Render: EndFrame(layer) => {endFrameResult}");

        Volatile.Write(ref _pendingXrFrame, 0);
        Volatile.Write(ref _pendingXrFrameCollected, 0);
    }

    /// <summary>
    /// Renders a single eye (view)
    /// </summary>
    private bool RenderEye(uint viewIndex, DelRenderToFBO renderCallback, CompositionLayerProjectionView* projectionViews)
    {
        uint imageIndex = 0;
        var acquireInfo = new SwapchainImageAcquireInfo
        {
            Type = StructureType.SwapchainImageAcquireInfo
        };

        bool acquired = false;
        int frameNo = Volatile.Read(ref _openXrPendingFrameNumber);
        try
        {
            var acquireResult = Api.AcquireSwapchainImage(_swapchains[viewIndex], in acquireInfo, ref imageIndex);
            if (acquireResult != Result.Success)
                return false;
            acquired = true;

            if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
                Debug.Out($"OpenXR[{frameNo}] Eye{viewIndex}: Acquire => {acquireResult} imageIndex={imageIndex}");

        // Wait for image ready
        var waitInfo = new SwapchainImageWaitInfo
        {
            Type = StructureType.SwapchainImageWaitInfo,
            // OpenXR timeouts are in nanoseconds. Use XR_INFINITE_DURATION (int64 max)
            // to avoid leaking an acquired image on timeout or stalling frame submission.
            Timeout = long.MaxValue
        };

            var waitResult = Api.WaitSwapchainImage(_swapchains[viewIndex], in waitInfo);
            if (waitResult != Result.Success)
                return false;

            if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
                Debug.Out($"OpenXR[{frameNo}] Eye{viewIndex}: Wait => {waitResult}");

            // Render to the texture (OpenGL path only)
            if (_gl is not null)
            {
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _swapchainFramebuffers[viewIndex][imageIndex]);
                _gl.Viewport(0, 0, _viewConfigViews[viewIndex].RecommendedImageRectWidth, _viewConfigViews[viewIndex].RecommendedImageRectHeight);

                // Guard against GL state leakage between eyes (scissor/read buffers/masks are commonly left in a bad state
                // by some passes and can make the second eye appear fully black).
                _gl.Disable(EnableCap.ScissorTest);
                _gl.ColorMask(true, true, true, true);
                _gl.DepthMask(true);

                renderCallback(_swapchainImagesGL[viewIndex][imageIndex].Image, viewIndex);

                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

                // Ensure GPU commands touching the swapchain image are submitted before releasing it.
                _gl.Flush();
            }

            // Setup projection view (only if we successfully acquired+waited the swapchain image).
            projectionViews[viewIndex] = default;
            projectionViews[viewIndex].Type = StructureType.CompositionLayerProjectionView;
            projectionViews[viewIndex].Next = null;
            projectionViews[viewIndex].Fov = _views[viewIndex].Fov;
            projectionViews[viewIndex].Pose = _views[viewIndex].Pose;
            projectionViews[viewIndex].SubImage.Swapchain = _swapchains[viewIndex];
            projectionViews[viewIndex].SubImage.ImageArrayIndex = 0;
            projectionViews[viewIndex].SubImage.ImageRect = new Rect2Di
            {
                Offset = new Offset2Di { X = 0, Y = 0 },
                Extent = new Extent2Di
                {
                    Width = (int)_viewConfigViews[viewIndex].RecommendedImageRectWidth,
                    Height = (int)_viewConfigViews[viewIndex].RecommendedImageRectHeight
                }
            };

            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"OpenXR RenderEye({viewIndex}) failed: {ex.Message}");
            return false;
        }
        finally
        {
            // Always release if we acquired; otherwise the runtime can eventually stall and/or the driver can hang.
            if (acquired)
            {
                var releaseInfo = new SwapchainImageReleaseInfo { Type = StructureType.SwapchainImageReleaseInfo };
                var releaseResult = Api.ReleaseSwapchainImage(_swapchains[viewIndex], in releaseInfo);
                if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
                    Debug.Out($"OpenXR[{frameNo}] Eye{viewIndex}: Release => {releaseResult}");
            }
        }
    }

    /// <summary>
    /// Renders both eyes in parallel using multiple graphics queues
    /// </summary>
    private void RenderEyesInParallel(DelRenderToFBO renderCallback, CompositionLayerProjectionView* projectionViews)
    {
        // Disabled: OpenXR swapchain acquire/wait/release and GL rendering generally must be serialized.
        // True parallel eye rendering should be implemented inside the graphics backend (e.g., Vulkan multi-queue)
        // while keeping xr* calls on one thread.
        for (uint i = 0; i < _viewCount; i++)
            RenderEye(i, renderCallback, projectionViews);
    }

    /// <summary>
    /// Initializes Vulkan swapchains for stereo rendering
    /// </summary>
    private unsafe void InitializeVulkanSwapchains(VulkanRenderer renderer)
    {
        // Get view configuration
        var viewConfigType = ViewConfigurationType.PrimaryStereo;
        _viewCount = 0;
        Api.EnumerateViewConfigurationView(_instance, _systemId, viewConfigType, 0, ref _viewCount, null);

        if (_viewCount != 2)
        {
            throw new Exception($"Expected 2 views, got {_viewCount}");
        }

        _views = new View[_viewCount];

        fixed (ViewConfigurationView* viewConfigViewsPtr = _viewConfigViews)
        {
            Api.EnumerateViewConfigurationView(_instance, _systemId, viewConfigType, _viewCount, ref _viewCount, viewConfigViewsPtr);
        }

        // Create swapchains for each view
        for (int i = 0; i < _viewCount; i++)
        {
            var swapchainCreateInfo = new SwapchainCreateInfo
            {
                Type = StructureType.SwapchainCreateInfo,
                UsageFlags = SwapchainUsageFlags.ColorAttachmentBit,
                Format = 37 /* VK_FORMAT_R8G8B8A8_SRGB */,
                SampleCount = 1,
                Width = (uint)_viewConfigViews[i].RecommendedImageRectWidth,
                Height = (uint)_viewConfigViews[i].RecommendedImageRectHeight,
                FaceCount = 1,
                ArraySize = 1,
                MipCount = 1
            };

            fixed (Swapchain* swapchainPtr = &_swapchains[i])
            {
                if (Api.CreateSwapchain(_session, in swapchainCreateInfo, swapchainPtr) != Result.Success)
                {
                    throw new Exception($"Failed to create swapchain for view {i}");
                }
            }

            // Get swapchain images
            uint imageCount = 0;
            Api.EnumerateSwapchainImages(_swapchains[i], 0, &imageCount, null);

            _swapchainImagesVK[i] = (SwapchainImageVulkan2KHR*)Marshal.AllocHGlobal((int)imageCount * sizeof(SwapchainImageVulkan2KHR));

            for (uint j = 0; j < imageCount; j++)
                _swapchainImagesVK[i][j].Type = StructureType.SwapchainImageVulkan2Khr;

            Api.EnumerateSwapchainImages(_swapchains[i], imageCount, &imageCount, (SwapchainImageBaseHeader*)_swapchainImagesVK[i]);

            Console.WriteLine($"Created swapchain {i} with {imageCount} images ({swapchainCreateInfo.Width}x{swapchainCreateInfo.Height})");
        }
    }
    /// <summary>
    /// Initializes the OpenXR session and associated resources.
    /// </summary>
    protected void Initialize()
    {
        CreateInstance();
        SetupDebugMessenger();
        CreateSystem();
        switch (Window?.Renderer)
        {
            case OpenGLRenderer renderer:
                // OpenGL session creation must happen on the same thread that owns the current GL context.
                // Attempting to MakeCurrent here can fail if the editor render thread is already using it.
                if (_deferredOpenGlInit is not null)
                    Window.RenderViewportsCallback -= _deferredOpenGlInit;

                _deferredOpenGlInit = () =>
                {
                    if (Window is null)
                        return;

                    // Run once.
                    Window.RenderViewportsCallback -= _deferredOpenGlInit;
                    _deferredOpenGlInit = null;

                    CreateOpenGLSession(renderer);
                    CreateReferenceSpace();
                    InitializeOpenGLSwapchains(renderer);
                    HookEngineTimerEvents();
                    Window.RenderViewportsCallback += Window_RenderViewportsCallback;
                };

                Window.RenderViewportsCallback += _deferredOpenGlInit;
                break;
            case VulkanRenderer renderer:
                CreateVulkanSession();
                CreateReferenceSpace();
                InitializeVulkanSwapchains(renderer);
                HookEngineTimerEvents();
                Window.RenderViewportsCallback += Window_RenderViewportsCallback;
                break;
            //case D3D12Renderer renderer:
            //    throw new NotImplementedException("DirectX 12 renderer not implemented");
            default:
                throw new Exception("Unsupported renderer");
        }
    }

    /// <summary>
    /// Render-thread callback that advances OpenXR state, submits the prepared frame (if any),
    /// then prepares the next frame's timing/views for the CollectVisible thread.
    /// </summary>
    private void Window_RenderViewportsCallback()
    {
        // Do NOT force a context switch here.
        // If we accidentally switch into a different (non-sharing) WGL context, engine-owned textures will become
        // invalid on this thread ("<texture> does not refer to an existing texture object"), which then cascades
        // into incomplete FBOs and black output.
        // The windowing layer should already have the correct render context current when invoking this callback.
        if (_gl is not null && OpenXrDebugGl)
        {
            nint hdcCurrent = wglGetCurrentDC();
            nint hglrcCurrent = wglGetCurrentContext();
            int dbg = Interlocked.Increment(ref _openXrDebugFrameIndex);
            if (dbg == 1 || (dbg % OpenXrDebugLogEveryNFrames) == 0)
            {
                Debug.Out(
                    $"OpenXR render thread WGL: current(HDC=0x{(nuint)hdcCurrent:X}, HGLRC=0x{(nuint)hglrcCurrent:X}) " +
                    $"session({_openXrSessionGlBindingTag}; HDC=0x{(nuint)_openXrSessionHdc:X}, HGLRC=0x{(nuint)_openXrSessionHglrc:X})");
            }
        }
        // Keep OpenXR event/state progression on the render thread.
        PollEvents();

        // Match OpenVR timing: allow the engine to update any VR/locomotion transforms right before rendering.
        // (OpenXR runs its own render callback path, so we need to invoke the same hook here.)
        Engine.VRState.InvokeRecalcMatrixOnDraw();

        if (!_sessionBegun)
            return;

        // Render the frame whose visibility buffers were published by the CollectVisible thread.
        RenderFrame(null);

        // After submitting the current frame (if any), prepare the next frame's timing + views.
        PrepareNextFrameOnRenderThread();
    }

    private void HookEngineTimerEvents()
    {
        if (_timerHooksInstalled)
            return;

        Engine.Time.Timer.CollectVisible += OpenXrCollectVisible;
        Engine.Time.Timer.SwapBuffers += OpenXrSwapBuffers;
        _timerHooksInstalled = true;
    }

    private void UnhookEngineTimerEvents()
    {
        if (!_timerHooksInstalled)
            return;

        Engine.Time.Timer.CollectVisible -= OpenXrCollectVisible;
        Engine.Time.Timer.SwapBuffers -= OpenXrSwapBuffers;
        _timerHooksInstalled = false;
    }

    private XRViewport? TryGetSourceViewport()
    {
        if (Window is null)
            return null;

        foreach (var vp in Window.Viewports)
        {
            if (vp is null)
                continue;
            if (vp.World is null)
                continue;
            if (vp.ActiveCamera is null)
                continue;
            return vp;
        }

        return null;
    }

    /// <summary>
    /// CollectVisible-thread callback.
    /// Builds per-eye visibility buffers for the OpenXR views prepared on the render thread.
    /// </summary>
    private void OpenXrCollectVisible()
    {
        // Runs on the engine's CollectVisible thread.
        // Consumes the views located on the render thread and builds per-eye visibility buffers.
        if (!_sessionBegun)
            return;

        if (Volatile.Read(ref _pendingXrFrame) == 0)
            return;

        // Avoid double-collecting if the engine calls this multiple times before SwapBuffers.
        // 0 = not started, 2 = in progress, 1 = done
        if (Interlocked.CompareExchange(ref _pendingXrFrameCollected, 2, 0) != 0)
            return;

        if (Volatile.Read(ref _frameSkipRender) != 0)
            return;

        bool success = false;
        try
        {
            int frameNo = Volatile.Read(ref _openXrPendingFrameNumber);
            if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
            {
                double msSinceLocate = MsSince(_openXrPrepareTimestamp);
                Debug.Out($"OpenXR[{frameNo}] CollectVisible: begin dt(Locate={msSinceLocate:F1}ms)");
            }

            _openXrCollectTimestamp = Stopwatch.GetTimestamp();

            var sourceViewport = TryGetSourceViewport();
            if (sourceViewport?.World is null || sourceViewport.ActiveCamera is null)
                return;

            // Prefer the VRState-driven world/cameras when available so OpenXR behaves like OpenVR (custom transforms, locomotion root, etc).
            var vrInfo = Engine.VRState.ViewInformation;
            _openXrFrameWorld = vrInfo.World ?? sourceViewport.World;
            _openXrFrameBaseCamera = sourceViewport.ActiveCamera;

            if (vrInfo.LeftEyeCamera is not null)
                _openXrLeftEyeCamera = vrInfo.LeftEyeCamera;
            if (vrInfo.RightEyeCamera is not null)
                _openXrRightEyeCamera = vrInfo.RightEyeCamera;

            // Best-effort locomotion root: HMD node's parent is typically the playspace/locomotion root.
            _openXrLocomotionRoot = vrInfo.HMDNode?.Transform.Parent ?? _openXrFrameBaseCamera.Transform.Parent;

            EnsureOpenXrEyeCameras(_openXrFrameBaseCamera);
            EnsureOpenXrViewports(
                _viewConfigViews[0].RecommendedImageRectWidth,
                _viewConfigViews[0].RecommendedImageRectHeight);

            // Match the scene/editor pipeline so lighting/post/etc are consistent.
            // However, if the source pipeline is single-pass stereo (DefaultRenderPipeline.Stereo=true),
            // per-eye OpenXR rendering must force a non-stereo pipeline to avoid left-eye-only deferred/post output.
            var sourcePipeline = sourceViewport.RenderPipeline;
            var desiredPipeline = SelectOpenXrRenderPipeline(sourcePipeline);
            if (desiredPipeline is not null)
            {
                if (!ReferenceEquals(_openXrLeftViewport!.RenderPipeline, desiredPipeline))
                    _openXrLeftViewport.RenderPipeline = desiredPipeline;
                if (!ReferenceEquals(_openXrRightViewport!.RenderPipeline, desiredPipeline))
                    _openXrRightViewport.RenderPipeline = desiredPipeline;

                // Keep camera post-process/pipeline state aligned with what we're actually executing.
                _openXrLeftEyeCamera!.RenderPipeline = desiredPipeline;
                _openXrRightEyeCamera!.RenderPipeline = desiredPipeline;

                // Always inherit the base camera's post-process values/material.
                // Otherwise the per-eye cameras keep default stage values which can easily yield black lighting
                // and makes exposure/tonemapping behave differently from the desktop view.
                var postSourcePipeline = sourcePipeline ?? desiredPipeline;
                if (postSourcePipeline is not null)
                {
                    CopyPostProcessState(postSourcePipeline, desiredPipeline, _openXrFrameBaseCamera!, _openXrLeftEyeCamera!);
                    CopyPostProcessState(postSourcePipeline, desiredPipeline, _openXrFrameBaseCamera!, _openXrRightEyeCamera!);
                }
            }

            UpdateOpenXrEyeCameraFromView(_openXrLeftEyeCamera!, 0);
            UpdateOpenXrEyeCameraFromView(_openXrRightEyeCamera!, 1);

            int leftAdded = 0;
            int rightAdded = 0;

            // Parallel buffer generation is only enabled on the Vulkan path.
            if (_parallelRenderingEnabled && Window?.Renderer is VulkanRenderer)
            {
                Task leftTask = Task.Run(() =>
                {
                    _openXrLeftViewport!.CollectVisible(
                        collectMirrors: true,
                        worldOverride: _openXrFrameWorld,
                        cameraOverride: _openXrLeftEyeCamera,
                        allowScreenSpaceUICollectVisible: false);
                    leftAdded = _openXrLeftViewport.RenderPipelineInstance.MeshRenderCommands.GetCommandsAddedCount();
                });

                Task rightTask = Task.Run(() =>
                {
                    _openXrRightViewport!.CollectVisible(
                        collectMirrors: true,
                        worldOverride: _openXrFrameWorld,
                        cameraOverride: _openXrRightEyeCamera,
                        allowScreenSpaceUICollectVisible: false);
                    rightAdded = _openXrRightViewport.RenderPipelineInstance.MeshRenderCommands.GetCommandsAddedCount();
                });

                Task.WaitAll(leftTask, rightTask);
            }
            else
            {
                _openXrLeftViewport!.CollectVisible(
                    collectMirrors: true,
                    worldOverride: _openXrFrameWorld,
                    cameraOverride: _openXrLeftEyeCamera,
                    allowScreenSpaceUICollectVisible: false);
                leftAdded = _openXrLeftViewport.RenderPipelineInstance.MeshRenderCommands.GetCommandsAddedCount();

                _openXrRightViewport!.CollectVisible(
                    collectMirrors: true,
                    worldOverride: _openXrFrameWorld,
                    cameraOverride: _openXrRightEyeCamera,
                    allowScreenSpaceUICollectVisible: false);
                rightAdded = _openXrRightViewport.RenderPipelineInstance.MeshRenderCommands.GetCommandsAddedCount();
            }

            int dbg = Interlocked.Increment(ref _openXrDebugFrameIndex);
            if (dbg == 1 || (dbg % OpenXrDebugLogEveryNFrames) == 0)
            {
                Debug.Out($"OpenXR CollectVisible: leftAdded={leftAdded}, rightAdded={rightAdded}, CullWithFrustum(L/R)={_openXrLeftViewport!.CullWithFrustum}/{_openXrRightViewport!.CullWithFrustum}");
            }

            if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
            {
                double msSinceLocate = MsSince(_openXrPrepareTimestamp);
                double msSinceCollect = MsSince(_openXrCollectTimestamp);
                Debug.Out($"OpenXR[{frameNo}] CollectVisible: done leftAdded={leftAdded} rightAdded={rightAdded} dt(Locate={msSinceLocate:F1}ms Collect={msSinceCollect:F1}ms)");
            }

            success = true;
        }
        finally
        {
            Volatile.Write(ref _pendingXrFrameCollected, success ? 1 : 0);
        }
    }

    /// <summary>
    /// CollectVisible-thread callback.
    /// Publishes the per-eye buffers to the render thread (sync point between CollectVisible and rendering).
    /// </summary>
    private void OpenXrSwapBuffers()
    {
        // Runs on the engine's CollectVisible thread, after the previous render completes.
        // Acts as the sync point between CollectVisible (buffer generation) and the render thread.
        if (!_sessionBegun)
            return;

        if (Volatile.Read(ref _pendingXrFrame) == 0)
            return;

        // If the runtime says "do not render", just let the render thread EndFrame with no layers.
        if (Volatile.Read(ref _frameSkipRender) != 0)
        {
            Interlocked.Exchange(ref _framePrepared, 1);
            return;
        }

        // If we didn't successfully collect this frame, don't try to render stale buffers.
        if (Volatile.Read(ref _pendingXrFrameCollected) != 1)
            return;

        int frameNo = Volatile.Read(ref _openXrPendingFrameNumber);
        if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
        {
            double msSinceLocate = MsSince(_openXrPrepareTimestamp);
            double msSinceCollect = MsSince(_openXrCollectTimestamp);
            Debug.Out($"OpenXR[{frameNo}] SwapBuffers: publishing eye buffers dt(Locate={msSinceLocate:F1}ms Collect={msSinceCollect:F1}ms)");
        }

        _openXrSwapTimestamp = Stopwatch.GetTimestamp();

        _openXrLeftViewport?.SwapBuffers(allowScreenSpaceUISwap: false);
        _openXrRightViewport?.SwapBuffers(allowScreenSpaceUISwap: false);

        Interlocked.Exchange(ref _framePrepared, 1);

        if (OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo))
            Debug.Out($"OpenXR[{frameNo}] SwapBuffers: framePrepared=1");
    }

    /// <summary>
    /// Render-thread helper.
    /// Waits/begins an OpenXR frame and locates views, making that frame available for CollectVisible.
    /// </summary>
    private void PrepareNextFrameOnRenderThread()
    {
        // Called on the render thread. Prepares the next OpenXR frame (WaitFrame/BeginFrame/LocateViews)
        // so the CollectVisible thread can build buffers for it.
        if (!_sessionBegun)
            return;

        // Only one OpenXR frame can be "in flight" between BeginFrame and EndFrame.
        if (Volatile.Read(ref _pendingXrFrame) != 0)
            return;

        // Clear any stale publish flags.
        Volatile.Write(ref _framePrepared, 0);
        Volatile.Write(ref _pendingXrFrameCollected, 0);

        if (!WaitFrame(out _frameState))
            return;

        if (!BeginFrame())
            return;

        int frameNo = Interlocked.Increment(ref _openXrLifecycleFrameIndex);
        Volatile.Write(ref _openXrPendingFrameNumber, frameNo);

        if (OpenXrDebugLifecycle && ShouldLogLifecycle(frameNo))
        {
            Debug.Out($"OpenXR[{frameNo}] Prepare: Wait+Begin ok predictedDisplayTime={_frameState.PredictedDisplayTime} shouldRender={_frameState.ShouldRender}");
        }

        if (_frameState.ShouldRender == 0)
        {
            Volatile.Write(ref _frameSkipRender, 1);
            Volatile.Write(ref _pendingXrFrame, 1);

            if (OpenXrDebugLifecycle && ShouldLogLifecycle(frameNo))
                Debug.Out($"OpenXR[{frameNo}] Prepare: ShouldRender=0 (will EndFrame with no layers)");
            return;
        }

        Volatile.Write(ref _frameSkipRender, 0);

        if (!LocateViews())
            return;

        _openXrPrepareTimestamp = Stopwatch.GetTimestamp();

        if (OpenXrDebugLifecycle && ShouldLogLifecycle(frameNo) && _views.Length >= 2)
        {
            var l = _views[0];
            var r = _views[1];
            Debug.Out(
                $"OpenXR[{frameNo}] Prepare: LocateViews ok " +
                $"L(pos={l.Pose.Position.X:F3},{l.Pose.Position.Y:F3},{l.Pose.Position.Z:F3}) " +
                $"R(pos={r.Pose.Position.X:F3},{r.Pose.Position.Y:F3},{r.Pose.Position.Z:F3})");
        }

        Volatile.Write(ref _pendingXrFrame, 1);
    }

    /// <summary>
    /// Creates an OpenXR system for the specified form factor.
    /// </summary>
    private void CreateSystem()
    {
        var systemGetInfo = new SystemGetInfo
        {
            Type = StructureType.SystemGetInfo,
            FormFactor = FormFactor.HeadMountedDisplay
        };
        var result = Api.GetSystem(_instance, in systemGetInfo, ref _systemId);
        if (result != Result.Success)
        {
            throw new Exception($"Failed to get system: {result}");
        }
    }

    private void CreateReferenceSpace()
    {
        var spaceCreateInfo = new ReferenceSpaceCreateInfo
        {
            Type = StructureType.ReferenceSpaceCreateInfo,
            ReferenceSpaceType = ReferenceSpaceType.Local,
            PoseInReferenceSpace = new Posef
            {
                Orientation = new Quaternionf { X = 0, Y = 0, Z = 0, W = 1 },
                Position = new Vector3f { X = 0, Y = 0, Z = 0 }
            }
        };

        Space space = default;
        if (Api.CreateReferenceSpace(_session, in spaceCreateInfo, ref space) != Result.Success)
            throw new Exception("Failed to create reference space");

        _appSpace = space;
    }

    ///// <summary>
    ///// Creates an OpenXR session using Vulkan graphics binding.
    ///// </summary>
    ///// <exception cref="Exception">Thrown when session creation fails.</exception>
    //private void CreateVulkanSession()
    //{
    //    if (Window is null)
    //        throw new Exception("Window is null");

    //    var requirements = new GraphicsRequirementsVulkanKHR
    //    {
    //        Type = StructureType.GraphicsRequirementsVulkanKhr
    //    };

    //    if (!Api.TryGetInstanceExtension<KhrVulkanEnable>("", instance, out var vulkanExtension))
    //        throw new Exception("Failed to get Vulkan extension");

    //    if (vulkanExtension.GetVulkanGraphicsRequirements(instance, _systemId, ref requirements) != Result.Success)
    //        throw new Exception("Failed to get Vulkan graphics requirements");

    //    Debug.Out($"Vulkan requirements: Min {requirements.MinApiVersionSupported}, Max {requirements.MaxApiVersionSupported}");

    //    if (Window.Renderer is not VulkanRenderer renderer)
    //        throw new Exception("Renderer is not a VulkanRenderer.");

    //    var graphicsFamilyIndex = renderer.FamilyQueueIndices.GraphicsFamilyIndex!.Value;

    //    var vkBinding = new GraphicsBindingVulkanKHR
    //    {
    //        Type = StructureType.GraphicsBindingVulkanKhr,
    //        Instance = new(renderer.Instance.Handle),
    //        PhysicalDevice = new(renderer.PhysicalDevice.Handle),
    //        Device = new(renderer.Device.Handle),
    //        QueueFamilyIndex = graphicsFamilyIndex,
    //        QueueIndex = 0
    //    };
    //    var createInfo = new SessionCreateInfo
    //    {
    //        Type = StructureType.SessionCreateInfo,
    //        SystemId = _systemId,
    //        Next = &vkBinding
    //    };
    //    var result = Api.CreateSession(instance, ref createInfo, ref _session);
    //    if (result != Result.Success)
    //        throw new Exception($"Failed to create session: {result}");
    //}

    /// <summary>
    /// Creates an OpenXR session using OpenGL graphics binding.
    /// </summary>
    /// <exception cref="Exception">Thrown when session creation fails.</exception>
    private void CreateOpenGLSession(OpenGLRenderer renderer)
    {
        if (Window is null)
            throw new Exception("Window is null");

        _gl = renderer.RawGL;

        // OpenXR OpenGL session creation requires the HGLRC/HDC to be current on the calling thread.
        // This method is expected to run on the window render thread (see deferred init in Initialize()).
        var w = Window.Window;

        // Ensure the window context is current on this thread.
        // (Calling MakeCurrent from the wrong thread can throw; here we're on the render callback thread.)
        try
        {
            w.MakeCurrent();
        }
        catch (Exception ex)
        {
            Debug.Out($"OpenGL MakeCurrent failed (continuing): {ex.Message}");
        }

        try
        {
            string glVersion = new((sbyte*)_gl.GetString(StringName.Version));
            string glVendor = new((sbyte*)_gl.GetString(StringName.Vendor));
            string glRenderer = new((sbyte*)_gl.GetString(StringName.Renderer));
            Debug.Out($"OpenGL context: {glVendor} / {glRenderer} / {glVersion}");
        }
        catch
        {
            // If the context isn't current/valid, querying strings can throw; the CreateSession call will fail anyway.
        }

        var requirements = new GraphicsRequirementsOpenGLKHR
        {
            Type = StructureType.GraphicsRequirementsOpenglKhr
        };

        if (!Api.TryGetInstanceExtension<KhrOpenglEnable>("", _instance, out var openglExtension))
            throw new Exception("Failed to get OpenGL extension");

        if (openglExtension.GetOpenGlgraphicsRequirements(_instance, _systemId, ref requirements) != Result.Success)
            throw new Exception("Failed to get OpenGL graphics requirements");

        Debug.Out($"OpenGL requirements: Min {requirements.MinApiVersionSupported}, Max {requirements.MaxApiVersionSupported}");

        int glMajor = 0;
        int glMinor = 0;
        try
        {
            glMajor = _gl.GetInteger(GetPName.MajorVersion);
            glMinor = _gl.GetInteger(GetPName.MinorVersion);
        }
        catch
        {
            // Ignore; we'll still try to create the session and report handles.
        }

        nint hdcFromWindow = w.Native?.Win32?.HDC ?? 0;
        nint hglrcFromWindow = w.GLContext?.Handle ?? 0;
        nint hdcCurrent = wglGetCurrentDC();
        nint hglrcCurrent = wglGetCurrentContext();

        Debug.Out($"OpenGL binding (window): HDC=0x{(nuint)hdcFromWindow:X}, HGLRC=0x{(nuint)hglrcFromWindow:X}");
        Debug.Out($"OpenGL binding (current): HDC=0x{(nuint)hdcCurrent:X}, HGLRC=0x{(nuint)hglrcCurrent:X}");

        if ((hglrcCurrent == 0 || hdcCurrent == 0) && (hglrcFromWindow == 0 || hdcFromWindow == 0))
            throw new Exception("Cannot create OpenXR session: no valid OpenGL handles available (both current and window handles are null). Ensure OpenXR OpenGL session creation runs on the window render thread and the GL context is created.");

        // Some runtimes are picky about which exact handles they accept. We'll attempt session creation using both
        // the current WGL handles and the window-reported handles (if different), and report both results.
        (nint hdc, nint hglrc, string tag)[] candidates =
        [
            (hdcCurrent, hglrcCurrent, "current"),
            (hdcFromWindow, hglrcFromWindow, "window"),
        ];

        var attemptResults = new List<string>(2);
        Result lastResult = Result.Success;
        nint selectedHdc = 0;
        nint selectedHglrc = 0;
        string selectedTag = string.Empty;

        // Validate GL version against runtime requirements if we can decode versions.
        try
        {
            static (ushort major, ushort minor, uint patch) DecodeVersion(ulong v)
            {
                ulong raw = v;
                ushort major = (ushort)((raw >> 48) & 0xFFFF);
                ushort minor = (ushort)((raw >> 32) & 0xFFFF);
                uint patch = (uint)(raw & 0xFFFFFFFF);
                return (major, minor, patch);
            }

            var (minMajor, minMinor, _) = DecodeVersion(requirements.MinApiVersionSupported);
            var (maxMajor, maxMinor, _) = DecodeVersion(requirements.MaxApiVersionSupported);

            bool hasGLVersion = glMajor > 0;
            bool hasMax = maxMajor != 0 || maxMinor != 0;

            if (hasGLVersion)
            {
                bool belowMin = glMajor < minMajor || (glMajor == minMajor && glMinor < minMinor);
                bool aboveMax = hasMax && (glMajor > maxMajor || (glMajor == maxMajor && glMinor > maxMinor));
                if (belowMin || aboveMax)
                {
                    throw new Exception(
                        $"Cannot create OpenXR session: current OpenGL version {glMajor}.{glMinor} is outside runtime requirements " +
                        $"[{minMajor}.{minMinor} .. {(hasMax ? $"{maxMajor}.{maxMinor}" : "(no max)")}].");
                }
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"OpenXR OpenGL preflight failed: {ex.Message}");
        }

        foreach (var (candidateHdc, candidateHglrc, tag) in candidates)
        {
            if (candidateHdc == 0 || candidateHglrc == 0)
                continue;

            // Skip duplicate handle pairs.
            if (selectedHdc == candidateHdc && selectedHglrc == candidateHglrc)
                continue;

            _session = default;

            var glBinding = new GraphicsBindingOpenGLWin32KHR
            {
                Type = StructureType.GraphicsBindingOpenglWin32Khr,
                HDC = candidateHdc,
                HGlrc = candidateHglrc
            };
            var createInfo = new SessionCreateInfo
            {
                Type = StructureType.SessionCreateInfo,
                SystemId = _systemId,
                Next = &glBinding
            };

            var r = Api.CreateSession(_instance, ref createInfo, ref _session);
            attemptResults.Add($"{tag}: {r} (HDC=0x{(nuint)candidateHdc:X}, HGLRC=0x{(nuint)candidateHglrc:X})");
            lastResult = r;
            if (r == Result.Success)
            {
                selectedHdc = candidateHdc;
                selectedHglrc = candidateHglrc;
                selectedTag = tag;
                break;
            }
        }

        if (_session.Handle == 0)
        {
            string activeRuntime = TryGetOpenXRActiveRuntime() ?? "<unknown>";
            throw new Exception(
                $"Failed to create OpenXR session: {lastResult}. GL={glMajor}.{glMinor}. ActiveRuntime={activeRuntime}. " +
                $"Attempts: {string.Join("; ", attemptResults)}. " +
                "SteamVR commonly has limited/fragile OpenGL OpenXR support; Vulkan is usually more reliable.");
        }

        _openXrSessionHdc = selectedHdc;
        _openXrSessionHglrc = selectedHglrc;
        _openXrSessionGlBindingTag = selectedTag;
        Debug.Out($"OpenXR session created using {selectedTag} OpenGL handles. HDC=0x{(nuint)selectedHdc:X}, HGLRC=0x{(nuint)selectedHglrc:X}");
    }

    /// <summary>
    /// Current state of the OpenXR session.
    /// </summary>
    private SessionState _sessionState = SessionState.Unknown;

    /// <summary>
    /// Configuration information for each view (eye).
    /// </summary>
    private readonly ViewConfigurationView[] _viewConfigViews = new ViewConfigurationView[2];

    /// <summary>
    /// Swapchain handles for each view.
    /// </summary>
    private readonly Swapchain[] _swapchains = new Swapchain[2];

    /// <summary>
    /// OpenGL swapchain image pointers for each view.
    /// </summary>
    private readonly SwapchainImageOpenGLKHR*[] _swapchainImagesGL = new SwapchainImageOpenGLKHR*[2];

    /// <summary>
    /// OpenGL framebuffer handles for each swapchain image.
    /// </summary>
    private readonly uint[][] _swapchainFramebuffers = new uint[2][];

    /// <summary>
    /// Number of swapchain images per view.
    /// </summary>
    private readonly uint[] _swapchainImageCounts = new uint[2];

    /// <summary>
    /// Vulkan swapchain image pointers for each view.
    /// </summary>
    private readonly SwapchainImageVulkan2KHR*[] _swapchainImagesVK = new SwapchainImageVulkan2KHR*[2];

    /// <summary>
    /// DirectX swapchain image pointers for each view.
    /// </summary>
    private readonly SwapchainImageD3D12KHR*[] _swapchainImagesDX = new SwapchainImageD3D12KHR*[2];

    /// <summary>
    /// Initializes OpenGL swapchains for stereo rendering.
    /// </summary>
    /// <param name="renderer">The OpenGL renderer to use.</param>
    /// <exception cref="Exception">Thrown when swapchain creation fails.</exception>
    private unsafe void InitializeOpenGLSwapchains(OpenGLRenderer renderer)
    {
        if (_gl is null)
            throw new Exception("OpenGL context not initialized for OpenXR");

        // Query supported swapchain formats for the active OpenXR runtime (for OpenGL these are GL internal format enums).
        uint formatCount = 0;
        var formatResult = Api.EnumerateSwapchainFormats(_session, 0, ref formatCount, null);
        if (formatResult != Result.Success || formatCount == 0)
            throw new Exception($"Failed to enumerate OpenXR swapchain formats for OpenGL. Result={formatResult}, Count={formatCount}");

        var formats = new long[formatCount];
        fixed (long* formatsPtr = formats)
        {
            formatResult = Api.EnumerateSwapchainFormats(_session, formatCount, ref formatCount, formatsPtr);
        }
        if (formatResult != Result.Success || formatCount == 0)
            throw new Exception($"Failed to enumerate OpenXR swapchain formats for OpenGL. Result={formatResult}, Count={formatCount}");

        static IEnumerable<long> GetPreferredFormats(long[] available)
        {
            // Prefer sRGB when available, fall back to linear RGBA8.
            long[] preferred =
            [
                (long)GLEnum.Srgb8Alpha8,
                (long)GLEnum.Rgba8,
            ];

            foreach (var pref in preferred)
                if (available.Contains(pref))
                    yield return pref;

            foreach (var f in available)
                if (!preferred.Contains(f))
                    yield return f;
        }

        var supportedFormatsLog = string.Join(", ", formats.Select(f => $"0x{f:X}"));
        Debug.Out($"OpenXR OpenGL supported swapchain formats: {supportedFormatsLog}");

        // Get view configuration
        var viewConfigType = ViewConfigurationType.PrimaryStereo;
        _viewCount = 0;
        Api.EnumerateViewConfigurationView(_instance, _systemId, viewConfigType, 0, ref _viewCount, null);

        if (_viewCount != 2)
            throw new Exception($"Expected 2 views, got {_viewCount}");

        _views = new View[_viewCount];
        for (int i = 0; i < _views.Length; i++)
            _views[i].Type = StructureType.View;

        // OpenXR requires the input structs to have their Type set.
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
            Debug.Out($"OpenXR view[{i}] recommended size: {rw}x{rh}, samples={_viewConfigViews[i].RecommendedSwapchainSampleCount}");

            if (rw == 0 || rh == 0)
                throw new Exception($"OpenXR runtime reported an invalid recommended image rect size for view {i}: {rw}x{rh}. Cannot create swapchains.");
        }

        // Create swapchains for each view
        for (int i = 0; i < _viewCount; i++)
        {
            uint width = (uint)_viewConfigViews[i].RecommendedImageRectWidth;
            uint height = (uint)_viewConfigViews[i].RecommendedImageRectHeight;
            uint recommendedSamples = _viewConfigViews[i].RecommendedSwapchainSampleCount;

            Result lastResult = Result.Success;
            bool created = false;

            foreach (var format in GetPreferredFormats(formats))
            {
                foreach (var usage in new[] { SwapchainUsageFlags.ColorAttachmentBit | SwapchainUsageFlags.SampledBit, SwapchainUsageFlags.ColorAttachmentBit })
                {
                    foreach (var samples in recommendedSamples > 1 ? [recommendedSamples, 1u] : new[] { 1u })
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
                            Debug.Out($"OpenXR swapchain[{i}] created. Format=0x{format:X}, Samples={samples}, Usage={usage}, Size={width}x{height}");
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
                throw new Exception($"Failed to create swapchain for view {i}. LastResult={lastResult}, RecommendedSamples={recommendedSamples}, Size={width}x{height}, SupportedFormats={supportedFormatsLog}");

            // Get swapchain images
            uint imageCount = 0;
            Api.EnumerateSwapchainImages(_swapchains[i], 0, &imageCount, null);

            _swapchainImagesGL[i] = (SwapchainImageOpenGLKHR*)Marshal.AllocHGlobal((int)imageCount * sizeof(SwapchainImageOpenGLKHR));

            _swapchainImageCounts[i] = imageCount;
            _swapchainFramebuffers[i] = new uint[imageCount];

            for (uint j = 0; j < imageCount; j++)
                _swapchainImagesGL[i][j].Type = StructureType.SwapchainImageOpenglKhr;

            Api.EnumerateSwapchainImages(_swapchains[i], imageCount, &imageCount, (SwapchainImageBaseHeader*)_swapchainImagesGL[i]);

            for (uint j = 0; j < imageCount; j++)
            {
                uint fbo = _gl.GenFramebuffer();
                _gl.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
                // Attach without assuming the underlying texture target (2D vs 2DMS etc).
                _gl.FramebufferTexture(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, _swapchainImagesGL[i][j].Image, 0);

                // Make the swapchain FBO robust against global ReadBuffer/DrawBuffers state changes.
                // Some engine passes intentionally set ReadBuffer=None; if that leaks, subsequent operations can become no-ops.
                unsafe
                {
                    GLEnum* drawBuffers = stackalloc GLEnum[1] { GLEnum.ColorAttachment0 };
                    _gl.NamedFramebufferDrawBuffers(fbo, 1, drawBuffers);
                    _gl.NamedFramebufferReadBuffer(fbo, GLEnum.ColorAttachment0);
                }
                _swapchainFramebuffers[i][j] = fbo;
            }

            Console.WriteLine($"Created swapchain {i} with {imageCount} images ({width}x{height})");
        }
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    /// <summary>
    /// Delegate for rendering to a framebuffer texture.
    /// </summary>
    /// <param name="textureHandle">OpenGL texture handle to render to.</param>
    /// <param name="viewIndex">Index of the view (eye) being rendered.</param>
    public delegate void DelRenderToFBO(uint textureHandle, uint viewIndex);

    private void RenderViewportsToSwapchain(uint textureHandle, uint viewIndex)
    {
        if (Window is null)
            return;

        if (_openXrFrameWorld is null)
            return;

        if (_openXrLeftViewport is null || _openXrRightViewport is null)
            return;

        if (_openXrLeftEyeCamera is null || _openXrRightEyeCamera is null)
            return;

        if (Window.Renderer is OpenGLRenderer renderer)
        {
            if (_gl is null)
                return;

            int frameNo = Volatile.Read(ref _openXrPendingFrameNumber);
            bool logLifecycle = OpenXrDebugLifecycle && frameNo != 0 && ShouldLogLifecycle(frameNo);

            static string DetectTextureTarget(GL gl, uint tex)
            {
                // Heuristic: try binding the texture name to common targets and see which one succeeds.
                // (Binding to an incompatible target yields GL_INVALID_OPERATION.)
                gl.GetError();
                gl.BindTexture(TextureTarget.Texture2D, tex);
                var e2d = gl.GetError();
                if (e2d == GLEnum.NoError)
                    return "Texture2D";

                gl.GetError();
                gl.BindTexture(TextureTarget.Texture2DArray, tex);
                var e2da = gl.GetError();
                if (e2da == GLEnum.NoError)
                    return "Texture2DArray";
                gl.GetError();
                gl.BindTexture(TextureTarget.Texture2DMultisample, tex);
                var e2dms = gl.GetError();
                if (e2dms == GLEnum.NoError)
                    return "Texture2DMultisample";

                return $"Unknown(err2D={e2d}, err2DA={e2da}, err2DMS={e2dms})";
            }

            // Diagnostic: prove swapchain rendering/submission works (and swapchain texture names are valid in this context).
            // If this shows solid colors in the HMD, the issue is in mirror rendering or blit source, not OpenXR submission.
            if (OpenXrDebugClearOnly)
            {
                if (viewIndex == 0)
                    _gl.ClearColor(1f, 0f, 0f, 1f);
                else
                    _gl.ClearColor(0f, 1f, 0f, 1f);
                _gl.Clear(ClearBufferMask.ColorBufferBit);
                return;
            }

            uint width = _viewConfigViews[viewIndex].RecommendedImageRectWidth;
            uint height = _viewConfigViews[viewIndex].RecommendedImageRectHeight;
            EnsureViewportMirrorTargets(renderer, width, height);

            var eyeViewport = viewIndex == 0 ? _openXrLeftViewport : _openXrRightViewport;
            var eyeCamera = viewIndex == 0 ? _openXrLeftEyeCamera : _openXrRightEyeCamera;

            var previous = AbstractRenderer.Current;
            try
            {
                renderer.Active = true;
                AbstractRenderer.Current = renderer;

                // Make sure the eye pose reflects the latest locomotion-root rotation for *this* render.
                ApplyOpenXrEyePoseForRenderThread(viewIndex);

                // CollectVisible/SwapBuffers are handled on the engine's CollectVisible thread.
                eyeViewport.Render(_viewportMirrorFbo, _openXrFrameWorld, eyeCamera, shadowPass: false, forcedMaterial: null);

                var srcApiTex = renderer.GetOrCreateAPIRenderObject(_viewportMirrorColor, generateNow: true) as IGLTexture;
                if (srcApiTex is null || srcApiTex.BindingId == 0)
                    return;

                if (logLifecycle)
                {
                    string srcTarget = DetectTextureTarget(_gl, srcApiTex.BindingId);
                    string dstTarget = DetectTextureTarget(_gl, textureHandle);
                    Debug.Out($"OpenXR[{frameNo}] GLBlit: view={viewIndex} srcTex={srcApiTex.BindingId}({srcTarget}) dstTex={textureHandle}({dstTarget}) size={width}x{height}");
                }

                if (OpenXrDebugGl)
                {
                    bool srcIsTex = _gl.IsTexture(srcApiTex.BindingId);
                    bool dstIsTex = _gl.IsTexture(textureHandle);
                    int dbg = Interlocked.Increment(ref _openXrDebugFrameIndex);
                    if (dbg == 1 || (dbg % OpenXrDebugLogEveryNFrames) == 0)
                    {
                        Debug.Out($"OpenXR GL: view={viewIndex} srcTex={srcApiTex.BindingId} valid={srcIsTex} dstTex={textureHandle} valid={dstIsTex}");
                    }
                }

                // These utility FBOs must be created in (and used with) the current GL context.
                // Some runtimes/drivers use a distinct context for OpenXR rendering; reusing cached FBO ids from a
                // different context will trigger GL_INVALID_OPERATION and result in black output.
                var hglrcCurrent = wglGetCurrentContext();
                if (_blitFboHglrc != 0 && _blitFboHglrc != hglrcCurrent)
                {
                    _blitReadFbo = 0;
                    _blitDrawFbo = 0;
                }
                _blitFboHglrc = hglrcCurrent;

                if (_blitReadFbo == 0)
                    _blitReadFbo = _gl.GenFramebuffer();
                if (_blitDrawFbo == 0)
                    _blitDrawFbo = _gl.GenFramebuffer();

                // Blit can be clipped by scissor/masks if left enabled by previous passes.
                _gl.Disable(EnableCap.ScissorTest);
                _gl.ColorMask(true, true, true, true);

                _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, _blitReadFbo);
                // Some engine passes intentionally set ReadBuffer=None; if that leaks, blits can become no-ops.
                _gl.ReadBuffer(GLEnum.ColorAttachment0);
                // Attach without assuming the underlying texture target (2D vs 2DMS etc).
                _gl.FramebufferTexture(FramebufferTarget.ReadFramebuffer, FramebufferAttachment.ColorAttachment0, srcApiTex.BindingId, 0);

                _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _blitDrawFbo);
                unsafe
                {
                    GLEnum* drawBuffers = stackalloc GLEnum[1] { GLEnum.ColorAttachment0 };
                    _gl.DrawBuffers(1, drawBuffers);
                }
                // Attach without assuming the underlying texture target (2D vs 2DMS etc).
                _gl.FramebufferTexture(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, textureHandle, 0);

                if (OpenXrDebugGl)
                {
                    var readStatus = _gl.CheckFramebufferStatus(FramebufferTarget.ReadFramebuffer);
                    var drawStatus = _gl.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer);
                    var err = _gl.GetError();
                    int dbg = Interlocked.Increment(ref _openXrDebugFrameIndex);
                    if (dbg == 1 || (dbg % OpenXrDebugLogEveryNFrames) == 0)
                    {
                        Debug.Out($"OpenXR GL: FBO status read={readStatus} draw={drawStatus} glGetError={err}");
                    }
                }

                if (logLifecycle)
                {
                    var readStatus = _gl.CheckFramebufferStatus(FramebufferTarget.ReadFramebuffer);
                    var drawStatus = _gl.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer);
                    var err = _gl.GetError();
                    Debug.Out($"OpenXR[{frameNo}] GLBlit: view={viewIndex} FBO read={readStatus} draw={drawStatus} glErr={err}");
                }

                _gl.BlitFramebuffer(
                    0, 0, (int)width, (int)height,
                    0, 0, (int)width, (int)height,
                    ClearBufferMask.ColorBufferBit,
                    BlitFramebufferFilter.Linear);

                if (logLifecycle)
                {
                    var err = _gl.GetError();
                    Debug.Out($"OpenXR[{frameNo}] GLBlit: view={viewIndex} post-blit glErr={err}");
                }

                if (OpenXrDebugGl)
                {
                    var err = _gl.GetError();
                    int dbg = Interlocked.Increment(ref _openXrDebugFrameIndex);
                    if (dbg == 1 || (dbg % OpenXrDebugLogEveryNFrames) == 0)
                    {
                        Debug.Out($"OpenXR GL: post-blit glGetError={err}");
                    }
                }

                // Restore to a neutral state (RenderEye will re-bind what it needs).
                _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, 0);
                _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, 0);
            }
            finally
            {
                renderer.Active = false;
                AbstractRenderer.Current = previous;
            }
        }
    }

    private void EnsureOpenXrViewport(uint width, uint height)
    {
        // Kept for compatibility with older call sites; prefer per-eye viewports.
        EnsureOpenXrViewports(width, height);
    }

    private void EnsureOpenXrViewports(uint width, uint height)
    {
        _openXrLeftViewport ??= new XRViewport(Window)
        {
            AutomaticallyCollectVisible = false,
            AutomaticallySwapBuffers = false,
            AllowUIRender = false,
            SetRenderPipelineFromCamera = false
        };
        _openXrRightViewport ??= new XRViewport(Window)
        {
            AutomaticallyCollectVisible = false,
            AutomaticallySwapBuffers = false,
            AllowUIRender = false,
            SetRenderPipelineFromCamera = false
        };

        _openXrLeftViewport.Camera = _openXrLeftEyeCamera;
        _openXrRightViewport.Camera = _openXrRightEyeCamera;

        // Diagnostic default: disable frustum culling while OpenXR is still being validated.
        // If this makes the world appear, the remaining issue is almost certainly frustum/projection/pose conversion.
        _openXrLeftViewport.CullWithFrustum = false;
        _openXrRightViewport.CullWithFrustum = false;

        // Keep them independent of editor viewport layout.
        _openXrLeftViewport.SetFullScreen();
        _openXrRightViewport.SetFullScreen();

        // Ensure pipeline sizes track our swapchain size, but keep internal resolution exact.
        if (_openXrLeftViewport.Width != (int)width || _openXrLeftViewport.Height != (int)height)
        {
            _openXrLeftViewport.Resize(width, height, setInternalResolution: false);
            _openXrLeftViewport.SetInternalResolution((int)width, (int)height, correctAspect: false);
        }
        else if (_openXrLeftViewport.InternalWidth != (int)width || _openXrLeftViewport.InternalHeight != (int)height)
        {
            _openXrLeftViewport.SetInternalResolution((int)width, (int)height, correctAspect: false);
        }

        if (_openXrRightViewport.Width != (int)width || _openXrRightViewport.Height != (int)height)
        {
            _openXrRightViewport.Resize(width, height, setInternalResolution: false);
            _openXrRightViewport.SetInternalResolution((int)width, (int)height, correctAspect: false);
        }
        else if (_openXrRightViewport.InternalWidth != (int)width || _openXrRightViewport.InternalHeight != (int)height)
        {
            _openXrRightViewport.SetInternalResolution((int)width, (int)height, correctAspect: false);
        }
    }

    private void EnsureOpenXrEyeCameras(XRCamera baseCamera)
    {
        // Prefer Engine.VRState cameras if provided (OpenVR-style setup).
        var vrInfo = Engine.VRState.ViewInformation;
        _openXrLeftEyeCamera ??= vrInfo.LeftEyeCamera ?? new XRCamera(new Transform());
        _openXrRightEyeCamera ??= vrInfo.RightEyeCamera ?? new XRCamera(new Transform());

        // Match the OpenVR + locomotion setup: eye transforms live under the locomotion root.
        var locomotionRoot = _openXrLocomotionRoot ?? baseCamera.Transform.Parent;
        if (locomotionRoot is not null)
        {
            // Only reparent if the camera is currently unparented or parented to the base camera;
            // preserve custom hierarchies when the app provides its own VR rig.
            if (_openXrLeftEyeCamera.Transform.Parent is null || ReferenceEquals(_openXrLeftEyeCamera.Transform.Parent, baseCamera.Transform))
                _openXrLeftEyeCamera.Transform.SetParent(locomotionRoot, preserveWorldTransform: false, EParentAssignmentMode.Immediate);
            if (_openXrRightEyeCamera.Transform.Parent is null || ReferenceEquals(_openXrRightEyeCamera.Transform.Parent, baseCamera.Transform))
                _openXrRightEyeCamera.Transform.SetParent(locomotionRoot, preserveWorldTransform: false, EParentAssignmentMode.Immediate);
        }

        CopyCameraCommon(baseCamera, _openXrLeftEyeCamera);
        CopyCameraCommon(baseCamera, _openXrRightEyeCamera);
    }

    private static void CopyCameraCommon(XRCamera src, XRCamera dst)
    {
        dst.CullingMask = src.CullingMask;
        dst.ShadowCollectMaxDistance = src.ShadowCollectMaxDistance;
        dst.RenderPipeline = src.RenderPipeline;

        float nearZ = src.Parameters.NearZ;
        float farZ = src.Parameters.FarZ;

        if (dst.Parameters is not XROpenXRFovCameraParameters openxrParams)
        {
            openxrParams = new XROpenXRFovCameraParameters(nearZ, farZ);
            dst.Parameters = openxrParams;
        }
        else
        {
            openxrParams.NearZ = nearZ;
            openxrParams.FarZ = farZ;
        }
    }

    private void UpdateOpenXrEyeCameraFromView(XRCamera camera, uint viewIndex)
    {
        var pose = _views[viewIndex].Pose;
        var fov = _views[viewIndex].Fov;

        // OpenXR way: render the world directly from the per-eye view pose returned by xrLocateViews
        // in the same reference space we submit in the projection layer (layer.Space == _appSpace).
        // This keeps the rendered images consistent with the submitted projectionViews[*].Pose and
        // avoids timewarp/reprojection artifacts from pose-space mismatches.
        Vector3 eyePos = new(pose.Position.X, pose.Position.Y, pose.Position.Z);
        Quaternion eyeRot = Quaternion.Normalize(new Quaternion(
            pose.Orientation.X,
            pose.Orientation.Y,
            pose.Orientation.Z,
            pose.Orientation.W));

        Matrix4x4 eyeLocalMatrix = Matrix4x4.CreateFromQuaternion(eyeRot);
        eyeLocalMatrix.Translation = eyePos;

        // Store the local pose so the render thread can compose it with the *current* locomotion root render matrix.
        lock (_openXrEyePoseLock)
        {
            if (viewIndex == 0)
                _openXrLeftEyeLocalPose = eyeLocalMatrix;
            else if (viewIndex == 1)
                _openXrRightEyeLocalPose = eyeLocalMatrix;
        }

        // Keep the transform components in sync (useful for tools/debug/UI and any world-matrix consumers).
        camera.Transform.DeriveLocalMatrix(eyeLocalMatrix, networkSmoothed: false);

        if (camera.Parameters is XROpenXRFovCameraParameters openxrParams)
            openxrParams.SetAngles(fov.AngleLeft, fov.AngleRight, fov.AngleUp, fov.AngleDown);
    }

    private void ApplyOpenXrEyePoseForRenderThread(uint viewIndex)
    {
        var camera = viewIndex == 0 ? _openXrLeftEyeCamera : _openXrRightEyeCamera;
        if (camera is null)
            return;

        Matrix4x4 localPose;
        lock (_openXrEyePoseLock)
            localPose = viewIndex == 0 ? _openXrLeftEyeLocalPose : _openXrRightEyeLocalPose;

        var root = _openXrLocomotionRoot;
        Matrix4x4 rootRender = root?.RenderMatrix ?? Matrix4x4.Identity;
        Matrix4x4 eyeRender = localPose * rootRender;

        // Apply composed render matrix so rapid locomotion-root rotations can't temporarily snap the eye.
        camera.Transform.SetRenderMatrix(eyeRender, recalcAllChildRenderMatrices: false);
    }

    private void EnsureViewportMirrorTargets(OpenGLRenderer renderer, uint width, uint height)
    {
        width = Math.Max(1u, width);
        height = Math.Max(1u, height);

        if (_viewportMirrorFbo is not null && _viewportMirrorWidth == width && _viewportMirrorHeight == height)
            return;

        try
        {
            _viewportMirrorFbo?.Destroy();
            _viewportMirrorFbo = null;
            _viewportMirrorDepth?.Destroy();
            _viewportMirrorDepth = null;
            _viewportMirrorColor?.Destroy();
            _viewportMirrorColor = null;
        }
        catch
        {
            // Best-effort cleanup.
        }

        _viewportMirrorWidth = width;
        _viewportMirrorHeight = height;

        _viewportMirrorColor = XRTexture2D.CreateFrameBufferTexture(
            width,
            height,
            EPixelInternalFormat.Rgba8,
            EPixelFormat.Rgba,
            EPixelType.UnsignedByte,
            EFrameBufferAttachment.ColorAttachment0);
        _viewportMirrorColor.Resizable = true;
        _viewportMirrorColor.MinFilter = ETexMinFilter.Linear;
        _viewportMirrorColor.MagFilter = ETexMagFilter.Linear;
        _viewportMirrorColor.UWrap = ETexWrapMode.ClampToEdge;
        _viewportMirrorColor.VWrap = ETexWrapMode.ClampToEdge;
        _viewportMirrorColor.Name = "OpenXRViewportMirrorColor";

        _viewportMirrorDepth = new XRRenderBuffer(width, height, ERenderBufferStorage.Depth24Stencil8, EFrameBufferAttachment.DepthStencilAttachment)
        {
            Name = "OpenXRViewportMirrorDepth"
        };

        _viewportMirrorFbo = new XRFrameBuffer(
            (_viewportMirrorColor, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (_viewportMirrorDepth, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            Name = "OpenXRViewportMirrorFBO"
        };

        // Ensure GPU objects are created on this renderer/context.
        renderer.GetOrCreateAPIRenderObject(_viewportMirrorColor, generateNow: true);
        renderer.GetOrCreateAPIRenderObject(_viewportMirrorDepth, generateNow: true);
        renderer.GetOrCreateAPIRenderObject(_viewportMirrorFbo, generateNow: true);
    }

    ///// <summary>
    ///// Renders a frame for both eyes in the XR device.
    ///// </summary>
    ///// <param name="renderCallback">Callback function to render content to each eye's texture.</param>
    //public void RenderFrame(DelRenderToFBO renderCallback)
    //{
    //    PollEvents();

    //    if (_sessionState != SessionState.Focused)
    //        return;

    //    WaitFrame(out FrameState frameState);
    //    BeginFrame();

    //    var projectionViews = stackalloc CompositionLayerProjectionView[2];
    //    var layer = new CompositionLayerProjection
    //    {
    //        Type = StructureType.CompositionLayerProjection,
    //        Views = projectionViews
    //    };

    //    var layers = stackalloc CompositionLayerBaseHeader*[1];
    //    layers[0] = (CompositionLayerBaseHeader*)&layer;
    //    var frameEndInfo = new FrameEndInfo
    //    {
    //        Type = StructureType.FrameEndInfo,
    //        DisplayTime = frameState.PredictedDisplayTime,
    //        EnvironmentBlendMode = EnvironmentBlendMode.Opaque,
    //        LayerCount = 1,
    //        Layers = layers
    //    };

    //    for (uint i = 0; i < _viewCount; i++)
    //    {
    //        // Acquire swapchain image
    //        uint imageIndex = 0;
    //        Api.AcquireSwapchainImage(_swapchains[i], null, ref imageIndex);

    //        // Wait for image ready
    //        var waitInfo = new SwapchainImageWaitInfo
    //        {
    //            Type = StructureType.SwapchainImageWaitInfo,
    //            Timeout = 1000 // 1 second
    //        };

    //        if (Api.WaitSwapchainImage(_swapchains[i], in waitInfo) != Result.Success)
    //            continue;

    //        // Render to the texture
    //        renderCallback(_swapchainImagesGL[i][imageIndex].Image, i);

    //        // Release the image
    //        var releaseInfo = new SwapchainImageReleaseInfo
    //        {
    //            Type = StructureType.SwapchainImageReleaseInfo
    //        };
    //        Api.ReleaseSwapchainImage(_swapchains[i], in releaseInfo);

    //        // Setup projection view
    //        projectionViews[i].Type = StructureType.View;
    //        projectionViews[i].Fov = new Fovf
    //        {
    //            AngleLeft = -0.5f,
    //            AngleRight = 0.5f,
    //            AngleUp = 0.5f,
    //            AngleDown = -0.5f
    //        };
    //        // Set the pose to identity for now
    //        projectionViews[i].Pose = new Posef
    //        {
    //            Orientation = new Quaternionf
    //            {
    //                X = 0,
    //                Y = 0,
    //                Z = 0,
    //                W = 1
    //            },
    //            Position = new Vector3f
    //            {
    //                X = i * 0.1f,
    //                Y = 0,
    //                Z = 0
    //            }
    //        };
    //        // Set the swapchain image
    //        projectionViews[i].SubImage.Swapchain = _swapchains[i];
    //        projectionViews[i].SubImage.ImageRect = new Rect2Di
    //        {
    //            Offset = new Offset2Di
    //            {
    //                X = 0,
    //                Y = 0
    //            },
    //            Extent = new Extent2Di
    //            {
    //                Width = (int)_viewConfigViews[i].RecommendedImageRectWidth,
    //                Height = (int)_viewConfigViews[i].RecommendedImageRectHeight
    //            }
    //        };
    //    }

    //    Api.EndFrame(_session, in frameEndInfo);
    //}

    /// <summary>
    /// Begins an OpenXR frame.
    /// </summary>
    /// <returns>True if the frame was successfully begun, false otherwise.</returns>
    private bool BeginFrame()
    {
        var frameBeginInfo = new FrameBeginInfo { Type = StructureType.FrameBeginInfo };
        if (Api.BeginFrame(_session, in frameBeginInfo) != Result.Success)
        {
            Debug.LogWarning("Failed to begin OpenXR frame.");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Waits for the next frame timing from the OpenXR runtime.
    /// </summary>
    /// <param name="frameState">Returns the frame state information.</param>
    /// <returns>True if successfully waited for the frame, false otherwise.</returns>
    private bool WaitFrame(out FrameState frameState)
    {
        var frameWaitInfo = new FrameWaitInfo { Type = StructureType.FrameWaitInfo };
        frameState = new FrameState { Type = StructureType.FrameState };
        if (Api.WaitFrame(_session, in frameWaitInfo, ref frameState) != Result.Success)
        {
            Debug.LogWarning("Failed to wait for OpenXR frame.");
            return false;
        }
        _frameState = frameState;
        return true;
    }

    private bool LocateViews()
    {
        var viewLocateInfo = new ViewLocateInfo
        {
            Type = StructureType.ViewLocateInfo,
            DisplayTime = _frameState.PredictedDisplayTime,
            Space = _appSpace,
            ViewConfigurationType = ViewConfigurationType.PrimaryStereo
        };

        var viewState = new ViewState { Type = StructureType.ViewState };
        uint viewCountOutput = _viewCount;
        fixed (View* viewsPtr = _views)
        {
            var viewsSpan = new Span<View>(viewsPtr, (int)_viewCount);
            if (Api.LocateView(_session, &viewLocateInfo, &viewState, &viewCountOutput, viewsSpan) != Result.Success)
            {
                Debug.LogWarning("Failed to locate OpenXR views.");
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Polls for OpenXR events and handles them appropriately.
    /// </summary>
    private void PollEvents()
    {
        // OpenXR requires the input buffer's Type be set to EventDataBuffer.
        // The runtime then overwrites the same memory with the specific event struct.
        var eventData = new EventDataBuffer
        {
            Type = StructureType.EventDataBuffer,
            Next = null
        };

        while (true)
        {
            var result = Api.PollEvent(_instance, ref eventData);
            if (result == Result.EventUnavailable)
                break;
            if (result != Result.Success)
            {
                Debug.LogWarning($"xrPollEvent failed: {result}");
                break;
            }

            EventDataBuffer* eventDataPtr = &eventData;
            
            // The first field of every XrEventData* struct is StructureType.
            switch (eventData.Type)
            {
                case StructureType.EventDataSessionStateChanged:
                    {
                        var stateChanged = (EventDataSessionStateChanged*)eventDataPtr;
                        _sessionState = stateChanged->State;
                        Debug.Out($"Session state changed to: {_sessionState}");
                        if (_sessionState == SessionState.Ready)
                        {
                            var beginInfo = new SessionBeginInfo
                            {
                                Type = StructureType.SessionBeginInfo,
                                PrimaryViewConfigurationType = ViewConfigurationType.PrimaryStereo
                            };

                                var beginResult = Api.BeginSession(_session, in beginInfo);
                                Debug.Out($"xrBeginSession: {beginResult}");
                                if (beginResult == Result.Success)
                                {
                                    _sessionBegun = true;
                                    Debug.Out("Session began successfully");
                                }
                        }
                            else if (_sessionState == SessionState.Stopping)
                            {
                                var endResult = Api.EndSession(_session);
                                Debug.Out($"xrEndSession: {endResult}");
                                _sessionBegun = false;
                            }
                            else if (_sessionState == SessionState.Exiting || _sessionState == SessionState.LossPending)
                            {
                                _sessionBegun = false;
                            }
                    }
                    break;
                default:
                    Debug.Out(eventData.Type.ToString());
                    break;
            }

            // Reset the buffer type for the next poll (the runtime overwrites it with the event type).
            eventData.Type = StructureType.EventDataBuffer;
            eventData.Next = null;
        }
    }

    /// <summary>
    /// Cleans up OpenXR resources.
    /// </summary>
    protected void CleanUp()
    {
        if (Window is not null)
            Window.RenderViewportsCallback -= Window_RenderViewportsCallback;

        if (Window is not null && _deferredOpenGlInit is not null)
            Window.RenderViewportsCallback -= _deferredOpenGlInit;
        _deferredOpenGlInit = null;

        UnhookEngineTimerEvents();

        // Break viewport/camera links.
        if (_openXrLeftViewport is not null)
            _openXrLeftViewport.Camera = null;
        if (_openXrRightViewport is not null)
            _openXrRightViewport.Camera = null;

        // Cleanup swapchains
        for (int i = 0; i < _viewCount; i++)
        {
            if (_swapchainFramebuffers[i] is not null && _gl is not null)
                foreach (var fbo in _swapchainFramebuffers[i])
                    _gl.DeleteFramebuffer(fbo);

            if (_swapchainImagesGL[i] != null)
                Marshal.FreeHGlobal((nint)_swapchainImagesGL[i]);
            if (_swapchains[i].Handle != 0)
                Api.DestroySwapchain(_swapchains[i]);
        }

        if (_appSpace.Handle != 0)
            Api.DestroySpace(_appSpace);

        if (_session.Handle != 0)
            Api.DestroySession(_session);

        if (_gl is not null)
        {
            if (_blitReadFbo != 0)
            {
                _gl.DeleteFramebuffer(_blitReadFbo);
                _blitReadFbo = 0;
            }
            if (_blitDrawFbo != 0)
            {
                _gl.DeleteFramebuffer(_blitDrawFbo);
                _blitDrawFbo = 0;
            }
        }

        try
        {
            _viewportMirrorFbo?.Destroy();
            _viewportMirrorFbo = null;
            _viewportMirrorDepth?.Destroy();
            _viewportMirrorDepth = null;
            _viewportMirrorColor?.Destroy();
            _viewportMirrorColor = null;
        }
        catch
        {
            // Best-effort cleanup.
        }

        DestroyValidationLayers();
        DestroyInstance();
    }

    #endregion
}
