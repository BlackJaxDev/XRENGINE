using Silk.NET.OpenGL;
using Silk.NET.OpenXR;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using XREngine.Data.Rendering;
using XREngine.Rendering;
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
    /// Controls which OpenXR pose cache VR transforms should use when invoked from RecalcMatrixOnDraw.
    /// OpenXR sets this immediately before invoking Engine.VRState.InvokeRecalcMatrixOnDraw().
    /// </summary>
    public OpenXrPoseTiming PoseTimingForRecalc { get; internal set; } = OpenXrPoseTiming.Late;

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

    private XRWorldInstance? _openXrFrameWorld;
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

    private static bool OpenXrDebugGl => Engine.Rendering.Settings.OpenXrDebugGl;
    private static bool OpenXrDebugClearOnly => Engine.Rendering.Settings.OpenXrDebugClearOnly;
    private static bool OpenXrDebugLifecycle => Engine.Rendering.Settings.OpenXrDebugLifecycle;
    private static bool OpenXrDebugRenderRightThenLeft => Engine.Rendering.Settings.OpenXrDebugRenderRightThenLeft;

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

    #region Vulkan parallel rendering toggle

    /// <summary>
    /// Flag indicating if parallel rendering is enabled
    /// </summary>
    private bool _parallelRenderingEnabled = false;

    private readonly object _openXrParallelCollectDispatchLock = new();
    private Thread? _openXrLeftCollectWorker;
    private Thread? _openXrRightCollectWorker;
    private AutoResetEvent? _openXrLeftCollectStart;
    private AutoResetEvent? _openXrRightCollectStart;
    private ManualResetEventSlim? _openXrLeftCollectDone;
    private ManualResetEventSlim? _openXrRightCollectDone;
    private volatile bool _openXrParallelCollectWorkersStop;

    private XRWorldInstance? _openXrParallelCollectWorld;
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
        sourcePipeline ??= new DefaultRenderPipeline(stereo: false);

        // If the source pipeline type changed, recreate our dedicated instance.
        if (_openXrRenderPipeline is null || _openXrRenderPipeline.GetType() != sourcePipeline.GetType())
        {
            RenderPipeline created;
            try
            {
                if (sourcePipeline is DefaultRenderPipeline srcDefault)
                {
                    created = new DefaultRenderPipeline(stereo: false)
                    {
                        IsShadowPass = srcDefault.IsShadowPass,
                    };
                }
                else
                {
                    created = (RenderPipeline?)Activator.CreateInstance(sourcePipeline.GetType())
                              ?? new DefaultRenderPipeline(stereo: false);
                    created.IsShadowPass = sourcePipeline.IsShadowPass;
                }
            }
            catch
            {
                created = new DefaultRenderPipeline(stereo: false);
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
