using XREngine.Data.Core;
using XREngine.Scene.Transforms;

namespace XREngine.Scene.Prefabs;

/// <summary>Rejects foreign graph adoption while one owned hierarchy is constructed and published.</summary>
internal sealed class HierarchyConstructionScope : IDisposable
{
    private static readonly Lock ScopeLock = new();
    private static HierarchyConstructionScope[] _active = [];
    private readonly ObjectConstructionContext _context;
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private bool _disposed;

    internal HierarchyConstructionScope(ObjectConstructionContext context)
    {
        _context = context;
        // These snapshots allocate once per construction, never per frame or property write.
        lock (ScopeLock)
            Volatile.Write(ref _active, [.. _active, this]);
    }

    internal static void RequireLink(XRObjectBase owner, XRObjectBase? value)
    {
        foreach (HierarchyConstructionScope scope in Volatile.Read(ref _active))
        {
            bool ownsOwner = scope._context.OwnsAllocation(owner);
            bool ownsValue = value is not null && scope._context.OwnsAllocation(value);
            if (!ownsOwner && !ownsValue)
                continue;
            scope.RequireOwnerThread();
            if (!ownsOwner || (value is not null && !ownsValue))
                throw new InvalidOperationException("An owned hierarchy cannot adopt or enter another construction's graph.");
        }
    }

    internal static void RequireAssignment(XRObjectBase owner, XRObjectBase? original,
        XRObjectBase? current, XRObjectBase? value)
    {
        RequireLink(owner, current);
        RequireLink(owner, value);
        if (!ReferenceEquals(original, current) &&
            (Retains(owner) || (original is not null && Retains(original))))
            throw new InvalidOperationException("A hierarchy link changed during its assignment callbacks.");
    }

    internal static void RequireTransformAssignment(SceneNode owner, TransformBase? original,
        TransformBase? current, TransformBase value)
    {
        RequireAssignment(owner, original, current, value);
        if ((Retains(owner) || Retains(value)) &&
            value.SceneNode is { } previousOwner && !ReferenceEquals(previousOwner, owner))
            throw new InvalidOperationException("An owned transform already belongs to another node.");
    }

    internal static void RequireNodeAssignment(TransformBase owner, SceneNode? original,
        SceneNode? current, SceneNode? value)
    {
        RequireAssignment(owner, original, current, value);
        if (Retains(owner) && current is not null && value is not null && !ReferenceEquals(current, value))
            throw new InvalidOperationException("An owned transform must leave its current node before adoption.");
        if (Retains(owner) && value is not null &&
            (value.IsTransformNull || !ReferenceEquals(value.Transform, owner)))
            throw new InvalidOperationException("An owned transform must be the node's current transform before adoption.");
    }

    internal static void RequireDetachedWorld(XRObjectBase value, IRuntimeWorldContext? world)
    {
        foreach (HierarchyConstructionScope scope in Volatile.Read(ref _active))
        {
            if (!scope._context.OwnsAllocation(value))
                continue;
            scope.RequireOwnerThread();
            if (world is not null)
                throw new InvalidOperationException("An owned hierarchy must remain detached until construction completes.");
        }
    }

    internal static bool Retains(XRObjectBase value)
    {
        foreach (HierarchyConstructionScope scope in Volatile.Read(ref _active))
            if (scope._context.OwnsAllocation(value))
                return true;
        return false;
    }

    internal static void RequireSceneRootAdmission(SceneNode node)
    {
        foreach (HierarchyConstructionScope scope in Volatile.Read(ref _active))
        {
            if (!scope._context.OwnsAllocation(node))
                continue;
            scope.RequireOwnerThread();
            throw new InvalidOperationException("An owned hierarchy cannot enter any scene root collection before construction completes.");
        }
    }

    private void RequireOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("A hierarchy under construction belongs to its synchronous factory thread.");
    }

    public void Dispose()
    {
        RequireOwnerThread();
        lock (ScopeLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            Volatile.Write(ref _active, _active.Where(scope => !ReferenceEquals(scope, this)).ToArray());
        }
    }
}
