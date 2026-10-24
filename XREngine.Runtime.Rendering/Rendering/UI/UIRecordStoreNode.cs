using System.Threading;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

/// <summary>Owns one immutable record page or a set of child nodes.</summary>
internal sealed class UIRecordStoreNode<T> where T : struct
{
    internal readonly UIRecordStoreNode<T>?[]? Children;
    internal readonly T[]? Values;
    private readonly Action<T>? _releaseRecord;
    private int _claims = 1;

    internal UIRecordStoreNode(UIRecordStoreNode<T>?[] children) => Children = children;

    internal UIRecordStoreNode(ReadOnlySpan<T> values, Action<T>? retainRecord,
        Action<T>? releaseRecord)
    {
        Values = values.ToArray();
        _releaseRecord = releaseRecord;
        int retained = 0;
        try
        {
            if (retainRecord is not null)
                for (; retained < Values.Length; retained++)
                {
                    retainRecord(Values[retained]);
                    UIRuntimeCounters.Add(UIWorkCounter.PublicationRecordClaimsRetained);
                }
        }
        catch (Exception failure)
        {
            List<Exception>? cleanupFailures = null;
            if (releaseRecord is not null)
                for (int index = 0; index < retained; index++)
                    try
                    {
                        UIRuntimeCounters.Add(UIWorkCounter.PublicationRecordClaimsReleased);
                        releaseRecord(Values[index]);
                    }
                    catch (Exception cleanupFailure)
                    {
                        (cleanupFailures ??= []).Add(cleanupFailure);
                    }
            if (cleanupFailures is not null)
            {
                cleanupFailures.Insert(0, failure);
                throw new AggregateException("A UI record page retain and its cleanup failed.", cleanupFailures);
            }
            throw;
        }
        UIRuntimeCounters.Add(UIWorkCounter.PublicationRecordCellsCopied, Values.Length);
    }

    internal void Retain()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _claims);
            ObjectDisposedException.ThrowIf(current == 0, this);
        } while (Interlocked.CompareExchange(ref _claims, checked(current + 1), current) != current);
    }

    internal void Release()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _claims);
            if (current == 0)
                throw new InvalidOperationException("A UI record node was released twice.");
        } while (Interlocked.CompareExchange(ref _claims, current - 1, current) != current);
        if (current != 1)
            return;

        List<Exception>? failures = null;
        if (Children is { } children)
        {
            foreach (UIRecordStoreNode<T>? child in children)
                try { child?.Release(); }
                catch (Exception failure) { (failures ??= []).Add(failure); }
        }
        else if (Values is { } values && _releaseRecord is not null)
        {
            foreach (T value in values)
                try
                {
                    UIRuntimeCounters.Add(UIWorkCounter.PublicationRecordClaimsReleased);
                    _releaseRecord(value);
                }
                catch (Exception failure) { (failures ??= []).Add(failure); }
        }
        if (failures is not null)
            throw new AggregateException("UI record node cleanup failed.", failures);
    }
}
