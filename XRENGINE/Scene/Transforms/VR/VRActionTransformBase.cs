using System.Numerics;
using XREngine.Input.Devices.Types.OpenVR;
using XREngine.Scene.Transforms;
using static XREngine.Input.Devices.InputInterface;

namespace XREngine.Data.Components.Scene
{
    public abstract class VRActionTransformBase<TCategory, TName> : TransformBase, IVRActionTransformBase<TCategory, TName>
        where TCategory : struct, Enum
        where TName : struct, Enum
    {
        private TCategory _actionCategory;
        public TCategory ActionCategory
        {
            get => _actionCategory;
            set => SetField(ref _actionCategory, value);
        }

        private TName _actionName;
        public TName ActionName
        {
            get => _actionName;
            set => SetField(ref _actionName, value);
        }

        private Vector3 _position = Vector3.Zero;
        public Vector3 Position
        {
            get => _position;
            internal set => SetField(ref _position, value);
        }

        private Quaternion _rotation = Quaternion.Identity;
        public Quaternion Rotation
        {
            get => _rotation;
            internal set => SetField(ref _rotation, value);
        }

        public string ActionPath => MakeVRActionPath(ActionCategory.ToString(), ActionName.ToString(), false);

        public override Vector3 LocalTranslation
            => Position;

        public override Quaternion LocalRotation
            => Quaternion.Normalize(Rotation);

        public override Quaternion InverseLocalRotation
            => Quaternion.Normalize(Quaternion.Inverse(Rotation));

        public override Vector3 WorldTranslation
            => Parent is null
                ? Position
                : Vector3.Transform(Position, ParentWorldMatrix);

        public override Quaternion WorldRotation
            => Parent is null
                ? Quaternion.Normalize(Rotation)
                : Quaternion.Normalize(ParentWorldRotation * Rotation);

        public override Quaternion InverseWorldRotation
            => Parent is null
                ? Quaternion.Normalize(Quaternion.Inverse(Rotation))
                : Quaternion.Normalize(Quaternion.Inverse(Rotation) * ParentInverseWorldRotation);

        public override Quaternion RenderRotation
            => Parent is null
                ? Quaternion.Normalize(Rotation)
                : Quaternion.Normalize(Parent.RenderRotation * Rotation);

        public override Quaternion InverseRenderRotation
            => Parent is null
                ? Quaternion.Normalize(Quaternion.Inverse(Rotation))
                : Quaternion.Normalize(Quaternion.Inverse(Rotation) * Parent.InverseRenderRotation);

        protected override Matrix4x4 CreateLocalMatrix()
            => Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position);
    }
}
