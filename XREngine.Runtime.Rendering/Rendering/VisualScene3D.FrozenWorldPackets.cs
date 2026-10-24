using System.Threading;
using XREngine.Rendering.UI;

namespace XREngine.Scene;

public partial class VisualScene3D
{
    private UIWorldCollectPacketSet? _frozenWorldPacketSetForCollection;
    private int _frozenWorldPacketCaptureActive;
    private int _frozenWorldPacketCaptureReentered;

    internal UIWorldCollectPacketSet? FrozenWorldPacketSetForCollection
        => Volatile.Read(ref _frozenWorldPacketSetForCollection);

    internal UIWorldCollectPacketSet? RetainFrozenWorldPacketSetForCollection()
    {
        UIWorldCollectPacketSet? selected = FrozenWorldPacketSetForCollection;
        long generation = selected?.Generation ?? 0L;
        if (selected is null || !selected.TryRetain(generation))
            return null;
        if (ReferenceEquals(selected, FrozenWorldPacketSetForCollection) &&
            selected.Generation == generation)
            return selected;
        selected.Release();
        return null;
    }

    internal bool HasFrozenWorldPacketSources => _frozenWorldPacketSources.Count != 0;

    /// <summary>Captures registered world UI sources before light and view collection.</summary>
    internal bool TryCaptureFrozenWorldPackets(UIWorldCollectPacketSet packetSet,
        IRuntimeWorldContext expectedWorld)
    {
        if (Interlocked.CompareExchange(ref _frozenWorldPacketCaptureActive, 1, 0) != 0)
        {
            Volatile.Write(ref _frozenWorldPacketCaptureReentered, 1);
            return false;
        }
        try
        {
            Volatile.Write(ref _frozenWorldPacketCaptureReentered, 0);
            int sourceCount = _frozenWorldPacketSources.Count;
            long sourceRevision = _frozenWorldPacketSourceRevision;
            for (int index = 0; index < sourceCount; index++)
            {
                IFrozenWorldPacketSource source = _frozenWorldPacketSources[index];
                if (!source.IsInWorld(expectedWorld))
                    return false;
                if (!source.TryAcquireCollectPacket(expectedWorld, out UIPublication? packet))
                    return false;
                if (packet is not null)
                {
                    object key;
                    try { key = source.PacketKey; }
                    catch
                    {
                        packet.Release();
                        throw;
                    }
                    if (!packetSet.TryAdd(key, packet))
                        return false;
                }
                if (sourceCount != _frozenWorldPacketSources.Count ||
                    sourceRevision != _frozenWorldPacketSourceRevision)
                    return false;
            }
            for (int index = 0; index < sourceCount; index++)
                if (!_frozenWorldPacketSources[index].IsInWorld(expectedWorld))
                    return false;
            return sourceCount == _frozenWorldPacketSources.Count &&
                sourceRevision == _frozenWorldPacketSourceRevision &&
                Volatile.Read(ref _frozenWorldPacketCaptureReentered) == 0 &&
                packetSet.TrySeal();
        }
        finally
        {
            Volatile.Write(ref _frozenWorldPacketCaptureActive, 0);
        }
    }

    internal void SetFrozenWorldPacketSetForCollection(UIWorldCollectPacketSet? packetSet)
    {
        if (packetSet is not null && !packetSet.IsSealed)
            throw new InvalidOperationException("The world UI packet set is not sealed.");
        Volatile.Write(ref _frozenWorldPacketSetForCollection, packetSet);
    }
}
