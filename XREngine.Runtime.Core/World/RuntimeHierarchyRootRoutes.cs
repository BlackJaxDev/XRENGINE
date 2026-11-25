using System.Collections.ObjectModel;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine;

/// <summary>Immutable ownership receipt for one root's loaded-scene routing metadata.</summary>
public sealed class RuntimeHierarchyRootRoutes
{
    internal RuntimeHierarchyRootRoutes(RuntimeWorld owner, SceneNode root, XRScene[] scenes, bool externallyOwned)
    {
        Owner = owner;
        Root = root;
        Transform = root.Transform;
        TargetWorld = owner.TargetWorld;
        ScenePolicy = owner.ScenePolicy;
        Scenes = Array.AsReadOnly(scenes);
        ExternallyOwned = externallyOwned;
    }

    public RuntimeWorld Owner { get; }
    public SceneNode Root { get; }
    public ReadOnlyCollection<XRScene> Scenes { get; }
    public bool ExternallyOwned { get; }
    internal TransformBase Transform { get; }
    internal XRWorld? TargetWorld { get; }
    internal IRuntimeWorldScenePolicy? ScenePolicy { get; }
}
