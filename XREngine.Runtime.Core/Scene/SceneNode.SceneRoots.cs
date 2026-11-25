using MemoryPack;
using System.ComponentModel;
using YamlDotNet.Serialization;

namespace XREngine.Scene;

public sealed partial class SceneNode
{
    private Dictionary<SceneRootCollection, int>? _sceneRootMemberships;
    private int _hasEnteredRootCollection;

    internal bool HasEnteredRootCollection => Volatile.Read(ref _hasEnteredRootCollection) != 0;
    private void MarkRootCollectionAdoption() => Volatile.Write(ref _hasEnteredRootCollection, 1);

    /// <summary>Copies this node's exact scene root owners at an authoring or release boundary.</summary>
    public SceneRootMembership[] CaptureSceneRootMemberships()
    {
        lock (SceneRootCollection.MutationGate)
        {
            if (_sceneRootMemberships is null || _sceneRootMemberships.Count == 0)
                return [];
            SceneRootMembership[] result = new SceneRootMembership[_sceneRootMemberships.Count];
            int index = 0;
            foreach (var pair in _sceneRootMemberships)
                result[index++] = new(pair.Key, pair.Value);
            return result;
        }
    }

    [YamlIgnore, MemoryPackIgnore, Browsable(false)]
    public bool HasSceneRootOwner
    {
        get
        {
            lock (SceneRootCollection.MutationGate)
                return _sceneRootMemberships is { Count: > 0 };
        }
    }

    internal void PrepareSceneRootOccurrence(SceneRootCollection roots)
    {
        _sceneRootMemberships ??= new(ReferenceEqualityComparer.Instance);
        if (!_sceneRootMemberships.ContainsKey(roots))
            _sceneRootMemberships.EnsureCapacity(checked(_sceneRootMemberships.Count + 1));
    }

    internal void AddSceneRootOccurrence(SceneRootCollection roots)
    {
        _sceneRootMemberships!.TryGetValue(roots, out int count);
        _sceneRootMemberships[roots] = checked(count + 1);
        MarkRootCollectionAdoption();
    }

    internal void RemoveSceneRootOccurrence(SceneRootCollection roots)
    {
        if (_sceneRootMemberships is null || !_sceneRootMemberships.TryGetValue(roots, out int count))
            throw new InvalidOperationException("The scene root occurrence is not registered with its node.");
        if (count == 1)
            _sceneRootMemberships.Remove(roots);
        else
            _sceneRootMemberships[roots] = count - 1;
    }
}
