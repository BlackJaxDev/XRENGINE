using System.Diagnostics;
using System.Globalization;
using XREngine.Rendering;

namespace XREngine.Editor.CutsceneRecording;

/// <summary>
/// Coordinates deterministic frame-by-frame cutscene evaluation and render-thread readback scheduling.
/// Capture indexes from a session are local to the active segment; the controller adds the segment
/// offset to get the global cutscene frame, so consecutive segments continue one timeline.
/// </summary>
/// <remarks>
/// A frame is captured only after the render thread shows the applied cutscene state. The update thread applies
/// the state, then the controller records the latest requested visibility-collect generation. The capture waits
/// for a render frame that started after the apply and that consumed a collect generation requested after it.
/// This fence replaces a fixed number of settle frames: it waits exactly as long as the frame pipeline needs.
/// The controller also counts the render frames that each gate rejected, so a slow capture shows its bottleneck.
/// </remarks>
internal sealed class CutsceneLockstepController(XRViewport viewport, CutsceneRecordingScene scene, int totalFrames)
{
    /// <summary>
    /// Settle frames for a host that reports no visibility-collect generations. Two render frames cover one
    /// collect pass that overlapped the apply and one collect pass that started after it.
    /// </summary>
    private const ulong FallbackSettleRenderFrames = 2UL;

    private readonly object _gate = new();
    private int _segmentOffset;
    private int _segmentFrames;
    private int _targetFrameIndex;
    private int _appliedFrameIndex = -1;
    private int _pendingApplyFrame = -1;
    private ulong _appliedRenderFrameId;
    private long _appliedCollectGeneration;
    private ulong _scheduledAtRenderFrameId;
    private bool _readyToCapture;
    private bool _detached;

    private long _capturedFrames;
    private long _applyWaitRenderFrames;
    private long _visibilityWaitRenderFrames;
    private long _gpuDataWaitRenderFrames;
    private long _readbackWaitRenderFrames;
    private long _applyTicks;
    private long _maxApplyTicks;
    private bool _usedFallbackSettle;

    public void Attach(int segmentOffset, int segmentFrames)
    {
        lock (_gate)
        {
            _detached = false;
            _segmentOffset = segmentOffset;
            _segmentFrames = Math.Min(segmentFrames, totalFrames - segmentOffset);
            _targetFrameIndex = 0;
            _appliedFrameIndex = -1;
            _pendingApplyFrame = -1;
            _scheduledAtRenderFrameId = 0UL;
            _readyToCapture = false;
        }

        ApplyFrameOnUpdateThread(0);
    }

    public void Detach()
    {
        lock (_gate)
        {
            _detached = true;
            _pendingApplyFrame = -1;
            _readyToCapture = false;
        }
    }

    public bool ShouldCaptureFrame(int scheduledFrameCount)
    {
        int frameToEnqueue = -1;
        lock (_gate)
        {
            if (_detached)
                return false;

            ulong currentRenderFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
            if (_pendingApplyFrame >= 0 && currentRenderFrameId > _scheduledAtRenderFrameId)
            {
                frameToEnqueue = _pendingApplyFrame;
                _pendingApplyFrame = -1;
            }

            if (frameToEnqueue < 0)
            {
                if (!_readyToCapture ||
                    scheduledFrameCount != _targetFrameIndex ||
                    _appliedFrameIndex != scheduledFrameCount)
                {
                    _applyWaitRenderFrames++;
                    return false;
                }

                if (!HasRenderedAppliedState(currentRenderFrameId))
                {
                    _visibilityWaitRenderFrames++;
                    return false;
                }

                if (!scene.IsReadyForCapture)
                {
                    _gpuDataWaitRenderFrames++;
                    return false;
                }
            }
            else
            {
                _applyWaitRenderFrames++;
            }
        }

        if (frameToEnqueue >= 0)
        {
            Engine.EnqueueUpdateThreadTask(() => ApplyFrameOnUpdateThread(frameToEnqueue));
            return false;
        }

        if (!viewport.TryEnterRenderPipelineReadbackScope(out IDisposable? probeScope))
        {
            lock (_gate)
                _readbackWaitRenderFrames++;
            return false;
        }

        probeScope?.Dispose();
        lock (_gate)
            _capturedFrames++;
        return true;
    }

    public void OnFrameScheduled(int captureIndex)
    {
        lock (_gate)
        {
            _readyToCapture = false;
            int nextFrame = captureIndex + 1;
            _targetFrameIndex = nextFrame;
            if (_detached || nextFrame >= _segmentFrames)
            {
                _pendingApplyFrame = -1;
                return;
            }

            // Defer advancing the cutscene until the next render frame begins so end-of-frame UI compositing
            // and readback on the current render frame continue reading the active shot camera's render target.
            _pendingApplyFrame = nextFrame;
            _scheduledAtRenderFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
        }
    }

    /// <summary>
    /// Formats the gate statistics of every segment so far: the captured frame count, the average number of
    /// render frames that each gate rejected per captured frame, and the apply time on the update thread.
    /// </summary>
    public string FormatStatistics()
    {
        lock (_gate)
        {
            double captured = Math.Max(1L, _capturedFrames);
            double applyMs = _applyTicks * 1000.0 / Stopwatch.Frequency;
            double maxApplyMs = _maxApplyTicks * 1000.0 / Stopwatch.Frequency;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"lockstep(captured={_capturedFrames}, waitPerFrame[apply={_applyWaitRenderFrames / captured:F2}, " +
                $"visibility={_visibilityWaitRenderFrames / captured:F2}, gpuData={_gpuDataWaitRenderFrames / captured:F2}, " +
                $"readback={_readbackWaitRenderFrames / captured:F2}], applyMsAvg={applyMs / captured:F1}, " +
                $"applyMsMax={maxApplyMs:F1}, fence={(_usedFallbackSettle ? "settle-frames" : "collect-generation")})");
        }
    }

    /// <summary>
    /// Returns true when the render frame that just finished shows the applied cutscene state: it started after
    /// the apply finished, and its visible-object package comes from a collect pass that the host requested after
    /// the apply. A host without collect generations uses <see cref="FallbackSettleRenderFrames"/> instead.
    /// </summary>
    private bool HasRenderedAppliedState(ulong currentRenderFrameId)
    {
        if (currentRenderFrameId <= _appliedRenderFrameId)
            return false;

        if (_appliedCollectGeneration <= 0L)
        {
            _usedFallbackSettle = true;
            return currentRenderFrameId >= _appliedRenderFrameId + FallbackSettleRenderFrames;
        }

        return RuntimeEngine.Rendering.Stats.FrameLifecycle.ConsumedCollectGeneration > _appliedCollectGeneration;
    }

    private void ApplyFrameOnUpdateThread(int frameToApply)
    {
        int globalFrame;
        lock (_gate)
        {
            if (_detached || frameToApply >= _segmentFrames || _appliedFrameIndex == frameToApply)
                return;
            globalFrame = _segmentOffset + frameToApply;
        }

        long applyStart = Stopwatch.GetTimestamp();
        scene.ApplyFrame(globalFrame);
        long applyTicks = Stopwatch.GetTimestamp() - applyStart;

        lock (_gate)
        {
            if (_detached)
                return;

            _appliedFrameIndex = frameToApply;
            _appliedRenderFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
            // A collect pass requests its generation just before it starts, so any later generation began after this apply.
            _appliedCollectGeneration = RuntimeEngine.Rendering.Stats.FrameLifecycle.RequestedCollectGeneration;
            _applyTicks += applyTicks;
            _maxApplyTicks = Math.Max(_maxApplyTicks, applyTicks);
            _readyToCapture = true;
        }
    }
}
