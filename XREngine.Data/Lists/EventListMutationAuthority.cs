namespace System.Collections.Generic;

/// <summary>
/// Owns structural links in one list. The list installs this authority during construction.
/// Storage hooks must use reference identity and must not invoke application callbacks.
/// </summary>
public abstract class EventListMutationAuthority<T>
{
    /// <summary>
    /// Removes one reserved occurrence for this exact authority. Validation and progress
    /// hooks run inside the storage gate and must not call application code.
    /// The authority must retain its private removal capability through notifications.
    /// </summary>
    protected void RemoveReservedOccurrence(EventList<T> list, int index, T item,
        Action validate, Action committed)
        => list.RemoveReservedOccurrence(this, index, item, validate, committed);

    protected internal abstract Lock MutationGate { get; }
    protected internal abstract void Initialize(EventList<T> list, IReadOnlyList<T> values);
    protected internal abstract void RequireCurrent(EventList<T> list);
    protected internal abstract void EnterMutation(EventList<T> list);
    protected internal virtual void RequireRemovalCurrent(EventList<T> list) => RequireCurrent(list);
    protected internal virtual void EnterRemoval(EventList<T> list) => EnterMutation(list);
    protected internal abstract void ExitMutation();
    protected internal abstract void ReserveItem(T item, bool adding);
    protected internal abstract void ReleaseItem(T item);
    protected internal abstract void PrepareAdd(T item);
    protected internal abstract void Added(T item);
    protected internal abstract void Removed(T item);
    protected internal virtual void EnterLifetimeMutation(EventList<T> list) { }
    protected internal virtual void ExitLifetimeMutation(EventList<T> list) { }
}
