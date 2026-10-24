using System.Runtime.ExceptionServices;
using XREngine.Core;

namespace System.Collections.Generic;

public partial class EventList<T>
{
    /// <summary>
    /// Removes one exact entry without a pre-change veto. Reference types use reference identity.
    /// All removal observers run after the entry is removed, including during a batch update.
    /// The first observer failure is reported after the remaining observers run.
    /// </summary>
    public bool RemoveExactNonVetoable(T item)
    {
        if (_mutationAuthority is not null)
            return GuardedRemove(item, null, reportRemoved: true, reportModified: true, nonVetoable: true);
        bool removed = false;
        ReaderWriterLockSlim? listLock = _lock;
        listLock?.EnterWriteLock();
        try
        {
            for (int index = 0; index < _list.Count; index++)
            {
                bool matches = typeof(T).IsValueType
                    ? EqualityComparer<T>.Default.Equals(_list[index], item)
                    : ReferenceEquals(_list[index], item);
                if (!matches)
                    continue;
                _list.RemoveAt(index);
                removed = true;
                break;
            }
        }
        finally { listLock?.ExitWriteLock(); }
        if (!removed)
            return false;

        NotifyExactRemoval(item);
        return true;
    }

    private void NotifyExactRemoval(T item)
    {
        SingleHandler? removedHandlers = PostRemoved;
        SingleHandler? anythingRemovedHandlers = PostAnythingRemoved;
        Action? modifiedHandlers = PostModified;
        TCollectionChangedEventHandler<T>? changedHandlers = CollectionChanged;
        Exception? failure = null;
        NotifyRemovalObservers(removedHandlers, handler => handler(item), ref failure);
        NotifyRemovalObservers(anythingRemovedHandlers, handler => handler(item), ref failure);
        NotifyRemovalObservers(modifiedHandlers, handler => handler(), ref failure);
        if (changedHandlers is not null)
        {
            var change = new TCollectionChangedEventArgs<T>(ECollectionChangedAction.Remove, item);
            NotifyRemovalObservers(changedHandlers, handler => handler(this, change), ref failure);
        }
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void NotifyRemovalObservers<THandler>(THandler? handlers,
        Action<THandler> notify, ref Exception? failure) where THandler : Delegate
    {
        if (handlers is null)
            return;
        // This lifecycle path allocates an observer snapshot so a failed callback cannot skip cleanup.
        foreach (Delegate handler in handlers.GetInvocationList())
            try { notify((THandler)handler); }
            catch (Exception exception) { failure ??= exception; }
    }
}
