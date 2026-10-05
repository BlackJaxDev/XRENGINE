using System;
using System.ComponentModel;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Scene.Mesh;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Capture;

// Serialized identities of the objects the spawner refers to. Scene serializers
// write a referenced object as a full copy, so the spawner stores the identities of
// its placement models and generated probe nodes instead and rebinds them to the
// live objects after deserialization.
public partial class LightProbeGridSpawnerComponent
{
    private Guid[] _placementBoundsModelIds = [];
    private Guid[] _spawnedProbeNodeIds = [];

    /// <summary>
    /// Identities of <see cref="PlacementBoundsModels"/>, which is what scene
    /// serialization stores. After deserialization the spawner binds them to the
    /// live model components of its world when it begins play, activates or
    /// builds its grid; a serialized component reference would come back as a
    /// detached copy that has no renderable meshes and no world bounds.
    /// </summary>
    [Browsable(false)]
    public Guid[] PlacementBoundsModelIds
    {
        get => _placementBoundsModelIds;
        set => SetField(ref _placementBoundsModelIds, value ?? []);
    }

    /// <summary>
    /// Identities of the probe nodes this spawner generated. Generated probes are
    /// ordinary child nodes and are serialized with the scene; after
    /// deserialization the spawner re-adopts the children with these identities,
    /// so it neither loses track of its grid nor spawns a second one beside it.
    /// </summary>
    [Browsable(false)]
    public Guid[] SpawnedProbeNodeIds
    {
        get => _spawnedProbeNodeIds;
        set => SetField(ref _spawnedProbeNodeIds, value ?? []);
    }

    private static Guid[] CollectIds(ModelComponent[] models)
    {
        Guid[] ids = new Guid[models.Length];
        for (int index = 0; index < models.Length; ++index)
            ids[index] = models[index]?.ID ?? Guid.Empty;
        return ids;
    }

    private Guid[] CollectSpawnedNodeIds()
    {
        Guid[] ids = new Guid[_spawnedNodes.Count];
        for (int index = 0; index < ids.Length; ++index)
            ids[index] = _spawnedNodes[index].ID;
        return ids;
    }

    /// <summary>
    /// Binds <see cref="PlacementBoundsModelIds"/> to the live model components of
    /// the spawner's world. Models not found yet, for example in a scene that is
    /// still loading, are bound on a later call.
    /// </summary>
    private void BindPlacementBoundsModels()
    {
        Guid[] ids = _placementBoundsModelIds;
        if (ids.Length == 0 || PlacementBoundsModelsMatchIds(ids))
            return;

        IRuntimeRenderWorld? world = WorldAs<IRuntimeRenderWorld>();
        if (world is null)
            return;

        ModelComponent?[] bound = new ModelComponent?[ids.Length];
        int boundCount = 0;
        foreach (SceneNode root in world.RootNodes)
        {
            root.IterateComponents<ModelComponent>(model =>
            {
                int index = Array.IndexOf(ids, model.ID);
                if (index < 0 || bound[index] is not null)
                    return;

                bound[index] = model;
                ++boundCount;
            }, iterateChildHierarchy: true);
        }

        if (boundCount == 0)
            return;

        ModelComponent[] models = new ModelComponent[boundCount];
        int next = 0;
        foreach (ModelComponent? model in bound)
            if (model is not null)
                models[next++] = model;

        // The identities stay as serialized, so models not found yet bind later.
        SetField(ref _placementBoundsModels, models, nameof(PlacementBoundsModels));
    }

    private bool PlacementBoundsModelsMatchIds(Guid[] ids)
    {
        ModelComponent[] models = _placementBoundsModels;
        if (models.Length != ids.Length)
            return false;

        for (int index = 0; index < models.Length; ++index)
        {
            ModelComponent? model = models[index];
            if (model is null || model.IsDestroyed || model.ID != ids[index])
                return false;
        }

        return true;
    }

    /// <summary>
    /// Re-adopts the generated probes that deserialization restored as children,
    /// using <see cref="SpawnedProbeNodeIds"/>. Does nothing while the spawner
    /// already tracks a grid.
    /// </summary>
    private void AdoptSerializedGrid()
    {
        Guid[] ids = _spawnedProbeNodeIds;
        if (_spawnedNodes.Count > 0 || ids.Length == 0 || SceneNode is null)
            return;

        foreach (TransformBase? child in SceneNode.Transform.Children)
        {
            SceneNode? node = child?.SceneNode;
            if (node is null || node.IsDestroyed || Array.IndexOf(ids, node.ID) < 0)
                continue;

            _spawnedNodes.Add(node);
            if (node.GetComponent<LightProbeComponent>() is { } probe)
                _spawnedProbes.Add(probe);
        }

        if (_spawnedNodes.Count > 0)
            _captureStatus = $"Ready: {_spawnedProbes.Count} probes.";
    }
}
