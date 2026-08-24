using System.Threading;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Frame-publication-owned variable payload storage for render packets.
/// Packet headers retain only numeric ranges into these arrays; diagnostic text
/// is deliberately isolated from the worker-facing packet header.
/// </summary>
internal sealed class RenderPacketPayloadArena
{
    private DrawPacket[] _draws = new DrawPacket[64];
    private DispatchPacket[] _dispatches = new DispatchPacket[16];
    private string[] _targetNames = new string[16];
    private int _drawCount;
    private int _dispatchCount;
    private int _targetNameCount;
    private int _leaseCount;

    internal bool IsLeased => Volatile.Read(ref _leaseCount) != 0;

    internal void EnsurePublicationCapacity(int drawCount, int dispatchCount, int targetNameCount)
    {
        EnsureCapacity(ref _draws, drawCount);
        EnsureCapacity(ref _dispatches, dispatchCount);
        EnsureCapacity(ref _targetNames, targetNameCount);
    }

    internal int AppendDraws(ReadOnlySpan<DrawPacket> draws)
    {
        int start = _drawCount;
        EnsureCapacity(ref _draws, _drawCount + draws.Length);
        draws.CopyTo(_draws.AsSpan(_drawCount));
        _drawCount += draws.Length;
        return start;
    }

    internal int AppendDispatches(ReadOnlySpan<DispatchPacket> dispatches)
    {
        int start = _dispatchCount;
        EnsureCapacity(ref _dispatches, _dispatchCount + dispatches.Length);
        dispatches.CopyTo(_dispatches.AsSpan(_dispatchCount));
        _dispatchCount += dispatches.Length;
        return start;
    }

    internal int AppendTargetName(string targetName)
    {
        EnsureCapacity(ref _targetNames, _targetNameCount + 1);
        int index = _targetNameCount++;
        _targetNames[index] = targetName;
        return index;
    }

    internal ref readonly DrawPacket GetDraw(int index)
        => ref _draws[index];

    internal ref readonly DispatchPacket GetDispatch(int index)
        => ref _dispatches[index];

    internal string GetTargetName(int index)
        => _targetNames[index];

    internal void AcquireLease()
        => Interlocked.Increment(ref _leaseCount);

    internal void ReleaseLease()
    {
        if (Interlocked.Decrement(ref _leaseCount) >= 0)
            return;

        Interlocked.Increment(ref _leaseCount);
        throw new InvalidOperationException("Render-packet payload-arena lease underflow.");
    }

    /// <summary>Reuses this frame arena only after every packet publication retired.</summary>
    internal void ResetForPublication()
    {
        if (IsLeased)
            throw new InvalidOperationException("A leased render-packet payload arena cannot be reset.");

        if (_targetNameCount > 0)
            Array.Clear(_targetNames, 0, _targetNameCount);
        _drawCount = 0;
        _dispatchCount = 0;
        _targetNameCount = 0;
    }

    private static void EnsureCapacity<T>(ref T[] storage, int required)
    {
        if (storage.Length >= required)
            return;

        int capacity = Math.Max(required, storage.Length == 0 ? 16 : storage.Length * 2);
        Array.Resize(ref storage, capacity);
    }
}
