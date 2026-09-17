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
    {
        lock (_temporalHistorySync)
        {
            ulong next = _temporalHistoryEpoch == ulong.MaxValue ? 1UL : _temporalHistoryEpoch + 1UL;
            SetField(ref _temporalHistoryEpoch, next, nameof(TemporalHistoryEpoch));
            _hasPreviousViewProjectionMatrix = false;
            _previousViewProjectionMatrixUnjittered = ViewProjectionMatrixUnjittered;
            _currentRecordedViewProjectionUnjittered = ViewProjectionMatrixUnjittered;
        }
    }
}
