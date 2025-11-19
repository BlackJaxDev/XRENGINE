using MagicPhysX;

namespace XREngine.Rendering.Physics.Physx.Joints
{
    public unsafe class PhysxJoint_Revolute(PxRevoluteJoint* joint) : PhysxJoint, IHingeJoint
    {
        public PxRevoluteJoint* _joint = joint;

        public override unsafe PxJoint* JointBase => (PxJoint*)_joint;

        public float GetAngleRad() => _joint->GetAngle();
        public float GetAngleDeg() => float.RadiansToDegrees(GetAngleRad());
        public float GetVelocity() => _joint->GetVelocity();

        public (float lower, float upper, float restitution, float bounceThreshold, float stiffness, float damping) Limit
        {
            get
            {
                var pair = _joint->GetLimit();
                return (pair.lower, pair.upper, pair.restitution, pair.bounceThreshold, pair.stiffness, pair.damping);
            }
            set
            {
                var pair = new PxJointAngularLimitPair
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

        public void SetDriveVelocity(float velocity, bool wake)
            => _joint->SetDriveVelocityMut(velocity, wake);

        public float DriveVelocity
        {
            get => _joint->GetDriveVelocity();
            set => _joint->SetDriveVelocityMut(value, true);
        }

        public float DriveForceLimit
        {
            get => _joint->GetDriveForceLimit();
            set => _joint->SetDriveForceLimitMut(value);
        }

        public float DriveGearRatio
        {
            get => _joint->GetDriveGearRatio();
            set => _joint->SetDriveGearRatioMut(value);
        }

        public PxRevoluteJointFlags RevoluteFlags
        {
            get => _joint->GetRevoluteJointFlags();
            set => _joint->SetRevoluteJointFlagsMut(value);
        }

        public void SetRevoluteJointFlag(PxRevoluteJointFlag flag, bool value)
            => _joint->SetRevoluteJointFlagMut(flag, value);
    }
}