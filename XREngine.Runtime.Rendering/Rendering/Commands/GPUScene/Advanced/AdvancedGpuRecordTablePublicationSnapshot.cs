namespace XREngine.Rendering.Commands;

/// <summary>
/// Preallocated immutable copy of one table's structural publication. The
/// publication-ring owner creates one snapshot per retained ring entry, then
/// asks the live table to capture into it while sealing that entry.
/// </summary>
public sealed class AdvancedGpuRecordTablePublicationSnapshot<T> where T : unmanaged
{
    private readonly AdvancedGpuRecordPublicationDelta[] _deltas;
    private readonly AdvancedGpuHandleRemap[] _remaps;
    private int _deltaCount;
    private int _remapCount;

    public AdvancedGpuRecordTablePublicationSnapshot(
        int deltaCapacity,
        int remapCapacity)
    {
        if (deltaCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(deltaCapacity));
        if (remapCapacity < 0)
            throw new ArgumentOutOfRangeException(nameof(remapCapacity));

        _deltas = new AdvancedGpuRecordPublicationDelta[deltaCapacity];
        _remaps = new AdvancedGpuHandleRemap[remapCapacity];
    }

    public ulong Sequence { get; private set; }

    public ReadOnlySpan<AdvancedGpuRecordPublicationDelta> Deltas
        => _deltas.AsSpan(0, _deltaCount);

    public ReadOnlySpan<AdvancedGpuHandleRemap> Remaps
        => _remaps.AsSpan(0, _remapCount);

    internal bool TryCapture(
        ulong sequence,
        ReadOnlySpan<AdvancedGpuRecordPublicationDelta> deltas,
        ReadOnlySpan<AdvancedGpuHandleRemap> remaps)
    {
        if (sequence == 0u ||
            deltas.Length > _deltas.Length ||
            remaps.Length > _remaps.Length)
        {
            return false;
        }

        deltas.CopyTo(_deltas);
        remaps.CopyTo(_remaps);
        _deltaCount = deltas.Length;
        _remapCount = remaps.Length;
        Sequence = sequence;
        return true;
    }
}
