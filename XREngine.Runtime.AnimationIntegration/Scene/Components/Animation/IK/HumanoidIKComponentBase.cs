using System.Numerics;
using XREngine.Scene.Transforms;
using Transform = XREngine.Scene.Transforms.Transform;

namespace XREngine.Components.Animation
{
    public abstract class HumanoidIKComponentBase : BaseIKSolverComponent
    {
        private HumanoidComponent? _assignedHumanoid;
        public HumanoidComponent? AssignedHumanoid
        {
            get => _assignedHumanoid;
            set => SetField(ref _assignedHumanoid, value);
        }

        protected HumanoidComponent? TryGetHumanoid()
            => AssignedHumanoid ?? GetSiblingComponent<HumanoidComponent>(false);

        protected override bool ShouldApplySolverPose()
            => TryGetHumanoid()?.IsAnimatedPosePreviewActive ?? true;

        public HumanoidComponent Humanoid => TryGetHumanoid() ?? GetSiblingComponent<HumanoidComponent>(true)!;
        public Transform? Root => TryGetHumanoid()?.SceneNode?.GetTransformAs<Transform>(true);

        protected (TransformBase? tfm, Matrix4x4 offset) GetHumanoidTarget(EHumanoidIKTarget target)
            => TryGetHumanoid()?.GetIKTarget(target) ?? HumanoidIKTargetDefaults.Empty;

        protected Transform? GetHumanoidTargetTransform(EHumanoidIKTarget target)
            => TryGetHumanoid()?.GetIKTargetTransform(target) as Transform;

        protected void SetHumanoidTarget(EHumanoidIKTarget target, TransformBase? tfm, Matrix4x4 offset)
            => TryGetHumanoid()?.SetIKTarget(target, tfm, offset);

        protected static Matrix4x4 GetMatrixForTarget((TransformBase? tfm, Matrix4x4 offset) target)
            => target.offset * (target.tfm?.RenderMatrix ?? Matrix4x4.Identity);

        protected static void SetTransform(TransformBase? tfm, Vector3 position, Quaternion? rotation = null)
        {
            if (tfm is null)
                return;

            if (tfm is Transform concrete)
            {
                concrete.TargetTranslation = position;
                if (rotation.HasValue)
                    concrete.TargetRotation = rotation.Value;
            }
            else
            {
                Matrix4x4 matrix = rotation.HasValue
                    ? Matrix4x4.CreateFromQuaternion(rotation.Value) * Matrix4x4.CreateTranslation(position)
                    : Matrix4x4.CreateTranslation(position);
                tfm.DeriveWorldMatrix(matrix);
            }
        }
    }
}