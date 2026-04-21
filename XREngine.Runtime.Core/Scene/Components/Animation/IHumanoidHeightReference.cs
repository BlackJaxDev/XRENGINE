using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Animation
{
    /// <summary>
    /// Minimal humanoid surface needed by height-scaling systems that live outside
    /// the animation integration assembly.
    /// </summary>
    public interface IHumanoidHeightReference
    {
        SceneNode? HeadNode { get; }
        TransformBase RootTransform { get; }
    }
}