using MagicPhysX;
using XREngine.Scene.Physics.Joints;

namespace XREngine.Rendering.Physics.Physx.Joints
{
    public unsafe class PhysxJoint_Spherical(PxSphericalJoint* joint) : PhysxJoint, IAbstractSphericalJoint
    {
        public PxSphericalJoint* _joint = joint;

        public override unsafe PxJoint* JointBase => (PxJoint*)_joint;

        public (float zAngle, float yAngle, float restitution, float bounceThreshold, float stiffness, float damping) LimitCone
        {
            get
            {
                var cone = _joint->GetLimitCone();
                return (cone.zAngle, cone.yAngle, cone.restitution, cone.bounceThreshold, cone.stiffness, cone.damping);
            }
            set
            {
                var cone = new PxJointLimitCone() { zAngle = value.zAngle, yAngle = value.yAngle, restitution = value.restitution, bounceThreshold = value.bounceThreshold, stiffness = value.stiffness, damping = value.damping };
                _joint->SetLimitConeMut(&cone);
            }
        }

        public float SwingZAngle => _joint->GetSwingZAngle();
        public float SwingYAngle => _joint->GetSwingYAngle();

        public PxSphericalJointFlags SphericalFlags
        {
            get => _joint->GetSphericalJointFlags();
            set => _joint->SetSphericalJointFlagsMut(value);
        }

        public void SetSphericalJointFlag(PxSphericalJointFlag flag, bool value)
            => _joint->SetSphericalJointFlagMut(flag, value);

        #region IAbstractSphericalJoint

        float IAbstractSphericalJoint.SwingYAngle => SwingYAngle;
        float IAbstractSphericalJoint.SwingZAngle => SwingZAngle;

        bool IAbstractSphericalJoint.EnableLimitCone
        {
            get => SphericalFlags.HasFlag(PxSphericalJointFlags.LimitEnabled);
            set => SetSphericalJointFlag(PxSphericalJointFlag.LimitEnabled, value);
        }

        JointLimitCone IAbstractSphericalJoint.LimitCone
        {
            get
            {
                var (zAngle, yAngle, restitution, bounceThreshold, stiffness, damping) = LimitCone;
                return new JointLimitCone(yAngle, zAngle, stiffness, damping, restitution, bounceThreshold);
            }
            set => LimitCone = (value.ZAngleRadians, value.YAngleRadians, value.Restitution, value.BounceThreshold, value.Stiffness, value.Damping);
        }

        #endregion
    }
}