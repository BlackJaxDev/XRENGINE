using System.Runtime.ExceptionServices;

namespace System.Collections.Generic;

public partial class EventList<T>
{
    internal void RemoveReservedOccurrence(EventListMutationAuthority<T> authority, int index, T item,
        Action validate, Action committed)
    {
        if (!ReferenceEquals(_mutationAuthority, authority))
            throw new InvalidOperationException("The removal requires this list's installed authority.");
        Exception? failure = null;
        bool removed = false;
        lock (authority.MutationGate)
        {
            validate();
            if (IsDestroyed || IsDestroyQueued || (uint)index >= (uint)_list.Count || !SameItem(_list[index], item))
                throw new InvalidOperationException("The reserved list occurrence changed before removal.");
            _lock!.EnterWriteLock();
            try
            {
                _list.RemoveAt(index);
                removed = true;
                authority.Removed(item);
                _mutationRevision++;
                committed();
            }
            catch (Exception error) { failure = error; }
            finally { _lock.ExitWriteLock(); }
        }
        if (removed)
            try { NotifyExactRemoval(item); }
            catch (Exception error) { failure ??= error; }
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
