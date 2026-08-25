using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Scene;

namespace XREngine;

/// <summary>
/// Restores scene/component ownership while the lower cooked-binary serializer hydrates an
/// object graph. This keeps scene semantics in Runtime.Core rather than in Data.
/// </summary>
public sealed class RuntimeCookedBinaryObjectLifecycleServices : ICookedBinaryObjectLifecycleServices
{
    private static readonly AsyncLocal<SceneNode?> CurrentOwningSceneNode = new();

    public static RuntimeCookedBinaryObjectLifecycleServices Instance { get; } = new();

    private RuntimeCookedBinaryObjectLifecycleServices()
    {
    }

    public void PrepareInstance(object instance)
    {
        if (instance is not XRComponent component)
            return;

        SceneNode? owner = CurrentOwningSceneNode.Value;
        if (owner is null)
            return;

        try
        {
            component.ConstructionSetSceneNode(owner);
            if (owner.World is not null)
                component.World = owner.World;
        }
        catch
        {
            // Snapshot restore remains best effort when an individual component rejects ownership.
        }
    }

    public IDisposable? EnterMemberScope(object instance, string memberName)
        => instance is SceneNode sceneNode && memberName == nameof(SceneNode.ComponentsSerialized)
            ? new OwningSceneNodeScope(sceneNode)
            : null;

    private sealed class OwningSceneNodeScope : IDisposable
    {
        private readonly SceneNode? _previous;
        private int _disposed;

        public OwningSceneNodeScope(SceneNode node)
        {
            _previous = CurrentOwningSceneNode.Value;
            CurrentOwningSceneNode.Value = node;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                CurrentOwningSceneNode.Value = _previous;
        }
    }
}
