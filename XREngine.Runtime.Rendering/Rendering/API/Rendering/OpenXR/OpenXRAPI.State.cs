using Silk.NET.OpenGL;
using Silk.NET.OpenXR;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using XREngine.Data.Core;
using XREngine.Data.Rendering;
using XREngine.Input;
using XREngine.Rendering;
using XREngine.Rendering.Occlusion;
using XREngine.Rendering.PostProcessing;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    public const float OpenXrMinPoseTimeOffsetMs = -20.0f;
    public const float OpenXrMaxPoseTimeOffsetMs = 20.0f;

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
    private ViewConfigurationType _activeViewConfigurationType = ViewConfigurationType.PrimaryStereo;
    private ERvcFallbackReason _activeViewConfigurationFallbackReason = ERvcFallbackReason.None;
    private string _activeViewConfigurationDiagnostic = "Primary stereo view configuration selected.";
    private readonly RvcFrameViewDiagnostics[] _openXrRvcFrameViewDiagnostics = new RvcFrameViewDiagnostics[RenderFrameViewSet.MaxViewCount];
    private readonly RvcFrameViewProjectionDiagnostics[] _openXrRvcFrameViewProjectionDiagnostics = new RvcFrameViewProjectionDiagnostics[RenderFrameViewSet.MaxViewCount];
    private readonly Matrix4x4[] _openXrRvcPreviousViewProjectionMatrices = new Matrix4x4[RenderFrameViewSet.MaxViewCount];
    private readonly RvcOpenXrVisibilityMaskState[] _openXrRvcVisibilityMaskStates = new RvcOpenXrVisibilityMaskState[RenderFrameViewSet.MaxViewCount];
    private readonly Vector2[][] _openXrRvcHiddenAreaMaskVertices = new Vector2[RenderFrameViewSet.MaxViewCount][];
    private readonly uint[][] _openXrRvcHiddenAreaMaskIndices = new uint[RenderFrameViewSet.MaxViewCount][];
    private readonly Vector2[][] _openXrRvcVisibleAreaMaskVertices = new Vector2[RenderFrameViewSet.MaxViewCount][];
    private readonly uint[][] _openXrRvcVisibleAreaMaskIndices = new uint[RenderFrameViewSet.MaxViewCount][];
    private XrGetVisibilityMaskKhrDelegate? _xrGetVisibilityMaskKhr;
    private ulong _openXrRvcVisibilityMaskRevision;
    private uint _openXrRvcProfiledViewMask;

    private Space _appSpace;
    private View[] _views = new View[RenderFrameViewSet.MaxViewCount];
    private readonly View[] _openXrPredictedViews = new View[RenderFrameViewSet.MaxViewCount];
    private readonly View[] _openXrLateViews = new View[RenderFrameViewSet.MaxViewCount];
    private int _openXrPredictedViewCount;
    private int _openXrLateViewCount;
    private int _openXrPredictedViewFrameNumber;
    private int _openXrLateViewFrameNumber;
    private View[]? _lastValidViews;
    private int _hasLastValidViews;
    // Rate-limit flags for tracking-loss warnings: 0 = no streak logged yet, 1 = logged for this streak.
    // Cleared by CacheLastValidViews() once tracking recovers.
    private int _trackingLossStreakLogged;
    private int _freezeFallbackStreakLogged;
    private FrameState _frameState;
    private GL? _gl;
    private System.Action? _deferredOpenGlInit;

    private bool _sessionBegun;

    #endregion

    #region Engine camera + viewport integration

    private XRViewport? _openXrLeftViewport;
    private XRViewport? _openXrRightViewport;
    private XRViewport? _openXrStereoViewport;
    private XRCamera? _openXrLeftEyeCamera;
    private XRCamera? _openXrRightEyeCamera;
    private XRCamera? _openXrSubscribedLeftEyeSettingsCamera;
    private XRCamera? _openXrSubscribedRightEyeSettingsCamera;
    private XRCamera? _openXrEyeSettingsSourceCamera;
    private CameraPostProcessStateCollection? _openXrSharedEyePostProcessStates;
    private int _openXrSyncingEyeCameraSettings;

    private readonly object _openXrPoseLock = new();

    // Predicted poses are intended for the update/CollectVisible thread.
    private Matrix4x4 _openXrPredLeftEyeLocalPose = Matrix4x4.Identity;
    private Matrix4x4 _openXrPredRightEyeLocalPose = Matrix4x4.Identity;
    private Matrix4x4 _openXrPredHeadLocalPose = Matrix4x4.Identity;
    private (float Left, float Right, float Up, float Down) _openXrPredLeftEyeFov;
    private (float Left, float Right, float Up, float Down) _openXrPredRightEyeFov;

    // Late poses are sampled again right before rendering (late-update style).
    private Matrix4x4 _openXrLateLeftEyeLocalPose = Matrix4x4.Identity;
    private Matrix4x4 _openXrLateRightEyeLocalPose = Matrix4x4.Identity;
    private Matrix4x4 _openXrLateHeadLocalPose = Matrix4x4.Identity;
    private (float Left, float Right, float Up, float Down) _openXrLateLeftEyeFov;
    private (float Left, float Right, float Up, float Down) _openXrLateRightEyeFov;

    // Controller poses (local in app space).
    private Matrix4x4 _openXrPredLeftControllerLocalPose = Matrix4x4.Identity;
    private Matrix4x4 _openXrPredRightControllerLocalPose = Matrix4x4.Identity;
    private Matrix4x4 _openXrLateLeftControllerLocalPose = Matrix4x4.Identity;
    private Matrix4x4 _openXrLateRightControllerLocalPose = Matrix4x4.Identity;
    private int _openXrPredLeftControllerValid;
    private int _openXrPredRightControllerValid;
    private int _openXrLateLeftControllerValid;
    private int _openXrLateRightControllerValid;

    // Tracker poses keyed by OpenXR user path string (e.g. /user/vive_tracker_htcx/role/waist).
    private readonly System.Collections.Generic.Dictionary<string, Matrix4x4> _openXrPredTrackerLocalPose = new(StringComparer.Ordinal);
    private readonly System.Collections.Generic.Dictionary<string, Matrix4x4> _openXrLateTrackerLocalPose = new(StringComparer.Ordinal);
    private readonly System.Collections.Generic.HashSet<string> _openXrKnownTrackerPaths = new(StringComparer.Ordinal);
    private readonly System.Collections.Generic.Dictionary<string, RuntimeVrTrackerInfo> _openXrKnownTrackers = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns the latest predicted HMD pose in the app reference space (center-eye), as a local matrix.
    /// Updated from xrLocateViews and intended for VR-related transforms to pull during RecalcMatrixOnDraw.
    /// </summary>
    public bool TryGetHeadLocalPose(out Matrix4x4 localPose)
        => TryGetHeadLocalPose(OpenXrPoseTiming.Predicted, out localPose);

    public bool TryGetHeadLocalPose(OpenXrPoseTiming timing, out Matrix4x4 localPose)
    {
        lock (_openXrPoseLock)
            localPose = timing == OpenXrPoseTiming.Late ? _openXrLateHeadLocalPose : _openXrPredHeadLocalPose;
        return true;
    }

    /// <summary>
    /// Returns the latest predicted per-eye view pose in the app reference space, as a local matrix.
    /// </summary>
    public bool TryGetEyeLocalPose(bool leftEye, out Matrix4x4 localPose)
        => TryGetEyeLocalPose(OpenXrPoseTiming.Predicted, leftEye, out localPose);

    public bool TryGetEyeLocalPose(OpenXrPoseTiming timing, bool leftEye, out Matrix4x4 localPose)
    {
        lock (_openXrPoseLock)
        {
            if (timing == OpenXrPoseTiming.Late)
                localPose = leftEye ? _openXrLateLeftEyeLocalPose : _openXrLateRightEyeLocalPose;
            else
                localPose = leftEye ? _openXrPredLeftEyeLocalPose : _openXrPredRightEyeLocalPose;
        }
        return true;
    }

    /// <summary>
    /// Returns the latest predicted per-eye asymmetric FOV angles (radians), matching XrFovf.
    /// </summary>
    public bool TryGetEyeFovAngles(bool leftEye, out float angleLeft, out float angleRight, out float angleUp, out float angleDown)
        => TryGetEyeFovAngles(OpenXrPoseTiming.Predicted, leftEye, out angleLeft, out angleRight, out angleUp, out angleDown);

    public bool TryGetEyeFovAngles(OpenXrPoseTiming timing, bool leftEye, out float angleLeft, out float angleRight, out float angleUp, out float angleDown)
    {
        lock (_openXrPoseLock)
        {
            var f = timing == OpenXrPoseTiming.Late
                ? (leftEye ? _openXrLateLeftEyeFov : _openXrLateRightEyeFov)
                : (leftEye ? _openXrPredLeftEyeFov : _openXrPredRightEyeFov);
            angleLeft = f.Left;
            angleRight = f.Right;
            angleUp = f.Up;
            angleDown = f.Down;
        }
        return true;
    }

    public bool TryGetControllerLocalPose(bool leftHand, OpenXrPoseTiming timing, out Matrix4x4 localPose)
    {
        int valid = 0;
        lock (_openXrPoseLock)
        {
            if (timing == OpenXrPoseTiming.Late)
            {
                if (leftHand)
                {
                    localPose = _openXrLateLeftControllerLocalPose;
                    valid = _openXrLateLeftControllerValid;
                }
                else
                {
                    localPose = _openXrLateRightControllerLocalPose;
                    valid = _openXrLateRightControllerValid;
                }
            }
            else
            {
                if (leftHand)
                {
                    localPose = _openXrPredLeftControllerLocalPose;
                    valid = _openXrPredLeftControllerValid;
                }
                else
                {
                    localPose = _openXrPredRightControllerLocalPose;
                    valid = _openXrPredRightControllerValid;
                }
            }
        }
        return valid != 0;
    }

    public bool TryGetTrackerLocalPose(string trackerUserPath, OpenXrPoseTiming timing, out Matrix4x4 localPose)
    {
        lock (_openXrPoseLock)
        {
            var dict = timing == OpenXrPoseTiming.Late ? _openXrLateTrackerLocalPose : _openXrPredTrackerLocalPose;
            return dict.TryGetValue(trackerUserPath, out localPose);
        }
    }

    public string[] GetKnownTrackerUserPaths()
    {
        lock (_openXrPoseLock)
            return [.. _openXrKnownTrackerPaths];
    }

    public RuntimeVrTrackerInfo[] GetKnownTrackers()
    {
        lock (_openXrPoseLock)
            return [.. _openXrKnownTrackers.Values];
    }
    private TransformBase? _openXrLocomotionRoot;

    // OpenXR renders each eye in a separate pipeline execution (stereoPass=false).
    // IMPORTANT: The OpenXR eye viewports must NOT share the desktop viewport's RenderPipeline instance
    // or each other's RenderPipeline command-chain objects. The shared visibility buffer may be reused,
    // but render command objects can cache per-view state while lowering to backend frame ops.
    private RenderPipeline? _openXrLeftRenderPipeline;
    private RenderPipeline? _openXrRightRenderPipeline;
    private RenderPipeline? _openXrStereoRenderPipeline;
    private readonly Matrix4x4[] _openXrStereoCullProjections = new Matrix4x4[2];
    private readonly Matrix4x4[] _openXrStereoCullViews = new Matrix4x4[2];
    private Matrix4x4 _openXrCombinedProjectionMatrix = Matrix4x4.Identity;

    private IRuntimeRenderWorld? _openXrFrameWorld;
    private XRCamera? _openXrFrameBaseCamera;

    #endregion

    #region Frame lifecycle (thread handoff)

    // Frame lifecycle is split across the engine threads:
    // - Render thread: PollEvents + EndFrame (previous) + WaitFrame/BeginFrame/LocateViews (next)
    // - CollectVisible thread: CollectVisible (build per-eye command buffers)
    // - CollectVisible thread: SwapBuffers (sync point; publishes buffers to render thread)
    // - Render thread: Acquire/Wait/Render/Release swapchain images + EndFrame
    // NOTE: Do not mark these as 'volatile' because we pass them by 'ref' to Volatile/Interlocked APIs,
    // and C# does not treat 'ref volatile-field' as volatile (CS0420). Use Volatile.Read/Write and Interlocked instead.
    private int _framePrepared;
    private int _frameSkipRender;

    // Pending OpenXR frame (WaitFrame/BeginFrame done; views located) awaiting engine CollectVisible+SwapBuffers.
    private int _pendingXrFrame;
    private int _pendingXrFrameCollected;
    private int _pendingXrFrameUsesTrueSinglePassStereo;
    private readonly int _openXrOcclusionPovId = OcclusionViewOwnership.AllocatePovId();

    private int _openXrPendingFrameNumber;
    private int _openXrLifecycleFrameIndex;
    private int _openXrRenderThreadId;
    private int _openXrActionsSyncedFrameNumber;
    private ulong _openXrLastRenderedFrameId;
    private readonly float? _phase524bTsrRenderScaleOverride =
        ResolvePhase524bTsrRenderScaleOverride();

    // Dedicated OpenXR pacing thread (only used when OpenXrRenderPacingHandling == DedicatedThread).
    private Thread? _openXrPacingThread;
    private int _openXrPacingThreadId;
    private int _openXrCollectVisiblePrepThreadId;
    private int _openXrCollectVisiblePrepActive;
    private int _openXrFramePrepActive;
    private readonly ManualResetEventSlim _openXrPacingWakeEvent = new(initialState: false);
    private int _openXrPacingStopRequested;

    private long _openXrPrepareTimestamp;
    private long _openXrCollectTimestamp;
    private long _openXrSwapTimestamp;

    private int _openXrDebugFrameIndex;
    private const int OpenXrDebugLogEveryNFrames = 60;

    private static bool OpenXrDebugGl => RuntimeEngine.Rendering.Settings.OpenXrDebugGl;
    private static bool OpenXrDebugClearOnly => RuntimeEngine.Rendering.Settings.OpenXrDebugClearOnly;
    private static bool OpenXrDebugLifecycle => RuntimeEngine.Rendering.Settings.OpenXrDebugLifecycle;
    private static bool OpenXrDebugRenderRightThenLeft => RuntimeEngine.Rendering.Settings.OpenXrDebugRenderRightThenLeft;
    private static bool VulkanCaptureEyeOutputs =>
        string.Equals(Environment.GetEnvironmentVariable(XREngineEnvironmentVariables.VulkanCaptureEyeOutputs), "1", StringComparison.Ordinal);
    private static bool OpenXrPrepareFrameAfterDesktopRender => RuntimeEngine.Rendering.Settings.OpenXrPrepareFrameAfterDesktopRender;
    private static float OpenXrDeadlineSafetyMarginMs => RuntimeEngine.Rendering.Settings.OpenXrDeadlineSafetyMarginMs;
    private static float OpenXrPoseTimeOffsetMs => RuntimeEngine.Rendering.Settings.OpenXrPoseTimeOffsetMs;
    private static OpenXrCollectVisiblePosePolicy OpenXrCollectPosePolicy => RuntimeEngine.Rendering.Settings.OpenXrCollectVisiblePosePolicy;
    private static float OpenXrCollectFrustumPaddingDegrees => RuntimeEngine.Rendering.Settings.OpenXrCollectVisibleFrustumPaddingDegrees;
    private static OpenXrTrackingLossPolicy OpenXrTrackingLossHandling => RuntimeEngine.Rendering.Settings.OpenXrTrackingLossPolicy;
    private static OpenXrActionSyncPolicy OpenXrActionSyncHandling => RuntimeEngine.Rendering.Settings.OpenXrActionSyncPolicy;
    private static OpenXrRenderPacingMode OpenXrRenderPacingHandling => RuntimeEngine.Rendering.Settings.OpenXrRenderPacingMode;

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

    private readonly XRTexture2D?[] _vulkanEyeMirrorColors = new XRTexture2D?[2];
    private readonly XRRenderBuffer?[] _vulkanEyeMirrorDepths = new XRRenderBuffer?[2];
    private readonly XRFrameBuffer?[] _vulkanEyeMirrorFbos = new XRFrameBuffer?[2];
    private uint _vulkanEyeMirrorWidth;
    private uint _vulkanEyeMirrorHeight;

    private XRTexture2DArray? _vulkanStereoColorArray;
    private XRTexture2DArray? _vulkanStereoDepthArray;
    private XRTexture2DArrayView? _vulkanStereoLeftColorView;
    private XRTexture2DArrayView? _vulkanStereoRightColorView;
    private XRFrameBuffer? _vulkanStereoFbo;
    private uint _vulkanStereoWidth;
    private uint _vulkanStereoHeight;
    private Silk.NET.Vulkan.Format _vulkanStereoColorFormat;

    private XRTexture2D? _previewLeftEyeTexture;
    private XRTexture2D? _previewRightEyeTexture;
    private uint _previewEyeTextureWidth;
    private uint _previewEyeTextureHeight;
    private EPixelInternalFormat _previewEyeTextureInternalFormat = EPixelInternalFormat.Rgba8;
    private ESizedInternalFormat _previewEyeTextureSizedFormat = ESizedInternalFormat.Rgba8;

    public XRTexture2D? PreviewLeftEyeTexture => _previewLeftEyeTexture;
    public XRTexture2D? PreviewRightEyeTexture => _previewRightEyeTexture;
    public XRTexture2D? DesktopMirrorTexture => _viewportMirrorColor;

    private uint _blitReadFbo;
    private uint _blitDrawFbo;

    private nint _blitFboHglrc;
    private uint _openXrCurrentSwapchainFramebuffer;

    #endregion

    #region Parallel collect worker state

    private readonly object _openXrParallelCollectDispatchLock = new();
    private Thread? _openXrLeftCollectWorker;
    private Thread? _openXrRightCollectWorker;
    private AutoResetEvent? _openXrLeftCollectStart;
    private AutoResetEvent? _openXrRightCollectStart;
    private ManualResetEventSlim? _openXrLeftCollectDone;
    private ManualResetEventSlim? _openXrRightCollectDone;
    private volatile bool _openXrParallelCollectWorkersStop;

    private IRuntimeRenderWorld? _openXrParallelCollectWorld;
    private XRCamera? _openXrParallelCollectLeftCamera;
    private XRCamera? _openXrParallelCollectRightCamera;
    private int _openXrParallelCollectLeftAdded;
    private int _openXrParallelCollectRightAdded;
    private long _openXrParallelCollectLeftBuildTicks;
    private long _openXrParallelCollectRightBuildTicks;
    private Exception? _openXrParallelCollectLeftError;
    private Exception? _openXrParallelCollectRightError;

    #endregion

    #region Session + swapchain state

    /// <summary>
    /// Current state of the OpenXR session.
    /// </summary>
    private SessionState _sessionState = SessionState.Unknown;
    private OpenXrRuntimeState _runtimeState = OpenXrRuntimeState.DesktopOnly;
    private OpenXrRuntimeLossReason _runtimeLossReason = OpenXrRuntimeLossReason.None;
    private DateTime _nextProbeUtc = DateTime.MinValue;
    private TimeSpan _probeInterval = TimeSpan.FromSeconds(1.5);
    private readonly TimeSpan _maximumProbeRetryInterval = TimeSpan.FromSeconds(30);
    private int _consecutiveInstanceProbeFailures;
    private int _consecutiveSystemProbeFailures;
    private string? _runtimeFailureReason;
    private readonly TimeSpan _graphicsDeviceFailureProbeInterval = TimeSpan.FromMinutes(1);
    private readonly TimeSpan _intentionalOpenXrRecreateBackoffBypassDuration = TimeSpan.FromSeconds(10);
    private readonly TimeSpan _intentionalOpenXrRecreateProbeInterval = TimeSpan.FromMilliseconds(250);
    private DateTime _intentionalOpenXrRecreateBackoffBypassUntilUtc = DateTime.MinValue;
    private readonly object _runtimeLossLock = new();
    private int _runtimeLossPending;
    private int _sessionRunning;
    private bool _runtimeMonitoringEnabled;
    private IXrGraphicsBinding? _graphicsBinding;

    public OpenXrRuntimeState RuntimeState => _runtimeState;
    public bool IsSessionRunning => Volatile.Read(ref _sessionRunning) != 0;
    public string? RuntimeFailureReason => _runtimeFailureReason;

    /// <summary>
    /// Configuration information for each view (eye).
    /// </summary>
    private readonly ViewConfigurationView[] _viewConfigViews = new ViewConfigurationView[RenderFrameViewSet.MaxViewCount];
    private readonly ViewConfigurationView[] _nonFoveatedStereoViewConfigViews = new ViewConfigurationView[RenderFrameViewSet.MaxViewCount];
    private readonly ViewConfigurationView[] _foveatedQuadViewConfigViews = new ViewConfigurationView[RenderFrameViewSet.MaxViewCount];
    private uint _nonFoveatedStereoViewConfigViewCount;
    private uint _foveatedQuadViewConfigViewCount;

    /// <summary>
    /// Swapchain handles for each view.
    /// </summary>
    private readonly Swapchain[] _swapchains = new Swapchain[RenderFrameViewSet.MaxViewCount];

    /// <summary>
    /// OpenGL swapchain image pointers for each view.
    /// </summary>
    private readonly SwapchainImageOpenGLKHR*[] _swapchainImagesGL = new SwapchainImageOpenGLKHR*[RenderFrameViewSet.MaxViewCount];

    /// <summary>
    /// OpenGL framebuffer handles for each swapchain image.
    /// </summary>
    private readonly uint[]?[] _swapchainFramebuffers = new uint[]?[RenderFrameViewSet.MaxViewCount];

    /// <summary>
    /// Number of swapchain images per view.
    /// </summary>
    private readonly uint[] _swapchainImageCounts = new uint[RenderFrameViewSet.MaxViewCount];

    /// <summary>
    /// Actual swapchain extents requested/created for each view.
    /// </summary>
    private readonly uint[] _swapchainWidths = new uint[RenderFrameViewSet.MaxViewCount];
    private readonly uint[] _swapchainHeights = new uint[RenderFrameViewSet.MaxViewCount];
    private EOpenXrEyeResolutionPreset _appliedOpenXrEyeResolutionPreset = RuntimeRenderingHostServiceDefaults.OpenXrEyeResolutionPreset;
    private float _appliedOpenXrEyeResolutionScale = RuntimeRenderingHostServiceDefaults.OpenXrEyeResolutionScale;
    private uint _appliedOpenXrCustomEyeResolutionWidth = RuntimeRenderingHostServiceDefaults.OpenXrCustomEyeResolutionWidth;
    private uint _appliedOpenXrCustomEyeResolutionHeight = RuntimeRenderingHostServiceDefaults.OpenXrCustomEyeResolutionHeight;
    private bool _renderSettingsChangedSubscribed;
    private int _openXrEyeResolutionRecreateQueued;

    /// <summary>
    /// Vulkan swapchain image pointers for each view.
    /// </summary>
    private readonly SwapchainImageVulkan2KHR*[] _swapchainImagesVK = new SwapchainImageVulkan2KHR*[RenderFrameViewSet.MaxViewCount];

    /// <summary>
    /// DirectX swapchain image pointers for each view.
    /// </summary>
    private readonly SwapchainImageD3D12KHR*[] _swapchainImagesDX = new SwapchainImageD3D12KHR*[RenderFrameViewSet.MaxViewCount];

    #endregion

    internal bool TryGetGl(out GL gl)
    {
        if (_gl is not null)
        {
            gl = _gl;
            return true;
        }

        gl = null!;
        return false;
    }

    #region Pipeline helpers

    /// <summary>
    /// Returns an OpenXR-owned pipeline instance that matches the source pipeline type/config as closely as possible,
    /// without sharing the source pipeline instance.
    /// </summary>
    private RenderPipeline GetOrCreateOpenXrPipeline(RenderPipeline? sourcePipeline, int eyeIndex)
    {
        sourcePipeline ??= RuntimeEngine.Rendering.NewRenderPipeline(stereo: false);
        return eyeIndex == 1
            ? GetOrCreateOpenXrPipelineInSlot(sourcePipeline, stereo: false, ref _openXrRightRenderPipeline)
            : GetOrCreateOpenXrPipelineInSlot(sourcePipeline, stereo: false, ref _openXrLeftRenderPipeline);
    }

    private RenderPipeline GetOrCreateOpenXrStereoPipeline(RenderPipeline? sourcePipeline)
    {
        sourcePipeline ??= RuntimeEngine.Rendering.NewRenderPipeline(stereo: true);
        return GetOrCreateOpenXrPipelineInSlot(sourcePipeline, stereo: true, ref _openXrStereoRenderPipeline);
    }

    private static RenderPipeline GetOrCreateOpenXrPipelineInSlot(
        RenderPipeline sourcePipeline,
        bool stereo,
        ref RenderPipeline? openXrPipeline)
    {
        // If the source pipeline type changed, recreate our dedicated instance.
        if (openXrPipeline is null || openXrPipeline.GetType() != sourcePipeline.GetType())
        {
            openXrPipeline = CreateOpenXrPipeline(sourcePipeline, stereo);
        }
        else
        {
            // Keep simple flags aligned if the source changes them at runtime.
            openXrPipeline.IsShadowPass = sourcePipeline.IsShadowPass;
        }

        return openXrPipeline;
    }

    private static RenderPipeline CreateOpenXrPipeline(RenderPipeline sourcePipeline, bool stereo)
    {
        RenderPipeline created;
        try
        {
            if (stereo)
            {
                created = CreateStereoOpenXrPipeline(sourcePipeline);
            }
            else if (!RenderPipeline.TryCreateOpenXrPipeline(sourcePipeline, out RenderPipeline? registered) || registered is null)
            {
                if (XRRuntimeEnvironment.IsAotRuntimeBuild)
                    throw new InvalidOperationException($"No registered OpenXR render pipeline factory for type {sourcePipeline.GetType().FullName}.");

                created = (RenderPipeline?)Activator.CreateInstance(sourcePipeline.GetType())
                          ?? RuntimeEngine.Rendering.NewRenderPipeline(stereo: false);
            }
            else
            {
                created = registered;
            }
        }
        catch when (!XRRuntimeEnvironment.IsAotRuntimeBuild)
        {
            created = RuntimeEngine.Rendering.NewRenderPipeline(stereo);
        }

        created.IsShadowPass = sourcePipeline.IsShadowPass;
        return created;
    }

    private static RenderPipeline CreateStereoOpenXrPipeline(RenderPipeline sourcePipeline)
    {
        if (sourcePipeline is DefaultRenderPipeline)
            return new DefaultRenderPipeline(stereo: true);
        if (sourcePipeline is DefaultRenderPipeline2)
            return new DefaultRenderPipeline2(stereo: true);

        if (XRRuntimeEnvironment.IsAotRuntimeBuild)
            throw new InvalidOperationException($"No registered stereo OpenXR render pipeline factory for type {sourcePipeline.GetType().FullName}.");

        var constructor = sourcePipeline.GetType().GetConstructor([typeof(bool)]);
        if (constructor is not null && constructor.Invoke([true]) is RenderPipeline reflected)
            return reflected;

        return RuntimeEngine.Rendering.NewRenderPipeline(stereo: true);
    }

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

    private void EnsureOpenXrEyeSettingsOwnership(XRCamera leftEyeCamera, XRCamera rightEyeCamera)
    {
        CameraPostProcessStateCollection sharedPostProcessStates =
            _openXrSharedEyePostProcessStates
            ??= leftEyeCamera.PostProcessStates ?? new CameraPostProcessStateCollection();

        try
        {
            Volatile.Write(ref _openXrSyncingEyeCameraSettings, 1);

            if (!ReferenceEquals(leftEyeCamera.PostProcessStates, sharedPostProcessStates))
                leftEyeCamera.PostProcessStates = sharedPostProcessStates;
            if (!ReferenceEquals(rightEyeCamera.PostProcessStates, sharedPostProcessStates))
                rightEyeCamera.PostProcessStates = sharedPostProcessStates;
        }
        finally
        {
            Volatile.Write(ref _openXrSyncingEyeCameraSettings, 0);
        }

        UpdateOpenXrEyeSettingsSubscriptions(leftEyeCamera, rightEyeCamera);
        _openXrEyeSettingsSourceCamera ??= leftEyeCamera;
    }

    private void UpdateOpenXrEyeSettingsSubscriptions(XRCamera? leftEyeCamera, XRCamera? rightEyeCamera)
    {
        UpdateOpenXrEyeSettingsSubscription(ref _openXrSubscribedLeftEyeSettingsCamera, leftEyeCamera);
        UpdateOpenXrEyeSettingsSubscription(ref _openXrSubscribedRightEyeSettingsCamera, rightEyeCamera);

        if (leftEyeCamera is null || rightEyeCamera is null)
            _openXrEyeSettingsSourceCamera = null;
        else if (!ReferenceEquals(_openXrEyeSettingsSourceCamera, leftEyeCamera) &&
                 !ReferenceEquals(_openXrEyeSettingsSourceCamera, rightEyeCamera))
        {
            _openXrEyeSettingsSourceCamera = leftEyeCamera;
        }
    }

    private void UpdateOpenXrEyeSettingsSubscription(ref XRCamera? subscribedCamera, XRCamera? camera)
    {
        if (ReferenceEquals(subscribedCamera, camera))
            return;

        if (subscribedCamera is not null)
            subscribedCamera.PropertyChanged -= HandleOpenXrEyeCameraSettingsChanged;

        subscribedCamera = camera;

        if (subscribedCamera is not null)
            subscribedCamera.PropertyChanged += HandleOpenXrEyeCameraSettingsChanged;
    }

    private void HandleOpenXrEyeCameraSettingsChanged(object? sender, IXRPropertyChangedEventArgs args)
    {
        if (Volatile.Read(ref _openXrSyncingEyeCameraSettings) != 0)
            return;
        if (sender is not XRCamera sourceCamera)
            return;

        XRCamera? destinationCamera = ResolveOpenXrEyeSettingsDestination(sourceCamera);
        if (destinationCamera is null)
            return;

        switch (args.PropertyName)
        {
            case nameof(XRCamera.AntiAliasingModeOverride):
            case nameof(XRCamera.MsaaSampleCountOverride):
            case nameof(XRCamera.OutputHDROverride):
            case nameof(XRCamera.TsrRenderScaleOverride):
            case nameof(XRCamera.PostProcessMaterial):
                _openXrEyeSettingsSourceCamera = sourceCamera;
                CopyOpenXrEyeScalarSettings(sourceCamera, destinationCamera);
                break;
            case nameof(XRCamera.PostProcessStates):
                _openXrEyeSettingsSourceCamera = sourceCamera;
                AdoptOpenXrEyePostProcessStates(sourceCamera, destinationCamera);
                break;
        }
    }

    private XRCamera? ResolveOpenXrEyeSettingsDestination(XRCamera sourceCamera)
    {
        if (ReferenceEquals(sourceCamera, _openXrSubscribedLeftEyeSettingsCamera))
            return _openXrSubscribedRightEyeSettingsCamera;
        if (ReferenceEquals(sourceCamera, _openXrSubscribedRightEyeSettingsCamera))
            return _openXrSubscribedLeftEyeSettingsCamera;
        return null;
    }

    private void CopyOpenXrEyeScalarSettings(XRCamera sourceCamera, XRCamera destinationCamera)
    {
        try
        {
            Volatile.Write(ref _openXrSyncingEyeCameraSettings, 1);

            destinationCamera.AntiAliasingModeOverride = sourceCamera.AntiAliasingModeOverride;
            destinationCamera.MsaaSampleCountOverride = sourceCamera.MsaaSampleCountOverride;
            destinationCamera.OutputHDROverride = sourceCamera.OutputHDROverride;
            destinationCamera.TsrRenderScaleOverride = sourceCamera.TsrRenderScaleOverride;
            destinationCamera.PostProcessMaterial = sourceCamera.PostProcessMaterial;
        }
        finally
        {
            Volatile.Write(ref _openXrSyncingEyeCameraSettings, 0);
        }
    }

    private void AdoptOpenXrEyePostProcessStates(XRCamera sourceCamera, XRCamera destinationCamera)
    {
        CameraPostProcessStateCollection sharedPostProcessStates =
            sourceCamera.PostProcessStates ?? new CameraPostProcessStateCollection();
        _openXrSharedEyePostProcessStates = sharedPostProcessStates;

        try
        {
            Volatile.Write(ref _openXrSyncingEyeCameraSettings, 1);

            if (!ReferenceEquals(destinationCamera.PostProcessStates, sharedPostProcessStates))
                destinationCamera.PostProcessStates = sharedPostProcessStates;
        }
        finally
        {
            Volatile.Write(ref _openXrSyncingEyeCameraSettings, 0);
        }
    }

    #endregion
}
