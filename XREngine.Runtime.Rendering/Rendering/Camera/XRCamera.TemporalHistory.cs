using System.Threading;
using YamlDotNet.Serialization;

namespace XREngine.Rendering;

public partial class XRCamera
{
    private readonly object _temporalHistorySync = new();
    private ulong _temporalHistoryEpoch = 1UL;

    /// <summary>
    /// Identifies continuous camera motion independently of projection jitter.
    /// </summary>
    [YamlIgnore]
    public ulong TemporalHistoryEpoch => Volatile.Read(ref _temporalHistoryEpoch);

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
        }
    }
}
