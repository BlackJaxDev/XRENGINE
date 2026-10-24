using XREngine.Data.Core;

namespace XREngine.Scene.Prefabs;

/// <summary>Holds exact structural lifetimes stable while ordinary edit callbacks remain enabled.</summary>
internal sealed class HierarchyLifetimeAdmission : IDisposable
{
    private readonly XRWorldObjectBase[] _objects;
    private readonly ObjectConstructionOwnership _ownership;
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private bool _disposed;

    private HierarchyLifetimeAdmission(ObjectConstructionOwnership ownership, XRWorldObjectBase[] objects)
    {
        _ownership = ownership;
        _objects = objects;
    }

    internal static HierarchyLifetimeAdmission Acquire(ObjectConstructionOwnership ownership)
    {
        if (!ownership.HasSealedIdentities)
            throw new InvalidOperationException("The owned construction has no sealed lifetime identities.");
        List<XRWorldObjectBase> objects = [];
        foreach (ObjectConstructionIdentity identity in ownership.Identities)
            if (identity.Target is XRWorldObjectBase structural)
                objects.Add(structural);
        HierarchyLifetimeAdmission admission = new(ownership, [.. objects]);
        lock (SceneRootCollection.MutationGate)
        {
            foreach (XRWorldObjectBase value in admission._objects)
                if (value.HasStructuralMutation)
                    throw new InvalidOperationException("A clone endpoint already has an admitted mutation.");
            try
            {
                foreach (XRWorldObjectBase value in admission._objects)
                    if (!value.TrySetLifetimeAdmission(admission))
                        throw new InvalidOperationException("The clone endpoint has another lifetime admission owner.");
                // Lifetime writers increment before testing admission. CAS installation
                // and this second sweep close the writer/reader entry race.
                foreach (XRWorldObjectBase value in admission._objects)
                    if (value.HasActiveStructuralMutation)
                        throw new InvalidOperationException("A clone lifetime operation is still in progress.");
                admission.RequireCurrent();
                return admission;
            }
            catch
            {
                foreach (XRWorldObjectBase value in admission._objects)
                    value.ReleaseLifetimeAdmission(admission);
                throw;
            }
        }
    }

    internal void RequireCurrent()
    {
        if (_disposed || Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("The clone lifetime admission is no longer active on its owner thread.");
        foreach (ObjectConstructionIdentity identity in _ownership.Identities)
            if (!identity.IsCurrent)
                throw new InvalidOperationException("An owned allocation changed lifetime after construction.");
        foreach (XRWorldObjectBase value in _objects)
            if (value.IsDestroyed || value.IsDestroyQueued)
                throw new InvalidOperationException("An owned structural lifetime is no longer live.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("Clone lifetime admission must close on its owner thread.");
        lock (SceneRootCollection.MutationGate)
        {
            foreach (XRWorldObjectBase value in _objects)
                value.ReleaseLifetimeAdmission(this);
            _disposed = true;
        }
    }
}
