using XREngine.Data.Core;

namespace XREngine.Editor;

/// <summary>Owns one open structural step without suppressing changes made by its callbacks.</summary>
internal sealed class StructuralChangeHandle(Undo.ChangeScope scope, Undo.StructuralUndoStep step) : IDisposable
{
    private int _disposed;

    /// <summary>Removes only this inverse after the caller verifies complete restoration.</summary>
    public bool RemoveRestoredStep()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return Undo.RemoveRestoredStructuralStep(scope, step);
    }

    /// <summary>Transfers one reserved resource to this scope and its eventual history action.</summary>
    public void RetainResource(UndoHistoryResource resource)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Undo.RetainHistoryResource(scope, step, resource);
    }

    /// <summary>Releases this resource after its inverse was removed following complete restoration.</summary>
    public void ReleaseRestoredResource(UndoHistoryResource resource)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Undo.ReleaseRestoredHistoryResource(scope, step, resource);
    }

    /// <summary>Records the exact values of one owned property even if its observer failed.</summary>
    public void ReconcileProperty(XRBase target, string propertyName, object? original, object? current)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Undo.ReconcileStructuralProperty(scope, step, target, propertyName, original, current);
    }

    /// <summary>Removes one automatic property step whose replay belongs to this structural inverse.</summary>
    public void RemoveOwnedProperty(XRBase target, string propertyName)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Undo.ReconcileStructuralProperty(scope, step, target, propertyName, null, null);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            Undo.ReleaseStructuralMutation();
    }
}
