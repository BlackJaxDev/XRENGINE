namespace XREngine.Cutscenes;

/// <summary>Describes how a cutscene binding finds its target object at run time.</summary>
public enum ECutsceneBindingKind
{
    /// <summary>The target is a scene object found by its identifier.</summary>
    SceneObject,
    /// <summary>The target is a node inside a prefab instance.</summary>
    PrefabNode,
    /// <summary>The target is an object that the cutscene creates when it starts.</summary>
    Spawned,
    /// <summary>The caller supplies the target at run time under a slot name.</summary>
    RuntimeSlot,
}
