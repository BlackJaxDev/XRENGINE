using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Scene;

namespace XREngine;

/// <summary>
/// Assigns owning <see cref="SceneNode"/> references to <see cref="XRComponent"/> instances created
/// directly inside <see cref="SceneNode.ComponentsSerialized"/> during cooked-binary deserialization,
/// while clearing the ambient owner during nested component member reads so cross-node references keep
/// their true owning nodes.
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
    {
        if (instance is SceneNode sceneNode && memberName == nameof(SceneNode.ComponentsSerialized))
            return new OwningSceneNodeScope(sceneNode);

        return CurrentOwningSceneNode.Value is not null
            ? new OwningSceneNodeScope(null)
            : null;
    }

    private sealed class OwningSceneNodeScope : IDisposable
    {
        private readonly SceneNode? _previous;
        private int _disposed;

        public OwningSceneNodeScope(SceneNode? node)
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
