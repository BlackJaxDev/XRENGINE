using System.Numerics;

namespace XREngine.Scene.Transforms
{
    /// <summary>
    /// Does not transform the node.
    /// Useful if you want to force the user to be unable to transform the node (categorizing components, etc).
    /// </summary>
    /// <param name="parent"></param>
    public class TransformNone : TransformBase
    {
        public TransformNone() { }
        public TransformNone(TransformBase parent)
            : base(parent) { }

        protected override Matrix4x4 CreateLocalMatrix()
            => Matrix4x4.Identity;
    }
}