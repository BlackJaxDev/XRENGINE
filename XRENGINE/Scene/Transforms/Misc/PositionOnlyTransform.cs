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

        public override Vector3 LocalTranslation
            => Vector3.Zero;

        public override Quaternion LocalRotation
            => Quaternion.Identity;

        public override Quaternion InverseLocalRotation
            => Quaternion.Identity;

        public override Vector3 WorldTranslation
            => Parent?.WorldTranslation ?? Vector3.Zero;

        public override Quaternion WorldRotation
            => Quaternion.Identity;

        public override Quaternion InverseWorldRotation
            => Quaternion.Identity;

        public override Quaternion RenderRotation
            => Quaternion.Identity;

        public override Quaternion InverseRenderRotation
            => Quaternion.Identity;

        protected override Matrix4x4 CreateWorldMatrix()
            => Parent is null
                ? Matrix4x4.Identity
                : Matrix4x4.CreateTranslation(Parent.WorldTranslation);

        protected override Matrix4x4 CreateLocalMatrix()
            => Matrix4x4.Identity;
    }
}
