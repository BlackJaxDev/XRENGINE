using System.Numerics;
using System.Threading;
using YamlDotNet.Serialization;

namespace XREngine.Rendering;

public partial class XRCamera
{
    private readonly object _temporalHistorySync = new();
    private ulong _temporalHistoryEpoch = 1UL;
    private Matrix4x4 _previousViewProjectionMatrixUnjittered = Matrix4x4.Identity;
    private Matrix4x4 _currentRecordedViewProjectionUnjittered = Matrix4x4.Identity;
    private ulong _lastRecordedRenderFrameId;
    private ulong _lastRecordedEpoch;
    private bool _hasPreviousViewProjectionMatrix;

    /// <summary>
    /// Identifies continuous camera motion independently of projection jitter.
    /// </summary>
    [YamlIgnore]
    public ulong TemporalHistoryEpoch => Volatile.Read(ref _temporalHistoryEpoch);

    /// <summary>
    /// The unjittered view-projection matrix from the previous frame.
    /// Falls back to the current unjittered view-projection matrix if no prior frame exists or after a cut.
    /// </summary>
    [YamlIgnore]
    public Matrix4x4 PreviousViewProjectionMatrixUnjittered
    {
        get
        {
            lock (_temporalHistorySync)
            {
                UpdateUnjitteredViewProjectionHistory();
                return _hasPreviousViewProjectionMatrix ? _previousViewProjectionMatrixUnjittered : ViewProjectionMatrixUnjittered;
            }
        }
    }

    /// <summary>
    /// True if continuous camera history is available from the prior frame without an epoch cut.
    /// </summary>
    [YamlIgnore]
    public bool HasPreviousViewProjectionMatrix
    {
        get
        {
            lock (_temporalHistorySync)
            {
                UpdateUnjitteredViewProjectionHistory();
                return _hasPreviousViewProjectionMatrix;
            }
        }
    }

    public void UpdateUnjitteredViewProjectionHistory()
    {
        lock (_temporalHistorySync)
        {
            ulong currentFrameId = RuntimeEngine.Rendering.State.RenderFrameId;
            if (currentFrameId != 0UL && currentFrameId == _lastRecordedRenderFrameId)
                return;

            ulong epoch = Volatile.Read(ref _temporalHistoryEpoch);
            Matrix4x4 currentVp = ViewProjectionMatrixUnjittered;

            bool epochMatches = _lastRecordedEpoch == epoch;
            bool frameAdvanced = (currentFrameId != 0UL && currentFrameId > _lastRecordedRenderFrameId)
                || (currentFrameId == 0UL && _lastRecordedRenderFrameId == 0UL && currentVp != _currentRecordedViewProjectionUnjittered && _currentRecordedViewProjectionUnjittered != Matrix4x4.Identity);

            if (epochMatches && frameAdvanced)
            {
                _previousViewProjectionMatrixUnjittered = _currentRecordedViewProjectionUnjittered;
                _hasPreviousViewProjectionMatrix = true;
            }
            else if (!epochMatches)
            {
                _previousViewProjectionMatrixUnjittered = currentVp;
                _hasPreviousViewProjectionMatrix = false;
            }

            _currentRecordedViewProjectionUnjittered = currentVp;
            _lastRecordedEpoch = epoch;
            if (currentFrameId != 0UL)
                _lastRecordedRenderFrameId = currentFrameId;
        }
    }

    /// <summary>
    /// Invalidates prior camera transforms after an instantaneous camera cut or
    /// authored reset. Smooth movement must preserve this epoch.
    /// </summary>
    public void InvalidateTemporalHistory()
        => InvalidateTemporalHistory(publishNotifications: true);

    private long _autoExposureResetCount;

    /// <summary>
    /// Resets the automatic exposure adaptation of this camera in every pipeline state that it has, so the next exposure
    /// update uses the metered value with no blend from the old exposure. Call it after an instantaneous cut or a jump in time,
    /// after <see cref="InvalidateTemporalHistory()"/>. The reset applies only to frames captured with the current
    /// <see cref="TemporalHistoryEpoch"/> or a later one, so a frame from before the invalidation that still renders does not
    /// use it. <see cref="InvalidateTemporalHistory()"/> does not reset exposure, because smooth exposure adaptation is not
    /// view history. A camera with no post-process state yet gets one first, so the first cut to a camera also resets its
    /// exposure; only that first call allocates.
    /// </summary>
    public void ResetAutoExposureHistory()
    {
        Interlocked.Increment(ref _autoExposureResetCount);
        _ = GetActivePostProcessState();
        PostProcessStates.ResetAutoExposureHistory(TemporalHistoryEpoch);
    }

    /// <summary>The number of <see cref="ResetAutoExposureHistory"/> calls on this camera.</summary>
    internal long AutoExposureResetCount => Interlocked.Read(ref _autoExposureResetCount);

    /// <summary>
    /// Invalidates prior camera transforms, like <see cref="InvalidateTemporalHistory()"/>. With
    /// <paramref name="publishNotifications"/> false, no property change event runs, so a caller on another thread or a caller
    /// in each slow frame does not run listeners or allocate event arguments.
    /// </summary>
    public void InvalidateTemporalHistory(bool publishNotifications)
    {
        lock (_temporalHistorySync)
        {
            ulong next = _temporalHistoryEpoch == ulong.MaxValue ? 1UL : _temporalHistoryEpoch + 1UL;
            SetField(ref _temporalHistoryEpoch, next, publishNotifications, nameof(TemporalHistoryEpoch));
            _hasPreviousViewProjectionMatrix = false;
            _previousViewProjectionMatrixUnjittered = ViewProjectionMatrixUnjittered;
            _currentRecordedViewProjectionUnjittered = ViewProjectionMatrixUnjittered;
        }
    }
}
