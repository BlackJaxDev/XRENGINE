using Silk.NET.OpenGL;
using Silk.NET.OpenXR;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Scene.Transforms;

namespace XREngine.Rendering.API.Rendering.OpenXR;

public unsafe partial class OpenXRAPI
{
    public enum OpenXrRuntimeState
    {
        DesktopOnly,
        XrInstanceReady,
        XrSystemReady,
        SessionCreated,
        SessionRunning,
        SessionStopping,
        SessionLost,
        RecreatePending
    }

    public enum OpenXrRuntimeLossReason
    {
        None,
        SessionExiting,
        SessionLossPending,
        SessionLostError,
        InstanceLostError,
        RuntimeUnavailable,
        ShutdownRequested
    }

    public enum OpenXrPoseTiming
    {
        Predicted,
        Late
    }

    public enum OpenXrCollectVisiblePosePolicy
    {
        Predicted,
        RelocatePredicted,
        PaddedFrustum
    }

    public enum OpenXrTrackingLossPolicy
    {
        FreezeLastValid,
        Identity,
        SkipFrame
    }

    public enum OpenXrActionSyncPolicy
    {
        PredictedOnly,
        PredictedAndLate
    }

    /// <summary>
    /// Controls where OpenXR's next-frame preparation (xrWaitFrame / xrBeginFrame / LocateViews(Predicted) /
    /// UpdateActionPoseCaches(Predicted)) runs.
    /// </summary>
    public enum OpenXrRenderPacingMode
    {
        /// <summary>Run prep inline at the start of the render callback (legacy behavior).</summary>
        InRenderCallback,
        /// <summary>Run prep at the end of the render callback after desktop viewports finish (default).</summary>
        PostRenderCallback,
        /// <summary>Run prep on a dedicated OpenXR pacing thread; the render thread only signals after xrEndFrame.</summary>
        DedicatedThread,
        /// <summary>Run prep on the engine CollectVisible thread before building OpenXR visibility buffers.</summary>
        CollectVisibleThread
    }

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
    private XRCamera? _openXrLeftEyeCamera;
    private XRCamera? _openXrRightEyeCamera;

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
    private TransformBase? _openXrLocomotionRoot;

    // OpenXR renders each eye in a separate pipeline execution (stereoPass=false).
    // IMPORTANT: The OpenXR eye viewports must NOT share the desktop viewport's RenderPipeline instance.
    // Sharing a pipeline instance across viewports of different sizes can cause constant FBO/cache churn.
    // We therefore keep an OpenXR-owned pipeline instance (non-stereo) and never reuse the desktop's.
    private RenderPipeline? _openXrRenderPipeline;
    private RenderCommandCollection? _openXrSharedMeshRenderCommands;
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

    private int _openXrPendingFrameNumber;
    private int _openXrLifecycleFrameIndex;
    private int _openXrRenderThreadId;
    private int _openXrActionsSyncedFrameNumber;

    // Dedicated OpenXR pacing thread (only used when OpenXrRenderPacingHandling == DedicatedThread).
    private Thread? _openXrPacingThread;
    private int _openXrPacingThreadId;
    private int _openXrCollectVisiblePrepThreadId;
    private int _openXrCollectVisiblePrepActive;
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

    private XRTexture2D? _previewLeftEyeTexture;
    private XRTexture2D? _previewRightEyeTexture;
    private uint _previewEyeTextureWidth;
    private uint _previewEyeTextureHeight;

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
    private readonly TimeSpan _graphicsDeviceFailureProbeInterval = TimeSpan.FromMinutes(1);
    private readonly object _runtimeLossLock = new();
    private int _runtimeLossPending;
    private int _sessionRunning;
    private bool _runtimeMonitoringEnabled;
    private IXrGraphicsBinding? _graphicsBinding;

    public OpenXrRuntimeState RuntimeState => _runtimeState;
    public bool IsSessionRunning => Volatile.Read(ref _sessionRunning) != 0;

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
    private readonly uint[]?[] _swapchainFramebuffers = new uint[]?[2];

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
    private RenderPipeline GetOrCreateOpenXrPipeline(RenderPipeline? sourcePipeline)
    {
        // Best-effort: if no source pipeline exists, fall back to a sane default.
        sourcePipeline ??= RuntimeEngine.Rendering.NewRenderPipeline(stereo: false);

        // If the source pipeline type changed, recreate our dedicated instance.
        if (_openXrRenderPipeline is null || _openXrRenderPipeline.GetType() != sourcePipeline.GetType())
        {
            RenderPipeline created;
            try
            {
                if (!RenderPipeline.TryCreateOpenXrPipeline(sourcePipeline, out RenderPipeline? registered) || registered is null)
                {
                    if (XRRuntimeEnvironment.IsAotRuntimeBuild)
                        throw new InvalidOperationException($"No registered OpenXR render pipeline factory for type {sourcePipeline.GetType().FullName}.");

                    created = (RenderPipeline?)Activator.CreateInstance(sourcePipeline.GetType())
                              ?? RuntimeEngine.Rendering.NewRenderPipeline(stereo: false);
                    created.IsShadowPass = sourcePipeline.IsShadowPass;
                }
                else
                {
                    created = registered;
                }
            }
            catch when (!XRRuntimeEnvironment.IsAotRuntimeBuild)
            {
                created = RuntimeEngine.Rendering.NewRenderPipeline(stereo: false);
            }

            _openXrRenderPipeline = created;
        }
        else
        {
            // Keep simple flags aligned if the source changes them at runtime.
            _openXrRenderPipeline.IsShadowPass = sourcePipeline.IsShadowPass;
        }

        return _openXrRenderPipeline;
    }

    private RenderCommandCollection EnsureOpenXrSharedMeshRenderCommands(RenderPipeline pipeline)
    {
        RenderCommandCollection commands = _openXrSharedMeshRenderCommands ??= new RenderCommandCollection();
        commands.SetRenderPasses(pipeline.PassIndicesAndSorters, pipeline.PassMetadata);
        return commands;
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

    #endregion
}
