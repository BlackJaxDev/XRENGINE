using System;
using System.Threading;

namespace XREngine.Rendering.UI.State;

/// <summary>Ends one journal transaction on the thread that began it.</summary>
internal sealed class UIChangeTransactionScope(UIChangeJournal journal, int ownerThreadId) : IDisposable
{
    internal int OwnerThreadId { get; } = ownerThreadId;

    internal UIChangeTransactionScope? Previous { get; set; }

    internal bool IsDisposed { get; set; }

    /// <summary>Ends this scope once. A call from another thread throws.</summary>
    public void Dispose()
    {
        if (Thread.CurrentThread.ManagedThreadId != OwnerThreadId)
            throw new InvalidOperationException("A UI change transaction must end on its owner thread.");

        journal.EndTransaction(this);
    }
}
