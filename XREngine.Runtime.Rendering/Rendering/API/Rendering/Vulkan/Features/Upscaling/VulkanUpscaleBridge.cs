using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.DLSS;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.XeSS;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Provides a bridge for handling upscaling features in Vulkan, such as DLSS and XeSS, managing frame resources and state transitions.
/// </summary>
public sealed class VulkanUpscaleBridge : IDisposable
{
    private const string ViewportResizeReason = "viewport resized";
    private const string InternalResolutionResizeReason = "internal resolution resized";

    private readonly XRViewport _viewport;
    private bool _disposed;
    private EVulkanUpscaleBridgeState _state = EVulkanUpscaleBridgeState.Initializing;
    private VulkanUpscaleBridgeFrameResources _frameResources;
    private bool _hasFrameResources;
    private string? _lastStateReason;
    private string? _pendingRecreateReason;
    private string? _lastFaultFingerprint;
    private readonly HashSet<string> _loggedStateFingerprints = [];
    private VulkanUpscaleBridgeSidecar? _sidecar;
    private VulkanUpscaleBridgeFrameSlot[] _frameSlots = [];
    private int _frameSlotIndex = -1;
    private uint _resourceGeneration;

    public VulkanUpscaleBridge(XRViewport viewport)
    {
        _viewport = viewport ?? throw new ArgumentNullException(nameof(viewport));
        QueueModel = RuntimeEngine.Rendering.VulkanUpscaleBridgeQueueModel;

        _viewport.Resized += HandleViewportResized;
        _viewport.InternalResolutionResized += HandleInternalResolutionResized;
    }

    public XRViewport Viewport => _viewport;
    public EVulkanUpscaleBridgeState State => _state;
    public EVulkanUpscaleBridgeQueueModel QueueModel { get; }
    public VulkanUpscaleBridgeFrameResources CurrentFrameResources => _frameResources;
    public bool HasFrameResources => _hasFrameResources;
    public string? LastStateReason => _lastStateReason;
    public string? PendingRecreateReason => _pendingRecreateReason;
    public bool SidecarDeviceOwned { get; private set; }
    internal uint ResourceGeneration => _resourceGeneration;
    internal bool HasInteropResources => _sidecar is not null && _frameSlots.Length > 0;
    /// <summary>
    /// Gets the current frame slot for the upscaling bridge, if available.
    /// </summary>
    internal VulkanUpscaleBridgeFrameSlot? CurrentFrameSlot
        => _frameSlotIndex >= 0 && _frameSlotIndex < _frameSlots.Length
            ? _frameSlots[_frameSlotIndex]
            : null;

    /// <summary>
    /// Tries to resolve the current frame slot for the upscaling bridge.
    /// </summary>
    /// <param name="slot">The resolved current frame slot, if available.</param>
    /// <returns>True if the current frame slot is successfully resolved and the bridge is ready; otherwise, false.</returns>
    internal bool TryResolveCurrentFrameSlot(out VulkanUpscaleBridgeFrameSlot? slot)
    {
        slot = CurrentFrameSlot;
        return !_disposed && _state == EVulkanUpscaleBridgeState.Ready && _sidecar is not null && slot is not null;
    }

    /// <summary>
    /// Prepares the upscaling bridge for the current frame, updating frame resources and determining availability based on the provided snapshot.
    /// </summary>
    /// <param name="pipeline">The render pipeline instance for the current frame.</param>
    /// <param name="snapshot">The capability snapshot for the upscaling bridge.</param>
    /// <returns>The current state of the upscaling bridge after preparation.</returns>
    public EVulkanUpscaleBridgeState PrepareForFrame(
        XRRenderPipelineInstance pipeline,
        global::XREngine.VulkanUpscaleBridgeCapabilitySnapshot snapshot)
    {
        if (_disposed)
            return EVulkanUpscaleBridgeState.Disabled;

        try
        {
            VulkanUpscaleBridgeFrameResources frameResources = BuildFrameResources(pipeline);
            bool hadFrameResources = _hasFrameResources;
            VulkanUpscaleBridgeFrameResources previousFrameResources = _frameResources;

            _frameResources = frameResources;
            _hasFrameResources = true;

            EVulkanUpscaleBridgeState availability = DetermineAvailability(snapshot, in frameResources, out string availabilityReason);
            if (availability is EVulkanUpscaleBridgeState.Disabled or EVulkanUpscaleBridgeState.Unsupported)
            {
                DestroyInteropResources();
                SidecarDeviceOwned = false;
                _pendingRecreateReason = null;
                TransitionState(availability, availabilityReason);
                return _state;
            }

            if (hadFrameResources && !previousFrameResources.Equals(frameResources))
                MarkNeedsRecreate(ResolveConfigurationChangeReason(previousFrameResources, frameResources));

            if (_state == EVulkanUpscaleBridgeState.Faulted && string.IsNullOrWhiteSpace(_pendingRecreateReason))
                return _state;

            if (!hadFrameResources)
                _pendingRecreateReason ??= "initial bridge configuration";

            if (_state != EVulkanUpscaleBridgeState.Ready || !string.IsNullOrWhiteSpace(_pendingRecreateReason))
            {
                if (_viewport.Window?.Renderer is not OpenGLRenderer renderer)
                {
                    DestroyInteropResources();
                    SidecarDeviceOwned = false;
                    TransitionState(EVulkanUpscaleBridgeState.Unsupported, "bridge MVP only applies to OpenGL windows");
                    return _state;
                }

                string readyReason = _pendingRecreateReason ?? "initial bridge configuration";
                TransitionState(EVulkanUpscaleBridgeState.Initializing, readyReason, log: false);

                RecreateInteropResources(
                    renderer,
                    snapshot,
                    in frameResources,
                    hadFrameResources ? previousFrameResources : null,
                    readyReason);
                SidecarDeviceOwned = _sidecar is not null;
                _pendingRecreateReason = null;
                _lastFaultFingerprint = null;

                TransitionState(
                    EVulkanUpscaleBridgeState.Ready,
                    $"bridge sidecar ready with {_frameSlots.Length} interop slots on '{_sidecar?.DeviceName ?? "<unknown>"}'");
                return _state;
            }

            AdvanceFrameSlot();

            return _state;
        }
        catch (Exception ex)
        {
            return ReportFaultOnce("PrepareForFrame", ex);
        }
    }

    /// <summary>
    /// Notifies the upscaling bridge that the vendor selection has changed, potentially triggering a recreation of the bridge.
    /// </summary>
    /// <param name="reason">The reason for the vendor selection change.</param>
    public void NotifyVendorSelectionChanged(string reason)
    {
        if (_disposed)
            return;

        if (!RuntimeEngine.EffectiveSettings.EnableNvidiaDlss && !RuntimeEngine.EffectiveSettings.EnableIntelXess)
        {
            DestroyInteropResources();
            SidecarDeviceOwned = false;
            _pendingRecreateReason = null;
            TransitionState(EVulkanUpscaleBridgeState.Disabled, reason);
            return;
        }

        MarkNeedsRecreate(reason);
    }

    /// <summary>
    /// Notifies the upscaling bridge that the capability snapshot has changed, potentially triggering a recreation of the bridge.
    /// </summary>
    /// <param name="reason">The reason for the capability snapshot change.</param>
    public void NotifyCapabilitySnapshotChanged(string reason)
    {
        if (_disposed)
            return;

        if (!RuntimeEngine.Rendering.VulkanUpscaleBridgeRequested)
        {
            DestroyInteropResources();
            SidecarDeviceOwned = false;
            _pendingRecreateReason = null;
            TransitionState(EVulkanUpscaleBridgeState.Disabled, reason);
            return;
        }

        MarkNeedsRecreate(reason);
    }

    /// <summary>
    /// Marks the upscaling bridge as needing recreation, typically due to a change in configuration or environment.
    /// </summary>
    /// <param name="reason">The reason for marking the bridge as needing recreation.</param>
    public void MarkNeedsRecreate(string reason)
    {
        if (_disposed)
            return;

        string effectiveReason = string.IsNullOrWhiteSpace(reason)
            ? "bridge configuration changed"
            : reason;

        _pendingRecreateReason = effectiveReason;
        if (_state is EVulkanUpscaleBridgeState.Ready or EVulkanUpscaleBridgeState.Faulted)
            TransitionState(EVulkanUpscaleBridgeState.NeedsRecreate, effectiveReason);
    }

    /// <summary>
    /// Destroys the upscaling bridge, releasing all associated resources and transitioning it to the disabled state.
    /// </summary>
    /// <param name="reason">The reason for destroying the bridge.</param>
    public void Destroy(string reason)
    {
        if (_disposed)
            return;

        _disposed = true;
        _viewport.Resized -= HandleViewportResized;
        _viewport.InternalResolutionResized -= HandleInternalResolutionResized;

        DestroyInteropResources();
        SidecarDeviceOwned = false;
        _pendingRecreateReason = null;
        TransitionState(EVulkanUpscaleBridgeState.Disabled, reason);
    }

    /// <summary>
    /// Disposes the upscaling bridge, releasing all associated resources. 
    /// This is equivalent to calling <see cref="Destroy"/> with a reason of "bridge disposed".
    /// </summary>
    /// <remarks>
    /// This method should be called when the upscaling bridge is no longer needed, ensuring that all resources are properly released.
    /// </remarks>
    public void Dispose()
        => Destroy("bridge disposed");

    /// <summary>
    /// Attempts to execute a passthrough operation using the provided frame buffers and renderer.
    /// </summary>
    /// <param name="renderer">The OpenGL renderer used for the passthrough operation.</param>
    /// <param name="sourceColorFbo">The source color frame buffer.</param>
    /// <param name="sourceDepthFbo">The source depth frame buffer.</param>
    /// <param name="sourceMotionFbo">The source motion frame buffer.</param>
    /// <param name="outputTexture">The output texture resulting from the passthrough operation.</param>
    /// <param name="dispatchDuration">The duration of the dispatch operation.</param>
    /// <param name="failureReason">The reason for failure, if the operation was not successful.</param>
    /// <returns>True if the passthrough operation was successful; otherwise, false.</returns>
    internal bool TryExecutePassthrough(
        OpenGLRenderer renderer,
        XRFrameBuffer sourceColorFbo,
        XRFrameBuffer sourceDepthFbo,
        XRFrameBuffer sourceMotionFbo,
        out XRTexture? outputTexture,
        out TimeSpan dispatchDuration,
        out string failureReason)
    {
        outputTexture = null;
        dispatchDuration = TimeSpan.Zero;
        failureReason = string.Empty;

        if (!TryResolveCurrentFrameSlot(out VulkanUpscaleBridgeFrameSlot? slot) || slot is null || _sidecar is null)
        {
            failureReason = _disposed
                ? "bridge was disposed"
                : _state != EVulkanUpscaleBridgeState.Ready
                    ? $"bridge is not ready (state={_state})"
                    : "bridge interop slot is unavailable";
            return false;
        }

        try
        {
            _sidecar.WaitForFrameSlotAvailability(slot);

            renderer.BlitFBOToFBO(
                sourceColorFbo,
                slot.SourceColorFrameBuffer,
                EReadBufferMode.ColorAttachment0,
                colorBit: true,
                depthBit: false,
                stencilBit: false,
                linearFilter: false);
            renderer.BlitFBOToFBO(
                sourceDepthFbo,
                slot.SourceDepthFrameBuffer,
                EReadBufferMode.None,
                colorBit: false,
                depthBit: true,
                stencilBit: true,
                linearFilter: false);
            renderer.BlitFBOToFBO(
                sourceMotionFbo,
                slot.SourceMotionFrameBuffer,
                EReadBufferMode.ColorAttachment0,
                colorBit: true,
                depthBit: false,
                stencilBit: false,
                linearFilter: false);

            if (!TryGetGlTextureBinding(renderer, slot.SourceColorTexture, out uint sourceColorTextureId)
                || !TryGetGlTextureBinding(renderer, slot.SourceDepthTexture, out uint sourceDepthTextureId)
                || !TryGetGlTextureBinding(renderer, slot.SourceMotionTexture, out uint sourceMotionTextureId)
                || !TryGetGlTextureBinding(renderer, slot.OutputColorTexture, out uint outputColorTextureId))
            {
                failureReason = "failed to resolve one or more OpenGL bridge texture handles";
                return false;
            }

            Span<uint> readyTextureIds = stackalloc uint[3]
            {
                sourceColorTextureId,
                sourceDepthTextureId,
                sourceMotionTextureId,
            };
            Span<Silk.NET.OpenGLES.TextureLayout> readyLayouts = stackalloc Silk.NET.OpenGLES.TextureLayout[3]
            {
                Silk.NET.OpenGLES.TextureLayout.GeneralExt,
                Silk.NET.OpenGLES.TextureLayout.GeneralExt,
                Silk.NET.OpenGLES.TextureLayout.GeneralExt,
            };

            renderer.SignalExternalTextureSemaphore(slot.GlReadySemaphore, readyTextureIds, readyLayouts);
            slot.SourceColor.CurrentLayout = Silk.NET.Vulkan.ImageLayout.General;
            slot.SourceDepth.CurrentLayout = Silk.NET.Vulkan.ImageLayout.General;
            slot.SourceMotion.CurrentLayout = Silk.NET.Vulkan.ImageLayout.General;

            Stopwatch dispatchStopwatch = Stopwatch.StartNew();
            _sidecar.SubmitPassthroughBlit(slot);
            dispatchStopwatch.Stop();
            dispatchDuration = dispatchStopwatch.Elapsed;

            renderer.WaitExternalTextureSemaphore(
                slot.GlCompleteSemaphore,
                outputColorTextureId,
                Silk.NET.OpenGLES.TextureLayout.GeneralExt);

            outputTexture = slot.OutputColorTexture;
            return true;
        }
        catch (Exception ex)
        {
            failureReason = ex.Message;
            ReportFaultOnce("TryExecutePassthrough", ex);
            return false;
        }
    }

    /// <summary>
    /// Attempts to execute a vendor-specific upscale operation using the provided frame buffers and renderer.
    /// </summary>
    /// <param name="renderer">The OpenGL renderer used for the upscale operation.</param>
    /// <param name="sourceColorFbo">The source color frame buffer.</param>
    /// <param name="sourceDepthFbo">The source depth frame buffer.</param>
    /// <param name="sourceMotionFbo">The source motion frame buffer.</param>
    /// <param name="sourceExposureFbo">The optional source exposure frame buffer.</param>
    /// <param name="parameters">The dispatch parameters for the upscale operation.</param>
    /// <param name="outputTexture">The output texture resulting from the upscale operation.</param>
    /// <param name="dispatchDuration">The duration of the dispatch operation.</param>
    /// <param name="failureReason">The reason for failure, if the operation was not successful.</param>
    /// <returns>True if the vendor-specific upscale operation was successful; otherwise, false.</returns>
    internal bool TryExecuteVendorUpscale(
        OpenGLRenderer renderer,
        XRFrameBuffer sourceColorFbo,
        XRFrameBuffer sourceDepthFbo,
        XRFrameBuffer sourceMotionFbo,
        XRFrameBuffer? sourceExposureFbo,
        in VulkanUpscaleBridgeDispatchParameters parameters,
        out XRTexture? outputTexture,
        out TimeSpan dispatchDuration,
        out string failureReason)
    {
        outputTexture = null;
        dispatchDuration = TimeSpan.Zero;
        failureReason = string.Empty;

        if (!TryResolveCurrentFrameSlot(out VulkanUpscaleBridgeFrameSlot? slot) || slot is null || _sidecar is null)
        {
            failureReason = _disposed
                ? "bridge was disposed"
                : _state != EVulkanUpscaleBridgeState.Ready
                    ? $"bridge is not ready (state={_state})"
                    : "bridge interop slot is unavailable";
            return false;
        }

        try
        {
            _sidecar.WaitForFrameSlotAvailability(slot);

            renderer.BlitFBOToFBO(
                sourceColorFbo,
                slot.SourceColorFrameBuffer,
                EReadBufferMode.ColorAttachment0,
                colorBit: true,
                depthBit: false,
                stencilBit: false,
                linearFilter: false);
            renderer.BlitFBOToFBO(
                sourceDepthFbo,
                slot.SourceDepthFrameBuffer,
                EReadBufferMode.None,
                colorBit: false,
                depthBit: true,
                stencilBit: true,
                linearFilter: false);
            renderer.BlitFBOToFBO(
                sourceMotionFbo,
                slot.SourceMotionFrameBuffer,
                EReadBufferMode.ColorAttachment0,
                colorBit: true,
                depthBit: false,
                stencilBit: false,
                linearFilter: false);

            if (parameters.HasExposureTexture && sourceExposureFbo is not null)
            {
                renderer.BlitFBOToFBO(
                    sourceExposureFbo,
                    slot.ExposureFrameBuffer,
                    EReadBufferMode.ColorAttachment0,
                    colorBit: true,
                    depthBit: false,
                    stencilBit: false,
                    linearFilter: false);
            }

            if (!TryGetGlTextureBinding(renderer, slot.SourceColorTexture, out uint sourceColorTextureId)
                || !TryGetGlTextureBinding(renderer, slot.SourceDepthTexture, out uint sourceDepthTextureId)
                || !TryGetGlTextureBinding(renderer, slot.SourceMotionTexture, out uint sourceMotionTextureId)
                || !TryGetGlTextureBinding(renderer, slot.OutputColorTexture, out uint outputColorTextureId))
            {
                failureReason = "failed to resolve one or more OpenGL bridge texture handles";
                return false;
            }

            uint exposureTextureId = 0u;
            if (parameters.HasExposureTexture && (!TryGetGlTextureBinding(renderer, slot.ExposureTexture, out exposureTextureId) || exposureTextureId == 0u))
            {
                failureReason = "failed to resolve the OpenGL bridge exposure texture handle";
                return false;
            }

            Span<uint> readyTextureIds = stackalloc uint[4];
            Span<Silk.NET.OpenGLES.TextureLayout> readyLayouts = stackalloc Silk.NET.OpenGLES.TextureLayout[4];
            readyTextureIds[0] = sourceColorTextureId;
            readyLayouts[0] = Silk.NET.OpenGLES.TextureLayout.GeneralExt;
            readyTextureIds[1] = sourceDepthTextureId;
            readyLayouts[1] = Silk.NET.OpenGLES.TextureLayout.GeneralExt;
            readyTextureIds[2] = sourceMotionTextureId;
            readyLayouts[2] = Silk.NET.OpenGLES.TextureLayout.GeneralExt;

            int readyCount = 3;
            if (parameters.HasExposureTexture)
            {
                readyTextureIds[readyCount] = exposureTextureId;
                readyLayouts[readyCount] = Silk.NET.OpenGLES.TextureLayout.GeneralExt;
                readyCount++;
            }

            renderer.SignalExternalTextureSemaphore(slot.GlReadySemaphore, readyTextureIds[..readyCount], readyLayouts[..readyCount]);
            slot.SourceColor.CurrentLayout = Silk.NET.Vulkan.ImageLayout.General;
            slot.SourceDepth.CurrentLayout = Silk.NET.Vulkan.ImageLayout.General;
            slot.SourceMotion.CurrentLayout = Silk.NET.Vulkan.ImageLayout.General;
            if (parameters.HasExposureTexture)
                slot.Exposure.CurrentLayout = Silk.NET.Vulkan.ImageLayout.General;
            slot.OutputColor.CurrentLayout = Silk.NET.Vulkan.ImageLayout.General;

            Stopwatch dispatchStopwatch = Stopwatch.StartNew();
            bool submitOk = _sidecar.SubmitVendorUpscale(slot, in parameters, out string vendorFailure);
            dispatchStopwatch.Stop();
            dispatchDuration = dispatchStopwatch.Elapsed;

            if (!submitOk)
            {
                failureReason = string.IsNullOrWhiteSpace(vendorFailure)
                    ? $"bridge {parameters.Vendor} submission failed"
                    : vendorFailure;
                return false;
            }

            renderer.WaitExternalTextureSemaphore(
                slot.GlCompleteSemaphore,
                outputColorTextureId,
                Silk.NET.OpenGLES.TextureLayout.GeneralExt);

            outputTexture = slot.OutputColorTexture;
            return true;
        }
        catch (Exception ex)
        {
            failureReason = ex.Message;
            ReportFaultOnce("TryExecuteVendorUpscale", ex);
            return false;
        }
    }

    /// <summary>
    /// Handles the event when the viewport is resized, marking the bridge as needing recreation.
    /// </summary>
    /// <param name="_">The viewport that was resized.</param>
    private void HandleViewportResized(XRViewport _)
        => MarkNeedsRecreate(ViewportResizeReason);

    /// <summary>
    /// Handles the event when the internal resolution is resized, marking the bridge as needing recreation.
    /// </summary>
    /// <param name="_">The viewport whose internal resolution was resized.</param>
    private void HandleInternalResolutionResized(XRViewport _)
        => MarkNeedsRecreate(InternalResolutionResizeReason);

    /// <summary>
    /// Reports a fault in the Vulkan upscale bridge, ensuring it is only reported once for a given exception fingerprint.
    /// </summary>
    /// <param name="stage">The stage during which the fault occurred.</param>
    /// <param name="ex">The exception that caused the fault.</param>
    /// <returns>The new state of the Vulkan upscale bridge after reporting the fault.</returns>
    private EVulkanUpscaleBridgeState ReportFaultOnce(string stage, Exception ex)
    {
        string fingerprint = string.Concat(stage, "|", ex.GetType().FullName, "|", ex.Message);
        if (!string.Equals(_lastFaultFingerprint, fingerprint, StringComparison.Ordinal))
        {
            _lastFaultFingerprint = fingerprint;
            Debug.LogWarning($"Vulkan upscale bridge faulted during {stage} for {DescribeViewport()}: {ex.Message}");
        }

        DestroyInteropResources();
        SidecarDeviceOwned = false;
        // Clear the pending recreate reason so PrepareForFrame doesn't immediately re-enter
        // the recreate branch next frame and throw the same exception. Re-entry is gated to
        // explicit signals (resize, vendor change, snapshot refresh) via MarkNeedsRecreate.
        _pendingRecreateReason = null;
        TransitionState(EVulkanUpscaleBridgeState.Faulted, ex.Message, log: false);
        return _state;
    }

    /// <summary>
    /// Transitions the Vulkan upscale bridge to a new state, optionally logging the transition.
    /// </summary>
    /// <param name="newState">The new state to transition to.</param>
    /// <param name="reason">The reason for the state transition.</param>
    /// <param name="log">Whether to log the state transition.</param>
    private void TransitionState(EVulkanUpscaleBridgeState newState, string? reason, bool log = false)
    {
        if (_state == newState && string.Equals(_lastStateReason, reason, StringComparison.Ordinal))
            return;

        _state = newState;
        _lastStateReason = reason;

        if (!log)
            return;

        string reasonText = string.IsNullOrWhiteSpace(reason) ? "<none>" : reason;
        string logFingerprint = string.Concat(
            newState.ToString(),
            "|",
            reasonText,
            "|",
            _resourceGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!_loggedStateFingerprints.Add(logFingerprint))
            return;

        XREngine.Debug.Rendering(
            "[RenderDiag] VulkanUpscaleBridge VP={0} state={1} queue={2} reason='{3}' sidecarOwned={4}",
            DescribeViewport(),
            _state,
            QueueModel,
            reasonText,
            SidecarDeviceOwned ? 1 : 0);
    }

    /// <summary>
    /// Describes the current viewport, including its window title, index, and dimensions.
    /// </summary>
    /// <returns>A string describing the current viewport.</returns>
    private string DescribeViewport()
    {
        string windowTitle = _viewport.Window?.Window?.Title ?? "Viewport";
        return $"{windowTitle}#{_viewport.Index}:{_viewport.Width}x{_viewport.Height}/{_viewport.InternalWidth}x{_viewport.InternalHeight}";
    }

    /// <summary>
    /// Builds the frame resources required for the Vulkan upscale bridge based on the current render pipeline instance.
    /// </summary>
    /// <param name="pipeline">The current render pipeline instance.</param>
    /// <returns>A new instance of <see cref="VulkanUpscaleBridgeFrameResources"/> containing the frame resources.</returns>
    private static VulkanUpscaleBridgeFrameResources BuildFrameResources(XRRenderPipelineInstance pipeline)
    {
        EAntiAliasingMode antiAliasingMode = pipeline.EffectiveAntiAliasingModeThisFrame
            ?? RuntimeEngine.EffectiveSettings.AntiAliasingMode;
        uint msaaSampleCount = Math.Max(1u, pipeline.EffectiveMsaaSampleCountThisFrame ?? RuntimeEngine.EffectiveSettings.MsaaSampleCount);
        bool stereo = pipeline.Pipeline switch
        {
            DefaultRenderPipeline { Stereo: true } => true,
            DefaultRenderPipeline2 { Stereo: true } => true,
            _ => false,
        };

        return new VulkanUpscaleBridgeFrameResources(
            DisplayWidth: Math.Max(1, pipeline.LastWindowViewport?.Width ?? 0),
            DisplayHeight: Math.Max(1, pipeline.LastWindowViewport?.Height ?? 0),
            InternalWidth: Math.Max(1, pipeline.LastWindowViewport?.InternalWidth ?? 0),
            InternalHeight: Math.Max(1, pipeline.LastWindowViewport?.InternalHeight ?? 0),
            OutputHdr: pipeline.EffectiveOutputHDRThisFrame ?? RuntimeEngine.Rendering.Settings.OutputHDR,
            AntiAliasingMode: antiAliasingMode,
            MsaaSampleCount: antiAliasingMode == EAntiAliasingMode.Msaa ? msaaSampleCount : 1u,
            Stereo: stereo,
            EnableDlss: RuntimeEngine.EffectiveSettings.EnableNvidiaDlss || antiAliasingMode == EAntiAliasingMode.Dlaa,
            DlssQuality: RuntimeEngine.EffectiveSettings.DlssQuality,
            EnableXess: antiAliasingMode != EAntiAliasingMode.Dlaa && RuntimeEngine.EffectiveSettings.EnableIntelXess,
            XessQuality: RuntimeEngine.EffectiveSettings.XessQuality,
            QueueModel: RuntimeEngine.Rendering.VulkanUpscaleBridgeQueueModel);
    }

    /// <summary>
    /// Determines the availability of the Vulkan upscale bridge based on the current capability snapshot and frame resources.
    /// </summary>
    /// <param name="snapshot">The current capability snapshot of the Vulkan upscale bridge.</param>
    /// <param name="frameResources">The frame resources for the current frame.</param>
    /// <param name="reason">The reason why the bridge is unavailable, if applicable.</param>
    /// <returns>The current state of the Vulkan upscale bridge.</returns>
    private EVulkanUpscaleBridgeState DetermineAvailability(
        global::XREngine.VulkanUpscaleBridgeCapabilitySnapshot snapshot,
        in VulkanUpscaleBridgeFrameResources frameResources,
        out string reason)
    {
        if (!RuntimeEngine.Rendering.VulkanUpscaleBridgeRequested)
        {
            reason = $"{RuntimeEngine.Rendering.VulkanUpscaleBridgeEnvVar}=0 disabled the OpenGL->Vulkan upscale bridge";
            return EVulkanUpscaleBridgeState.Disabled;
        }

        if (!frameResources.VendorRequested)
        {
            reason = "no vendor upscaler is enabled";
            return EVulkanUpscaleBridgeState.Disabled;
        }

        if (snapshot.WindowsOnly && !OperatingSystem.IsWindows())
        {
            reason = "bridge MVP is Windows only";
            return EVulkanUpscaleBridgeState.Unsupported;
        }

        if (_viewport.Window?.Renderer is not OpenGLRenderer)
        {
            reason = "bridge MVP only applies to OpenGL windows";
            return EVulkanUpscaleBridgeState.Unsupported;
        }

        if (snapshot.MonoViewportOnly && (_viewport.Window?.Viewports.Count ?? 0) != 1)
        {
            reason = "bridge MVP only supports a single viewport per window";
            return EVulkanUpscaleBridgeState.Unsupported;
        }

        if (frameResources.Stereo)
        {
            reason = "bridge MVP excludes stereo/XR pipelines";
            return EVulkanUpscaleBridgeState.Unsupported;
        }

        if (frameResources.OutputHdr && !snapshot.HdrSupported)
        {
            reason = "bridge HDR output is unavailable";
            return EVulkanUpscaleBridgeState.Unsupported;
        }

        if (!snapshot.HasRequiredOpenGlInterop)
        {
            reason = "required OpenGL external-memory/semaphore extensions are unavailable";
            return EVulkanUpscaleBridgeState.Unsupported;
        }

        if (!snapshot.VulkanProbeSucceeded)
        {
            reason = snapshot.ProbeFailureReason ?? "Vulkan bridge probe failed";
            return EVulkanUpscaleBridgeState.Unsupported;
        }

        if (!snapshot.HasRequiredVulkanInterop)
        {
            reason = "required Vulkan external-memory/semaphore import extensions are unavailable";
            return EVulkanUpscaleBridgeState.Unsupported;
        }

        if (snapshot.SamePhysicalGpu == false)
        {
            reason = snapshot.GpuIdentityReason ?? "OpenGL and Vulkan resolved to different physical GPUs";
            return EVulkanUpscaleBridgeState.Unsupported;
        }

        if (!TryResolveRequestedVendorRuntime(in frameResources, out reason))
            return EVulkanUpscaleBridgeState.Unsupported;

        reason = "bridge prerequisites satisfied";
        return EVulkanUpscaleBridgeState.Ready;
    }

    /// <summary>
    /// Attempts to resolve the requested vendor runtime for the Vulkan upscale bridge based on the current frame resources.
    /// </summary>
    /// <param name="frameResources">The frame resources for the current frame.</param>
    /// <param name="reason">The reason why the requested vendor runtime could not be resolved, if applicable.</param>
    /// <returns><c>true</c> if the requested vendor runtime is available; otherwise, <c>false</c>.</returns>
    private static bool TryResolveRequestedVendorRuntime(
        in VulkanUpscaleBridgeFrameResources frameResources,
        out string reason)
    {
        bool dlssSupported = frameResources.EnableDlss && NvidiaDlssManager.IsSupported;
        bool xessSupported = frameResources.EnableXess && IntelXessManager.IsSupported;
        if (dlssSupported || xessSupported)
        {
            reason = string.Empty;
            return true;
        }

        bool preferDlss = RuntimeEngine.Rendering.VulkanUpscaleBridgeSnapshot.DlssFirst;
        string? dlssFailure = frameResources.EnableDlss ? NvidiaDlssManager.LastError : null;
        string? xessFailure = frameResources.EnableXess ? IntelXessManager.LastError : null;
        reason = preferDlss
            ? dlssFailure ?? xessFailure ?? "No supported bridge vendor runtime is currently available."
            : xessFailure ?? dlssFailure ?? "No supported bridge vendor runtime is currently available.";
        return false;
    }

    /// <summary>
    /// Resolves the reason for a configuration change between two sets of Vulkan upscale bridge frame resources.
    /// </summary>
    /// <param name="previous">The previous frame resources.</param>
    /// <param name="current">The current frame resources.</param>
    /// <returns>A string describing the reason for the configuration change.</returns>
    private static string ResolveConfigurationChangeReason(
        in VulkanUpscaleBridgeFrameResources previous,
        in VulkanUpscaleBridgeFrameResources current)
    {
        if (previous.DisplayWidth != current.DisplayWidth || previous.DisplayHeight != current.DisplayHeight)
            return "viewport resized";

        if (previous.InternalWidth != current.InternalWidth || previous.InternalHeight != current.InternalHeight)
            return "internal resolution changed";

        if (previous.OutputHdr != current.OutputHdr)
            return "output HDR changed";

        if (previous.AntiAliasingMode != current.AntiAliasingMode || previous.MsaaSampleCount != current.MsaaSampleCount)
            return "anti-aliasing resources changed";

        if (previous.EnableDlss != current.EnableDlss || previous.EnableXess != current.EnableXess)
            return "vendor selection changed";

        if (previous.DlssQuality != current.DlssQuality || previous.XessQuality != current.XessQuality)
            return "vendor quality changed";

        if (previous.QueueModel != current.QueueModel)
            return "queue model changed";

        if (previous.Stereo != current.Stereo)
            return "stereo pipeline state changed";

        return "bridge configuration changed";
    }

    /// <summary>
    /// Recreates the interop resources for the Vulkan upscale bridge, optionally reusing existing frame slots if possible.
    /// </summary>
    /// <param name="renderer">The OpenGL renderer instance.</param>
    /// <param name="snapshot">The capability snapshot for the Vulkan upscale bridge.</param>
    /// <param name="frameResources">The current frame resources.</param>
    /// <param name="previousFrameResources">The previous frame resources, if available.</param>
    /// <param name="recreateReason">The reason for recreating the interop resources.</param>
    /// <exception cref="InvalidOperationException">Thrown if the Vulkan upscale bridge sidecar fails to create or recreate any interop frame slots.</exception>
    private void RecreateInteropResources(
        OpenGLRenderer renderer,
        global::XREngine.VulkanUpscaleBridgeCapabilitySnapshot snapshot,
        in VulkanUpscaleBridgeFrameResources frameResources,
        VulkanUpscaleBridgeFrameResources? previousFrameResources,
        string recreateReason)
    {
        if (_sidecar is not null
            && previousFrameResources is VulkanUpscaleBridgeFrameResources previous
            && CanRecreateFrameSlotsInPlace(in previous, in frameResources, recreateReason))
        {
            _frameSlots = _sidecar.RecreateFrameSlots(renderer, frameResources, SanitizeLabel(DescribeViewport()));
            _frameSlotIndex = _frameSlots.Length > 0 ? 0 : -1;
            unchecked
            {
                _resourceGeneration++;
            }

            if (_frameSlots.Length == 0)
                throw new InvalidOperationException("The Vulkan upscale bridge sidecar did not recreate any interop frame slots.");

            return;
        }

        DestroyInteropResources();

        _sidecar = new VulkanUpscaleBridgeSidecar(snapshot.OpenGlVendor, snapshot.OpenGlRenderer, in frameResources);
        _frameSlots = _sidecar.CreateFrameSlots(renderer, frameResources, SanitizeLabel(DescribeViewport()));
        _frameSlotIndex = _frameSlots.Length > 0 ? 0 : -1;
        unchecked
        {
            _resourceGeneration++;
        }

        if (_frameSlots.Length == 0)
            throw new InvalidOperationException("The Vulkan upscale bridge sidecar did not create any interop frame slots.");
    }

    /// <summary>
    /// Determines whether the interop frame slots can be recreated in place based on the previous and current frame resources and the reason for recreation.
    /// </summary>
    /// <param name="previous">The previous frame resources.</param>
    /// <param name="current">The current frame resources.</param>
    /// <param name="recreateReason">The reason for recreating the interop resources.</param>
    /// <returns>True if the frame slots can be recreated in place; otherwise, false.</returns>
    private static bool CanRecreateFrameSlotsInPlace(
        in VulkanUpscaleBridgeFrameResources previous,
        in VulkanUpscaleBridgeFrameResources current,
        string recreateReason)
        => previous.EnableDlss == current.EnableDlss
            && previous.EnableXess == current.EnableXess
            && previous.QueueModel == current.QueueModel
            && previous.Stereo == current.Stereo && IsFrameSlotOnlyRecreateReason(recreateReason);

    /// <summary>
    /// Determines whether the given reason for recreating interop resources only requires recreating the frame slots.
    /// </summary>
    /// <param name="recreateReason">The reason for recreating the interop resources.</param>
    /// <returns>True if only the frame slots need to be recreated; otherwise, false.</returns>
    private static bool IsFrameSlotOnlyRecreateReason(string recreateReason)
        => string.Equals(recreateReason, ViewportResizeReason, StringComparison.Ordinal)
            || string.Equals(recreateReason, InternalResolutionResizeReason, StringComparison.Ordinal)
            || string.Equals(recreateReason, "internal resolution changed", StringComparison.Ordinal)
            || string.Equals(recreateReason, "output HDR changed", StringComparison.Ordinal)
            || string.Equals(recreateReason, "anti-aliasing resources changed", StringComparison.Ordinal)
            || string.Equals(recreateReason, "vendor quality changed", StringComparison.Ordinal);

    /// <summary>
    /// Destroys the interop resources associated with the Vulkan upscale bridge, including the sidecar and frame slots.
    /// </summary>
    private void DestroyInteropResources()
    {
        // Dispose of the sidecar if it exists.
        _sidecar?.Dispose();
        _sidecar = null;

        // Clear the frame slots and reset the frame slot index.
        _frameSlots = [];
        _frameSlotIndex = -1;
    }

    /// <summary>
    /// Advances to the next frame slot, wrapping around if necessary.
    /// </summary>
    /// <remarks>
    /// If there are no frame slots, the frame slot index is set to -1.
    /// </remarks>
    private void AdvanceFrameSlot()
    {
        if (_frameSlots.Length == 0)
        {
            _frameSlotIndex = -1;
            return;
        }

        if (_frameSlotIndex < 0)
        {
            _frameSlotIndex = 0;
            return;
        }

        _frameSlotIndex = (_frameSlotIndex + 1) % _frameSlots.Length;
    }

    /// <summary>
    /// Sanitizes the given label by replacing non-alphanumeric characters with dots and ensuring it is not empty.
    /// </summary>
    /// <param name="value">The label to sanitize.</param>
    /// <returns>The sanitized label.</returns>
    private static string SanitizeLabel(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        int length = 0;
        foreach (char c in value)
        {
            if (char.IsLetterOrDigit(c))
                buffer[length++] = c;
            else if (c is '#' or ':' or '/' or '\\' or ' ' or '-')
                buffer[length++] = '.';
        }

        return length == 0 ? "Viewport" : new string(buffer[..length]);
    }

    /// <summary>
    /// Tries to get the OpenGL texture binding ID for the given texture.
    /// </summary>
    /// <param name="renderer">The OpenGL renderer instance.</param>
    /// <param name="texture">The XR texture to get the binding for.</param>
    /// <param name="bindingId">The output binding ID if found, otherwise 0.</param>
    /// <returns>True if the binding ID was successfully retrieved, otherwise false.</returns>
    private static bool TryGetGlTextureBinding(OpenGLRenderer renderer, XRTexture texture, out uint bindingId)
    {
        bindingId = renderer.GenericToAPI<GLTexture2D>(texture)?.BindingId ?? 0u;
        return bindingId != 0;
    }
}
