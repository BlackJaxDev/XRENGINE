using System.Threading;

namespace XREngine.Rendering.UI;

/// <summary>Holds one bounded world UI packet selection across collect and render work.</summary>
internal sealed class UIWorldCollectPacketSet
{
    internal const int MaximumPackets = 64;
    private readonly object?[] _keys = new object?[MaximumPackets];
    private readonly UIPublication?[] _packets = new UIPublication?[MaximumPackets];
    private int _count;
    private int _claims;
    private long _generation;
    private bool _sealed;
    private bool _failed;
    private int _collectionFailed;

    internal int Count => _count;
    internal long Generation => Volatile.Read(ref _generation);
    internal bool Failed => _failed || Volatile.Read(ref _collectionFailed) != 0;
    internal bool IsSealed => _sealed;

    internal bool TryBegin(long generation = 1)
    {
        if (generation <= 0)
            return false;
        if (Interlocked.CompareExchange(ref _claims, 1, 0) != 0)
            return false;
        Volatile.Write(ref _generation, generation);
        _count = 0;
        _failed = false;
        _collectionFailed = 0;
        _sealed = false;
        return true;
    }

    /// <summary>Takes the caller's packet claim, also on duplicate or failure.</summary>
    internal bool TryAdd(object key, UIPublication packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (key is null)
        {
            packet.Release();
            throw new ArgumentNullException(nameof(key));
        }
        if (Volatile.Read(ref _claims) != 1 || _sealed || _failed)
        {
            packet.Release();
            return false;
        }
        for (int index = 0; index < _count; index++)
            if (ReferenceEquals(_keys[index], key))
            {
                bool same = ReferenceEquals(_packets[index], packet);
                packet.Release();
                _failed |= !same;
                return same;
            }
        if (_count == MaximumPackets)
        {
            packet.Release();
            _failed = true;
            return false;
        }
        _keys[_count] = key;
        _packets[_count++] = packet;
        return true;
    }

    internal bool TrySeal()
    {
        if (Volatile.Read(ref _claims) != 1 || _failed || _sealed)
            return false;
        _sealed = true;
        return true;
    }

    internal bool TryGet(object key, out UIPublication? packet)
    {
        if (!_sealed || Failed || Volatile.Read(ref _claims) <= 0)
        {
            packet = null;
            return false;
        }
        for (int index = 0; index < _count; index++)
            if (ReferenceEquals(_keys[index], key))
            {
                packet = _packets[index];
                return packet is not null;
            }
        packet = null;
        return false;
    }

    internal bool TryRetain()
    {
        int claims;
        do
        {
            claims = Volatile.Read(ref _claims);
            if (claims <= 0 || !_sealed || Failed)
                return false;
        } while (Interlocked.CompareExchange(ref _claims, checked(claims + 1), claims) != claims);
        return true;
    }

    internal bool TryRetain(long expectedGeneration)
    {
        if (expectedGeneration <= 0 || Generation != expectedGeneration || !TryRetain())
            return false;
        if (Generation == expectedGeneration)
            return true;
        Release();
        return false;
    }

    internal void Release() => ReleaseCore(null);

    internal void MarkCollectionFailed() => Volatile.Write(ref _collectionFailed, 1);

    internal void ReleaseForTesting(Action onRetiring) => ReleaseCore(onRetiring);

    private void ReleaseCore(Action? onRetiring)
    {
        int claims;
        do
        {
            claims = Volatile.Read(ref _claims);
            if (claims <= 0)
                throw new InvalidOperationException("A world UI packet set was released twice.");
        } while (Interlocked.CompareExchange(ref _claims,
            claims == 1 ? -1 : claims - 1, claims) != claims);
        if (claims != 1)
            return;
        List<Exception>? failures = null;
        try
        {
            try { onRetiring?.Invoke(); }
            catch (Exception error) { (failures ??= []).Add(error); }
            int count = _count;
            _count = 0;
            _sealed = false;
            for (int index = 0; index < count; index++)
            {
                UIPublication? packet = _packets[index];
                _packets[index] = null;
                _keys[index] = null;
                try { packet?.Release(); }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
        }
        finally
        {
            Volatile.Write(ref _claims, 0);
        }
        if (failures is not null)
            throw new AggregateException("World UI packet claims could not retire.", failures);
    }
}
