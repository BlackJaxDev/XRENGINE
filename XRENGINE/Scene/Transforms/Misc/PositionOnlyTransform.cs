using System.Numerics;

namespace XREngine.Scene.Transforms
{
    /// <summary>
    /// This transform will only inherit the world position of its parent.
    /// </summary>
    /// <param name="parent"></param>
    public class PositionOnlyTransform : TransformBase
    {
        public PositionOnlyTransform() { }
        public PositionOnlyTransform(TransformBase parent)
            : base(parent) { }

        protected override Matrix4x4 CreateWorldMatrix()
            => Parent is null
                ? Matrix4x4.Identity
                : Matrix4x4.CreateTranslation(Parent.WorldTranslation);

        protected override Matrix4x4 CreateLocalMatrix()
            => Matrix4x4.Identity;
    }
}
