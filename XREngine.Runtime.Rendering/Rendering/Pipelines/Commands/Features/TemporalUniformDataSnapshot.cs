using System.Threading;

namespace XREngine.Rendering.Pipelines.Commands;

/// <summary>
/// Publishes a coherent temporal-uniform snapshot without making render-thread
/// readers contend with the temporal-state writer.
/// </summary>
internal sealed class TemporalUniformDataSnapshot
{
    private readonly object _writerSync = new();
    private int _version;
    private int _hasValue;
    private VPRC_TemporalAccumulationPass.TemporalUniformData _data;

    /// <summary>
    /// Atomically publishes a complete temporal-uniform value.
    /// </summary>
    public void Publish(in VPRC_TemporalAccumulationPass.TemporalUniformData data)
    {
        lock (_writerSync)
        {
            int writeVersion = Interlocked.Increment(ref _version);
            try
            {
                _data = data;
                Volatile.Write(ref _hasValue, 1);
            }
            finally
            {
                // Readers may consume the snapshot only after the complete
                // large struct is visible under an even version.
                Volatile.Write(ref _version, unchecked(writeVersion + 1));
            }
        }
    }

    /// <summary>
    /// Reads the most recently published complete value.
    /// </summary>
    public bool TryRead(out VPRC_TemporalAccumulationPass.TemporalUniformData data)
    {
        if (Volatile.Read(ref _hasValue) == 0)
        {
            data = default;
            return false;
        }

        SpinWait spinWait = default;
        while (true)
        {
            int version = Volatile.Read(ref _version);
            if ((version & 1) != 0)
            {
                spinWait.SpinOnce();
                continue;
            }

            data = _data;
            if (version == Volatile.Read(ref _version))
                return true;

            spinWait.SpinOnce();
        }
    }
}
