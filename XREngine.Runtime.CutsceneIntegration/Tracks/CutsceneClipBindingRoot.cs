using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Cutscenes;

/// <summary>
/// The object that a clip copy binds its member tree to. Clip member paths start at a component property,
/// and this type gives those properties without a new component on the target.
/// </summary>
/// <param name="sceneNode">The target of the clip.</param>
public sealed class CutsceneClipBindingRoot(SceneNode sceneNode)
{
    /// <summary>The target of the clip.</summary>
    public SceneNode SceneNode { get; } = sceneNode;

    /// <summary>The transform of the target.</summary>
    public TransformBase Transform => SceneNode.Transform;
}
