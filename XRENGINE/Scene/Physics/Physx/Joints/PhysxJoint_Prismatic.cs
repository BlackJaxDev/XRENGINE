using MagicPhysX;
using XREngine.Scene.Physics.Joints;

namespace XREngine.Rendering.Physics.Physx.Joints
{
    public unsafe class PhysxJoint_Prismatic(PxPrismaticJoint* joint) : PhysxJoint, IPrismaticJoint, IAbstractPrismaticJoint
    {
        public PxPrismaticJoint* _joint = joint;

        public override unsafe PxJoint* JointBase => (PxJoint*)_joint;

        public float Velocity => _joint->GetVelocity();
        public float Position => _joint->GetPosition();
        public (float lower, float upper, float restitution, float bounceThreshold, float stiffness, float damping) Limit
        {
            get 
            {
                PxJointLinearLimitPair pair = _joint->GetLimit();
                return (pair.lower, pair.upper, pair.restitution, pair.bounceThreshold, pair.stiffness, pair.damping);
            }
            set
            {
                PxJointLinearLimitPair pair = new()
                {
                    lower = value.lower,
                    upper = value.upper,
                    restitution = value.restitution,
                    bounceThreshold = value.bounceThreshold,
                    stiffness = value.stiffness,
                    damping = value.damping
                };
                _joint->SetLimitMut(&pair);
            }
        }
        public PxPrismaticJointFlags PrismaticFlags
        {
            get => _joint->GetPrismaticJointFlags();
            set => _joint->SetPrismaticJointFlagsMut(value);
        }

        public void SetPrismaticJointFlag(PxPrismaticJointFlag flag, bool value)
            => _joint->SetPrismaticJointFlagMut(flag, value);

        #region IAbstractPrismaticJoint

        float IAbstractPrismaticJoint.Position => Position;
        float IAbstractPrismaticJoint.Velocity => Velocity;

        bool IAbstractPrismaticJoint.EnableLimit
        {
            get => PrismaticFlags.HasFlag(PxPrismaticJointFlags.LimitEnabled);
            set => SetPrismaticJointFlag(PxPrismaticJointFlag.LimitEnabled, value);
        }

        JointLinearLimitPair IAbstractPrismaticJoint.Limit
        {
            get
            {
                var (lower, upper, restitution, bounceThreshold, stiffness, damping) = Limit;
                return new JointLinearLimitPair(lower, upper, stiffness, damping, restitution, bounceThreshold);
            }
            set => Limit = (value.Lower, value.Upper, value.Restitution, value.BounceThreshold, value.Stiffness, value.Damping);
        }

        #endregion
    }
}