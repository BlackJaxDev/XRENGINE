using System.ComponentModel;
using System.Text.Json.Serialization;
using MemoryPack;
using XREngine.Core;
using XREngine.Data.Core;
using YamlDotNet.Serialization;

namespace System.Collections.Generic;

public partial class EventList<T>
{
    [NonSerialized]
    private ulong _mutationIntentRevision;

    /// <summary>Changes before each mutation attempt, including rejected and equal writes.</summary>
    [Browsable(false), YamlIgnore, JsonIgnore, MemoryPackIgnore]
    public ulong MutationIntentRevision => Volatile.Read(ref _mutationIntentRevision);

    private ulong AdvanceMutationIntent()
    {
        while (true)
        {
            ulong current = MutationIntentRevision;
            if (current == ulong.MaxValue)
                throw new InvalidOperationException("The list mutation intent cannot advance.");
            if (Interlocked.CompareExchange(ref _mutationIntentRevision, current + 1, current) == current)
                return current + 1;
        }
    }

    /// <summary>
    /// Writes one ordinary list slot after each callback confirms its exact owner.
    /// The caller must own the mutation thread. This method does not admit structural lists.
    /// A veto returns false; a callback failure can leave the new value committed.
    /// </summary>
    public bool SetIndexValidated<TState>(int index, T expectedValue, T value,
        TState state, Action<TState> requireCurrent)
    {
        ulong revision = AdvanceMutationIntent();
        ArgumentNullException.ThrowIfNull(requireCurrent);
        if (_mutationAuthority is not null)
            throw new InvalidOperationException("A structural list requires its installed mutation authority.");
        ObjectConstructionIdentity identity = ObjectConstructionIdentity.Capture(this);
        ReaderWriterLockSlim? listLock = _lock;
        int count = Count;
        bool updating = _updating, allowNull = _allowNull, allowDuplicates = _allowDuplicates;
        int thread = Environment.CurrentManagedThreadId;

        void RequireStorage(T expected)
        {
            if (Environment.CurrentManagedThreadId != thread || !identity.IsCurrent ||
                IsDestroyed || IsDestroyQueued || MutationIntentRevision != revision ||
                !ReferenceEquals(_lock, listLock) || _updating != updating ||
                _allowNull != allowNull || _allowDuplicates != allowDuplicates ||
                _list.Count != count || (uint)index >= (uint)count || !SameItem(_list[index], expected))
                throw new InvalidOperationException("The list slot owner changed during mutation callbacks.");
        }

        void Require(T expected)
        {
            requireCurrent(state);
            listLock?.EnterReadLock();
            try { RequireStorage(expected); }
            finally { listLock?.ExitReadLock(); }
        }

        Require(expectedValue);
        bool rejected = !allowNull && value is null || !allowDuplicates && Contains(value);
        Require(expectedValue);
        if (rejected)
            return false;
        if (!updating)
        {
            foreach (Func<bool> callback in Delegate.EnumerateInvocationList(PreModified))
            {
                bool accepted = callback();
                Require(expectedValue);
                if (!accepted) return false;
            }
            foreach (SingleCancelableHandler callback in Delegate.EnumerateInvocationList(PreAdded))
            {
                bool accepted = callback(value);
                Require(expectedValue);
                if (!accepted) return false;
            }
            foreach (SingleCancelableHandler callback in Delegate.EnumerateInvocationList(PreAnythingAdded))
            {
                bool accepted = callback(value);
                Require(expectedValue);
                if (!accepted) return false;
            }
            foreach (PreIndexSetHandler callback in Delegate.EnumerateInvocationList(PreIndexSet))
            {
                bool accepted = callback(index, value);
                Require(expectedValue);
                if (!accepted) return false;
            }
        }
        Require(expectedValue);
        listLock?.EnterWriteLock();
        try
        {
            RequireStorage(expectedValue);
            _list[index] = value;
        }
        finally { listLock?.ExitWriteLock(); }
        if (!updating)
        {
            foreach (SingleHandler callback in Delegate.EnumerateInvocationList(PostAdded))
            {
                callback(value);
                Require(value);
            }
            foreach (SingleHandler callback in Delegate.EnumerateInvocationList(PostAnythingAdded))
            {
                callback(value);
                Require(value);
            }
            foreach (PostIndexSetHandler callback in Delegate.EnumerateInvocationList(PostIndexSet))
            {
                callback(index, expectedValue);
                Require(value);
            }
            foreach (Action callback in Delegate.EnumerateInvocationList(PostModified))
            {
                callback();
                Require(value);
            }
            if (CollectionChanged is { } changed)
            {
                var args = new TCollectionChangedEventArgs<T>(ECollectionChangedAction.Replace, value, index);
                foreach (TCollectionChangedEventHandler<T> callback in Delegate.EnumerateInvocationList(changed))
                {
                    callback(this, args);
                    Require(value);
                }
            }
        }
        Require(value);
        return true;
    }
}
